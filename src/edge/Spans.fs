module PulseBoard.Spans

open System
open System.Collections.Concurrent
open System.Collections.Generic
open PulseBoard.Tenancy

// Phase 4 #4 (traces / service map / RUM).
//
// PulseBoard's existing `Otlp.traces` handler counted spans without
// storing them — fine for billing, useless for product. This module
// adds an in-process span store so we can:
//
//   * surface recent traces in the UI (Traces tab),
//   * render a derived service map (nodes = services, edges =
//     parent->child calls with RED stats),
//   * answer ad-hoc "show me this traceId" lookups from the SPA.
//
// Storage is deliberately in-memory + capped. Real production
// deployments swap to a Tempo upstream via `--tempo-url=` and the
// existing `IRawTraceBackend` passthrough — at that point this store
// is a hot cache, not the system of record.
//
// We never persist spans across restarts (yet); the SPA tabs will
// just show "no traces seen since boot" after a restart. That matches
// every other in-process embedded store in the codebase
// (`MetricStore`, `LogStore`) and keeps Phase 4 #4 honest about its
// scope.

// -- model ------------------------------------------------------------------

[<Struct>]
type SpanKind =
  | KindUnspecified
  | KindInternal
  | KindServer
  | KindClient
  | KindProducer
  | KindConsumer

let kindName = function
  | KindUnspecified -> "unspecified"
  | KindInternal    -> "internal"
  | KindServer      -> "server"
  | KindClient      -> "client"
  | KindProducer    -> "producer"
  | KindConsumer    -> "consumer"

let kindOfInt = function
  | 1 -> KindInternal
  | 2 -> KindServer
  | 3 -> KindClient
  | 4 -> KindProducer
  | 5 -> KindConsumer
  | _ -> KindUnspecified

[<NoComparison>]
type Span =
  { traceId      : string            // 32-char lowercase hex
    spanId       : string            // 16-char lowercase hex
    parentSpanId : string            // "" when root
    service      : string            // resource.service.name (or "unknown")
    operation    : string            // span.name
    kind         : SpanKind
    startMs      : int64
    endMs        : int64
    statusCode   : int               // 0=unset 1=ok 2=error
    attributes   : Map<string,string> }

let duration (s : Span) = max 0L (s.endMs - s.startMs)
let isError  (s : Span) = s.statusCode = 2

[<NoComparison>]
type TraceSummary =
  { traceId       : string
    rootService   : string
    rootOperation : string
    startMs       : int64
    durationMs    : int64
    spanCount     : int
    errorCount    : int
    services      : string array }

[<NoComparison>]
type ServiceMapNode =
  { service    : string
    spanCount  : int
    errorCount : int
    p50Ms      : float
    p95Ms      : float
    p99Ms      : float }

[<NoComparison>]
type ServiceMapEdge =
  { fromService : string
    toService   : string
    callCount   : int
    errorCount  : int
    p50Ms       : float
    p95Ms       : float
    p99Ms       : float }

[<NoComparison>]
type ServiceMap =
  { nodes       : ServiceMapNode array
    edges       : ServiceMapEdge array
    sinceMs     : int64
    generatedMs : int64 }

// -- aggregation helpers ----------------------------------------------------

let private percentile (sorted : float array) (p : float) : float =
  if sorted.Length = 0 then 0.0
  else
    let idx = int (floor (p * float (sorted.Length - 1)))
    sorted.[min (sorted.Length - 1) (max 0 idx)]

let summarise (spans : Span array) : TraceSummary =
  let traceId = if spans.Length = 0 then "" else spans.[0].traceId
  // Root = the (first) span with empty / unknown parent in this set.
  let bySpanId =
    spans
    |> Array.map (fun s -> s.spanId, s)
    |> Map.ofArray
  let isRoot (s : Span) =
    String.IsNullOrEmpty s.parentSpanId
    || not (bySpanId.ContainsKey s.parentSpanId)
  let root =
    spans
    |> Array.tryFind isRoot
    |> Option.defaultWith (fun () -> spans.[0])
  let startMs = spans |> Array.map (fun s -> s.startMs) |> Array.min
  let endMs   = spans |> Array.map (fun s -> s.endMs)   |> Array.max
  let errors  = spans |> Array.filter isError |> Array.length
  let services =
    spans |> Array.map (fun s -> s.service) |> Array.distinct |> Array.sort
  { traceId       = traceId
    rootService   = root.service
    rootOperation = root.operation
    startMs       = startMs
    durationMs    = max 0L (endMs - startMs)
    spanCount     = spans.Length
    errorCount    = errors
    services      = services }

let buildMap (spans : Span array) (sinceMs : int64) : ServiceMap =
  // 1) Group spans by (traceId, spanId) for O(1) parent lookup.
  let bySpanId = Dictionary<string, Span>()
  for s in spans do bySpanId.[s.traceId + "/" + s.spanId] <- s

  // 2) Per-service latency samples for the node summary.
  let nodeSamples = Dictionary<string, ResizeArray<float>>()
  let nodeErrors  = Dictionary<string, int>()
  // 3) Per-edge (caller -> callee) samples. We only follow parent links
  //    inside the same trace, hence the composite key.
  let edgeSamples = Dictionary<string * string, ResizeArray<float>>()
  let edgeErrors  = Dictionary<string * string, int>()

  let bump (d : Dictionary<_, ResizeArray<float>>) k v =
    let xs =
      match d.TryGetValue k with
      | true, ra -> ra
      | _        -> let ra = ResizeArray<float>() in d.[k] <- ra; ra
    xs.Add v

  let bumpErr (d : Dictionary<_, int>) k =
    let prev = match d.TryGetValue k with true, v -> v | _ -> 0
    d.[k] <- prev + 1

  for s in spans do
    let dur = float (duration s)
    bump nodeSamples s.service dur
    if isError s then bumpErr nodeErrors s.service
    if not (String.IsNullOrEmpty s.parentSpanId) then
      match bySpanId.TryGetValue (s.traceId + "/" + s.parentSpanId) with
      | true, parent when parent.service <> s.service ->
        let k = (parent.service, s.service)
        bump edgeSamples k dur
        if isError s then bumpErr edgeErrors k
      | _ -> ()

  let nodes =
    nodeSamples
    |> Seq.map (fun kv ->
      let arr = kv.Value.ToArray() |> Array.sort
      { service    = kv.Key
        spanCount  = arr.Length
        errorCount = (match nodeErrors.TryGetValue kv.Key with true,v -> v | _ -> 0)
        p50Ms      = percentile arr 0.50
        p95Ms      = percentile arr 0.95
        p99Ms      = percentile arr 0.99 })
    |> Seq.sortBy (fun n -> n.service)
    |> Seq.toArray

  let edges =
    edgeSamples
    |> Seq.map (fun kv ->
      let (fromS, toS) = kv.Key
      let arr = kv.Value.ToArray() |> Array.sort
      { fromService = fromS
        toService   = toS
        callCount   = arr.Length
        errorCount  = (match edgeErrors.TryGetValue kv.Key with true,v -> v | _ -> 0)
        p50Ms       = percentile arr 0.50
        p95Ms       = percentile arr 0.95
        p99Ms       = percentile arr 0.99 })
    |> Seq.sortBy (fun e -> e.fromService, e.toService)
    |> Seq.toArray

  { nodes = nodes
    edges = edges
    sinceMs = sinceMs
    generatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }

// -- store ------------------------------------------------------------------

type ISpanStore =
  abstract Ingest         : TenantId * Span seq -> unit
  abstract Traces         : TenantId * sinceMs:int64 * limit:int -> TraceSummary array
  abstract GetTrace       : TenantId * traceId:string -> Span array
  abstract Map            : TenantId * sinceMs:int64 -> ServiceMap
  abstract PruneOlderThan : cutoffMs:int64 -> int
  abstract Count          : TenantId -> int

/// In-memory bounded store. Per-tenant we keep at most `capacity`
/// spans in a deque (oldest evicted first). Lookups derive
/// `TraceSummary` / `ServiceMap` from a snapshot, so concurrent
/// ingest never blocks readers.
type InMemorySpanStore(capacity : int) =
  // We need both an exclusive lock and the underlying buffer per
  // tenant. Storing them together as a tuple inside the dict means
  // every reader/writer goes through the same lock instance for that
  // tenant.
  let tenants = ConcurrentDictionary<string, obj * ResizeArray<Span>>()

  let key (TenantId s) = s

  let bucket (tid : TenantId) =
    tenants.GetOrAdd(key tid, fun _ -> (obj (), ResizeArray<Span>()))

  interface ISpanStore with
    member _.Ingest (tid, spans) =
      let lk, ra = bucket tid
      lock lk (fun () ->
        for s in spans do
          ra.Add s
          if ra.Count > capacity then
            // Drop oldest 10% in one shot — bulk eviction is much
            // cheaper than removing index 0 per ingest.
            let drop = max 1 (capacity / 10)
            ra.RemoveRange(0, drop))

    member _.Traces (tid, sinceMs, limit) =
      let lk, ra = bucket tid
      let snap = lock lk (fun () -> ra.ToArray())
      snap
      |> Array.filter (fun s -> s.endMs >= sinceMs)
      |> Array.groupBy (fun s -> s.traceId)
      |> Array.map (fun (_, spans) -> summarise spans)
      |> Array.sortByDescending (fun t -> t.startMs)
      |> fun xs -> if xs.Length <= limit then xs else xs.[.. limit - 1]

    member _.GetTrace (tid, traceId) =
      let lk, ra = bucket tid
      let snap = lock lk (fun () -> ra.ToArray())
      snap
      |> Array.filter (fun s -> s.traceId = traceId)
      |> Array.sortBy (fun s -> s.startMs)

    member _.Map (tid, sinceMs) =
      let lk, ra = bucket tid
      let snap = lock lk (fun () -> ra.ToArray())
      let recent = snap |> Array.filter (fun s -> s.endMs >= sinceMs)
      buildMap recent sinceMs

    member _.PruneOlderThan cutoffMs =
      let mutable dropped = 0
      for kv in tenants do
        let lk, ra = kv.Value
        lock lk (fun () ->
          let survivors = ra |> Seq.filter (fun s -> s.endMs >= cutoffMs) |> Array.ofSeq
          dropped <- dropped + (ra.Count - survivors.Length)
          ra.Clear()
          ra.AddRange(survivors))
      dropped

    member _.Count tid =
      let lk, ra = bucket tid
      lock lk (fun () -> ra.Count)
