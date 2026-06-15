module PulseBoard.BillPredictor

// Phase 14.3 — Predicted monthly bill.
//
// Take the in-meter usage counters for the **current month** (the
// rollup loop drains every 24 h, so `Snapshot` is intra-period usage
// since the last drain — see Billing.fs), figure out what fraction of
// the month has elapsed, and linearly project to month-end. Run the
// projected per-pillar `UsageInput` through `Pricing.estimate` to get
// the projected bill in USD.
//
// Linear projection is intentionally simple — operators want a single
// number that reflects "if today keeps looking like today, here's what
// you'll pay". Anything fancier (seasonal/rolling) would be guesswork
// in this codebase; we shouldn't pretend to know better.

open System
open PulseBoard.Tenancy
open PulseBoard.Billing
open PulseBoard.Pricing

/// One pillar's projection.
[<NoComparison; NoEquality>]
type ProjectedPillar =
  { /// Pillar key as it appears on the API surface
    /// (`ingest`/`logs`/`series`/`spans`/`evals`/`seats`).
    pillar       : string
    kind         : UsageKind
    currentRaw   : int64
    projectedRaw : int64
    /// USD cost attributable to this pillar's projected raw quantity.
    usd          : decimal }

/// Whole projection for one tenant.
[<NoComparison; NoEquality>]
type ProjectedBill =
  { tenant       : TenantId
    plan         : Plan
    /// Plan base in USD (not pro-rated; that's how invoices read).
    baseUsd      : decimal
    pillars      : ProjectedPillar[]
    /// `baseUsd + sum(pillars.usd)`.
    totalUsd     : decimal
    /// UTC unix-ms — start of the current calendar month.
    periodStart  : int64
    /// UTC unix-ms — first instant of the following calendar month.
    periodEnd    : int64
    /// Fraction of the period elapsed at `nowMs`; in `(0,1]`.
    elapsedFrac  : float }

/// Stable mapping between the public pillar string and the billing
/// `UsageKind`. Used both for projection and budget rule evaluation.
let pillarToKind = function
  | "ingest" -> Some IngestBytes
  | "logs"   -> Some LogBytes
  | "series" -> Some ActiveSeries
  | "spans"  -> Some TraceSpans
  | "evals"  -> Some AlertEvals
  | "seats"  -> Some Seats
  | _        -> None

let kindToPillar = function
  | IngestBytes  -> "ingest"
  | LogBytes     -> "logs"
  | ActiveSeries -> "series"
  | TraceSpans   -> "spans"
  | AlertEvals   -> "evals"
  | Seats        -> "seats"

/// Unix-ms timestamps for the calendar month containing `nowMs` (UTC).
let periodFor (nowMs : int64) : int64 * int64 =
  let now   = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime
  let start = DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
  let next  = start.AddMonths 1
  let toMs (d : DateTime) = DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds()
  toMs start, toMs next

/// Fraction of the period elapsed; clamped to (eps, 1.0] so a
/// fresh-period snapshot doesn't project to infinity.
let private elapsedFraction (periodStart : int64) (periodEnd : int64) (nowMs : int64) =
  let span = float (periodEnd - periodStart)
  if span <= 0.0 then 1.0
  else
    let raw = float (nowMs - periodStart) / span
    if raw < 0.001 then 0.001
    elif raw > 1.0 then 1.0
    else raw

let private projectRaw (currentRaw : int64) (elapsedFrac : float) : int64 =
  if currentRaw <= 0L then 0L
  elif elapsedFrac >= 1.0 then currentRaw
  else
    let proj = float currentRaw / elapsedFrac
    if proj >= float Int64.MaxValue then Int64.MaxValue
    else int64 (System.Math.Round proj)

/// Build a `UsageInput` whose per-kind raw counts are the projected
/// values. Anything not present in the snapshot is treated as 0.
let private projectUsage (snapshot : Map<UsageKind, int64>) (elapsedFrac : float) : UsageInput =
  let cur k = Map.tryFind k snapshot |> Option.defaultValue 0L
  { ingestBytes  = projectRaw (cur IngestBytes)  elapsedFrac
    logBytes     = projectRaw (cur LogBytes)     elapsedFrac
    activeSeries = projectRaw (cur ActiveSeries) elapsedFrac
    traceSpans   = projectRaw (cur TraceSpans)   elapsedFrac
    alertEvals   = projectRaw (cur AlertEvals)   elapsedFrac
    seats        = projectRaw (cur Seats)        elapsedFrac }

/// Project one tenant's bill. `snapshot` is `IBillingMeter.Snapshot tenant`;
/// `plan` is the tenant's plan; `nowMs` is the wall clock.
let project (tenant : TenantId) (plan : Plan) (snapshot : Map<UsageKind, int64>)
            (nowMs : int64) : ProjectedBill =
  let pStart, pEnd = periodFor nowMs
  let frac         = elapsedFraction pStart pEnd nowMs
  let projected    = projectUsage snapshot frac
  let est          = estimate plan projected
  // Index the LineItems by kind so we can present pillars in a stable
  // order (ingest, logs, series, spans, evals, seats).
  let byKind =
    est.items
    |> Array.map (fun li -> li.kind, li)
    |> Map.ofArray
  let pillars =
    allUsageKinds
    |> Array.map (fun k ->
        let cur = Map.tryFind k snapshot |> Option.defaultValue 0L
        let proj = projectRaw cur frac
        let usd =
          match Map.tryFind k byKind with
          | Some li -> li.usd
          | None    -> 0m
        { pillar       = kindToPillar k
          kind         = k
          currentRaw   = cur
          projectedRaw = proj
          usd          = usd })
  { tenant       = tenant
    plan         = plan
    baseUsd      = est.baseUsd
    pillars      = pillars
    totalUsd     = est.totalUsd
    periodStart  = pStart
    periodEnd    = pEnd
    elapsedFrac  = frac }

/// Look up the projected USD for a single pillar (or "total"). Returns
/// `None` if the pillar string is unrecognised. Used by the rule
/// evaluator's budget hook.
let pillarUsd (bill : ProjectedBill) (pillar : string) : float option =
  match pillar with
  | "total" -> Some (float bill.totalUsd)
  | other ->
    match pillarToKind other with
    | None      -> None
    | Some kind ->
      bill.pillars
      |> Array.tryFind (fun p -> p.kind = kind)
      |> Option.map (fun p -> float p.usd)

// -- JSON ------------------------------------------------------------------

let private writeBill (w : System.Text.Json.Utf8JsonWriter) (b : ProjectedBill) =
  let (TenantId tid) = b.tenant
  w.WriteStartObject()
  w.WriteString("tenantId",   tid)
  w.WriteString("plan",       PulseBoard.Tenancy.planToText b.plan)
  w.WriteNumber("baseUsd",    b.baseUsd)
  w.WriteNumber("totalUsd",   b.totalUsd)
  w.WriteNumber("periodStart", b.periodStart)
  w.WriteNumber("periodEnd",   b.periodEnd)
  w.WriteNumber("elapsedFrac", b.elapsedFrac)
  w.WriteStartArray "pillars"
  for p in b.pillars do
    w.WriteStartObject()
    w.WriteString("pillar",       p.pillar)
    w.WriteNumber("currentRaw",   p.currentRaw)
    w.WriteNumber("projectedRaw", p.projectedRaw)
    w.WriteNumber("usd",          p.usd)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()

let serialiseBill (b : ProjectedBill) : string =
  use ms = new System.IO.MemoryStream()
  (
    use w = new System.Text.Json.Utf8JsonWriter(ms)
    writeBill w b)
  System.Text.Encoding.UTF8.GetString (ms.ToArray())
