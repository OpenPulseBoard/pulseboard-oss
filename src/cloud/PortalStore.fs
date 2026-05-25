module PulseBoard.PortalStore

open System
open System.Collections.Concurrent
open Npgsql
open PulseBoard.CustomerAuth

// Phase 10 step 4 — customer ⇄ workspace ownership + plan tracking.
//
// The provisioner already keeps a `pb_workspaces` table (slug → fly
// app, upstream, owner_email) for infra routing. This store is a
// *separate* customer-facing view of the same workspaces: it pins each
// slug to a specific PulseBoard customer (FK to `pb_customers.id`),
// records the plan the customer chose, and tracks the lifecycle bit
// the portal cares about (`creating` → `live` → `archived` / `failed`).
//
// Why two stores instead of one? The provisioner runs on infra hosts
// (admin/internal) and may exist before the customer-auth feature is
// turned on. The portal store can live entirely in the apex DB and
// references `pb_customers.id` directly, which keeps the FK simple.
// Operators who haven't enabled Phase 10 don't pay for an unused table.

// -- types ------------------------------------------------------------------

/// Public plan identifiers shown to the customer. The internal `Plans`
/// module in `src/edge` already defines `Free` / `Pro`; we keep the
/// portal vocabulary slightly different (adding `Starter`) so the
/// website can market three tiers without rewiring the in-binary
/// entitlement engine. `Starter` is treated as `Pro` for entitlements
/// — the difference is purely commercial (lower price, lower caps).
type PortalPlan =
  | Free
  | Starter
  | Pro

module PortalPlan =
  let toString = function
    | Free    -> "free"
    | Starter -> "starter"
    | Pro     -> "pro"

  let tryParse (s : string) : PortalPlan option =
    let s = if isNull s then "" else s.Trim().ToLowerInvariant()
    match s with
    | "free"    -> Some Free
    | "starter" -> Some Starter
    | "pro"     -> Some Pro
    | _         -> None

  /// Internal entitlement-tier mapping. Used by the portal -> provisioner
  /// bridge to inject the right `PULSE_PLAN` env into the workspace.
  let toInternal = function
    | Free            -> "free"
    | Starter | Pro   -> "pro"

/// Lifecycle of a portal-managed workspace row.
type WorkspaceStatus =
  /// Provisioner accepted the request; the Fly machine is being
  /// spun up but hasn't reported healthy yet. Sub-minute window.
  | Creating
  /// Provisioner returned 201; the customer can reach the workspace.
  | Live
  /// Customer (or admin) archived the workspace. Traffic is refused
  /// at the router; data is retained (re-activatable from /portal).
  | Archived
  /// Provisioner POST returned an error before we recorded a
  /// `Live` state. The error message lives in `error`.
  | Failed

module WorkspaceStatus =
  let toString = function
    | Creating -> "creating"
    | Live     -> "live"
    | Archived -> "archived"
    | Failed   -> "failed"
  let tryParse = function
    | "creating" -> Some Creating
    | "live"     -> Some Live
    | "archived" -> Some Archived
    | "failed"   -> Some Failed
    | _          -> None

[<NoComparison>]
type PortalWorkspace =
  { slug         : string
    customerId   : CustomerId
    plan         : PortalPlan
    status       : WorkspaceStatus
    /// `https://<slug>.<root>` once provisioned. Surfaced to the
    /// portal as the "Open workspace" link.
    publicUrl    : string option
    /// `http://<app>.flycast:80` — what the router proxies to.
    /// Kept here mainly for diagnostics in /api/portal/workspaces.
    upstreamUrl  : string option
    createdAt    : DateTimeOffset
    updatedAt    : DateTimeOffset
    archivedAt   : DateTimeOffset option
    /// Phase 10 step 7 — last time the workspace edge reported
    /// activity (via `POST /api/portal/internal/heartbeat`). The
    /// idle-sleep sweeper archives free workspaces whose
    /// `lastActiveAt` is older than the configured threshold. We
    /// default to `createdAt` so a brand-new workspace gets the
    /// full grace period before its first heartbeat.
    lastActiveAt : DateTimeOffset
    /// Phase 10 step 10 — set to `now` the first time the
    /// reconcile webhook sees a non-entitled Stripe subscription on
    /// a paid workspace. Cleared when the subscription returns to
    /// entitled. The `PurgeCron` archives the workspace once
    /// `overdueSince` is older than the configured grace period.
    overdueSince : DateTimeOffset option
    /// Free-text error string from a failed provision. UI surfaces
    /// the first 200 chars; full text is in the row for support
    /// debugging.
    error        : string option }

// -- interface --------------------------------------------------------------

[<Interface>]
type ICustomerWorkspaceStore =
  /// First-insert path. Caller is responsible for slug uniqueness;
  /// implementations throw on collision so the API surface returns 409.
  abstract Insert : PortalWorkspace -> unit
  abstract TryGet : slug:string -> PortalWorkspace option
  /// Atomic read-modify-write. Returns the post-update row or `None`
  /// if the slug doesn't exist.
  abstract Update : slug:string -> (PortalWorkspace -> PortalWorkspace) -> PortalWorkspace option
  /// Every workspace owned by this customer (any status), newest first.
  abstract ListForCustomer : CustomerId -> PortalWorkspace list
  /// Count of workspaces this customer has on a given plan that aren't
  /// archived. Used to enforce the free-tier "one workspace per
  /// customer" rule.
  abstract CountActiveOnPlan : CustomerId -> PortalPlan -> int
  /// Bump `lastActiveAt` to `now` for an existing workspace. No-op
  /// if the slug isn't known. Used by the workspace edge's
  /// heartbeat path — must be cheap (called potentially per-ingest).
  abstract TouchActivity : slug:string * now:DateTimeOffset -> unit
  /// Live free workspaces whose `lastActiveAt` is at or before
  /// `threshold`. Used by `FreeTierSleeper` to pick sleep candidates.
  abstract ListIdleFreeWorkspaces : threshold:DateTimeOffset -> PortalWorkspace list
  /// Workspaces that have been archived since at or before
  /// `threshold`. Used by `PurgeCron` to pick purge candidates.
  abstract ListPurgeCandidates : threshold:DateTimeOffset -> PortalWorkspace list
  /// Workspaces flagged overdue since at or before `threshold`
  /// AND still live. Used by `PurgeCron` to pick archive
  /// candidates after the payment grace period elapses.
  abstract ListOverdueCandidates : threshold:DateTimeOffset -> PortalWorkspace list
  /// Hard-delete a workspace row. Called by `PurgeCron` after the
  /// provisioner has destroyed the Fly app and dropped the schema.
  abstract Delete : slug:string -> unit

// -- helpers ----------------------------------------------------------------

// -- in-memory impl ---------------------------------------------------------

type InMemoryCustomerWorkspaceStore () =
  let rows = ConcurrentDictionary<string, PortalWorkspace>()
  interface ICustomerWorkspaceStore with
    member _.Insert w =
      if not (rows.TryAdd(w.slug, w)) then
        invalidOp (sprintf "workspace slug already exists: %s" w.slug)
    member _.TryGet slug =
      match rows.TryGetValue slug with true, w -> Some w | _ -> None
    member _.Update slug f =
      match rows.TryGetValue slug with
      | false, _ -> None
      | true, cur ->
        let next = f cur
        if rows.TryUpdate(slug, next, cur) then Some next
        else
          // Lost the race; retry once.
          match rows.TryGetValue slug with
          | true, cur2 ->
            let n2 = f cur2
            if rows.TryUpdate(slug, n2, cur2) then Some n2 else None
          | _ -> None
    member _.ListForCustomer cid =
      rows.Values
      |> Seq.filter (fun w -> w.customerId = cid)
      |> Seq.sortByDescending (fun w -> w.createdAt)
      |> Seq.toList
    member _.CountActiveOnPlan cid plan =
      rows.Values
      |> Seq.filter (fun w ->
        w.customerId = cid
        && w.plan = plan
        && w.status <> Archived
        && w.status <> Failed)
      |> Seq.length
    member _.TouchActivity (slug, now) =
      match rows.TryGetValue slug with
      | false, _ -> ()
      | true, cur ->
        let next = { cur with lastActiveAt = now }
        rows.TryUpdate(slug, next, cur) |> ignore
    member _.ListIdleFreeWorkspaces threshold =
      rows.Values
      |> Seq.filter (fun w ->
        w.plan = Free
        && w.status = Live
        && w.lastActiveAt <= threshold)
      |> Seq.sortBy (fun w -> w.lastActiveAt)
      |> Seq.toList
    member _.ListPurgeCandidates threshold =
      rows.Values
      |> Seq.filter (fun w ->
        w.status = Archived
        && (match w.archivedAt with
            | Some a -> a <= threshold
            | None   -> false))
      |> Seq.sortBy (fun w -> w.archivedAt |> Option.defaultValue DateTimeOffset.MinValue)
      |> Seq.toList
    member _.ListOverdueCandidates threshold =
      rows.Values
      |> Seq.filter (fun w ->
        w.status = Live
        && (match w.overdueSince with
            | Some t -> t <= threshold
            | None   -> false))
      |> Seq.sortBy (fun w -> w.overdueSince |> Option.defaultValue DateTimeOffset.MinValue)
      |> Seq.toList
    member _.Delete slug =
      rows.TryRemove slug |> ignore

// -- Postgres impl ----------------------------------------------------------

let schema = """
CREATE TABLE IF NOT EXISTS pb_customer_workspaces (
  slug          TEXT        PRIMARY KEY,
  customer_id   TEXT        NOT NULL REFERENCES pb_customers(id) ON DELETE CASCADE,
  plan          TEXT        NOT NULL DEFAULT 'free',
  status        TEXT        NOT NULL DEFAULT 'creating',
  public_url    TEXT,
  upstream_url  TEXT,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  archived_at   TIMESTAMPTZ,
  error         TEXT
);
ALTER TABLE pb_customer_workspaces
  ADD COLUMN IF NOT EXISTS last_active_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE pb_customer_workspaces
  ADD COLUMN IF NOT EXISTS overdue_since TIMESTAMPTZ;
CREATE INDEX IF NOT EXISTS pb_customer_workspaces_by_customer
  ON pb_customer_workspaces (customer_id);
CREATE INDEX IF NOT EXISTS pb_customer_workspaces_idle_free
  ON pb_customer_workspaces (plan, status, last_active_at);
CREATE INDEX IF NOT EXISTS pb_customer_workspaces_purge
  ON pb_customer_workspaces (status, archived_at)
  WHERE status = 'archived';
CREATE INDEX IF NOT EXISTS pb_customer_workspaces_overdue
  ON pb_customer_workspaces (status, overdue_since)
  WHERE overdue_since IS NOT NULL;
"""

let ensureSchema (connectionString : string) =
  use conn = new NpgsqlConnection(connectionString)
  conn.Open()
  use cmd = new NpgsqlCommand(schema, conn)
  cmd.ExecuteNonQuery() |> ignore

let private optStr (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None else Some (r.GetString i)
let private optDate (r : System.Data.Common.DbDataReader) (i : int) =
  if r.IsDBNull i then None
  else Some (DateTimeOffset(r.GetDateTime i, TimeSpan.Zero))

let private readRow (r : System.Data.Common.DbDataReader) : PortalWorkspace =
  { slug        = r.GetString 0
    customerId  = CustomerId (r.GetString 1)
    plan        = PortalPlan.tryParse (r.GetString 2) |> Option.defaultValue Free
    status      = WorkspaceStatus.tryParse (r.GetString 3) |> Option.defaultValue Failed
    publicUrl   = optStr r 4
    upstreamUrl = optStr r 5
    createdAt   = DateTimeOffset(r.GetDateTime 6, TimeSpan.Zero)
    updatedAt   = DateTimeOffset(r.GetDateTime 7, TimeSpan.Zero)
    archivedAt  = optDate r 8
    error       = optStr r 9
    lastActiveAt = DateTimeOffset(r.GetDateTime 10, TimeSpan.Zero)
    overdueSince = optDate r 11 }

let private selectCols =
  "slug, customer_id, plan, status, public_url, upstream_url, \
   created_at, updated_at, archived_at, error, last_active_at, overdue_since"

let private optParam (cmd : NpgsqlCommand) (name : string)
                     (dbType : NpgsqlTypes.NpgsqlDbType) (value : obj option) =
  let p = cmd.Parameters.Add(name, dbType)
  p.Value <- (value |> Option.defaultValue (box DBNull.Value))

type PgCustomerWorkspaceStore (connectionString : string) =
  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c
  interface ICustomerWorkspaceStore with
    member _.Insert w =
      let (CustomerId cid) = w.customerId
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_customer_workspaces \
             (slug, customer_id, plan, status, public_url, upstream_url, \
              created_at, updated_at, archived_at, error, last_active_at, overdue_since) \
           VALUES (@slug, @cid, @plan, @status, @pub, @up, @c, @u, @a, @err, @la, @ov)",
          conn)
      cmd.Parameters.AddWithValue("slug",   w.slug) |> ignore
      cmd.Parameters.AddWithValue("cid",    cid) |> ignore
      cmd.Parameters.AddWithValue("plan",   PortalPlan.toString w.plan) |> ignore
      cmd.Parameters.AddWithValue("status", WorkspaceStatus.toString w.status) |> ignore
      optParam cmd "pub"   NpgsqlTypes.NpgsqlDbType.Text       (w.publicUrl   |> Option.map box)
      optParam cmd "up"    NpgsqlTypes.NpgsqlDbType.Text       (w.upstreamUrl |> Option.map box)
      cmd.Parameters.AddWithValue("c", w.createdAt.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("u", w.updatedAt.UtcDateTime) |> ignore
      optParam cmd "a"     NpgsqlTypes.NpgsqlDbType.TimestampTz
        (w.archivedAt |> Option.map (fun d -> box d.UtcDateTime))
      optParam cmd "err"   NpgsqlTypes.NpgsqlDbType.Text       (w.error |> Option.map box)
      cmd.Parameters.AddWithValue("la", w.lastActiveAt.UtcDateTime) |> ignore
      optParam cmd "ov"    NpgsqlTypes.NpgsqlDbType.TimestampTz
        (w.overdueSince |> Option.map (fun d -> box d.UtcDateTime))
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGet slug =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_customer_workspaces WHERE slug = @s" selectCols,
          conn)
      cmd.Parameters.AddWithValue("s", slug) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readRow r) else None

    member this.Update slug f =
      use conn = openConn ()
      use tx = conn.BeginTransaction()
      use sel =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_customer_workspaces WHERE slug = @s FOR UPDATE" selectCols,
          conn, tx)
      sel.Parameters.AddWithValue("s", slug) |> ignore
      let cur =
        use r = sel.ExecuteReader()
        if r.Read() then Some (readRow r) else None
      match cur with
      | None -> None
      | Some w ->
        let next = f w
        use upd =
          new NpgsqlCommand(
            "UPDATE pb_customer_workspaces SET \
               plan = @plan, status = @status, \
               public_url = @pub, upstream_url = @up, \
               updated_at = @u, archived_at = @a, error = @err, \
               last_active_at = @la, overdue_since = @ov \
             WHERE slug = @s",
            conn, tx)
        upd.Parameters.AddWithValue("s",      slug) |> ignore
        upd.Parameters.AddWithValue("plan",   PortalPlan.toString next.plan) |> ignore
        upd.Parameters.AddWithValue("status", WorkspaceStatus.toString next.status) |> ignore
        optParam upd "pub"  NpgsqlTypes.NpgsqlDbType.Text       (next.publicUrl   |> Option.map box)
        optParam upd "up"   NpgsqlTypes.NpgsqlDbType.Text       (next.upstreamUrl |> Option.map box)
        upd.Parameters.AddWithValue("u", next.updatedAt.UtcDateTime) |> ignore
        optParam upd "a"    NpgsqlTypes.NpgsqlDbType.TimestampTz
          (next.archivedAt |> Option.map (fun d -> box d.UtcDateTime))
        optParam upd "err"  NpgsqlTypes.NpgsqlDbType.Text       (next.error |> Option.map box)
        upd.Parameters.AddWithValue("la", next.lastActiveAt.UtcDateTime) |> ignore
        optParam upd "ov"   NpgsqlTypes.NpgsqlDbType.TimestampTz
          (next.overdueSince |> Option.map (fun d -> box d.UtcDateTime))
        upd.ExecuteNonQuery() |> ignore
        tx.Commit()
        Some next

    member _.ListForCustomer (CustomerId cid) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_customer_workspaces \
             WHERE customer_id = @cid ORDER BY created_at DESC"
            selectCols,
          conn)
      cmd.Parameters.AddWithValue("cid", cid) |> ignore
      use r = cmd.ExecuteReader()
      [ while r.Read() do yield readRow r ]

    member _.CountActiveOnPlan (CustomerId cid) plan =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT COUNT(*) FROM pb_customer_workspaces \
           WHERE customer_id = @cid AND plan = @plan \
                 AND status NOT IN ('archived','failed')",
          conn)
      cmd.Parameters.AddWithValue("cid",  cid) |> ignore
      cmd.Parameters.AddWithValue("plan", PortalPlan.toString plan) |> ignore
      let n = cmd.ExecuteScalar() :?> int64
      int n

    member _.TouchActivity (slug, now) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "UPDATE pb_customer_workspaces SET last_active_at = @t WHERE slug = @s",
          conn)
      cmd.Parameters.AddWithValue("s", slug) |> ignore
      cmd.Parameters.AddWithValue("t", now.UtcDateTime) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.ListIdleFreeWorkspaces threshold =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_customer_workspaces \
             WHERE plan = 'free' AND status = 'live' \
                   AND last_active_at <= @t \
             ORDER BY last_active_at ASC"
            selectCols,
          conn)
      cmd.Parameters.AddWithValue("t", threshold.UtcDateTime) |> ignore
      use r = cmd.ExecuteReader()
      [ while r.Read() do yield readRow r ]

    member _.ListPurgeCandidates threshold =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_customer_workspaces \
             WHERE status = 'archived' AND archived_at IS NOT NULL \
                   AND archived_at <= @t \
             ORDER BY archived_at ASC"
            selectCols,
          conn)
      cmd.Parameters.AddWithValue("t", threshold.UtcDateTime) |> ignore
      use r = cmd.ExecuteReader()
      [ while r.Read() do yield readRow r ]

    member _.ListOverdueCandidates threshold =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_customer_workspaces \
             WHERE status = 'live' AND overdue_since IS NOT NULL \
                   AND overdue_since <= @t \
             ORDER BY overdue_since ASC"
            selectCols,
          conn)
      cmd.Parameters.AddWithValue("t", threshold.UtcDateTime) |> ignore
      use r = cmd.ExecuteReader()
      [ while r.Read() do yield readRow r ]

    member _.Delete slug =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "DELETE FROM pb_customer_workspaces WHERE slug = @s",
          conn)
      cmd.Parameters.AddWithValue("s", slug) |> ignore
      cmd.ExecuteNonQuery() |> ignore
