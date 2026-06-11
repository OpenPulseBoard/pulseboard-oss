module PulseBoard.Storage

open System
open System.Collections.Concurrent
open System.Threading
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Quotas

// Pluggable storage backends and
// cardinality enforcement (Phase 3 step 2).
//
// `IMetricBackend` / `ILogBackend` / `ITraceBackend` decouple the
// receiver-facing `IStorageClient` (see Gateway.fs) from the underlying
// store. Today the only implementations are `Embedded*Backend` which
// wrap the in-memory `MetricStore` / `LogStore` / a per-tenant trace
// counter — i.e. today's behaviour, just behind an interface so a
// follow-up commit can drop in a Mimir / Loki / Tempo client without
// touching receivers.
//
// Cardinality control is enforced here, in `EmbeddedMetricBackend`,
// because (a) the limiter already counts distinct series per tenant
// via `Limiter.TryAdmitSeries` (Phase 1) and (b) any cloud backend
// will impose its own series budget anyway — the wrapping backend is
// the right place to mediate.

[<RequireQualifiedAccess>]
type WriteOutcome =
  /// Sample was written.
  | Accepted
  /// Sample was rejected because admitting a new series would exceed
  /// the tenant's active-series cap. `cap` is the effective cap at the
  /// time of the rejection.
  | DroppedCardinality of cap : int

type IMetricBackend =
  /// Record a single sample for `tenantId`. Returns whether the sample
  /// was accepted; callers should treat `DroppedCardinality` as a
  /// silent drop (mirrors Prometheus / Mimir behaviour) and increment
  /// any "samples dropped" counter on their side if they want
  /// fine-grained visibility.
  abstract Record    : tenantId : string * name : string * point : Point -> WriteOutcome
  /// All currently-known metric names (global today; per-tenant when a
  /// cloud backend lands).
  abstract Names     : unit -> string[]
  abstract Get       : name : string -> Point[]
  abstract GetSince  : name : string * sinceMs : int64 -> Point[]
  /// Distinct series currently tracked for `tenantId` (0 when no
  /// cardinality limiter is wired).
  abstract SeriesCount        : tenantId : string -> int
  /// Monotonically-increasing count of samples dropped for `tenantId`
  /// because of cardinality rejection. Resets on process restart.
  abstract DroppedCardinality : tenantId : string -> int64
  /// Names of all metrics visible to `tenantId`.
  /// In-process backends return the same as `Names()`; cloud backends
  /// (e.g. Mimir) scope the call to the given tenant.
  abstract NamesFor    : tenantId : string -> string[]
  /// Points for `name` in `tenantId`'s series since `sinceMs` (ms).
  /// In-process backends delegate to `GetSince`.
  abstract GetSinceFor : tenantId : string * name : string * sinceMs : int64 -> Point[]

type ILogBackend =
  abstract Add  : tenantId : string * entry : LogEntry -> unit
  abstract Tail : count : int -> LogEntry[]

type ITraceBackend =
  abstract IncCount : tenantId : string * count : int -> unit
  abstract Count    : tenantId : string -> int64


/// In-process backend over `MetricStore`. When `limiter` is supplied,
/// every Record first asks `TryAdmitSeries`; rejection skips the write
/// and bumps the per-tenant drop counter. With no limiter the cap is
/// effectively unlimited (single-tenant / dev mode).
type EmbeddedMetricBackend(store : MetricStore, limiter : Limiter option) =
  // Per-tenant rejected-samples counter. The `ref` cell is mutated via
  // Interlocked; ConcurrentDictionary makes the outer slot lookup safe.
  let dropped = ConcurrentDictionary<string, int64 ref>()

  let bumpDrop (tid : string) =
    let cell = dropped.GetOrAdd(tid, fun _ -> ref 0L)
    Interlocked.Increment(&cell.contents) |> ignore

  member _.UnderlyingStore = store

  interface IMetricBackend with
    member _.Record(tid, name, p) =
      match limiter with
      | Some lim ->
        match lim.TryAdmitSeries(TenantId tid, name) with
        | CardinalityResult.Ok ->
          store.Record(name, p)
          WriteOutcome.Accepted
        | CardinalityResult.Rejected cap ->
          bumpDrop tid
          WriteOutcome.DroppedCardinality cap
      | None ->
        store.Record(name, p)
        WriteOutcome.Accepted

    member _.Names() = store.Names()
    member _.Get name = store.Get name
    member _.GetSince(name, since) = store.GetSince(name, since)

    member _.SeriesCount tid =
      match limiter with
      | Some lim -> lim.SeriesCountFor(TenantId tid)
      | None     -> 0

    member _.DroppedCardinality tid =
      match dropped.TryGetValue tid with
      | true, cell -> Volatile.Read &cell.contents
      | _          -> 0L

    member _.NamesFor _              = store.Names()
    member _.GetSinceFor(_, name, s) = store.GetSince(name, s)


type EmbeddedLogBackend(store : LogStore) =
  member _.UnderlyingStore = store
  interface ILogBackend with
    member _.Add(_, e) = store.Add e
    member _.Tail n    = store.Tail n


type EmbeddedTraceBackend() =
  let counters = ConcurrentDictionary<string, int64 ref>()
  interface ITraceBackend with
    member _.IncCount(tid, n) =
      if n <> 0 then
        let cell = counters.GetOrAdd(tid, fun _ -> ref 0L)
        Interlocked.Add(&cell.contents, int64 n) |> ignore
    member _.Count tid =
      match counters.TryGetValue tid with
      | true, cell -> Volatile.Read &cell.contents
      | _          -> 0L
