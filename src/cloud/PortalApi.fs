module PulseBoard.PortalApi

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.CustomerAuth
open PulseBoard.CustomerAuthApi
open PulseBoard.PortalStore
open PulseBoard.StripeClient
open PulseBoard.StripeStore

// Phase 10 step 4 — customer-facing portal API.
//
// Mounts under `/api/portal/*`. Every endpoint is gated by
// `CustomerAuthApi.tryAuthenticate` (cookie or 401). Workspace
// creation is delegated to the provisioner via HTTP — there are
// deliberately TWO services here:
//
//   * The provisioner (`src/cloud/Provisioner.fs`) runs on the admin
//     host, talks to Fly, and owns `pb_workspaces`. Its `/api/provision`
//     accepts an admin bearer token (so this portal can call it without
//     being a logged-in operator).
//   * The portal API (this file) runs on the apex (`pulseboard.cloud`
//     in site-only mode), owns `pb_customer_workspaces`, and acts as a
//     client of the provisioner.
//
// Splitting them this way means the apex can be a tiny single-binary
// process that only knows about customers + Stripe, and the
// provisioner stays focused on infra. They share the Postgres
// database but not their schemas.

// -- config -----------------------------------------------------------------

[<NoComparison; NoEquality>]
type ProvisionerClient =
  { /// e.g. "https://provisioner.internal.pulseboard.cloud" or
    /// "http://127.0.0.1:9090" in single-host dev. No trailing slash.
    baseUrl  : string
    /// Admin bearer token registered with the provisioner via
    /// `--admin-token=`. May be `None` for offline dev — in that case
    /// `/api/portal/workspaces` POST will return 503.
    token    : string option
    /// Public root (e.g. "pulseboard.cloud") used to assemble the
    /// final `https://<slug>.<root>` URL we surface to the portal
    /// when the provisioner hands back its short response.
    rootDomain : string }

[<NoComparison; NoEquality>]
type BillingDeps =
  { stripe      : StripeConfig
    stripeStore : IStripeStore }

[<NoComparison; NoEquality>]
type PortalApiConfig =
  { auth       : CustomerAuthConfig
    store      : ICustomerWorkspaceStore
    provisioner: ProvisionerClient
    /// Step 5 — when `Some`, plan switches and `/checkout` routes
    /// hit real Stripe and the `/api/stripe/webhook` route is
    /// mounted. When `None`, paid plans are not selectable in the
    /// portal (the API returns 503).
    billing    : BillingDeps option }

// -- helpers ----------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  if status = 204 then Suave.Successful.NO_CONTENT
  else
    let writer =
      match status with
      | 200 -> OK
      | 201 -> Suave.Successful.CREATED
      | 202 -> Suave.Successful.ACCEPTED
      | 400 -> BAD_REQUEST
      // Don't use Suave's UNAUTHORIZED here — it attaches a
      // `WWW-Authenticate: Basic realm="protected"` header which
      // makes browsers pop a native basic-auth dialog when the SPA
      // probes /api/portal/me without a cookie. We're a cookie-based
      // API, so emit a bare 401 instead.
      | 401 -> fun b -> OK b >=> Writers.setStatus HTTP_401
      | 403 -> FORBIDDEN
      | 404 -> NOT_FOUND
      | 409 -> Suave.RequestErrors.CONFLICT
      | 503 -> Suave.ServerErrors.SERVICE_UNAVAILABLE
      | _   -> INTERNAL_ERROR
    writer body >=> Writers.setMimeType "application/json"

let private errJson status msg =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize (msg : string)))

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private tryParseJson (body : string) : JsonDocument option =
  if String.IsNullOrWhiteSpace body then None
  else try Some (JsonDocument.Parse body) with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString() in if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

let private slugRegex = Regex(@"^[a-z][a-z0-9-]{2,31}$", RegexOptions.Compiled)
let private slugOk (s : string) =
  slugRegex.IsMatch s && not (s.StartsWith "-") && not (s.EndsWith "-")

let private requireAuth (cfg : PortalApiConfig) (inner : Customer -> WebPart) : WebPart =
  fun ctx -> async {
    match tryAuthenticate cfg.auth ctx.request with
    | None -> return! errJson 401 "not signed in" ctx
    | Some c ->
      if c.emailVerifiedAt.IsNone then
        return! errJson 403 "verify your email before using the portal" ctx
      else
        return! inner c ctx
  }

// -- JSON shaping -----------------------------------------------------------

let private workspaceJson (w : PortalWorkspace) : string =
  let (CustomerId cid) = w.customerId
  let s (v : string option) =
    match v with Some x -> JsonSerializer.Serialize x | None -> "null"
  let d (v : DateTimeOffset option) =
    match v with Some x -> JsonSerializer.Serialize (x.ToString("o")) | None -> "null"
  // NB: keep this as ONE logical sprintf line. Triple-quoted strings
  // in F# do NOT treat `\<newline>` as a line continuation — the
  // backslash + newline end up literal in the output, producing
  // invalid JSON that fails JSON.parse on the client.
  sprintf
    """{"slug":%s,"customerId":%s,"plan":%s,"status":%s,"publicUrl":%s,"upstreamUrl":%s,"createdAt":%s,"updatedAt":%s,"archivedAt":%s,"lastActiveAt":%s,"overdueSince":%s,"error":%s}"""
    (JsonSerializer.Serialize w.slug)
    (JsonSerializer.Serialize cid)
    (JsonSerializer.Serialize (PortalPlan.toString w.plan))
    (JsonSerializer.Serialize (WorkspaceStatus.toString w.status))
    (s w.publicUrl)
    (s w.upstreamUrl)
    (JsonSerializer.Serialize (w.createdAt.ToString("o")))
    (JsonSerializer.Serialize (w.updatedAt.ToString("o")))
    (d w.archivedAt)
    (JsonSerializer.Serialize (w.lastActiveAt.ToString("o")))
    (d w.overdueSince)
    (s w.error)

// -- plan catalog (Step 5 will turn the price ids into real Stripe ids) -----

let private planCatalogJson =
  """{"plans":[
       {"id":"free","name":"Free","priceMonthlyUsd":0,
        "limits":{"workspaces":1,"ingestGiBPerMonth":1,"retentionDays":7,
                  "seats":1,"series":2500,"spansPerMonth":100000},
        "description":"One free workspace, capped usage, 7-day retention."},
       {"id":"starter","name":"Starter","priceMonthlyUsd":19,
        "limits":{"workspaces":3,"ingestGiBPerMonth":25,"retentionDays":30,
                  "seats":3,"series":50000,"spansPerMonth":5000000},
        "description":"Side projects with metrics + logs + traces."},
       {"id":"pro","name":"Pro","priceMonthlyUsd":99,
        "limits":{"workspaces":10,"ingestGiBPerMonth":250,"retentionDays":90,
                  "seats":10,"series":500000,"spansPerMonth":50000000},
        "description":"Production teams with multi-region needs."}
     ]}"""

// -- provisioner client -----------------------------------------------------

let private http = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)

[<NoComparison>]
type private ProvisionResult =
  | Ok of slug:string * publicUrl:string * upstreamUrl:string * tenantId:string * apiKeyId:string * apiKey:string
  | Error of status:int * msg:string

let private workspaceCreateJson (w : PortalWorkspace)
                                (tenantId : string) (apiKeyId : string) (apiKey : string) : string =
  sprintf
    """{"workspace":%s,"bootstrap":{"tenantId":%s,"apiKeyId":%s,"apiKey":%s,"warning":"plaintext apiKey is shown once and cannot be recovered"}}"""
    (workspaceJson w)
    (JsonSerializer.Serialize tenantId)
    (JsonSerializer.Serialize apiKeyId)
    (JsonSerializer.Serialize apiKey)

let private issuedKeyJson (tenantId : string) (apiKeyId : string) (apiKey : string) : string =
  sprintf
    """{"tenantId":%s,"apiKeyId":%s,"apiKey":%s,"warning":"plaintext apiKey is shown once and cannot be recovered"}"""
    (JsonSerializer.Serialize tenantId)
    (JsonSerializer.Serialize apiKeyId)
    (JsonSerializer.Serialize apiKey)

let private callProvisionerCreate (cfg : ProvisionerClient)
                                  (slug : string) (email : string)
                                  (plan : PortalPlan) : Async<ProvisionResult> =
  async {
    match cfg.token with
    | None ->
      return ProvisionResult.Error (503, "provisioner service token not configured")
    | Some tok ->
      try
        let body =
          sprintf """{"slug":%s,"email":%s,"plan":%s}"""
            (JsonSerializer.Serialize slug)
            (JsonSerializer.Serialize email)
            (JsonSerializer.Serialize (PortalPlan.toInternal plan))
        use req =
          new HttpRequestMessage(
            HttpMethod.Post, cfg.baseUrl.TrimEnd '/' + "/api/provision")
        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", tok)
        req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use! resp = http.SendAsync req |> Async.AwaitTask
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        if not resp.IsSuccessStatusCode then
          return ProvisionResult.Error (int resp.StatusCode, text)
        else
          use doc = JsonDocument.Parse text
          let root = doc.RootElement
          let str (n : string) =
            match root.TryGetProperty n with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
          // The provisioner may suffix the slug on collision; trust
          // what it returns, not what we asked for.
          let finalSlug =
            match str "slug" with "" -> slug | s -> s
          let publicUrl = str "url"
          let tenantId = str "tenantId"
          let apiKeyId = str "apiKeyId"
          let apiKey = str "apiKey"
          // The provisioner doesn't surface upstream_url in its 201
          // response (it's an internal detail), so we leave it None
          // until the heartbeat sweeper fills it in.
          return ProvisionResult.Ok (finalSlug, publicUrl, "", tenantId, apiKeyId, apiKey)
      with ex ->
        return ProvisionResult.Error (502, ex.Message)
  }

let private callProvisionerAdmin (cfg : ProvisionerClient)
                                 (slug : string) (action : string)
                                 : Async<int * string> =
  async {
    match cfg.token with
    | None -> return (503, "provisioner service token not configured")
    | Some tok ->
      try
        let url =
          sprintf "%s/admin/workspaces/%s/%s"
            (cfg.baseUrl.TrimEnd '/')
            (Uri.EscapeDataString slug) action
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", tok)
        use! resp = http.SendAsync req |> Async.AwaitTask
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        return (int resp.StatusCode, text)
      with ex ->
        return (502, ex.Message)
  }

let private callProvisionerIssueKey (cfg : ProvisionerClient)
                                    (slug : string) (label : string) : Async<int * string> =
  async {
    match cfg.token with
    | None -> return (503, "provisioner service token not configured")
    | Some tok ->
      try
        let url =
          sprintf "%s/api/provision/workspaces/%s/keys"
            (cfg.baseUrl.TrimEnd '/')
            (Uri.EscapeDataString slug)
        let body = sprintf """{"label":%s}""" (JsonSerializer.Serialize label)
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", tok)
        req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use! resp = http.SendAsync req |> Async.AwaitTask
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        return (int resp.StatusCode, text)
      with ex ->
        return (502, ex.Message)
  }

// -- handlers ---------------------------------------------------------------

let private listWorkspaces (cfg : PortalApiConfig) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    let rows = cfg.store.ListForCustomer c.id
    let arr = rows |> List.map workspaceJson |> String.concat ","
    return! jsonResp 200 (sprintf """{"workspaces":[%s]}""" arr) ctx
  })

let private createWorkspace (cfg : PortalApiConfig) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match tryParseJson (readBody ctx.request) with
    | None -> return! errJson 400 "invalid JSON body" ctx
    | Some doc ->
      use _ = doc
      let root = doc.RootElement
      let slugRaw = tryGetString root "slug"
      let planStr =
        tryGetString root "plan" |> Option.defaultValue "free"
      match slugRaw, PortalPlan.tryParse planStr with
      | None, _ -> return! errJson 400 "field 'slug' is required" ctx
      | _, None -> return! errJson 400 "field 'plan' must be one of free|starter|pro" ctx
      | Some raw, Some plan ->
        let slug = raw.ToLowerInvariant()
        if not (slugOk slug) then
          return!
            errJson 400 "slug must match ^[a-z][a-z0-9-]{2,31}$ (lowercase, no leading/trailing '-')" ctx
        else
          // Free-tier rule: at most one *active* free workspace per
          // customer. Paid plans are uncapped on count here; the
          // Stripe step will gate that.
          if plan = Free && cfg.store.CountActiveOnPlan c.id Free >= 1 then
            return!
              errJson 409
                "you already have a free workspace; upgrade an existing one or pick a paid plan for the new one"
                ctx
          else
            // Pre-insert the row in `creating` state so a parallel
            // request for the same slug 409s instead of spawning two
            // Fly apps.
            let now = DateTimeOffset.UtcNow
            let pending : PortalWorkspace =
              { slug        = slug
                customerId  = c.id
                plan        = plan
                status      = Creating
                publicUrl   = None
                upstreamUrl = None
                createdAt   = now
                updatedAt   = now
                archivedAt  = None
                lastActiveAt = now
                overdueSince = None
                error       = None }
            let mutable inserted = false
            try cfg.store.Insert pending; inserted <- true
            with ex ->
              eprintfn "  [portal] pre-insert failed for %s: %s" slug ex.Message
            if not inserted then
              return! errJson 409 "slug already in use" ctx
            else
              // Call the provisioner. On failure, stamp the row as
              // `failed` so the customer sees the error in /portal
              // and can retry with a different slug.
              let! r = callProvisionerCreate cfg.provisioner slug c.email plan
              match r with
              | ProvisionResult.Ok (finalSlug, publicUrl, upstreamUrl, tenantId, apiKeyId, apiKey) ->
                // If the provisioner picked a different slug, we
                // re-key the row. Simplest impl: insert the new
                // row, mark the old one failed. (The legitimate
                // case here is a slug collision against an existing
                // tenant; rare with the regex constraints.)
                if finalSlug <> slug then
                  cfg.store.Update slug (fun w ->
                    { w with
                        status = Failed
                        error = Some "renamed by provisioner"
                        updatedAt = DateTimeOffset.UtcNow })
                  |> ignore
                  let real : PortalWorkspace =
                    { pending with
                        slug      = finalSlug
                        status    = Live
                        publicUrl = (if publicUrl = "" then None else Some publicUrl)
                        upstreamUrl =
                          (if upstreamUrl = "" then None else Some upstreamUrl)
                        updatedAt = DateTimeOffset.UtcNow }
                  try cfg.store.Insert real with _ -> ()
                  return! jsonResp 201 (workspaceCreateJson real tenantId apiKeyId apiKey) ctx
                else
                  let updated =
                    cfg.store.Update slug (fun w ->
                      { w with
                          status = Live
                          publicUrl =
                            (if publicUrl = "" then None else Some publicUrl)
                          upstreamUrl =
                            (if upstreamUrl = "" then None else Some upstreamUrl)
                          updatedAt = DateTimeOffset.UtcNow })
                  match updated with
                  | Some w -> return! jsonResp 201 (workspaceCreateJson w tenantId apiKeyId apiKey) ctx
                  | None   -> return! errJson 500 "row vanished" ctx
              | ProvisionResult.Error (status, msg) ->
                cfg.store.Update slug (fun w ->
                  { w with
                      status = Failed
                      error = Some (sprintf "HTTP %d: %s" status msg)
                      updatedAt = DateTimeOffset.UtcNow })
                |> ignore
                eprintfn "  [portal] provisioner failed for %s: HTTP %d %s" slug status msg
                return! errJson 502 (sprintf "provisioner: %s" msg) ctx
  })

let private issueWorkspaceKeyPortal (cfg : PortalApiConfig) (slug : string) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match cfg.store.TryGet slug with
    | None -> return! errJson 404 "workspace not found" ctx
    | Some w when w.customerId <> c.id -> return! errJson 404 "workspace not found" ctx
    | Some w ->
      let label =
        match tryParseJson (readBody ctx.request) with
        | Some doc ->
          use _ = doc
          tryGetString doc.RootElement "label"
          |> Option.defaultValue (sprintf "customer portal (%s)" c.email)
        | None -> sprintf "customer portal (%s)" c.email
      let! status, body = callProvisionerIssueKey cfg.provisioner w.slug label
      if status <> 201 && status <> 200 then
        return! errJson 502 (sprintf "provisioner: %s" body) ctx
      else
        try
          use doc = JsonDocument.Parse body
          let root = doc.RootElement
          let str (name : string) =
            match root.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
          return! jsonResp 201 (issuedKeyJson (str "tenantId") (str "apiKeyId") (str "apiKey")) ctx
        with ex ->
          return! errJson 502 (sprintf "bad provisioner response: %s" ex.Message) ctx
  })

let private archive (cfg : PortalApiConfig) (slug : string) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match cfg.store.TryGet slug with
    | None -> return! errJson 404 "no such workspace" ctx
    | Some w when w.customerId <> c.id ->
      // Don't leak the existence of someone else's slug.
      return! errJson 404 "no such workspace" ctx
    | Some w when w.status = Archived ->
      return! jsonResp 200 (workspaceJson w) ctx
    | Some _ ->
      let! (status, body) = callProvisionerAdmin cfg.provisioner slug "archive"
      if status >= 200 && status < 300 then
        let now = DateTimeOffset.UtcNow
        let updated =
          cfg.store.Update slug (fun w ->
            { w with
                status = Archived
                archivedAt = Some now
                updatedAt = now })
        match updated with
        | Some w -> return! jsonResp 200 (workspaceJson w) ctx
        | None   -> return! errJson 500 "row vanished" ctx
      else
        return! errJson 502 (sprintf "provisioner archive failed (HTTP %d): %s" status body) ctx
  })

let private unarchive (cfg : PortalApiConfig) (slug : string) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match cfg.store.TryGet slug with
    | None -> return! errJson 404 "no such workspace" ctx
    | Some w when w.customerId <> c.id ->
      return! errJson 404 "no such workspace" ctx
    | Some _ ->
      let! (status, body) = callProvisionerAdmin cfg.provisioner slug "unarchive"
      if status >= 200 && status < 300 then
        let now = DateTimeOffset.UtcNow
        let updated =
          cfg.store.Update slug (fun w ->
            { w with status = Live; archivedAt = None; updatedAt = now })
        match updated with
        | Some w -> return! jsonResp 200 (workspaceJson w) ctx
        | None   -> return! errJson 500 "row vanished" ctx
      else
        return! errJson 502 (sprintf "provisioner unarchive failed (HTTP %d): %s" status body) ctx
  })

// -- billing helpers (step 5) ----------------------------------------------

/// Ensure this PulseBoard customer has a Stripe customer object,
/// creating one lazily on first paid action. Returns the cus_xxx
/// id on success.
let private ensureStripeCustomer (b : BillingDeps) (c : Customer)
                                 : Async<Result<string, int * string>> =
  async {
    match b.stripeStore.TryGetStripeCustomerId c.id with
    | Some sc -> return Result.Ok sc
    | None ->
      let (CustomerId cid) = c.id
      let! r = StripeClient.createCustomer b.stripe c.email cid
      match r with
      | Result.Error e -> return Result.Error e
      | Result.Ok cust ->
        b.stripeStore.SetStripeCustomerId c.id cust.id
        return Result.Ok cust.id
  }

/// Take a fresh `SubscriptionView` from Stripe and project it into our
/// `StripeSubscription` row. Used both at checkout completion and on
/// webhook updates.
let private subFromView (b : BillingDeps) (customerId : CustomerId)
                        (v : SubscriptionView) : StripeSubscription =
  let now = DateTimeOffset.UtcNow
  { id                = v.id
    itemId            = v.itemId
    stripeCustomerId  = v.customerId
    customerId        = customerId
    workspaceSlug     = v.workspaceSlug
    priceId           = v.priceId
    plan              = StripeConfig.planForPrice b.stripe v.priceId
    status            = v.status
    currentPeriodEnd  = v.currentPeriodEnd
    cancelAtPeriodEnd = v.cancelAtPeriodEnd
    createdAt         = now
    updatedAt         = now }

/// Single source of truth for "what plan should this workspace be on
/// right now, given the Stripe subscription state?". Mirroring this
/// from the webhook into `pb_customer_workspaces.plan` keeps the
/// portal UI fast (no Stripe round-trip per page render) and is
/// already idempotent — re-running the same event is a no-op.
let private reconcileWorkspacePlan (cfg : PortalApiConfig)
                                   (sub : StripeSubscription) =
  match sub.workspaceSlug with
  | None -> ()
  | Some slug ->
    let entitled = StripeSubscription.isEntitled sub
    cfg.store.Update slug (fun w ->
      if w.customerId <> sub.customerId then w
      elif entitled then
        // Subscription is good: clear any pending overdue mark and
        // sync plan to whatever Stripe says.
        let nextPlan = sub.plan
        if w.plan = nextPlan && w.overdueSince.IsNone then w
        else
          { w with
              plan = nextPlan
              overdueSince = None
              updatedAt = DateTimeOffset.UtcNow }
      else
        // Subscription is not entitled (canceled / unpaid / past_due
        // beyond Stripe's own grace, etc.). Phase 10 step 10 grace:
        // keep the workspace on its current paid plan and stamp
        // `overdueSince` so the PurgeCron archives it once the
        // configured grace period (default 3 days) elapses. We do
        // NOT immediately downgrade to Free \u2014 that bricks active
        // workloads during a transient card decline.
        if w.plan = PortalPlan.Free then
          // Workspace was already free; no entitlement to lose.
          w
        elif w.overdueSince.IsSome then
          // Already flagged; leave the original timestamp so the
          // grace window doesn't keep resetting on each webhook.
          w
        else
          { w with
              overdueSince = Some DateTimeOffset.UtcNow
              updatedAt = DateTimeOffset.UtcNow })
    |> ignore

// -- billing JSON helpers ---------------------------------------------------

let private subscriptionJson (s : StripeSubscription) : string =
  let (CustomerId cid) = s.customerId
  let sOpt (v : string option) =
    match v with Some x -> JsonSerializer.Serialize x | None -> "null"
  let dOpt (v : DateTimeOffset option) =
    match v with
    | Some x -> JsonSerializer.Serialize (x.ToString("o"))
    | None   -> "null"
  sprintf
    """{"id":%s,"customerId":%s,"workspaceSlug":%s,"priceId":%s,"plan":%s,"status":%s,"currentPeriodEnd":%s,"cancelAtPeriodEnd":%b}"""
    (JsonSerializer.Serialize s.id)
    (JsonSerializer.Serialize cid)
    (sOpt s.workspaceSlug)
    (JsonSerializer.Serialize s.priceId)
    (JsonSerializer.Serialize (PortalPlan.toString s.plan))
    (JsonSerializer.Serialize s.status)
    (dOpt s.currentPeriodEnd)
    s.cancelAtPeriodEnd

// -- billing endpoints ------------------------------------------------------

/// POST /api/portal/workspaces/<slug>/checkout  { "plan": "starter"|"pro" }
/// → { "url": "https://checkout.stripe.com/..." }
let private startCheckout (cfg : PortalApiConfig) (slug : string) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match cfg.billing with
    | None -> return! errJson 503 "billing not configured" ctx
    | Some b ->
      match cfg.store.TryGet slug with
      | None -> return! errJson 404 "no such workspace" ctx
      | Some w when w.customerId <> c.id -> return! errJson 404 "no such workspace" ctx
      | Some _ ->
        match tryParseJson (readBody ctx.request) with
        | None -> return! errJson 400 "invalid JSON body" ctx
        | Some doc ->
          use _ = doc
          let planStr = tryGetString doc.RootElement "plan" |> Option.defaultValue ""
          match PortalPlan.tryParse planStr with
          | None | Some Free ->
            return! errJson 400 "field 'plan' must be one of starter|pro" ctx
          | Some plan ->
            match StripeConfig.priceFor b.stripe plan with
            | None ->
              return! errJson 503 (sprintf "no Stripe price configured for plan '%s'" planStr) ctx
            | Some priceId ->
              let! ec = ensureStripeCustomer b c
              match ec with
              | Result.Error (st, msg) ->
                return! errJson 502 (sprintf "stripe createCustomer: HTTP %d %s" st msg) ctx
              | Result.Ok sc ->
                let! r =
                  StripeClient.createCheckoutSession b.stripe sc priceId slug "/portal"
                match r with
                | Result.Error (st, msg) ->
                  return! errJson 502 (sprintf "stripe checkout: HTTP %d %s" st msg) ctx
                | Result.Ok sess ->
                  return!
                    jsonResp 200
                      (sprintf """{"url":%s,"sessionId":%s}"""
                         (JsonSerializer.Serialize sess.url)
                         (JsonSerializer.Serialize sess.id))
                      ctx
  })

/// POST /api/portal/billing/portal  → { "url": "https://billing.stripe.com/..." }
let private openBillingPortal (cfg : PortalApiConfig) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match cfg.billing with
    | None -> return! errJson 503 "billing not configured" ctx
    | Some b ->
      let! ec = ensureStripeCustomer b c
      match ec with
      | Result.Error (st, msg) ->
        return! errJson 502 (sprintf "stripe customer: HTTP %d %s" st msg) ctx
      | Result.Ok sc ->
        let! r = StripeClient.createBillingPortalSession b.stripe sc "/portal"
        match r with
        | Result.Error (st, msg) ->
          return! errJson 502 (sprintf "stripe portal: HTTP %d %s" st msg) ctx
        | Result.Ok sess ->
          return!
            jsonResp 200
              (sprintf """{"url":%s}""" (JsonSerializer.Serialize sess.url))
              ctx
  })

let private switchPlan (cfg : PortalApiConfig) (slug : string) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    match tryParseJson (readBody ctx.request) with
    | None -> return! errJson 400 "invalid JSON body" ctx
    | Some doc ->
      use _ = doc
      let root = doc.RootElement
      match tryGetString root "plan" |> Option.bind PortalPlan.tryParse with
      | None -> return! errJson 400 "field 'plan' must be one of free|starter|pro" ctx
      | Some plan ->
        match cfg.store.TryGet slug with
        | None -> return! errJson 404 "no such workspace" ctx
        | Some w when w.customerId <> c.id ->
          return! errJson 404 "no such workspace" ctx
        | Some w when w.plan = plan ->
          return! jsonResp 200 (workspaceJson w) ctx
        | Some w ->
          let curSub =
            cfg.billing
            |> Option.bind (fun b -> b.stripeStore.TryGetSubscriptionBySlug slug)
          match w.plan, plan, cfg.billing with
          // free → paid: customer must go through Stripe checkout to
          // attach a payment method first. Surface a clear 402.
          | Free, (Starter | Pro), _ ->
            return!
              errJson 402
                "to upgrade a workspace to a paid plan, POST /api/portal/workspaces/<slug>/checkout first"
                ctx
          // paid → free with no active sub on record: just flip the
          // local plan column (free-quota check still applies).
          | (Starter | Pro), Free, _ when curSub.IsNone ->
            if cfg.store.CountActiveOnPlan c.id Free >= 1 then
              return!
                errJson 409
                  "you already have a free workspace; can't downgrade a second one to free"
                  ctx
            else
              let updated =
                cfg.store.Update slug (fun cur ->
                  { cur with plan = Free; updatedAt = DateTimeOffset.UtcNow })
              match updated with
              | Some w' -> return! jsonResp 200 (workspaceJson w') ctx
              | None    -> return! errJson 500 "row vanished" ctx
          // paid → free with active sub: cancel at period end via
          // Stripe; webhook will downgrade once it fires.
          | (Starter | Pro), Free, Some b ->
            match curSub with
            | None -> return! errJson 500 "subscription disappeared" ctx
            | Some sub ->
              let! r = StripeClient.cancelSubscriptionAtPeriodEnd b.stripe sub.id
              match r with
              | Result.Error (st, msg) ->
                return! errJson 502 (sprintf "stripe cancel: HTTP %d %s" st msg) ctx
              | Result.Ok v ->
                let updated = subFromView b c.id v
                b.stripeStore.UpsertSubscription updated
                return!
                  jsonResp 200
                    (sprintf """{"workspace":%s,"subscription":%s}"""
                       (workspaceJson w) (subscriptionJson updated))
                    ctx
          // paid → paid: swap the price item on the existing sub.
          | (Starter | Pro), (Starter | Pro), Some b ->
            match curSub with
            | None ->
              return!
                errJson 402
                  "no active subscription on this workspace — re-checkout to upgrade"
                  ctx
            | Some sub ->
              match StripeConfig.priceFor b.stripe plan with
              | None ->
                return! errJson 503 (sprintf "no Stripe price configured for plan '%s'"
                                       (PortalPlan.toString plan)) ctx
              | Some priceId ->
                let! r =
                  StripeClient.updateSubscriptionPrice b.stripe sub.id sub.itemId priceId
                match r with
                | Result.Error (st, msg) ->
                  return! errJson 502 (sprintf "stripe update: HTTP %d %s" st msg) ctx
                | Result.Ok v ->
                  let updated = subFromView b c.id v
                  b.stripeStore.UpsertSubscription updated
                  reconcileWorkspacePlan cfg updated
                  // Update returns the fresh local row.
                  let w' =
                    cfg.store.TryGet slug |> Option.defaultValue w
                  return!
                    jsonResp 200
                      (sprintf """{"workspace":%s,"subscription":%s}"""
                         (workspaceJson w') (subscriptionJson updated))
                      ctx
          // No billing configured but the customer asks for a paid switch.
          | _, (Starter | Pro), None ->
            return! errJson 503 "billing not configured" ctx
          | _ ->
            return! errJson 400 "unsupported plan transition" ctx
  })

let private billingSummary (cfg : PortalApiConfig) : WebPart =
  requireAuth cfg (fun c -> fun ctx -> async {
    let (CustomerId cid) = c.id
    let stripeCust =
      cfg.billing |> Option.bind (fun b -> b.stripeStore.TryGetStripeCustomerId c.id)
    let subs =
      match cfg.billing with
      | Some b -> b.stripeStore.ListSubscriptionsForCustomer c.id
      | None   -> []
    let subsJson =
      subs |> List.map subscriptionJson |> String.concat ","
    let body =
      sprintf
        """{"customerId":%s,"email":%s,"stripeCustomerId":%s,"billingEnabled":%b,"subscriptions":[%s]}"""
        (JsonSerializer.Serialize cid)
        (JsonSerializer.Serialize c.email)
        (match stripeCust with Some s -> JsonSerializer.Serialize s | None -> "null")
        cfg.billing.IsSome
        subsJson
    return! jsonResp 200 body ctx
  })

// -- webhook ----------------------------------------------------------------

/// `/api/stripe/webhook` — handles inbound subscription lifecycle
/// events. Signature is verified using the configured webhook secret;
/// without one the route returns 503 (NOT 200) so Stripe's dashboard
/// shows the delivery as failing instead of silently dropping it.
let private stripeWebhook (cfg : PortalApiConfig) : WebPart =
  fun ctx -> async {
    match cfg.billing with
    | None -> return! errJson 503 "billing not configured" ctx
    | Some b ->
      match b.stripe.webhookSecret with
      | None -> return! errJson 503 "webhook secret not configured" ctx
      | Some secret ->
        let raw =
          if isNull ctx.request.rawForm then [||] else ctx.request.rawForm
        let sigHeader =
          match ctx.request.header "stripe-signature" with
          | Choice1Of2 v -> v
          | _ -> ""
        if not (StripeClient.verifyWebhookSignature secret raw sigHeader 300 DateTimeOffset.UtcNow) then
          return! errJson 400 "invalid signature" ctx
        else
          match StripeClient.parseEvent raw with
          | None -> return! errJson 400 "malformed event body" ctx
          | Some ev ->
            use _ = ev.document
            try
              match ev.eventType with
              // Subscription lifecycle — these are the events we
              // mirror into pb_stripe_subscriptions.
              | "customer.subscription.created"
              | "customer.subscription.updated"
              | "customer.subscription.deleted" ->
                let view = StripeClient.parseSubscription ev.object
                let custLookup =
                  b.stripeStore.TryGetCustomerByStripeId view.customerId
                match custLookup with
                | None ->
                  // We don't recognise this Stripe customer — log
                  // and return 200 (Stripe retries 4xx forever, but
                  // there's no work for us to do).
                  eprintfn "  [stripe-webhook] unknown stripe_customer_id %s on %s — ignoring"
                    view.customerId ev.eventType
                  return! jsonResp 200 """{"received":true,"action":"ignored-unknown-customer"}""" ctx
                | Some cid ->
                  let viewWithDeleted =
                    if ev.eventType = "customer.subscription.deleted" then
                      { view with status = "canceled" }
                    else view
                  let row = subFromView b cid viewWithDeleted
                  b.stripeStore.UpsertSubscription row
                  reconcileWorkspacePlan cfg row
                  return! jsonResp 200 """{"received":true}""" ctx
              // Checkout completion is informational — we use it to
              // bind the freshly-created subscription back to a
              // workspace when the subscription's metadata didn't
              // round-trip (rare but possible if the customer used
              // a saved-card flow). The subscription.* events do
              // the heavy lifting; this branch is a safety net.
              | "checkout.session.completed" ->
                let subId =
                  match ev.object.TryGetProperty "subscription" with
                  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                  | _ -> ""
                if subId = "" then
                  return! jsonResp 200 """{"received":true,"action":"no-subscription"}""" ctx
                else
                  let! r = StripeClient.retrieveSubscription b.stripe subId
                  match r with
                  | Result.Error (st, msg) ->
                    eprintfn "  [stripe-webhook] fetch sub %s: HTTP %d %s" subId st msg
                    return! jsonResp 200 """{"received":true,"action":"sub-fetch-failed"}""" ctx
                  | Result.Ok view ->
                    match b.stripeStore.TryGetCustomerByStripeId view.customerId with
                    | None ->
                      return! jsonResp 200 """{"received":true,"action":"ignored-unknown-customer"}""" ctx
                    | Some cid ->
                      let row = subFromView b cid view
                      b.stripeStore.UpsertSubscription row
                      reconcileWorkspacePlan cfg row
                      return! jsonResp 200 """{"received":true}""" ctx
              | _ ->
                // Other event types (invoice.*, payment_intent.*) are
                // out of scope for step 5 — ack so Stripe doesn't
                // retry, but log the type for future expansion.
                return!
                  jsonResp 200
                    (sprintf """{"received":true,"action":"ignored","type":%s}"""
                       (JsonSerializer.Serialize ev.eventType))
                    ctx
            with ex ->
              eprintfn "  [stripe-webhook] handler crashed on %s: %s"
                ev.eventType ex.Message
              return! errJson 500 "webhook handler error" ctx
  }

let private plans : WebPart =
  fun ctx -> async {
    return! jsonResp 200 planCatalogJson ctx
  }

// -- internal heartbeat (step 7) -------------------------------------------

/// `POST /api/portal/internal/heartbeat` — called by the workspace
/// edge whenever it ingests data (typically rate-limited to ~1/min
/// per slug on the workspace side). Authenticates via the same
/// `PULSE_PROVISIONER_TOKEN` bearer the provisioner uses, since the
/// workspace edge already holds that token to reach the
/// provisioner. Body: `{"slug":"acme"}` or `{"slugs":["a","b"]}`.
/// Touches `last_active_at` on every known slug; unknown slugs are
/// silently ignored (so a delayed heartbeat for an already-deleted
/// workspace doesn't 404 noisily).
let private internalHeartbeat (cfg : PortalApiConfig) : WebPart =
  fun ctx -> async {
    let expected = cfg.provisioner.token
    match expected with
    | None -> return! errJson 503 "heartbeat disabled (no provisioner token)" ctx
    | Some tok ->
      let presented =
        match ctx.request.header "authorization" with
        | Choice1Of2 v when v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
          v.Substring(7).Trim()
        | _ -> ""
      // Constant-time compare to keep timing channels closed.
      let eq =
        let a = Encoding.UTF8.GetBytes tok
        let b = Encoding.UTF8.GetBytes presented
        a.Length = b.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
             System.ReadOnlySpan<byte>(a), System.ReadOnlySpan<byte>(b))
      if not eq then return! errJson 401 "bad bearer token" ctx
      else
        match tryParseJson (readBody ctx.request) with
        | None -> return! errJson 400 "invalid JSON body" ctx
        | Some doc ->
          use _ = doc
          let root = doc.RootElement
          let slugs : string list =
            match root.TryGetProperty "slug" with
            | true, v when v.ValueKind = JsonValueKind.String -> [ v.GetString() ]
            | _ ->
              match root.TryGetProperty "slugs" with
              | true, v when v.ValueKind = JsonValueKind.Array ->
                [ for e in v.EnumerateArray() do
                    if e.ValueKind = JsonValueKind.String then yield e.GetString() ]
              | _ -> []
          if List.isEmpty slugs then
            return! errJson 400 "field 'slug' or 'slugs' required" ctx
          else
            let now = DateTimeOffset.UtcNow
            for s in slugs do
              try cfg.store.TouchActivity(s, now)
              with ex ->
                eprintfn "  [heartbeat] touch %s: %s" s ex.Message
            return!
              jsonResp 200
                (sprintf """{"received":%d}""" (List.length slugs)) ctx
  }

// -- composition ------------------------------------------------------------

let webPart (cfg : PortalApiConfig) : WebPart =
  choose [
    GET  >=> path "/api/portal/me"          >=> requireAuth cfg (fun c -> fun ctx -> async {
      let (CustomerId cid) = c.id
      let body =
        sprintf
          """{"customerId":%s,"email":%s,"emailVerified":%b,"hasGithub":%b,"hasPassword":%b}"""
          (JsonSerializer.Serialize cid)
          (JsonSerializer.Serialize c.email)
          c.emailVerifiedAt.IsSome c.githubUserId.IsSome c.passwordHash.IsSome
      return! jsonResp 200 body ctx
    })
    GET  >=> path "/api/portal/plans"       >=> plans
    GET  >=> path "/api/portal/workspaces"  >=> listWorkspaces cfg
    POST >=> path "/api/portal/workspaces"  >=> createWorkspace cfg
    POST >=> pathScan "/api/portal/workspaces/%s/keys"      (fun s -> issueWorkspaceKeyPortal cfg s)
    POST >=> pathScan "/api/portal/workspaces/%s/archive"   (fun s -> archive   cfg s)
    POST >=> pathScan "/api/portal/workspaces/%s/unarchive" (fun s -> unarchive cfg s)
    POST >=> pathScan "/api/portal/workspaces/%s/plan"      (fun s -> switchPlan cfg s)
    POST >=> pathScan "/api/portal/workspaces/%s/checkout"  (fun s -> startCheckout cfg s)
    POST >=> path "/api/portal/billing/portal" >=> openBillingPortal cfg
    GET  >=> path "/api/portal/billing"     >=> billingSummary cfg
    POST >=> path "/api/stripe/webhook"     >=> stripeWebhook cfg
    POST >=> path "/api/portal/internal/heartbeat" >=> internalHeartbeat cfg
  ]
