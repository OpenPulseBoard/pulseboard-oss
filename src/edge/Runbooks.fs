module PulseBoard.Runbooks

// PLAN-NEXT 14.1 — Inline runbooks for alerts.
//
// Every alert rule carries an optional markdown `runbook` (see
// `Rules.Rule.runbook`). This module turns that free-text markdown into
// an actionable, trackable checklist and owns the post-incident view:
//
//   1.  Step parsing.  `parseSteps` extracts ordered steps from the
//       markdown — GFM task-list items (`- [ ] …`) first, then ordered
//       list items (`1. …`), then plain bullets (`- …`). Each step is an
//       (index, text) pair the portal renders as a checkbox.
//
//   2.  Progress store.  Per `(tenant, fingerprint)` we keep which steps
//       have been completed, by whom, and when. Records are created the
//       moment an alert with a runbook fires (via `Tracker.Observe`, wired
//       into the alert sink) so the post-incident view sees every incident
//       even if nobody touched the checklist. Persistence is an append-only
//       NDJSON journal per tenant under `<root>/<tenant>.ndjson`, replayed
//       at startup (latest line per fingerprint wins).
//
//   3.  Metric.  Completing a step records `pulse_runbook_step_seconds`
//       (seconds from alert fire to step completion) into the self-metrics
//       store so MTTR-by-step is queryable like any other series.
//
//   4.  Post-incident view.  `GET /api/runbooks/incidents` aggregates the
//       progress records by rule: incident count, mean MTTR, mean completed
//       steps, and the step indices most often skipped — feeding runbook
//       quality improvements.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Rules

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- step parsing -----------------------------------------------------------

[<NoComparison>]
type RunbookStep =
  { idx  : int
    text : string }

let private taskItem = System.Text.RegularExpressions.Regex(@"^\s*[-*]\s+\[( |x|X)\]\s+(.*)$")
let private orderedItem = System.Text.RegularExpressions.Regex(@"^\s*\d+[.)]\s+(.*)$")
let private bulletItem = System.Text.RegularExpressions.Regex(@"^\s*[-*]\s+(.*)$")

/// Extract an ordered checklist from runbook markdown. Task-list items
/// win; if none exist we fall back to ordered list items; if none of
/// those, plain bullets. Headings/prose are ignored as steps.
let parseSteps (markdown : string) : RunbookStep[] =
  if String.IsNullOrWhiteSpace markdown then [||] else
  let lines = markdown.Replace("\r\n", "\n").Split('\n')
  let collect (re : System.Text.RegularExpressions.Regex) (group : int) =
    lines
    |> Array.choose (fun l ->
      let m = re.Match l
      if m.Success then
        let t = m.Groups.[group].Value.Trim()
        if t.Length = 0 then None else Some t
      else None)
  let tasks = collect taskItem 2
  let chosen =
    if tasks.Length > 0 then tasks
    else
      let ordered = collect orderedItem 1
      if ordered.Length > 0 then ordered
      else collect bulletItem 1
  chosen |> Array.mapi (fun i t -> { idx = i; text = t })

// -- progress model ---------------------------------------------------------

[<NoComparison>]
type StepCompletion =
  { idx  : int
    at   : int64
    user : string }

[<NoComparison>]
type RunbookProgress =
  { fingerprint : string
    ruleId      : string
    ruleName    : string
    runbook     : string
    stepTexts   : string[]
    firedAt     : int64
    startedAt   : int64                       // first observation / interaction
    resolvedAt  : int64 option
    completions : Map<int, StepCompletion> }

let private totalSteps (p : RunbookProgress) = p.stepTexts.Length

// -- JSON codec -------------------------------------------------------------

let private writeProgress (w : Utf8JsonWriter) (p : RunbookProgress) =
  w.WriteStartObject()
  w.WriteString("fingerprint", p.fingerprint)
  w.WriteString("ruleId",      p.ruleId)
  w.WriteString("ruleName",    p.ruleName)
  w.WriteString("runbook",     p.runbook)
  w.WritePropertyName "stepTexts"
  w.WriteStartArray()
  for t in p.stepTexts do w.WriteStringValue t
  w.WriteEndArray()
  w.WriteNumber("firedAt",   p.firedAt)
  w.WriteNumber("startedAt", p.startedAt)
  match p.resolvedAt with
  | Some t -> w.WriteNumber("resolvedAt", t)
  | None   -> ()
  w.WritePropertyName "completions"
  w.WriteStartArray()
  for KeyValue(_, c) in p.completions do
    w.WriteStartObject()
    w.WriteNumber("idx", c.idx)
    w.WriteNumber("at",  c.at)
    w.WriteString("user", c.user)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()

let serialiseProgress (p : RunbookProgress) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeProgress w p)
  Encoding.UTF8.GetString(ms.ToArray())

let private readStr (el : JsonElement) (name : string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
  | _ -> None

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L in (if v.TryGetInt64 &n then n else dflt)
  | _ -> dflt

let parseProgress (line : string) : RunbookProgress option =
  try
    use doc = JsonDocument.Parse line
    let r = doc.RootElement
    match readStr r "fingerprint" with
    | None -> None
    | Some fp ->
      let stepTexts =
        match r.TryGetProperty "stepTexts" with
        | true, a when a.ValueKind = JsonValueKind.Array ->
          a.EnumerateArray()
          |> Seq.choose (fun e ->
            if e.ValueKind = JsonValueKind.String then Some (e.GetString()) else None)
          |> Array.ofSeq
        | _ -> [||]
      let completions =
        match r.TryGetProperty "completions" with
        | true, a when a.ValueKind = JsonValueKind.Array ->
          a.EnumerateArray()
          |> Seq.choose (fun e ->
            let idx = int (readInt64 e "idx" -1L)
            if idx < 0 then None
            else Some (idx, { idx = idx
                              at = readInt64 e "at" 0L
                              user = readStr e "user" |> Option.defaultValue "" }))
          |> Map.ofSeq
        | _ -> Map.empty
      let resolvedAt =
        match r.TryGetProperty "resolvedAt" with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some (readInt64 r "resolvedAt" 0L)
        | _ -> None
      Some {
        fingerprint = fp
        ruleId      = readStr r "ruleId"   |> Option.defaultValue ""
        ruleName    = readStr r "ruleName" |> Option.defaultValue ""
        runbook     = readStr r "runbook"  |> Option.defaultValue ""
        stepTexts   = stepTexts
        firedAt     = readInt64 r "firedAt" 0L
        startedAt   = readInt64 r "startedAt" 0L
        resolvedAt  = resolvedAt
        completions = completions }
  with _ -> None

// -- store ------------------------------------------------------------------

type IRunbookStore =
  abstract Get    : TenantId * string -> RunbookProgress option
  abstract Upsert : TenantId * RunbookProgress -> unit
  abstract List   : TenantId -> RunbookProgress[]

type FileRunbookStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, ConcurrentDictionary<string, RunbookProgress>>()
  let sync  = obj()
  let pathFor (TenantId t) = Path.Combine(root, t + ".ndjson")
  let bucket (TenantId t) =
    cache.GetOrAdd(t, fun _ ->
      let m = ConcurrentDictionary<string, RunbookProgress>()
      let p = Path.Combine(root, t + ".ndjson")
      if File.Exists p then
        try
          for line in File.ReadAllLines p do
            match parseProgress line with
            | Some pr -> m.[pr.fingerprint] <- pr   // latest line wins
            | None -> ()
        with _ -> ()
      m)
  interface IRunbookStore with
    member _.Get(tid, fp) =
      match (bucket tid).TryGetValue fp with
      | true, p -> Some p | _ -> None
    member _.Upsert(tid, p) =
      lock sync (fun () ->
        (bucket tid).[p.fingerprint] <- p
        try File.AppendAllText(pathFor tid, serialiseProgress p + "\n")
        with _ -> ())
    member _.List tid =
      (bucket tid).Values |> Seq.toArray

// -- tracker (observes the alert sink) --------------------------------------

/// Side-channel that materialises a `RunbookProgress` record when an alert
/// with a runbook starts firing, and stamps `resolvedAt` when it resolves.
/// Wired into the same alert sink that drives routing so every incident is
/// captured for the post-incident view regardless of UI interaction.
type Tracker(store : IRunbookStore) =
  member _.Observe(a : AlertInstance) =
    match a.runbook with
    | None -> ()
    | Some rb when not (String.IsNullOrWhiteSpace rb) ->
      match a.state with
      | AlertState.Firing ->
        match store.Get(a.tenantId, a.fingerprint) with
        | Some _ -> ()
        | None ->
          let now = nowMs ()
          let steps = parseSteps rb |> Array.map (fun s -> s.text)
          store.Upsert(a.tenantId,
            { fingerprint = a.fingerprint
              ruleId      = a.ruleId
              ruleName    = a.ruleName
              runbook     = rb
              stepTexts   = steps
              firedAt     = a.firedAt |> Option.defaultValue now
              startedAt   = now
              resolvedAt  = None
              completions = Map.empty })
      | AlertState.Resolved ->
        match store.Get(a.tenantId, a.fingerprint) with
        | Some p when p.resolvedAt.IsNone ->
          store.Upsert(a.tenantId,
            { p with resolvedAt = Some (a.resolvedAt |> Option.defaultValue (nowMs ())) })
        | _ -> ()
      | AlertState.Pending -> ()
    | Some _ -> ()

// -- progress JSON (for the portal) -----------------------------------------

let private writeProgressView (w : Utf8JsonWriter) (p : RunbookProgress) =
  w.WriteStartObject()
  w.WriteString("fingerprint", p.fingerprint)
  w.WriteString("ruleId",   p.ruleId)
  w.WriteString("ruleName", p.ruleName)
  w.WriteString("runbook",  p.runbook)
  w.WriteNumber("firedAt",  p.firedAt)
  match p.resolvedAt with
  | Some t -> w.WriteNumber("resolvedAt", t)
  | None   -> w.WriteNull "resolvedAt"
  w.WriteNumber("totalSteps", totalSteps p)
  w.WriteNumber("completedSteps", p.completions.Count)
  w.WritePropertyName "steps"
  w.WriteStartArray()
  p.stepTexts
  |> Array.iteri (fun i text ->
    w.WriteStartObject()
    w.WriteNumber("idx", i)
    w.WriteString("text", text)
    match p.completions.TryFind i with
    | Some c ->
      w.WriteBoolean("done", true)
      w.WriteNumber("at", c.at)
      w.WriteString("user", c.user)
    | None ->
      w.WriteBoolean("done", false)
    w.WriteEndObject())
  w.WriteEndArray()
  w.WriteEndObject()

let serialiseProgressView (p : RunbookProgress) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeProgressView w p)
  Encoding.UTF8.GetString(ms.ToArray())

// -- post-incident aggregation ----------------------------------------------

let private serialiseIncidents (records : RunbookProgress[]) : string =
  // Group by rule; an incident "closes" once resolved (MTTR = resolvedAt -
  // firedAt) — open incidents still count toward step stats but not MTTR.
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    records
    |> Array.groupBy (fun p -> p.ruleId, p.ruleName)
    |> Array.sortBy (fun ((_, name), _) -> name)
    |> Array.iter (fun ((ruleId, ruleName), group) ->
      let resolved =
        group |> Array.choose (fun p -> p.resolvedAt |> Option.map (fun r -> float (r - p.firedAt)))
      let avgMttrMs =
        if resolved.Length = 0 then 0.0 else Array.average resolved
      let totalDefined =
        group |> Array.sumBy totalSteps
      let totalCompleted =
        group |> Array.sumBy (fun p -> p.completions.Count)
      let avgCompleted =
        if group.Length = 0 then 0.0 else float totalCompleted / float group.Length
      // Skipped = steps that exist but were never completed across
      // resolved incidents, tallied per step index.
      let skipCounts = System.Collections.Generic.Dictionary<int, int>()
      for p in group do
        if p.resolvedAt.IsSome then
          for i in 0 .. totalSteps p - 1 do
            if not (p.completions.ContainsKey i) then
              skipCounts.[i] <- (match skipCounts.TryGetValue i with | true, n -> n | _ -> 0) + 1
      w.WriteStartObject()
      w.WriteString("ruleId", ruleId)
      w.WriteString("ruleName", ruleName)
      w.WriteNumber("incidents", group.Length)
      w.WriteNumber("resolvedIncidents", resolved.Length)
      w.WriteNumber("avgMttrMs", avgMttrMs)
      w.WriteNumber("totalStepsDefined", totalDefined)
      w.WriteNumber("totalStepsCompleted", totalCompleted)
      w.WriteNumber("avgCompletedSteps", avgCompleted)
      w.WritePropertyName "skippedSteps"
      w.WriteStartArray()
      skipCounts
      |> Seq.sortByDescending (fun kv -> kv.Value)
      |> Seq.iter (fun kv ->
        w.WriteStartObject()
        w.WriteNumber("idx", kv.Key)
        w.WriteNumber("count", kv.Value)
        w.WriteEndObject())
      w.WriteEndArray()
      w.WriteEndObject())
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

// -- REST -------------------------------------------------------------------

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

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

/// `resolveActive` returns the currently-firing `AlertInstance` for a
/// fingerprint (so the runbook can be materialised on demand if the alert
/// fired before the tracker observed it). `selfMetrics` receives the
/// `pulse_runbook_step_seconds` samples.
let webPart (multiTenant : bool)
            (store        : IRunbookStore)
            (selfMetrics  : MetricStore)
            (resolveActive : TenantId -> string -> AlertInstance option) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }

  // Resolve (or lazily create) the progress record for an active alert.
  let ensureRecord (tid : TenantId) (fp : string) : RunbookProgress option =
    match store.Get(tid, fp) with
    | Some p -> Some p
    | None ->
      match resolveActive tid fp with
      | Some a ->
        match a.runbook with
        | Some rb when not (String.IsNullOrWhiteSpace rb) ->
          let now = nowMs ()
          let steps = parseSteps rb |> Array.map (fun s -> s.text)
          let rec_ =
            { fingerprint = fp
              ruleId      = a.ruleId
              ruleName    = a.ruleName
              runbook     = rb
              stepTexts   = steps
              firedAt     = a.firedAt |> Option.defaultValue now
              startedAt   = now
              resolvedAt  = None
              completions = Map.empty }
          store.Upsert(tid, rec_)
          Some rec_
        | _ -> None
      | None -> None

  choose [
    GET >=> pathScan "/api/alerts/%s/runbook" (fun fp ->
      withTenant (fun tid ->
        match ensureRecord tid fp with
        | Some p -> jsonResp 200 (serialiseProgressView p)
        | None   -> errJson 404 "no runbook for this alert"))

    POST >=> pathScan "/api/alerts/%s/runbook/step" (fun fp ->
      withTenant (fun tid ->
        fun ctx -> async {
          match ensureRecord tid fp with
          | None -> return! errJson 404 "no runbook for this alert" ctx
          | Some p ->
            try
              use doc = JsonDocument.Parse (readBody ctx.request)
              let r = doc.RootElement
              let idx = int (readInt64 r "idx" -1L)
              let doneFlag =
                match r.TryGetProperty "done" with
                | true, v when v.ValueKind = JsonValueKind.False -> false
                | _ -> true
              let user = readStr r "user" |> Option.defaultValue "operator"
              if idx < 0 || idx >= totalSteps p then
                return! errJson 400 "step index out of range" ctx
              else
                let now = nowMs ()
                let completions =
                  if doneFlag then
                    Map.add idx { idx = idx; at = now; user = user } p.completions
                  else
                    Map.remove idx p.completions
                let updated = { p with completions = completions }
                store.Upsert(tid, updated)
                if doneFlag then
                  // pulse_runbook_step_seconds: time from fire to completion.
                  let elapsed = float (now - p.firedAt) / 1000.0
                  selfMetrics.Record(
                    "pulse_runbook_step_seconds",
                    { ts = now; value = max 0.0 elapsed })
                return! jsonResp 200 (serialiseProgressView updated) ctx
            with ex ->
              return! errJson 400 ("invalid body: " + ex.Message) ctx
        }))

    GET >=> path "/api/runbooks/incidents" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseIncidents (store.List tid)))
  ]
