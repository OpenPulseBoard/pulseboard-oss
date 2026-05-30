module PulseBoard.PgAgentStore

// Postgres-backed IAgentStore so enrolled agents and pending enrollment
// tokens survive restarts. Without this the in-memory store is wiped on
// boot and every previously-enrolled agent fails authentication with
// `{"error":"forbidden"}` on its next /api/agent/v1/checkin call.
//
// Schema is owned in-module and applied idempotently via `ensureSchema`
// alongside the other Pg* stores at startup.

open System
open System.Security.Cryptography
open System.Text
open Npgsql
open Suave
open PulseBoard.AgentApi

let private schema = """
CREATE TABLE IF NOT EXISTS pb_agents (
  id              TEXT   PRIMARY KEY,
  tenant_id       TEXT   NOT NULL,
  hostname        TEXT   NOT NULL,
  version         TEXT   NOT NULL DEFAULT '',
  config_hash     TEXT   NOT NULL DEFAULT '',
  last_seen_ms    BIGINT NOT NULL,
  enrolled_at_ms  BIGINT NOT NULL,
  api_key_hash    TEXT   NOT NULL
);
CREATE INDEX IF NOT EXISTS pb_agents_tenant_idx ON pb_agents (tenant_id);

CREATE TABLE IF NOT EXISTS pb_agent_enroll_tokens (
  token         TEXT   PRIMARY KEY,
  tenant_id     TEXT   NOT NULL,
  expires_at_ms BIGINT NOT NULL
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private generateId () = Guid.NewGuid().ToString("N")

let private generateKey () =
  let bytes = RandomNumberGenerator.GetBytes 32
  "ak_" + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')

let private generateToken () =
  let bytes = RandomNumberGenerator.GetBytes 20
  "et_" + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')

let private hashKey (apiKey : string) =
  SHA256.HashData(Encoding.UTF8.GetBytes apiKey) |> Convert.ToHexString

let private constantTimeEquals (a : string) (b : string) =
  let ab = Encoding.UTF8.GetBytes a
  let bb = Encoding.UTF8.GetBytes b
  if ab.Length = bb.Length then
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan ab, ReadOnlySpan bb)
  else
    false

type PgAgentStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let readRecord (r : System.Data.Common.DbDataReader) : AgentRecord =
    { id         = r.GetString 0
      tenantId   = r.GetString 1
      hostname   = r.GetString 2
      version    = r.GetString 3
      configHash = r.GetString 4
      lastSeen   = r.GetInt64  5
      enrolledAt = r.GetInt64  6 }

  interface IAgentStore with

    member _.Enroll(tenantId, hostname, version) =
      let id      = generateId()
      let apiKey  = generateKey()
      let keyHash = hashKey apiKey
      let now     = nowMs()
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_agents \
           (id, tenant_id, hostname, version, config_hash, last_seen_ms, enrolled_at_ms, api_key_hash) \
           VALUES (@id, @tid, @host, @ver, '', @ls, @en, @kh)",
          conn)
      cmd.Parameters.AddWithValue("id",   id)       |> ignore
      cmd.Parameters.AddWithValue("tid",  tenantId) |> ignore
      cmd.Parameters.AddWithValue("host", hostname) |> ignore
      cmd.Parameters.AddWithValue("ver",  version)  |> ignore
      cmd.Parameters.AddWithValue("ls",   now)      |> ignore
      cmd.Parameters.AddWithValue("en",   now)      |> ignore
      cmd.Parameters.AddWithValue("kh",   keyHash)  |> ignore
      cmd.ExecuteNonQuery() |> ignore
      // Match InMemoryAgentStore: temporarily stash the raw key in
      // `configHash` so the caller can extract it before discarding.
      { id         = id
        tenantId   = tenantId
        hostname   = hostname
        version    = version
        configHash = apiKey
        lastSeen   = now
        enrolledAt = now }

    member _.Checkin(agentId, version, configHash, _metaJson) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_agents \
             SET version = @ver, config_hash = @ch, last_seen_ms = @ls \
           WHERE id = @id",
          conn)
      cmd.Parameters.AddWithValue("ver", version)    |> ignore
      cmd.Parameters.AddWithValue("ch",  configHash) |> ignore
      cmd.Parameters.AddWithValue("ls",  nowMs())    |> ignore
      cmd.Parameters.AddWithValue("id",  agentId)    |> ignore
      cmd.ExecuteNonQuery() > 0

    member _.List(tenantId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT id, tenant_id, hostname, version, config_hash, last_seen_ms, enrolled_at_ms \
             FROM pb_agents WHERE tenant_id = @tid ORDER BY enrolled_at_ms DESC",
          conn)
      cmd.Parameters.AddWithValue("tid", tenantId) |> ignore
      use r = cmd.ExecuteReader()
      let acc = ResizeArray<AgentRecord>()
      while r.Read() do acc.Add(readRecord r)
      List.ofSeq acc

    member _.TryGet(agentId) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT id, tenant_id, hostname, version, config_hash, last_seen_ms, enrolled_at_ms \
             FROM pb_agents WHERE id = @id",
          conn)
      cmd.Parameters.AddWithValue("id", agentId) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readRecord r) else None

    member _.ValidateKey(agentId, apiKey) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT api_key_hash FROM pb_agents WHERE id = @id",
          conn)
      cmd.Parameters.AddWithValue("id", agentId) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then
        let stored = r.GetString 0
        constantTimeEquals stored (hashKey apiKey)
      else
        false

    member _.GenerateToken(tenantId) =
      let token = generateToken()
      let exp   = nowMs() + 30L * 60L * 1000L
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_agent_enroll_tokens (token, tenant_id, expires_at_ms) \
           VALUES (@tok, @tid, @exp)",
          conn)
      cmd.Parameters.AddWithValue("tok", token)    |> ignore
      cmd.Parameters.AddWithValue("tid", tenantId) |> ignore
      cmd.Parameters.AddWithValue("exp", exp)      |> ignore
      cmd.ExecuteNonQuery() |> ignore
      { token = token; tenantId = tenantId; expiresAt = exp }

    member _.RedeemToken(token) =
      use conn = openConn ()
      // Atomically delete the token and return its row if still valid.
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_agent_enroll_tokens \
            WHERE token = @tok \
           RETURNING tenant_id, expires_at_ms",
          conn)
      cmd.Parameters.AddWithValue("tok", token) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then
        let tid = r.GetString 0
        let exp = r.GetInt64  1
        if nowMs() <= exp then Some tid else None
      else
        None

// ---------------------------------------------------------------------------
// Ingest middleware: accept agent bearer credentials
// ---------------------------------------------------------------------------

/// Middleware: if no tenant context has been attached yet AND the request
/// carries `Authorization: Bearer <agentId>:<apiKey>` matching an enrolled
/// agent, synthesise a TenantCtx with the Ingest scope and attach it. Lets
/// the agent's existing enrollment credentials authenticate against
/// `/v1/metrics`, `/v1/logs`, `/v1/traces`, `/loki/api/v1/push` without a
/// separate tenant-scoped API key.
let private extractBearer (req : HttpRequest) : string option =
  let header (name : string) =
    req.headers
    |> Seq.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
    |> Option.map (snd >> fun v -> v.Trim())
  match header "authorization" with
  | Some v when v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
    let token = v.Substring(7).Trim()
    if token.Length > 0 then Some token else None
  | _ -> None

let resolveAgentBearer (tenantStore : PulseBoard.Tenancy.ITenantStore)
                       (agentStore : IAgentStore)
                       (inner : WebPart) : WebPart =
  fun ctx -> async {
    match PulseBoard.Rbac.tryGetTenant ctx with
    | Some _ -> return! inner ctx
    | None ->
      match extractBearer ctx.request with
      | None -> return! inner ctx
      | Some presented ->
        let i = presented.IndexOf ':'
        if i <= 0 || i = presented.Length - 1 then
          return! inner ctx
        else
          let agentId = presented.Substring(0, i)
          let apiKey  = presented.Substring(i + 1)
          if not (agentStore.ValidateKey(agentId, apiKey)) then
            return! inner ctx
          else
            match agentStore.TryGet agentId with
            | None -> return! inner ctx
            | Some rec' ->
              match tenantStore.TryGetTenant (PulseBoard.Tenancy.TenantId rec'.tenantId) with
              | None -> return! inner ctx
              | Some tenant ->
                let tctx : PulseBoard.Tenancy.TenantCtx =
                  { tenant   = tenant
                    apiKeyId = PulseBoard.Tenancy.ApiKeyId ("agent:" + agentId)
                    role     = PulseBoard.Tenancy.Editor
                    scopes   = PulseBoard.Tenancy.Scope.Ingest }
                return! inner (PulseBoard.Rbac.attachTenant ctx tctx)
  }
