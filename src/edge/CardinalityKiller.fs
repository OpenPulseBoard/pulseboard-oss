module PulseBoard.CardinalityKiller

// Phase 14.3 — Cost guard rails / cardinality killer.
//
// Operators look at the per-series cost table (Costs.fs) and see a
// runaway label — e.g. `http_requests_total{user_id="..."}` blowing up
// to millions of series because someone labelled a metric with the
// request's user id. This module gives them a one-click switch to
// **drop that label everywhere**:
//
//   1. **Edge side (immediate):** before a sample is admitted in
//      Ingest.fs, the inline-label series name is rewritten — the
//      offending label is stripped from `{...}`. Both `costTracker`
//      and `metricStore` then see the reduced-cardinality series,
//      and the cost dashboard shrinks within a single ingest cycle.
//
//   2. **Agent side (within ~60s):** on every Upsert we re-render the
//      default agent group's overlay TOML so it carries a managed
//      `[[processors.relabel]] action = "labeldrop"` entry with a
//      regex over every active label. The agent's `config_poller`
//      picks up the version bump on its next poll and the labels
//      stop being shipped at source. This is the difference between
//      "we still pay to receive it" and "we pay nothing".
//
// v1 is wildcard-only: a kill rule names a label and applies to every
// metric. The plan calls for exactly this ("drop this label
// everywhere"); per-metric scoping is an obvious follow-up but adds
// non-trivial complexity because the agent's `labeldrop` action is
// metric-agnostic, so a per-metric rule has no clean overlay
// representation — it would only take effect at the edge.

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open PulseBoard.Tenancy

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- model ------------------------------------------------------------------

[<NoComparison>]
type DroppedLabel =
  { /// The label name to drop from every inbound series.
    label     : string
    /// Free-form note shown in the UI ("noisy user_id seen in
    /// cardinality explorer on 2026-06-14").
    reason    : string
    createdAt : int64 }

type ICardinalityKillerStore =
  /// All active drop rules for a tenant, in insertion order.
  abstract List   : tenant:TenantId -> DroppedLabel[]
  /// Add (or replace, same `label`) a drop rule. Returns the stored
  /// record so callers can see the `createdAt`.
  abstract Upsert : tenant:TenantId * rule:DroppedLabel -> DroppedLabel
  /// Remove a rule. Returns true if anything was removed.
  abstract Delete : tenant:TenantId * label:string -> bool
  /// Hot-path predicate: does `label` get stripped for this tenant?
  /// Called once per (sample × label) so it must be O(1).
  abstract IsKilled : tenant:TenantId * label:string -> bool

// -- in-memory --------------------------------------------------------------

type InMemoryCardinalityKillerStore() =
  // (tenantId, label) -> rule
  let rules = ConcurrentDictionary<string * string, DroppedLabel>()

  let key (TenantId t) label = (t, label)

  interface ICardinalityKillerStore with
    member _.List tenant =
      let (TenantId t) = tenant
      rules
      |> Seq.choose (fun kv ->
          let (tid, _) = kv.Key
          if tid = t then Some kv.Value else None)
      |> Seq.sortBy (fun r -> r.createdAt)
      |> Seq.toArray

    member _.Upsert (tenant, rule) =
      if String.IsNullOrWhiteSpace rule.label then
        invalidArg "rule.label" "label must not be empty"
      let stored =
        { label     = rule.label.Trim()
          reason    = rule.reason
          createdAt = if rule.createdAt > 0L then rule.createdAt else nowMs() }
      rules.[key tenant stored.label] <- stored
      stored

    member _.Delete (tenant, label) =
      if String.IsNullOrWhiteSpace label then false
      else rules.TryRemove(key tenant (label.Trim())) |> fst

    member _.IsKilled (tenant, label) =
      rules.ContainsKey(key tenant label)

// -- series-name rewriter ---------------------------------------------------

/// Strip every label in `drops` from the inline-label portion of a
/// series name like `http_requests_total{a="1",b="2"}`. Returns the
/// rewritten name; if nothing matched (or the name has no label block)
/// the input is returned unchanged.
///
/// This deliberately uses a hand-rolled scanner instead of a regex
/// because it runs once per ingested metric sample and we want it
/// allocation-free in the common case where there's nothing to drop.
let stripLabels (drops : ICardinalityKillerStore) (tenant : TenantId)
                (name : string) : string =
  if isNull name then name
  else
    let openIdx = name.IndexOf '{'
    if openIdx < 0 then name
    else
      let closeIdx = name.LastIndexOf '}'
      if closeIdx <= openIdx then name
      else
        let metric = name.Substring(0, openIdx)
        let inner  = name.Substring(openIdx + 1, closeIdx - openIdx - 1)
        // Quick scan: any candidate matches a kill rule?
        let mutable anyMatch = false
        let parts = inner.Split(',')
        let kept = ResizeArray<string>(parts.Length)
        for p in parts do
          let p = p.Trim()
          if p.Length = 0 then ()
          else
            let eq = p.IndexOf '='
            if eq <= 0 then kept.Add p
            else
              let labelName = p.Substring(0, eq).Trim()
              if drops.IsKilled(tenant, labelName) then
                anyMatch <- true
              else
                kept.Add p
        if not anyMatch then name
        elif kept.Count = 0 then metric
        else metric + "{" + String.concat "," kept + "}"

// -- overlay TOML rendering -------------------------------------------------

[<Literal>]
let private OverlayMarkerOpen =
  "# >>> pulseboard cardinality killer (managed) >>>"

[<Literal>]
let private OverlayMarkerClose =
  "# <<< pulseboard cardinality killer (managed) <<<"

/// Return `overlay` with the managed cardinality-killer block replaced
/// by `block`. If `block` is empty the managed region is removed
/// entirely. Any text outside the markers is preserved verbatim — this
/// lets operators hand-edit other parts of the overlay without us
/// stomping their work on the next sync.
let private replaceManagedBlock (overlay : string) (block : string) : string =
  let overlay = if isNull overlay then "" else overlay
  let openIdx = overlay.IndexOf OverlayMarkerOpen
  if openIdx < 0 then
    // No managed region yet — append (with a leading blank line if the
    // file already has content).
    if String.IsNullOrEmpty block then overlay
    elif overlay.Length = 0 then block
    else overlay.TrimEnd() + "\n\n" + block
  else
    let closeStart = overlay.IndexOf(OverlayMarkerClose, openIdx)
    if closeStart < 0 then
      // Malformed marker — append a fresh block, leave the old one.
      if String.IsNullOrEmpty block then overlay
      else overlay.TrimEnd() + "\n\n" + block
    else
      let after = closeStart + OverlayMarkerClose.Length
      let before = overlay.Substring(0, openIdx).TrimEnd()
      let tail   = if after < overlay.Length then overlay.Substring(after).TrimStart('\r', '\n') else ""
      let parts = ResizeArray<string>()
      if before.Length > 0 then parts.Add before
      if not (String.IsNullOrEmpty block) then parts.Add block
      if tail.Length > 0 then parts.Add tail
      String.concat "\n\n" parts

/// Render the managed `[[processors.relabel]]` block for a set of
/// labels. Returns "" when there are no rules so the overlay shrinks
/// back to whatever the operator had before.
let renderManagedBlock (labels : string[]) : string =
  let labels =
    labels
    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
    |> Array.map (fun l -> l.Trim())
    |> Array.distinct
    |> Array.sort
  if labels.Length = 0 then ""
  else
    let escaped =
      labels
      |> Array.map (fun l -> System.Text.RegularExpressions.Regex.Escape l)
      |> String.concat "|"
    let sb = StringBuilder()
    sb.AppendLine OverlayMarkerOpen |> ignore
    sb.AppendLine "[[processors.relabel]]" |> ignore
    sb.AppendLine "action = \"labeldrop\"" |> ignore
    sb.AppendLine (sprintf "regex  = \"^(%s)$\"" escaped) |> ignore
    sb.Append OverlayMarkerClose |> ignore
    sb.ToString()

/// Splice the current set of kill-rule labels into an overlay TOML
/// string, preserving any operator-authored content outside the
/// managed markers.
let applyToOverlay (currentOverlay : string) (labels : string[]) : string =
  let block = renderManagedBlock labels
  replaceManagedBlock currentOverlay block

// -- JSON codecs ------------------------------------------------------------

let private writeRule (w : Utf8JsonWriter) (r : DroppedLabel) =
  w.WriteStartObject()
  w.WriteString("label",     r.label)
  w.WriteString("reason",    r.reason)
  w.WriteNumber("createdAt", r.createdAt)
  w.WriteEndObject()

let serialiseRules (rules : DroppedLabel[]) : string =
  use ms = new MemoryStream()
  use w  = new Utf8JsonWriter(ms)
  w.WriteStartArray()
  for r in rules do writeRule w r
  w.WriteEndArray()
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

/// Parse an inbound POST body. Either a bare `{"label":"..","reason":".."}`
/// or `{"labels":["a","b"], "reason":".."}` (batch).
let parseRules (json : string) : Result<DroppedLabel[], string> =
  try
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let str (el : JsonElement) (n : string) =
      match el.TryGetProperty n with
      | true, e when e.ValueKind = JsonValueKind.String -> e.GetString()
      | _ -> ""
    let reason = str root "reason"
    let labels =
      match root.TryGetProperty "labels" with
      | true, el when el.ValueKind = JsonValueKind.Array ->
        el.EnumerateArray()
        |> Seq.choose (fun e ->
            match e.ValueKind with
            | JsonValueKind.String -> Some (e.GetString())
            | _ -> None)
        |> Seq.toArray
      | _ ->
        let single = str root "label"
        if String.IsNullOrWhiteSpace single then [||] else [| single |]
    let now = nowMs()
    let rules =
      labels
      |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
      |> Array.map (fun l ->
          { label = l.Trim(); reason = reason; createdAt = now })
    if rules.Length = 0 then
      Error "no labels supplied"
    else
      Ok rules
  with ex -> Error (sprintf "invalid JSON: %s" ex.Message)
