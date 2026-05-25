module PulseBoard.StripeClient

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Collections.Generic

// Phase 10 step 5 — minimum-viable Stripe REST client.
//
// We deliberately avoid the Stripe.net SDK: a single static-method call
// pattern + plain HTTP keeps the OSS surface dependency-free and lets
// the open-core community swap in any compatible billing back-end.
//
// All Stripe v1 endpoints used here accept `application/x-www-form-urlencoded`
// bodies (the legacy form encoding — *not* JSON). We post pairs as
// `key=value&...`, including bracketed nested keys like
// `line_items[0][price]=price_abc`.
//
// Webhook signature verification follows the Stripe-Signature spec:
//   header = "t=<unix>,v1=<hex>(,v1=<hex>)*"
//   payload = "<t>.<raw body>"
//   compare = HMAC_SHA256(secret, payload) hex-encoded
//   reject if no v1 matches OR |now - t| > tolerance (default 5min).

// -- config -----------------------------------------------------------------

[<NoComparison; NoEquality>]
type StripeConfig =
  { /// `sk_test_...` / `sk_live_...`. Required for any API call; when
    /// the field is `None` the portal still mounts but billing
    /// endpoints return 503 (handy for offline dev).
    secretKey       : string
    /// `whsec_...`. Required for `/api/stripe/webhook` to accept
    /// inbound events. When `None`, the webhook route returns 503.
    webhookSecret   : string option
    /// Used in checkout-session `success_url` / `cancel_url` and
    /// billing-portal `return_url`. e.g. "https://pulseboard.cloud".
    publicBase      : string
    /// `price_xxx` for the Starter tier (monthly recurring). When
    /// missing, the starter plan can't be checked out / switched to.
    priceStarter    : string option
    priceStarterAnnual : string option
    priceProMonthly : string option
    priceProAnnual  : string option }

module StripeConfig =
  /// Maps the user-facing plan id (sent by the portal SPA) to the
  /// configured Stripe price id. Annual variants are out of scope for
  /// step 5 — we surface monthly only.
  let priceFor (cfg : StripeConfig) (plan : PulseBoard.PortalStore.PortalPlan) : string option =
    match plan with
    | PulseBoard.PortalStore.Free    -> None
    | PulseBoard.PortalStore.Starter -> cfg.priceStarter
    | PulseBoard.PortalStore.Pro     -> cfg.priceProMonthly

  /// Reverse mapping for webhooks: given a stripe price id, work out
  /// which portal plan it represents. Unknown prices default to `Pro`
  /// (safest from a quota/lockout perspective) and log a warning.
  let planForPrice (cfg : StripeConfig) (priceId : string) : PulseBoard.PortalStore.PortalPlan =
    if cfg.priceStarter = Some priceId then PulseBoard.PortalStore.Starter
    elif cfg.priceStarterAnnual = Some priceId then PulseBoard.PortalStore.Starter
    elif cfg.priceProMonthly = Some priceId then PulseBoard.PortalStore.Pro
    elif cfg.priceProAnnual = Some priceId then PulseBoard.PortalStore.Pro
    else
      eprintfn "  [stripe] WARN unknown price id %s — falling back to Pro" priceId
      PulseBoard.PortalStore.Pro

// -- HTTP -------------------------------------------------------------------

/// One shared, long-lived HttpClient. Stripe rate-limits per-key, not
/// per-connection; a single pool is the right shape.
let private http : HttpClient =
  let h = new HttpClient(BaseAddress = Uri "https://api.stripe.com")
  h.Timeout <- TimeSpan.FromSeconds 30.0
  h

let private encodeForm (pairs : seq<string * string>) =
  pairs
  |> Seq.map (fun (k, v) -> Uri.EscapeDataString k + "=" + Uri.EscapeDataString v)
  |> String.concat "&"

let private postForm (cfg : StripeConfig) (path : string)
                     (pairs : seq<string * string>) : Async<Result<JsonDocument, int * string>> =
  async {
    let body = encodeForm pairs
    use req = new HttpRequestMessage(HttpMethod.Post, path)
    req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", cfg.secretKey)
    // Stripe-Version pin — a fixed API version means an SDK upgrade
    // never silently changes our shape. We use a known-stable date.
    req.Headers.TryAddWithoutValidation("Stripe-Version", "2024-12-18.acacia") |> ignore
    req.Content <- new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
    try
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if not resp.IsSuccessStatusCode then
        return Result.Error (int resp.StatusCode, text)
      else
        return Result.Ok (JsonDocument.Parse text)
    with ex ->
      return Result.Error (502, ex.Message)
  }

let private jget (el : JsonElement) (n : string) : string =
  match el.TryGetProperty n with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | _ -> ""

let private jgetOpt (el : JsonElement) (n : string) : string option =
  match el.TryGetProperty n with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString() in if String.IsNullOrEmpty s then None else Some s
  | _ -> None

let private jgetBool (el : JsonElement) (n : string) : bool =
  match el.TryGetProperty n with
  | true, v when v.ValueKind = JsonValueKind.True  -> true
  | _ -> false

let private jgetLong (el : JsonElement) (n : string) : int64 option =
  match el.TryGetProperty n with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    match v.TryGetInt64() with true, n -> Some n | _ -> None
  | _ -> None

// -- typed responses --------------------------------------------------------

[<NoComparison; NoEquality>]
type StripeCustomer =
  { id    : string
    email : string option }

[<NoComparison; NoEquality>]
type CheckoutSession =
  { id  : string
    url : string }

[<NoComparison; NoEquality>]
type BillingPortalSession =
  { id  : string
    url : string }

[<NoComparison; NoEquality>]
type SubscriptionView =
  { id                  : string
    /// The single subscription item id — used to update price in-place.
    /// Stripe subscriptions can have multiple items in general; we
    /// only ever create one per workspace and so use the first.
    itemId              : string
    customerId          : string
    priceId             : string
    status              : string
    currentPeriodEnd    : DateTimeOffset option
    cancelAtPeriodEnd   : bool
    /// `metadata[workspace_slug]` round-tripped through the checkout
    /// session and onto the subscription, so webhooks can tie a
    /// subscription back to a portal workspace row without a separate
    /// lookup table.
    workspaceSlug       : string option }

let parseSubscription (root : JsonElement) : SubscriptionView =
  let items =
    match root.TryGetProperty "items" with
    | true, items ->
      match items.TryGetProperty "data" with
      | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray() |> Seq.toList
      | _ -> []
    | _ -> []
  let firstItemId, firstPriceId =
    match items with
    | [] -> "", ""
    | x :: _ ->
      let pid =
        match x.TryGetProperty "price" with
        | true, p -> jget p "id"
        | _       -> ""
      jget x "id", pid
  let slug =
    match root.TryGetProperty "metadata" with
    | true, m when m.ValueKind = JsonValueKind.Object -> jgetOpt m "workspace_slug"
    | _ -> None
  { id                = jget root "id"
    itemId            = firstItemId
    customerId        = jget root "customer"
    priceId           = firstPriceId
    status            = jget root "status"
    currentPeriodEnd  =
      jgetLong root "current_period_end"
      |> Option.map (fun s -> DateTimeOffset.FromUnixTimeSeconds s)
    cancelAtPeriodEnd = jgetBool root "cancel_at_period_end"
    workspaceSlug     = slug }

// -- API calls --------------------------------------------------------------

/// Create a Stripe customer for this PulseBoard customer. Idempotent
/// against `metadata[pb_customer_id]` only at the caller level — we
/// don't dedupe here; the portal store remembers the cus_id once
/// created so we never call this twice for the same account.
let createCustomer (cfg : StripeConfig) (email : string) (pbCustomerId : string)
                   : Async<Result<StripeCustomer, int * string>> =
  async {
    let! r =
      postForm cfg "/v1/customers"
        [ "email", email
          "metadata[pb_customer_id]", pbCustomerId ]
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok doc ->
      use _ = doc
      let root = doc.RootElement
      return Result.Ok { id = jget root "id"; email = jgetOpt root "email" }
  }

/// Start a subscription-mode checkout. `priceId` must already be the
/// concrete Stripe price (looked up via StripeConfig.priceFor by the
/// caller). `workspaceSlug` is stamped into both the checkout
/// session's metadata AND the resulting subscription's metadata, so
/// the webhook handler can route the event back to the right row.
let createCheckoutSession (cfg : StripeConfig)
                          (stripeCustomerId : string)
                          (priceId : string)
                          (workspaceSlug : string)
                          (returnPath : string)
                          : Async<Result<CheckoutSession, int * string>> =
  async {
    let baseUrl = cfg.publicBase.TrimEnd '/'
    let! r =
      postForm cfg "/v1/checkout/sessions"
        [ "mode", "subscription"
          "customer", stripeCustomerId
          "line_items[0][price]", priceId
          "line_items[0][quantity]", "1"
          "success_url", sprintf "%s%s?checkout=success&session_id={CHECKOUT_SESSION_ID}" baseUrl returnPath
          "cancel_url",  sprintf "%s%s?checkout=cancel"  baseUrl returnPath
          "client_reference_id", workspaceSlug
          "metadata[workspace_slug]", workspaceSlug
          "subscription_data[metadata][workspace_slug]", workspaceSlug
          "allow_promotion_codes", "true"
          "billing_address_collection", "auto" ]
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok doc ->
      use _ = doc
      let root = doc.RootElement
      return Result.Ok { id = jget root "id"; url = jget root "url" }
  }

/// Create a Stripe billing-portal session so the customer can manage
/// payment methods, cancel, etc. without us re-implementing the UI.
let createBillingPortalSession (cfg : StripeConfig)
                               (stripeCustomerId : string)
                               (returnPath : string)
                               : Async<Result<BillingPortalSession, int * string>> =
  async {
    let baseUrl = cfg.publicBase.TrimEnd '/'
    let! r =
      postForm cfg "/v1/billing_portal/sessions"
        [ "customer", stripeCustomerId
          "return_url", baseUrl + returnPath ]
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok doc ->
      use _ = doc
      let root = doc.RootElement
      return Result.Ok { id = jget root "id"; url = jget root "url" }
  }

/// Swap the price on an existing subscription item (the cheap path
/// for paid↔paid plan changes — no checkout dance, no card re-collection).
/// `proration_behavior=create_prorations` gives the customer the
/// expected mid-cycle credit/charge.
let updateSubscriptionPrice (cfg : StripeConfig)
                            (subscriptionId : string)
                            (subscriptionItemId : string)
                            (newPriceId : string)
                            : Async<Result<SubscriptionView, int * string>> =
  async {
    let! r =
      postForm cfg (sprintf "/v1/subscriptions/%s" (Uri.EscapeDataString subscriptionId))
        [ "items[0][id]", subscriptionItemId
          "items[0][price]", newPriceId
          "proration_behavior", "create_prorations"
          "expand[]", "items.data.price" ]
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok doc ->
      use _ = doc
      return Result.Ok (parseSubscription doc.RootElement)
  }

/// Cancel a subscription at period end (so the customer keeps access
/// until they've used what they paid for). The webhook will then mark
/// it `canceled` and we'll downgrade the workspace.
let cancelSubscriptionAtPeriodEnd (cfg : StripeConfig)
                                  (subscriptionId : string)
                                  : Async<Result<SubscriptionView, int * string>> =
  async {
    let! r =
      postForm cfg (sprintf "/v1/subscriptions/%s" (Uri.EscapeDataString subscriptionId))
        [ "cancel_at_period_end", "true" ]
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok doc ->
      use _ = doc
      return Result.Ok (parseSubscription doc.RootElement)
  }

/// Fetch one subscription (used during webhook reconciliation when
/// the event payload is incomplete or stale).
let retrieveSubscription (cfg : StripeConfig)
                         (subscriptionId : string)
                         : Async<Result<SubscriptionView, int * string>> =
  async {
    try
      use req =
        new HttpRequestMessage(
          HttpMethod.Get,
          sprintf "/v1/subscriptions/%s?expand[]=items.data.price"
            (Uri.EscapeDataString subscriptionId))
      req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", cfg.secretKey)
      req.Headers.TryAddWithoutValidation("Stripe-Version", "2024-12-18.acacia") |> ignore
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if not resp.IsSuccessStatusCode then
        return Result.Error (int resp.StatusCode, text)
      else
        use doc = JsonDocument.Parse text
        return Result.Ok (parseSubscription doc.RootElement)
    with ex ->
      return Result.Error (502, ex.Message)
  }

// -- webhook signature ------------------------------------------------------

/// Parse a `Stripe-Signature` header value into its `t` (unix
/// timestamp) and the list of `v1` HMAC hex strings.
let private parseSigHeader (header : string) : (int64 * string list) option =
  if isNull header then None
  else
    let mutable t : int64 voption = ValueNone
    let v1s = ResizeArray<string>()
    for part in header.Split(',') do
      let kv = part.Split([| '=' |], 2)
      if kv.Length = 2 then
        match kv.[0].Trim(), kv.[1].Trim() with
        | "t",  v ->
          match Int64.TryParse v with
          | true, n -> t <- ValueSome n
          | _       -> ()
        | "v1", v -> v1s.Add v
        | _ -> ()
    match t with
    | ValueSome n when v1s.Count > 0 -> Some (n, List.ofSeq v1s)
    | _ -> None

let private hexLower (bytes : byte[]) =
  let sb = StringBuilder(bytes.Length * 2)
  for b in bytes do sb.AppendFormat("{0:x2}", b) |> ignore
  sb.ToString()

let private ctEq (a : string) (b : string) =
  if a.Length <> b.Length then false
  else
    let mutable d = 0
    for i in 0 .. a.Length - 1 do d <- d ||| (int a.[i] ^^^ int b.[i])
    d = 0

/// Verify a Stripe webhook signature header against the raw request
/// body. `now` is injectable for tests; production code passes
/// `DateTimeOffset.UtcNow`.
let verifyWebhookSignature (secret : string)
                           (rawBody : byte[])
                           (sigHeader : string)
                           (toleranceSeconds : int)
                           (now : DateTimeOffset)
                           : bool =
  match parseSigHeader sigHeader with
  | None -> false
  | Some (t, v1s) ->
    let drift = abs (now.ToUnixTimeSeconds() - t)
    if drift > int64 toleranceSeconds then false
    else
      let payload =
        let prefix = Encoding.UTF8.GetBytes(string t + ".")
        let buf = Array.zeroCreate<byte> (prefix.Length + rawBody.Length)
        Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length)
        Buffer.BlockCopy(rawBody, 0, buf, prefix.Length, rawBody.Length)
        buf
      use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)
      let mac = hexLower (h.ComputeHash payload)
      v1s |> List.exists (fun v -> ctEq mac v)

// -- webhook event shape ----------------------------------------------------

[<NoComparison; NoEquality>]
type StripeEvent =
  { id        : string
    eventType : string
    /// `data.object` JsonElement; webhook handlers `match`
    /// on `eventType` and pull what they need out of this.
    object    : JsonElement
    /// We retain a reference to the parent document so callers can
    /// `use _ = ev.document` to release the parse buffer when done.
    document  : JsonDocument }

let parseEvent (rawBody : byte[]) : StripeEvent option =
  try
    let doc = JsonDocument.Parse(ReadOnlyMemory<byte> rawBody)
    let root = doc.RootElement
    let id = jget root "id"
    let typ = jget root "type"
    let obj =
      match root.TryGetProperty "data" with
      | true, d ->
        match d.TryGetProperty "object" with
        | true, o -> o
        | _       -> root
      | _ -> root
    if id = "" || typ = "" then
      doc.Dispose()
      None
    else
      Some { id = id; eventType = typ; object = obj; document = doc }
  with _ -> None
