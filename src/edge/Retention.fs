module PulseBoard.Retention

open System
open System.Collections.Concurrent
open System.Threading
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Storage

// Per-tenant retention policies and the embedded-backend compactor
//.
//
// Shape:
//   * `RetentionPolicy` carries an optional TTL (milliseconds) per
//     pillar. `None` for a field means "fall back to the system default
//     for that pillar"; the default itself may also be `None` meaning
//     "keep forever / until the ring rolls over". Zero or negative TTLs
//     are treated as "keep forever" so operators can spell out "no
//     retention enforcement" without a special token.
//   * `IRetentionRepo` persists per-tenant overrides; an in-memory
//     impl is provided here and a Postgres impl lives in
//     `PgRetentionOverrides.fs`.
//   * `RetentionStore` is the in-memory authority for "effective TTL
//     for tenant X, pillar Y" — defaults merged with overrides.
//   * `EmbeddedCompactor` walks the in-process `MetricStore` /
//     `LogStore` on a timer and prunes anything older than the
//     effective horizon.
//
// Multi-tenant embedded caveat: the embedded `MetricStore` and
// `LogStore` are process-global, not keyed by tenant. The compactor
// therefore enforces the *most generous* TTL across all configured
// tenants for each pillar — i.e. it never drops data that *any*
// tenant's policy still wants. Per-tenant retention is fully honoured
// by the cloud backends (Mimir / Loki / Tempo) which have native
// tenant-aware lifecycle rules; the embedded compactor is
// a best-effort floor for the OSS / single-binary deployment.

[<NoComparison>]
type RetentionPolicy =
  { metricsMs : int64 option
    logsMs    : int64 option
    tracesMs  : int64 option }
  static member Empty =
    { metricsMs = None; logsMs = None; tracesMs = None }

[<NoComparison>]
type EffectivePolicy =
  { metricsMs            : int64 option
    logsMs               : int64 option
    tracesMs             : int64 option
    metricsOverridden    : bool
    logsOverridden       : bool
    tracesOverridden     : bool }

type IRetentionRepo =
  abstract LoadAll : unit -> (TenantId * RetentionPolicy) seq
  abstract Upsert  : tenantId : TenantId * policy : RetentionPolicy -> unit
  abstract Clear   : tenantId : TenantId -> unit

type InMemoryRetentionRepo() =
  interface IRetentionRepo with
    member _.LoadAll() = Seq.empty
    member _.Upsert(_, _) = ()
    member _.Clear _ = ()

/// Returns the policy with any non-positive TTL collapsed to `None`
/// (zero / negative = "keep forever / no enforcement").
let private normalise (p : RetentionPolicy) : RetentionPolicy =
  let clean = function
    | Some n when n > 0L -> Some n
    | _                  -> None
  { metricsMs = clean p.metricsMs
    logsMs    = clean p.logsMs
    tracesMs  = clean p.tracesMs }

type RetentionStore(defaults : RetentionPolicy, repo : IRetentionRepo) =

  let defaults = normalise defaults
  let overrides = ConcurrentDictionary<TenantId, RetentionPolicy>()

  do
    for tid, p in repo.LoadAll() do
      overrides.[tid] <- normalise p

  member _.Defaults : RetentionPolicy = defaults

  /// Set or clear the per-field overrides for `tenantId`. Fields left
  /// at `None` on the supplied policy fall back to the system default.
  /// Passing an all-`None` policy effectively clears the override row.
  member _.SetOverride(tenantId : TenantId, policy : RetentionPolicy) =
    let p = normalise policy
    if p.metricsMs.IsNone && p.logsMs.IsNone && p.tracesMs.IsNone then
      overrides.TryRemove tenantId |> ignore
      repo.Clear tenantId
    else
      overrides.[tenantId] <- p
      repo.Upsert(tenantId, p)

  member _.ClearOverride(tenantId : TenantId) =
    overrides.TryRemove tenantId |> ignore
    repo.Clear tenantId

  member _.Effective(tenantId : TenantId) : EffectivePolicy =
    let ov =
      match overrides.TryGetValue tenantId with
      | true, p -> p
      | _       -> RetentionPolicy.Empty
    let pick (o : int64 option) (d : int64 option) =
      match o with
      | Some _ -> o
      | None   -> d
    { metricsMs         = pick ov.metricsMs defaults.metricsMs
      logsMs            = pick ov.logsMs    defaults.logsMs
      tracesMs          = pick ov.tracesMs  defaults.tracesMs
      metricsOverridden = ov.metricsMs.IsSome
      logsOverridden    = ov.logsMs.IsSome
      tracesOverridden  = ov.tracesMs.IsSome }

  /// Snapshot of currently-overridden tenants (used by the compactor
  /// to compute the most-generous horizon across known overrides).
  member _.OverrideTenants() : TenantId[] =
    overrides.Keys |> Seq.toArray

/// Largest TTL across the system default and every per-tenant override
/// for the given pillar. `None` means "keep forever" — any `None` in
/// the input set forces the result to `None`.
let private maxHorizon
  (defaults : int64 option)
  (overrideTtls : int64 option seq) : int64 option =
  let mutable result = defaults
  let mutable infinite = defaults.IsNone
  if not infinite then
    let mutable cur = defaults.Value
    for o in overrideTtls do
      if not infinite then
        match o with
        | None   -> infinite <- true
        | Some n -> if n > cur then cur <- n
    if infinite then result <- None
    else result <- Some cur
  // If defaults were already None, every tenant inherits "keep forever".
  result

/// Embedded compactor. Holds optional refs to the in-process stores
/// (they're `None` when the corresponding pillar has been swapped to
/// a cloud backend, in which case retention is enforced upstream).
type EmbeddedCompactor(retention   : RetentionStore,
                      metricStore : MetricStore option,
                      logStore    : LogStore option,
                      intervalMs  : int) =

  let cts = new CancellationTokenSource()
  let mutable lastMetricsDropped = 0
  let mutable lastLogsDropped    = 0
  let mutable lastRunAtMs        = 0L

  let horizonFor (pillar : RetentionPolicy -> int64 option) =
    let defaults = pillar retention.Defaults
    let tenants  = retention.OverrideTenants()
    let ttls =
      tenants
      |> Array.map (fun t -> pillar (retention.Effective(t) |> fun e ->
          { metricsMs = e.metricsMs; logsMs = e.logsMs; tracesMs = e.tracesMs }))
    maxHorizon defaults ttls

  /// Run one compaction pass. Returns `(metricsDropped, logsDropped)`.
  member _.CompactOnce() : int * int =
    let now = nowMs ()
    let metricsHorizon = horizonFor (fun p -> p.metricsMs)
    let logsHorizon    = horizonFor (fun p -> p.logsMs)
    let metricsDropped =
      match metricStore, metricsHorizon with
      | Some ms, Some ttl -> ms.PruneOlderThan(now - ttl)
      | _                 -> 0
    let logsDropped =
      match logStore, logsHorizon with
      | Some ls, Some ttl -> ls.PruneOlderThan(now - ttl)
      | _                 -> 0
    lastMetricsDropped <- metricsDropped
    lastLogsDropped    <- logsDropped
    lastRunAtMs        <- now
    metricsDropped, logsDropped

  member x.Start() =
    let loop = async {
      while not cts.IsCancellationRequested do
        try
          x.CompactOnce() |> ignore
        with ex ->
          eprintfn "[retention] compact failed: %s" ex.Message
        do! Async.Sleep intervalMs
    }
    Async.Start(loop, cts.Token)

  member _.LastMetricsDropped = lastMetricsDropped
  member _.LastLogsDropped    = lastLogsDropped
  member _.LastRunAtMs        = lastRunAtMs
  member _.IntervalMs         = intervalMs

  member _.Stop() =
    try cts.Cancel() with _ -> ()

  interface IDisposable with
    member x.Dispose() =
      x.Stop()
      cts.Dispose()
