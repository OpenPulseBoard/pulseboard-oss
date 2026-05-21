module PulseBoard.Audit

open System
open System.Text.Json
open PulseBoard.Tenancy

// Append-only audit trail. The in-memory ring keeps the last `capacity`
// events for `GET /api/admin/audit`. Persisted Postgres + nightly S3 export
// land in a later pass (PLAN.md Phase 1 step 4).

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
