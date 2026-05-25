module PulseBoard.PgCustomerStore

open System
open Npgsql
open PulseBoard.CustomerAuth

// Phase 10 — Postgres backing for the customer-account identity tables.
//
// Lives next to `PgWorkspaceRegistry.fs` and uses the same conventions:
//   * schema is owned in-module, applied idempotently at startup,
//   * one connection per operation (Npgsql pools internally),
//   * `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`
//     for future migrations.
//
// Tables:
//   pb_customers             — one row per person/account
//   pb_customer_email_tokens — short-lived verify/reset link state
//   pb_customer_sessions     — refresh-token rows (revocable)
//   pb_customer_stripe_links — customer ↔ Stripe customer id (Phase 10 #6)
//
// The Stripe table is created here so the migration is atomic with the
// rest of the customer surface; the WebParts that populate it ship in a
// later step.

let private schema = """
CREATE TABLE IF NOT EXISTS pb_customers (
  id                TEXT        PRIMARY KEY,
  email             TEXT        NOT NULL UNIQUE,
  email_verified_at TIMESTAMPTZ,
  password_hash     BYTEA,
  password_salt     BYTEA,
  password_algo     TEXT,
  github_user_id    BIGINT      UNIQUE,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS pb_customer_email_tokens (
  token_hash   BYTEA       PRIMARY KEY,
  customer_id  TEXT        NOT NULL REFERENCES pb_customers(id) ON DELETE CASCADE,
  purpose      TEXT        NOT NULL,                  -- 'verify' | 'reset'
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at   TIMESTAMPTZ NOT NULL,
  consumed_at  TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS pb_customer_email_tokens_cust
  ON pb_customer_email_tokens(customer_id);

CREATE TABLE IF NOT EXISTS pb_customer_sessions (
  id                  TEXT        PRIMARY KEY,
  customer_id         TEXT        NOT NULL REFERENCES pb_customers(id) ON DELETE CASCADE,
  refresh_token_hash  BYTEA       NOT NULL,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at          TIMESTAMPTZ NOT NULL,
  revoked_at          TIMESTAMPTZ,
  user_agent          TEXT,
  ip                  TEXT
);
CREATE INDEX IF NOT EXISTS pb_customer_sessions_cust
  ON pb_customer_sessions(customer_id);

CREATE TABLE IF NOT EXISTS pb_customer_stripe_links (
  customer_id            TEXT        PRIMARY KEY REFERENCES pb_customers(id) ON DELETE CASCADE,
  stripe_customer_id     TEXT        NOT NULL UNIQUE,
  default_payment_method TEXT,
  updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

// -- helpers ----------------------------------------------------------------

let private optStr (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None else Some (r.GetString i)

let private optBytes (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None else Some (r.GetFieldValue<byte[]> i)

let private optDate (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None
  else Some (DateTimeOffset(r.GetDateTime i, TimeSpan.Zero))

let private optInt64 (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None else Some (r.GetInt64 i)

let private readCustomer (r : System.Data.Common.DbDataReader) : Customer =
  { id              = CustomerId (r.GetString 0)
    email           = r.GetString 1
    emailVerifiedAt = optDate r 2
    passwordHash    = optBytes r 3
    passwordSalt    = optBytes r 4
    passwordAlgo    = optStr r 5
    githubUserId    = optInt64 r 6
    createdAt       = DateTimeOffset(r.GetDateTime 7, TimeSpan.Zero)
    updatedAt       = DateTimeOffset(r.GetDateTime 8, TimeSpan.Zero) }

let private readToken (r : System.Data.Common.DbDataReader) : EmailToken =
  let purpose =
    tryParseEmailTokenPurpose (r.GetString 2)
    |> Option.defaultValue Verify
  { tokenHash  = r.GetFieldValue<byte[]> 0
    customerId = CustomerId (r.GetString 1)
    purpose    = purpose
    createdAt  = DateTimeOffset(r.GetDateTime 3, TimeSpan.Zero)
    expiresAt  = DateTimeOffset(r.GetDateTime 4, TimeSpan.Zero)
    consumedAt = optDate r 5 }

let private readSession (r : System.Data.Common.DbDataReader) : CustomerSession =
  { id               = r.GetString 0
    customerId       = CustomerId (r.GetString 1)
    refreshTokenHash = r.GetFieldValue<byte[]> 2
    createdAt        = DateTimeOffset(r.GetDateTime 3, TimeSpan.Zero)
    expiresAt        = DateTimeOffset(r.GetDateTime 4, TimeSpan.Zero)
    revokedAt        = optDate r 5
    userAgent        = optStr r 6
    ip               = optStr r 7 }

let private optParam (cmd : NpgsqlCommand) (name : string)
                     (dbType : NpgsqlTypes.NpgsqlDbType) (value : obj option) =
  let p = cmd.Parameters.Add(name, dbType)
  match value with
  | Some v -> p.Value <- v
  | None   -> p.Value <- DBNull.Value

// -- store ------------------------------------------------------------------

type PgCustomerStore (connectionString : string) =

  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c

  let custCols =
    "id, email, email_verified_at, password_hash, password_salt, \
     password_algo, github_user_id, created_at, updated_at"

  let tokenCols =
    "token_hash, customer_id, purpose, created_at, expires_at, consumed_at"

  let sessionCols =
    "id, customer_id, refresh_token_hash, created_at, expires_at, \
     revoked_at, user_agent, ip"

  let trySelectCustomer (where : string) (bind : NpgsqlCommand -> unit)
      : Customer option =
    use conn = openConn ()
    use cmd =
      new NpgsqlCommand(sprintf "SELECT %s FROM pb_customers WHERE %s" custCols where, conn)
    bind cmd
    use r = cmd.ExecuteReader()
    if r.Read() then Some (readCustomer r) else None

  interface ICustomerStore with

    member _.Insert c =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "INSERT INTO pb_customers (%s)
             VALUES (@id, @email, @ev, @ph, @ps, @pa, @gh, @ca, @ua)
             ON CONFLICT (id) DO UPDATE SET
               email             = EXCLUDED.email,
               email_verified_at = EXCLUDED.email_verified_at,
               password_hash     = EXCLUDED.password_hash,
               password_salt     = EXCLUDED.password_salt,
               password_algo     = EXCLUDED.password_algo,
               github_user_id    = EXCLUDED.github_user_id,
               updated_at        = EXCLUDED.updated_at"
            custCols,
          conn)
      let (CustomerId cid) = c.id
      cmd.Parameters.AddWithValue("@id", cid)            |> ignore
      cmd.Parameters.AddWithValue("@email", c.email)     |> ignore
      optParam cmd "@ev" NpgsqlTypes.NpgsqlDbType.TimestampTz
        (c.emailVerifiedAt |> Option.map (fun d -> box d.UtcDateTime))
      optParam cmd "@ph" NpgsqlTypes.NpgsqlDbType.Bytea
        (c.passwordHash |> Option.map box)
      optParam cmd "@ps" NpgsqlTypes.NpgsqlDbType.Bytea
        (c.passwordSalt |> Option.map box)
      optParam cmd "@pa" NpgsqlTypes.NpgsqlDbType.Text
        (c.passwordAlgo |> Option.map box)
      optParam cmd "@gh" NpgsqlTypes.NpgsqlDbType.Bigint
        (c.githubUserId |> Option.map box)
      cmd.Parameters.AddWithValue("@ca", c.createdAt.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("@ua", c.updatedAt.UtcDateTime) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member this.Update id f =
      // SELECT … FOR UPDATE inside a transaction so concurrent updaters
      // serialize on the same row. Matches PgWorkspaceRegistry.Update.
      use conn = openConn ()
      use tx = conn.BeginTransaction()
      let (CustomerId cid) = id
      let cur =
        use cmd =
          new NpgsqlCommand(
            sprintf "SELECT %s FROM pb_customers WHERE id = @id FOR UPDATE" custCols,
            conn, tx)
        cmd.Parameters.AddWithValue("@id", cid) |> ignore
        use r = cmd.ExecuteReader()
        if r.Read() then Some (readCustomer r) else None
      match cur with
      | None -> None
      | Some current ->
        let next = f current
        use up =
          new NpgsqlCommand(
            "UPDATE pb_customers SET
               email             = @email,
               email_verified_at = @ev,
               password_hash     = @ph,
               password_salt     = @ps,
               password_algo     = @pa,
               github_user_id    = @gh,
               updated_at        = @ua
             WHERE id = @id",
            conn, tx)
        up.Parameters.AddWithValue("@id", cid)                |> ignore
        up.Parameters.AddWithValue("@email", next.email)      |> ignore
        optParam up "@ev" NpgsqlTypes.NpgsqlDbType.TimestampTz
          (next.emailVerifiedAt |> Option.map (fun d -> box d.UtcDateTime))
        optParam up "@ph" NpgsqlTypes.NpgsqlDbType.Bytea
          (next.passwordHash |> Option.map box)
        optParam up "@ps" NpgsqlTypes.NpgsqlDbType.Bytea
          (next.passwordSalt |> Option.map box)
        optParam up "@pa" NpgsqlTypes.NpgsqlDbType.Text
          (next.passwordAlgo |> Option.map box)
        optParam up "@gh" NpgsqlTypes.NpgsqlDbType.Bigint
          (next.githubUserId |> Option.map box)
        up.Parameters.AddWithValue("@ua", next.updatedAt.UtcDateTime) |> ignore
        up.ExecuteNonQuery() |> ignore
        tx.Commit()
        Some next

    member _.TryGetById (CustomerId cid) =
      trySelectCustomer "id = @id" (fun c ->
        c.Parameters.AddWithValue("@id", cid) |> ignore)

    member _.TryGetByEmail raw =
      let canon = canonEmail raw
      trySelectCustomer "email = @email" (fun c ->
        c.Parameters.AddWithValue("@email", canon) |> ignore)

    member _.TryGetByGithub g =
      trySelectCustomer "github_user_id = @gh" (fun c ->
        c.Parameters.AddWithValue("@gh", g) |> ignore)

    member _.List () =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_customers ORDER BY created_at DESC" custCols,
          conn)
      use r = cmd.ExecuteReader()
      let out = ResizeArray<Customer>()
      while r.Read() do out.Add(readCustomer r)
      List.ofSeq out

    member _.InsertEmailToken t =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "INSERT INTO pb_customer_email_tokens (%s)
             VALUES (@h, @cid, @p, @ca, @ea, @co)
             ON CONFLICT (token_hash) DO NOTHING"
            tokenCols,
          conn)
      let (CustomerId cid) = t.customerId
      cmd.Parameters.AddWithValue("@h", t.tokenHash)       |> ignore
      cmd.Parameters.AddWithValue("@cid", cid)             |> ignore
      cmd.Parameters.AddWithValue("@p", emailTokenPurposeText t.purpose) |> ignore
      cmd.Parameters.AddWithValue("@ca", t.createdAt.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("@ea", t.expiresAt.UtcDateTime) |> ignore
      optParam cmd "@co" NpgsqlTypes.NpgsqlDbType.TimestampTz
        (t.consumedAt |> Option.map (fun d -> box d.UtcDateTime))
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGetEmailToken digest =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_customer_email_tokens WHERE token_hash = @h" tokenCols,
          conn)
      cmd.Parameters.AddWithValue("@h", digest) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readToken r) else None

    member _.ConsumeEmailToken (digest, at) =
      // Atomic: only flip consumed_at if it's still NULL and the token
      // hasn't expired. Affected-rows tells us whether it counted.
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_customer_email_tokens
             SET consumed_at = @at
           WHERE token_hash  = @h
             AND consumed_at IS NULL
             AND expires_at  > @at",
          conn)
      cmd.Parameters.AddWithValue("@h", digest)            |> ignore
      cmd.Parameters.AddWithValue("@at", at.UtcDateTime)   |> ignore
      cmd.ExecuteNonQuery() = 1

    member _.InsertSession s =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "INSERT INTO pb_customer_sessions (%s)
             VALUES (@id, @cid, @rh, @ca, @ea, @rv, @ua, @ip)"
            sessionCols,
          conn)
      let (CustomerId cid) = s.customerId
      cmd.Parameters.AddWithValue("@id", s.id)                |> ignore
      cmd.Parameters.AddWithValue("@cid", cid)                |> ignore
      cmd.Parameters.AddWithValue("@rh", s.refreshTokenHash)  |> ignore
      cmd.Parameters.AddWithValue("@ca", s.createdAt.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("@ea", s.expiresAt.UtcDateTime) |> ignore
      optParam cmd "@rv" NpgsqlTypes.NpgsqlDbType.TimestampTz
        (s.revokedAt |> Option.map (fun d -> box d.UtcDateTime))
      optParam cmd "@ua" NpgsqlTypes.NpgsqlDbType.Text
        (s.userAgent |> Option.map box)
      optParam cmd "@ip" NpgsqlTypes.NpgsqlDbType.Text
        (s.ip |> Option.map box)
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGetSession id =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_customer_sessions WHERE id = @id" sessionCols,
          conn)
      cmd.Parameters.AddWithValue("@id", id) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readSession r) else None

    member _.RevokeSession (id, at) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_customer_sessions
             SET revoked_at = @at
           WHERE id = @id AND revoked_at IS NULL",
          conn)
      cmd.Parameters.AddWithValue("@id", id)             |> ignore
      cmd.Parameters.AddWithValue("@at", at.UtcDateTime) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.ListSessions (CustomerId cid) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_customer_sessions
              WHERE customer_id = @cid
              ORDER BY created_at DESC"
            sessionCols,
          conn)
      cmd.Parameters.AddWithValue("@cid", cid) |> ignore
      use r = cmd.ExecuteReader()
      let out = ResizeArray<CustomerSession>()
      while r.Read() do out.Add(readSession r)
      List.ofSeq out
