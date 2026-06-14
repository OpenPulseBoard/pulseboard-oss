module PulseBoard.CostsApi

// Phase 14.3 — tenant-scoped cost transparency + cardinality killer API.
//
// Admin already exposes `/api/admin/tenants/{id}/cost/series` for
// platform owners (Admin.costsWebPart). Tenants themselves need to see
// their own cost breakdown and one-click drop noisy labels. Endpoints:
//
//   GET    /api/cost/series?top=N          — top-N series for the
//                                           caller's tenant
//   GET    /api/cost/teams                 — per-team aggregation
//   GET    /api/cost/dropped-labels        — active kill rules
//   POST   /api/cost/dropped-labels        — add (single or batch)
//   DELETE /api/cost/dropped-labels/{lbl}  — remove a kill rule
//
// Every write side-effects the default agent group's overlay TOML so
// the rule reaches the collector on its next config poll.

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.CardinalityKiller
open PulseBoard.AgentGroups
open PulseBoard.Costs

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private resolveTenant multiTenant (ctx : HttpContext) =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else Some (TenantId "__local__")

let private parseTop (ctx : HttpContext) =
  match ctx.request.queryParam "top" with
  | Choice1Of2 s ->
    match Int32.TryParse s with
    | true, n when n > 0 && n <= 1000 -> n
    | _ -> 20
  | _ -> 20

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

/// Re-render the default group's overlay so its managed cardinality
/// killer block reflects the current set of kill rules for `tenant`.
/// Bumps the group version when (and only when) the overlay changes,
/// causing the agent to pick up the new labeldrop on its next poll.
let syncOverlay (groupStore : IAgentGroupStore)
                (killer     : ICardinalityKillerStore)
                (tenant     : TenantId) : unit =
  let labels = killer.List tenant |> Array.map (fun r -> r.label)
  let cur =
    groupStore.TryGet(tenant, DefaultGroupId)
    |> Option.defaultWith emptyDefaultGroup
  let newOverlay = applyToOverlay cur.overlayToml labels
  if newOverlay <> cur.overlayToml then
    groupStore.Upsert(tenant, { cur with overlayToml = newOverlay }) |> ignore

let webPart (multiTenant : bool)
            (costs       : ICostTracker)
            (killer      : ICardinalityKillerStore)
            (groupStore  : IAgentGroupStore) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }

  let listDrops (tid : TenantId) : WebPart =
    fun ctx -> async {
      let body = killer.List tid |> serialiseRules
      return! jsonResp 200 body ctx
    }

  let postDrops (tid : TenantId) : WebPart =
    fun ctx -> async {
      let body = readBody ctx.request
      match parseRules body with
      | Result.Error msg -> return! errJson 400 msg ctx
      | Result.Ok rules ->
        let stored =
          rules |> Array.map (fun r -> killer.Upsert(tid, r))
        syncOverlay groupStore killer tid
        return! jsonResp 201 (serialiseRules stored) ctx
    }

  let deleteDrop (tid : TenantId) (label : string) : WebPart =
    fun ctx -> async {
      let removed = killer.Delete(tid, Uri.UnescapeDataString label)
      if removed then
        syncOverlay groupStore killer tid
        return! jsonResp 200 """{"ok":true}""" ctx
      else
        return! errJson 404 "no such label"  ctx
    }

  choose [
    GET >=> path "/api/cost/series" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          let rows = costs.TopSeries(tid, parseTop ctx)
          let (TenantId t) = tid
          return! jsonResp 200 (topSeriesJson t rows) ctx
        })

    GET >=> path "/api/cost/teams" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          let rows = costs.TeamBreakdown(tid, defaultTeamFor)
          let (TenantId t) = tid
          return! jsonResp 200 (teamBreakdownJson t rows) ctx
        })

    GET    >=> path   "/api/cost/dropped-labels" >=> withTenant listDrops
    POST   >=> path   "/api/cost/dropped-labels" >=> withTenant postDrops
    DELETE >=> pathScan "/api/cost/dropped-labels/%s" (fun label ->
      withTenant (fun tid -> deleteDrop tid label))
  ]
