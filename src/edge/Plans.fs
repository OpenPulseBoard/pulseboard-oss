module PulseBoard.Plans

open PulseBoard.Tenancy
open PulseBoard.Quotas

// Phase 7 #2 — commercial plan catalog.
//
// Plans live on the `Tenant` record (`Tenancy.Plan`); this module owns the
// per-plan defaults and feature gates. Nothing else in the edge encodes
// "what does Pro mean" — callers ask `defaultRate`, `defaultCardinality`,
// `allows`, `softCap`, `hardCap`. Numbers below are deliberately conservative
// for OSS; production overrides land via `--plan-*` flags or the existing
// per-tenant quota override surface.

[<RequireQualifiedAccess>]
type Feature =
  /// SSO/OIDC login on the tenant (Pro+).
  | Sso
  /// Bring-your-own KMS key for envelope encryption (Enterprise only).
  | Byok
  /// Audited tenant impersonation by staff (Enterprise only; Phase 7 #6).
  | Impersonation
  /// Custom hostname / BYO TLS cert (Enterprise only; Phase 6 #2).
  | CustomDomain

/// Feature entitlement check. `Free` keeps only the OSS surface; `Pro`
/// adds SSO; `Enterprise` unlocks the rest.
let allows (plan : Plan) (f : Feature) : bool =
  match plan, f with
  | Enterprise, _                  -> true
  | Pro,        Feature.Sso        -> true
  | _,          _                  -> false

// -- Default rate limits (token-bucket) ------------------------------------
// `capacity` = burst tokens; `refillPerSec` = sustained rate. Numbers are
// the *plan-level baseline* used when no per-tenant override exists. They
// stack with `Quotas.QuotaStore` overrides exactly the same way: an explicit
// per-tenant cap always wins.

let private mk capacity refill : Limit =
  { capacity = capacity; refillPerSec = refill }

let defaultRate (plan : Plan) (kind : Kind) : Limit =
  match plan, kind with
  // Free — generous enough for a hobby project, tight enough to prevent
  // accidental abuse on the shared cluster.
  | Free, Ingest    -> mk 200.0      100.0
  | Free, Query     -> mk  40.0       20.0
  | Free, AlertEval -> mk  20.0       10.0
  | Free, LogBytes  -> mk (1.0 * 1024.0 * 1024.0)  (256.0 * 1024.0)  // 1 MiB burst, 256 KiB/s

  // Pro — startup-shaped: handles a few hundred services comfortably.
  | Pro,  Ingest    -> mk 2_000.0   1_000.0
  | Pro,  Query     -> mk   400.0     200.0
  | Pro,  AlertEval -> mk   200.0     100.0
  | Pro,  LogBytes  -> mk (16.0 * 1024.0 * 1024.0) (4.0  * 1024.0 * 1024.0) // 16 MiB / 4 MiB/s

  // Enterprise — effectively unmetered at the edge; real caps come from
  // explicit per-tenant overrides backed by the customer contract.
  | Enterprise, Ingest    -> mk 50_000.0  25_000.0
  | Enterprise, Query     -> mk 10_000.0   5_000.0
  | Enterprise, AlertEval -> mk  5_000.0   2_500.0
  | Enterprise, LogBytes  -> mk (256.0 * 1024.0 * 1024.0) (64.0 * 1024.0 * 1024.0)

let defaultCardinality (plan : Plan) : int =
  match plan with
  | Free       ->     10_000
  | Pro        ->    250_000
  | Enterprise ->  5_000_000

// -- Billing soft / hard caps ----------------------------------------------
// Soft cap = the plan's "expected monthly" envelope; crossing it produces a
// warning header + overage email (Phase 7 #1). Hard cap = soft × 1.5; crossing
// it returns 429 on the affected ingest path and audits a `Deny`. Units are
// raw counts that match `Billing.UsageKind`.

let ingestBytesSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->          5L * 1024L * 1024L * 1024L           //   5 GiB / month
  | Pro        ->        250L * 1024L * 1024L * 1024L           // 250 GiB / month
  | Enterprise ->  System.Int64.MaxValue                        // contract-bound

let logBytesSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->          1L * 1024L * 1024L * 1024L           //   1 GiB / month
  | Pro        ->        100L * 1024L * 1024L * 1024L           // 100 GiB / month
  | Enterprise ->  System.Int64.MaxValue

let activeSeriesSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->     10_000L
  | Pro        ->    250_000L
  | Enterprise ->  System.Int64.MaxValue

let traceSpansSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->       1_000_000L                              //   1M / month
  | Pro        ->      50_000_000L                              //  50M / month
  | Enterprise ->  System.Int64.MaxValue

let alertEvalsSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->      10_000_000L
  | Pro        ->     500_000_000L
  | Enterprise ->  System.Int64.MaxValue

let seatsSoftCap (plan : Plan) : int64 =
  match plan with
  | Free       ->          3L
  | Pro        ->         25L
  | Enterprise ->  System.Int64.MaxValue

/// Scale a soft cap to its hard cap. Returns `MaxValue` unchanged so
/// Enterprise stays unbounded.
let toHardCap (soft : int64) : int64 =
  if soft = System.Int64.MaxValue then soft
  else
    // 1.5× rounded down; saturates instead of overflowing.
    let scaled = soft / 2L * 3L
    if scaled < soft then System.Int64.MaxValue else scaled
