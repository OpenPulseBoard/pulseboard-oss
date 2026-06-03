module PulseBoard.PgSecretsStore

// Postgres-backed ISecretsStore and IPiiPolicyStore.
//
// DEK envelopes: one row per tenant in pb_secrets_deks. The envelope is
// the same AES-GCM-wrapped JSON as the file-backed store; the KEK is
// still loaded from PULSE_MASTER_KEY (or the on-disk master.key) so the
// root secret never touches the database.
//
// PII policies: one row per tenant in pb_secrets_pii_policies, stored as
// a JSON array of field names.
//
// Both stores cache in memory to keep the encrypt/decrypt path fast.

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open PulseBoard.Secrets

let private schema = """
CREATE TABLE IF NOT EXISTS pb_secrets_deks (
  tenant_id TEXT PRIMARY KEY,
  envelope  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pb_secrets_pii_policies (
  tenant_id TEXT PRIMARY KEY,
  fields    TEXT NOT NULL
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

type PgSecretsStore(connectionString : string, kek : byte[]) =

  let cache = ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal)

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let getDek (tid : string) =
    cache.GetOrAdd(tid, fun _ ->
      use conn = openConn ()
      use selectCmd =
        new NpgsqlCommand(
          "SELECT envelope FROM pb_secrets_deks WHERE tenant_id = @tid",
          conn)
      selectCmd.Parameters.AddWithValue("tid", tid) |> ignore
      let existing = selectCmd.ExecuteScalar()
      if not (isNull existing) then
        deserialiseDekEnvelope kek (existing :?> string)
      else
        // Generate a new DEK, insert with ON CONFLICT DO NOTHING to handle
        // races, then read back the winning row.
        let dek      = RandomNumberGenerator.GetBytes 32
        let envelope = serialiseDekEnvelope kek dek
        use insCmd =
          new NpgsqlCommand(
            "INSERT INTO pb_secrets_deks (tenant_id, envelope) \
             VALUES (@tid, @env) ON CONFLICT (tenant_id) DO NOTHING",
            conn)
        insCmd.Parameters.AddWithValue("tid", tid)      |> ignore
        insCmd.Parameters.AddWithValue("env", envelope) |> ignore
        insCmd.ExecuteNonQuery() |> ignore
        use readCmd =
          new NpgsqlCommand(
            "SELECT envelope FROM pb_secrets_deks WHERE tenant_id = @tid",
            conn)
        readCmd.Parameters.AddWithValue("tid", tid) |> ignore
        let stored = readCmd.ExecuteScalar()
        if isNull stored then dek
        else deserialiseDekEnvelope kek (stored :?> string))

  interface ISecretsStore with
    member _.GetOrCreateDek tid = getDek tid
    member _.Encrypt (tid, plaintext) = encryptWithDek (getDek tid) plaintext
    member _.Decrypt (tid, token)     = decryptWithDek (getDek tid) token

type PgPiiPolicyStore(connectionString : string) =

  let cache = ConcurrentDictionary<string, string[]>(StringComparer.Ordinal)

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let parseFields (json : string) : string[] =
    try
      use doc = JsonDocument.Parse json
      [| for el in doc.RootElement.EnumerateArray() do
           if el.ValueKind = JsonValueKind.String then yield el.GetString() |]
    with _ -> [||]

  let serialiseFields (fields : string[]) : string =
    use ms = new System.IO.MemoryStream()
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for f in fields do w.WriteStringValue f
    w.WriteEndArray()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

  interface IPiiPolicyStore with

    member _.Get (tid : string) =
      cache.GetOrAdd(tid, fun _ ->
        use conn = openConn ()
        use cmd =
          new NpgsqlCommand(
            "SELECT fields FROM pb_secrets_pii_policies WHERE tenant_id = @tid",
            conn)
        cmd.Parameters.AddWithValue("tid", tid) |> ignore
        let result = cmd.ExecuteScalar()
        if isNull result then [||] else parseFields (result :?> string))

    member _.Put (tid : string, fields : string[]) =
      let cleaned =
        fields
        |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Array.map (fun s -> s.Trim())
        |> Array.distinct
      let json = serialiseFields cleaned
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_secrets_pii_policies (tenant_id, fields) VALUES (@tid, @fields) \
           ON CONFLICT (tenant_id) DO UPDATE SET fields = EXCLUDED.fields",
          conn)
      cmd.Parameters.AddWithValue("tid",    tid)  |> ignore
      cmd.Parameters.AddWithValue("fields", json) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      cache.[tid] <- cleaned
