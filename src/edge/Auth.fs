module PulseBoard.Auth

open System
open System.IO
open System.Security.Cryptography
open Suave
open Suave.Authentication

/// Map of token name (used as the Basic-Auth username) to its shared secret.
type TokenMap = Map<string, string>

let private bytes (s : string) = System.Text.Encoding.UTF8.GetBytes s

/// Constant-time string comparison built on `CryptographicOperations.FixedTimeEquals`.
/// Returns false for length mismatch, but still performs work to avoid trivially
/// leaking length via early-exit timing.
let private constantTimeEquals (a : string) (b : string) =
  let ab, bb = bytes a, bytes b
  if ab.Length = bb.Length then
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan ab, ReadOnlySpan bb)
  else
    // Comparable-cost dummy work; result still false.
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan ab, ReadOnlySpan ab) |> ignore
    false

/// Parse a token spec: lines or comma-separated entries of `name:secret`.
/// Blank lines and `#` comments are ignored.
let parse (raw : string) : TokenMap =
  if String.IsNullOrWhiteSpace raw then Map.empty
  else
    raw.Split([| ','; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun rawLine ->
        let line = rawLine.Trim()
        if line.Length = 0 || line.StartsWith "#" then None
        else
          let i = line.IndexOf ':'
          if i <= 0 || i = line.Length - 1 then None
          else
            let name   = line.Substring(0, i).Trim()
            let secret = line.Substring(i + 1).Trim()
            if name.Length > 0 && secret.Length > 0 then Some (name, secret)
            else None)
    |> Map.ofArray

let loadFromFile (path : string) : TokenMap =
  if File.Exists path then parse (File.ReadAllText path) else Map.empty

let loadFromEnv (envName : string) : TokenMap =
  let v = Environment.GetEnvironmentVariable envName
  if isNull v then Map.empty else parse v

/// Verifier suitable for `Suave.Authentication.authenticateBasic`.
let verify (tokens : TokenMap) (username : string, password : string) : bool =
  match Map.tryFind username tokens with
  | Some secret -> constantTimeEquals secret password
  | None ->
    // Run a same-shaped comparison to avoid trivially revealing whether
    // the token name exists via response time.
    constantTimeEquals password password |> ignore
    false

/// Wrap `inner` so that requests must present a valid token via HTTP Basic.
/// If `tokens` is empty, returns `inner` unchanged (auth disabled).
let protect (tokens : TokenMap) (inner : WebPart) : WebPart =
  if Map.isEmpty tokens then inner
  else authenticateBasic (verify tokens) inner

// -- Scoped API keys (Phase 1) ------------------------------------------------

/// Pull a presented API key out of the request. Accepts either
/// `X-API-Key: <token>` or `Authorization: Bearer <token>` (case-insensitive).
let private extractApiKey (req : HttpRequest) : string option =
  let header (name : string) =
    req.headers
    |> Seq.tryFind (fun (k, _) ->
         String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
    |> Option.map (snd >> fun v -> v.Trim())
  match header "x-api-key" with
  | Some v when v.Length > 0 -> Some v
  | _ ->
    match header "authorization" with
    | Some v when v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
      let token = v.Substring(7).Trim()
      if token.Length > 0 then Some token else None
    | _ -> None

/// WebPart middleware: if a valid API key is presented, attach the resolved
/// `TenantCtx` to `ctx.userState`. Always falls through to `inner` — actual
/// gating is the job of `PulseBoard.Rbac.requireScope`. Invalid or absent
/// keys leave `userState` untouched (so the request reaches a 403 via the
/// scope check rather than a 401 here).
let resolveApiKey (store : PulseBoard.Tenancy.ITenantStore)
                  (inner : WebPart) : WebPart =
  fun ctx -> async {
    match extractApiKey ctx.request with
    | None -> return! inner ctx
    | Some presented ->
      match PulseBoard.Tenancy.verify store presented with
      | None   -> return! inner ctx
      | Some t -> return! inner (PulseBoard.Rbac.attachTenant ctx t)
  }
