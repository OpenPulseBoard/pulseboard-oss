module PulseBoard.PurgeCron

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open PulseBoard.PortalStore

// Phase 10 step 10 — two lifecycle automations sharing a single
// periodic loop:
//
//   1. **Archive → purge.** Workspaces that have been in `Archived`
//      state for longer than `purgeThreshold` are permanently
//      destroyed via the provisioner's `/admin/workspaces/<slug>/purge`
//      endpoint (which tears down the Fly app and drops the
//      per-workspace Postgres schema). On success we hard-delete the
//      `pb_customer_workspaces` row.
//
//   2. **Payment overdue → archive.** Workspaces flagged
//      `overdueSince` (set by `reconcileWorkspacePlan` when Stripe
//      reports a non-entitled subscription on a paid workspace) for
//      longer than `overdueGrace` are archived via the provisioner.
//      The row stays alive in `Archived` state and will eventually
//      be picked up by pass (1) if the customer never returns.
//
// Both passes share the same provisioner credentials, safety cap,
// and cancellation token. Either pass is disabled by setting its
// threshold to `<= 0`.

[<NoComparison; NoEquality>]
type CronConfig =
  { store            : ICustomerWorkspaceStore
    provisionerUrl   : string
    provisionerToken : string
    /// Archived-for-longer-than this triggers purge. `<= 0` disables
    /// the purge pass entirely.
    purgeThreshold   : TimeSpan
    /// Overdue-for-longer-than this triggers archive. `<= 0` disables
    /// the overdue pass entirely.
    overdueGrace     : TimeSpan
    /// How often the loop runs (one tick = one purge pass + one
    /// overdue pass).
    interval         : TimeSpan
    /// Safety cap per pass per tick.
    maxPerTick       : int }

let private http = new HttpClient(Timeout = TimeSpan.FromMinutes 2.0)

let private bearer (cfg : CronConfig) =
  AuthenticationHeaderValue("Bearer", cfg.provisionerToken)

let private postAdmin (cfg : CronConfig) (path : string) (body : string)
                      : Async<Result<unit, int * string>> =
  async {
    try
      let url =
        sprintf "%s/%s" (cfg.provisionerUrl.TrimEnd '/') (path.TrimStart '/')
      use req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Headers.Authorization <- bearer cfg
      if not (String.IsNullOrEmpty body) then
        req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if resp.IsSuccessStatusCode then return Result.Ok ()
      else return Result.Error (int resp.StatusCode, text)
    with ex ->
      return Result.Error (502, ex.Message)
  }

let private jsonEscape (s : string) =
  System.Text.Json.JsonSerializer.Serialize (s : string)

// -- pass 1: archived → purge ----------------------------------------------

let private purgePass (cfg : CronConfig) : Async<unit> =
  async {
    if cfg.purgeThreshold <= TimeSpan.Zero then return ()
    else
    let now = DateTimeOffset.UtcNow
    let threshold = now - cfg.purgeThreshold
    let candidates = cfg.store.ListPurgeCandidates threshold
    if List.isEmpty candidates then return ()
    else
      let take = min (List.length candidates) cfg.maxPerTick
      printfn "  [purge] %d archived workspace(s) past %s; purging %d"
        (List.length candidates) (cfg.purgeThreshold.ToString()) take
      for w in candidates |> List.truncate take do
        let path = sprintf "admin/workspaces/%s/purge" (Uri.EscapeDataString w.slug)
        let body = sprintf """{"confirm":%s}""" (jsonEscape w.slug)
        let! r = postAdmin cfg path body
        match r with
        | Result.Ok () ->
          try cfg.store.Delete w.slug
          with ex ->
            eprintfn "  [purge] %s: registry delete failed: %s" w.slug ex.Message
          let aged =
            match w.archivedAt with
            | Some a -> (now - a).ToString()
            | None   -> "?"
          printfn "  [purge] %s purged (archived for %s)" w.slug aged
        | Result.Error (st, msg) ->
          eprintfn "  [purge] %s failed: HTTP %d %s" w.slug st msg
  }

// -- pass 2: payment overdue → archive -------------------------------------

let private overduePass (cfg : CronConfig) : Async<unit> =
  async {
    if cfg.overdueGrace <= TimeSpan.Zero then return ()
    else
    let now = DateTimeOffset.UtcNow
    let threshold = now - cfg.overdueGrace
    let candidates = cfg.store.ListOverdueCandidates threshold
    if List.isEmpty candidates then return ()
    else
      let take = min (List.length candidates) cfg.maxPerTick
      printfn "  [overdue] %d workspace(s) past %s grace; archiving %d"
        (List.length candidates) (cfg.overdueGrace.ToString()) take
      for w in candidates |> List.truncate take do
        let path = sprintf "admin/workspaces/%s/archive" (Uri.EscapeDataString w.slug)
        let! r = postAdmin cfg path ""
        match r with
        | Result.Ok () ->
          let days =
            match w.overdueSince with
            | Some t -> int (now - t).TotalDays
            | None   -> 0
          cfg.store.Update w.slug (fun cur ->
            { cur with
                status     = Archived
                archivedAt = Some now
                updatedAt  = now
                error      = Some (sprintf "auto-archived after %d days payment overdue" days) })
          |> ignore
          printfn "  [overdue] %s archived (overdue %d day(s))" w.slug days
        | Result.Error (st, msg) ->
          eprintfn "  [overdue] %s failed: HTTP %d %s" w.slug st msg
  }

let private tickOnce (cfg : CronConfig) : Async<unit> =
  async {
    try do! overduePass cfg
    with ex -> eprintfn "  [overdue] pass crashed: %s" ex.Message
    try do! purgePass cfg
    with ex -> eprintfn "  [purge] pass crashed: %s" ex.Message
  }

/// Spawn the cron loop. Returns a `CancellationTokenSource` the
/// caller can use to stop it on shutdown. The loop logs but never
/// throws — any internal exception is caught and the next tick
/// proceeds. Returns a disabled (already-cancelled-on-stop) source
/// when both thresholds are non-positive or credentials are missing.
let start (cfg : CronConfig) : CancellationTokenSource =
  let cts = new CancellationTokenSource()
  let purgeOn   = cfg.purgeThreshold > TimeSpan.Zero
  let overdueOn = cfg.overdueGrace  > TimeSpan.Zero
  if not purgeOn && not overdueOn then
    printfn "  [purge-cron] disabled (purgeThreshold and overdueGrace both <= 0)"
    cts
  elif String.IsNullOrWhiteSpace cfg.provisionerUrl
       || String.IsNullOrWhiteSpace cfg.provisionerToken then
    eprintfn "  [purge-cron] disabled (provisioner url/token unset)"
    cts
  else
    printfn "  PurgeCron:     every %s (purge after %s, overdue grace %s, max %d/tick/pass)"
      (cfg.interval.ToString())
      (if purgeOn   then cfg.purgeThreshold.ToString() else "disabled")
      (if overdueOn then cfg.overdueGrace.ToString()   else "disabled")
      cfg.maxPerTick
    let loop = async {
      // Stagger the first tick a little so the sleeper and we don't
      // both stampede the provisioner on cold start.
      do! Async.Sleep (TimeSpan.FromSeconds 60.0)
      while not cts.IsCancellationRequested do
        do! tickOnce cfg
        try do! Async.Sleep cfg.interval
        with :? OperationCanceledException -> ()
    }
    Async.Start(loop, cts.Token)
    cts
