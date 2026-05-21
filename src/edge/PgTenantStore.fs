module PulseBoard.PgTenantStore

open System
open System.Data
open System.Data.Common
open Npgsql
open NpgsqlTypes
open PulseBoard.Tenancy

// Postgres-backed `ITenantStore` (PLAN.md Phase 1 step 1). Schema is owned
// by this module: `EnsureSchema` runs idempotent `CREATE TABLE IF NOT EXISTS`
// at startup so a fresh database is usable without an out-of-band migration
// step. Tables are namespaced with the `pb_` prefix so cohabiting with other
// apps in a shared database is safe.
//
// The store is synchronous to match the existing `ITenantStore` contract;
// all I/O goes through short-lived `NpgsqlConnection`s and the built-in
// pool, so contention is bounded by the pool size, not the surface API.

let private schema = """
CREATE TABLE IF NOT EXISTS pb_tenants (
  id          TEXT        PRIMARY KEY,
  slug        TEXT        NOT NULL UNIQUE,
  created_at  TIMESTAMPTZ NOT NULL
);

-- Phase 7 #2: commercial plan. Idempotent column-add for existing
-- databases predating Phase 7; defaults to 'free' for backfill.
ALTER TABLE pb_tenants
  ADD COLUMN IF NOT EXISTS plan TEXT NOT NULL DEFAULT 'free';

CREATE TABLE IF NOT EXISTS pb_api_keys (
  id              TEXT        PRIMARY KEY,
  tenant_id       TEXT        NOT NULL REFERENCES pb_tenants(id) ON DELETE CASCADE,
  label           TEXT        NOT NULL,
  role            TEXT        NOT NULL,
  scopes          INTEGER     NOT NULL,
  hash_algorithm  TEXT        NOT NULL,
  iterations      INTEGER     NOT NULL,
  salt            BYTEA       NOT NULL,
  hash            BYTEA       NOT NULL,
  created_at      TIMESTAMPTZ NOT NULL,
  last_used_at    TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS pb_api_keys_tenant_idx ON pb_api_keys(tenant_id);

CREATE TABLE IF NOT EXISTS pb_users (
  id             TEXT        PRIMARY KEY,
  tenant_id      TEXT        NOT NULL REFERENCES pb_tenants(id) ON DELETE CASCADE,
  issuer         TEXT        NOT NULL,
  subject        TEXT        NOT NULL,
  email          TEXT        NULL,
  role           TEXT        NOT NULL,
  created_at     TIMESTAMPTZ NOT NULL,
  last_login_at  TIMESTAMPTZ NULL,
  UNIQUE (issuer, subject)
);
CREATE INDEX IF NOT EXISTS pb_users_tenant_idx ON pb_users(tenant_id);
"""

// -- Role <-> text -----------------------------------------------------------
// Stored as lowercase text so a human inspecting the table sees something
// readable and so future role additions are append-only.
let private roleToText = function
  | Viewer  -> "viewer"
  | Editor  -> "editor"
  | Admin   -> "admin"
  | Billing -> "billing"

let private textToRole (s : string) =
  match (if isNull s then "" else s.ToLowerInvariant()) with
  | "viewer"  -> Viewer
  | "editor"  -> Editor
  | "admin"   -> Admin
  | "billing" -> Billing
  | other     -> failwithf "PgTenantStore: unknown role '%s'" other

// -- Reader helpers (positional; query strings own the column order) --------

let private readTenant (r : DbDataReader) : Tenant =
  { id        = TenantId (r.GetString 0)
    slug      = r.GetString 1
    createdAt = DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero)
    plan      =
      match tryParsePlan (r.GetString 3) with
      | Some p -> p
      | None   -> Free }

let private readApiKey (r : DbDataReader) : ApiKeyRecord =
  { id            = ApiKeyId (r.GetString 0)
    tenantId      = TenantId (r.GetString 1)
    label         = r.GetString 2
    role          = textToRole (r.GetString 3)
    scopes        = enum<Scope> (r.GetInt32 4)
    hashAlgorithm = r.GetString 5
    iterations    = r.GetInt32 6
    salt          = r.GetFieldValue<byte[]> 7
    hash          = r.GetFieldValue<byte[]> 8
    createdAt     = DateTimeOffset(r.GetDateTime 9, TimeSpan.Zero)
    lastUsedAt    =
      ref (if r.IsDBNull 10 then None
           else Some (DateTimeOffset(r.GetDateTime 10, TimeSpan.Zero))) }

let private readUser (r : DbDataReader) : UserRecord =
  { id          = UserId (r.GetString 0)
    tenantId    = TenantId (r.GetString 1)
    issuer      = r.GetString 2
    subject     = r.GetString 3
    email       = if r.IsDBNull 4 then None else Some (r.GetString 4)
    role        = textToRole (r.GetString 5)
    createdAt   = DateTimeOffset(r.GetDateTime 6, TimeSpan.Zero)
    lastLoginAt =
      ref (if r.IsDBNull 7 then None
           else Some (DateTimeOffset(r.GetDateTime 7, TimeSpan.Zero))) }

let private apiKeyCols =
  "id, tenant_id, label, role, scopes, hash_algorithm, iterations, \
   salt, hash, created_at, last_used_at"

let private userCols =
  "id, tenant_id, issuer, subject, email, role, created_at, last_login_at"

// Mirrors the id-shape that Tenancy.fs uses for its in-memory store. The
// generator there is private; reproduce its policy here (9 random bytes →
// base64url) so identifiers look identical regardless of backend.
let private rng = System.Security.Cryptography.RandomNumberGenerator.Create()
let private genBytes (n : int) =
  let b = Array.zeroCreate n in rng.GetBytes b; b
let private toBase64Url (b : byte[]) =
  Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('=')
let private newId9 () = toBase64Url (genBytes 9)

let private defaultIterations = 100_000
let private pbkdf2 (secret : string) (salt : byte[]) (iterations : int) =
  System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
    password      = secret,
    salt          = ReadOnlySpan salt,
    iterations    = iterations,
    hashAlgorithm = System.Security.Cryptography.HashAlgorithmName.SHA256,
    outputLength  = 32)

/// Apply the schema. Safe to call repeatedly.
let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

type PgTenantStore(connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let exec (sql : string) (bind : NpgsqlCommand -> unit) =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    cmd.ExecuteNonQuery() |> ignore

  let queryOne (sql : string) (bind : NpgsqlCommand -> unit)
               (read : DbDataReader -> 'a) : 'a option =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    use r = cmd.ExecuteReader(CommandBehavior.SingleRow)
    if r.Read() then Some (read r) else None

  let queryAll (sql : string) (bind : NpgsqlCommand -> unit)
               (read : DbDataReader -> 'a) : 'a[] =
    use conn = openConn ()
    use cmd = new NpgsqlCommand(sql, conn)
    bind cmd
    use r = cmd.ExecuteReader()
    let acc = ResizeArray<'a>()
    while r.Read() do acc.Add (read r)
    acc.ToArray()

  let normaliseSlug (slug : string) =
    if isNull slug then "" else slug.Trim().ToLowerInvariant()

  interface ITenantStore with

    member _.CreateTenant slug =
      let slug = normaliseSlug slug
      if slug.Length = 0 then invalidArg "slug" "empty"
      // Idempotent insert; on conflict return the existing row. RETURNING
      // is suppressed on the conflict path, so do a follow-up SELECT to
      // recover the row uniformly.
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_tenants (id, slug, created_at) \
           VALUES (@id, @slug, @ts) ON CONFLICT (slug) DO NOTHING",
          conn)
      let id = newId9 ()
      cmd.Parameters.AddWithValue("id",   id)            |> ignore
      cmd.Parameters.AddWithValue("slug", slug)          |> ignore
      cmd.Parameters.AddWithValue("ts",   DateTime.UtcNow) |> ignore
      cmd.ExecuteNonQuery() |> ignore
      use sel =
        new NpgsqlCommand(
          "SELECT id, slug, created_at, plan FROM pb_tenants WHERE slug = @slug",
          conn)
      sel.Parameters.AddWithValue("slug", slug) |> ignore
      use r = sel.ExecuteReader(CommandBehavior.SingleRow)
      if r.Read() then readTenant r
      else failwith "PgTenantStore: tenant insert/select inconsistency"

    member _.TryGetTenant (TenantId id) =
      queryOne
        "SELECT id, slug, created_at, plan FROM pb_tenants WHERE id = @id"
        (fun c -> c.Parameters.AddWithValue("id", id) |> ignore)
        readTenant

    member _.TryGetTenantBySlug slug =
      let slug = normaliseSlug slug
      if slug.Length = 0 then None
      else
        queryOne
          "SELECT id, slug, created_at, plan FROM pb_tenants WHERE slug = @slug"
          (fun c -> c.Parameters.AddWithValue("slug", slug) |> ignore)
          readTenant

    member _.Tenants () =
      queryAll
        "SELECT id, slug, created_at, plan FROM pb_tenants ORDER BY created_at"
        ignore
        readTenant

    member this.UpdateTenantPlan (TenantId id, plan) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_tenants SET plan = @plan WHERE id = @id",
          conn)
      cmd.Parameters.AddWithValue("plan", planToText plan) |> ignore
      cmd.Parameters.AddWithValue("id",   id)              |> ignore
      let n = cmd.ExecuteNonQuery()
      if n = 0 then None
      else (this :> ITenantStore).TryGetTenant (TenantId id)

    member _.IssueApiKey (TenantId tenantId, label, role, scopes) =
      // Generate id + secret + salt + hash in-process, then a single INSERT.
      // FK to pb_tenants enforces tenant existence; we surface a friendly
      // error to match the in-memory store's invalidArg behaviour.
      let id  = newId9 ()
      let secret = toBase64Url (genBytes 32)
      let salt   = genBytes 16
      let halg   =
        argon2idTag argon2DefaultTime argon2DefaultMemKb argon2DefaultPara
      let iters  = argon2DefaultTime
      let hash   =
        argon2idHash secret salt argon2DefaultTime argon2DefaultMemKb argon2DefaultPara
      let createdAt = DateTime.UtcNow
      try
        exec
          "INSERT INTO pb_api_keys \
           (id, tenant_id, label, role, scopes, hash_algorithm, iterations, \
            salt, hash, created_at, last_used_at) \
           VALUES (@id, @tid, @label, @role, @scopes, @halg, @iters, \
                   @salt, @hash, @ts, NULL)"
          (fun c ->
            c.Parameters.AddWithValue("id",     id)                      |> ignore
            c.Parameters.AddWithValue("tid",    tenantId)                |> ignore
            c.Parameters.AddWithValue("label",  label)                   |> ignore
            c.Parameters.AddWithValue("role",   roleToText role)         |> ignore
            c.Parameters.AddWithValue("scopes", int scopes)              |> ignore
            c.Parameters.AddWithValue("halg",   halg)                    |> ignore
            c.Parameters.AddWithValue("iters",  iters)                   |> ignore
            c.Parameters.Add(NpgsqlParameter("salt", NpgsqlDbType.Bytea, Value = salt)) |> ignore
            c.Parameters.Add(NpgsqlParameter("hash", NpgsqlDbType.Bytea, Value = hash)) |> ignore
            c.Parameters.AddWithValue("ts",     createdAt)               |> ignore)
      with
      | :? PostgresException as ex when ex.SqlState = "23503" ->
        invalidArg "tenantId" "tenant not found"
      let record =
        { id            = ApiKeyId id
          tenantId      = TenantId tenantId
          label         = label
          role          = role
          scopes        = scopes
          hashAlgorithm = halg
          iterations    = iters
          salt          = salt
          hash          = hash
          createdAt     = DateTimeOffset(createdAt, TimeSpan.Zero)
          lastUsedAt    = ref None }
      { record = record; plaintext = sprintf "pk_%s.%s" id secret }

    member _.TryGetApiKey (ApiKeyId id) =
      queryOne
        (sprintf "SELECT %s FROM pb_api_keys WHERE id = @id" apiKeyCols)
        (fun c -> c.Parameters.AddWithValue("id", id) |> ignore)
        readApiKey

    member _.ApiKeysFor (TenantId tenantId) =
      queryAll
        (sprintf
           "SELECT %s FROM pb_api_keys WHERE tenant_id = @tid ORDER BY created_at"
           apiKeyCols)
        (fun c -> c.Parameters.AddWithValue("tid", tenantId) |> ignore)
        readApiKey

    member _.MarkUsed (ApiKeyId id) =
      // Fire-and-forget update; missing rows are silently ignored to match
      // the in-memory contract.
      exec
        "UPDATE pb_api_keys SET last_used_at = @ts WHERE id = @id"
        (fun c ->
          c.Parameters.AddWithValue("ts", DateTime.UtcNow) |> ignore
          c.Parameters.AddWithValue("id", id)              |> ignore)

    member _.TryGetUser (issuer, subject) =
      queryOne
        (sprintf
           "SELECT %s FROM pb_users WHERE issuer = @iss AND subject = @sub"
           userCols)
        (fun c ->
          c.Parameters.AddWithValue("iss", issuer)  |> ignore
          c.Parameters.AddWithValue("sub", subject) |> ignore)
        readUser

    member _.TryGetUserById (UserId id) =
      queryOne
        (sprintf "SELECT %s FROM pb_users WHERE id = @id" userCols)
        (fun c -> c.Parameters.AddWithValue("id", id) |> ignore)
        readUser

    member this.UpsertUser (TenantId tenantId, issuer, subject, email, roleIfNew) =
      // On first login: INSERT with `roleIfNew`. On repeat: refresh email
      // and tenantId, bump `last_login_at`, leave `role` untouched. We use
      // a single ON CONFLICT statement so the round trip is one query.
      let now = DateTime.UtcNow
      let emailParam : obj =
        match email with Some e -> box e | None -> box DBNull.Value
      try
        exec
          (sprintf "INSERT INTO pb_users (%s) \
                    VALUES (@id, @tid, @iss, @sub, @email, @role, @ts, @ts) \
                    ON CONFLICT (issuer, subject) DO UPDATE \
                      SET email         = EXCLUDED.email, \
                          tenant_id     = EXCLUDED.tenant_id, \
                          last_login_at = EXCLUDED.last_login_at"
                   userCols)
          (fun c ->
            c.Parameters.AddWithValue("id",    newId9 ())          |> ignore
            c.Parameters.AddWithValue("tid",   tenantId)           |> ignore
            c.Parameters.AddWithValue("iss",   issuer)             |> ignore
            c.Parameters.AddWithValue("sub",   subject)            |> ignore
            c.Parameters.AddWithValue("email", emailParam)         |> ignore
            c.Parameters.AddWithValue("role",  roleToText roleIfNew) |> ignore
            c.Parameters.AddWithValue("ts",    now)                |> ignore)
      with
      | :? PostgresException as ex when ex.SqlState = "23503" ->
        invalidArg "tenantId" "tenant not found"
      match (this :> ITenantStore).TryGetUser(issuer, subject) with
      | Some u -> u
      | None   -> failwith "PgTenantStore: user upsert/select inconsistency"

    member _.UpdateUserRole (UserId id, role) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "UPDATE pb_users SET role = @role WHERE id = @id \
                   RETURNING %s" userCols,
          conn)
      cmd.Parameters.AddWithValue("role", roleToText role) |> ignore
      cmd.Parameters.AddWithValue("id",   id)              |> ignore
      use r = cmd.ExecuteReader(CommandBehavior.SingleRow)
      if r.Read() then Some (readUser r) else None

    member _.UsersFor (TenantId tenantId) =
      queryAll
        (sprintf
           "SELECT %s FROM pb_users WHERE tenant_id = @tid ORDER BY created_at"
           userCols)
        (fun c -> c.Parameters.AddWithValue("tid", tenantId) |> ignore)
        readUser
