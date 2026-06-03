module PulseBoard.PgBillingStore

// Postgres-backed IBillingProvider. Usage events are appended to
// pb_billing_events. The upsert on (tenant_id, kind, period_start) makes
// Report idempotent: re-reporting the same period overwrites the quantity
// rather than duplicating rows, matching the file provider's intent.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Plans
open PulseBoard.Billing

let private schema = """
CREATE TABLE IF NOT EXISTS pb_billing_events (
  id           BIGSERIAL   PRIMARY KEY,
  tenant_id    TEXT        NOT NULL,
  plan         TEXT        NOT NULL,
  kind         TEXT        NOT NULL,
  period_start TIMESTAMPTZ NOT NULL,
  period_end   TIMESTAMPTZ NOT NULL,
  quantity     BIGINT      NOT NULL,
  UNIQUE (tenant_id, kind, period_start)
);
CREATE INDEX IF NOT EXISTS pb_billing_events_tenant_idx
  ON pb_billing_events (tenant_id, period_start DESC);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

type PgBillingProvider(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  interface IBillingProvider with

    member _.Name = "postgres"

    member _.Report (events : UsageEvent[]) = async {
      try
        use conn = openConn ()
        for ev in events do
          let (TenantId tenantId) = ev.tenantId
          use cmd =
            new NpgsqlCommand(
              "INSERT INTO pb_billing_events \
               (tenant_id, plan, kind, period_start, period_end, quantity) \
               VALUES (@tid, @plan, @kind, @ps, @pe, @qty) \
               ON CONFLICT (tenant_id, kind, period_start) \
               DO UPDATE SET quantity = EXCLUDED.quantity, \
                             period_end = EXCLUDED.period_end, \
                             plan = EXCLUDED.plan",
              conn)
          cmd.Parameters.AddWithValue("tid",  tenantId)              |> ignore
          cmd.Parameters.AddWithValue("plan", planToText ev.plan)    |> ignore
          cmd.Parameters.AddWithValue("kind", usageKindStr ev.kind)  |> ignore
          cmd.Parameters.AddWithValue("ps",   ev.periodStart)        |> ignore
          cmd.Parameters.AddWithValue("pe",   ev.periodEnd)          |> ignore
          cmd.Parameters.AddWithValue("qty",  ev.quantity)           |> ignore
          cmd.ExecuteNonQuery() |> ignore
        return Ok events.Length
      with ex ->
        return Error ex.Message
    }
