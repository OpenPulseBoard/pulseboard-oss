module PulseBoard.Oidc

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.IdentityModel.Protocols
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Microsoft.IdentityModel.Tokens
open System.IdentityModel.Tokens.Jwt
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Redirection
open Suave.RequestErrors
open PulseBoard.Tenancy

// OIDC browser flow. The edge acts as a confidential client (or public client
// with PKCE) against an arbitrary OIDC provider:
//   /auth/login    -> redirect to <issuer>/authorize with state + nonce + PKCE
//   /auth/callback -> exchange code, validate id_token, mint a Session.cookie,
//                     302 to the `returnTo` URL captured at login
//   /auth/logout   -> clear cookie, optionally redirect
//   /auth/me       -> return current session as JSON, or 401
//
// Tenant mapping: every successful SSO login lands in the tenant configured
// via `--oidc-tenant=<slug>` (auto-created on first hit so a fresh deployment
// can SSO in without seeding). Role assignment:
//   1. If the (issuer, sub) is already a known user — reuse the stored
//      role (sticky; admin REST is the only way to change it).
//   2. Otherwise, if the user's email matches a `roleOverrides` entry —
//      use that role and persist a new user record.
//   3. Otherwise, fall back to `defaultRole`. If `defaultRole` is `None`,
//      reject the login (403).
// Scopes are derived from the resolved role via `Tenancy.scopesForRole`.

[<NoComparison; NoEquality>]
type Config =
  { issuer       : string
    clientId     : string
    clientSecret : string option       // None for public clients (PKCE only)
    redirectUri  : string
    tenantSlug   : string
    /// Space-separated OAuth scopes. `openid` is always added.
    scopes       : string
    /// httpOnly cookie `Secure` flag. Default: derived from redirectUri scheme.
    cookieSecure : bool
    /// Session lifetime issued after a successful login.
    sessionTtl   : TimeSpan
    /// HS256 signing key for our own session JWTs.
    sessionKey   : byte[]
    /// Role assigned to a brand-new user whose email doesn't match any
    /// override. `None` means new users are rejected (403) — useful for
    /// closed-membership tenants where users must be pre-provisioned.
    defaultRole  : Role option
    /// Case-insensitive email → role overrides. Applied only on first
    /// login; subsequent role changes live in the store (sticky) and must
    /// be managed via the admin REST surface.
    roleOverrides : Map<string, Role> }

let scopesDefault = "openid email profile"

/// Bundle the constants we share across the request handlers.
[<NoComparison; NoEquality>]
type private PendingLogin =
  { nonce        : string
    codeVerifier : string
    returnTo     : string
    createdAt    : DateTimeOffset }

let private pendingTtl = TimeSpan.FromMinutes 10.0

/// In-memory CSRF/PKCE state. Bounded: capped at 1024 in-flight logins, with
/// expiry sweeping done lazily on each new login attempt.
type private StateStore () =
  let map = ConcurrentDictionary<string, PendingLogin>()
  let max = 1024

  let sweep () =
    let cutoff = DateTimeOffset.UtcNow - pendingTtl
    for KeyValue (k, v) in map do
      if v.createdAt < cutoff then map.TryRemove k |> ignore

  member _.Put (state : string, pending : PendingLogin) =
    sweep ()
    if map.Count >= max then
      // Drop oldest to bound memory; not a security issue (states are
      // single-use and per-user anyway).
      let oldest =
        map
        |> Seq.sortBy (fun kv -> kv.Value.createdAt)
        |> Seq.tryHead
      match oldest with
      | Some kv -> map.TryRemove kv.Key |> ignore
      | None -> ()
    map.[state] <- pending

  member _.TakeAndRemove (state : string) =
    match map.TryRemove state with
    | true, p when DateTimeOffset.UtcNow - p.createdAt < pendingTtl -> Some p
    | _ -> None

// -- random helpers ---------------------------------------------------------

let private rng = RandomNumberGenerator.Create()

let private randBytes (n : int) =
  let b = Array.zeroCreate n
  rng.GetBytes b
  b

let private toBase64Url (b : byte[]) =
  Convert.ToBase64String(b)
    .Replace('+', '-')
    .Replace('/', '_')
    .TrimEnd('=')

let private newState ()        = toBase64Url (randBytes 16)
let private newNonce ()        = toBase64Url (randBytes 16)
let private newCodeVerifier () = toBase64Url (randBytes 32)

let private s256 (verifier : string) =
  use sha = SHA256.Create()
  toBase64Url (sha.ComputeHash(Encoding.ASCII.GetBytes verifier))

let private urlEncode (s : string) = Uri.EscapeDataString s

// -- discovery + token endpoints --------------------------------------------

let private newConfigManager (issuer : string) =
  let metadataUrl =
    if issuer.EndsWith "/" then issuer + ".well-known/openid-configuration"
    else issuer + "/.well-known/openid-configuration"
  ConfigurationManager<OpenIdConnectConfiguration>(
    metadataUrl, OpenIdConnectConfigurationRetriever())

let private http = new HttpClient()

[<NoComparison; NoEquality>]
type private TokenResponse =
  { idToken     : string
    accessToken : string option }

let private exchangeCode (cfg : Config)
                         (oidc : OpenIdConnectConfiguration)
                         (code : string)
                         (codeVerifier : string)
    : Async<Result<TokenResponse, string>> = async {
  let form =
    [ KeyValuePair("grant_type", "authorization_code")
      KeyValuePair("code", code)
      KeyValuePair("redirect_uri", cfg.redirectUri)
      KeyValuePair("client_id", cfg.clientId)
      KeyValuePair("code_verifier", codeVerifier) ]
  let form =
    match cfg.clientSecret with
    | Some s -> form @ [ KeyValuePair("client_secret", s) ]
    | None   -> form
  use content = new FormUrlEncodedContent(form)
  use req = new HttpRequestMessage(HttpMethod.Post, oidc.TokenEndpoint)
  req.Content <- content
  // RFC 6749 §2.3.1 — also include HTTP Basic for confidential clients that
  // require it (some IdPs reject the form-encoded secret).
  match cfg.clientSecret with
  | Some s ->
    let raw = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(cfg.clientId + ":" + s))
    req.Headers.Authorization <- AuthenticationHeaderValue("Basic", raw)
  | None -> ()
  try
    let! resp = http.SendAsync req |> Async.AwaitTask
    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    if not resp.IsSuccessStatusCode then
      return Result.Error
        (sprintf "token endpoint %d: %s" (int resp.StatusCode) body)
    else
      use doc = JsonDocument.Parse body
      let root = doc.RootElement
      match root.TryGetProperty "id_token" with
      | true, v when v.ValueKind = JsonValueKind.String ->
        let access =
          match root.TryGetProperty "access_token" with
          | true, a when a.ValueKind = JsonValueKind.String ->
            Some (a.GetString())
          | _ -> None
        return Result.Ok { idToken = v.GetString(); accessToken = access }
      | _ ->
        return Result.Error (sprintf "token response missing id_token: %s" body)
  with ex ->
    return Result.Error (sprintf "token exchange threw: %s" ex.Message)
}

// -- id_token validation ----------------------------------------------------

let private jwtHandler =
  let h = JwtSecurityTokenHandler()
  h.MapInboundClaims <- false
  h

/// Validate the id_token against the IdP's JWKS, issuer, audience, nonce.
/// Returns the validated principal's `sub` and optional `email`.
let private validateIdToken (cfg : Config)
                            (oidc : OpenIdConnectConfiguration)
                            (idToken : string)
                            (expectedNonce : string)
    : Result<string * string option, string> =
  try
    let parms = TokenValidationParameters()
    parms.ValidateIssuer           <- true
    parms.ValidIssuer              <- oidc.Issuer
    parms.ValidateAudience         <- true
    parms.ValidAudience            <- cfg.clientId
    parms.ValidateLifetime         <- true
    parms.ValidateIssuerSigningKey <- true
    parms.IssuerSigningKeys        <- oidc.SigningKeys
    parms.ClockSkew                <- TimeSpan.FromMinutes 2.0
    let principal, _ = jwtHandler.ValidateToken(idToken, parms)
    let claim n =
      principal.Claims
      |> Seq.tryFind (fun c -> c.Type = n)
      |> Option.map (fun c -> c.Value)
    let nonce = claim "nonce"
    if nonce <> Some expectedNonce then
      Result.Error "nonce mismatch"
    else
      match claim "sub" with
      | Some s -> Result.Ok (s, claim "email")
      | None   -> Result.Error "id_token missing sub"
  with ex ->
    Result.Error (sprintf "id_token validation failed: %s" ex.Message)

// -- WebPart handlers -------------------------------------------------------

let private htmlError (status : int) (msg : string) : WebPart =
  let body =
    sprintf
      "<!doctype html><meta charset=utf-8><title>auth error</title><pre>%s</pre>"
      (WebUtility.HtmlEncode msg)
  let writer =
    match status with
    | 400 -> BAD_REQUEST
    | _   -> ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "text/html; charset=utf-8"

/// Build /auth/login: stores PKCE+nonce+state, redirects to authorize.
let private loginHandler (cfg : Config)
                         (cfgMgr : ConfigurationManager<OpenIdConnectConfiguration>)
                         (states : StateStore) : WebPart =
  fun ctx -> async {
    let returnTo =
      match ctx.request.queryParam "returnTo" with
      | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) && v.StartsWith "/" -> v
      | _ -> "/"
    try
      let! oidc =
        cfgMgr.GetConfigurationAsync(CancellationToken.None)
        |> Async.AwaitTask
      let state    = newState ()
      let nonce    = newNonce ()
      let verifier = newCodeVerifier ()
      states.Put(state,
        { nonce = nonce; codeVerifier = verifier
          returnTo = returnTo; createdAt = DateTimeOffset.UtcNow })
      let scopes =
        if cfg.scopes.Contains "openid" then cfg.scopes
        else "openid " + cfg.scopes
      let url =
        sprintf
          "%s?response_type=code&client_id=%s&redirect_uri=%s&scope=%s&state=%s&nonce=%s&code_challenge=%s&code_challenge_method=S256"
          oidc.AuthorizationEndpoint
          (urlEncode cfg.clientId)
          (urlEncode cfg.redirectUri)
          (urlEncode scopes)
          (urlEncode state)
          (urlEncode nonce)
          (urlEncode (s256 verifier))
      return! FOUND url ctx
    with ex ->
      return! htmlError 500 (sprintf "OIDC discovery failed: %s" ex.Message) ctx
  }

let private callbackHandler (cfg : Config)
                            (cfgMgr : ConfigurationManager<OpenIdConnectConfiguration>)
                            (states : StateStore)
                            (store : ITenantStore) : WebPart =
  fun ctx -> async {
    let q n =
      match ctx.request.queryParam n with
      | Choice1Of2 v -> Some v
      | _ -> None
    match q "error" with
    | Some err ->
      let desc = q "error_description" |> Option.defaultValue ""
      return! htmlError 400 (sprintf "IdP error: %s %s" err desc) ctx
    | None ->
      match q "state", q "code" with
      | None, _ | _, None ->
        return! htmlError 400 "missing state or code" ctx
      | Some state, Some code ->
        match states.TakeAndRemove state with
        | None -> return! htmlError 400 "unknown or expired state" ctx
        | Some pending ->
          try
            let! oidc =
              cfgMgr.GetConfigurationAsync(CancellationToken.None)
              |> Async.AwaitTask
            let! tokens =
              exchangeCode cfg oidc code pending.codeVerifier
            match tokens with
            | Result.Error msg ->
              return! htmlError 400 msg ctx
            | Result.Ok t ->
              match validateIdToken cfg oidc t.idToken pending.nonce with
              | Result.Error msg ->
                return! htmlError 400 msg ctx
              | Result.Ok (sub, email) ->
                // Map IdP user to tenant. Auto-create tenant on first use
                // so a fresh deployment can SSO in without seeding.
                let tenant = store.CreateTenant cfg.tenantSlug
                let existing = store.TryGetUser (oidc.Issuer, sub)
                let resolvedRole =
                  match existing with
                  | Some u -> Some u.role          // sticky
                  | None ->
                    let overrideRole =
                      email
                      |> Option.map (fun e -> e.Trim().ToLowerInvariant())
                      |> Option.bind (fun e -> Map.tryFind e cfg.roleOverrides)
                    match overrideRole with
                    | Some r -> Some r
                    | None   -> cfg.defaultRole
                match resolvedRole with
                | None ->
                  return!
                    htmlError 403
                      (sprintf "user %s is not provisioned for tenant '%s'"
                         (email |> Option.defaultValue sub) cfg.tenantSlug)
                      ctx
                | Some role ->
                  let user =
                    store.UpsertUser(tenant.id, oidc.Issuer, sub, email, role)
                  let scopes = scopesForRole user.role
                  let now = DateTimeOffset.UtcNow
                  let claims : Session.SessionClaims =
                    { subject  = sub
                      email    = email
                      tenantId = tenant.id
                      role     = user.role
                      scopes   = scopes
                      issuedAt = now
                      expires  = now + cfg.sessionTtl
                      issuer   = oidc.Issuer }
                  let token = Session.mint cfg.sessionKey claims
                  let setC =
                    Session.setSessionCookie cfg.cookieSecure claims.expires token
                  return!
                    (setC >=> FOUND pending.returnTo) ctx
          with ex ->
            return! htmlError 500 (sprintf "callback failed: %s" ex.Message) ctx
  }

let private logoutHandler (cfg : Config) : WebPart =
  fun ctx -> async {
    let returnTo =
      match ctx.request.queryParam "returnTo" with
      | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) && v.StartsWith "/" -> v
      | _ -> "/"
    return! (Session.clearSessionCookie >=> FOUND returnTo) ctx
  }

let private meHandler : WebPart =
  fun ctx -> async {
    match PulseBoard.Rbac.tryGetTenant ctx with
    | None ->
      return!
        (UNAUTHORIZED """{"error":"not signed in"}"""
         >=> Writers.setMimeType "application/json") ctx
    | Some t ->
      let (TenantId tid) = t.tenant.id
      let (ApiKeyId src) = t.apiKeyId
      let body =
        sprintf
          """{"tenant":%s,"slug":%s,"role":"%s","scopes":%d,"via":%s}"""
          (JsonSerializer.Serialize tid)
          (JsonSerializer.Serialize t.tenant.slug)
          (match t.role with
           | Viewer  -> "viewer" | Editor -> "editor"
           | Admin   -> "admin"  | Billing -> "billing")
          (int t.scopes)
          (JsonSerializer.Serialize src)
      return!
        (OK body >=> Writers.setMimeType "application/json") ctx
  }

/// Build all `/auth/*` WebParts plus a `resolveSession` middleware.
let build (cfg : Config) (store : ITenantStore) =
  let cfgMgr = newConfigManager cfg.issuer
  let states = StateStore()
  let routes : WebPart =
    choose [
      GET >=> path "/auth/login"    >=> loginHandler cfg cfgMgr states
      GET >=> path "/auth/callback" >=> callbackHandler cfg cfgMgr states store
      path "/auth/logout"           >=> logoutHandler cfg
      GET >=> path "/auth/me"       >=> meHandler
    ]
  /// Middleware: if a valid session cookie is present, attach TenantCtx.
  let resolveSession (inner : WebPart) : WebPart =
    fun ctx -> async {
      match Session.tryReadCookie ctx.request with
      | None -> return! inner ctx
      | Some tok ->
        match Session.tryVerify cfg.sessionKey store tok with
        | None   -> return! inner ctx
        | Some t -> return! inner (PulseBoard.Rbac.attachTenant ctx t)
    }
  routes, resolveSession
