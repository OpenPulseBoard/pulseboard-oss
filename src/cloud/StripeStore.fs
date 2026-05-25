module PulseBoard.StripeStore

open System
open System.Collections.Concurrent
open Npgsql
open PulseBoard.CustomerAuth

// Phase 10 step 5 — persistence for Stripe linkage and subscriptions.
//
// We deliberately keep two tables:
//   * `pb_customer_stripe_links` already exists (created in step 1)
//     and maps `pb_customers.id` → `cus_xxx`. The link is 1:1.
//   * `pb_stripe_subscriptions` is new here and tracks one row per
//     Stripe subscription. A subscription belongs to a customer and
//     (via metadata at checkout time) to a single workspace slug.
//
// Webhooks land at `/api/stripe/webhook`, which dispatches to
// `IStripeStore.UpsertSubscription` and (when the status crosses a
// threshold) updates the matching `pb_customer_workspaces` row's
// `plan` column via `ICustomerWorkspaceStore`.

[<NoComparison>]
type StripeSubscription =
  { id                : string
    itemId            : string
    stripeCustomerId  : string
    customerId        : CustomerId
    workspaceSlug     : string option
    priceId           : string
    plan              : PulseBoard.PortalStore.PortalPlan
    /// Stripe statuses: `incomplete`, `incomplete_expired`, `trialing`,
    /// `active`, `past_due`, `canceled`, `unpaid`, `paused`. We treat
    /// `active` and `trialing` as "entitled"; everything else falls
    /// back to free at the next webhook tick.
    status            : string
    currentPeriodEnd  : DateTimeOffset option
    cancelAtPeriodEnd : bool
    createdAt         : DateTimeOffset
    updatedAt         : DateTimeOffset }

module StripeSubscription =
  /// Is the customer currently entitled to the paid features of this
  /// subscription? Used by webhook -> workspace-plan reconciliation.
  let isEntitled (s : StripeSubscription) =
    match s.status with
    | "active" | "trialing" | "past_due" -> true
    | _ -> false

[<Interface>]
type IStripeStore =
  // -- customer ↔ stripe customer id ----------------------------------------
  abstract TryGetStripeCustomerId : CustomerId -> string option
  /// Idempotent: subsequent calls with the same `stripe_customer_id`
  /// are no-ops. Different cus_id for the same customer is an error
  /// (we never re-key — the first one wins).
  abstract SetStripeCustomerId    : CustomerId -> string -> unit
  abstract TryGetCustomerByStripeId : stripeCustomerId:string -> CustomerId option

  // -- subscriptions --------------------------------------------------------
  abstract UpsertSubscription    : StripeSubscription -> unit
  abstract TryGetSubscription    : subscriptionId:string -> StripeSubscription option
  abstract TryGetSubscriptionBySlug : slug:string -> StripeSubscription option
  abstract ListSubscriptionsForCustomer : CustomerId -> StripeSubscription list

// -- in-memory impl ---------------------------------------------------------

type InMemoryStripeStore () =
  let links = ConcurrentDictionary<string, string>()           // pb_cust_id -> cus_xxx
  let reverse = ConcurrentDictionary<string, string>()         // cus_xxx -> pb_cust_id
  let subs = ConcurrentDictionary<string, StripeSubscription>()
  interface IStripeStore with
    member _.TryGetStripeCustomerId (CustomerId cid) =
      match links.TryGetValue cid with true, v -> Some v | _ -> None
    member _.SetStripeCustomerId (CustomerId cid) sc =
      links.TryAdd(cid, sc) |> ignore
      reverse.TryAdd(sc, cid) |> ignore
    member _.TryGetCustomerByStripeId sc =
      match reverse.TryGetValue sc with
      | true, v -> Some (CustomerId v) | _ -> None
    member _.UpsertSubscription s =
      subs.[s.id] <- s
    member _.TryGetSubscription id =
      match subs.TryGetValue id with true, v -> Some v | _ -> None
    member _.TryGetSubscriptionBySlug slug =
      subs.Values
      |> Seq.tryFind (fun s -> s.workspaceSlug = Some slug)
    member _.ListSubscriptionsForCustomer cid =
      subs.Values
      |> Seq.filter (fun s -> s.customerId = cid)
      |> Seq.sortByDescending (fun s -> s.createdAt)
      |> Seq.toList

// -- Postgres impl ----------------------------------------------------------

let schema = """
CREATE TABLE IF NOT EXISTS pb_stripe_subscriptions (
  subscription_id        TEXT        PRIMARY KEY,
  subscription_item_id   TEXT        NOT NULL,
  stripe_customer_id     TEXT        NOT NULL,
  customer_id            TEXT        NOT NULL REFERENCES pb_customers(id) ON DELETE CASCADE,
  workspace_slug         TEXT,
  price_id               TEXT        NOT NULL,
  plan                   TEXT        NOT NULL,
  status                 TEXT        NOT NULL,
  current_period_end     TIMESTAMPTZ,
  cancel_at_period_end   BOOLEAN     NOT NULL DEFAULT FALSE,
  created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS pb_stripe_subscriptions_by_customer
  ON pb_stripe_subscriptions (customer_id);
CREATE INDEX IF NOT EXISTS pb_stripe_subscriptions_by_slug
  ON pb_stripe_subscriptions (workspace_slug);
CREATE INDEX IF NOT EXISTS pb_stripe_subscriptions_by_stripe_cust
  ON pb_stripe_subscriptions (stripe_customer_id);
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

let private readRow (r : System.Data.Common.DbDataReader) : StripeSubscription =
  { id                = r.GetString 0
    itemId            = r.GetString 1
    stripeCustomerId  = r.GetString 2
    customerId        = CustomerId (r.GetString 3)
    workspaceSlug     = optStr r 4
    priceId           = r.GetString 5
    plan              =
      PulseBoard.PortalStore.PortalPlan.tryParse (r.GetString 6)
      |> Option.defaultValue PulseBoard.PortalStore.Pro
    status            = r.GetString 7
    currentPeriodEnd  = optDate r 8
    cancelAtPeriodEnd = r.GetBoolean 9
    createdAt         = DateTimeOffset(r.GetDateTime 10, TimeSpan.Zero)
    updatedAt         = DateTimeOffset(r.GetDateTime 11, TimeSpan.Zero) }

let private cols =
  "subscription_id, subscription_item_id, stripe_customer_id, customer_id, \
   workspace_slug, price_id, plan, status, current_period_end, \
   cancel_at_period_end, created_at, updated_at"

let private optParam (cmd : NpgsqlCommand) (name : string)
                     (dbType : NpgsqlTypes.NpgsqlDbType) (value : obj option) =
  let p = cmd.Parameters.Add(name, dbType)
  p.Value <- (value |> Option.defaultValue (box DBNull.Value))

type PgStripeStore (connectionString : string) =
  let openConn () =
    let c = new NpgsqlConnection(connectionString)
    c.Open()
    c
  interface IStripeStore with
    member _.TryGetStripeCustomerId (CustomerId cid) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT stripe_customer_id FROM pb_customer_stripe_links WHERE customer_id = @c",
          conn)
      cmd.Parameters.AddWithValue("c", cid) |> ignore
      match cmd.ExecuteScalar() with
      | :? string as s -> Some s
      | _ -> None

    member _.SetStripeCustomerId (CustomerId cid) sc =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_customer_stripe_links \
             (customer_id, stripe_customer_id, updated_at) \
           VALUES (@c, @sc, NOW()) \
           ON CONFLICT (customer_id) DO NOTHING",
          conn)
      cmd.Parameters.AddWithValue("c",  cid) |> ignore
      cmd.Parameters.AddWithValue("sc", sc)  |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGetCustomerByStripeId sc =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "SELECT customer_id FROM pb_customer_stripe_links WHERE stripe_customer_id = @sc",
          conn)
      cmd.Parameters.AddWithValue("sc", sc) |> ignore
      match cmd.ExecuteScalar() with
      | :? string as s -> Some (CustomerId s)
      | _ -> None

    member _.UpsertSubscription s =
      let (CustomerId cid) = s.customerId
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          "INSERT INTO pb_stripe_subscriptions \
             (subscription_id, subscription_item_id, stripe_customer_id, customer_id, \
              workspace_slug, price_id, plan, status, \
              current_period_end, cancel_at_period_end, created_at, updated_at) \
           VALUES (@id, @item, @sc, @c, @slug, @price, @plan, @status, @cpe, @cape, @ca, @ua) \
           ON CONFLICT (subscription_id) DO UPDATE SET \
             subscription_item_id   = EXCLUDED.subscription_item_id, \
             stripe_customer_id     = EXCLUDED.stripe_customer_id, \
             workspace_slug         = EXCLUDED.workspace_slug, \
             price_id               = EXCLUDED.price_id, \
             plan                   = EXCLUDED.plan, \
             status                 = EXCLUDED.status, \
             current_period_end     = EXCLUDED.current_period_end, \
             cancel_at_period_end   = EXCLUDED.cancel_at_period_end, \
             updated_at             = EXCLUDED.updated_at",
          conn)
      cmd.Parameters.AddWithValue("id",     s.id) |> ignore
      cmd.Parameters.AddWithValue("item",   s.itemId) |> ignore
      cmd.Parameters.AddWithValue("sc",     s.stripeCustomerId) |> ignore
      cmd.Parameters.AddWithValue("c",      cid) |> ignore
      optParam cmd "slug"  NpgsqlTypes.NpgsqlDbType.Text (s.workspaceSlug |> Option.map box)
      cmd.Parameters.AddWithValue("price",  s.priceId) |> ignore
      cmd.Parameters.AddWithValue("plan",
        PulseBoard.PortalStore.PortalPlan.toString s.plan) |> ignore
      cmd.Parameters.AddWithValue("status", s.status) |> ignore
      optParam cmd "cpe"   NpgsqlTypes.NpgsqlDbType.TimestampTz
        (s.currentPeriodEnd |> Option.map (fun d -> box d.UtcDateTime))
      cmd.Parameters.AddWithValue("cape",   s.cancelAtPeriodEnd) |> ignore
      cmd.Parameters.AddWithValue("ca",     s.createdAt.UtcDateTime) |> ignore
      cmd.Parameters.AddWithValue("ua",     s.updatedAt.UtcDateTime) |> ignore
      cmd.ExecuteNonQuery() |> ignore

    member _.TryGetSubscription id =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf "SELECT %s FROM pb_stripe_subscriptions WHERE subscription_id = @id" cols,
          conn)
      cmd.Parameters.AddWithValue("id", id) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readRow r) else None

    member _.TryGetSubscriptionBySlug slug =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_stripe_subscriptions \
             WHERE workspace_slug = @s \
             ORDER BY updated_at DESC LIMIT 1"
            cols,
          conn)
      cmd.Parameters.AddWithValue("s", slug) |> ignore
      use r = cmd.ExecuteReader()
      if r.Read() then Some (readRow r) else None

    member _.ListSubscriptionsForCustomer (CustomerId cid) =
      use conn = openConn ()
      use cmd =
        new NpgsqlCommand(
          sprintf
            "SELECT %s FROM pb_stripe_subscriptions \
             WHERE customer_id = @c ORDER BY created_at DESC"
            cols,
          conn)
      cmd.Parameters.AddWithValue("c", cid) |> ignore
      use r = cmd.ExecuteReader()
      [ while r.Read() do yield readRow r ]
