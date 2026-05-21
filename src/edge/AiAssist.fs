module PulseBoard.AiAssist

open System
open System.IO
open System.Text
open System.Text.Json
open PulseBoard.Tenancy

// Phase 8 #3 — native AI assist.
//
// The endpoint we ship today is `POST /api/ai/explain`. It takes:
//   { "seriesName": "...", "samples": [{ "ts": <ms>, "value": <num> }, ...],
//     "question": "why did p99 spike?" }
// and returns a short prose explanation produced by an `IAiProvider`.
//
// The OSS edge ships `EchoAiProvider` — a deterministic analyzer that
// computes mean / stddev / min / max / max-jump and stitches them into a
// human-readable summary. It needs no network access and no model weights,
// so the feature is *useful* even when the tenant has not configured an
// LLM provider. The interface is set up so a SaaS build can plug in an
// OpenAI / Anthropic / local-vLLM adapter without touching this module.
//
// Privacy: callers POST their own series data, so nothing is exfiltrated
// implicitly. Future providers should respect a per-tenant "ai.enabled"
// flag (Phase 8 follow-up) and refuse to forward data to external models
// unless that flag is set.

[<NoComparison; NoEquality>]
type Point = { ts : int64; value : double }

[<NoComparison; NoEquality>]
type ExplainContext =
  { tenant     : TenantId option
    seriesName : string
    samples    : Point[]
    question   : string option }

[<NoComparison; NoEquality>]
type ExplainResult =
  { /// Provider that produced the explanation (e.g. "echo", "openai").
    provider    : string
    /// Short prose answer; safe to render directly.
    summary     : string
    /// Provider-specific tags useful for the UI (e.g. detected spike ts).
    annotations : (string * string)[] }

type IAiProvider =
  abstract Name    : string
  abstract Explain : ExplainContext -> Async<ExplainResult>

// -- Deterministic stats ----------------------------------------------------

[<NoComparison; NoEquality>]
type private Stats =
  { count   : int
    mean    : double
    stdDev  : double
    minVal  : double
    maxVal  : double
    minTs   : int64
    maxTs   : int64
    /// Largest |value[i] - value[i-1]| in the window and the ts at which it occurred.
    maxJump : double
    jumpTs  : int64 }

let private statsOf (samples : Point[]) : Stats option =
  if isNull (box samples) || samples.Length = 0 then None
  else
    let mutable sum   = 0.0
    let mutable sumSq = 0.0
    let mutable mn    = Double.PositiveInfinity
    let mutable mx    = Double.NegativeInfinity
    let mutable mnTs  = 0L
    let mutable mxTs  = 0L
    let mutable jump  = 0.0
    let mutable jTs   = 0L
    let mutable prev  = Double.NaN
    for s in samples do
      let v = s.value
      sum   <- sum + v
      sumSq <- sumSq + v * v
      if v < mn then mn <- v; mnTs <- s.ts
      if v > mx then mx <- v; mxTs <- s.ts
      if not (Double.IsNaN prev) then
        let d = abs (v - prev)
        if d > jump then jump <- d; jTs <- s.ts
      prev <- v
    let n      = double samples.Length
    let mean   = sum / n
    let varc   = max 0.0 (sumSq / n - mean * mean)
    let stdDev = sqrt varc
    Some
      { count = samples.Length; mean = mean; stdDev = stdDev
        minVal = mn; maxVal = mx; minTs = mnTs; maxTs = mxTs
        maxJump = jump; jumpTs = jTs }

// -- Echo provider ----------------------------------------------------------

let private fmt (v : double) : string =
  if Double.IsNaN v || Double.IsInfinity v then "n/a"
  elif abs v >= 1000.0 then sprintf "%.0f" v
  elif abs v >= 1.0    then sprintf "%.2f" v
  else                      sprintf "%.4f" v

type EchoAiProvider () =
  interface IAiProvider with
    member _.Name = "echo"
    member _.Explain (ctx : ExplainContext) = async {
      match statsOf ctx.samples with
      | None ->
        return
          { provider = "echo"
            summary  = sprintf "No samples received for %s; nothing to explain." ctx.seriesName
            annotations = [||] }
      | Some s ->
        let spike =
          // Heuristic: a "spike" is a single-step jump > 2× stddev.
          if s.stdDev > 0.0 && s.maxJump > 2.0 * s.stdDev then
            Some s.jumpTs
          else None
        let sb = StringBuilder()
        sb.Append(sprintf "Series %s across %d sample(s): " ctx.seriesName s.count) |> ignore
        sb.Append(sprintf "mean=%s, stddev=%s, min=%s @ %d, max=%s @ %d. "
                    (fmt s.mean) (fmt s.stdDev) (fmt s.minVal) s.minTs
                    (fmt s.maxVal) s.maxTs) |> ignore
        match spike with
        | Some ts ->
          sb.Append(sprintf "Largest single-step jump was %s at %d (> 2× stddev) — that is the most likely culprit for any visible spike."
                      (fmt s.maxJump) ts) |> ignore
        | None ->
          sb.Append("No single-step jump exceeded 2× stddev — the series looks stationary in this window.") |> ignore
        match ctx.question with
        | Some q when not (String.IsNullOrWhiteSpace q) ->
          sb.Append(sprintf " (Question on file: \"%s\".)" q) |> ignore
        | _ -> ()
        let anns =
          [|
            yield "mean",   fmt s.mean
            yield "stdDev", fmt s.stdDev
            yield "min",    fmt s.minVal
            yield "max",    fmt s.maxVal
            match spike with Some ts -> yield "spikeTs", string ts | None -> ()
          |]
        return
          { provider = "echo"; summary = sb.ToString(); annotations = anns }
    }

// -- Request parsing --------------------------------------------------------

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty(name) with
  | true, p when p.ValueKind = JsonValueKind.String -> Some(p.GetString())
  | _ -> None

let private tryGetInt64 (el : JsonElement) (name : string) : int64 option =
  match el.TryGetProperty(name) with
  | true, p when p.ValueKind = JsonValueKind.Number ->
    match p.TryGetInt64() with
    | true, v -> Some v
    | _ -> None
  | _ -> None

let private tryGetDouble (el : JsonElement) (name : string) : double option =
  match el.TryGetProperty(name) with
  | true, p when p.ValueKind = JsonValueKind.Number ->
    match p.TryGetDouble() with
    | true, v -> Some v
    | _ -> None
  | _ -> None

/// Parse a request body into an `ExplainContext`. Throws on malformed JSON
/// — callers should wrap in try/with at the WebPart boundary.
let parseContext (tenant : TenantId option) (body : string) : ExplainContext =
  use doc = JsonDocument.Parse body
  let root = doc.RootElement
  let series = tryGetString root "seriesName" |> Option.defaultValue ""
  let question = tryGetString root "question"
  let samples =
    match root.TryGetProperty "samples" with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
      [|
        for s in arr.EnumerateArray() do
          let ts  = tryGetInt64  s "ts"    |> Option.defaultValue 0L
          let v   = tryGetDouble s "value" |> Option.defaultValue Double.NaN
          if not (Double.IsNaN v) then yield { ts = ts; value = v }
      |]
    | _ -> [||]
  { tenant = tenant; seriesName = series; samples = samples; question = question }

let resultJson (r : ExplainResult) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("provider", r.provider)
    w.WriteString("summary",  r.summary)
    w.WriteStartObject("annotations")
    for (k, v) in r.annotations do w.WriteString(k, v)
    w.WriteEndObject()
    w.WriteEndObject()
  )
  Encoding.UTF8.GetString(ms.ToArray())
