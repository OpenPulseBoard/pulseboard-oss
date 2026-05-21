module PulseBoard.Billing

open System
open System.IO
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open PulseBoard.Tenancy
open PulseBoard.Plans

// Phase 7 #1 — usage metering + Stripe-shaped billing pipeline.
//
// We meter what we sell:
//   IngestBytes   — raw bytes accepted by /ingest/metrics + remote_write + OTLP
//   LogBytes      — raw bytes accepted by /ingest/logs + Loki push + OTLP/logs
//   ActiveSeries  — distinct (tenant,name) series admitted in the period
//   TraceSpans    — span count accepted by /v1/traces
//   AlertEvals    — rule evaluations performed (success or fail)
//   Seats         — distinct SSO users active in the period
//
// In this pass only IngestBytes and LogBytes are wired to live counters
// (see Ingest.fs). The remaining kinds compile and persist as zeros until
// each producer is taught to call `meter.Record`.
//
// The pipeline has two halves:
//   1. `IBillingMeter` — fast, in-process counters bumped on every
//      ingest path. ConcurrentDictionary keyed by `(tenant, kind)`;
//      contention is per-cell, not per-store.
//   2. `IBillingProvider` — sink for the daily rollup. Production wires
//      this to Stripe (`POST /v1/subscription_items/<id>/usage_records`);
//      the OSS edge ships a `FileBillingProvider` that just appends JSONL
//      to `<data>/billing/events.jsonl`. The interface keeps Stripe out
//      of the edge process — when we do add it, only this module's
//      provider list changes.

type UsageKind =
  | IngestBytes
  | LogBytes
  | ActiveSeries
  | TraceSpans
  | AlertEvals
  | Seats

let usageKindStr = function
  | IngestBytes  -> "ingestBytes"
  | LogBytes     -> "logBytes"
  | ActiveSeries -> "activeSeries"
  | TraceSpans   -> "traceSpans"
  | AlertEvals   -> "alertEvals"
  | Seats        -> "seats"

let allUsageKinds =
  [| IngestBytes; LogBytes; ActiveSeries; TraceSpans; AlertEvals; Seats |]

let private softCapFor (plan : Plan) (kind : UsageKind) : int64 =
  match kind with
  | IngestBytes  -> ingestBytesSoftCap  plan
  | LogBytes     -> logBytesSoftCap     plan
  | ActiveSeries -> activeSeriesSoftCap plan
  | TraceSpans   -> traceSpansSoftCap   plan
  | AlertEvals   -> alertEvalsSoftCap   plan
  | Seats        -> seatsSoftCap        plan

[<NoComparison; NoEquality>]
type UsageEvent =
  { tenantId   : TenantId
    plan       : Plan
    kind       : UsageKind
    /// Wall-clock period start (UTC midnight for daily rollups).
    periodStart: DateTimeOffset
    /// Wall-clock period end (the moment the event was emitted).
    periodEnd  : DateTimeOffset
    quantity   : int64 }

[<RequireQualifiedAccess>]
type CapDecision =
  /// Under the soft cap; usage continues normally.
  | Under
  /// Past the soft cap but below the hard cap; ingest continues but the
  /// caller should add the `X-PulseBoard-Usage-Warning: soft-cap` header
  /// and trigger an overage email out-of-band.
  | Soft
  /// Past the hard cap; ingest must reject with 429.
  | Hard

type IBillingMeter =
  /// Bump the (tenant, kind) counter by `amount`. Safe to call from any
  /// thread; no allocation in the hot path beyond the dictionary lookup.
  abstract Record   : tenant:TenantId * kind:UsageKind * amount:int64 -> unit
  /// Read the current period's counter without resetting it.
  abstract Snapshot : tenant:TenantId -> Map<UsageKind, int64>
  /// Snapshot across every tenant currently in the meter.
  abstract SnapshotAll : unit -> (TenantId * Map<UsageKind, int64>)[]
  /// Atomically swap current counters out and reset to zero. Returns the
  /// rolled-up events ready to ship to providers; this is what the daily
  /// loop calls.
  abstract Drain    :
    tenants : (TenantId -> Plan) *
    periodStart : DateTimeOffset *
    periodEnd : DateTimeOffset ->
      UsageEvent[]
  /// Plan-aware cap check on a *projected* post-write counter — call after
  /// `Record` to decide whether the current request should be allowed.
  abstract CheckCap :
    tenant:TenantId * plan:Plan * kind:UsageKind -> CapDecision

type IBillingProvider =
  /// Ship a batch of usage events. Implementations should be idempotent
  /// per `(tenant, kind, periodStart)` because the daily loop may retry.
  abstract Report : UsageEvent[] -> Async<Result<int, string>>
  /// Free-form name for logging / audit (`stripe`, `file`, ...).
  abstract Name   : string

// -- In-memory meter --------------------------------------------------------

type InMemoryBillingMeter () =
  // Counters keyed by (TenantId * UsageKind). Per-cell `Interlocked.Add`
  // keeps this lock-free in the hot path.
  let counters = ConcurrentDictionary<struct (TenantId * UsageKind), int64 ref>()
  // Every tenant ever seen by the meter, for `SnapshotAll`/`Drain` even
  // when a kind hasn't been written yet.
  let seenTenants = ConcurrentDictionary<TenantId, unit>()

  let cell (key : struct (TenantId * UsageKind)) =
    counters.GetOrAdd(key, fun _ -> ref 0L)

  interface IBillingMeter with
    member _.Record (tenant, kind, amount) =
      if amount > 0L then
        seenTenants.[tenant] <- ()
        let c = cell (struct (tenant, kind))
        Interlocked.Add(&c.contents, amount) |> ignore

    member _.Snapshot tenant =
      allUsageKinds
      |> Array.map (fun k ->
        let v =
          match counters.TryGetValue (struct (tenant, k)) with
          | true, c -> Interlocked.Read(&c.contents)
          | _       -> 0L
        k, v)
      |> Map.ofArray

    member this.SnapshotAll () =
      seenTenants.Keys
      |> Seq.toArray
      |> Array.map (fun t -> t, (this :> IBillingMeter).Snapshot t)

    member _.Drain (planFor, periodStart, periodEnd) =
      let acc = ResizeArray<UsageEvent>()
      for tid in seenTenants.Keys |> Seq.toArray do
        let plan = planFor tid
        for k in allUsageKinds do
          let key = struct (tid, k)
          match counters.TryGetValue key with
          | true, c ->
            let q = Interlocked.Exchange(&c.contents, 0L)
            if q > 0L then
              acc.Add {
                tenantId    = tid
                plan        = plan
                kind        = k
                periodStart = periodStart
                periodEnd   = periodEnd
                quantity    = q }
          | _ -> ()
      acc.ToArray()

    member _.CheckCap (tenant, plan, kind) =
      let soft = softCapFor plan kind
      if soft = Int64.MaxValue then CapDecision.Under
      else
        let now =
          match counters.TryGetValue (struct (tenant, kind)) with
          | true, c -> Interlocked.Read(&c.contents)
          | _       -> 0L
        let hard = toHardCap soft
        if   now >= hard then CapDecision.Hard
        elif now >= soft then CapDecision.Soft
        else CapDecision.Under

// -- File provider (Stripe stub) -------------------------------------------

/// JSONL provider — one event per line at `<root>/events.jsonl`. Replace
/// with a Stripe-backed implementation when the SaaS edge ships; the
/// interface contract is identical.
type FileBillingProvider (root : string) =
  do Directory.CreateDirectory root |> ignore
  let path = Path.Combine(root, "events.jsonl")
  let writeLock = obj ()

  let writeEvent (ev : UsageEvent) =
    use ms = new MemoryStream()
    (
      use w = new Utf8JsonWriter(ms)
      w.WriteStartObject()
      let (TenantId t) = ev.tenantId
      w.WriteString("tenant", t)
      w.WriteString("plan",   planToText ev.plan)
      w.WriteString("kind",   usageKindStr ev.kind)
      w.WriteString("periodStart", ev.periodStart.ToString("o"))
      w.WriteString("periodEnd",   ev.periodEnd.ToString("o"))
      w.WriteNumber("quantity", ev.quantity)
      w.WriteEndObject()
    )
    ms.ToArray()

  interface IBillingProvider with
    member _.Name = "file"
    member _.Report events = async {
      try
        lock writeLock (fun () ->
          use fs =
            new FileStream(path, FileMode.Append, FileAccess.Write,
                           FileShare.Read, 4096, FileOptions.WriteThrough)
          for ev in events do
            let line = writeEvent ev
            fs.Write(line, 0, line.Length)
            fs.WriteByte(byte '\n')
        )
        return Ok events.Length
      with ex ->
        return Error ex.Message
    }

  /// Read the last `n` events out of the JSONL file (admin tail).
  member _.Tail (n : int) : UsageEvent[] =
    if not (File.Exists path) then [||]
    else
      let lines =
        try File.ReadAllLines path with _ -> [||]
      let take = min lines.Length (max 0 n)
      let start = lines.Length - take
      let out = ResizeArray<UsageEvent>()
      for i in start .. lines.Length - 1 do
        let line = lines.[i]
        if not (String.IsNullOrWhiteSpace line) then
          try
            use doc = JsonDocument.Parse(line : string)
            let r = doc.RootElement
            let getStr (name : string) =
              match r.TryGetProperty name with
              | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
              | _ -> ""
            let getLong (name : string) =
              match r.TryGetProperty name with
              | true, v when v.ValueKind = JsonValueKind.Number ->
                let mutable n = 0L
                if v.TryGetInt64 &n then n else 0L
              | _ -> 0L
            let parseKind = function
              | "ingestBytes"  -> Some IngestBytes
              | "logBytes"     -> Some LogBytes
              | "activeSeries" -> Some ActiveSeries
              | "traceSpans"   -> Some TraceSpans
              | "alertEvals"   -> Some AlertEvals
              | "seats"        -> Some Seats
              | _              -> None
            match parseKind (getStr "kind"), tryParsePlan (getStr "plan") with
            | Some k, Some p ->
              out.Add {
                tenantId    = TenantId (getStr "tenant")
                plan        = p
                kind        = k
                periodStart = DateTimeOffset.Parse (getStr "periodStart")
                periodEnd   = DateTimeOffset.Parse (getStr "periodEnd")
                quantity    = getLong "quantity" }
            | _ -> ()
          with _ -> ()
      out.ToArray()

  member this.Path = path

// -- Rollup loop ------------------------------------------------------------

/// Run a background loop that drains the meter at `intervalSec` and ships
/// results to every provider. The cadence is clamped to ≥ 5 s so a
/// misconfiguration can't busy-loop. Errors are swallowed; honors the
/// cancellation token.
let startRollupLoop (meter     : IBillingMeter)
                    (providers : IBillingProvider[])
                    (planFor   : TenantId -> Plan)
                    (intervalSec : int)
                    (ct        : CancellationToken) : Task =
  let delay = max 5 intervalSec
  Task.Run(System.Func<Task>(fun () -> task {
    let mutable periodStart = DateTimeOffset.UtcNow
    while not ct.IsCancellationRequested do
      try
        do! Task.Delay(TimeSpan.FromSeconds(float delay), ct)
      with :? OperationCanceledException -> ()
      if not ct.IsCancellationRequested then
        try
          let periodEnd = DateTimeOffset.UtcNow
          let events = meter.Drain (planFor, periodStart, periodEnd)
          periodStart <- periodEnd
          if events.Length > 0 then
            for p in providers do
              try
                let! _ = p.Report events
                ()
              with _ -> ()
        with _ -> ()
  }))

/// Synchronous flush helper used by `POST /api/admin/billing/flush`.
/// Returns the number of events emitted.
let flushNow (meter : IBillingMeter)
             (providers : IBillingProvider[])
             (planFor : TenantId -> Plan) : int =
  let now = DateTimeOffset.UtcNow
  let events = meter.Drain (planFor, now, now)
  for p in providers do
    try
      p.Report events |> Async.RunSynchronously |> ignore
    with _ -> ()
  events.Length
