module PulseBoard.Costs

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open PulseBoard.Tenancy
open PulseBoard.Billing

// Phase 8 #1 — cost transparency.
//
// The billing meter (Phase 7 #1) already tells a tenant *how much* they
// spent in total this period. Cost transparency goes one level deeper:
//
//   * **Cardinality explorer** — per-tenant, per-series sample counts so
//     a runaway label or chatty service stops being a mystery. Ranked by
//     samples desc; each row carries an estimated monthly USD cost using
//     the published `Pricing` rate card.
//
//   * **Team attribution** — many orgs prefix their metric names by team
//     (`payments.latency.p99`, `search.qps`, ...). We group accepted
//     samples by a configurable prefix policy (default = first dot-segment
//     of the name) so platform owners can show "team A burned $X of the
//     bill this month".
//
// Both surfaces read from a single `ICostTracker` that's tapped from the
// metrics ingest path. In OSS this is an in-process counter; the SaaS
// edge can swap in a backend that streams to a separate analytics DB.

[<NoComparison; NoEquality>]
type SeriesCost =
  { tenant        : TenantId
    seriesName    : string
    samples       : int64
    estimatedBytes: int64 }

[<NoComparison; NoEquality>]
type TeamCost =
  { tenant   : TenantId
    team     : string
    samples  : int64
    series   : int
    estimatedBytes : int64 }

type ICostTracker =
  /// Record `n` accepted samples for `seriesName` against the tenant.
  /// `approxBytes` is the caller's best estimate of the bytes-on-wire
  /// attributable to this group (full request body / total samples in
  /// the request is fine — we just need cost attribution to be roughly
  /// proportional).
  abstract RecordSamples :
    tenant:TenantId * seriesName:string * n:int * approxBytes:int64 -> unit
  /// Top-N series by sample count for a tenant.
  abstract TopSeries : tenant:TenantId * topN:int -> SeriesCost[]
  /// Per-team aggregation using `teamFor seriesName -> string`.
  abstract TeamBreakdown :
    tenant:TenantId * teamFor:(string -> string) -> TeamCost[]
  /// Reset all counters for a tenant (used by tests + the daily rollup).
  abstract ResetTenant : tenant:TenantId -> unit

/// Default team policy: everything before the first '.' in the series
/// name, lowercased. `unscoped` for series with no dot.
let defaultTeamFor (seriesName : string) : string =
  if String.IsNullOrEmpty seriesName then "unscoped"
  else
    let i = seriesName.IndexOf '.'
    if i <= 0 then "unscoped"
    else seriesName.Substring(0, i).ToLowerInvariant()

// -- In-memory tracker ------------------------------------------------------

[<AllowNullLiteral>]
type private SeriesCell() =
  let mutable samples = 0L
  let mutable bytes   = 0L
  member _.Add(n : int64, b : int64) =
    Interlocked.Add(&samples, n) |> ignore
    Interlocked.Add(&bytes,   b) |> ignore
  member _.Samples = Interlocked.Read &samples
  member _.Bytes   = Interlocked.Read &bytes

type InMemoryCostTracker () =
  // Keyed by (tenant, seriesName).
  let cells =
    ConcurrentDictionary<struct (TenantId * string), SeriesCell>()

  let cellFor (tenant : TenantId) (name : string) : SeriesCell =
    let key = struct (tenant, name)
    cells.GetOrAdd(key, fun _ -> SeriesCell())

  interface ICostTracker with
    member _.RecordSamples (tenant, seriesName, n, approxBytes) =
      if n > 0 && not (String.IsNullOrEmpty seriesName) then
        let c = cellFor tenant seriesName
        c.Add(int64 n, approxBytes)

    member _.TopSeries (tenant, topN) =
      let want = if topN <= 0 then 20 else topN
      let rows =
        cells
        |> Seq.choose (fun kv ->
          let struct (t, name) = kv.Key
          if t = tenant then
            Some
              { tenant = t
                seriesName = name
                samples = kv.Value.Samples
                estimatedBytes = kv.Value.Bytes }
          else None)
        |> Seq.toArray
      Array.sortInPlaceBy (fun (r : SeriesCost) -> -r.samples) rows
      if rows.Length <= want then rows
      else rows.[0..want-1]

    member _.TeamBreakdown (tenant, teamFor) =
      let groups = Collections.Generic.Dictionary<string, struct (int64 * int * int64)>()
      for kv in cells do
        let struct (t, name) = kv.Key
        if t = tenant then
          let team = teamFor name
          let struct (s, sc, b) =
            match groups.TryGetValue team with
            | true, v -> v
            | _ -> struct (0L, 0, 0L)
          groups.[team] <-
            struct (s + kv.Value.Samples, sc + 1, b + kv.Value.Bytes)
      let rows =
        groups
        |> Seq.map (fun kv ->
          let struct (s, sc, b) = kv.Value
          { tenant = tenant; team = kv.Key
            samples = s; series = sc; estimatedBytes = b })
        |> Seq.toArray
      Array.sortInPlaceBy (fun (r : TeamCost) -> -r.samples) rows
      rows

    member _.ResetTenant tenant =
      let stale = ResizeArray<_>()
      for kv in cells do
        let struct (t, _) = kv.Key
        if t = tenant then stale.Add kv.Key
      for k in stale do cells.TryRemove k |> ignore

// -- Cost estimation --------------------------------------------------------

/// Estimate the monthly USD for a per-series byte count using the Pro
/// `IngestBytes` overage rate. This is intentionally one-rate: cost
/// transparency wants a meaningful number, not perfect attribution.
let estimateSeriesCostUsd (bytes : int64) : decimal =
  match Map.tryFind PulseBoard.Billing.IngestBytes (PulseBoard.Pricing.card Pro).overage with
  | None -> 0m
  | Some r ->
    let units = decimal bytes * r.unitsPerRaw
    units * (r.centsPerUnit / 100m)

// -- JSON helpers -----------------------------------------------------------

let topSeriesJson (tenantId : string) (rows : SeriesCost[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("tenantId", tenantId)
    w.WriteStartArray("series")
    for r in rows do
      w.WriteStartObject()
      w.WriteString("name", r.seriesName)
      w.WriteNumber("samples", r.samples)
      w.WriteNumber("estimatedBytes", r.estimatedBytes)
      w.WriteNumber("estimatedMonthlyUsd", estimateSeriesCostUsd r.estimatedBytes)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
  )
  Encoding.UTF8.GetString(ms.ToArray())

let teamBreakdownJson (tenantId : string) (rows : TeamCost[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("tenantId", tenantId)
    w.WriteStartArray("teams")
    for r in rows do
      w.WriteStartObject()
      w.WriteString("team", r.team)
      w.WriteNumber("samples", r.samples)
      w.WriteNumber("series", r.series)
      w.WriteNumber("estimatedBytes", r.estimatedBytes)
      w.WriteNumber("estimatedMonthlyUsd", estimateSeriesCostUsd r.estimatedBytes)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
  )
  Encoding.UTF8.GetString(ms.ToArray())
