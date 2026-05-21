module PulseBoard.Pricing

open System
open System.IO
open System.Text
open System.Text.Json
open PulseBoard.Tenancy
open PulseBoard.Billing

// Phase 8 #5 — predictable pricing.
//
// One source of truth for what each plan costs. The numbers are
// deliberately public: every customer sees the same calculator and the
// same billing math, with no hidden multipliers. Pricing breaks down into
//   1. a flat monthly base (USD) per seat-included plan, and
//   2. metered overage rates per `UsageKind`, charged only on usage
//      that exceeds the plan's soft cap (from `Plans.fs`).
//
// All units below are billed per *the natural unit* — GiB for byte
// counters, raw count for series/spans/evals/seats — so the calculator
// can show line items without unit conversion gotchas.

[<NoComparison; NoEquality>]
type OverageRate =
  { /// Cents per *unit* (see `unitName`). Stored as integer cents so the
    /// JSON serializer never emits scientific notation on small rates.
    centsPerUnit : decimal
    /// Human-readable unit ("GiB", "1M series", "seat") shown on invoices.
    unitName     : string
    /// Multiplier from `UsageKind` raw counts to billable units. e.g.
    /// IngestBytes is billed per GiB, so this is `1 / (1024*1024*1024)`.
    unitsPerRaw  : decimal }

[<NoComparison; NoEquality>]
type PlanCard =
  { plan        : Plan
    /// Monthly base in USD. Free = 0.
    monthlyUsd  : decimal
    /// Number of seats included in the base (Free=1, Pro=5, Enterprise=contract).
    seatsIncluded : int
    overage     : Map<UsageKind, OverageRate> }

// -- Rate card --------------------------------------------------------------
// These are placeholders sized for the OSS launch. They line up with the
// soft caps in `Plans.fs`: usage *under* the soft cap is included; usage
// *over* the soft cap (up to the hard cap) is billed at the overage rate.

let private gib = 1024m * 1024m * 1024m

let private rate cents unitName perRaw =
  { centsPerUnit = cents; unitName = unitName; unitsPerRaw = perRaw }

let private freeOverage : Map<UsageKind, OverageRate> =
  // Free has no overage line item — hitting the soft cap throttles instead
  // of producing a charge. The map is empty so the calculator shows $0.
  Map.empty

let private proOverage : Map<UsageKind, OverageRate> =
  [
    IngestBytes,  rate  50m "GiB"      (1m / gib)            // $0.50/GiB
    LogBytes,     rate  30m "GiB"      (1m / gib)            // $0.30/GiB
    ActiveSeries, rate   8m "1k series" (1m / 1000m)         // $0.08 per 1k series
    TraceSpans,   rate  10m "1M spans" (1m / 1_000_000m)     // $0.10 per 1M spans
    AlertEvals,   rate   2m "1M evals" (1m / 1_000_000m)     // $0.02 per 1M evals
    Seats,        rate 1500m "seat"    1m                    // $15/seat (above 5 included)
  ] |> Map.ofList

let private enterpriseOverage : Map<UsageKind, OverageRate> =
  // Enterprise has no list-price overage — pricing is contract-bound and
  // hard caps stay at `Int64.MaxValue` from `Plans.fs`. We still publish
  // the included-unit information so the calculator can show "contact us".
  Map.empty

let card (plan : Plan) : PlanCard =
  match plan with
  | Free       -> { plan = Free; monthlyUsd =    0m; seatsIncluded = 1; overage = freeOverage }
  | Pro        -> { plan = Pro;  monthlyUsd =   99m; seatsIncluded = 5; overage = proOverage }
  | Enterprise -> { plan = Enterprise; monthlyUsd = 0m; seatsIncluded = 0; overage = enterpriseOverage }

let allCards : PlanCard[] =
  [| card Free; card Pro; card Enterprise |]

// -- Estimate ---------------------------------------------------------------

[<NoComparison; NoEquality>]
type UsageInput =
  { ingestBytes  : int64
    logBytes     : int64
    activeSeries : int64
    traceSpans   : int64
    alertEvals   : int64
    seats        : int64 }

let emptyUsage =
  { ingestBytes = 0L; logBytes = 0L; activeSeries = 0L
    traceSpans = 0L; alertEvals = 0L; seats = 0L }

let private rawFor (kind : UsageKind) (u : UsageInput) : int64 =
  match kind with
  | IngestBytes  -> u.ingestBytes
  | LogBytes     -> u.logBytes
  | ActiveSeries -> u.activeSeries
  | TraceSpans   -> u.traceSpans
  | AlertEvals   -> u.alertEvals
  | Seats        -> u.seats

let private softCapFor (plan : Plan) (kind : UsageKind) : int64 =
  match kind with
  | IngestBytes  -> PulseBoard.Plans.ingestBytesSoftCap  plan
  | LogBytes     -> PulseBoard.Plans.logBytesSoftCap     plan
  | ActiveSeries -> PulseBoard.Plans.activeSeriesSoftCap plan
  | TraceSpans   -> PulseBoard.Plans.traceSpansSoftCap   plan
  | AlertEvals   -> PulseBoard.Plans.alertEvalsSoftCap   plan
  | Seats        -> PulseBoard.Plans.seatsSoftCap        plan

[<NoComparison; NoEquality>]
type LineItem =
  { kind        : UsageKind
    raw         : int64
    includedRaw : int64
    overRaw     : int64
    units       : decimal
    usd         : decimal }

[<NoComparison; NoEquality>]
type Estimate =
  { plan       : Plan
    baseUsd    : decimal
    items      : LineItem[]
    /// Sum of base + every line item, USD.
    totalUsd   : decimal }

let estimate (plan : Plan) (u : UsageInput) : Estimate =
  let c = card plan
  let items =
    PulseBoard.Billing.allUsageKinds
    |> Array.map (fun kind ->
      let raw      = rawFor kind u
      let included =
        let soft = softCapFor plan kind
        if soft = Int64.MaxValue then raw else soft
      let over =
        if raw <= included then 0L
        elif included = Int64.MaxValue then 0L
        else raw - included
      let units, usd =
        match Map.tryFind kind c.overage with
        | None -> 0m, 0m
        | Some r ->
          let units = decimal over * r.unitsPerRaw
          let usd   = units * (r.centsPerUnit / 100m)
          units, usd
      { kind = kind; raw = raw; includedRaw = included
        overRaw = over; units = units; usd = usd })
  let total = c.monthlyUsd + (items |> Array.sumBy (fun li -> li.usd))
  { plan = plan; baseUsd = c.monthlyUsd; items = items; totalUsd = total }

let estimateAll (u : UsageInput) : Estimate[] =
  [| estimate Free u; estimate Pro u; estimate Enterprise u |]

// -- JSON helpers -----------------------------------------------------------

let private writeRateCard (w : Utf8JsonWriter) (c : PlanCard) =
  w.WriteStartObject()
  w.WriteString("plan", planToText c.plan)
  w.WriteNumber("monthlyUsd", c.monthlyUsd)
  w.WriteNumber("seatsIncluded", c.seatsIncluded)
  w.WriteStartObject("includedSoftCaps")
  for kind in PulseBoard.Billing.allUsageKinds do
    let soft = softCapFor c.plan kind
    if soft = Int64.MaxValue then
      w.WriteString(PulseBoard.Billing.usageKindStr kind, "unlimited")
    else
      w.WriteNumber(PulseBoard.Billing.usageKindStr kind, soft)
  w.WriteEndObject()
  w.WriteStartObject("overage")
  for KeyValue(kind, r) in c.overage do
    w.WriteStartObject(PulseBoard.Billing.usageKindStr kind)
    w.WriteNumber("centsPerUnit", r.centsPerUnit)
    w.WriteString("unit", r.unitName)
    w.WriteEndObject()
  w.WriteEndObject()
  w.WriteEndObject()

let rateCardJson () : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("currency", "USD")
    w.WriteString("billingPeriod", "monthly")
    w.WriteStartArray("plans")
    for c in allCards do
      writeRateCard w c
    w.WriteEndArray()
    w.WriteEndObject()
  )
  Encoding.UTF8.GetString(ms.ToArray())

let private writeEstimate (w : Utf8JsonWriter) (e : Estimate) =
  w.WriteStartObject()
  w.WriteString("plan", planToText e.plan)
  w.WriteNumber("baseUsd", e.baseUsd)
  w.WriteNumber("totalUsd", e.totalUsd)
  w.WriteStartArray("items")
  for li in e.items do
    w.WriteStartObject()
    w.WriteString("kind", PulseBoard.Billing.usageKindStr li.kind)
    w.WriteNumber("raw", li.raw)
    if li.includedRaw = Int64.MaxValue then
      w.WriteString("included", "unlimited")
    else
      w.WriteNumber("included", li.includedRaw)
    w.WriteNumber("over", li.overRaw)
    w.WriteNumber("units", li.units)
    w.WriteNumber("usd", li.usd)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()

let estimateJson (results : Estimate[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("currency", "USD")
    w.WriteStartArray("plans")
    for e in results do writeEstimate w e
    w.WriteEndArray()
    w.WriteEndObject()
  )
  Encoding.UTF8.GetString(ms.ToArray())

let private tryGetInt64 (el : JsonElement) (name : string) : int64 option =
  match el.TryGetProperty(name) with
  | true, p when p.ValueKind = JsonValueKind.Number ->
    match p.TryGetInt64() with
    | true, v -> Some v
    | _ -> None
  | _ -> None

/// Parse `{ingestBytes,logBytes,activeSeries,traceSpans,alertEvals,seats}`.
/// Missing fields default to zero so the calculator can submit sparse input.
let parseUsageInput (body : string) : UsageInput =
  if String.IsNullOrWhiteSpace body then emptyUsage
  else
    use doc = JsonDocument.Parse body
    let el = doc.RootElement
    let i (n : string) = tryGetInt64 el n |> Option.defaultValue 0L
    { ingestBytes  = i "ingestBytes"
      logBytes     = i "logBytes"
      activeSeries = i "activeSeries"
      traceSpans   = i "traceSpans"
      alertEvals   = i "alertEvals"
      seats        = i "seats" }
