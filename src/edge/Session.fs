module PulseBoard.Session

open System
open System.Security.Cryptography
open System.Text
open System.Collections.Generic
open System.IdentityModel.Tokens.Jwt
open Microsoft.IdentityModel.Tokens
open Suave
open Suave.Http
open Suave.Cookie
open PulseBoard.Tenancy

// Server-signed HS256 session JWTs in an httpOnly cookie. Used by the OIDC
// browser flow: after the IdP callback validates the upstream id_token we
// mint one of these so subsequent /api/* requests can be authorized without
// re-contacting the IdP. The cookie is stateless — no server-side session
// store — which keeps the edge horizontally scalable; the trade-off is that
// revocation only kicks in at expiry. Default lifetime is short (15 min).

let cookieName = "pulse_session"

let defaultLifetime = TimeSpan.FromMinutes 15.0

[<NoComparison; NoEquality>]
type SessionClaims =
  { subject  : string                 // IdP `sub`
    email    : string option
    tenantId : TenantId
    role     : Role
    scopes   : Scope
    issuedAt : DateTimeOffset
    expires  : DateTimeOffset
    issuer   : string }                // upstream OIDC issuer

let private roleStr = function
  | Viewer  -> "viewer"
  | Editor  -> "editor"
  | Admin   -> "admin"
  | Billing -> "billing"

let private parseRole = function
  | "viewer"  -> Some Viewer
  | "editor"  -> Some Editor
  | "admin"   -> Some Admin
  | "billing" -> Some Billing
  | _         -> None

/// Generate a fresh 32-byte HS256 signing key (base64-encoded form returned
/// for `--session-secret=` logging in dev mode).
let generateKey () : byte[] =
  let b = Array.zeroCreate 32
  RandomNumberGenerator.Fill(Span b)
  b

let keyToBase64 (key : byte[]) = Convert.ToBase64String key

/// Decode a base64 (or base64url) session secret. Throws if shorter than
/// 32 bytes (HS256 requires >= 256 bits of key material).
let keyFromBase64 (raw : string) : byte[] =
  let normalized =
    raw.Trim()
       .Replace('-', '+')
       .Replace('_', '/')
  let padded =
    match normalized.Length % 4 with
    | 0 -> normalized
    | 2 -> normalized + "=="
    | 3 -> normalized + "="
    | _ -> normalized + "==="   // tolerate odd input
  let bytes = Convert.FromBase64String padded
  if bytes.Length < 32 then
    invalidArg "raw" "session secret must be at least 32 bytes (256 bits)"
  bytes

let private handler =
  let h = JwtSecurityTokenHandler()
  h.MapInboundClaims <- false
  h

let mint (key : byte[]) (claims : SessionClaims) : string =
  let creds =
    SigningCredentials(SymmetricSecurityKey key, SecurityAlgorithms.HmacSha256)
  let (TenantId tid) = claims.tenantId
  let descriptor = SecurityTokenDescriptor()
  descriptor.Issuer    <- "pulseboard-edge"
  descriptor.Audience  <- "pulseboard-edge"
  descriptor.IssuedAt  <- Nullable claims.issuedAt.UtcDateTime
  descriptor.NotBefore <- Nullable claims.issuedAt.UtcDateTime
  descriptor.Expires   <- Nullable claims.expires.UtcDateTime
  descriptor.SigningCredentials <- creds
  let cd = Dictionary<string, obj>()
  cd.["sub"]      <- box claims.subject
  cd.["tenant"]   <- box tid
  cd.["role"]     <- box (roleStr claims.role)
  cd.["scopes"]   <- box (int claims.scopes)
  cd.["iss_up"]   <- box claims.issuer
  match claims.email with
  | Some e -> cd.["email"] <- box e
  | None -> ()
  descriptor.Claims <- cd
  let token = handler.CreateToken descriptor
  handler.WriteToken token

/// Verify a session JWT and rehydrate the `TenantCtx` it stands for.
/// Returns `None` on any signature/expiry/parse/tenant-missing failure.
let tryVerify (key : byte[]) (store : ITenantStore) (token : string)
    : TenantCtx option =
  if String.IsNullOrWhiteSpace token then None
  else
    try
      let parms = TokenValidationParameters()
      parms.ValidateIssuer           <- true
      parms.ValidIssuer              <- "pulseboard-edge"
      parms.ValidateAudience         <- true
      parms.ValidAudience            <- "pulseboard-edge"
      parms.ValidateLifetime         <- true
      parms.ValidateIssuerSigningKey <- true
      parms.IssuerSigningKey         <- SymmetricSecurityKey key
      parms.ClockSkew                <- TimeSpan.FromSeconds 30.0
      let principal, _ = handler.ValidateToken(token, parms)
      let claim n =
        principal.Claims
        |> Seq.tryFind (fun c -> c.Type = n)
        |> Option.map (fun c -> c.Value)
      match claim "tenant", claim "role", claim "scopes" with
      | Some tid, Some r, Some s ->
        match parseRole r, Int32.TryParse s with
        | Some role, (true, scopeInt) ->
          match store.TryGetTenant (TenantId tid) with
          | Some t ->
            // No ApiKeyId for browser sessions — synthesize a sentinel so
            // the existing TenantCtx shape continues to work and audit
            // records make the source obvious.
            Some
              { tenant   = t
                apiKeyId = ApiKeyId ("session:" + (claim "sub" |> Option.defaultValue "?"))
                role     = role
                scopes   = enum<Scope> scopeInt }
          | None -> None
        | _ -> None
      | _ -> None
    with _ -> None

// -- cookie helpers ---------------------------------------------------------

let tryReadCookie (req : HttpRequest) : string option =
  match req.cookies.TryGetValue cookieName with
  | true, c when not (String.IsNullOrWhiteSpace c.value) -> Some c.value
  | _ -> None

/// Set the session cookie. `secure` should be `true` in production (HTTPS);
/// `false` for loopback dev where the browser would otherwise reject it.
let setSessionCookie (secure : bool) (expires : DateTimeOffset) (token : string)
    : WebPart =
  let c = HttpCookie.createKV cookieName token
  let c = { c with
              httpOnly = true
              secure   = secure
              path     = Some "/"
              expires  = Some expires
              sameSite = Some SameSite.Lax }
  setCookie c

let clearSessionCookie : WebPart =
  let c = HttpCookie.createKV cookieName ""
  let c = { c with
              httpOnly = true
              path     = Some "/"
              expires  = Some (DateTimeOffset.UtcNow.AddDays(-1.0)) }
  setCookie c
