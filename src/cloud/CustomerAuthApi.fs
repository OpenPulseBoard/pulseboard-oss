module PulseBoard.CustomerAuthApi

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.IdentityModel.Tokens.Jwt
open Microsoft.IdentityModel.Tokens
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open Suave.Cookie
open PulseBoard.CustomerAuth
open PulseBoard.EmailSender
open PulseBoard.GithubOAuth

// Phase 10 step 2 — email/password customer signup, signin, email
// verification, password reset, and refresh-token rotation.
//
// Two cookies are issued post-signin, both `HttpOnly; Secure; SameSite=Lax`:
//
//   pb_access  — short-lived (1 h) HS256 JWT. Carried on every portal
//                request. Stateless verification on the apex.
//   pb_refresh — long-lived (30 d) opaque token. ONLY sent to
//                `/api/auth/refresh` (path-restricted cookie). Server-side
//                row in `pb_customer_sessions` so we can revoke.
//
// The JWT audience (`pulseboard-portal`) is intentionally different from
// the workspace OIDC session JWT (`pulseboard-edge`, from `Session.fs`)
// so a stolen apex cookie can't be presented at a workspace and vice
// versa.
//
// Email enumeration: signup and forgot-password endpoints always return
// the same 202 response regardless of whether the email exists, so a
// drive-by attacker can't probe the customer table.

// -- constants --------------------------------------------------------------

let accessCookieName  = "pb_access"
let refreshCookieName = "pb_refresh"
let refreshCookiePath = "/api/auth"

let jwtIssuer   = "pulseboard-apex"
let jwtAudience = "pulseboard-portal"

let accessLifetime  = TimeSpan.FromHours   1.0
let refreshLifetime = TimeSpan.FromDays   30.0

let verifyLifetime  = TimeSpan.FromHours  48.0
let resetLifetime   = TimeSpan.FromHours   1.0

// -- key handling -----------------------------------------------------------
//
// Customer-session signing secret. Separate from `Session.fs`'s
// `--session-secret`. Generated fresh on each boot if not supplied —
// fine for dev, fatal for prod (sessions don't survive a restart), so
// the binary logs a warning when running with an ephemeral key.

let generateKey () : byte[] =
  let b = Array.zeroCreate 32
  RandomNumberGenerator.Fill(Span b)
  b

let keyFromBase64 (raw : string) : byte[] =
  let normalized = raw.Trim().Replace('-', '+').Replace('_', '/')
  let padded =
    match normalized.Length % 4 with
    | 0 -> normalized
    | 2 -> normalized + "=="
    | 3 -> normalized + "="
    | _ -> normalized + "==="
  let bytes = Convert.FromBase64String padded
  if bytes.Length < 32 then
    invalidArg "raw" "customer session secret must be at least 32 bytes"
  bytes

let keyToBase64 (key : byte[]) = Convert.ToBase64String key

let private handler =
  let h = JwtSecurityTokenHandler()
  h.MapInboundClaims <- false
  h

// -- JWT helpers ------------------------------------------------------------

[<NoComparison; NoEquality>]
type AccessClaims =
  { customerId : CustomerId
    email      : string
    issuedAt   : DateTimeOffset
    expires    : DateTimeOffset }

let mintAccessJwt (key : byte[]) (claims : AccessClaims) : string =
  let creds =
    SigningCredentials(SymmetricSecurityKey key, SecurityAlgorithms.HmacSha256)
  let (CustomerId cid) = claims.customerId
  let d = SecurityTokenDescriptor()
  d.Issuer    <- jwtIssuer
  d.Audience  <- jwtAudience
  d.IssuedAt  <- Nullable claims.issuedAt.UtcDateTime
  d.NotBefore <- Nullable claims.issuedAt.UtcDateTime
  d.Expires   <- Nullable claims.expires.UtcDateTime
  d.SigningCredentials <- creds
  let cd = Dictionary<string, obj>()
  cd.["sub"]   <- box cid
  cd.["email"] <- box claims.email
  d.Claims <- cd
  let token = handler.CreateToken d
  handler.WriteToken token

let tryVerifyAccessJwt (key : byte[]) (token : string) : AccessClaims option =
  if String.IsNullOrWhiteSpace token then None
  else
    try
      let parms = TokenValidationParameters()
      parms.ValidateIssuer           <- true
      parms.ValidIssuer              <- jwtIssuer
      parms.ValidateAudience         <- true
      parms.ValidAudience            <- jwtAudience
      parms.ValidateLifetime         <- true
      parms.ValidateIssuerSigningKey <- true
      parms.IssuerSigningKey         <- SymmetricSecurityKey key
      parms.ClockSkew                <- TimeSpan.FromSeconds 30.0
      let principal, validated = handler.ValidateToken(token, parms)
      let claim n =
        principal.Claims
        |> Seq.tryFind (fun c -> c.Type = n)
        |> Option.map (fun c -> c.Value)
      match claim "sub", claim "email" with
      | Some cid, Some email ->
        Some
          { customerId = CustomerId cid
            email      = email
            issuedAt   = DateTimeOffset(validated.ValidFrom,  TimeSpan.Zero)
            expires    = DateTimeOffset(validated.ValidTo,    TimeSpan.Zero) }
      | _ -> None
    with _ -> None

// -- cookie helpers ---------------------------------------------------------

let private setCookieAttrs (secure : bool) (path : string)
                           (expires : DateTimeOffset) (name : string)
                           (value : string) : WebPart =
  let c = HttpCookie.createKV name value
  let c = { c with
              httpOnly = true
              secure   = secure
              path     = Some path
              expires  = Some expires
              sameSite = Some SameSite.Lax }
  setCookie c

let private clearCookie (path : string) (name : string) : WebPart =
  let c = HttpCookie.createKV name ""
  let c = { c with
              httpOnly = true
              path     = Some path
              expires  = Some (DateTimeOffset.UtcNow.AddDays -1.0) }
  setCookie c

let tryReadCookie (req : HttpRequest) (name : string) : string option =
  match req.cookies.TryGetValue name with
  | true, c when not (String.IsNullOrWhiteSpace c.value) -> Some c.value
  | _ -> None

// -- request helpers --------------------------------------------------------

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  match status with
  | 204 -> Suave.Successful.NO_CONTENT
  | _ ->
    let writer =
      match status with
      | 200 -> OK
      | 201 -> Suave.Successful.CREATED
      | 202 -> Suave.Successful.ACCEPTED
      | 400 -> BAD_REQUEST
      // See PortalApi.jsonResp: Suave's UNAUTHORIZED writer sets a
      // `WWW-Authenticate: Basic` header that triggers the browser's
      // native basic-auth dialog. We use cookies, so emit a bare 401.
      | 401 -> fun b -> OK b >=> Writers.setStatus HTTP_401
      | 403 -> FORBIDDEN
      | 404 -> NOT_FOUND
      | 409 -> Suave.RequestErrors.CONFLICT
      | 410 -> Suave.RequestErrors.GONE
      | 429 -> Suave.RequestErrors.TOO_MANY_REQUESTS
      | _   -> INTERNAL_ERROR
    writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private tryParseJson (body : string) : JsonDocument option =
  if String.IsNullOrWhiteSpace body then None
  else try Some (JsonDocument.Parse body) with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

let private clientIp (ctx : HttpContext) : string =
  let fwd =
    ctx.request.headers
    |> Seq.tryFind (fun (k, _) ->
         String.Equals(k, "x-forwarded-for", StringComparison.OrdinalIgnoreCase))
    |> Option.map (snd >> fun v -> v.Trim())
  match fwd with
  | Some v when not (String.IsNullOrWhiteSpace v) -> v.Split(',').[0].Trim()
  | _ ->
    try ctx.clientIpTrustProxy.ToString() with _ -> "unknown"

let private userAgent (ctx : HttpContext) : string option =
  ctx.request.headers
  |> Seq.tryFind (fun (k, _) ->
       String.Equals(k, "user-agent", StringComparison.OrdinalIgnoreCase))
  |> Option.map snd

// -- validation -------------------------------------------------------------

/// Cheap email shape check: one '@', a non-empty local part, and a dot
/// somewhere in the domain. Not RFC-5322 — we rely on the verification
/// email to do the real proof-of-control.
let isPlausibleEmail (raw : string) : bool =
  if isNull raw then false
  else
    let s = raw.Trim()
    if s.Length < 3 || s.Length > 254 then false
    else
      let at = s.IndexOf '@'
      if at <= 0 || at = s.Length - 1 then false
      else s.Substring(at + 1).Contains '.'

/// Password policy: 12-128 chars. Length is the dominant factor — we
/// don't impose composition rules (NIST SP 800-63B §5.1.1.2).
let private passwordError (pw : string) : string option =
  if isNull pw then Some "password is required"
  elif pw.Length < 12 then Some "password must be at least 12 characters"
  elif pw.Length > 128 then Some "password must be at most 128 characters"
  else None

// -- per-IP rate limiter ----------------------------------------------------
//
// Used by signup + signin + forgot to slow down credential-stuffing and
// enumeration probes. 30 attempts / hour / IP — generous enough that a
// legitimate user fumbling their password won't trip it.

[<NoComparison; NoEquality>]
type private Bucket = { mutable count : int; mutable resetAt : DateTimeOffset }

type AuthRateLimiter (maxPerWindow : int, windowSec : int) =
  let buckets = System.Collections.Concurrent.ConcurrentDictionary<string, Bucket>()
  let win () = TimeSpan.FromSeconds(float windowSec)
  member _.TryConsume (ip : string) : Result<unit, DateTimeOffset> =
    let now = DateTimeOffset.UtcNow
    let b = buckets.GetOrAdd(ip, fun _ -> { count = 0; resetAt = now + win () })
    lock b (fun () ->
      if now >= b.resetAt then
        b.count   <- 0
        b.resetAt <- now + win ()
      if b.count >= maxPerWindow then Result.Error b.resetAt
      else
        b.count <- b.count + 1
        Result.Ok ())

// -- configuration record ---------------------------------------------------

[<NoComparison; NoEquality>]
type CustomerAuthConfig =
  { store        : ICustomerStore
    sender       : IEmailSender
    /// HS256 signing key for the access JWT. >= 32 bytes.
    signingKey   : byte[]
    /// Public-facing base URL used to assemble email links
    /// (e.g. "https://pulseboard.cloud").
    publicBase   : string
    /// `From:` header for outgoing auth emails.
    fromAddress  : string
    /// Cookies marked `Secure`? Set `true` in prod, `false` for
    /// loopback dev where the browser would otherwise drop them.
    secureCookies: bool
    rateLimiter  : AuthRateLimiter
    /// GitHub OAuth config. `None` disables the `/api/auth/github/*`
    /// endpoints; the `signin.html` button surfaces a clear 404 in
    /// that case.
    github       : GithubConfig option
    githubStates : StateCache }

let defaultConfig (store : ICustomerStore) (sender : IEmailSender)
                  (publicBase : string) (fromAddress : string)
                  (secureCookies : bool) : CustomerAuthConfig =
  { store         = store
    sender        = sender
    signingKey    = generateKey ()
    publicBase    = publicBase.TrimEnd '/'
    fromAddress   = fromAddress
    secureCookies = secureCookies
    rateLimiter   = AuthRateLimiter(30, 3600)
    github        = None
    githubStates  = StateCache() }

// -- email templates --------------------------------------------------------

let private verifyEmail (cfg : CustomerAuthConfig) (toAddr : string) (token : string) : EmailMessage =
  let link = sprintf "%s/auth/verify?token=%s" cfg.publicBase (Uri.EscapeDataString token)
  { fromAddress = cfg.fromAddress
    toAddress   = toAddr
    subject     = "Verify your PulseBoard email"
    body        =
      sprintf
        "Welcome to PulseBoard.\n\n\
         Click the link below to verify your email address. The link\n\
         expires in 48 hours.\n\n\
         %s\n\n\
         If you didn't sign up, you can ignore this message."
        link }

let private resetEmail (cfg : CustomerAuthConfig) (toAddr : string) (token : string) : EmailMessage =
  let link = sprintf "%s/auth/reset?token=%s" cfg.publicBase (Uri.EscapeDataString token)
  { fromAddress = cfg.fromAddress
    toAddress   = toAddr
    subject     = "Reset your PulseBoard password"
    body        =
      sprintf
        "Someone (hopefully you) asked to reset the password on your\n\
         PulseBoard account. Click the link below within the next hour\n\
         to choose a new password:\n\n\
         %s\n\n\
         If you didn't request this, ignore the email — the link will\n\
         expire on its own."
        link }

// -- session helpers --------------------------------------------------------

/// Mint a fresh access JWT + refresh token, persist the refresh-token
/// session row, and return both cookies as a WebPart that sets them.
let issueSession (cfg : CustomerAuthConfig) (ctx : HttpContext)
                 (customer : Customer) : WebPart * string =
  let now      = DateTimeOffset.UtcNow
  let accessExp  = now + accessLifetime
  let refreshExp = now + refreshLifetime
  let access =
    mintAccessJwt cfg.signingKey
      { customerId = customer.id
        email      = customer.email
        issuedAt   = now
        expires    = accessExp }
  let refreshPlain, refreshHash = mintToken 32
  let sessionId = Guid.NewGuid().ToString "N"
  let row : CustomerSession =
    { id               = sessionId
      customerId       = customer.id
      refreshTokenHash = refreshHash
      createdAt        = now
      expiresAt        = refreshExp
      revokedAt        = None
      userAgent        = userAgent ctx
      ip               = Some (clientIp ctx) }
  cfg.store.InsertSession row
  // The refresh cookie value is `<sessionId>.<plaintext>` so the
  // refresh endpoint can both look up the row AND verify the
  // plaintext hashes to the stored digest. Two-part token format
  // mirrors the workspace API key (`pk_<id>.<secret>`).
  let refreshCookieValue = sprintf "%s.%s" sessionId refreshPlain
  let setCookies =
    setCookieAttrs cfg.secureCookies "/" accessExp accessCookieName access
    >=> setCookieAttrs cfg.secureCookies refreshCookiePath refreshExp
                       refreshCookieName refreshCookieValue
  setCookies, access

// -- WebPart handlers -------------------------------------------------------

let private signup (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    let ip = clientIp ctx
    match cfg.rateLimiter.TryConsume ip with
    | Result.Error _ ->
      return! errJson 429 "too many signup attempts; try again later" ctx
    | Result.Ok () ->
      match tryParseJson (readBody ctx.request) with
      | None -> return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        let email = tryGetString root "email" |> Option.map canonEmail
        let pw    = tryGetString root "password"
        match email, pw with
        | None, _ -> return! errJson 400 "field 'email' is required" ctx
        | _, None -> return! errJson 400 "field 'password' is required" ctx
        | Some e, Some p ->
          if not (isPlausibleEmail e) then
            return! errJson 400 "email looks malformed" ctx
          else
            match passwordError p with
            | Some msg -> return! errJson 400 msg ctx
            | None ->
              // Always respond 202 regardless of whether the email
              // already exists — prevents account enumeration.
              match cfg.store.TryGetByEmail e with
              | Some _ ->
                // Best-effort: send a "you already have an account"
                // reminder so honest users aren't stuck wondering.
                let reminder : EmailMessage =
                  { fromAddress = cfg.fromAddress
                    toAddress   = e
                    subject     = "PulseBoard signup"
                    body        =
                      sprintf
                        "Someone tried to sign up at PulseBoard with\n\
                         this email address. An account already exists\n\
                         for %s — try signing in instead, or use the\n\
                         password-reset link at %s/forgot if you've\n\
                         forgotten the password."
                        e cfg.publicBase }
                fireAndForget cfg.sender reminder
                return!
                  jsonResp 202 """{"status":"check your email"}""" ctx
              | None ->
                let hash = hashPassword p
                let now  = DateTimeOffset.UtcNow
                let cid  = newCustomerId ()
                let customer : Customer =
                  { id              = cid
                    email           = e
                    emailVerifiedAt = None
                    passwordHash    = Some hash.hash
                    passwordSalt    = Some hash.salt
                    passwordAlgo    = Some hash.algo
                    githubUserId    = None
                    createdAt       = now
                    updatedAt       = now }
                let mutable inserted = false
                try
                  cfg.store.Insert customer
                  inserted <- true
                with ex ->
                  eprintfn "  [auth] insert customer failed: %s" ex.Message
                if not inserted then
                  return! errJson 500 "internal error" ctx
                else
                  let tokenPlain, tokenHash = mintToken 32
                  let tok : EmailToken =
                    { tokenHash  = tokenHash
                      customerId = cid
                      purpose    = Verify
                      createdAt  = now
                      expiresAt  = now + verifyLifetime
                      consumedAt = None }
                  try cfg.store.InsertEmailToken tok
                  with ex ->
                    eprintfn "  [auth] insert email token failed: %s" ex.Message
                  fireAndForget cfg.sender (verifyEmail cfg e tokenPlain)
                  return! jsonResp 202 """{"status":"check your email"}""" ctx
  }

let private signin (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    let ip = clientIp ctx
    match cfg.rateLimiter.TryConsume ip with
    | Result.Error _ ->
      eprintfn "  [auth] signin rate-limited from ip=%s" ip
      return! errJson 429 "too many signin attempts; try again later" ctx
    | Result.Ok () ->
      match tryParseJson (readBody ctx.request) with
      | None -> return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        let email = tryGetString root "email" |> Option.map canonEmail
        let pw    = tryGetString root "password"
        match email, pw with
        | Some e, Some p ->
          match cfg.store.TryGetByEmail e with
          | None ->
            // Constant-time dummy hash to defeat timing oracles.
            verifyDummy p
            eprintfn "  [auth] signin failed email=%s ip=%s reason=no_customer" e ip
            return! errJson 401 "invalid email or password" ctx
          | Some c ->
            let ok =
              match c.passwordHash, c.passwordSalt, c.passwordAlgo with
              | Some h, Some s, Some a -> verifyPassword p s a h
              | _ ->
                // GitHub-only account; treat as no-password.
                verifyDummy p
                false
            if not ok then
              eprintfn "  [auth] signin failed email=%s ip=%s reason=bad_password_or_no_password" e ip
              return! errJson 401 "invalid email or password" ctx
            elif c.emailVerifiedAt.IsNone then
              eprintfn "  [auth] signin blocked email=%s ip=%s reason=email_unverified" e ip
              return! errJson 403 "email not verified yet" ctx
            else
              let setCookies, _ = issueSession cfg ctx c
              let (CustomerId cid) = c.id
              eprintfn "  [auth] signin ok customerId=%s email=%s ip=%s" cid c.email ip
              let body =
                sprintf
                  """{"customerId":%s,"email":%s,"emailVerified":true}"""
                  (JsonSerializer.Serialize cid)
                  (JsonSerializer.Serialize c.email)
              return! (jsonResp 200 body >=> setCookies) ctx
        | _ ->
          return! errJson 400 "email and password are required" ctx
  }

/// GET /auth/verify?token=<plaintext>
let private verify (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    let tokenParam =
      ctx.request.queryParam "token"
      |> function Choice1Of2 v -> Some v | _ -> None
    match tokenParam with
    | None -> return! errJson 400 "missing token" ctx
    | Some plaintext ->
      let digest = hashPresentedToken plaintext
      let now = DateTimeOffset.UtcNow
      match cfg.store.TryGetEmailToken digest with
      | Some t when t.purpose = Verify ->
        if cfg.store.ConsumeEmailToken(digest, now) then
          // Stamp email_verified_at on the customer row.
          cfg.store.Update t.customerId (fun c ->
            { c with emailVerifiedAt = Some now; updatedAt = now })
          |> ignore
          // Redirect to the signin page with a banner flag.
          let target = cfg.publicBase + "/signin?verified=1"
          return! Redirection.FOUND target ctx
        else
          return! errJson 410 "token expired or already used" ctx
      | _ ->
        return! errJson 404 "token not found" ctx
  }

let private forgot (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    let ip = clientIp ctx
    match cfg.rateLimiter.TryConsume ip with
    | Result.Error _ ->
      eprintfn "  [auth] forgot rate-limited from ip=%s" ip
      return! errJson 429 "too many reset requests; try again later" ctx
    | Result.Ok () ->
      match tryParseJson (readBody ctx.request) with
      | None -> return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let email = tryGetString doc.RootElement "email" |> Option.map canonEmail
        match email with
        | None -> return! errJson 400 "field 'email' is required" ctx
        | Some e ->
          match cfg.store.TryGetByEmail e with
          | Some c when c.passwordHash.IsSome ->
            let now = DateTimeOffset.UtcNow
            let tokenPlain, tokenHash = mintToken 32
            let tok : EmailToken =
              { tokenHash  = tokenHash
                customerId = c.id
                purpose    = Reset
                createdAt  = now
                expiresAt  = now + resetLifetime
                consumedAt = None }
            try cfg.store.InsertEmailToken tok
            with ex -> eprintfn "  [auth] insert reset token failed: %s" ex.Message
            let (CustomerId cid) = c.id
            eprintfn "  [auth] forgot token created customerId=%s email=%s ip=%s" cid e ip
            fireAndForget cfg.sender (resetEmail cfg e tokenPlain)
          | _ ->
            eprintfn "  [auth] forgot no eligible password account email=%s ip=%s" e ip
          // Always 202 — same response regardless of existence.
          return! jsonResp 202 """{"status":"check your email"}""" ctx
  }

let private reset (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match tryParseJson (readBody ctx.request) with
    | None -> return! errJson 400 "invalid JSON body" ctx
    | Some doc ->
      use _ = doc
      let root = doc.RootElement
      let token = tryGetString root "token"
      let pw    = tryGetString root "password"
      match token, pw with
      | None, _ -> return! errJson 400 "field 'token' is required" ctx
      | _, None -> return! errJson 400 "field 'password' is required" ctx
      | Some t, Some p ->
        match passwordError p with
        | Some msg -> return! errJson 400 msg ctx
        | None ->
          let digest = hashPresentedToken t
          let now = DateTimeOffset.UtcNow
          match cfg.store.TryGetEmailToken digest with
          | Some row when row.purpose = Reset ->
            if cfg.store.ConsumeEmailToken(digest, now) then
              let hash = hashPassword p
              cfg.store.Update row.customerId (fun c ->
                { c with
                    passwordHash = Some hash.hash
                    passwordSalt = Some hash.salt
                    passwordAlgo = Some hash.algo
                    // Mark email as verified on the assumption that the
                    // reset link landed in the inbox of the address on
                    // file — same proof-of-control as the verify flow.
                    emailVerifiedAt =
                      match c.emailVerifiedAt with
                      | Some _ as v -> v
                      | None -> Some now
                    updatedAt = now })
              |> ignore
              // Revoke every active session for this customer — anyone
              // who knew the old password is signed out.
              for s in cfg.store.ListSessions row.customerId do
                if s.revokedAt.IsNone then
                  cfg.store.RevokeSession(s.id, now)
              return! jsonResp 200 """{"status":"password updated"}""" ctx
            else
              return! errJson 410 "token expired or already used" ctx
          | _ -> return! errJson 404 "token not found" ctx
  }

let private signout (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match tryReadCookie ctx.request refreshCookieName with
    | Some raw ->
      let dot = raw.IndexOf '.'
      if dot > 0 then
        let sid = raw.Substring(0, dot)
        cfg.store.RevokeSession(sid, DateTimeOffset.UtcNow)
    | None -> ()
    let clear =
      clearCookie "/" accessCookieName
      >=> clearCookie refreshCookiePath refreshCookieName
    return! (jsonResp 204 "" >=> clear) ctx
  }

let private refresh (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match tryReadCookie ctx.request refreshCookieName with
    | None -> return! errJson 401 "no refresh cookie" ctx
    | Some raw ->
      let dot = raw.IndexOf '.'
      if dot <= 0 || dot = raw.Length - 1 then
        return! errJson 400 "malformed refresh cookie" ctx
      else
        let sid = raw.Substring(0, dot)
        let plain = raw.Substring(dot + 1)
        let now = DateTimeOffset.UtcNow
        match cfg.store.TryGetSession sid with
        | None -> return! errJson 401 "no such session" ctx
        | Some s ->
          if s.revokedAt.IsSome || s.expiresAt <= now then
            return! errJson 401 "session expired or revoked" ctx
          else
            let presented = hashPresentedToken plain
            if not (CryptographicOperations.FixedTimeEquals(
                     ReadOnlySpan presented, ReadOnlySpan s.refreshTokenHash)) then
              return! errJson 401 "bad refresh token" ctx
            else
              match cfg.store.TryGetById s.customerId with
              | None -> return! errJson 401 "customer gone" ctx
              | Some c ->
                // Rotate: revoke the presented session, mint a fresh one.
                cfg.store.RevokeSession(s.id, now)
                let setCookies, _ = issueSession cfg ctx c
                let (CustomerId cid) = c.id
                let body =
                  sprintf """{"customerId":%s,"email":%s}"""
                    (JsonSerializer.Serialize cid)
                    (JsonSerializer.Serialize c.email)
                return! (jsonResp 200 body >=> setCookies) ctx
  }

let private me (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match tryReadCookie ctx.request accessCookieName with
    | None -> return! errJson 401 "not signed in" ctx
    | Some token ->
      match tryVerifyAccessJwt cfg.signingKey token with
      | None -> return! errJson 401 "invalid or expired session" ctx
      | Some claims ->
        match cfg.store.TryGetById claims.customerId with
        | None -> return! errJson 401 "customer gone" ctx
        | Some c ->
          let (CustomerId cid) = c.id
          let body =
            sprintf
              """{"customerId":%s,"email":%s,"emailVerified":%b,"hasGithub":%b,"hasPassword":%b}"""
              (JsonSerializer.Serialize cid)
              (JsonSerializer.Serialize c.email)
              c.emailVerifiedAt.IsSome
              c.githubUserId.IsSome
              c.passwordHash.IsSome
          return! jsonResp 200 body ctx
  }

// -- GitHub OAuth -----------------------------------------------------------

let private ghStateCookieName = "pb_gh_state"
let private ghStateCookiePath = "/api/auth/github"

/// Set the CSRF state cookie (10-min lifetime).
let private setGhStateCookie (cfg : CustomerAuthConfig) (state : string) : WebPart =
  let exp = DateTimeOffset.UtcNow + PulseBoard.GithubOAuth.pendingLifetime
  setCookieAttrs cfg.secureCookies ghStateCookiePath exp ghStateCookieName state

let private clearGhStateCookie : WebPart =
  clearCookie ghStateCookiePath ghStateCookieName

/// `GET /api/auth/github/start`
///
/// Optional `?returnTo=/relative/path` lets the caller pick where the
/// browser lands post-callback. We only honour same-origin paths
/// (starting with `/`) to prevent open-redirect abuse. If the user is
/// already signed in (carries `pb_access`), the callback will *link*
/// the GH account to that customer instead of creating a new one.
let private githubStart (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match cfg.github with
    | None -> return! errJson 404 "GitHub sign-in is not configured" ctx
    | Some gh ->
      let returnTo =
        ctx.request.queryParam "returnTo"
        |> function
           | Choice1Of2 v when v.StartsWith "/" && not (v.StartsWith "//") -> v
           | _ -> "/portal"
      let linkTo =
        match tryReadCookie ctx.request accessCookieName with
        | None -> None
        | Some token ->
          match tryVerifyAccessJwt cfg.signingKey token with
          | None -> None
          | Some claims -> Some claims.customerId
      let state = PulseBoard.GithubOAuth.generateState ()
      let pending : PulseBoard.GithubOAuth.PendingState =
        { state            = state
          createdAt        = DateTimeOffset.UtcNow
          returnTo         = returnTo
          linkToCustomerId = linkTo }
      cfg.githubStates.Insert pending
      let url = PulseBoard.GithubOAuth.buildAuthorizeUrl gh state
      return! (Redirection.FOUND url >=> setGhStateCookie cfg state) ctx
  }

/// `GET /api/auth/github/callback?code=...&state=...`
let private githubCallback (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    match cfg.github with
    | None -> return! errJson 404 "GitHub sign-in is not configured" ctx
    | Some gh ->
      let qCode  = ctx.request.queryParam "code"
      let qState = ctx.request.queryParam "state"
      let qErr   = ctx.request.queryParam "error"
      // The user clicked "Cancel" on GitHub's consent screen.
      match qErr with
      | Choice1Of2 e ->
        return! Redirection.FOUND (sprintf "/signin?error=%s"
                  (Uri.EscapeDataString ("github: " + e))) ctx
      | _ ->
      match qCode, qState with
      | Choice1Of2 code, Choice1Of2 state ->
        // Defense in depth: state must be present in BOTH the URL and
        // the cookie. Defeats CSRF where an attacker tricks the user
        // into hitting /callback?state=<attacker's>.
        let cookieState = tryReadCookie ctx.request ghStateCookieName
        if cookieState <> Some state then
          return! (errJson 400 "state mismatch" >=> clearGhStateCookie) ctx
        else
          match cfg.githubStates.TryConsume state with
          | None ->
            return! (errJson 400 "state expired or already used"
                     >=> clearGhStateCookie) ctx
          | Some pending ->
            // Exchange code for token, then load the user.
            let! tokR = PulseBoard.GithubOAuth.exchangeCode gh code
            match tokR with
            | Result.Error e ->
              eprintfn "  [auth] github token exchange failed: %A" e
              return! (errJson 502 "github token exchange failed"
                       >=> clearGhStateCookie) ctx
            | Result.Ok token ->
              let! userR = PulseBoard.GithubOAuth.fetchUser token
              match userR with
              | Result.Error e ->
                eprintfn "  [auth] github /user failed: %A" e
                return! (errJson 502 "github /user fetch failed"
                         >=> clearGhStateCookie) ctx
              | Result.Ok ghUser ->
                let now = DateTimeOffset.UtcNow
                // -- branch 1: linking an existing customer --
                match pending.linkToCustomerId with
                | Some cid ->
                  // Refuse if some other customer already owns this GH id.
                  match cfg.store.TryGetByGithub ghUser.id with
                  | Some owner when owner.id <> cid ->
                    return! (errJson 409 "this GitHub account is already linked to a different PulseBoard account"
                             >=> clearGhStateCookie) ctx
                  | _ ->
                    cfg.store.Update cid (fun c ->
                      { c with
                          githubUserId = Some ghUser.id
                          updatedAt    = now })
                    |> ignore
                    return! (Redirection.FOUND
                              (pending.returnTo + "?linked=github")
                             >=> clearGhStateCookie) ctx
                | None ->
                  // -- branch 2: sign-in via existing GH link --
                  match cfg.store.TryGetByGithub ghUser.id with
                  | Some c ->
                    let setCookies, _ = issueSession cfg ctx c
                    return! (Redirection.FOUND pending.returnTo
                             >=> setCookies
                             >=> clearGhStateCookie) ctx
                  | None ->
                    // -- branch 3: maybe attach to existing email
                    //              customer, otherwise create new --
                    let email = ghUser.email |> Option.map canonEmail
                    let existingByEmail =
                      email |> Option.bind cfg.store.TryGetByEmail
                    match existingByEmail with
                    | Some c ->
                      // A customer with this email exists but has no GH
                      // link. We auto-link only if their email is already
                      // verified (proof they own that inbox) AND GitHub
                      // also considers it verified. Otherwise we refuse
                      // to merge: the user should sign in with their
                      // password and click "Link GitHub" from /portal.
                      if c.emailVerifiedAt.IsSome then
                        cfg.store.Update c.id (fun cur ->
                          { cur with
                              githubUserId = Some ghUser.id
                              updatedAt    = now })
                        |> ignore
                        match cfg.store.TryGetById c.id with
                        | Some refreshed ->
                          let setCookies, _ = issueSession cfg ctx refreshed
                          return! (Redirection.FOUND
                                    (pending.returnTo + "?linked=github")
                                   >=> setCookies
                                   >=> clearGhStateCookie) ctx
                        | None ->
                          return! (errJson 500 "customer vanished"
                                   >=> clearGhStateCookie) ctx
                      else
                        return! (Redirection.FOUND
                                  "/signin?error=existing_account_unverified"
                                 >=> clearGhStateCookie) ctx
                    | None ->
                      // Brand-new customer. GitHub gave us a verified
                      // primary email, so we skip the verify-email
                      // step entirely.
                      let primaryEmail =
                        email |> Option.defaultWith (fun () ->
                          // Fall back to a noreply if GH refused to
                          // share — rare, but the user can change it
                          // later from /portal.
                          sprintf "%d+%s@users.noreply.github.com" ghUser.id ghUser.login)
                      let cid = newCustomerId ()
                      let customer : Customer =
                        { id              = cid
                          email           = primaryEmail
                          emailVerifiedAt = Some now
                          passwordHash    = None
                          passwordSalt    = None
                          passwordAlgo    = None
                          githubUserId    = Some ghUser.id
                          createdAt       = now
                          updatedAt       = now }
                      let mutable inserted = false
                      try cfg.store.Insert customer; inserted <- true
                      with ex ->
                        eprintfn "  [auth] insert github customer failed: %s" ex.Message
                      if not inserted then
                        return! (errJson 500 "could not create account"
                                 >=> clearGhStateCookie) ctx
                      else
                        let setCookies, _ = issueSession cfg ctx customer
                        return! (Redirection.FOUND pending.returnTo
                                 >=> setCookies
                                 >=> clearGhStateCookie) ctx
      | _ ->
        return! (errJson 400 "missing code or state"
                 >=> clearGhStateCookie) ctx
  }

/// `POST /api/auth/github/unlink` — drop the GH binding for the
/// currently signed-in customer. Refused if the customer has no
/// password (would lock them out).
let private githubUnlink (cfg : CustomerAuthConfig) : WebPart =
  fun ctx -> async {
    let signedIn =
      match tryReadCookie ctx.request accessCookieName with
      | None -> None
      | Some token ->
        match tryVerifyAccessJwt cfg.signingKey token with
        | None -> None
        | Some claims -> cfg.store.TryGetById claims.customerId
    match signedIn with
    | None -> return! errJson 401 "not signed in" ctx
    | Some c ->
      if c.githubUserId.IsNone then
        return! jsonResp 200 """{"status":"no link"}""" ctx
      elif c.passwordHash.IsNone then
        return! errJson 409 "set a password before unlinking GitHub (would lock you out)" ctx
      else
        let now = DateTimeOffset.UtcNow
        cfg.store.Update c.id (fun cur ->
          { cur with githubUserId = None; updatedAt = now }) |> ignore
        return! jsonResp 200 """{"status":"unlinked"}""" ctx
  }


/// Compose every customer-auth WebPart under `/api/auth/*` plus the
/// browser-friendly `/auth/verify` redirect target. Mount before any
/// auth gate.
let webPart (cfg : CustomerAuthConfig) : WebPart =
  choose [
    POST >=> path "/api/auth/signup"   >=> signup cfg
    POST >=> path "/api/auth/signin"   >=> signin cfg
    POST >=> path "/api/auth/signout"  >=> signout cfg
    POST >=> path "/api/auth/refresh"  >=> refresh cfg
    POST >=> path "/api/auth/forgot"   >=> forgot cfg
    POST >=> path "/api/auth/reset"    >=> reset cfg
    GET  >=> path "/api/auth/me"       >=> me cfg
    GET  >=> path "/auth/verify"       >=> verify cfg
    GET  >=> path "/api/auth/github/start"    >=> githubStart cfg
    GET  >=> path "/api/auth/github/callback" >=> githubCallback cfg
    POST >=> path "/api/auth/github/unlink"   >=> githubUnlink cfg
  ]

/// Helper for downstream gates (the member portal). Returns the
/// authenticated `Customer` if the request carries a valid access
/// cookie, `None` otherwise. Callers should respond 401 themselves.
let tryAuthenticate (cfg : CustomerAuthConfig) (req : HttpRequest) : Customer option =
  match tryReadCookie req accessCookieName with
  | None -> None
  | Some token ->
    match tryVerifyAccessJwt cfg.signingKey token with
    | None -> None
    | Some claims -> cfg.store.TryGetById claims.customerId
