module PulseBoard.Audit

open System
open System.Text.Json
open PulseBoard.Tenancy

// Append-only audit trail. The in-memory ring keeps the last `capacity`
// events for `GET /api/admin/audit`; durable persistence is provided by
// `PgAuditLog` and the nightly S3 export by `S3AuditExporter`. Sinks are
// composed via `CompositeAuditLog` (defined below) so admin tail still
// answers from the ring while every event is also persisted to Postgres.

type Outcome =
  | Allow
  | Deny
  | Error

[<NoComparison; NoEquality>]
type AuditEvent =
  { ts       : DateTimeOffset
    tenant   : TenantId option
    apiKeyId : ApiKeyId option
    /// Logical action name (`ingest`, `query`, `admin`, ...).
    action   : string
    /// Request path the decision applied to.
    resource : string
    outcome  : Outcome
    remoteIp : string option
    details  : string option }

type IAuditLog =
  abstract Append : AuditEvent -> unit
  abstract Tail   : count : int -> AuditEvent[]

/// Fan-out audit log. `Append` is best-effort dispatched to every inner
/// log (individual failures are swallowed so a flaky sink can't drop the
/// audit trail elsewhere). `Tail` is served from the first inner log,
/// which by convention is the in-memory ring — Postgres-backed sinks
/// return [||] from `Tail` since they're designed for long-term storage,
/// not paged reads.
type CompositeAuditLog (inner : IAuditLog[]) =
  do if isNull inner then nullArg "inner"
  interface IAuditLog with
    member _.Append ev =
      for l in inner do
        try l.Append ev with _ -> ()
    member _.Tail n =
      if inner.Length = 0 then [||]
      else inner.[0].Tail n

type InMemoryAuditLog (capacity : int) =
  do if capacity <= 0 then invalidArg "capacity" "must be > 0"
  let buf  = Array.zeroCreate<AuditEvent> capacity
  let lock' = obj ()
  let mutable head  = 0
  let mutable count = 0

  interface IAuditLog with
    member _.Append ev =
      lock lock' (fun () ->
        buf.[head] <- ev
        head <- (head + 1) % capacity
        if count < capacity then count <- count + 1)

    member _.Tail n =
      lock lock' (fun () ->
        let n = min (max n 0) count
        let result = Array.zeroCreate n
        let start  = (head - n + capacity) % capacity
        for i in 0 .. n - 1 do
          result.[i] <- buf.[(start + i) % capacity]
        result)

let private outcomeStr = function
  | Allow -> "allow"
  | Deny  -> "deny"
  | Error -> "error"

let private jsonStr (s : string) = JsonSerializer.Serialize s
let private jsonOpt = function
  | Some s -> jsonStr s
  | None   -> "null"

/// Stable JSON shape for an audit event. Hand-written to keep the field
/// order deterministic (helpful for log greppability and snapshot tests).
let serialize (ev : AuditEvent) : string =
  let tenant   = ev.tenant   |> Option.map (fun (TenantId t) -> t) |> jsonOpt
  let apiKeyId = ev.apiKeyId |> Option.map (fun (ApiKeyId k) -> k) |> jsonOpt
  sprintf
    """{"ts":"%s","tenant":%s,"apiKey":%s,"action":%s,"resource":%s,"outcome":"%s","remoteIp":%s,"details":%s}"""
    (ev.ts.ToString("o"))
    tenant
    apiKeyId
    (jsonStr ev.action)
    (jsonStr ev.resource)
    (outcomeStr ev.outcome)
    (jsonOpt ev.remoteIp)
    (jsonOpt ev.details)
