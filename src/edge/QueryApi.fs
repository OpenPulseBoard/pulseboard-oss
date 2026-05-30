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
    // Scan with quoted strings (e.g. regex matcher values like ".+")
    // stripped out, so operators inside quotes don't trigger false
    // positives.
    let stripQuoted (input : string) : string =
      let sb = StringBuilder()
      let mutable j = 0
      while j < input.Length do
        let c = input.[j]
        if c = '"' then
          j <- j + 1
          while j < input.Length && input.[j] <> '"' do
            if input.[j] = '\\' && j + 1 < input.Length then j <- j + 2
            else j <- j + 1
          if j < input.Length then j <- j + 1   // skip closing quote
        else
          sb.Append c |> ignore
          j <- j + 1
      sb.ToString()
    let scanSrc = stripQuoted s
    let unsupported = [| "("; ")"; "["; "]"; "+"; "-"; "*"; "/"; "%"; " or "; " and "; " unless " |]
    let lowered = " " + scanSrc.ToLowerInvariant() + " "
    let badIdx =
      unsupported
      |> Array.tryFind (fun tok ->
          if tok.StartsWith " " then lowered.Contains tok
          else scanSrc.Contains tok)
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

// =====================================================================
// Embedded PromQL — small expression evaluator
// =====================================================================
//
// Beyond the bare vector selectors that `parseVectorSelector` accepts
// (used by the Prometheus /series endpoint and by the LogQL parser),
// `/api/prom/api/v1/query` and `/query_range` accept a tiny PromQL
// subset just rich enough to drive the bundled dashboards:
//
//   * scalar literals, parens, unary `-`, unary `+`
//   * binary `+`  `-`  `*`  `/`  `%`
//     - scalar⊗scalar -> scalar
//     - scalar⊗vector / vector⊗scalar -> per-sample
//     - vector⊗vector -> 1:1 matching on every label except `__name__`
//   * vector selectors:        foo        foo{a="b", c!~"d"}
//   * range selectors:         foo[5m]    (only as a range-fn arg)
//   * range functions:         rate, irate, increase
//   * aggregations:            sum, avg, min, max, count
//     (no `by` / `without` grouping — the result has no labels)
//
// Anything outside this subset still short-circuits with the same
// "use --mimir-url= for full PromQL" hint.

type PromOp = PoAdd | PoSub | PoMul | PoDiv | PoMod

type PromExpr =
  | PeNum   of float
  | PeNeg   of PromExpr
  | PeSel   of VectorSelector
  | PeRange of VectorSelector * int64       // window in ms
  | PeCall  of string * PromExpr             // function name (lower-case), arg
  | PeBin   of PromOp * PromExpr * PromExpr

let private parsePromDurationMs (raw : string) : int64 option =
  let m = Regex.Match(raw, @"^(\d+)(ms|s|m|h|d|w)$")
  if not m.Success then None
  else
    let n = int64 m.Groups.[1].Value
    let mult =
      match m.Groups.[2].Value with
      | "ms" -> 1L | "s" -> 1_000L | "m" -> 60_000L
      | "h"  -> 3_600_000L | "d" -> 86_400_000L | "w" -> 604_800_000L
      | _    -> 0L
    if mult > 0L then Some (n * mult) else None

/// Recursive-descent parser for the embedded PromQL subset above.
let parsePromExpr (input : string) : Result<PromExpr, string> =
  let s = if isNull input then "" else input
  let mutable i = 0
  let skipWs () =
    while i < s.Length && Char.IsWhiteSpace s.[i] do i <- i + 1
  let eatChar (c : char) =
    skipWs ()
    if i < s.Length && s.[i] = c then i <- i + 1; true else false
  let isIdStart c = Char.IsLetter c || c = '_' || c = ':'
  let isIdCont  c = Char.IsLetterOrDigit c || c = '_' || c = ':'
  let readIdent () =
    skipWs ()
    let start = i
    if i < s.Length && isIdStart s.[i] then
      i <- i + 1
      while i < s.Length && isIdCont s.[i] do i <- i + 1
      Some (s.Substring(start, i - start))
    else None
  let readNumber () =
    skipWs ()
    let start = i
    let mutable sawDigit = false
    let mutable sawDot   = false
    while i < s.Length && (Char.IsDigit s.[i] || (s.[i] = '.' && not sawDot)) do
      if s.[i] = '.' then sawDot <- true else sawDigit <- true
      i <- i + 1
    if i < s.Length && (s.[i] = 'e' || s.[i] = 'E') then
      i <- i + 1
      if i < s.Length && (s.[i] = '+' || s.[i] = '-') then i <- i + 1
      while i < s.Length && Char.IsDigit s.[i] do i <- i + 1
    if sawDigit then
      let raw = s.Substring(start, i - start)
      match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
      | true, v -> Some v
      | _       -> None
    else
      i <- start; None
  let readBraceGroup () : Result<string, string> option =
    skipWs ()
    if i < s.Length && s.[i] = '{' then
      let start = i + 1
      let mutable depth = 1
      i <- i + 1
      while depth > 0 && i < s.Length do
        let c = s.[i]
        if c = '"' then
          i <- i + 1
          while i < s.Length && s.[i] <> '"' do
            if s.[i] = '\\' && i + 1 < s.Length then i <- i + 2
            else i <- i + 1
          if i < s.Length then i <- i + 1
        elif c = '{' then depth <- depth + 1; i <- i + 1
        elif c = '}' then depth <- depth - 1; if depth > 0 then i <- i + 1
        else i <- i + 1
      if depth <> 0 then Some (Result.Error "unterminated '{'")
      else
        let inner = s.Substring(start, i - start)
        i <- i + 1
        Some (Result.Ok inner)
    else None
  let readBracketDuration () : Result<int64, string> option =
    skipWs ()
    if i < s.Length && s.[i] = '[' then
      i <- i + 1
      let start = i
      while i < s.Length && s.[i] <> ']' do i <- i + 1
      if i >= s.Length then Some (Result.Error "unterminated '['")
      else
        let raw = s.Substring(start, i - start).Trim()
        i <- i + 1
        match parsePromDurationMs raw with
        | Some ms -> Some (Result.Ok ms)
        | None    -> Some (Result.Error (sprintf "invalid duration '%s'" raw))
    else None
  let buildSelector (name : string) (innerOpt : string option) : Result<VectorSelector, string> =
    let withBraces =
      match innerOpt with
      | None       -> name
      | Some inner -> name + "{" + inner + "}"
    parseVectorSelector withBraces

  let aggFns   = Set.ofList [ "sum"; "avg"; "min"; "max"; "count" ]
  let rangeFns = Set.ofList [ "rate"; "irate"; "increase" ]

  let rec parseExpr () = parseAddSub ()
  and parseAddSub () =
    let mutable left = parseMulDiv ()
    let mutable keep = true
    while keep do
      match left with
      | Result.Error _ -> keep <- false
      | Result.Ok l ->
        skipWs ()
        if i < s.Length && (s.[i] = '+' || s.[i] = '-') then
          let op = if s.[i] = '+' then PoAdd else PoSub
          i <- i + 1
          match parseMulDiv () with
          | Result.Error e -> left <- Result.Error e; keep <- false
          | Result.Ok r    -> left <- Result.Ok (PeBin (op, l, r))
        else keep <- false
    left
  and parseMulDiv () =
    let mutable left = parseUnary ()
    let mutable keep = true
    while keep do
      match left with
      | Result.Error _ -> keep <- false
      | Result.Ok l ->
        skipWs ()
        if i < s.Length && (s.[i] = '*' || s.[i] = '/' || s.[i] = '%') then
          let op =
            match s.[i] with
            | '*' -> PoMul
            | '/' -> PoDiv
            | _   -> PoMod
          i <- i + 1
          match parseUnary () with
          | Result.Error e -> left <- Result.Error e; keep <- false
          | Result.Ok r    -> left <- Result.Ok (PeBin (op, l, r))
        else keep <- false
    left
  and parseUnary () =
    skipWs ()
    if i < s.Length && s.[i] = '-' then
      i <- i + 1
      match parseUnary () with
      | Result.Ok e -> Result.Ok (PeNeg e)
      | err -> err
    elif i < s.Length && s.[i] = '+' then
      i <- i + 1
      parseUnary ()
    else parseAtom ()
  and parseAtom () =
    skipWs ()
    if i >= s.Length then Result.Error "unexpected end of expression"
    elif s.[i] = '(' then
      i <- i + 1
      match parseExpr () with
      | Result.Error e -> Result.Error e
      | Result.Ok e ->
        if eatChar ')' then Result.Ok e else Result.Error "expected ')'"
    elif Char.IsDigit s.[i] || s.[i] = '.' then
      match readNumber () with
      | Some v -> Result.Ok (PeNum v)
      | None   -> Result.Error "invalid number"
    elif isIdStart s.[i] then
      match readIdent () with
      | None      -> Result.Error "expected identifier"
      | Some name ->
        skipWs ()
        let lower = name.ToLowerInvariant()
        if i < s.Length && s.[i] = '(' then
          if not (aggFns.Contains lower || rangeFns.Contains lower) then
            Result.Error (sprintf "embedded PromQL does not support function '%s'; use --mimir-url= for full PromQL" name)
          else
            i <- i + 1
            match parseExpr () with
            | Result.Error e -> Result.Error e
            | Result.Ok arg ->
              if not (eatChar ')') then Result.Error "expected ')'"
              elif rangeFns.Contains lower then
                match arg with
                | PeRange _ -> Result.Ok (PeCall (lower, arg))
                | _ -> Result.Error (sprintf "%s() requires a range selector like foo[5m]" lower)
              else
                Result.Ok (PeCall (lower, arg))
        else
          let innerRes =
            match readBraceGroup () with
            | None                   -> Result.Ok None
            | Some (Result.Ok inner) -> Result.Ok (Some inner)
            | Some (Result.Error e)  -> Result.Error e
          match innerRes with
          | Result.Error e -> Result.Error e
          | Result.Ok innerOpt ->
            match buildSelector name innerOpt with
            | Result.Error e -> Result.Error e
            | Result.Ok sel ->
              match readBracketDuration () with
              | None                -> Result.Ok (PeSel sel)
              | Some (Result.Ok ms) -> Result.Ok (PeRange (sel, ms))
              | Some (Result.Error e) -> Result.Error e
    else
      Result.Error (sprintf "unexpected character '%c'" s.[i])

  match parseExpr () with
  | Result.Error e -> Result.Error e
  | Result.Ok e ->
    skipWs ()
    if i < s.Length then
      Result.Error (sprintf "unexpected trailing input near '%s'" (s.Substring i))
    else
      Result.Ok e

// ---------- Evaluator ----------

type PromSeries = { labels : (string*string)[]; value : float }

type EvalVal =
  | VScalar of float
  | VVector of PromSeries[]

let private dropName (labels : (string*string)[]) =
  labels |> Array.filter (fun (k, _) -> k <> "__name__")

let private labelsKey (labels : (string*string)[]) =
  labels
  |> Array.sortBy fst
  |> Array.map (fun (k, v) -> k + "\u0000" + v)
  |> String.concat "\u0001"

let private staleWindowMs = 5L * 60_000L

let private collectSeries (metricStore : MetricStore) (sel : VectorSelector) =
  metricStore.Names()
  |> Array.choose (fun n ->
      let labels = parseSeriesName n
      if matchesSelector sel labels then Some (n, labels) else None)

// Most recent sample with ts <= at, within the staleness window.
let private sampleInstant (points : Point[]) (at : int64) : float option =
  if points.Length = 0 then None
  else
    let mutable lo = 0
    let mutable hi = points.Length
    while lo < hi do
      let mid = (lo + hi) / 2
      if points.[mid].ts <= at then lo <- mid + 1 else hi <- mid
    if lo = 0 then None
    else
      let p = points.[lo - 1]
      if at - p.ts > staleWindowMs then None else Some p.value

// Counter-aware rate over (endTs - windowMs, endTs].
let private rateOver (points : Point[]) (endTs : int64) (windowMs : int64) : float option =
  let startTs = endTs - windowMs
  let window = points |> Array.filter (fun p -> p.ts > startTs && p.ts <= endTs)
  if window.Length < 2 then None
  else
    let mutable sum = 0.0
    let mutable prev = window.[0].value
    for k in 1 .. window.Length - 1 do
      let cur = window.[k].value
      let delta = cur - prev
      if delta >= 0.0 then sum <- sum + delta
      else sum <- sum + cur      // counter reset
      prev <- cur
    let dt = float (window.[window.Length - 1].ts - window.[0].ts) / 1000.0
    if dt <= 0.0 then None else Some (sum / dt)

let private irateOver (points : Point[]) (endTs : int64) (windowMs : int64) : float option =
  let startTs = endTs - windowMs
  let window = points |> Array.filter (fun p -> p.ts > startTs && p.ts <= endTs)
  if window.Length < 2 then None
  else
    let a = window.[window.Length - 2]
    let b = window.[window.Length - 1]
    let delta = if b.value >= a.value then b.value - a.value else b.value
    let dt = float (b.ts - a.ts) / 1000.0
    if dt <= 0.0 then None else Some (delta / dt)

let private increaseOver (points : Point[]) (endTs : int64) (windowMs : int64) : float option =
  match rateOver points endTs windowMs with
  | None   -> None
  | Some r -> Some (r * float windowMs / 1000.0)

let private aggregate (op : string) (vs : PromSeries[]) : PromSeries[] =
  let xs =
    vs
    |> Array.map (fun s -> s.value)
    |> Array.filter (fun v -> not (Double.IsNaN v))
  if xs.Length = 0 then [||]
  else
    let v =
      match op with
      | "sum"   -> Array.sum xs
      | "avg"   -> Array.sum xs / float xs.Length
      | "min"   -> Array.min xs
      | "max"   -> Array.max xs
      | "count" -> float xs.Length
      | _       -> Double.NaN
    [| { labels = [||]; value = v } |]

let private applyOp (op : PromOp) (a : float) (b : float) : float =
  match op with
  | PoAdd -> a + b
  | PoSub -> a - b
  | PoMul -> a * b
  | PoDiv -> if b = 0.0 then Double.NaN else a / b
  | PoMod -> if b = 0.0 then Double.NaN else a % b

let private binCombine (op : PromOp) (lhs : EvalVal) (rhs : EvalVal) : EvalVal =
  match lhs, rhs with
  | VScalar a, VScalar b -> VScalar (applyOp op a b)
  | VVector vs, VScalar b ->
    VVector (vs |> Array.map (fun s -> { s with value = applyOp op s.value b }))
  | VScalar a, VVector vs ->
    VVector (vs |> Array.map (fun s -> { s with value = applyOp op a s.value }))
  | VVector ls, VVector rs ->
    // 1:1 matching on every label except __name__.
    let idx = System.Collections.Generic.Dictionary<string, PromSeries>()
    for r in rs do
      let k = labelsKey (dropName r.labels)
      idx.[k] <- r
    let out = ResizeArray<PromSeries>()
    for l in ls do
      let stripped = dropName l.labels
      let k = labelsKey stripped
      match idx.TryGetValue k with
      | true, r -> out.Add { labels = stripped; value = applyOp op l.value r.value }
      | _ -> ()
    VVector (out.ToArray())

let rec evalAt (metricStore : MetricStore) (at : int64) (e : PromExpr) : EvalVal =
  match e with
  | PeNum v -> VScalar v
  | PeNeg x ->
    match evalAt metricStore at x with
    | VScalar v  -> VScalar (-v)
    | VVector vs -> VVector (vs |> Array.map (fun s -> { s with value = -s.value }))
  | PeSel sel ->
    let series =
      collectSeries metricStore sel
      |> Array.choose (fun (n, labels) ->
          let pts = metricStore.GetSince(n, at - staleWindowMs)
          sampleInstant pts at
          |> Option.map (fun v -> { labels = labels; value = v }))
    VVector series
  | PeRange _ ->
    // Range vector outside a range function — unsupported, evaluates to empty.
    VVector [||]
  | PeCall (name, PeRange (sel, window))
      when name = "rate" || name = "irate" || name = "increase" ->
    let fn =
      match name with
      | "rate"     -> rateOver
      | "irate"    -> irateOver
      | "increase" -> increaseOver
      | _          -> rateOver
    let series =
      collectSeries metricStore sel
      |> Array.choose (fun (n, labels) ->
          let pts = metricStore.GetSince(n, at - window - 1000L)
          fn pts at window
          |> Option.map (fun v -> { labels = dropName labels; value = v }))
    VVector series
  | PeCall (name, arg) ->
    match evalAt metricStore at arg with
    | VScalar _ as s -> s
    | VVector vs     -> VVector (aggregate name vs)
  | PeBin (op, a, b) ->
    binCombine op (evalAt metricStore at a) (evalAt metricStore at b)

// Group per-step EvalVal results into a Prometheus matrix.
let private toMatrix (perStep : (int64 * EvalVal)[]) : ((string*string)[] * Point[])[] =
  let bucket =
    System.Collections.Generic.Dictionary<string, (string*string)[] * ResizeArray<Point>>()
  let getBucket (k : string) (labels : (string*string)[]) =
    match bucket.TryGetValue k with
    | true, pair -> pair
    | _          ->
      let pair = labels, ResizeArray<Point>()
      bucket.[k] <- pair
      pair
  for (ts, ev) in perStep do
    match ev with
    | VScalar v ->
      let _, pts = getBucket "" [||]
      pts.Add { ts = ts; value = v }
    | VVector ss ->
      for s in ss do
        let _, pts = getBucket (labelsKey s.labels) s.labels
        pts.Add { ts = ts; value = s.value }
  bucket.Values
  |> Seq.map (fun (l, pts) -> l, pts.ToArray())
  |> Seq.toArray

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
    match parsePromExpr query with
    | Result.Error msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | Result.Ok expr ->
      match evalAt metricStore timeMs expr with
      | VScalar v ->
        let body =
          sprintf """{"status":"success","data":{"resultType":"scalar","result":[%s,%s]}}"""
            (formatFloat (float timeMs / 1000.0))
            (JsonString (formatFloat v))
        return! ok body ctx
      | VVector ss ->
        let entries =
          ss
          |> Array.map (fun s -> s.labels, { ts = timeMs; value = s.value })
        return! ok (vectorResponse entries) ctx
  }

let private promQueryRangeEmbedded
              (metricStore : MetricStore)
              (_rollupStore : RollupStore option) : WebPart =
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
    match parsePromExpr query with
    | Result.Error msg ->
      return! errJson BAD_REQUEST "bad_data" msg ctx
    | Result.Ok expr ->
      let steps = ResizeArray<int64 * EvalVal>()
      let mutable t = startMs
      while t <= endMs do
        steps.Add (t, evalAt metricStore t expr)
        t <- t + stepMs
      let matrix =
        toMatrix (steps.ToArray())
        |> Array.filter (fun (_, pts) -> pts.Length > 0)
      return! ok (matrixResponse matrix) ctx
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
