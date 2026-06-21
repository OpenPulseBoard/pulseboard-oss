module PulseBoard.Rbac

open System
open Suave
open Suave.Operators
open PulseBoard.Tenancy
open PulseBoard.Audit

// `ctx.userState` key under which the resolved TenantCtx is attached after
// `Auth.resolveApiKey`. Consumers should not read this directly — use
// `tryGetTenant` instead.
let private tenantStateKey = "pulse.tenant"

/// Suave's `HttpContext.userState` is a mutable `Dictionary<string, obj>`;
/// we set the entry in place and return the same context.
let attachTenant (ctx : HttpContext) (t : TenantCtx) =
  ctx.userState.[tenantStateKey] <- box t
  ctx

let tryGetTenant (ctx : HttpContext) : TenantCtx option =
  match ctx.userState.TryGetValue tenantStateKey with
  | true, (:? TenantCtx as t) -> Some t
  | _ -> None

/// Best-effort client IP: trust `X-Forwarded-For` if the proxy injected
/// one, otherwise leave unset. Wiring up Suave's connection-level IP is
/// deferred until the edge sits behind a known LB topology (Phase 6).
let private remoteIp (ctx : HttpContext) : string option =
  ctx.request.headers
  |> Seq.tryFind (fun (k, _) ->
       String.Equals(k, "x-forwarded-for", StringComparison.OrdinalIgnoreCase))
  |> Option.map (snd >> fun v -> v.Trim())

let private emit (log : IAuditLog) action outcome (ctx : HttpContext) details =
  let t = tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = outcome
      remoteIp = remoteIp ctx
      details  = details }
  try log.Append ev with _ -> ()

let private forbidden (msg : string) : WebPart =
  Suave.RequestErrors.FORBIDDEN (sprintf """{"error":%s}""" (System.Text.Json.JsonSerializer.Serialize msg))
  >=> Writers.setMimeType "application/json"

/// Best-effort sketch of the presented bearer/api-key header, for
/// diagnostic log lines on 403s. Returns just enough context (header
/// kind + token prefix) to tell apart "wrong format", "wrong key",
/// "no header at all" without leaking the secret half.
let private describeAuth (ctx : HttpContext) : string =
  let header (name : string) =
    ctx.request.headers
    |> Seq.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
    |> Option.map (snd >> fun v -> v.Trim())
  let sample (v : string) =
    if isNull v || v.Length = 0 then "<empty>"
    else
      let dot = v.IndexOf '.'
      let cut = if dot > 0 then dot else min 12 v.Length
      v.Substring(0, cut) + (if v.Length > cut then "…" else "")
  match header "authorization" with
  | Some v when v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
    sprintf "auth=bearer token=%s" (sample (v.Substring(7).Trim()))
  | Some v -> sprintf "auth=other scheme=%s" (sample v)
  | None ->
    match header "x-api-key" with
    | Some v -> sprintf "auth=x-api-key token=%s" (sample v)
    | None   -> "auth=<none>"

/// WebPart guard: require an authenticated `TenantCtx` whose `scopes`
/// cover `need`. Records exactly one audit event per request (allow or
/// deny); on deny the inner part is not invoked and a 403 is returned.
let requireScope (log : IAuditLog) (action : string) (need : Scope)
                 (inner : WebPart) : WebPart =
  fun ctx -> async {
    match tryGetTenant ctx with
    | None ->
      emit log action Deny ctx (Some "no tenant context")
      eprintfn "[auth] deny action=%s reason=no-tenant path=%s %s"
        action ctx.request.path (describeAuth ctx)
      return! forbidden "forbidden" ctx
    | Some t when hasScope t.scopes need ->
      emit log action Allow ctx None
      return! inner ctx
    | Some t ->
      emit log action Deny ctx (Some "missing scope")
      eprintfn "[auth] deny action=%s reason=missing-scope path=%s tenant=%s have=%d need=%d"
        action ctx.request.path
        (let (PulseBoard.Tenancy.TenantId id) = t.tenant.id in id)
        (int t.scopes) (int need)
      return! forbidden "insufficient scope" ctx
  }

/// WebPart guard: charge `cost` tokens against the tenant's bucket for
/// `kind`. On allow, falls through to `inner`. On throttle, returns 429
/// with a `Retry-After` header (seconds, RFC 7231) and audits one
/// `quota.<kind>` deny event with the projected wait in details. Assumes
/// a TenantCtx is already attached (i.e. composed after `requireScope`).
let requireQuota (log : IAuditLog)
                 (limiter : PulseBoard.Quotas.Limiter)
                 (kind : PulseBoard.Quotas.Kind)
                 (cost : float)
                 (inner : WebPart) : WebPart =
  let action = "quota." + PulseBoard.Quotas.kindStr kind
  fun ctx -> async {
    match tryGetTenant ctx with
    | None ->
      // No tenant attached — the upstream `requireScope` should have
      // already 403'd. Be safe and pass through; the gate after us will
      // catch it.
      return! inner ctx
    | Some t ->
      match limiter.TryAcquire(t.tenant.id, kind, cost) with
      | PulseBoard.Quotas.AcquireResult.Ok ->
        return! inner ctx
      | PulseBoard.Quotas.AcquireResult.Throttled retryMs ->
        let retrySec = max 1 (int (ceil (float retryMs / 1000.0)))
        emit log action Deny ctx
          (Some (sprintf "retryAfterMs=%d cost=%g" retryMs cost))
        let body =
          sprintf
            """{"error":"rate limit exceeded","kind":"%s","retryAfterMs":%d}"""
            (PulseBoard.Quotas.kindStr kind) retryMs
        return!
          (Suave.RequestErrors.TOO_MANY_REQUESTS body
           >=> Writers.setMimeType "application/json"
           >=> Writers.setHeader "Retry-After" (string retrySec)) ctx
  }
