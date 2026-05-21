module PulseBoard.Quotas

open System
open System.Collections.Concurrent
open PulseBoard.Tenancy

// Per-tenant token-bucket rate limits at the edge. PLAN.md Phase 1 step 5.
//
// Today: ingest RPS and query RPS. Each kind has its own bucket per tenant
// so a noisy ingest stream can't starve queries (and vice versa). Future
// kinds (cardinality, log GiB/day, alert eval rps) slot in by adding cases
// to `Kind` and writing the relevant cost-accounting at the call sites.
//
// Buckets refill continuously at `refillPerSec`. Acquiring `cost` tokens
// succeeds when the bucket has at least `cost` tokens available; otherwise
// we return the projected wait until enough tokens accrue. Capacity acts as
// the burst allowance: callers can briefly exceed the steady-state rate.
//
// Concurrency: each bucket carries its own object lock; the outer dict is
// `ConcurrentDictionary` so distinct tenants never contend.

type Kind =
  | Ingest
  | Query

[<NoComparison; NoEquality>]
type Limit =
  { /// Maximum tokens the bucket can hold (i.e. instantaneous burst).
    capacity      : float
    /// Tokens added per second.
    refillPerSec  : float }

let kindStr = function
  | Ingest -> "ingest"
  | Query  -> "query"

/// `Limit` with `capacity = 0.0` disables the bucket (every acquire passes).
/// Useful to opt out per kind without threading `Option<Limit>` through the
/// call sites.
let disabled : Limit = { capacity = 0.0; refillPerSec = 0.0 }
let isDisabled (l : Limit) = l.capacity <= 0.0

[<NoComparison; NoEquality>]
type private Bucket =
  { mutable tokens   : float
    mutable lastTick : int64 }   // Stopwatch ticks

let private ticksPerSec = float TimeSpan.TicksPerSecond

type IQuotaStore =
  /// Resolve the configured limit for a (tenant, kind). Implementations may
  /// consult a per-tenant override table; the simple default returns the
  /// same limit for every tenant.
  abstract LimitFor : tenantId : TenantId * kind : Kind -> Limit

type DefaultQuotaStore (ingest : Limit, query : Limit) =
  interface IQuotaStore with
    member _.LimitFor (_tenantId, kind) =
      match kind with
      | Ingest -> ingest
      | Query  -> query

[<RequireQualifiedAccess>]
type AcquireResult =
  | Ok
  /// Not enough tokens; suggested wait before retrying, rounded up to ms.
  | Throttled of retryAfterMs : int

type Limiter (store : IQuotaStore) =
  let buckets = ConcurrentDictionary<struct (TenantId * Kind), Bucket>()

  let now () = DateTime.UtcNow.Ticks

  /// Mutates the bucket in place under its own lock.
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
          if limit.refillPerSec <= 0.0 then 3600.0  // effectively never
          else deficit / limit.refillPerSec
        let ms = int (ceil (waitSec * 1000.0))
        AcquireResult.Throttled (max 1 ms))

  /// Try to acquire `cost` tokens from the (`tenantId`, `kind`) bucket.
  /// `cost` defaults to 1.0 for a single request; ingest batch handlers may
  /// pass a higher cost (e.g. number of points) to charge proportionally.
  member _.TryAcquire (tenantId : TenantId, kind : Kind, ?cost : float) =
    let cost = defaultArg cost 1.0
    let limit = store.LimitFor (tenantId, kind)
    if isDisabled limit then AcquireResult.Ok
    else
      let b =
        buckets.GetOrAdd(
          struct (tenantId, kind),
          fun _ ->
            // Start full so a tenant's first request is never throttled.
            { tokens = limit.capacity; lastTick = now () })
      stepAndTake b limit cost
