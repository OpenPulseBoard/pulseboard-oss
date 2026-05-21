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

/// WebPart guard: require an authenticated `TenantCtx` whose `scopes`
/// cover `need`. Records exactly one audit event per request (allow or
/// deny); on deny the inner part is not invoked and a 403 is returned.
let requireScope (log : IAuditLog) (action : string) (need : Scope)
                 (inner : WebPart) : WebPart =
  fun ctx -> async {
    match tryGetTenant ctx with
    | None ->
      emit log action Deny ctx (Some "no tenant context")
      return! forbidden "forbidden" ctx
    | Some t when hasScope t.scopes need ->
      emit log action Allow ctx None
      return! inner ctx
    | Some _ ->
      emit log action Deny ctx (Some "missing scope")
      return! forbidden "insufficient scope" ctx
  }
