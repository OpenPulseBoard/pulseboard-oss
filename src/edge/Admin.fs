module PulseBoard.Admin

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.Audit
open PulseBoard.Quotas
open PulseBoard.Retention

// REST surface for tenant + API key management. All routes require the
// `Admin` scope; gating is composed at the call site in Program.fs via
// `Rbac.requireScope`. Handlers here append a per-action audit event with
// richer details (slug, key id, role, scopes) on top of the gate event so
// the audit trail captures the business decision, not just the access.

// -- helpers ----------------------------------------------------------------

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK
    | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST
    | 404 -> NOT_FOUND
    | 409 -> Suave.RequestErrors.CONFLICT
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private tryParseJson (body : string) : JsonDocument option =
  if String.IsNullOrWhiteSpace body then None
  else
    try Some (JsonDocument.Parse body)
    with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

let private parseRole (s : string) : Role option =
  match s.Trim().ToLowerInvariant() with
  | "viewer"  -> Some Viewer
  | "editor"  -> Some Editor
  | "admin"   -> Some Admin
  | "billing" -> Some Billing
  | _ -> None

let private roleStr = function
  | Viewer  -> "viewer"
  | Editor  -> "editor"
  | Admin   -> "admin"
  | Billing -> "billing"

let private parseScope (s : string) : Scope option =
  match s.Trim().ToLowerInvariant() with
  | "ingest" -> Some Scope.Ingest
  | "query"  -> Some Scope.Query
  | "admin"  -> Some Scope.Admin
  | _ -> None

let private scopesToList (have : Scope) : string list =
  [ if hasScope have Scope.Ingest then yield "ingest"
    if hasScope have Scope.Query  then yield "query"
    if hasScope have Scope.Admin  then yield "admin" ]

let private jsonArr (xs : string seq) =
  xs
  |> Seq.map JsonSerializer.Serialize
  |> String.concat ","
  |> sprintf "[%s]"

let private tenantJson (t : Tenant) =
  let (TenantId id) = t.id
  sprintf """{"id":%s,"slug":%s,"plan":"%s","createdAt":"%s"}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize t.slug)
    (planToText t.plan)
    (t.createdAt.ToString("o"))

let private apiKeySummaryJson (r : ApiKeyRecord) =
  let (ApiKeyId id)     = r.id
  let (TenantId tid)    = r.tenantId
  let lastUsed =
    match !r.lastUsedAt with
    | Some ts -> sprintf "\"%s\"" (ts.ToString("o"))
    | None    -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"label":%s,"role":"%s","scopes":%s,"createdAt":"%s","lastUsedAt":%s}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize r.label)
    (roleStr r.role)
    (jsonArr (scopesToList r.scopes))
    (r.createdAt.ToString("o"))
    lastUsed

let private issuedKeyJson (issued : IssuedKey) =
  let r = issued.record
  let (ApiKeyId id)  = r.id
  let (TenantId tid) = r.tenantId
  sprintf
    """{"id":%s,"tenantId":%s,"label":%s,"role":"%s","scopes":%s,"createdAt":"%s","plaintext":%s,"warning":"plaintext is shown once and cannot be recovered"}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize r.label)
    (roleStr r.role)
    (jsonArr (scopesToList r.scopes))
    (r.createdAt.ToString("o"))
    (JsonSerializer.Serialize issued.plaintext)

let private userJson (u : UserRecord) =
  let (UserId uid)   = u.id
  let (TenantId tid) = u.tenantId
  let lastLogin =
    match !u.lastLoginAt with
    | Some ts -> sprintf "\"%s\"" (ts.ToString("o"))
    | None    -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"issuer":%s,"subject":%s,"email":%s,"role":"%s","createdAt":"%s","lastLoginAt":%s}"""
    (JsonSerializer.Serialize uid)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize u.issuer)
    (JsonSerializer.Serialize u.subject)
    (match u.email with Some e -> JsonSerializer.Serialize e | None -> "null")
    (roleStr u.role)
    (u.createdAt.ToString("o"))
    lastLogin

// -- audit helpers ----------------------------------------------------------

let private auditEvent (log : IAuditLog) (ctx : HttpContext)
                       (action : string) (outcome : Outcome)
                       (details : string option) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = outcome
      remoteIp = None
      details  = details }
  try log.Append ev with _ -> ()

// -- handlers ---------------------------------------------------------------

let private listTenants (store : ITenantStore) : WebPart =
  fun ctx -> async {
    let body =
      store.Tenants()
      |> Array.sortBy (fun t -> t.createdAt)
      |> Array.map tenantJson
      |> String.concat ","
      |> sprintf "[%s]"
    return! jsonResp 200 body ctx
  }

let private createTenant (store : ITenantStore) (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let body = readBody ctx.request
    match tryParseJson body with
    | None ->
      auditEvent log ctx "admin.tenant.create" Deny (Some "invalid json")
      return! errJson 400 "invalid JSON body" ctx
    | Some doc ->
      use _ = doc
      match tryGetString doc.RootElement "slug" with
      | None ->
        auditEvent log ctx "admin.tenant.create" Deny (Some "missing slug")
        return! errJson 400 "field 'slug' is required" ctx
      | Some slug ->
        try
          let existed = store.TryGetTenantBySlug slug |> Option.isSome
          let t = store.CreateTenant slug
          let (TenantId tid) = t.id
          let outcome = if existed then Allow else Allow
          let detail =
            if existed then sprintf "slug=%s id=%s (existing)" slug tid
            else            sprintf "slug=%s id=%s (created)"  slug tid
          auditEvent log ctx "admin.tenant.create" outcome (Some detail)
          let status = if existed then 200 else 201
          return! jsonResp status (tenantJson t) ctx
        with ex ->
          auditEvent log ctx "admin.tenant.create" Error (Some ex.Message)
          return! errJson 400 ex.Message ctx
  }

let private listApiKeys (store : ITenantStore) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        store.ApiKeysFor (TenantId tenantId)
        |> Array.map apiKeySummaryJson
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private issueApiKey (store : ITenantStore) (log : IAuditLog)
                        (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditEvent log ctx "admin.apikey.issue" Deny
        (Some (sprintf "tenantId=%s not found" tenantId))
      return! errJson 404 "tenant not found" ctx
    | Some t ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.apikey.issue" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        let label = tryGetString root "label" |> Option.defaultValue "unnamed"
        let roleRaw =
          tryGetString root "role" |> Option.defaultValue "viewer"
        match parseRole roleRaw with
        | None ->
          auditEvent log ctx "admin.apikey.issue" Deny
            (Some (sprintf "bad role=%s" roleRaw))
          return! errJson 400
            "field 'role' must be one of: viewer|editor|admin|billing" ctx
        | Some role ->
          // `scopes`: optional array of strings. Defaults derived from role
          // so the common case ("give me an editor key") needs no scopes
          // field.
          let scopesFromBody =
            match root.TryGetProperty "scopes" with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
              let mutable acc = Scope.None
              let mutable bad : string option = None
              for el in arr.EnumerateArray() do
                if bad.IsNone then
                  if el.ValueKind = JsonValueKind.String then
                    match parseScope (el.GetString()) with
                    | Some s -> acc <- acc ||| s
                    | None   -> bad <- Some (el.GetString())
                  else bad <- Some (el.ToString())
              match bad with
              | Some b -> Result.Error b
              | None   -> Result.Ok (Some acc)
            | _ -> Result.Ok None
          match scopesFromBody with
          | Result.Error b ->
            auditEvent log ctx "admin.apikey.issue" Deny
              (Some (sprintf "bad scope=%s" b))
            return! errJson 400
              (sprintf "unknown scope '%s' (allowed: ingest|query|admin)" b) ctx
          | Result.Ok scopesOpt ->
            let defaultScopes =
              match role with
              | Viewer  -> Scope.Query
              | Editor  -> Scope.Ingest ||| Scope.Query
              | Admin   -> Scope.Ingest ||| Scope.Query ||| Scope.Admin
              | Billing -> Scope.None
            let scopes =
              match scopesOpt with
              | Some s when s <> Scope.None -> s
              | _ -> defaultScopes
            try
              let issued = store.IssueApiKey(t.id, label, role, scopes)
              let (ApiKeyId kid) = issued.record.id
              let (TenantId tid) = t.id
              auditEvent log ctx "admin.apikey.issue" Allow
                (Some (sprintf "tenantId=%s apiKeyId=%s role=%s scopes=%s"
                         tid kid (roleStr role)
                         (String.concat "|" (scopesToList scopes))))
              return! jsonResp 201 (issuedKeyJson issued) ctx
            with ex ->
              auditEvent log ctx "admin.apikey.issue" Error (Some ex.Message)
              return! errJson 400 ex.Message ctx
  }

let private listUsers (store : ITenantStore) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        store.UsersFor (TenantId tenantId)
        |> Array.map userJson
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private updateUserRole (store : ITenantStore) (log : IAuditLog)
                           (userId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetUserById (UserId userId) with
    | None ->
      auditEvent log ctx "admin.user.update" Deny
        (Some (sprintf "userId=%s not found" userId))
      return! errJson 404 "user not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.user.update" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        match tryGetString doc.RootElement "role" with
        | None ->
          auditEvent log ctx "admin.user.update" Deny (Some "missing role")
          return! errJson 400 "field 'role' is required" ctx
        | Some roleRaw ->
          match parseRole roleRaw with
          | None ->
            auditEvent log ctx "admin.user.update" Deny
              (Some (sprintf "bad role=%s" roleRaw))
            return! errJson 400
              "field 'role' must be one of: viewer|editor|admin|billing" ctx
          | Some role ->
            match store.UpdateUserRole(UserId userId, role) with
            | None ->
              auditEvent log ctx "admin.user.update" Error
                (Some "update returned None")
              return! errJson 500 "failed to update user" ctx
            | Some updated ->
              let (UserId uid) = updated.id
              auditEvent log ctx "admin.user.update" Allow
                (Some (sprintf "userId=%s role=%s" uid (roleStr role)))
              return! jsonResp 200 (userJson updated) ctx
  }

let private auditTail (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let tail =
      match ctx.request.queryParam "tail" with
      | Choice1Of2 v ->
        match Int32.TryParse v with
        | true, n when n > 0 -> min n 1000
        | _ -> 100
      | _ -> 100
    let events = log.Tail tail
    let body =
      events
      |> Array.map serialize
      |> String.concat ","
      |> sprintf "[%s]"
    return! jsonResp 200 body ctx
  }

// -- quota handlers ---------------------------------------------------------

let private limitJson (l : Limit) =
  sprintf """{"capacity":%g,"refillPerSec":%g}""" l.capacity l.refillPerSec

let private quotasJson (eff : Effective) =
  let rates =
    allKinds
    |> Array.map (fun k ->
        let l = Map.find k eff.rates
        sprintf "%s:%s" (JsonSerializer.Serialize (kindStr k)) (limitJson l))
    |> String.concat ","
  let overrides =
    eff.rateOverrides
    |> Seq.map (fun k -> JsonSerializer.Serialize (kindStr k))
    |> String.concat ","
  sprintf
    """{"rates":{%s},"cardinality":%d,"rateOverrides":[%s],"cardinalityOverridden":%b}"""
    rates eff.cardinality overrides eff.cardinalityOverridden

let private showQuotas (store : ITenantStore) (quotaStore : QuotaStore)
                       (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None -> return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let eff = quotaStore.Effective (TenantId tenantId)
      return! jsonResp 200 (quotasJson eff) ctx
  }

/// Parse a rate-limit object: `null` means "clear override", a `{capacity,
/// refillPerSec}` object means "set". Returns `Error` for malformed shapes.
let private parseRateOverride (el : JsonElement) : Result<Limit option, string> =
  match el.ValueKind with
  | JsonValueKind.Null -> Result.Ok None
  | JsonValueKind.Object ->
    let cap =
      match el.TryGetProperty "capacity" with
      | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetDouble())
      | _ -> None
    let rate =
      match el.TryGetProperty "refillPerSec" with
      | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetDouble())
      | _ -> None
    match cap, rate with
    | Some c, Some r when c >= 0.0 && r >= 0.0 ->
      Result.Ok (Some { capacity = c; refillPerSec = r })
    | _ ->
      Result.Error "rate override requires non-negative 'capacity' and 'refillPerSec'"
  | _ -> Result.Error "rate override must be object or null"

let private updateQuotas (store : ITenantStore) (quotaStore : QuotaStore)
                         (log : IAuditLog) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditEvent log ctx "admin.quota.set" Deny
        (Some (sprintf "tenantId=%s not found" tenantId))
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.quota.set" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
          auditEvent log ctx "admin.quota.set" Deny (Some "body not object")
          return! errJson 400 "body must be a JSON object" ctx
        else
          // Validate everything first so a malformed field doesn't leave
          // the store half-updated.
          let mutable err : string option = None
          let rateUpdates = ResizeArray<Kind * Limit option>()
          let mutable cardUpdate : (int option * bool) = (None, false)
          for prop in root.EnumerateObject() do
            if err.IsNone then
              if prop.Name = "cardinality" then
                match prop.Value.ValueKind with
                | JsonValueKind.Null ->
                  cardUpdate <- (None, true)
                | JsonValueKind.Number ->
                  let n = prop.Value.GetInt32()
                  if n < 0 then
                    err <- Some "cardinality must be >= 0 (0 = unlimited)"
                  else
                    cardUpdate <- (Some n, true)
                | _ ->
                  err <- Some "cardinality must be integer or null"
              else
                match tryParseKind prop.Name with
                | None ->
                  err <- Some (sprintf "unknown quota kind '%s'" prop.Name)
                | Some k ->
                  match parseRateOverride prop.Value with
                  | Result.Error m -> err <- Some m
                  | Result.Ok lo   -> rateUpdates.Add(k, lo)
          match err with
          | Some m ->
            auditEvent log ctx "admin.quota.set" Deny (Some m)
            return! errJson 400 m ctx
          | None ->
            try
              for k, lo in rateUpdates do
                quotaStore.SetRateOverride(TenantId tenantId, k, lo)
              let cardOpt, cardSet = cardUpdate
              if cardSet then
                quotaStore.SetCardinalityOverride(TenantId tenantId, cardOpt)
              let detail =
                let parts =
                  [ for k, lo in rateUpdates ->
                      match lo with
                      | Some l ->
                        sprintf "%s=%g/%g" (kindStr k) l.capacity l.refillPerSec
                      | None ->
                        sprintf "%s=clear" (kindStr k)
                    if cardSet then
                      match cardOpt with
                      | Some n -> yield sprintf "cardinality=%d" n
                      | None   -> yield "cardinality=clear" ]
                String.concat " " parts
              auditEvent log ctx "admin.quota.set" Allow
                (Some (sprintf "tenantId=%s %s" tenantId detail))
              let eff = quotaStore.Effective (TenantId tenantId)
              return! jsonResp 200 (quotasJson eff) ctx
            with ex ->
              auditEvent log ctx "admin.quota.set" Error (Some ex.Message)
              return! errJson 500 ex.Message ctx
  }

// -- retention (Phase 3 step 3) ---------------------------------------------

let private retentionJson (eff : EffectivePolicy) : string =
  let field (v : int64 option) =
    match v with Some n -> string n | None -> "null"
  sprintf
    """{"metricsMs":%s,"logsMs":%s,"tracesMs":%s,"overridden":{"metricsMs":%b,"logsMs":%b,"tracesMs":%b}}"""
    (field eff.metricsMs)
    (field eff.logsMs)
    (field eff.tracesMs)
    eff.metricsOverridden
    eff.logsOverridden
    eff.tracesOverridden

let private showRetention (store : ITenantStore) (retention : RetentionStore)
                          (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None -> return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let eff = retention.Effective (TenantId tenantId)
      return! jsonResp 200 (retentionJson eff) ctx
  }

/// Parse one TTL field: a number sets it, `null` clears it, missing
/// leaves it untouched. Returns `Ok (value, present)` where `present`
/// is `false` if the property was absent from the body.
let private parseTtlField (root : JsonElement) (name : string)
  : Result<(int64 option * bool), string> =
  match root.TryGetProperty name with
  | false, _ -> Result.Ok (None, false)
  | true, v ->
    match v.ValueKind with
    | JsonValueKind.Null -> Result.Ok (None, true)
    | JsonValueKind.Number ->
      match v.TryGetInt64() with
      | true, n when n >= 0L -> Result.Ok (Some n, true)
      | _ -> Result.Error (sprintf "'%s' must be a non-negative integer or null" name)
    | _ -> Result.Error (sprintf "'%s' must be integer or null" name)

let private updateRetention (store : ITenantStore) (retention : RetentionStore)
                            (log : IAuditLog) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditEvent log ctx "admin.retention.set" Deny
        (Some (sprintf "tenantId=%s not found" tenantId))
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.retention.set" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
          auditEvent log ctx "admin.retention.set" Deny (Some "body not object")
          return! errJson 400 "body must be a JSON object" ctx
        else
          // Validate up-front so a malformed field doesn't leave the
          // store half-updated.
          let metrics = parseTtlField root "metricsMs"
          let logs    = parseTtlField root "logsMs"
          let traces  = parseTtlField root "tracesMs"
          let err =
            [ metrics; logs; traces ]
            |> List.tryPick (function Result.Error m -> Some m | _ -> None)
          match err with
          | Some m ->
            auditEvent log ctx "admin.retention.set" Deny (Some m)
            return! errJson 400 m ctx
          | None ->
            try
              // Build target policy by overlaying the current override
              // with the requested mutations. Fields explicitly set to
              // `null` clear; fields absent are left untouched.
              let cur = retention.Effective (TenantId tenantId)
              let curOverride =
                { metricsMs = if cur.metricsOverridden then cur.metricsMs else None
                  logsMs    = if cur.logsOverridden    then cur.logsMs    else None
                  tracesMs  = if cur.tracesOverridden  then cur.tracesMs  else None }
              let apply (curVal : int64 option) (parsed : Result<int64 option * bool, string>) =
                match parsed with
                | Result.Ok (v, true)  -> v
                | _                    -> curVal
              let next : RetentionPolicy =
                { metricsMs = apply curOverride.metricsMs metrics
                  logsMs    = apply curOverride.logsMs    logs
                  tracesMs  = apply curOverride.tracesMs  traces }
              retention.SetOverride(TenantId tenantId, next)
              let detail =
                let part name (r : Result<int64 option * bool, string>) =
                  match r with
                  | Result.Ok (Some n, true) -> Some (sprintf "%s=%d" name n)
                  | Result.Ok (None, true)   -> Some (sprintf "%s=clear" name)
                  | _                        -> None
                [ part "metricsMs" metrics
                  part "logsMs"    logs
                  part "tracesMs"  traces ]
                |> List.choose id
                |> String.concat " "
              auditEvent log ctx "admin.retention.set" Allow
                (Some (sprintf "tenantId=%s %s" tenantId detail))
              let eff = retention.Effective (TenantId tenantId)
              return! jsonResp 200 (retentionJson eff) ctx
            with ex ->
              auditEvent log ctx "admin.retention.set" Error (Some ex.Message)
              return! errJson 500 ex.Message ctx
  }

/// Complete admin WebPart. Gating (`Admin` scope) is applied by the caller.
let webPart (store : ITenantStore) (quotaStore : QuotaStore)
            (metricBackend : PulseBoard.Storage.IMetricBackend)
            (retention : RetentionStore)
            (log : IAuditLog) : WebPart =
  let showCardinality (tenantId : string) : WebPart =
    fun ctx -> async {
      match store.TryGetTenant (TenantId tenantId) with
      | None -> return! errJson 404 "tenant not found" ctx
      | Some _ ->
        let eff = quotaStore.Effective (TenantId tenantId)
        let json =
          sprintf """{"seriesCount":%d,"droppedSamples":%d,"cap":%d,"capOverridden":%b}"""
            (metricBackend.SeriesCount tenantId)
            (metricBackend.DroppedCardinality tenantId)
            eff.cardinality
            eff.cardinalityOverridden
        return! jsonResp 200 json ctx
    }
  choose [
    GET  >=> path "/api/admin/audit"        >=> auditTail log
    GET  >=> path "/api/admin/tenants"      >=> listTenants store
    POST >=> path "/api/admin/tenants"      >=> createTenant store log
    GET  >=> pathScan "/api/admin/tenants/%s/api-keys" (listApiKeys store)
    POST >=> pathScan "/api/admin/tenants/%s/api-keys" (issueApiKey store log)
    GET  >=> pathScan "/api/admin/tenants/%s/users"    (listUsers store)
    PATCH >=> pathScan "/api/admin/users/%s"           (updateUserRole store log)
    GET  >=> pathScan "/api/admin/tenants/%s/quotas"   (showQuotas store quotaStore)
    PUT  >=> pathScan "/api/admin/tenants/%s/quotas"   (updateQuotas store quotaStore log)
    GET  >=> pathScan "/api/admin/tenants/%s/cardinality" showCardinality
    GET  >=> pathScan "/api/admin/tenants/%s/retention"
              (showRetention store retention)
    PUT  >=> pathScan "/api/admin/tenants/%s/retention"
              (updateRetention store retention log)
    NOT_FOUND """{"error":"unknown admin endpoint"}"""
      >=> Writers.setMimeType "application/json"
  ]

// ---------------------------------------------------------------------------
// PLAN.md Phase 6 #4 — envelope-encryption / PII surface. Kept in this file
// rather than `Secrets.fs` because `Secrets.fs` compiles before `Rbac.fs`
// and these handlers consume `Rbac.tryGetTenant` indirectly (the caller
// composes `requireScope Admin` over the whole web part).
// ---------------------------------------------------------------------------

let private tenantFromCtx (ctx : HttpContext) : TenantId option =
  PulseBoard.Rbac.tryGetTenant ctx
  |> Option.map (fun t -> t.tenant.id)

let private piiGet (policy : PulseBoard.Secrets.IPiiPolicyStore) : WebPart =
  fun ctx -> async {
    match tenantFromCtx ctx with
    | None -> return! errJson 400 "no tenant" ctx
    | Some (TenantId tid) ->
      let fields = policy.Get tid
      let sb = StringBuilder().Append("{\"fields\":[")
      for i in 0 .. fields.Length - 1 do
        if i > 0 then sb.Append(',') |> ignore
        sb.Append(JsonSerializer.Serialize(fields.[i] : string)) |> ignore
      sb.Append("]}") |> ignore
      return! jsonResp 200 (sb.ToString()) ctx
  }

let private piiPut (policy : PulseBoard.Secrets.IPiiPolicyStore)
                   (log : IAuditLog) : WebPart =
  fun ctx -> async {
    match tenantFromCtx ctx with
    | None -> return! errJson 400 "no tenant" ctx
    | Some (TenantId tid) ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None -> return! errJson 400 "invalid json" ctx
      | Some doc ->
        try
          let root = doc.RootElement
          let fields =
            match root.TryGetProperty "fields" with
            | true, v when v.ValueKind = JsonValueKind.Array ->
              [| for f in v.EnumerateArray() do
                   if f.ValueKind = JsonValueKind.String then
                     yield f.GetString() |]
            | _ -> [||]
          policy.Put(tid, fields)
          auditEvent log ctx "secrets.policy.put" Allow
            (Some (sprintf "tenant=%s count=%d" tid fields.Length))
          return! jsonResp 200 (sprintf """{"ok":true,"count":%d}""" fields.Length) ctx
        with ex ->
          return! errJson 400 ex.Message ctx
  }

let private secretsEncrypt (secrets : PulseBoard.Secrets.ISecretsStore) : WebPart =
  fun ctx -> async {
    match tenantFromCtx ctx with
    | None -> return! errJson 400 "no tenant" ctx
    | Some (TenantId tid) ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None -> return! errJson 400 "invalid json" ctx
      | Some doc ->
        match tryGetString doc.RootElement "plaintext" with
        | None -> return! errJson 400 "missing plaintext" ctx
        | Some pt ->
          let token = secrets.Encrypt(tid, pt)
          return! jsonResp 200
                    (sprintf """{"ciphertext":%s}""" (JsonSerializer.Serialize token))
                    ctx
  }

let private secretsDecrypt (secrets : PulseBoard.Secrets.ISecretsStore)
                           (log : IAuditLog) : WebPart =
  fun ctx -> async {
    match tenantFromCtx ctx with
    | None -> return! errJson 400 "no tenant" ctx
    | Some (TenantId tid) ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None -> return! errJson 400 "invalid json" ctx
      | Some doc ->
        match tryGetString doc.RootElement "ciphertext" with
        | None -> return! errJson 400 "missing ciphertext" ctx
        | Some token ->
          match secrets.Decrypt(tid, token) with
          | None ->
            auditEvent log ctx "secrets.decrypt" Deny (Some "decrypt failed")
            return! errJson 400 "decrypt failed" ctx
          | Some pt ->
            auditEvent log ctx "secrets.decrypt" Allow None
            return! jsonResp 200
                      (sprintf """{"plaintext":%s}""" (JsonSerializer.Serialize pt))
                      ctx
  }

/// PLAN.md Phase 6 #4 REST surface. Mount under `Rbac.requireScope Admin`.
let secretsWebPart (secrets : PulseBoard.Secrets.ISecretsStore)
                   (policy  : PulseBoard.Secrets.IPiiPolicyStore)
                   (log     : IAuditLog) : WebPart =
  choose [
    GET  >=> path "/api/secrets/policy"  >=> piiGet policy
    PUT  >=> path "/api/secrets/policy"  >=> piiPut policy log
    POST >=> path "/api/secrets/encrypt" >=> secretsEncrypt secrets
    POST >=> path "/api/secrets/decrypt" >=> secretsDecrypt secrets log
  ]

// ---------------------------------------------------------------------------
// PLAN.md Phase 7 #1 + #2 — commercial admin surface: plan mutation, usage
// snapshot, on-demand billing flush. Mount under `Rbac.requireScope Admin`.
// ---------------------------------------------------------------------------

let private updateTenantPlan (store : ITenantStore) (log : IAuditLog)
                             (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditEvent log ctx "admin.tenant.plan" Deny
        (Some (sprintf "tenantId=%s not found" tenantId))
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      match tryParseJson (readBody ctx.request) with
      | None ->
        auditEvent log ctx "admin.tenant.plan" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        match tryGetString doc.RootElement "plan" with
        | None ->
          auditEvent log ctx "admin.tenant.plan" Deny (Some "missing plan")
          return! errJson 400 "field 'plan' is required" ctx
        | Some s ->
          match tryParsePlan s with
          | None ->
            auditEvent log ctx "admin.tenant.plan" Deny
              (Some (sprintf "bad plan=%s" s))
            return! errJson 400
              "field 'plan' must be one of: free|pro|enterprise" ctx
          | Some plan ->
            match store.UpdateTenantPlan (TenantId tenantId, plan) with
            | None ->
              auditEvent log ctx "admin.tenant.plan" Error
                (Some "update failed")
              return! errJson 500 "update failed" ctx
            | Some t ->
              auditEvent log ctx "admin.tenant.plan" Allow
                (Some (sprintf "tenantId=%s plan=%s" tenantId (planToText plan)))
              return! jsonResp 200 (tenantJson t) ctx
  }

let private tenantUsage (store : ITenantStore)
                        (meter : PulseBoard.Billing.IBillingMeter)
                        (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None -> return! errJson 404 "tenant not found" ctx
    | Some t ->
      let snap = meter.Snapshot (TenantId tenantId)
      let sb = StringBuilder()
      sb.Append("{\"tenantId\":") |> ignore
      sb.Append(JsonSerializer.Serialize tenantId) |> ignore
      sb.Append(",\"plan\":\"")
        .Append(planToText t.plan)
        .Append("\",\"usage\":{") |> ignore
      let mutable first = true
      for KeyValue(k, v) in snap do
        if not first then sb.Append(',') |> ignore
        first <- false
        sb.Append('"').Append(PulseBoard.Billing.usageKindStr k).Append('"')
          .Append(':').Append(v) |> ignore
      sb.Append("}}") |> ignore
      return! jsonResp 200 (sb.ToString()) ctx
  }

let private billingFlush (store : ITenantStore)
                         (meter : PulseBoard.Billing.IBillingMeter)
                         (providers : PulseBoard.Billing.IBillingProvider[])
                         (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let planFor (tid : TenantId) =
      match store.TryGetTenant tid with
      | Some t -> t.plan
      | None   -> Free
    let n = PulseBoard.Billing.flushNow meter providers planFor
    auditEvent log ctx "admin.billing.flush" Allow
      (Some (sprintf "events=%d providers=%d" n providers.Length))
    return!
      jsonResp 200 (sprintf """{"events":%d,"providers":%d}""" n providers.Length) ctx
  }

/// Phase 7 #1 + #2 web part. Mount under `Rbac.requireScope Admin` exactly
/// like the secrets surface.
let billingWebPart (store     : ITenantStore)
                   (meter     : PulseBoard.Billing.IBillingMeter)
                   (providers : PulseBoard.Billing.IBillingProvider[])
                   (log       : IAuditLog) : WebPart =
  choose [
    PATCH >=> pathScan "/api/admin/tenants/%s/plan"
                (updateTenantPlan store log)
    GET   >=> pathScan "/api/admin/tenants/%s/usage"
                (tenantUsage store meter)
    POST  >=> path "/api/admin/billing/flush"
                >=> billingFlush store meter providers log
  ]

