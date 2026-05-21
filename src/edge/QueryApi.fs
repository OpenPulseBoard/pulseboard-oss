module PulseBoard.QueryApi

open System
open System.Globalization
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.RegularExpressions
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.Writers
open PulseBoard.TimeSeries
open PulseBoard.Rollups

// Phase 4 step 1: speak the Prometheus and Loki HTTP query APIs.
//
// Two modes per pillar:
//
//   * Proxy mode -- when `--mimir-url=` / `--loki-url=` is wired the
//     edge forwards every PromQL / LogQL request verbatim to the
//     upstream's `/prometheus/api/v1/*` / `/loki/api/v1/*` surface.
//     Multi-tenant deployments inject the resolved tenant id via the
//     configured org header.
//
//   * Embedded mode -- when no cloud URL is set we serve a *minimal*
//     subset directly out of the in-process MetricStore / LogStore /
//     RollupStore so dashboards and the explore view have something
//     to talk to in the single-binary OSS deployment.
//
//     Embedded PromQL supports vector selectors only:
//
//         metric_name
//         metric_name{label="val", other!~"regex"}
//
//     i.e. no functions (`rate`, `sum`, `avg`, ...), no binary ops,
//     no subqueries. Anything we don't recognise short-circuits with
//     a 422 / `errorType:"bad_data"` payload that nudges the operator
//     toward `--mimir-url=` for full PromQL.
//
//     Embedded LogQL supports stream selectors with at most one
//     line filter:
//
//         {service="app"}                            -- stream only
//         {service="app", level="error"} |= "panic"  -- substring
//         {service="app"} != "noisy"                 -- exclude
//
// The native `/api/metrics`, `/api/metrics/<n>`, `/api/logs`
// endpoints in `Query.fs` are untouched -- they remain the
// PulseBoard-native DSL surface.

// ---------------------------------------------------------------------
// Shared HTTP plumbing
// ---------------------------------------------------------------------

let private http =
  let h = new HttpClientHandler()
  h.AutomaticDecompression <-
    System.Net.DecompressionMethods.GZip ||| System.Net.DecompressionMethods.Deflate
  new HttpClient(h, Timeout = TimeSpan.FromSeconds 60.0)

// ---------------------------------------------------------------------
// JSON helpers (we hand-format the Prometheus/Loki envelopes so we
// can keep float formatting and `__name__` / label ordering exactly
// matching the reference servers).
// ---------------------------------------------------------------------

let private JsonString (s : string) : string =
  if isNull s then "null"
  else
    let sb = StringBuilder(s.Length + 8)
    sb.Append '"' |> ignore
    for c in s do
      match c with
      | '"'  -> sb.Append "\\\"" |> ignore
      | '\\' -> sb.Append "\\\\" |> ignore
      | '\n' -> sb.Append "\\n"  |> ignore
      | '\r' -> sb.Append "\\r"  |> ignore
      | '\t' -> sb.Append "\\t"  |> ignore
      | c when c < ' ' ->
        sb.AppendFormat("\\u{0:x4}", int c) |> ignore
      | c    -> sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

let private ok (s : string) : WebPart =
  OK s >=> setMimeType "application/json"

/// `errJson code errorType msg` — Prometheus-style error envelope.
let private errJson (code : string -> WebPart)
                    (errorType : string) (msg : string) : WebPart =
  let body =
    sprintf """{"status":"error","errorType":%s,"error":%s}"""
      (JsonString errorType) (JsonString msg)
  code body >=> setMimeType "application/json"

let private formatFloat (v : float) : string =
  if Double.IsNaN v                 then "NaN"
  elif Double.IsPositiveInfinity v  then "+Inf"
  elif Double.IsNegativeInfinity v  then "-Inf"
  else v.ToString("R", CultureInfo.InvariantCulture)

let private labelsJson (pairs : (string * string)[]) : string =
  let sb = StringBuilder()
  sb.Append '{' |> ignore
  let mutable first = true
  for (k, v) in pairs do
    if not first then sb.Append ',' |> ignore
    first <- false
    sb.Append(JsonString k) |> ignore
    sb.Append ':' |> ignore
    sb.Append(JsonString v) |> ignore
  sb.Append '}' |> ignore
  sb.ToString()

// ---------------------------------------------------------------------
// Series-name <-> label parsing
// ---------------------------------------------------------------------
//
// PulseBoard stores series as `cpu` or `cpu{host="a",region="b"}`.
// The Prometheus / Loki APIs deal with explicit label maps, so we
// translate at the boundary.

let parseSeriesName (full : string) : (string * string)[] =
  if isNull full then [||]
  else
    let brace = full.IndexOf '{'
    if brace < 0 then
      [| "__name__", full |]
    else
      let metric = full.Substring(0, brace).Trim()
      let inner =
        let s = full.Substring(brace + 1)
        if s.EndsWith "}" then s.Substring(0, s.Length - 1) else s
      let pairs = ResizeArray<string * string>()
      pairs.Add("__name__", metric)
      let sb = StringBuilder()
      let mutable i = 0
      while i < inner.Length do
        // name
        sb.Clear() |> ignore
        while i < inner.Length && inner.[i] <> '=' do
          sb.Append inner.[i] |> ignore
          i <- i + 1
        let name = sb.ToString().Trim()
        if i < inner.Length then i <- i + 1
        if i < inner.Length && inner.[i] = '"' then i <- i + 1
        // value
        sb.Clear() |> ignore
        let mutable closed = false
        while i < inner.Length && not closed do
          let c = inner.[i]
          if c = '\\' && i + 1 < inner.Length then
            sb.Append inner.[i + 1] |> ignore
            i <- i + 2
          elif c = '"' then
            closed <- true
            i <- i + 1
          else
            sb.Append c |> ignore
            i <- i + 1
        let value = sb.ToString()
        if name.Length > 0 then pairs.Add(name, value)
        // skip , and whitespace
        while i < inner.Length
              && (inner.[i] = ',' || Char.IsWhiteSpace inner.[i]) do
          i <- i + 1
      pairs.ToArray()

// ---------------------------------------------------------------------
// PromQL vector selector parser
// ---------------------------------------------------------------------

type MatchOp =
  | Eq
  | Neq
  | Re
  | NotRe

type LabelMatcher =
  { name : string
    op   : MatchOp
    value : string
    re   : Regex option }     // compiled once for Re / NotRe

type VectorSelector =
  { name     : string option   // __name__ when given outside { }
    matchers : LabelMatcher[] }

let private isNameStart c = Char.IsLetter c || c = '_' || c = ':'
let private isNameCont  c = Char.IsLetterOrDigit c || c = '_' || c = ':'

/// Parse a Prometheus vector selector. Returns Result.Error with a short
/// description when the expression uses anything beyond the supported
/// subset (functions, aggregations, binary ops, durations, ...).
let parseVectorSelector (expr : string) : Result<VectorSelector, string> =
  let s = if isNull expr then "" else expr.Trim()
  if s.Length = 0 then Result.Error "empty query"
  else
    // Reject obvious unsupported syntax up front so we surface a
    // clear "use Mimir for full PromQL" hint rather than mis-parsing.
    let unsupported = [| "("; ")"; "["; "]"; "+"; "-"; "*"; "/"; "%"; " or "; " and "; " unless " |]
    let lowered = " " + s.ToLowerInvariant() + " "
    let badIdx =
      unsupported
      |> Array.tryFind (fun tok ->
          if tok.StartsWith " " then lowered.Contains tok
          else s.Contains tok)
    match badIdx with
    | Some tok ->
      Result.Error (sprintf "embedded PromQL supports vector selectors only (got %s); use --mimir-url= for full PromQL" (tok.Trim()))
    | None ->
      let mutable i = 0
      // Optional name.
      let nameBuf = StringBuilder()
      if i < s.Length && isNameStart s.[i] then
        while i < s.Length && isNameCont s.[i] do
          nameBuf.Append s.[i] |> ignore
          i <- i + 1
      let name =
        if nameBuf.Length = 0 then None else Some (nameBuf.ToString())
      // skip whitespace
      while i < s.Length && Char.IsWhiteSpace s.[i] do i <- i + 1
      let matchers = ResizeArray<LabelMatcher>()
      if i < s.Length && s.[i] = '{' then
        i <- i + 1
        let mutable ok = true
        let mutable closed = false
        while ok && not closed && i < s.Length do
          while i < s.Length && Char.IsWhiteSpace s.[i] do i <- i + 1
          if i < s.Length && s.[i] = '}' then
            closed <- true
            i <- i + 1
          else
            // name
            let nm = StringBuilder()
            while i < s.Length && isNameCont s.[i] do
              nm.Append s.[i] |> ignore
              i <- i + 1
            while i < s.Length && Char.IsWhiteSpace s.[i] do i <- i + 1
            // op
            let op =
              if i + 1 < s.Length && s.[i] = '=' && s.[i + 1] = '~' then
                i <- i + 2; Some Re
              elif i + 1 < s.Length && s.[i] = '!' && s.[i + 1] = '~' then
                i <- i + 2; Some NotRe
              elif i + 1 < s.Length && s.[i] = '!' && s.[i + 1] = '=' then
                i <- i + 2; Some Neq
              elif i < s.Length && s.[i] = '=' then
                i <- i + 1; Some Eq
              else None
            match op with
            | None -> ok <- false
            | Some op ->
              while i < s.Length && Char.IsWhiteSpace s.[i] do i <- i + 1
              if i >= s.Length || s.[i] <> '"' then ok <- false
              else
                i <- i + 1
                let v = StringBuilder()
                let mutable cls = false
                while i < s.Length && not cls do
                  let c = s.[i]
                  if c = '\\' && i + 1 < s.Length then
                    v.Append s.[i + 1] |> ignore
                    i <- i + 2
                  elif c = '"' then
                    cls <- true; i <- i + 1
                  else
                    v.Append c |> ignore; i <- i + 1
                if not cls then ok <- false
                else
                  let value = v.ToString()
                  let re =
                    match op with
                    | Re | NotRe ->
                      try Some (Regex("^" + value + "$", RegexOptions.CultureInvariant))
                      with _ -> None
                    | _ -> None
                  matchers.Add(
                    { name = nm.ToString(); op = op; value = value; re = re })
              // skip , and whitespace
              while i < s.Length && (s.[i] = ',' || Char.IsWhiteSpace s.[i]) do
                i <- i + 1
        if not ok then Result.Error "malformed label matcher"
        elif not closed then Result.Error "unterminated label set"
        else Result.Ok { name = name; matchers = matchers.ToArray() }
      elif name.IsSome then
        Result.Ok { name = name; matchers = [||] }
      else
        Result.Error "expected metric name or '{'"

let private matchOne (m : LabelMatcher) (actual : string) : bool =
  match m.op with
  | Eq    -> actual = m.value
  | Neq   -> actual <> m.value
  | Re    -> match m.re with Some r -> r.IsMatch actual | None -> false
  | NotRe -> match m.re with Some r -> not (r.IsMatch actual) | None -> false

/// True when the series identified by `seriesLabels` satisfies the
/// selector. Missing labels match the empty string (Prometheus rule).
let matchesSelector (sel : VectorSelector)
                    (seriesLabels : (string * string)[]) : bool =
  let getLabel n =
    seriesLabels
    |> Array.tryFind (fun (k, _) -> k = n)
    |> Option.map snd
    |> Option.defaultValue ""
  let nameOk =
    match sel.name with
    | None   -> true
    | Some n -> getLabel "__name__" = n
  nameOk
  && sel.matchers |> Array.forall (fun m -> matchOne m (getLabel m.name))

// ---------------------------------------------------------------------
// Query-string helpers
// ---------------------------------------------------------------------

let private qp (ctx : HttpContext) (k : string) : string option =
  match ctx.request.queryParam k with
  | Choice1Of2 v -> Some v
  | _            -> None

let private allValues (ctx : HttpContext) (k : string) : string[] =
  ctx.request.query
  |> List.choose (fun (n, v) ->
      if n = k then v else None)
  |> List.toArray

/// Prometheus accepts time as float seconds (with sub-second precision)
/// or an RFC3339 string. We accept the former.
let private parseTimeSec (s : string) : int64 option =
  match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
  | true, v -> Some (int64 (v * 1000.0))
  | _       ->
    match DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
    | true, dto -> Some (dto.ToUnixTimeMilliseconds())
    | _         -> None

/// Loki times are int64 unix nanoseconds.
let private parseTimeNs (s : string) : int64 option =
  match Int64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
  | true, ns -> Some (ns / 1_000_000L)
  | _        ->
    match DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
    | true, dto -> Some (dto.ToUnixTimeMilliseconds())
    | _         -> None

/// `step` is either a float number of seconds or a duration string
/// like `30s`, `1m`, `1h`.
let private parseStepMs (s : string) : int64 option =
  if isNull s || s.Length = 0 then None
  else
    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v when v > 0.0 -> Some (int64 (v * 1000.0))
    | _ ->
      let m = Regex.Match(s, @"^(\d+)(ms|s|m|h|d)$")
      if not m.Success then None
      else
        let n = Int64.Parse(m.Groups.[1].Value, CultureInfo.InvariantCulture)
        let mult =
          match m.Groups.[2].Value with
          | "ms" -> 1L
          | "s"  -> 1_000L
          | "m"  -> 60_000L
          | "h"  -> 3_600_000L
          | "d"  -> 86_400_000L
          | _    -> 0L
        if mult > 0L then Some (n * mult) else None

// ---------------------------------------------------------------------
// Proxy helper
// ---------------------------------------------------------------------

/// Forward the current request to `<baseUrl>/<suffix>?<rawQuery>`,
/// preserving method and (for POST) the body. Adds the configured
/// org header when a TenantCtx is attached.
let private proxy (baseUrl : string) (orgHeader : string option)
                  (bearer : string option) (suffix : string) : WebPart =
  fun ctx -> async {
    try
      let q = ctx.request.rawQuery
      let url =
        let basePart = baseUrl.TrimEnd '/'
        let suff     = suffix.TrimStart '/'
        if String.IsNullOrEmpty q then sprintf "%s/%s" basePart suff
        else sprintf "%s/%s?%s" basePart suff q
      let methodStr = ctx.request.``method``.ToString().ToUpperInvariant()
      let httpMethod =
        match methodStr with
        | "GET"  -> HttpMethod.Get
        | "POST" -> HttpMethod.Post
        | _      -> HttpMethod.Get
      use req = new HttpRequestMessage(httpMethod, url)
      if httpMethod = HttpMethod.Post then
        let body = ctx.request.rawForm
        let ct =
          ctx.request.headers
          |> Seq.tryFind (fun (k, _) ->
              String.Equals(k, "content-type", StringComparison.OrdinalIgnoreCase))
          |> Option.map snd
          |> Option.defaultValue "application/x-www-form-urlencoded"
        req.Content <- new ByteArrayContent(body)
        req.Content.Headers.ContentType <- MediaTypeHeaderValue.Parse ct
      // Tenant header (only when present and a TenantCtx is attached).
      match orgHeader, PulseBoard.Rbac.tryGetTenant ctx with
      | Some h, Some t ->
        let (PulseBoard.Tenancy.TenantId tid) = t.tenant.id
        req.Headers.TryAddWithoutValidation(h, tid) |> ignore
      | _ -> ()
      match bearer with
      | Some t ->
        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", t)
      | None -> ()
      use! resp = http.SendAsync(req) |> Async.AwaitTask
      let! payload = resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
      let bodyStr = Encoding.UTF8.GetString payload
      let contentType =
        match resp.Content.Headers.ContentType with
        | null -> "application/json"
        | ct   -> ct.ToString()
      // Suave 3.4 exposes a finite set of status-code helpers; map
      // upstream's StatusCode onto the closest equivalent so the
      // dashboard sees a non-200 when the backend is unhappy.
      let writer : string -> WebPart =
        match int resp.StatusCode with
        | 200 -> OK
        | 201 -> Suave.Successful.CREATED
        | 204 -> fun _ -> Suave.Successful.NO_CONTENT
        | 400 | 422 -> BAD_REQUEST
        | 401 -> Suave.RequestErrors.UNAUTHORIZED
        | 403 -> Suave.RequestErrors.FORBIDDEN
        | 404 -> NOT_FOUND
        | 409 -> Suave.RequestErrors.CONFLICT
        | 429 -> Suave.RequestErrors.TOO_MANY_REQUESTS
        | _   -> Suave.ServerErrors.INTERNAL_ERROR
      return! (writer bodyStr >=> setMimeType contentType) ctx
    with ex ->
      return!
        errJson Suave.ServerErrors.INTERNAL_ERROR
                "internal" (sprintf "upstream error: %s" ex.Message) ctx
  }

// ---------------------------------------------------------------------
// Embedded Prometheus query / query_range
// ---------------------------------------------------------------------

let private collectMatchingSeries
              (metricStore : MetricStore)
              (sel : VectorSelector) : (string * (string * string)[])[] =
  metricStore.Names()
  |> Array.choose (fun n ->
      let labels = parseSeriesName n
      if matchesSelector sel labels then Some (n, labels) else None)

let private vectorResponse
              (entries : ((string * string)[] * Point) seq) : string =
  let sb = StringBuilder()
  sb.Append """{"status":"success","data":{"resultType":"vector","result":[""" |> ignore
  let mutable first = true
  for (labels, p) in entries do
    if not first then sb.Append ',' |> ignore
    first <- false
    sb.Append "{\"metric\":" |> ignore
    sb.Append (labelsJson labels) |> ignore
    sb.Append ",\"value\":[" |> ignore
    sb.AppendFormat(CultureInfo.InvariantCulture, "{0}", float p.ts / 1000.0)
      |> ignore
    sb.Append ',' |> ignore
    sb.Append (JsonString (formatFloat p.value)) |> ignore
    sb.Append "]}" |> ignore
  sb.Append "]}}" |> ignore
  sb.ToString()

let private matrixResponse
              (entries : ((string * string)[] * Point[]) seq) : string =
  let sb = StringBuilder()
  sb.Append """{"status":"success","data":{"resultType":"matrix","result":[""" |> ignore
  let mutable first = true
  for (labels, pts) in entries do
    if not first then sb.Append ',' |> ignore
    first <- false
    sb.Append "{\"metric\":" |> ignore
    sb.Append (labelsJson labels) |> ignore
    sb.Append ",\"values\":[" |> ignore
    let mutable pf = true
    for p in pts do
      if not pf then sb.Append ',' |> ignore
      pf <- false
      sb.Append '[' |> ignore
      sb.AppendFormat(CultureInfo.InvariantCulture, "{0}", float p.ts / 1000.0)
        |> ignore
      sb.Append ',' |> ignore
      sb.Append (JsonString (formatFloat p.value)) |> ignore
      sb.Append ']' |> ignore
    sb.Append "]}" |> ignore
  sb.Append "]}}" |> ignore
  sb.ToString()

let private promQueryEmbedded
              (metricStore : MetricStore) : WebPart =
  fun ctx -> async {
    let query = qp ctx "query" |> Option.defaultValue ""
    let timeMs =
      qp ctx "time"
      |> Option.bind parseTimeSec
      |> Option.defaultValue (nowMs ())
    match parseVectorSelector query with
    | Result.Error msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | Result.Ok sel ->
      let series = collectMatchingSeries metricStore sel
      // Instant vector = the most recent sample <= time per series.
      let results =
        series
        |> Array.choose (fun (n, labels) ->
            let pts = metricStore.Get n
            let eligible = pts |> Array.filter (fun p -> p.ts <= timeMs)
            if eligible.Length = 0 then None
            else Some (labels, eligible.[eligible.Length - 1]))
      return! ok (vectorResponse results) ctx
  }

let private promQueryRangeEmbedded
              (metricStore : MetricStore)
              (rollupStore : RollupStore option) : WebPart =
  fun ctx -> async {
    let query = qp ctx "query" |> Option.defaultValue ""
    let startMs =
      qp ctx "start"
      |> Option.bind parseTimeSec
      |> Option.defaultValue (nowMs () - 3_600_000L)
    let endMs =
      qp ctx "end"
      |> Option.bind parseTimeSec
      |> Option.defaultValue (nowMs ())
    let stepMs =
      qp ctx "step"
      |> Option.bind parseStepMs
      |> Option.defaultValue 15_000L
    match parseVectorSelector query with
    | Result.Error msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | Result.Ok sel ->
      // Prefer rollups whose resolution matches the requested step.
      let useRollup =
        rollupStore
        |> Option.bind (fun rs ->
            tryParseResolutionMs stepMs |> Option.map (fun r -> rs, r))
      let series = collectMatchingSeries metricStore sel
      let results =
        series
        |> Array.map (fun (n, labels) ->
            let pts =
              match useRollup with
              | Some (rs, res) ->
                rs.GetSinceAgg(n, res.Ms, startMs, Agg.Avg)
                |> Array.filter (fun p -> p.ts <= endMs)
              | None ->
                metricStore.GetSince(n, startMs)
                |> Array.filter (fun p -> p.ts <= endMs)
            labels, pts)
        |> Array.filter (fun (_, pts) -> pts.Length > 0)
      return! ok (matrixResponse results) ctx
  }

let private promLabelsEmbedded (metricStore : MetricStore) : WebPart =
  fun ctx -> async {
    let labels =
      metricStore.Names()
      |> Array.collect (parseSeriesName >> Array.map fst)
      |> Array.distinct
      |> Array.sort
    let sb = StringBuilder()
    sb.Append """{"status":"success","data":[""" |> ignore
    let mutable first = true
    for l in labels do
      if not first then sb.Append ',' |> ignore
      first <- false
      sb.Append (JsonString l) |> ignore
    sb.Append "]}" |> ignore
    return! ok (sb.ToString()) ctx
  }

let private promLabelValuesEmbedded (metricStore : MetricStore) : WebPart =
  pathScan "/api/prom/api/v1/label/%s/values" (fun name ->
    fun ctx -> async {
      let values =
        metricStore.Names()
        |> Array.choose (fun n ->
            parseSeriesName n
            |> Array.tryFind (fun (k, _) -> k = name)
            |> Option.map snd)
        |> Array.distinct
        |> Array.sort
      let sb = StringBuilder()
      sb.Append """{"status":"success","data":[""" |> ignore
      let mutable first = true
      for v in values do
        if not first then sb.Append ',' |> ignore
        first <- false
        sb.Append (JsonString v) |> ignore
      sb.Append "]}" |> ignore
      return! ok (sb.ToString()) ctx
    })

let private promSeriesEmbedded (metricStore : MetricStore) : WebPart =
  fun ctx -> async {
    let matches = allValues ctx "match[]"
    let selectors =
      matches
      |> Array.map parseVectorSelector
    let firstErr = selectors |> Array.tryPick (function Result.Error e -> Some e | _ -> None)
    match firstErr with
    | Some msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | None ->
      let sels = selectors |> Array.choose (function Result.Ok s -> Some s | _ -> None)
      let matching =
        metricStore.Names()
        |> Array.choose (fun n ->
            let labels = parseSeriesName n
            let hit =
              if sels.Length = 0 then true
              else sels |> Array.exists (fun s -> matchesSelector s labels)
            if hit then Some labels else None)
      let sb = StringBuilder()
      sb.Append """{"status":"success","data":[""" |> ignore
      let mutable first = true
      for labels in matching do
        if not first then sb.Append ',' |> ignore
        first <- false
        sb.Append (labelsJson labels) |> ignore
      sb.Append "]}" |> ignore
      return! ok (sb.ToString()) ctx
  }

// ---------------------------------------------------------------------
// Embedded LogQL
// ---------------------------------------------------------------------

type LogQuery =
  { matchers : LabelMatcher[]
    /// Line filter: Some (true, "needle") means `|= needle`,
    /// Some (false, "needle") means `!= needle`, None means no filter.
    lineFilter : (bool * string) option }

let parseLogQl (expr : string) : Result<LogQuery, string> =
  let s = if isNull expr then "" else expr.Trim()
  if s.Length = 0 || s.[0] <> '{' then
    Result.Error "embedded LogQL requires a stream selector starting with '{'"
  else
    let close = s.IndexOf '}'
    if close < 0 then Result.Error "unterminated stream selector"
    else
      let selector = s.Substring(0, close + 1)   // {…}
      let trailer  = s.Substring(close + 1).Trim()
      // Reuse the PromQL selector parser by injecting a dummy name.
      match parseVectorSelector ("__log__" + selector) with
      | Result.Error e -> Result.Error e
      | Result.Ok sel ->
        let matchers =
          sel.matchers |> Array.filter (fun m -> m.name <> "__name__")
        // Optional `|= "needle"` or `!= "needle"`.
        let lineFilter =
          if trailer.Length = 0 then Result.Ok None
          else
            let m = Regex.Match(trailer, @"^(\|=|!=)\s*""((?:[^""\\]|\\.)*)""\s*$")
            if m.Success then
              let neg = m.Groups.[1].Value = "!="
              Result.Ok (Some ((not neg), m.Groups.[2].Value))
            else
              Result.Error (sprintf "embedded LogQL only supports a single |= or != line filter, got %s" trailer)
        match lineFilter with
        | Result.Error e -> Result.Error e
        | Result.Ok lf   -> Result.Ok { matchers = matchers; lineFilter = lf }

let logMatches (q : LogQuery) (e : LogEntry) : bool =
  let getLabel = function
    | "service" -> e.service
    | "level"   -> e.level
    | _         -> ""
  let labelsOk =
    q.matchers |> Array.forall (fun m -> matchOne m (getLabel m.name))
  let lineOk =
    match q.lineFilter with
    | None                  -> true
    | Some (true,  needle)  -> e.message.Contains needle
    | Some (false, needle)  -> not (e.message.Contains needle)
  labelsOk && lineOk

let private streamsResponse (entries : LogEntry seq) (limit : int) : string =
  // Group by (service, level) to produce Loki streams.
  let groups =
    entries
    |> Seq.groupBy (fun e -> e.service, e.level)
    |> Seq.map (fun ((svc, lvl), es) ->
        let arr =
          es |> Seq.sortByDescending (fun e -> e.ts) |> Seq.truncate limit |> Seq.toArray
        (svc, lvl, arr))
  let sb = StringBuilder()
  sb.Append """{"status":"success","data":{"resultType":"streams","result":[""" |> ignore
  let mutable first = true
  for (svc, lvl, arr) in groups do
    if not first then sb.Append ',' |> ignore
    first <- false
    sb.Append "{\"stream\":" |> ignore
    sb.Append (labelsJson [| "service", svc; "level", lvl |]) |> ignore
    sb.Append ",\"values\":[" |> ignore
    let mutable vf = true
    for e in arr do
      if not vf then sb.Append ',' |> ignore
      vf <- false
      sb.Append '[' |> ignore
      sb.Append (JsonString (string (int64 e.ts * 1_000_000L))) |> ignore
      sb.Append ',' |> ignore
      sb.Append (JsonString e.message) |> ignore
      sb.Append ']' |> ignore
    sb.Append "]}" |> ignore
  sb.Append "]}}" |> ignore
  sb.ToString()

let private lokiQueryRangeEmbedded (logStore : LogStore) : WebPart =
  fun ctx -> async {
    let query = qp ctx "query" |> Option.defaultValue ""
    let startMs =
      qp ctx "start"
      |> Option.bind parseTimeNs
      |> Option.defaultValue (nowMs () - 3_600_000L)
    let endMs =
      qp ctx "end"
      |> Option.bind parseTimeNs
      |> Option.defaultValue (nowMs ())
    let limit =
      qp ctx "limit"
      |> Option.bind (fun v ->
          match Int32.TryParse v with true, n when n > 0 -> Some n | _ -> None)
      |> Option.defaultValue 100
    match parseLogQl query with
    | Result.Error msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | Result.Ok q ->
      let entries =
        logStore.Snapshot()
        |> Array.filter (fun e ->
            e.ts >= startMs && e.ts <= endMs && logMatches q e)
      return! ok (streamsResponse entries limit) ctx
  }

let private lokiLabelsEmbedded : WebPart =
  fun ctx -> async {
    return!
      ok """{"status":"success","data":["service","level"]}""" ctx
  }

let private lokiLabelValuesEmbedded (logStore : LogStore) : WebPart =
  pathScan "/api/loki/api/v1/label/%s/values" (fun name ->
    fun ctx -> async {
      let snap = logStore.Snapshot()
      let values =
        match name with
        | "service" ->
          snap |> Array.map (fun e -> e.service) |> Array.distinct |> Array.sort
        | "level"   ->
          snap |> Array.map (fun e -> e.level)   |> Array.distinct |> Array.sort
        | _         -> [||]
      let sb = StringBuilder()
      sb.Append """{"status":"success","data":[""" |> ignore
      let mutable first = true
      for v in values do
        if not first then sb.Append ',' |> ignore
        first <- false
        sb.Append (JsonString v) |> ignore
      sb.Append "]}" |> ignore
      return! ok (sb.ToString()) ctx
    })

// ---------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------

/// Upstream wiring captured at startup.
type Upstream =
  { /// Base URL, e.g. `https://mimir.internal`. We append
    /// `/prometheus/api/v1/...` / `/loki/api/v1/...` ourselves.
    baseUrl   : string
    orgHeader : string option
    bearer    : string option }

/// Prometheus-compatible routes. When `upstream` is `Some` every
/// request is forwarded; when `None` we serve the embedded subset.
let promRoutes (upstream : Upstream option)
               (metricStore : MetricStore)
               (rollupStore : RollupStore option) : WebPart =
  let prefix = "/api/prom/api/v1"
  match upstream with
  | Some u ->
    let fwd suffix =
      proxy u.baseUrl u.orgHeader u.bearer ("/prometheus/api/v1/" + suffix)
    choose [
      path (prefix + "/query")        >=> fwd "query"
      path (prefix + "/query_range")  >=> fwd "query_range"
      path (prefix + "/labels")       >=> fwd "labels"
      pathScan "/api/prom/api/v1/label/%s/values"
        (fun n -> fwd ("label/" + n + "/values"))
      path (prefix + "/series")       >=> fwd "series"
      path (prefix + "/metadata")     >=> fwd "metadata"
    ]
  | None ->
    choose [
      GET >=> path (prefix + "/query")
          >=> promQueryEmbedded metricStore
      GET >=> path (prefix + "/query_range")
          >=> promQueryRangeEmbedded metricStore rollupStore
      GET >=> path (prefix + "/labels")
          >=> promLabelsEmbedded metricStore
      GET >=> promLabelValuesEmbedded metricStore
      GET >=> path (prefix + "/series")
          >=> promSeriesEmbedded metricStore
    ]

/// Loki-compatible routes.
let lokiRoutes (upstream : Upstream option)
               (logStore : LogStore) : WebPart =
  let prefix = "/api/loki/api/v1"
  match upstream with
  | Some u ->
    let fwd suffix =
      proxy u.baseUrl u.orgHeader u.bearer ("/loki/api/v1/" + suffix)
    choose [
      path (prefix + "/query")        >=> fwd "query"
      path (prefix + "/query_range")  >=> fwd "query_range"
      path (prefix + "/labels")       >=> fwd "labels"
      pathScan "/api/loki/api/v1/label/%s/values"
        (fun n -> fwd ("label/" + n + "/values"))
      path (prefix + "/series")       >=> fwd "series"
    ]
  | None ->
    choose [
      GET >=> path (prefix + "/query_range")
          >=> lokiQueryRangeEmbedded logStore
      GET >=> path (prefix + "/labels") >=> lokiLabelsEmbedded
      GET >=> lokiLabelValuesEmbedded logStore
    ]

let webPart (promUpstream : Upstream option)
            (lokiUpstream : Upstream option)
            (metricStore : MetricStore)
            (rollupStore : RollupStore option)
            (logStore : LogStore) : WebPart =
  choose [
    promRoutes promUpstream metricStore rollupStore
    lokiRoutes lokiUpstream logStore
  ]
