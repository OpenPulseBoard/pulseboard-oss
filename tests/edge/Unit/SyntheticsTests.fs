module PulseBoard.Tests.Unit.SyntheticsTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open Xunit
open FsUnit.Xunit
open PulseBoard.Synthetics
open PulseBoard.Tenancy
open PulseBoard.TimeSeries

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private mk kind target : Check =
    { id = Guid.NewGuid().ToString "N"
      name = "probe"; kind = kind; target = target
      intervalMs = 60_000L; timeoutMs = 5_000L
      expectStatus = 0; enabled = true
      createdAt = 0L; updatedAt = 0L }

let private withStore (f : ISyntheticStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try f (FileSyntheticStore dir :> ISyntheticStore, TenantId "t1")
    finally try Directory.Delete(dir, true) with _ -> ()

// -- kind parsing -----------------------------------------------------------

[<Fact>]
let ``kindOfStr accepts http https tcp dns and rejects others`` () =
    kindOfStr "http"  |> should equal (Some Http)
    kindOfStr "HTTPS" |> should equal (Some Http)
    kindOfStr "tcp"   |> should equal (Some Tcp)
    kindOfStr "dns"   |> should equal (Some Dns)
    kindOfStr "icmp"  |> should equal None

// -- validation -------------------------------------------------------------

[<Fact>]
let ``validate rejects empty name and target`` () =
    match validate { mk Http "https://x" with name = "" } with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate { mk Http "" with name = "n" } with
    | Error _ -> () | Ok _ -> failwith "expected error"

[<Fact>]
let ``validate requires absolute http url`` () =
    match validate (mk Http "not-a-url") with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mk Http "https://example.com/health") with
    | Ok _ -> () | Error e -> failwith e

[<Fact>]
let ``validate requires tcp host colon port in range`` () =
    match validate (mk Tcp "host") with Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mk Tcp "host:70000") with Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mk Tcp "host:5432") with Ok _ -> () | Error e -> failwith e

[<Fact>]
let ``normalise clamps interval and timeout into bounds`` () =
    let c = normalise { mk Tcp "h:1" with intervalMs = 100L; timeoutMs = 999_999L }
    c.intervalMs |> should equal 5_000L
    c.timeoutMs  |> should equal 60_000L

// -- request parsing --------------------------------------------------------

[<Fact>]
let ``parseRequest assigns id and timestamps on create`` () =
    match parseRequest None """{"name":"api","kind":"http","target":"https://e.com"}""" with
    | Ok c ->
        c.id.Length |> should be (greaterThan 0)
        c.createdAt |> should be (greaterThan 0L)
        c.enabled   |> should equal true
    | Error e -> failwith e

[<Fact>]
let ``parseRequest preserves id and createdAt on update`` () =
    let existing = { mk Http "https://e.com" with id = "keep"; createdAt = 42L }
    match parseRequest (Some existing) """{"name":"api","kind":"http","target":"https://e.com"}""" with
    | Ok c ->
        c.id |> should equal "keep"
        c.createdAt |> should equal 42L
    | Error e -> failwith e

[<Fact>]
let ``parseRequest rejects unknown kind`` () =
    match parseRequest None """{"name":"x","kind":"ping","target":"h"}""" with
    | Error _ -> () | Ok _ -> failwith "expected error"

// -- store roundtrip --------------------------------------------------------

[<Fact>]
let ``FileSyntheticStore upsert get list delete roundtrips`` () =
    withStore (fun (store, tid) ->
        let c = mk Http "https://e.com"
        store.Upsert(tid, c)
        (store.TryGet(tid, c.id)).Value.target |> should equal "https://e.com"
        store.List tid |> Array.length |> should equal 1
        store.Delete(tid, c.id) |> should equal true
        store.TryGet(tid, c.id) |> should equal None
        store.Delete(tid, c.id) |> should equal false)

[<Fact>]
let ``FileSyntheticStore reloads from disk into a fresh instance`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        let tid = TenantId "t1"
        let c = mk Tcp "db:5432"
        (FileSyntheticStore dir :> ISyntheticStore).Upsert(tid, c)
        let fresh = FileSyntheticStore dir :> ISyntheticStore
        (fresh.TryGet(tid, c.id)).Value.kind |> should equal Tcp
    finally try Directory.Delete(dir, true) with _ -> ()

// -- SSRF guard -------------------------------------------------------------

[<Fact>]
let ``hostAllowed blocks loopback and private ranges when not permitted`` () =
    hostAllowed false "127.0.0.1"       |> should equal false
    hostAllowed false "10.1.2.3"        |> should equal false
    hostAllowed false "192.168.0.5"     |> should equal false
    hostAllowed false "172.16.0.1"      |> should equal false
    hostAllowed false "169.254.169.254" |> should equal false
    hostAllowed false "::1"             |> should equal false

[<Fact>]
let ``hostAllowed permits public ip and anything when allowPrivate`` () =
    hostAllowed false "8.8.8.8"   |> should equal true
    hostAllowed true  "127.0.0.1" |> should equal true

// -- series encoding --------------------------------------------------------

[<Fact>]
let ``series encodes labels inline as a prom series string`` () =
    let r = { checkId = "c"; name = "api up"; kind = Http; region = "edge"
              up = true; durationMs = 10.0; detail = "HTTP 200"; at = 0L }
    series "pulse_synthetic_up" r
    |> should equal "pulse_synthetic_up{check=\"api up\",kind=\"http\",region=\"edge\"}"

// -- probes -----------------------------------------------------------------

[<Fact>]
let ``runTcp connects to a listening socket and reports up`` () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    try
        let up, _, _ = runTcp (sprintf "127.0.0.1:%d" port) 2_000L |> Async.RunSynchronously
        up |> should equal true
    finally listener.Stop()

[<Fact>]
let ``runTcp reports down for a closed port`` () =
    // Bind then immediately release to obtain a port nobody is listening on.
    let l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    let port = (l.LocalEndpoint :?> IPEndPoint).Port
    l.Stop()
    let up, _, _ = runTcp (sprintf "127.0.0.1:%d" port) 1_000L |> Async.RunSynchronously
    up |> should equal false

[<Fact>]
let ``runDns resolves localhost`` () =
    let up, _, _ = runDns "localhost" 2_000L |> Async.RunSynchronously
    up |> should equal true

// -- runner -----------------------------------------------------------------

[<Fact>]
let ``Runner Probe records up metric and a log line`` () =
    let metrics = MetricStore(capacityPerMetric = 64)
    let logs    = LogStore(capacity = 64)
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    try
        // allowPrivate=true so the loopback target isn't blocked by the SSRF guard.
        let runner = Runner(FileSyntheticStore (Path.GetTempPath()) :> ISyntheticStore,
                            metrics, logs, "edge", true)
        let c = mk Tcp (sprintf "127.0.0.1:%d" port)
        let r = runner.Probe c |> Async.RunSynchronously
        r.up |> should equal true
        let pts = metrics.Get (series "pulse_synthetic_up" r)
        pts.Length |> should be (greaterThan 0)
        (Array.last pts).value |> should equal 1.0
        logs.Snapshot() |> Array.exists (fun e -> e.service = "synthetics") |> should equal true
    finally listener.Stop()

[<Fact>]
let ``Runner Probe blocks private target when allowPrivate is false`` () =
    let metrics = MetricStore(capacityPerMetric = 64)
    let logs    = LogStore(capacity = 64)
    let runner = Runner(FileSyntheticStore (Path.GetTempPath()) :> ISyntheticStore,
                        metrics, logs, "edge", false)
    let r = runner.Probe (mk Tcp "127.0.0.1:5432") |> Async.RunSynchronously
    r.up |> should equal false
    r.detail |> should haveSubstring "blocked"
