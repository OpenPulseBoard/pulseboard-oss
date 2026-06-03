module PulseBoard.PgRoutingStore

// Postgres-backed IConfigStore. One routing config document per tenant,
// stored as an opaque JSON body. UpsertSilence / DeleteSilence use a
// serialisable transaction to prevent lost-update races between concurrent
// silence edits.

open System
open Npgsql
open PulseBoard.Tenancy
open PulseBoard.Routing

let private schema = """
CREATE TABLE IF NOT EXISTS pb_routing_configs (
  tenant_id TEXT PRIMARY KEY,
  body      TEXT NOT NULL
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private tid (TenantId t) = t

let private defaultConfig () : Config =
  { route =
      { id = "root"; matchers = [||]; receiverId = None; policyId = None
        groupBy = [| "alertname"; "service" |]
        groupWaitMs = 30_000L; groupIntervalMs = 300_000L
        repeatIntervalMs = 3_600_000L
        continue_ = false; muteTimeIds = [||]; children = [||] }
    receivers   = [||]
    silences    = [||]
    inhibitions = [||]
    muteTimes   = [||] }

type PgConfigStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let getConfigInTx (tenantId : TenantId) (conn : NpgsqlConnection) (tx : NpgsqlTransaction) : Config =
    use cmd =
      new NpgsqlCommand(
        "SELECT body FROM pb_routing_configs WHERE tenant_id = @tid FOR UPDATE",
        conn, tx)
    cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
    let result = cmd.ExecuteScalar()
    if isNull result then defaultConfig ()
    else
      match parseConfig (result :?> string) with
      | Result.Ok c -> c
      | Result.Error _ -> defaultConfig ()

  let saveConfigInTx (tenantId : TenantId) (c : Config) (conn : NpgsqlConnection) (tx : NpgsqlTransaction) =
    let body = serialiseConfig c
    use cmd =
      new NpgsqlCommand(
        "INSERT INTO pb_routing_configs (tenant_id, body) VALUES (@tid, @body) \
         ON CONFLICT (tenant_id) DO UPDATE SET body = EXCLUDED.body",
        conn, tx)
    cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
    cmd.Parameters.AddWithValue("body", body)         |> ignore
    cmd.ExecuteNonQuery() |> ignore

  interface IConfigStore with

    member _.Get (tenantId : TenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT body FROM pb_routing_configs WHERE tenant_id = @tid",
          conn)
      cmd.Parameters.AddWithValue("tid", tid tenantId) |> ignore
      let result = cmd.ExecuteScalar()
      if isNull result then defaultConfig ()
      else
        match parseConfig (result :?> string) with
        | Result.Ok c -> c
        | Result.Error _ -> defaultConfig ()

    member _.Set (tenantId : TenantId, c : Config) =
      let body = serialiseConfig c
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_routing_configs (tenant_id, body) VALUES (@tid, @body) \
           ON CONFLICT (tenant_id) DO UPDATE SET body = EXCLUDED.body",
          conn)
      cmd.Parameters.AddWithValue("tid",  tid tenantId) |> ignore
      cmd.Parameters.AddWithValue("body", body)         |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member s.UpsertSilence (tenantId : TenantId, sil : Silence) =
      use conn = openConn ()
      use tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)
      let c = getConfigInTx tenantId conn tx
      let others = c.silences |> Array.filter (fun x -> x.id <> sil.id)
      let updated = { c with silences = Array.append others [| sil |] }
      saveConfigInTx tenantId updated conn tx
      tx.Commit()

    member s.DeleteSilence (tenantId : TenantId, id : string) =
      use conn = openConn ()
      use tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)
      let c = getConfigInTx tenantId conn tx
      let next = c.silences |> Array.filter (fun x -> x.id <> id)
      if next.Length = c.silences.Length then
        tx.Rollback()
        false
      else
        saveConfigInTx tenantId { c with silences = next } conn tx
        tx.Commit()
        true
