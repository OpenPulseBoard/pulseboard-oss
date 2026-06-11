module PulseBoard.Quotas

open System
open System.Collections.Concurrent
open PulseBoard.Tenancy

// Per-tenant resource limits enforced at the edge.
//
// Today: ingest RPS, query RPS, alert evaluation RPS, log bytes/day, and
// active series cardinality. All RPS-style limits share a token-bucket
// implementation (see `Limiter.TryAcquire`); cardinality is a hard
// "distinct names per tenant" cap with its own admission API.
//
// Per-tenant overrides live in an in-memory map fed by an optional
// `IOverrideRepo` (Postgres in production, in-memory in dev). Writes go
// through the repo synchronously so the database is the source of truth.
//
// Concurrency: each bucket carries its own object lock; the outer dicts
// are `ConcurrentDictionary` so distinct tenants never contend.

type Kind =
  | Ingest
  | Query
  | AlertEval
  | LogBytes

let kindStr = function
  | Ingest    -> "ingest"
  | Query     -> "query"
  | AlertEval -> "alertEval"
  | LogBytes  -> "logBytes"

let tryParseKind = function
  | "ingest"    -> Some Ingest
  | "query"     -> Some Query
  | "alertEval" -> Some AlertEval
  | "logBytes"  -> Some LogBytes
  | _           -> None

let allKinds = [| Ingest; Query; AlertEval; LogBytes |]

[<NoComparison; NoEquality>]
type Limit =
  { /// Maximum tokens the bucket can hold (i.e. instantaneous burst).
    capacity     : float
    /// Tokens added per second.
    refillPerSec : float }

let disabled : Limit = { capacity = 0.0; refillPerSec = 0.0 }
let isDisabled (l : Limit) = l.capacity <= 0.0

[<RequireQualifiedAccess>]
type AcquireResult =
  | Ok
  /// Not enough tokens; suggested wait before retrying, rounded up to ms.
  | Throttled of retryAfterMs : int

[<RequireQualifiedAccess>]
type CardinalityResult =
  | Ok
  /// New series rejected because admitting it would exceed `cap`.
  | Rejected of cap : int

/// Persistence shim for the per-tenant override table. `LoadAll` returns
/// the union of rate + cardinality overrides per tenant; `kind = None`
/// means the row is the cardinality cap (`capacity` carries the integer
/// count).
type IOverrideRepo =
  abstract LoadAll           : unit -> (TenantId * Kind option * Limit) seq
  abstract UpsertRate        : tenantId : TenantId * kind : Kind * limit : Limit -> unit
  abstract ClearRate         : tenantId : TenantId * kind : Kind -> unit
  abstract UpsertCardinality : tenantId : TenantId * cap : int -> unit
  abstract ClearCardinality  : tenantId : TenantId -> unit

/// In-memory `IOverrideRepo`. Used when no Postgres connection is
/// configured; all overrides vaporise on restart.
type InMemoryOverrideRepo () =
  interface IOverrideRepo with
    member _.LoadAll () = Seq.empty
    member _.UpsertRate (_, _, _) = ()
    member _.ClearRate (_, _) = ()
    member _.UpsertCardinality (_, _) = ()
    member _.ClearCardinality _ = ()

[<NoComparison; NoEquality>]
type Effective =
  { rates                 : Map<Kind, Limit>
    cardinality           : int
    rateOverrides         : Set<Kind>
    cardinalityOverridden : bool }

type QuotaStore (defaults           : Map<Kind, Limit>,
                 cardinalityDefault : int,
                 repo               : IOverrideRepo) =

  let rateOverrides =
    ConcurrentDictionary<struct (TenantId * Kind), Limit>()
  let cardOverrides =
    ConcurrentDictionary<TenantId, int>()

  do
    for tid, kindOpt, lim in repo.LoadAll () do
      match kindOpt with
      | Some k -> rateOverrides.[struct (tid, k)] <- lim
      | None   -> cardOverrides.[tid] <- int lim.capacity

  member _.DefaultLimit (kind : Kind) : Limit =
    match Map.tryFind kind defaults with
    | Some l -> l
    | None   -> disabled

  member _.DefaultCardinality : int = cardinalityDefault

  member this.LimitFor (tenantId : TenantId, kind : Kind) : Limit =
    match rateOverrides.TryGetValue (struct (tenantId, kind)) with
    | true, l -> l
    | _       -> this.DefaultLimit kind

  member this.CardinalityFor (tenantId : TenantId) : int =
    match cardOverrides.TryGetValue tenantId with
    | true, n -> n
    | _       -> this.DefaultCardinality

  member this.Effective (tenantId : TenantId) : Effective =
    let rates =
      allKinds
      |> Array.map (fun k -> k, this.LimitFor (tenantId, k))
      |> Map.ofArray
    let ovKinds =
      allKinds
      |> Array.filter (fun k ->
          rateOverrides.ContainsKey (struct (tenantId, k)))
      |> Set.ofArray
    { rates                 = rates
      cardinality           = this.CardinalityFor tenantId
      rateOverrides         = ovKinds
      cardinalityOverridden = cardOverrides.ContainsKey tenantId }

  /// Set or clear a per-tenant rate override. `None` reverts to the
  /// process default for that kind.
  member _.SetRateOverride (tenantId : TenantId, kind : Kind,
                            limit : Limit option) =
    match limit with
    | Some l ->
      rateOverrides.[struct (tenantId, kind)] <- l
      repo.UpsertRate (tenantId, kind, l)
    | None ->
      rateOverrides.TryRemove (struct (tenantId, kind)) |> ignore
      repo.ClearRate (tenantId, kind)

  /// Set or clear a per-tenant cardinality cap. `None` reverts to the
  /// process default; `Some 0` means unlimited.
  member _.SetCardinalityOverride (tenantId : TenantId, cap : int option) =
    match cap with
    | Some n when n >= 0 ->
      cardOverrides.[tenantId] <- n
      repo.UpsertCardinality (tenantId, n)
    | Some _ ->
      invalidArg "cap" "must be non-negative (0 = unlimited)"
    | None ->
      cardOverrides.TryRemove tenantId |> ignore
      repo.ClearCardinality tenantId

[<NoComparison; NoEquality>]
type private Bucket =
  { mutable tokens   : float
    mutable lastTick : int64 }

let private ticksPerSec = float TimeSpan.TicksPerSecond

type Limiter (store : QuotaStore) =
  let buckets = ConcurrentDictionary<struct (TenantId * Kind), Bucket>()
  // Per-tenant active-series set: dictionary keys form the set, the
  // value byte is unused. Distinct-name counting is a simple proxy for
  // "active series" — sufficient for Phase 1 since this codebase models
  // a series as a metric name.
  let series =
    ConcurrentDictionary<TenantId, ConcurrentDictionary<string, byte>>()

  let now () = DateTime.UtcNow.Ticks

  let stepAndTake (b : Bucket) (limit : Limit) (cost : float) =
    lock b (fun () ->
      let n = now ()
      let elapsed = float (n - b.lastTick) / ticksPerSec
      if elapsed > 0.0 then
        b.tokens   <- min limit.capacity (b.tokens + elapsed * limit.refillPerSec)
        b.lastTick <- n
      if b.tokens + 1e-9 >= cost then
        b.tokens <- b.tokens - cost
        AcquireResult.Ok
      else
        let deficit = cost - b.tokens
        let waitSec =
          if limit.refillPerSec <= 0.0 then 3600.0
          else deficit / limit.refillPerSec
        let ms = int (ceil (waitSec * 1000.0))
        AcquireResult.Throttled (max 1 ms))

  member _.Store = store

  /// Try to acquire `cost` tokens from the (`tenantId`, `kind`) bucket.
  member _.TryAcquire (tenantId : TenantId, kind : Kind, ?cost : float) =
    let cost = defaultArg cost 1.0
    let limit = store.LimitFor (tenantId, kind)
    if isDisabled limit then AcquireResult.Ok
    else
      let b =
        buckets.GetOrAdd(
          struct (tenantId, kind),
          fun _ -> { tokens = limit.capacity; lastTick = now () })
      stepAndTake b limit cost

  /// Admit a new (or existing) series for `tenantId`. Returns `Ok` if the
  /// series was already tracked or the cap allows admitting it; `Rejected`
  /// otherwise. A cap of `0` means unlimited.
  member _.TryAdmitSeries (tenantId : TenantId, seriesName : string) =
    let cap = store.CardinalityFor tenantId
    let set =
      series.GetOrAdd(
        tenantId,
        fun _ -> ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
    if set.ContainsKey seriesName then CardinalityResult.Ok
    elif cap <= 0 then
      set.TryAdd(seriesName, 0uy) |> ignore
      CardinalityResult.Ok
    elif set.Count >= cap then
      // Two concurrent admits at the cap may both win, briefly exceeding
      // it by one. Acceptable for an edge defence.
      CardinalityResult.Rejected cap
    else
      set.TryAdd(seriesName, 0uy) |> ignore
      CardinalityResult.Ok

  /// Currently-tracked distinct series count for `tenantId`.
  member _.SeriesCountFor (tenantId : TenantId) =
    match series.TryGetValue tenantId with
    | true, set -> set.Count
    | _         -> 0
