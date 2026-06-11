module PulseBoard.Rules

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.QueryApi

// Persisted rule groups. Today's hardcoded
// `cpu > 0.9` lives in `Alerts.fs`; this module replaces that with a
// tenant-scoped CRUD model plus a sharded evaluator pool. Every rule
// expresses a *boolean alarm* on top of an embedded PromQL/LogQL
// query — the evaluator runs the query, applies the rule's threshold
// to whatever shape comes back, and emits an `AlertInstance` per
// breaching label-set.
//
//   PromQL rules — evaluated by the embedded engine in `QueryApi`
//     (`parsePromExpr` + `evalAt`): vector selectors, range functions
//     (rate/irate/increase), aggregations with `by`/`without` grouping,
//     and scalar/vector arithmetic. Each resulting series is compared
//     against the threshold; one alert per breaching label-set. Queries
//     outside this subset still need an upstream Mimir ruler.
//
//   LogQL rules — `parseLogQl` plus the embedded `logMatches` count
//     matching entries within the rule's evaluation window; the
//     threshold is then `count <cmp> threshold`. Alert labels come
//     from the rule's `labels` map (the embedded LogQL surface does
//     not project labels beyond `service` / `level`).
//
// Persistence: one JSON document per group under
// `<dataDir>/rules/<tenant>/<groupId>.json`. The on-disk shape is
// stable and human-editable.

// -- model ------------------------------------------------------------------

type Cmp = Gt | Lt | Gte | Lte | Eq | Neq

let private cmpToStr = function
  | Gt -> ">" | Lt -> "<" | Gte -> ">=" | Lte -> "<="
  | Eq -> "==" | Neq -> "!="

let private strToCmp = function
  | ">"  -> Some Gt  | "<"  -> Some Lt
  | ">=" -> Some Gte | "<=" -> Some Lte
  | "==" -> Some Eq  | "!=" -> Some Neq
  | _    -> None

let private evalCmp cmp (a : float) (b : float) =
  match cmp with
  | Gt  -> a >  b | Lt  -> a <  b
  | Gte -> a >= b | Lte -> a <= b
  | Eq  -> a =  b | Neq -> a <> b

[<RequireQualifiedAccess>]
type Severity =
  | Info | Warning | Critical | Page

let severityToStr = function
  | Severity.Info -> "info" | Severity.Warning -> "warning"
  | Severity.Critical -> "critical" | Severity.Page -> "page"

let strToSeverity = function
  | "info"     -> Severity.Info
  | "warning"  -> Severity.Warning
  | "critical" -> Severity.Critical
  | "page"     -> Severity.Page
  | _          -> Severity.Warning

type RuleLang = PromQL | LogQL

let private langToStr = function PromQL -> "promql" | LogQL -> "logql"
let private strToLang = function
  | "promql" -> Some PromQL
  | "logql"  -> Some LogQL
  | _        -> None

[<NoComparison>]
type Rule =
  { id          : string
    name        : string
    lang        : RuleLang
    expr        : string
    cmp         : Cmp
    threshold   : float
    forMs       : int64               // pending → firing dwell
    severity    : Severity
    labels      : Map<string,string>
    annotations : Map<string,string>
    runbook     : string option }     // optional markdown runbook

[<NoComparison>]
type RuleGroup =
  { id         : string
    name       : string
    intervalMs : int64                // evaluation cadence
    rules      : Rule[]
    createdAt  : DateTimeOffset
    updatedAt  : DateTimeOffset }

[<RequireQualifiedAccess>]
type AlertState = Pending | Firing | Resolved

let alertStateToStr = function
  | AlertState.Pending -> "pending"
  | AlertState.Firing -> "firing"
  | AlertState.Resolved -> "resolved"

[<NoComparison>]
type AlertInstance =
  { fingerprint : string              // stable hash of rule.id + sorted labels
    tenantId    : TenantId
    ruleId      : string
    ruleName    : string
    groupId     : string
    severity    : Severity
    labels      : Map<string,string>  // rule labels merged with series labels
    annotations : Map<string,string>
    value       : float
    state       : AlertState
    activeAt    : int64               // first breach
    firedAt     : int64 option
    resolvedAt  : int64 option
    lastEvalAt  : int64
    runbook     : string option }     // copied from the originating rule (14.1)

// -- JSON -------------------------------------------------------------------

let private writeMap (w : Utf8JsonWriter) (name : string) (m : Map<string,string>) =
  w.WritePropertyName name
  w.WriteStartObject()
  for KeyValue(k, v) in m do w.WriteString(k, v)
  w.WriteEndObject()

let private writeRule (w : Utf8JsonWriter) (r : Rule) =
  w.WriteStartObject()
  w.WriteString("id",          r.id)
  w.WriteString("name",        r.name)
  w.WriteString("lang",        langToStr r.lang)
  w.WriteString("expr",        r.expr)
  w.WriteString("cmp",         cmpToStr r.cmp)
  w.WriteNumber("threshold",   r.threshold)
  w.WriteNumber("forMs",       r.forMs)
  w.WriteString("severity",    severityToStr r.severity)
  writeMap w "labels"      r.labels
  writeMap w "annotations" r.annotations
  match r.runbook with
  | Some rb -> w.WriteString("runbook", rb)
  | None    -> ()
  w.WriteEndObject()

let private writeGroup (w : Utf8JsonWriter) (g : RuleGroup) =
  w.WriteStartObject()
  w.WriteString("id",         g.id)
  w.WriteString("name",       g.name)
  w.WriteNumber("intervalMs", g.intervalMs)
  w.WritePropertyName "rules"
  w.WriteStartArray()
  for r in g.rules do writeRule w r
  w.WriteEndArray()
  w.WriteString("createdAt",  g.createdAt.ToString("o"))
  w.WriteString("updatedAt",  g.updatedAt.ToString("o"))
  w.WriteEndObject()

let serialiseGroup (g : RuleGroup) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeGroup w g)
  Encoding.UTF8.GetString(ms.ToArray())

let serialiseGroups (gs : RuleGroup seq) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for g in gs do writeGroup w g
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let private readStr (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if isNull s then None else Some s
  | _ -> None

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) : int64 =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L
    if v.TryGetInt64 &n then n else dflt
  | _ -> dflt

let private readFloat (el : JsonElement) (name : string) (dflt : float) : float =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable f = 0.0
    if v.TryGetDouble &f then f else dflt
  | _ -> dflt

let private readMap (el : JsonElement) (name : string) : Map<string,string> =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Object ->
    v.EnumerateObject()
    |> Seq.choose (fun p ->
      if p.Value.ValueKind = JsonValueKind.String
      then Some (p.Name, p.Value.GetString())
      else None)
    |> Map.ofSeq
  | _ -> Map.empty

let private parseRule (el : JsonElement) : Rule option =
  match readStr el "name", readStr el "lang", readStr el "expr",
        readStr el "cmp" with
  | Some name, Some langS, Some expr, Some cmpS ->
    match strToLang langS, strToCmp cmpS with
    | Some lang, Some cmp ->
      Some {
        id          = readStr el "id" |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
        name        = name
        lang        = lang
        expr        = expr
        cmp         = cmp
        threshold   = readFloat el "threshold" 0.0
        forMs       = readInt64 el "forMs" 0L
        severity    = readStr el "severity" |> Option.map strToSeverity |> Option.defaultValue Severity.Warning
        labels      = readMap el "labels"
        annotations = readMap el "annotations"
        runbook     = readStr el "runbook" }
    | _ -> None
  | _ -> None

let parseGroup (body : string) : Result<RuleGroup, string> =
  if String.IsNullOrWhiteSpace body then Result.Error "empty body" else
  try
    use doc = JsonDocument.Parse body
    let r = doc.RootElement
    match readStr r "name" with
    | None -> Result.Error "missing 'name'"
    | Some name ->
      let rules =
        match r.TryGetProperty "rules" with
        | true, rs when rs.ValueKind = JsonValueKind.Array ->
          rs.EnumerateArray() |> Seq.choose parseRule |> Array.ofSeq
        | _ -> [||]
      let now = DateTimeOffset.UtcNow
      let readDate name dflt =
        match readStr r name with
        | Some s ->
          let mutable dt = DateTimeOffset.MinValue
          if DateTimeOffset.TryParse(s, &dt) then dt else dflt
        | None -> dflt
      Result.Ok {
        id         = readStr r "id" |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
        name       = name
        intervalMs = max 1_000L (readInt64 r "intervalMs" 15_000L)
        rules      = rules
        createdAt  = readDate "createdAt" now
        updatedAt  = readDate "updatedAt" now }
  with ex -> Result.Error ex.Message

// -- store ------------------------------------------------------------------

type IRuleStore =
  abstract List   : TenantId -> RuleGroup[]
  abstract TryGet : TenantId * string -> RuleGroup option
  abstract Upsert : TenantId * RuleGroup -> unit
  abstract Delete : TenantId * string -> bool

let private sanitize (s : string) =
  let sb = StringBuilder()
  for c in s do
    if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
      sb.Append c |> ignore
    else
      sb.Append '_' |> ignore
  let out = sb.ToString()
  if out.Length = 0 then "_" else out

type FileRuleStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, RuleGroup>()

  let tenantDir (TenantId tid) =
    let d = Path.Combine(root, sanitize tid)
    Directory.CreateDirectory d |> ignore
    d

  let key (TenantId tid) id = sanitize tid + "/" + sanitize id

  let loadDir (tid : TenantId) =
    let dir = tenantDir tid
    Directory.EnumerateFiles(dir, "*.json")
    |> Seq.choose (fun f ->
      try
        match parseGroup (File.ReadAllText f) with
        | Result.Ok g -> Some g
        | Result.Error _ -> None
      with _ -> None)
    |> Seq.toArray

  do
    for sub in Directory.EnumerateDirectories root do
      let tid = TenantId (Path.GetFileName sub)
      for g in loadDir tid do
        cache.[key tid g.id] <- g

  interface IRuleStore with
    member _.List tid =
      let dir = tenantDir tid
      let prefix =
        let (TenantId s) = tid
        sanitize s + "/"
      if not (cache.Keys |> Seq.exists (fun k -> k.StartsWith prefix)) then
        for g in loadDir tid do cache.[key tid g.id] <- g
      cache.Values
      |> Seq.filter (fun g ->
        File.Exists(Path.Combine(dir, sanitize g.id + ".json")))
      |> Seq.sortBy (fun g -> g.name)
      |> Seq.toArray

    member _.TryGet(tid, id) =
      let path = Path.Combine(tenantDir tid, sanitize id + ".json")
      if File.Exists path then
        match parseGroup (File.ReadAllText path) with
        | Result.Ok g -> cache.[key tid g.id] <- g; Some g
        | Result.Error _ -> None
      else None

    member _.Upsert(tid, g) =
      let path = Path.Combine(tenantDir tid, sanitize g.id + ".json")
      let tmp = path + ".tmp"
      File.WriteAllText(tmp, serialiseGroup g)
      if File.Exists path then File.Delete path
      File.Move(tmp, path)
      cache.[key tid g.id] <- g

    member _.Delete(tid, id) =
      let path = Path.Combine(tenantDir tid, sanitize id + ".json")
      cache.TryRemove(key tid id) |> ignore
      if File.Exists path then File.Delete path; true else false

// -- evaluator --------------------------------------------------------------

type IAlertSink =
  abstract OnAlert : AlertInstance -> unit

/// Stable fingerprint for an alert: combines rule id with the sorted
/// label-set hash. Two evaluations of the same rule against the same
/// series collapse onto the same fingerprint so state survives across
/// ticks.
let fingerprint (ruleId : string) (labels : Map<string,string>) : string =
  let sorted =
    labels
    |> Map.toSeq
    |> Seq.sortBy fst
    |> Seq.map (fun (k, v) -> k + "=" + v)
    |> String.concat ","
  let raw = ruleId + "|" + sorted
  use sha = System.Security.Cryptography.SHA1.Create()
  let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes raw)
  let sb = StringBuilder(bytes.Length * 2)
  for b in bytes do sb.AppendFormat("{0:x2}", int b) |> ignore
  sb.ToString().Substring(0, 16)

/// Interpolate Prometheus-style annotation templates against an
/// alert's resolved labels and value. Supports `{{ $labels.<name> }}`
/// (replaced with the label value, or empty when absent) and
/// `{{ $value }}` (the breaching sample value). Whitespace inside the
/// braces is optional. Unknown directives are left untouched so the
/// raw text is still visible rather than silently dropped.
let private labelTemplateRe =
  System.Text.RegularExpressions.Regex(
    @"\{\{\s*\$labels\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}",
    System.Text.RegularExpressions.RegexOptions.Compiled)
let private valueTemplateRe =
  System.Text.RegularExpressions.Regex(
    @"\{\{\s*\$value\s*\}\}",
    System.Text.RegularExpressions.RegexOptions.Compiled)

let templateText (labels : Map<string,string>) (value : float) (text : string) : string =
  if String.IsNullOrEmpty text || not (text.Contains "{{") then text
  else
    let withLabels =
      labelTemplateRe.Replace(text, fun m ->
        match Map.tryFind m.Groups.[1].Value labels with
        | Some v -> v
        | None   -> "")
    valueTemplateRe.Replace(
      withLabels,
      value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))

let templateAnnotations (annotations : Map<string,string>)
                        (labels : Map<string,string>)
                        (value : float) : Map<string,string> =
  annotations |> Map.map (fun _ v -> templateText labels value v)

/// Tenants are resolved by the caller; the evaluator only sees a
/// tenant id and a `(MetricStore, LogStore)` pair. In single-tenant
/// mode the same stores feed every tenant (only `__local__` is ever
/// passed). In multi-tenant mode a per-tenant lookup callback should
/// return the right stores; for now this evaluator runs against the
/// process-wide stores because the embedded metric/log buffers are
/// not yet partitioned by tenant.
type Evaluator(metricStore : MetricStore,
               logStore    : LogStore,
               ruleStore   : IRuleStore,
               sink        : IAlertSink,
               selfMetrics : MetricStore) =

  // (tenantId, fingerprint) -> AlertInstance
  let state = ConcurrentDictionary<string * string, AlertInstance>()

  // Per-group last-eval timestamp so we can skip ticks before the
  // group's interval elapses.
  let lastEval = ConcurrentDictionary<string * string, int64>()  // (tid, groupId) -> ms

  // Optional remote PromQL evaluator. When set (e.g. ingest fans out to
  // Mimir and the in-process MetricStore is empty), PromQL rules are
  // evaluated by delegating the full expression to the backend's
  // instant-query endpoint instead of the embedded engine, so alerts
  // see the same data as the dashboards. The tenant id is passed through
  // so the upstream read is scoped to the same tenant the data was
  // written under (matching the dashboard query proxy).
  let mutable remoteQuery
    : (TenantId -> string -> int64 -> Result<(Map<string,string> * float)[], string>) option = None

  let workerCount = max 2 (Environment.ProcessorCount / 2)

  /// Shard a group across workers by stable hash of groupId.
  let shardOf (groupId : string) =
    let h = (hash groupId &&& 0x7fffffff) % workerCount
    h

  let evalPromRule (tid : TenantId) (rule : Rule) (now : int64) : (Map<string,string> * float)[] =
    // Evaluate the embedded PromQL subset (vector selectors, range
    // functions, aggregations with by/without grouping, scalar/vector
    // arithmetic). Each resulting series whose value breaches the
    // threshold becomes one alert, keyed by its retained labels.
    match remoteQuery with
    | Some q ->
      // Delegate the full expression to the remote backend (e.g. Mimir),
      // then apply the threshold to each returned series.
      match q tid rule.expr now with
      | Result.Ok series ->
        series |> Array.filter (fun (_, v) -> evalCmp rule.cmp v rule.threshold)
      | Result.Error _ -> [||]
    | None ->
      match parsePromExpr rule.expr with
      | Result.Error _ -> [||]
      | Result.Ok expr ->
        match evalAt metricStore now expr with
        | VScalar v ->
          if evalCmp rule.cmp v rule.threshold then [| (Map.empty, v) |] else [||]
        | VVector series ->
          series
          |> Array.choose (fun s ->
            if evalCmp rule.cmp s.value rule.threshold then
              let lbl =
                s.labels
                |> Array.filter (fun (k, _) -> k <> "__name__")
                |> Array.fold (fun m (k, v) -> Map.add k v m) Map.empty
              Some (lbl, s.value)
            else None)

  let evalLogRule (rule : Rule) (now : int64) (interval : int64) : (Map<string,string> * float)[] =
    match parseLogQl rule.expr with
    | Result.Error _ -> [||]
    | Result.Ok q ->
      let cutoff = now - max interval rule.forMs
      let count =
        logStore.Snapshot()
        |> Array.filter (fun e -> e.ts >= cutoff && logMatches q e)
        |> Array.length
        |> float
      if evalCmp rule.cmp count rule.threshold then
        // Use rule labels directly — no series projection on the
        // embedded LogQL surface.
        [| (Map.empty, count) |]
      else [||]

  let evalOne (tid : TenantId) (group : RuleGroup) (rule : Rule) (now : int64) =
    let swStart = System.Diagnostics.Stopwatch.StartNew()
    let hits =
      try
        match rule.lang with
        | PromQL -> evalPromRule tid rule now
        | LogQL  -> evalLogRule  rule now group.intervalMs
      with _ -> [||]
    swStart.Stop()
    let elapsed = float swStart.ElapsedMilliseconds / 1000.0
    selfMetrics.Record(
      "pulse_rule_eval_seconds",
      { ts = now; value = elapsed })

    // Build "currently breaching" fingerprint set.
    let breaching = HashSet<string>()
    for (seriesLabels, value) in hits do
      let mergedLabels =
        rule.labels
        |> Map.fold (fun acc k v -> Map.add k v acc) seriesLabels
        |> Map.add "alertname" rule.name
        |> Map.add "severity"  (severityToStr rule.severity)
      let fp = fingerprint rule.id mergedLabels
      breaching.Add fp |> ignore
      let key = (let (TenantId t) = tid in t, fp)
      match state.TryGetValue key with
      | true, prev ->
        // Transition pending → firing once `forMs` has elapsed.
        let next =
          if prev.state = AlertState.Pending && now - prev.activeAt >= rule.forMs then
            let fired =
              { prev with
                  state = AlertState.Firing
                  firedAt = Some now
                  lastEvalAt = now
                  value = value }
            sink.OnAlert fired
            fired
          else
            { prev with lastEvalAt = now; value = value }
        state.[key] <- next
      | _ ->
        let inst =
          { fingerprint = fp
            tenantId    = tid
            ruleId      = rule.id
            ruleName    = rule.name
            groupId     = group.id
            severity    = rule.severity
            labels      = mergedLabels
            annotations = templateAnnotations rule.annotations mergedLabels value
            runbook     = rule.runbook
            value       = value
            state       =
              if rule.forMs <= 0L then AlertState.Firing else AlertState.Pending
            activeAt    = now
            firedAt     = if rule.forMs <= 0L then Some now else None
            resolvedAt  = None
            lastEvalAt  = now }
        state.[key] <- inst
        if inst.state = AlertState.Firing then sink.OnAlert inst

    // Anything that *was* firing for this rule and is no longer
    // breaching → mark resolved and emit one resolution event.
    let stale =
      state
      |> Seq.filter (fun kv ->
        let (t, fp) = kv.Key
        kv.Value.ruleId = rule.id
        && t = (let (TenantId tt) = tid in tt)
        && not (breaching.Contains fp)
        && kv.Value.state <> AlertState.Resolved)
      |> Seq.toArray
    for kv in stale do
      let resolved =
        { kv.Value with
            state = AlertState.Resolved
            resolvedAt = Some now
            lastEvalAt = now }
      state.[kv.Key] <- resolved
      sink.OnAlert resolved
      // Drop resolved instances after one notification to keep state
      // bounded; recurring breaches will re-create the instance.
      state.TryRemove kv.Key |> ignore

  let tick (workerId : int) (now : int64) (tenants : TenantId[]) =
    for tid in tenants do
      for group in ruleStore.List tid do
        if shardOf group.id = workerId then
          let key = (let (TenantId t) = tid in t, group.id)
          let last =
            match lastEval.TryGetValue key with
            | true, v -> v | _ -> 0L
          if now - last >= group.intervalMs then
            lastEval.[key] <- now
            for r in group.rules do
              evalOne tid group r now

  let cts = new CancellationTokenSource()
  let mutable tenantsProvider : (unit -> TenantId[]) = fun () -> [||]

  member _.SetTenantsProvider(f : unit -> TenantId[]) = tenantsProvider <- f

  /// Delegate PromQL rule evaluation to a remote backend (e.g. Mimir's
  /// instant-query endpoint). When set, `f tenant expr nowMs` must return
  /// one `(labels, value)` pair per result series. Used when ingest fans
  /// out to an upstream store and the in-process MetricStore is empty.
  member _.SetPromQuery(f : TenantId -> string -> int64 -> Result<(Map<string,string> * float)[], string>) =
    remoteQuery <- Some f

  member _.Start() =
    for w in 0 .. workerCount - 1 do
      let workerId = w
      Task.Run(fun () ->
        task {
          while not cts.IsCancellationRequested do
            try
              let now = nowMs ()
              tick workerId now (tenantsProvider ())
            with ex ->
              eprintfn "[rules] worker %d: %s" workerId ex.Message
            do! Task.Delay(1_000, cts.Token)
        } :> Task) |> ignore

  member _.Stop() = cts.Cancel()

  /// Active alert instances visible to a tenant.
  member _.Active(tid : TenantId) =
    let (TenantId t) = tid
    state
    |> Seq.filter (fun kv ->
      let (tt, _) = kv.Key in tt = t
      && kv.Value.state <> AlertState.Resolved)
    |> Seq.map (fun kv -> kv.Value)
    |> Seq.toArray

// -- alert JSON (used by REST + by Routing) ---------------------------------

let writeAlert (w : Utf8JsonWriter) (a : AlertInstance) =
  let (TenantId tid) = a.tenantId
  w.WriteStartObject()
  w.WriteString("fingerprint", a.fingerprint)
  w.WriteString("tenantId",    tid)
  w.WriteString("ruleId",      a.ruleId)
  w.WriteString("ruleName",    a.ruleName)
  w.WriteString("groupId",     a.groupId)
  w.WriteString("severity",    severityToStr a.severity)
  w.WriteString("state",       alertStateToStr a.state)
  w.WriteNumber("value",       a.value)
  w.WriteNumber("activeAt",    a.activeAt)
  match a.firedAt with
  | Some t -> w.WriteNumber("firedAt", t) | None -> ()
  match a.resolvedAt with
  | Some t -> w.WriteNumber("resolvedAt", t) | None -> ()
  w.WriteNumber("lastEvalAt", a.lastEvalAt)
  writeMap w "labels"      a.labels
  writeMap w "annotations" a.annotations
  match a.runbook with
  | Some rb -> w.WriteString("runbook", rb)
  | None    -> ()
  w.WriteEndObject()

let serialiseAlerts (xs : AlertInstance seq) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for a in xs do writeAlert w a
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

// -- REST -------------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 204 -> fun _ -> Suave.Successful.NO_CONTENT
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
    | _   -> INTERNAL_ERROR
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

let webPart (multiTenant : bool)
            (store : IRuleStore)
            (evaluator : Evaluator) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }
  choose [
    GET >=> path "/api/rules" >=>
      withTenant (fun tid -> jsonResp 200 (serialiseGroups (store.List tid)))
    POST >=> path "/api/rules" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseGroup (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid rule group: " + e) ctx
          | Result.Ok g ->
            let withId = { g with id = Guid.NewGuid().ToString "N" }
            store.Upsert(tid, withId)
            return! jsonResp 201 (serialiseGroup withId) ctx
        })
    GET >=> pathScan "/api/rules/%s" (fun id ->
      withTenant (fun tid ->
        match store.TryGet(tid, id) with
        | Some g -> jsonResp 200 (serialiseGroup g)
        | None   -> errJson 404 "no such rule group"))
    PUT >=> pathScan "/api/rules/%s" (fun id ->
      withTenant (fun tid ->
        fun ctx -> async {
          match parseGroup (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid rule group: " + e) ctx
          | Result.Ok g ->
            let existing = store.TryGet(tid, id)
            let stamped =
              { g with
                  id = id
                  createdAt =
                    match existing with
                    | Some prev -> prev.createdAt | None -> g.createdAt
                  updatedAt = DateTimeOffset.UtcNow }
            store.Upsert(tid, stamped)
            return! jsonResp 200 (serialiseGroup stamped) ctx
        }))
    DELETE >=> pathScan "/api/rules/%s" (fun id ->
      withTenant (fun tid ->
        if store.Delete(tid, id) then Suave.Successful.NO_CONTENT
        else errJson 404 "no such rule group"))
    GET >=> path "/api/alerts" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseAlerts (evaluator.Active tid)))
  ]

// -- default seed -----------------------------------------------------------

let private defaultGroup () : RuleGroup =
  let now = DateTimeOffset.UtcNow
  { id         = "default"
    name       = "default"
    intervalMs = 15_000L
    rules =
      [|
        { id          = "cpu-high"
          name        = "cpu-high"
          lang        = PromQL
          expr        = "cpu"
          cmp         = Gt
          threshold   = 0.9
          forMs       = 30_000L
          severity    = Severity.Warning
          labels      = Map.ofList [ "team", "infra" ]
          annotations = Map.ofList [ "summary", "CPU sustained above 90%" ]
          runbook     =
            Some (
              "## CPU high runbook\n\n\
               - [ ] Check the top CPU consumers (`top` / process metrics)\n\
               - [ ] Confirm the spike is not a deploy or batch job\n\
               - [ ] Scale out or shed load if sustained\n\
               - [ ] Page the service owner if unresolved after 15m") }
      |]
    createdAt = now
    updatedAt = now }

let seedIfEmpty (store : IRuleStore) (tid : TenantId) =
  let existing = store.List tid
  if existing.Length = 0 then store.Upsert(tid, defaultGroup ())
