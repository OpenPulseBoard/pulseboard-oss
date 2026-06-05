module PulseBoard.StatusPages

// PLAN-NEXT 14.6 — Built-in public status pages.
//
// Per workspace an operator defines one or more "status pages", each carrying
// a list of components and (optionally) scheduled maintenance windows. A
// component reflects the health of one thing customers care about and is
// backed by either:
//
//   * a synthetic check (14.8) — resolved to its `pulse_synthetic_up` series, or
//   * a raw metric series selector + comparison — so any existing telemetry can
//     drive the page (e.g. `pulse_slo_ingest_success_ratio_5m >= 0.99`).
//
// The public surface is unauthenticated and lives at:
//
//   GET /api/public/status            → live JSON for the default (first) page
//   GET /api/public/status/<slug>     → live JSON for a named page
//   GET /status[, /status/<slug>]     → the self-contained status.html viewer
//
// Live JSON folds in uptime history (averaged over the available window),
// current incidents auto-derived from active/firing alert groups, and active
// or upcoming maintenance windows. Operators administer pages through the
// authenticated CRUD surface under /api/status/pages.

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

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- model ------------------------------------------------------------------

/// What backs a component's health signal.
type CompSource =
  /// A 14.8 synthetic check id; resolved to `pulse_synthetic_up{check="<name>"`.
  | Synthetic of checkId : string
  /// A raw metric series selector with a comparison: "up" iff cmp(last, thr).
  | Metric of selector : string * cmp : string * threshold : float

[<NoComparison>]
type StatusComponent =
  { id          : string
    name        : string
    description : string
    source      : CompSource }

[<NoComparison>]
type Maintenance =
  { id       : string
    title    : string
    body     : string
    startsAt : int64
    endsAt   : int64 }   // 0 => open-ended

[<NoComparison>]
type StatusPage =
  { id           : string
    slug         : string
    title        : string
    description  : string
    components   : StatusComponent list
    maintenances : Maintenance list
    createdAt    : int64
    updatedAt    : int64 }

let validCmps = [ ">"; ">="; "<"; "<="; "=="; "!=" ]

let private slugOk (s : string) =
  s.Length > 0 && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')

/// Validate the user-supplied shape of a page; returns it unchanged or an
/// error message.
let validate (p : StatusPage) : Result<StatusPage, string> =
  if String.IsNullOrWhiteSpace p.slug then Result.Error "slug is required"
  elif not (slugOk p.slug) then
    Result.Error "slug must contain only letters, digits, '-' or '_'"
  elif String.IsNullOrWhiteSpace p.title then Result.Error "title is required"
  else
    let compErr =
      p.components
      |> List.tryPick (fun c ->
        if String.IsNullOrWhiteSpace c.name then Some "component name is required"
        else
          match c.source with
          | Synthetic id when String.IsNullOrWhiteSpace id ->
            Some "synthetic component needs a checkId"
          | Metric (sel, _, _) when String.IsNullOrWhiteSpace sel ->
            Some "metric component needs a selector"
          | Metric (_, cmp, _) when not (List.contains cmp validCmps) ->
            Some (sprintf "invalid comparison '%s'" cmp)
          | _ -> None)
    match compErr with
    | Some e -> Result.Error e
    | None   -> Result.Ok p

// -- JSON codec -------------------------------------------------------------

let private writeComp (w : Utf8JsonWriter) (c : StatusComponent) =
  w.WriteStartObject()
  w.WriteString("id",          c.id)
  w.WriteString("name",        c.name)
  w.WriteString("description", c.description)
  (match c.source with
   | Synthetic id ->
     w.WriteString("sourceKind", "synthetic")
     w.WriteString("checkId",    id)
   | Metric (sel, cmp, thr) ->
     w.WriteString("sourceKind", "metric")
     w.WriteString("selector",   sel)
     w.WriteString("cmp",        cmp)
     w.WriteNumber("threshold",  thr))
  w.WriteEndObject()

let private writeMaint (w : Utf8JsonWriter) (m : Maintenance) =
  w.WriteStartObject()
  w.WriteString("id",       m.id)
  w.WriteString("title",    m.title)
  w.WriteString("body",     m.body)
  w.WriteNumber("startsAt", m.startsAt)
  w.WriteNumber("endsAt",   m.endsAt)
  w.WriteEndObject()

let private writePage (w : Utf8JsonWriter) (p : StatusPage) =
  w.WriteStartObject()
  w.WriteString("id",          p.id)
  w.WriteString("slug",        p.slug)
  w.WriteString("title",       p.title)
  w.WriteString("description", p.description)
  w.WritePropertyName "components"
  w.WriteStartArray()
  for c in p.components do writeComp w c
  w.WriteEndArray()
  w.WritePropertyName "maintenances"
  w.WriteStartArray()
  for m in p.maintenances do writeMaint w m
  w.WriteEndArray()
  w.WriteNumber("createdAt", p.createdAt)
  w.WriteNumber("updatedAt", p.updatedAt)
  w.WriteEndObject()

let serialisePage (p : StatusPage) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writePage w p)
  Encoding.UTF8.GetString(ms.ToArray())

let serialisePages (ps : StatusPage[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for p in ps do writePage w p
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let private readStr (el : JsonElement) (name : string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
  | _ -> None

let private strOr el name dflt = readStr el name |> Option.defaultValue dflt

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L in (if v.TryGetInt64 &n then n else dflt)
  | _ -> dflt

let private readFloat (el : JsonElement) (name : string) (dflt : float) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0.0 in (if v.TryGetDouble &n then n else dflt)
  | _ -> dflt

let private parseComp (el : JsonElement) : StatusComponent =
  let source =
    match (strOr el "sourceKind" "metric").ToLowerInvariant() with
    | "synthetic" -> Synthetic (strOr el "checkId" "")
    | _ -> Metric (strOr el "selector" "", strOr el "cmp" ">=", readFloat el "threshold" 0.5)
  { id          = strOr el "id" (Guid.NewGuid().ToString "N")
    name        = strOr el "name" ""
    description = strOr el "description" ""
    source      = source }

let private parseMaint (el : JsonElement) : Maintenance =
  { id       = strOr el "id" (Guid.NewGuid().ToString "N")
    title    = strOr el "title" ""
    body     = strOr el "body" ""
    startsAt = readInt64 el "startsAt" 0L
    endsAt   = readInt64 el "endsAt" 0L }

let private readArray (el : JsonElement) (name : string) (f : JsonElement -> 'a) : 'a list =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Array ->
    [ for item in v.EnumerateArray() -> f item ]
  | _ -> []

/// Parse a stored page document back into a record (preserves all fields).
let parsePage (line : string) : StatusPage option =
  try
    use doc = JsonDocument.Parse line
    let r = doc.RootElement
    match readStr r "id", readStr r "slug" with
    | Some id, Some slug when id.Length > 0 && slug.Length > 0 ->
      Some {
        id           = id
        slug         = slug
        title        = strOr r "title" ""
        description  = strOr r "description" ""
        components   = readArray r "components" parseComp
        maintenances = readArray r "maintenances" parseMaint
        createdAt    = readInt64 r "createdAt" 0L
        updatedAt    = readInt64 r "updatedAt" 0L }
    | _ -> None
  with _ -> None

/// Parse an inbound portal request, assigning id/timestamps. When `existing`
/// is supplied (PUT-style upsert) its id, slug fallback, and createdAt are
/// preserved.
let parseRequest (existing : StatusPage option) (body : string) : Result<StatusPage, string> =
  try
    use doc = JsonDocument.Parse body
    let r = doc.RootElement
    let now = nowMs ()
    let p =
      { id           = existing |> Option.map (fun e -> e.id)
                                |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
        slug         = strOr r "slug"
                         (existing |> Option.map (fun e -> e.slug) |> Option.defaultValue "")
        title        = strOr r "title" ""
        description  = strOr r "description" ""
        components   = readArray r "components" parseComp
        maintenances = readArray r "maintenances" parseMaint
        createdAt    = existing |> Option.map (fun e -> e.createdAt) |> Option.defaultValue now
        updatedAt    = now }
    validate p
  with ex -> Result.Error ("invalid body: " + ex.Message)

// -- store ------------------------------------------------------------------

type IStatusStore =
  abstract List   : TenantId -> StatusPage[]
  abstract TryGet : TenantId * string -> StatusPage option
  abstract Upsert : TenantId * StatusPage -> unit
  abstract Delete : TenantId * string -> bool

let private sanitize (s : string) =
  let invalid = Path.GetInvalidFileNameChars()
  String(s.ToCharArray() |> Array.map (fun c -> if Array.contains c invalid then '_' else c))

type FileStatusStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, ConcurrentDictionary<string, StatusPage>>()
  let sync  = obj ()
  let tenantDir (TenantId t) =
    let d = Path.Combine(root, sanitize t)
    Directory.CreateDirectory d |> ignore
    d
  let bucket (TenantId t as tid) =
    cache.GetOrAdd(t, fun _ ->
      let m = ConcurrentDictionary<string, StatusPage>()
      let dir = tenantDir tid
      if Directory.Exists dir then
        for f in Directory.EnumerateFiles(dir, "*.json") do
          try
            match parsePage (File.ReadAllText f) with
            | Some p -> m.[p.id] <- p
            | None -> ()
          with _ -> ()
      m)
  interface IStatusStore with
    member _.List tid =
      (bucket tid).Values |> Seq.sortBy (fun p -> p.title) |> Seq.toArray
    member _.TryGet(tid, id) =
      match (bucket tid).TryGetValue id with
      | true, p -> Some p
      | _ -> None
    member _.Upsert(tid, p) =
      lock sync (fun () ->
        (bucket tid).[p.id] <- p
        let path = Path.Combine(tenantDir tid, sanitize p.id + ".json")
        let tmp = path + ".tmp"
        File.WriteAllText(tmp, serialisePage p)
        if File.Exists path then File.Delete path
        File.Move(tmp, path))
    member _.Delete(tid, id) =
      lock sync (fun () ->
        let removed = (bucket tid).TryRemove id |> fst
        let path = Path.Combine(tenantDir tid, sanitize id + ".json")
        if File.Exists path then (try File.Delete path with _ -> ())
        removed)

// -- live status ------------------------------------------------------------

let private esc (v : string) =
  v.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// A firing alert projected into the shape the public page needs, decoupling
/// this module from the Rules types.
[<NoComparison>]
type Incident =
  { title    : string
    severity : string
    summary  : string
    since    : int64
    labels   : Map<string, string> }

let private cmpOp (cmp : string) (v : float) (thr : float) =
  match cmp with
  | ">"  -> v > thr  | ">=" -> v >= thr
  | "<"  -> v < thr  | "<=" -> v <= thr
  | "==" -> v = thr  | "!=" -> v <> thr
  | _    -> v >= thr

/// Resolve a component to a (series-selector, cmp, threshold). Synthetic
/// checks become the `pulse_synthetic_up` series for the check's name; the
/// "up" condition is value >= 0.5.
let private resolveSource (checkNameOf : string -> string option)
                          (c : StatusComponent) : (string * string * float) option =
  match c.source with
  | Synthetic checkId ->
    match checkNameOf checkId with
    | Some name -> Some (sprintf "pulse_synthetic_up{check=\"%s\"" (esc name), ">=", 0.5)
    | None      -> None
  | Metric (sel, cmp, thr) -> Some (sel, cmp, thr)

/// Find the series whose name equals the selector, or — for bare metric names
/// and the synthetic prefix — begins with it.
let private findSeries (metrics : MetricStore) (selector : string) : string option =
  let names = metrics.Names()
  match names |> Array.tryFind (fun n -> n = selector) with
  | Some n -> Some n
  | None   -> names |> Array.tryFind (fun n -> n.StartsWith selector)

/// Compute (up, uptimeRatio) for a component over the window, or (None, None)
/// when no data backs it yet.
let private compState (metrics : MetricStore)
                      (checkNameOf : string -> string option)
                      (sinceMs : int64)
                      (c : StatusComponent) : bool option * float option =
  match resolveSource checkNameOf c with
  | None -> None, None
  | Some (selector, cmp, thr) ->
    match findSeries metrics selector with
    | None -> None, None
    | Some seriesName ->
      let pts = metrics.GetSince(seriesName, sinceMs)
      if pts.Length = 0 then None, None
      else
        let last    = pts.[pts.Length - 1]
        let up      = cmpOp cmp last.value thr
        let upCount = pts |> Array.filter (fun p -> cmpOp cmp p.value thr) |> Array.length
        Some up, Some (float upCount / float pts.Length)

/// Render the live public JSON for a page: per-component status + uptime,
/// auto-derived incidents, and active/upcoming maintenance windows.
let renderLive (metrics     : MetricStore)
               (checkNameOf : string -> string option)
               (incidents   : Incident[])
               (windowMs    : int64)
               (page        : StatusPage) : string =
  let now     = nowMs ()
  let sinceMs = now - windowMs
  let states  =
    page.components |> List.map (fun c -> c, compState metrics checkNameOf sinceMs c)
  let anyDown = states |> List.exists (fun (_, (up, _)) -> up = Some false)
  let anyUp   = states |> List.exists (fun (_, (up, _)) -> up = Some true)
  let allDownOrUnknown = states |> List.forall (fun (_, (up, _)) -> up <> Some true)
  let overall =
    if anyDown && allDownOrUnknown then "major_outage"
    elif anyDown || incidents.Length > 0 then "degraded"
    elif anyUp then "operational"
    else "unknown"
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("slug",        page.slug)
    w.WriteString("title",       page.title)
    w.WriteString("description", page.description)
    w.WriteString("status",      overall)
    w.WriteNumber("generatedAt", now)
    w.WriteNumber("windowMs",    windowMs)
    w.WritePropertyName "components"
    w.WriteStartArray()
    for (c, (up, uptime)) in states do
      w.WriteStartObject()
      w.WriteString("name",        c.name)
      w.WriteString("description", c.description)
      (match up with
       | Some true  -> w.WriteString("status", "operational")
       | Some false -> w.WriteString("status", "down")
       | None       -> w.WriteString("status", "unknown"))
      (match uptime with
       | Some u -> w.WriteNumber("uptime", u)
       | None   -> w.WriteNull("uptime"))
      w.WriteEndObject()
    w.WriteEndArray()
    w.WritePropertyName "incidents"
    w.WriteStartArray()
    for i in incidents do
      w.WriteStartObject()
      w.WriteString("title",    i.title)
      w.WriteString("severity", i.severity)
      w.WriteString("summary",  i.summary)
      w.WriteNumber("since",    i.since)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WritePropertyName "maintenances"
    w.WriteStartArray()
    for m in page.maintenances |> List.filter (fun m -> m.endsAt = 0L || m.endsAt >= now) do
      w.WriteStartObject()
      w.WriteString("title",    m.title)
      w.WriteString("body",     m.body)
      w.WriteNumber("startsAt", m.startsAt)
      w.WriteNumber("endsAt",   m.endsAt)
      let active = m.startsAt <= now && (m.endsAt = 0L || m.endsAt >= now)
      w.WriteBoolean("active",  active)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject())
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

/// Authenticated CRUD surface for status pages (mounted under the query scope).
/// `previewOf` renders the live JSON for an operator preview.
let webPart (multiTenant : bool)
            (store       : IStatusStore)
            (previewOf   : TenantId -> StatusPage -> string) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }

  choose [
    GET >=> path "/api/status/pages" >=>
      withTenant (fun tid -> jsonResp 200 (serialisePages (store.List tid)))

    POST >=> path "/api/status/pages" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseRequest None (readBody ctx.request) with
          | Result.Error msg -> return! errJson 400 msg ctx
          | Result.Ok p ->
            store.Upsert(tid, p)
            return! jsonResp 201 (serialisePage p) ctx
        })

    GET >=> pathScan "/api/status/pages/%s/preview" (fun id ->
      withTenant (fun tid ->
        match store.TryGet(tid, id) with
        | Some p -> jsonResp 200 (previewOf tid p)
        | None   -> errJson 404 "no such page"))

    PUT >=> pathScan "/api/status/pages/%s" (fun id ->
      withTenant (fun tid ->
        fun ctx -> async {
          match store.TryGet(tid, id) with
          | None -> return! errJson 404 "no such page" ctx
          | Some existing ->
            match parseRequest (Some existing) (readBody ctx.request) with
            | Result.Error msg -> return! errJson 400 msg ctx
            | Result.Ok p ->
              store.Upsert(tid, p)
              return! jsonResp 200 (serialisePage p) ctx
        }))

    GET >=> pathScan "/api/status/pages/%s" (fun id ->
      withTenant (fun tid ->
        match store.TryGet(tid, id) with
        | Some p -> jsonResp 200 (serialisePage p)
        | None   -> errJson 404 "no such page"))

    DELETE >=> pathScan "/api/status/pages/%s" (fun id ->
      withTenant (fun tid ->
        if store.Delete(tid, id) then jsonResp 200 """{"deleted":true}"""
        else errJson 404 "no such page"))
  ]

/// Unauthenticated public surface: live JSON + the status.html viewer. Mounted
/// BEFORE the auth-gated `query` so `/api/public/status*` is not intercepted.
let publicWebPart (wwwroot       : string)
                  (resolveBySlug : string -> (TenantId * StatusPage) option)
                  (defaultPage   : unit -> (TenantId * StatusPage) option)
                  (liveOf        : TenantId -> StatusPage -> string) : WebPart =
  choose [
    GET >=> path "/api/public/status" >=>
      (fun ctx -> async {
        match defaultPage () with
        | Some (tid, p) -> return! jsonResp 200 (liveOf tid p) ctx
        | None          -> return! errJson 404 "no status page configured" ctx
      })

    GET >=> pathScan "/api/public/status/%s" (fun slug ->
      fun ctx -> async {
        match resolveBySlug slug with
        | Some (tid, p) -> return! jsonResp 200 (liveOf tid p) ctx
        | None          -> return! errJson 404 "no such status page" ctx
      })

    GET >=> path "/status" >=> Suave.Files.browseFile wwwroot "status.html"
    GET >=> pathScan "/status/%s" (fun _ -> Suave.Files.browseFile wwwroot "status.html")
  ]
