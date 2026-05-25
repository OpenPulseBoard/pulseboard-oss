module PulseBoard.FreeTierSleeper

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open PulseBoard.PortalStore

// Phase 10 step 7 — periodic sweeper that archives free-tier
// workspaces idle for longer than `idleThreshold`. "Sleep" here
// means we call the provisioner's archive admin endpoint, which
// in turn invokes `IFlyClient.SuspendApp` on the per-workspace
// Fly machine. The workspace is brought back online when the
// customer hits the unarchive button in /portal (same path as
// any other archive).
//
// Why on the apex instead of the provisioner? The apex owns
// `pb_customer_workspaces.last_active_at` (heartbeats arrive
// there from the workspace edge over the public API). The
// provisioner doesn't track activity — its job ends after the
// Fly machine is up. Keeping the sweeper here means the
// activity-tracking and sleep-policy state stay co-located.

[<NoComparison; NoEquality>]
type SleeperConfig =
  { store           : ICustomerWorkspaceStore
    /// Same client config the portal uses to reach the provisioner.
    provisionerUrl  : string
    provisionerToken: string
    /// Minimum idleness before a free workspace is archived. `<= 0`
    /// disables the sweeper outright.
    idleThreshold   : TimeSpan
    /// How often the sweeper runs.
    interval        : TimeSpan
    /// Safety cap so a misconfigured threshold (e.g. 1 minute) can't
    /// nuke the entire free tier in one tick.
    maxPerTick      : int }

let private http = new HttpClient(Timeout = TimeSpan.FromMinutes 2.0)

let private callArchive (cfg : SleeperConfig) (slug : string)
                        : Async<Result<unit, int * string>> =
  async {
    try
      let url =
        sprintf "%s/admin/workspaces/%s/archive"
          (cfg.provisionerUrl.TrimEnd '/')
          (Uri.EscapeDataString slug)
      use req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", cfg.provisionerToken)
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if resp.IsSuccessStatusCode then return Result.Ok ()
      else return Result.Error (int resp.StatusCode, text)
    with ex ->
      return Result.Error (502, ex.Message)
  }

let private sweepOnce (cfg : SleeperConfig) : Async<unit> =
  async {
    let now = DateTimeOffset.UtcNow
    let threshold = now - cfg.idleThreshold
    let idle = cfg.store.ListIdleFreeWorkspaces threshold
    if List.isEmpty idle then return ()
    else
      let take = min (List.length idle) cfg.maxPerTick
      printfn "  [sleeper] %d idle free workspace(s) past %s; archiving %d"
        (List.length idle)
        (cfg.idleThreshold.ToString())
        take
      for w in idle |> List.truncate take do
        let idle = now - w.lastActiveAt
        let! r = callArchive cfg w.slug
        match r with
        | Result.Ok () ->
          let updated =
            cfg.store.Update w.slug (fun cur ->
              { cur with
                  status = Archived
                  archivedAt = Some now
                  updatedAt = now
                  error = Some (sprintf "auto-archived after %d days idle" (int idle.TotalDays)) })
          ignore updated
          printfn "  [sleeper] %s archived (idle %s)" w.slug (idle.ToString())
        | Result.Error (st, msg) ->
          eprintfn "  [sleeper] archive %s failed: HTTP %d %s" w.slug st msg
  }

/// Spawn the sweeper. Returns a `CancellationTokenSource` the caller
/// can use to stop it on shutdown. The loop logs but never throws —
/// any internal exception is caught and the next tick proceeds.
let start (cfg : SleeperConfig) : CancellationTokenSource =
  let cts = new CancellationTokenSource()
  if cfg.idleThreshold <= TimeSpan.Zero then
    printfn "  [sleeper] disabled (idleThreshold <= 0)"
    cts
  elif String.IsNullOrWhiteSpace cfg.provisionerUrl
       || String.IsNullOrWhiteSpace cfg.provisionerToken then
    eprintfn "  [sleeper] disabled (provisioner url/token unset)"
    cts
  else
    printfn "  Sleeper:       free-tier idle archive every %s after %s of inactivity (max %d/tick)"
      (cfg.interval.ToString()) (cfg.idleThreshold.ToString()) cfg.maxPerTick
    let loop = async {
      // Stagger the first sweep slightly so startup isn't pegged.
      do! Async.Sleep (TimeSpan.FromSeconds 30.0)
      while not cts.IsCancellationRequested do
        try do! sweepOnce cfg
        with ex ->
          eprintfn "  [sleeper] tick crashed: %s" ex.Message
        try do! Async.Sleep cfg.interval
        with :? OperationCanceledException -> ()
    }
    Async.Start(loop, cts.Token)
    cts
