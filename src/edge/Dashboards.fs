module PulseBoard.Dashboards

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open System.Threading
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy

// In-house dashboards (PLAN.md Phase 4 step 2). One JSON document per
// dashboard, keyed by (tenantId, dashId). Tenants are isolated at the
// store layer: every CRUD handler reads `tryGetTenant ctx` and only
// touches its own slice. In single-tenant mode all dashboards live
// under the synthetic `__local__` tenant.
//
// Persistence is a flat directory of JSON files under
// `<dataDir>/dashboards/<tenantId>/<dashId>.json`. We never embed
// tenant ids in the file body and never let callers set them — the
// server stamps the active tenant on every write. That keeps the
// model identical between single- and multi-tenant deployments and
// means an operator can `cp` a tenant's dashboards between hosts
// without rewriting anything.
//
// The body is opaque enough that we can grow panel options without a
// migration: panels carry a `type` discriminator and a freeform
// string-map `options` for type-specific knobs. The frontend is the
// source of truth for which option keys each panel type honours.

// -- model ------------------------------------------------------------------

[<NoComparison>]
type Panel =
  { id        : string
    title     : string
    /// "timeseries" | "stat" | "logs" | "table"
    panelType : string
    /// "promql" | "logql" | "native"
    queryLang : string
    expr      : string
    /// 12-column grid coords. (0,0) is top-left. w/h in grid units.
    x         : int
    y         : int
    w         : int
    h         : int
    options   : Map<string, string> }

[<NoComparison>]
type Dashboard =
  { id           : string
    title        : string
    /// Default lookback window (seconds). Picker can override at view time.
    timeRangeSec : int
    /// Auto-refresh interval (seconds). 0 = no refresh (live-WS or manual).
    refreshSec   : int
    panels       : Panel array
    createdAt    : DateTimeOffset
    updatedAt    : DateTimeOffset }

// -- JSON ------------------------------------------------------------------

let private jopts =
  let o = JsonSerializerOptions(WriteIndented = false)
  o

let private writePanel (w : Utf8JsonWriter) (p : Panel) =
  w.WriteStartObject()
  w.WriteString("id", p.id)
  w.WriteString("title", p.title)
  w.WriteString("type", p.panelType)
  w.WriteString("queryLang", p.queryLang)
  w.WriteString("expr", p.expr)
  w.WriteNumber("x", p.x)
  w.WriteNumber("y", p.y)
  w.WriteNumber("w", p.w)
  w.WriteNumber("h", p.h)
  w.WritePropertyName "options"
  w.WriteStartObject()
  for KeyValue(k, v) in p.options do w.WriteString(k, v)
  w.WriteEndObject()
  w.WriteEndObject()

let private writeDashboard (w : Utf8JsonWriter) (d : Dashboard) =
  w.WriteStartObject()
  w.WriteString("id", d.id)
  w.WriteString("title", d.title)
  w.WriteNumber("timeRangeSec", d.timeRangeSec)
  w.WriteNumber("refreshSec", d.refreshSec)
  w.WritePropertyName "panels"
  w.WriteStartArray()
  for p in d.panels do writePanel w p
  w.WriteEndArray()
  w.WriteString("createdAt", d.createdAt.ToString("o"))
  w.WriteString("updatedAt", d.updatedAt.ToString("o"))
  w.WriteEndObject()

let serialiseDashboard (d : Dashboard) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeDashboard w d
  )
  Encoding.UTF8.GetString(ms.ToArray())

let private serialiseList (ds : Dashboard seq) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for d in ds do writeDashboard w d
    w.WriteEndArray()
  )
  Encoding.UTF8.GetString(ms.ToArray())

let private readString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if isNull s then None else Some s
  | _ -> None

let private readInt (el : JsonElement) (name : string) (dflt : int) : int =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable i = 0
    if v.TryGetInt32 &i then i else dflt
  | _ -> dflt

let private readOptions (el : JsonElement) : Map<string,string> =
  match el.TryGetProperty "options" with
  | true, v when v.ValueKind = JsonValueKind.Object ->
    v.EnumerateObject()
    |> Seq.choose (fun p ->
      match p.Value.ValueKind with
      | JsonValueKind.String -> Some (p.Name, p.Value.GetString())
      | JsonValueKind.Number -> Some (p.Name, p.Value.GetRawText())
      | JsonValueKind.True   -> Some (p.Name, "true")
      | JsonValueKind.False  -> Some (p.Name, "false")
      | _ -> None)
    |> Map.ofSeq
  | _ -> Map.empty

let private parsePanel (el : JsonElement) : Panel option =
  match readString el "id", readString el "title", readString el "type",
        readString el "queryLang", readString el "expr" with
  | Some id, Some title, Some pt, Some lang, Some expr ->
    Some {
      id = id
      title = title
      panelType = pt
      queryLang = lang
      expr = expr
      x = readInt el "x" 0
      y = readInt el "y" 0
      w = max 1 (readInt el "w" 4)
      h = max 1 (readInt el "h" 3)
      options = readOptions el }
  | _ -> None

let parseDashboard (body : string) : Result<Dashboard, string> =
  if String.IsNullOrWhiteSpace body then
    Result.Error "empty body"
  else
    try
      use doc = JsonDocument.Parse body
      let r = doc.RootElement
      match readString r "title" with
      | None -> Result.Error "missing 'title'"
      | Some title ->
        let id =
          readString r "id"
          |> Option.defaultWith (fun () -> Guid.NewGuid().ToString("N"))
        let panels =
          match r.TryGetProperty "panels" with
          | true, ps when ps.ValueKind = JsonValueKind.Array ->
            ps.EnumerateArray()
            |> Seq.choose parsePanel
            |> Array.ofSeq
          | _ -> [||]
        let now = DateTimeOffset.UtcNow
        let readDate name dflt =
          match readString r name with
          | Some s ->
            let mutable dt = DateTimeOffset.MinValue
            if DateTimeOffset.TryParse(s, &dt) then dt else dflt
          | None -> dflt
        Result.Ok {
          id = id
          title = title
          timeRangeSec = max 60 (readInt r "timeRangeSec" 3600)
          refreshSec   = max 0  (readInt r "refreshSec"   30)
          panels = panels
          createdAt = readDate "createdAt" now
          updatedAt = readDate "updatedAt" now }
    with ex -> Result.Error ex.Message

// -- repo ------------------------------------------------------------------

type IDashboardRepo =
  abstract List   : TenantId -> Dashboard array
  abstract TryGet : TenantId * string -> Dashboard option
  abstract Upsert : TenantId * Dashboard -> unit
  abstract Delete : TenantId * string -> bool

/// On-disk JSON repo. Tenant slugs are not URL-decoded into paths;
/// we sanitise to a safe filename to keep traversal out of reach.
type FileDashboardRepo(root : string) =
  let sanitize (s : string) =
    let sb = StringBuilder()
    for c in s do
      if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
        sb.Append c |> ignore
      else
        sb.Append '_' |> ignore
    let out = sb.ToString()
    if out.Length = 0 then "_" else out

  let tenantDir (TenantId tid) =
    let d = Path.Combine(root, sanitize tid)
    Directory.CreateDirectory d |> ignore
    d

  let cache = ConcurrentDictionary<string, Dashboard>()
  let key (TenantId tid) id = sanitize tid + "/" + sanitize id

  let load (tid : TenantId) =
    let dir = tenantDir tid
    Directory.EnumerateFiles(dir, "*.json")
    |> Seq.choose (fun f ->
      try
        let body = File.ReadAllText f
        match parseDashboard body with
        | Result.Ok d -> Some d
        | Result.Error _ -> None
      with _ -> None)
    |> Seq.toArray

  do
    Directory.CreateDirectory root |> ignore
    // Eagerly warm the cache for known tenants by walking the root.
    for sub in Directory.EnumerateDirectories root do
      let synthetic = TenantId (Path.GetFileName sub)
      for d in load synthetic do
        cache.[key synthetic d.id] <- d

  interface IDashboardRepo with

    member _.List (tid : TenantId) =
      // Fall back to re-reading on first access for a previously
      // unseen tenant id (e.g. one created mid-process).
      let dir = tenantDir tid
      if not (cache.Keys |> Seq.exists (fun k -> k.StartsWith(sanitize (let (TenantId s) = tid in s) + "/"))) then
        for d in load tid do
          cache.[key tid d.id] <- d
      cache.Values
      |> Seq.filter (fun d ->
        // Only return dashboards from this tenant's folder.
        File.Exists(Path.Combine(dir, sanitize d.id + ".json")))
      |> Seq.sortBy (fun d -> d.title)
      |> Seq.toArray

    member _.TryGet (tid, id) =
      let path = Path.Combine(tenantDir tid, sanitize id + ".json")
      if File.Exists path then
        match parseDashboard (File.ReadAllText path) with
        | Result.Ok d ->
          cache.[key tid d.id] <- d
          Some d
        | Result.Error _ -> None
      else None

    member _.Upsert (tid, d) =
      let path = Path.Combine(tenantDir tid, sanitize d.id + ".json")
      let body = serialiseDashboard d
      // Atomic-ish write: dump to .tmp then move into place.
      let tmp = path + ".tmp"
      File.WriteAllText(tmp, body)
      if File.Exists path then File.Delete path
      File.Move(tmp, path)
      cache.[key tid d.id] <- d

    member _.Delete (tid, id) =
      let path = Path.Combine(tenantDir tid, sanitize id + ".json")
      cache.TryRemove(key tid id) |> ignore
      if File.Exists path then
        File.Delete path
        true
      else false

// -- seed -------------------------------------------------------------------

/// Default dashboard auto-created for a tenant that has zero saved
/// dashboards. Mirrors the legacy single-page demo: a metrics overview,
/// an alerts/stat strip, and a logs panel. Operators are expected to
/// edit / replace it from the UI.
let private defaultDashboard () : Dashboard =
  let now = DateTimeOffset.UtcNow
  let mkPanel id title pt lang expr x y w h opts =
    { id        = id
      title     = title
      panelType = pt
      queryLang = lang
      expr      = expr
      x = x; y = y; w = w; h = h
      options   = Map.ofList opts }
  { id           = "overview"
    title        = "Overview"
    timeRangeSec = 3600
    refreshSec   = 15
    panels =
      [|
        mkPanel "p-cpu" "CPU load"       "timeseries" "native" "cpu_usage"
                0 0 6 3 [ "unit", "ratio" ]
        mkPanel "p-req" "HTTP requests"  "timeseries" "promql" "http_requests_total"
                6 0 6 3 [ "unit", "rps" ]
        mkPanel "p-alerts" "Active alerts" "stat"     "native" "__alerts.firing"
                0 3 4 2 []
        mkPanel "p-mem" "Memory used"    "stat"      "native" "system.disk.used"
                4 3 4 2 [ "unit", "bytes" ]
        mkPanel "p-svc" "Service health" "stat"      "native" "__listeners.up"
                8 3 4 2 []
        mkPanel "p-logs" "Recent logs"   "logs"      "logql"  "{service=~\".+\"}"
                0 5 12 4 [ "tail", "200" ]
      |]
    createdAt = now
    updatedAt = now }

/// Synthetic tenant id used when running in single-tenant mode (no
/// real tenancy bound to requests). Kept stable so a process restart
/// reuses the same on-disk folder.
let singleTenantId = TenantId "__local__"

/// Make sure every known tenant has at least one dashboard.
let seedIfEmpty (repo : IDashboardRepo) (tid : TenantId) =
  let existing = repo.List tid
  if existing.Length = 0 then
    repo.Upsert(tid, defaultDashboard ())

// -- WebPart ----------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK
    | 201 -> Suave.Successful.CREATED
    | 204 -> fun _ -> Suave.Successful.NO_CONTENT
    | 400 -> BAD_REQUEST
    | 404 -> NOT_FOUND
    | 409 -> Suave.RequestErrors.CONFLICT
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private resolveTenant (multiTenant : bool) (ctx : HttpContext) : TenantId option =
  if multiTenant then
    match PulseBoard.Rbac.tryGetTenant ctx with
    | Some t -> Some t.tenant.id
    | None   -> None
  else
    Some singleTenantId

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

/// Build the public REST surface. `multiTenant` controls whether the
/// tenant id is sourced from the auth context (`true`) or pinned to
/// `singleTenantId` (`false`). Both flavours auto-seed an overview
/// dashboard the first time the active tenant is observed.
let webPart (multiTenant : bool) (repo : IDashboardRepo) : WebPart =

  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant in request" ctx
      | Some tid ->
        seedIfEmpty repo tid
        return! handler tid ctx
    }

  let listAll : WebPart =
    withTenant (fun tid ->
      let xs = repo.List tid
      jsonResp 200 (serialiseList xs))

  let getOne (id : string) : WebPart =
    withTenant (fun tid ->
      match repo.TryGet(tid, id) with
      | Some d -> jsonResp 200 (serialiseDashboard d)
      | None   -> errJson 404 "no such dashboard")

  let createOne : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        match parseDashboard (readBody ctx.request) with
        | Result.Error msg -> return! errJson 400 ("invalid dashboard: " + msg) ctx
        | Result.Ok d ->
          // Force a fresh id so create != update.
          let withId = { d with id = Guid.NewGuid().ToString("N") }
          repo.Upsert(tid, withId)
          return! jsonResp 201 (serialiseDashboard withId) ctx
      })

  let updateOne (id : string) : WebPart =
    withTenant (fun tid ->
      fun ctx -> async {
        match parseDashboard (readBody ctx.request) with
        | Result.Error msg -> return! errJson 400 ("invalid dashboard: " + msg) ctx
        | Result.Ok d ->
          let existing = repo.TryGet(tid, id)
          let stamped =
            { d with
                id = id
                createdAt =
                  match existing with
                  | Some prev -> prev.createdAt
                  | None      -> d.createdAt
                updatedAt = DateTimeOffset.UtcNow }
          repo.Upsert(tid, stamped)
          return! jsonResp 200 (serialiseDashboard stamped) ctx
      })

  let deleteOne (id : string) : WebPart =
    withTenant (fun tid ->
      if repo.Delete(tid, id) then
        Suave.Successful.NO_CONTENT
      else
        errJson 404 "no such dashboard")

  choose [
    GET    >=> path    "/api/dashboards"          >=> listAll
    POST   >=> path    "/api/dashboards"          >=> createOne
    GET    >=> pathScan "/api/dashboards/%s"      getOne
    PUT    >=> pathScan "/api/dashboards/%s"      updateOne
    DELETE >=> pathScan "/api/dashboards/%s"      deleteOne
  ]
