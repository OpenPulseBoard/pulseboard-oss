module PulseBoard.DemoFeeder.Program

// Demo data generator for the default "Overview" dashboard.
//
// Posts random-but-plausible samples to `/ingest/metrics` and
// `/ingest/logs` on a PulseBoard edge so the panels of the auto-created
// Overview dashboard (`cpu_usage`, `http_requests_total`,
// `system.disk.used`, recent logs) light up.
//
// Notes
// -----
// * The Overview dashboard's `Active alerts` (`__alerts.firing`) and
//   `Service health` (`__listeners.up`) panels are driven by the edge's
//   alert engine / listener registry, not by the ingest endpoints, so
//   they cannot be faked by this tool. They will read 0 / "no listeners"
//   unless you configure alert rules and listeners on the workspace.
// * Labels are encoded into the metric name using Prometheus-style
//   `name{k="v",...}` since the JSON ingest shape only reads `name`,
//   `value`, and `ts`. This matches the on-disk series layout under
//   `pulse-data/`.

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading

// ---------------------------------------------------------------------------
// CLI parsing
// ---------------------------------------------------------------------------

type Options =
  { baseUrl     : string
    token       : string option   // Bearer / X-API-Key
    basic       : string option   // "user:pass"
    intervalSec : float
    durationSec : float           // 0 = run forever
    seed        : int option
    insecure    : bool
    verbose     : bool }

let private defaults =
  { baseUrl     = "http://127.0.0.1:8775"
    token       = None
    basic       = None
    intervalSec = 5.0
    durationSec = 0.0
    seed        = None
    insecure    = false
    verbose     = false }

let private printUsage () =
  printfn "PulseBoard demo data feeder"
  printfn ""
  printfn "Usage:"
  printfn "  dotnet run --project tools/DemoFeeder -- [options]"
  printfn ""
  printfn "Options:"
  printfn "  --base-url=URL       Edge base URL (default %s)" defaults.baseUrl
  printfn "  --token=KEY          API key (sent as Authorization: Bearer KEY)"
  printfn "  --basic=USER:PASS    HTTP Basic credentials (mutually exclusive with --token)"
  printfn "  --interval-sec=N     Seconds between batches (default %g)" defaults.intervalSec
  printfn "  --duration-sec=N     Total seconds to run; 0 = forever (default 0)"
  printfn "  --seed=N             Deterministic RNG seed"
  printfn "  --insecure           Skip TLS certificate validation (self-signed / staging certs)"
  printfn "  --verbose            Log every batch"
  printfn "  -h, --help           Show this help"

let private parseArgs (argv : string[]) : Options option =
  let mutable o = defaults
  let mutable showHelp = false
  let mutable bad : string option = None
  let tryKv (prefix : string) (a : string) =
    if a.StartsWith prefix then Some (a.Substring prefix.Length) else None
  for a in argv do
    match a with
    | "-h" | "--help" -> showHelp <- true
    | "--verbose"     -> o <- { o with verbose = true }
    | "--insecure"    -> o <- { o with insecure = true }
    | _ ->
      match tryKv "--base-url="     a with
      | Some v -> o <- { o with baseUrl = v.TrimEnd '/' }
      | None ->
      match tryKv "--token="        a with
      | Some v -> o <- { o with token = Some v }
      | None ->
      match tryKv "--basic="        a with
      | Some v -> o <- { o with basic = Some v }
      | None ->
      match tryKv "--interval-sec=" a with
      | Some v -> o <- { o with intervalSec = float v }
      | None ->
      match tryKv "--duration-sec=" a with
      | Some v -> o <- { o with durationSec = float v }
      | None ->
      match tryKv "--seed="         a with
      | Some v -> o <- { o with seed = Some (int v) }
      | None ->
        bad <- Some a
  if showHelp then printUsage (); None
  elif bad.IsSome then
    eprintfn "unknown argument: %s" bad.Value
    printUsage ()
    None
  elif o.token.IsSome && o.basic.IsSome then
    eprintfn "--token and --basic are mutually exclusive"
    None
  else Some o

// ---------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------

let private mkClient (o : Options) : HttpClient =
  let handler = new HttpClientHandler()
  if o.insecure then
    handler.ServerCertificateCustomValidationCallback <-
      System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
  let c = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 30.0)
  // Force HTTP/1.1. Caddy and most edges negotiate h2 by default, but on
  // macOS .NET the h2 + ALPN dance occasionally surfaces as a misleading
  // "SSL connection could not be established" error. Ingest doesn't
  // benefit from multiplexing, so pin to 1.1 for reliability.
  c.DefaultRequestVersion <- System.Net.HttpVersion.Version11
  c.DefaultVersionPolicy  <- System.Net.Http.HttpVersionPolicy.RequestVersionExact
  match o.token, o.basic with
  | Some t, _ ->
    c.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", t)
  | _, Some b ->
    let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes b)
    c.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Basic", encoded)
  | _ -> ()
  c

let private postJson (client : HttpClient) (url : string) (body : string)
                     (verbose : bool) : Async<unit> = async {
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  try
    let! resp = client.PostAsync(url, content) |> Async.AwaitTask
    let! txt  = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    if not resp.IsSuccessStatusCode then
      eprintfn "[demo-feeder] %s -> %d: %s" url (int resp.StatusCode) txt
    elif verbose then
      printfn "[demo-feeder] %s -> %d: %s" url (int resp.StatusCode) txt
  with ex ->
    eprintfn "[demo-feeder] %s failed: %s" url ex.Message
}

// ---------------------------------------------------------------------------
// JSON helpers — hand-rolled to keep deps to zero
// ---------------------------------------------------------------------------

let private esc (s : string) =
  let sb = StringBuilder(s.Length + 2)
  for c in s do
    match c with
    | '\\' -> sb.Append "\\\\" |> ignore
    | '"'  -> sb.Append "\\\"" |> ignore
    | '\n' -> sb.Append "\\n"  |> ignore
    | '\r' -> sb.Append "\\r"  |> ignore
    | '\t' -> sb.Append "\\t"  |> ignore
    | c when int c < 0x20 -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
    | c -> sb.Append c |> ignore
  sb.ToString()

let private inv = System.Globalization.CultureInfo.InvariantCulture

let private sampleJson (name : string) (value : float) (tsMs : int64) =
  sprintf "{\"name\":\"%s\",\"value\":%s,\"ts\":%d}"
    (esc name) (value.ToString("R", inv)) tsMs

let private logJson (tsMs : int64) (service : string) (level : string) (message : string) =
  sprintf "{\"ts\":%d,\"service\":\"%s\",\"level\":\"%s\",\"message\":\"%s\"}"
    tsMs (esc service) (esc level) (esc message)

// ---------------------------------------------------------------------------
// Workload state — smooth random walks so the timeseries panels look alive
// ---------------------------------------------------------------------------

type Walker = { mutable value : float; lo : float; hi : float; step : float }

let private clamp lo hi v = max lo (min hi v)

let private tick (rng : Random) (w : Walker) =
  let delta = (rng.NextDouble() - 0.5) * 2.0 * w.step
  w.value <- clamp w.lo w.hi (w.value + delta)
  w.value

// HTTP requests counter shape: (method, status) -> running total
type Counter = { method : string; status : string; mutable total : float }

let private mkCounters () =
  [|
    { method = "GET";  status = "200"; total = 0.0 }
    { method = "GET";  status = "404"; total = 0.0 }
    { method = "POST"; status = "200"; total = 0.0 }
    { method = "POST"; status = "500"; total = 0.0 }
  |]

let private services = [| "web"; "api"; "worker"; "db" |]
let private levels   = [| "info"; "info"; "info"; "warn"; "error" |]

let private mkLogLine (rng : Random) =
  let svc = services.[rng.Next services.Length]
  let lvl = levels.[rng.Next levels.Length]
  let a = rng.Next 9999
  let b = rng.Next 9999
  let msg =
    match rng.Next 8 with
    | 0 -> sprintf "handled request id=%d in %dms" a b
    | 1 -> sprintf "cache hit for key=user:%d" a
    | 2 -> sprintf "background job %d completed" a
    | 3 -> sprintf "slow query took %dms (id=%d)" a b
    | 4 -> sprintf "retrying upstream connection attempt=%d" a
    | 5 -> sprintf "user %d signed in" a
    | 6 -> sprintf "queue depth high backlog=%d" a
    | _ -> sprintf "disk write completed bytes=%d" (a * 1024)
  svc, lvl, msg

// ---------------------------------------------------------------------------
// Batch generation
// ---------------------------------------------------------------------------

let private metricsBatch (rng : Random)
                         (cpu : Walker) (disk : Walker) (mem : Walker)
                         (counters : Counter[]) : string =
  let now =
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  let samples = ResizeArray<string>()

  // CPU — labelled per host so it looks like multiple machines.
  for host in [| "web1"; "web2"; "db1" |] do
    let v = tick rng cpu + (rng.NextDouble() - 0.5) * 0.05 |> clamp 0.0 1.0
    samples.Add (sampleJson (sprintf "cpu_usage{host=\"%s\"}" host) v now)

  // Single unlabelled cpu_usage so the default Overview panel (which
  // queries the bare name) still has data.
  samples.Add (sampleJson "cpu_usage" cpu.value now)

  // HTTP requests — increment counters and emit current totals per
  // (method,status). Promql sum / rate over the bare name will fold
  // every labelled series together.
  for c in counters do
    let inc =
      if c.status = "500" then float (rng.Next 3)
      elif c.status = "404" then float (rng.Next 8)
      else float (rng.Next 60 + 5)
    c.total <- c.total + inc
    let name = sprintf "http_requests_total{method=\"%s\",status=\"%s\"}" c.method c.status
    samples.Add (sampleJson name c.total now)
  samples.Add (sampleJson "http_requests_total"
                          (counters |> Array.sumBy (fun c -> c.total)) now)

  // Memory used + disk used — emit both for the "Memory used" stat
  // panel (the default queries `system.disk.used`, but real memory is
  // useful too).
  let diskV = tick rng disk
  let memV  = tick rng mem
  samples.Add (sampleJson "system.disk.used" diskV now)
  samples.Add (sampleJson "system.memory.used" memV now)

  "[" + String.Join(",", samples) + "]"

let private logsBatch (rng : Random) : string =
  let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  let n = 3 + rng.Next 5
  let lines =
    [| for _ in 1 .. n ->
         let svc, lvl, msg = mkLogLine rng
         logJson now svc lvl msg |]
  "[" + String.Join(",", lines) + "]"

// ---------------------------------------------------------------------------
// Main loop
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main argv =
  match parseArgs argv with
  | None -> 0
  | Some opts ->
    let rng =
      match opts.seed with
      | Some s -> Random s
      | None   -> Random ()

    let cpu  = { value = 0.35; lo = 0.05; hi = 0.95; step = 0.08 }
    // disk grows toward ~80% of 500GB; in bytes
    let disk = { value = 1.2e11; lo = 5.0e10; hi = 4.5e11; step = 5.0e8 }
    let mem  = { value = 4.0e9;  lo = 1.0e9;  hi = 1.6e10; step = 2.0e8 }
    let counters = mkCounters ()

    use client = mkClient opts
    let metricsUrl = opts.baseUrl + "/ingest/metrics"
    let logsUrl    = opts.baseUrl + "/ingest/logs"

    let auth =
      match opts.token, opts.basic with
      | Some _, _ -> "bearer"
      | _, Some _ -> "basic"
      | _         -> "none"
    printfn "[demo-feeder] base=%s auth=%s interval=%gs duration=%gs"
      opts.baseUrl auth opts.intervalSec opts.durationSec

    // Quiet Ctrl-C: flip the flag so the loop exits cleanly.
    let stop = ref false
    Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      stop := true
      printfn "[demo-feeder] stopping…")

    let started = DateTime.UtcNow
    let intervalMs = max 100 (int (opts.intervalSec * 1000.0))

    let mutable batches = 0
    while not !stop
          && (opts.durationSec <= 0.0
              || (DateTime.UtcNow - started).TotalSeconds < opts.durationSec) do
      let mBody = metricsBatch rng cpu disk mem counters
      let lBody = logsBatch rng
      Async.RunSynchronously (async {
        do! postJson client metricsUrl mBody opts.verbose
        do! postJson client logsUrl    lBody opts.verbose
      })
      batches <- batches + 1
      if not opts.verbose && batches % 12 = 0 then
        printfn "[demo-feeder] sent %d batches" batches
      if not !stop then Thread.Sleep intervalMs

    printfn "[demo-feeder] done; %d batches sent" batches
    0
