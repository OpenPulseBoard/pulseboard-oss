module PulseBoard.HeartbeatClient

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading

// Phase 10.1 — workspace → apex heartbeat. Each per-customer Fly app
// pings the apex on ingest so the apex's `FreeTierSleeper` knows the
// workspace is still in use. The provisioner stamps a workspace with
// its own slug + a token shared with apex at bootstrap time
// (`PULSE_APEX_HEARTBEAT_URL`, `PULSE_APEX_HEARTBEAT_TOKEN`,
// `PULSE_WORKSPACE_SLUG`).
//
// Design constraints:
// - Must be fire-and-forget. Ingest cannot block on a remote call.
// - Must self-throttle. A burst of 10 000 metrics/sec must produce
//   one heartbeat per minute (or whatever the configured interval),
//   not one per sample.
// - Must never throw out of `bump ()`. Heartbeats are best-effort;
//   any HTTP error is logged once and swallowed.
// - Must compile out cleanly when not configured (apex URL unset).

[<NoComparison; NoEquality>]
type Config =
  { apexUrl   : string         // e.g. "https://pulseboard.cloud"
    token     : string         // matches apex's PULSE_PROVISIONER_TOKEN
    slug      : string         // this workspace's slug
    interval  : TimeSpan }     // minimum gap between heartbeats

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)

// Mutable singleton because the workspace process serves exactly one
// slug; there is no per-tenant fan-out to worry about.
let mutable private cfg : Config option = None

// `lastSentTicks` is updated with `Interlocked.Exchange`; we use ticks
// (int64) so the throttle check is a single atomic read on the hot
// path. `DateTimeOffset.MinValue.UtcTicks = 0L`.
let mutable private lastSentTicks : int64 = 0L
let mutable private failureLogged : int = 0

let private logOnce (msg : string) =
  // Only log the first failure per process; otherwise a misconfigured
  // apex would flood stderr.
  if Interlocked.CompareExchange(&failureLogged, 1, 0) = 0 then
    eprintfn "  [heartbeat] %s (further failures suppressed)" msg

/// Initialize on workspace startup. Pass `None` to disable explicitly
/// (e.g. self-hosted single-tenant deploys).
let init (c : Config option) : unit =
  cfg <- c
  match c with
  | Some c when not (String.IsNullOrWhiteSpace c.apexUrl)
              && not (String.IsNullOrWhiteSpace c.token)
              && not (String.IsNullOrWhiteSpace c.slug) ->
    printfn "  Heartbeat:     %s → %s every %s (min)"
      c.slug c.apexUrl (c.interval.ToString())
  | Some _ ->
    cfg <- None
    eprintfn "  Heartbeat:     disabled (apex url / token / slug empty)"
  | None ->
    printfn "  Heartbeat:     disabled (no apex configured)"

let private sendAsync (c : Config) : Async<unit> =
  async {
    try
      let url = c.apexUrl.TrimEnd '/' + "/api/portal/internal/heartbeat"
      let bodyJson =
        // The apex accepts either {"slug":"..."} or {"slugs":[...]}.
        // Use the scalar form since we always send exactly one.
        sprintf "{\"slug\":%s}"
          (System.Text.Json.JsonSerializer.Serialize c.slug)
      use req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", c.token)
      req.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")
      use! resp = http.SendAsync req |> Async.AwaitTask
      if not resp.IsSuccessStatusCode then
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        logOnce (sprintf "HTTP %d: %s" (int resp.StatusCode) text)
    with ex ->
      logOnce ex.Message
  }

/// Call from every ingest entry point. Cheap when throttled
/// (one atomic read + one comparison); spawns an out-of-band async
/// HTTP POST when the throttle window elapses.
let bump () : unit =
  match cfg with
  | None -> ()
  | Some c ->
    let now = DateTimeOffset.UtcNow
    let last = Volatile.Read(&lastSentTicks)
    let nowTicks = now.UtcTicks
    let intervalTicks = c.interval.Ticks
    if nowTicks - last >= intervalTicks then
      // Claim the slot before firing; if another thread beat us,
      // skip. This guarantees at-most-one heartbeat in flight per
      // throttle window even under concurrent ingest.
      if Interlocked.CompareExchange(&lastSentTicks, nowTicks, last) = last then
        Async.Start (sendAsync c)
