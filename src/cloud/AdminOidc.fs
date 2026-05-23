module PulseBoard.AdminOidc

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
open Suave.Cookie

// Phase 10 — admin-portal SSO.
//
// The provisioner's bearer-token gate (`PULSE_ADMIN_TOKENS`) is fine
// for CI scripts and on-call CLI use, but rotating shared secrets is
// painful and the audit trail is "one of N tokens" instead of "Alice
// at 14:02 UTC". For human operators we add OIDC: the portal redirects
// to whatever upstream IdP the organisation uses (Google Workspace,
// Auth0, Authentik, Cognito, etc.), validates the returned id_token
// against the IdP's JWKS, and — if the email is in the allowlist —
// mints an HMAC-signed session cookie scoped to `/admin/*`.
//
// Bearer tokens are unchanged: `adminAuth` checks the bearer first,
// then falls back to the cookie, so existing automation keeps working
// while humans use SSO.
//
// Why a separate module from `src/edge/Oidc.fs`: that one is coupled
// to `ITenantStore` and `Session.fs`'s tenant-scoped JWTs, which the
// provisioner doesn't have. Our session is intentionally minimal —
// just `{email, exp}` HMAC'd — because the provisioner only needs to
// know "an allowed human is on the other end of this request", not
// who/what tenant/what role.

let cookieName = "pulse_admin"

[<NoComparison; NoEquality>]
type Config =
  { /// OIDC issuer base URL (no trailing path; discovery doc must live
    /// at `<issuer>/.well-known/openid-configuration`).
    issuer         : string
    clientId       : string
    /// `None` ⇒ public client (PKCE only). Most IdPs require a secret
    /// for confidential clients; we fall through gracefully if not set.
    clientSecret   : string option
    /// Full `https://admin.pulseboard.cloud/admin/callback` URL the IdP
    /// will 302 to. Must be registered with the IdP verbatim.
    redirectUri    : string
    /// Case-insensitive set of exact emails permitted to sign in.
    allowedEmails  : Set<string>
    /// Case-insensitive set of domains (e.g. `pulseboard.cloud`).
    /// Email is allowed when its domain matches.
    allowedDomains : Set<string>
    /// HMAC-SHA256 key for our own session cookie. Min 32 bytes.
    sessionKey     : byte[]
    /// How long an admin session lasts before re-auth is required.
    sessionTtl     : TimeSpan
    /// Set `Secure` on the cookie. Defaults to `true` when redirectUri
    /// is https://, `false` for http:// (smoke / loopback only).
    cookieSecure   : bool }

// -- base64url helpers ------------------------------------------------------

let private toB64Url (b : byte[]) =
  Convert.ToBase64String(b)
    .Replace('+', '-')
    .Replace('/', '_')
    .TrimEnd('=')

let private fromB64Url (s : string) =
  let normalized = s.Replace('-', '+').Replace('_', '/')
  let padded =
    match normalized.Length % 4 with
    | 0 -> normalized
    | 2 -> normalized + "=="
    | 3 -> normalized + "="
    | _ -> normalized + "==="
  Convert.FromBase64String padded

// -- HMAC session cookie ----------------------------------------------------
//
// Format: <b64url(json)>.<b64url(hmacSha256(key, json))>
// JSON shape: {"email":"a@b.co","exp":1716480000}
//
// No JWT lib needed — it's a single-tenant cookie, not interoperable
// with anyone else. Constant-time signature compare so a malicious
// caller can't binary-search the MAC byte by byte.

let private hmac (key : byte[]) (data : byte[]) : byte[] =
  use h = new HMACSHA256(key)
  h.ComputeHash data

let private ctEq (a : byte[]) (b : byte[]) =
  if a.Length <> b.Length then false
  else
    let mutable d = 0
    for i in 0 .. a.Length - 1 do d <- d ||| (int a.[i] ^^^ int b.[i])
    d = 0

let mintSession (key : byte[]) (email : string) (expires : DateTimeOffset) : string =
  let payload =
    sprintf """{"email":%s,"exp":%d}"""
      (JsonSerializer.Serialize email)
      (expires.ToUnixTimeSeconds())
  let p = Encoding.UTF8.GetBytes payload
  let sigBytes = hmac key p
  toB64Url p + "." + toB64Url sigBytes

let tryVerifySession (key : byte[]) (token : string) : string option =
  if String.IsNullOrWhiteSpace token then None
  else
    try
      let parts = token.Split '.'
      if parts.Length <> 2 then None
      else
        let p = fromB64Url parts.[0]
        let s = fromB64Url parts.[1]
        if not (ctEq s (hmac key p)) then None
        else
          use doc = JsonDocument.Parse p
          let root = doc.RootElement
          let email = root.GetProperty("email").GetString()
          let exp   = root.GetProperty("exp").GetInt64()
          if DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow then None
          else Some email
    with _ -> None

let tryReadCookie (req : HttpRequest) : string option =
  match req.cookies.TryGetValue cookieName with
  | true, c when not (String.IsNullOrWhiteSpace c.value) -> Some c.value
  | _ -> None

/// Return the verified email on the current request, or `None` if the
/// cookie is absent / forged / expired.
let tryReadSession (cfg : Config) (req : HttpRequest) : string option =
  tryReadCookie req |> Option.bind (tryVerifySession cfg.sessionKey)

let isEmailAllowed (cfg : Config) (email : string) =
  let e = email.Trim().ToLowerInvariant()
  if Set.contains e cfg.allowedEmails then true
  else
    let at = e.IndexOf '@'
    if at < 0 then false
    else Set.contains (e.Substring(at + 1)) cfg.allowedDomains

// -- random / PKCE helpers --------------------------------------------------

let private rng = RandomNumberGenerator.Create()
let private randBytes (n : int) = let b = Array.zeroCreate n in rng.GetBytes b; b
let private newState ()        = toB64Url (randBytes 16)
let private newNonce ()        = toB64Url (randBytes 16)
let private newCodeVerifier () = toB64Url (randBytes 32)
let private s256 (verifier : string) =
  use sha = SHA256.Create()
  toB64Url (sha.ComputeHash(Encoding.ASCII.GetBytes verifier))
let private urlEncode (s : string) = Uri.EscapeDataString s

// -- in-flight state (CSRF + PKCE) ------------------------------------------

[<NoComparison; NoEquality>]
type private PendingLogin =
  { nonce        : string
    codeVerifier : string
    returnTo     : string
    createdAt    : DateTimeOffset }

let private pendingTtl = TimeSpan.FromMinutes 10.0

type private StateStore () =
  let map = ConcurrentDictionary<string, PendingLogin>()
  let cap = 1024
  let sweep () =
    let cutoff = DateTimeOffset.UtcNow - pendingTtl
    for KeyValue (k, v) in map do
      if v.createdAt < cutoff then map.TryRemove k |> ignore
  member _.Put (state, pending) =
    sweep ()
    if map.Count >= cap then
      map
      |> Seq.sortBy (fun kv -> kv.Value.createdAt)
      |> Seq.tryHead
      |> Option.iter (fun kv -> map.TryRemove kv.Key |> ignore)
    map.[state] <- pending
  member _.TakeAndRemove (state : string) =
    match map.TryRemove state with
    | true, p when DateTimeOffset.UtcNow - p.createdAt < pendingTtl -> Some p
    | _ -> None

// -- OIDC discovery + token exchange + id_token validation ------------------

let private newCfgMgr (issuer : string) =
  let url =
    if issuer.EndsWith "/" then issuer + ".well-known/openid-configuration"
    else issuer + "/.well-known/openid-configuration"
  ConfigurationManager<OpenIdConnectConfiguration>(
    url, OpenIdConnectConfigurationRetriever())

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)

let private exchangeCode (cfg : Config)
                         (oidc : OpenIdConnectConfiguration)
                         (code : string)
                         (verifier : string)
    : Async<Result<string, string>> = async {
  let form =
    [ KeyValuePair("grant_type",   "authorization_code")
      KeyValuePair("code",         code)
      KeyValuePair("redirect_uri", cfg.redirectUri)
      KeyValuePair("client_id",    cfg.clientId)
      KeyValuePair("code_verifier", verifier) ]
  let form =
    match cfg.clientSecret with
    | Some s -> form @ [ KeyValuePair("client_secret", s) ]
    | None   -> form
  use content = new FormUrlEncodedContent(form)
  use req = new HttpRequestMessage(HttpMethod.Post, oidc.TokenEndpoint)
  req.Content <- content
  // RFC 6749 §2.3.1 — some IdPs require HTTP Basic for confidential
  // clients in addition to the form-encoded secret.
  match cfg.clientSecret with
  | Some s ->
    let raw =
      Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.clientId + ":" + s))
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
      match doc.RootElement.TryGetProperty "id_token" with
      | true, v when v.ValueKind = JsonValueKind.String ->
        return Result.Ok (v.GetString())
      | _ ->
        return Result.Error (sprintf "token response missing id_token: %s" body)
  with ex ->
    return Result.Error (sprintf "token exchange threw: %s" ex.Message)
}

let private jwtHandler =
  let h = JwtSecurityTokenHandler()
  h.MapInboundClaims <- false
  h

let private validateIdToken (cfg : Config)
                            (oidc : OpenIdConnectConfiguration)
                            (idToken : string)
                            (expectedNonce : string)
    : Result<string, string> =
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
    if claim "nonce" <> Some expectedNonce then
      Result.Error "nonce mismatch"
    else
      match claim "email" with
      | None ->
        Result.Error "id_token missing 'email' claim (request scope=email)"
      | Some email ->
        // email_verified is RECOMMENDED in OIDC; if the IdP sends it
        // and it's literally "false", reject. Anything else (absent,
        // "true", or boolean coerced via JWT) we accept.
        let verifiedOk =
          match claim "email_verified" with
          | Some "false" | Some "False" -> false
          | _ -> true
        if not verifiedOk then Result.Error "email_verified=false"
        else Result.Ok email
  with ex ->
    Result.Error (sprintf "id_token validation failed: %s" ex.Message)

// -- WebPart handlers -------------------------------------------------------

let private setSessionCookie (cfg : Config) (token : string) (exp : DateTimeOffset) : WebPart =
  let c = HttpCookie.createKV cookieName token
  let c =
    { c with
        httpOnly = true
        secure   = cfg.cookieSecure
        path     = Some "/"
        expires  = Some exp
        sameSite = Some SameSite.Lax }
  setCookie c

let private clearSessionCookie : WebPart =
  let c = HttpCookie.createKV cookieName ""
  let c =
    { c with
        httpOnly = true
        path     = Some "/"
        expires  = Some (DateTimeOffset.UtcNow.AddDays(-1.0)) }
  setCookie c

let private htmlError (status : int) (msg : string) : WebPart =
  let body =
    sprintf
      "<!doctype html><meta charset=utf-8><title>auth error</title><pre>%s</pre>"
      (WebUtility.HtmlEncode msg)
  let writer =
    match status with
    | 400 -> BAD_REQUEST
    | 403 -> FORBIDDEN
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "text/html; charset=utf-8"

let private loginHandler
    (cfg : Config)
    (mgr : ConfigurationManager<OpenIdConnectConfiguration>)
    (states : StateStore) : WebPart =
  fun ctx -> async {
    let returnTo =
      match ctx.request.queryParam "returnTo" with
      | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) && v.StartsWith "/" -> v
      | _ -> "/admin"
    try
      let! oidc =
        mgr.GetConfigurationAsync(CancellationToken.None) |> Async.AwaitTask
      let state    = newState ()
      let nonce    = newNonce ()
      let verifier = newCodeVerifier ()
      states.Put(state,
        { nonce = nonce; codeVerifier = verifier
          returnTo = returnTo; createdAt = DateTimeOffset.UtcNow })
      let url =
        sprintf
          "%s?response_type=code&client_id=%s&redirect_uri=%s&scope=%s&state=%s&nonce=%s&code_challenge=%s&code_challenge_method=S256"
          oidc.AuthorizationEndpoint
          (urlEncode cfg.clientId)
          (urlEncode cfg.redirectUri)
          (urlEncode "openid email profile")
          (urlEncode state)
          (urlEncode nonce)
          (urlEncode (s256 verifier))
      return! FOUND url ctx
    with ex ->
      return! htmlError 500 (sprintf "OIDC discovery failed: %s" ex.Message) ctx
  }

let private callbackHandler
    (cfg : Config)
    (mgr : ConfigurationManager<OpenIdConnectConfiguration>)
    (states : StateStore) : WebPart =
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
              mgr.GetConfigurationAsync(CancellationToken.None)
              |> Async.AwaitTask
            let! tokens = exchangeCode cfg oidc code pending.codeVerifier
            match tokens with
            | Result.Error msg -> return! htmlError 400 msg ctx
            | Result.Ok idToken ->
              match validateIdToken cfg oidc idToken pending.nonce with
              | Result.Error msg -> return! htmlError 400 msg ctx
              | Result.Ok email ->
                if not (isEmailAllowed cfg email) then
                  return!
                    htmlError 403
                      (sprintf "%s is not authorized for this admin portal" email)
                      ctx
                else
                  let exp = DateTimeOffset.UtcNow + cfg.sessionTtl
                  let token =
                    mintSession cfg.sessionKey (email.ToLowerInvariant()) exp
                  return!
                    (setSessionCookie cfg token exp >=> FOUND pending.returnTo) ctx
          with ex ->
            return! htmlError 500 (sprintf "callback failed: %s" ex.Message) ctx
  }

let private logoutHandler : WebPart =
  fun ctx -> async {
    let returnTo =
      match ctx.request.queryParam "returnTo" with
      | Choice1Of2 v when not (String.IsNullOrWhiteSpace v) && v.StartsWith "/" -> v
      | _ -> "/admin"
    return! (clearSessionCookie >=> FOUND returnTo) ctx
  }

let private whoamiHandler (cfg : Config) : WebPart =
  fun ctx -> async {
    match tryReadSession cfg ctx.request with
    | Some email ->
      let body = sprintf """{"email":%s}""" (JsonSerializer.Serialize email)
      return! (OK body >=> Writers.setMimeType "application/json") ctx
    | None ->
      return!
        (UNAUTHORIZED """{"error":"not signed in"}"""
         >=> Writers.setMimeType "application/json") ctx
  }

/// All `/admin/login`, `/admin/callback`, `/admin/logout`, `/admin/whoami`
/// routes. Mount this BEFORE the bearer-gated `/admin/workspaces` routes
/// in the provisioner's `choose` so the auth flow short-circuits.
let routes (cfg : Config) : WebPart =
  let mgr = newCfgMgr cfg.issuer
  let states = StateStore()
  choose [
    GET >=> path "/admin/login"    >=> loginHandler cfg mgr states
    GET >=> path "/admin/callback" >=> callbackHandler cfg mgr states
    path "/admin/logout"           >=> logoutHandler
    GET >=> path "/admin/whoami"   >=> whoamiHandler cfg
  ]
