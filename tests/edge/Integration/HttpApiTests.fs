module PulseBoard.Tests.Integration.HttpApiTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Xunit
open FsUnit.Xunit
open PulseBoard.Tests.Helpers.TestEdge

// ---------------------------------------------------------------------------
// In-process HTTP integration tests.
//
// Each test creates its own TestEnv (starts Suave on an ephemeral port)
// and disposes it when done. Routes exercised:
//
//   GET  /api/healthz          — health probe
//   POST /ingest/metrics        — metric ingest
//   GET  /api/metrics           — metric name listing
//   GET  /api/metrics/<name>    — metric series query
//   POST /ingest/logs           — log ingest
//   GET  /api/logs              — log tail
//
// These tests do NOT require Docker and are tagged Category=Integration
// so they can be filtered independently of Postgres tests.
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
type HttpApiTests() =

    // -- helpers ------------------------------------------------------------

    let postJson (client : HttpClient) (path : string) (body : string) =
        let content = new StringContent(body, Encoding.UTF8, "application/json")
        client.PostAsync(path, content).GetAwaiter().GetResult()

    let getStr (client : HttpClient) (path : string) =
        client.GetStringAsync(path).GetAwaiter().GetResult()

    let getStatus (client : HttpClient) (path : string) =
        client.GetAsync(path).GetAwaiter().GetResult()

    // -- health check -------------------------------------------------------

    [<Fact>]
    member _.``GET /api/healthz returns 200 with ok:true`` () =
        use env  = create ()
        use http = env.Http
        let resp = http.GetAsync("/api/healthz").GetAwaiter().GetResult()
        resp.StatusCode |> should equal HttpStatusCode.OK
        let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Assert.Contains("true", body)

    // -- metric ingest + query ----------------------------------------------

    [<Fact>]
    member _.``POST /ingest/metrics accepts a valid single-sample payload`` () =
        use env  = create ()
        use http = env.Http
        let body = """{"name":"test_metric","value":42.0}"""
        let resp = postJson http "/ingest/metrics" body
        resp.StatusCode |> should equal HttpStatusCode.OK
        let json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        let doc  = JsonDocument.Parse json
        doc.RootElement.GetProperty("accepted").GetInt32() |> should equal 1

    [<Fact>]
    member _.``POST /ingest/metrics then GET /api/metrics lists the metric name`` () =
        use env  = create ()
        use http = env.Http
        let name = sprintf "m_%s" (Guid.NewGuid().ToString("N").[..5])
        postJson http "/ingest/metrics" (sprintf """{"name":"%s","value":1.0}""" name)
        |> ignore
        let json  = getStr http "/api/metrics"
        Assert.Contains(name, json)

    [<Fact>]
    member _.``GET /api/metrics/<name> returns data after ingest`` () =
        use env  = create ()
        use http = env.Http
        let name = sprintf "s_%s" (Guid.NewGuid().ToString("N").[..5])
        postJson http "/ingest/metrics" (sprintf """{"name":"%s","value":99.0}""" name)
        |> ignore
        let resp = getStatus http (sprintf "/api/metrics/%s" name)
        resp.StatusCode |> should equal HttpStatusCode.OK
        let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Assert.Contains("99", body)

    [<Fact>]
    member _.``POST /ingest/metrics with JSON array accepts multiple samples`` () =
        use env  = create ()
        use http = env.Http
        let body = """[{"name":"arr_m","value":1.0},{"name":"arr_m","value":2.0}]"""
        let resp = postJson http "/ingest/metrics" body
        resp.StatusCode |> should equal HttpStatusCode.OK
        let doc  = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())
        doc.RootElement.GetProperty("accepted").GetInt32() |> should equal 2

    // -- log ingest + query -------------------------------------------------

    [<Fact>]
    member _.``POST /ingest/logs accepts a valid log entry`` () =
        use env  = create ()
        use http = env.Http
        let body = """{"service":"svc","level":"info","message":"hello world"}"""
        let resp = postJson http "/ingest/logs" body
        resp.StatusCode |> should equal HttpStatusCode.OK
        let doc  = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())
        doc.RootElement.GetProperty("accepted").GetInt32() |> should equal 1

    [<Fact>]
    member _.``GET /api/logs returns a JSON array after log ingest`` () =
        use env  = create ()
        use http = env.Http
        postJson http "/ingest/logs"
            """{"service":"app","level":"warn","message":"test entry"}"""
        |> ignore
        let json = getStr http "/api/logs"
        json.TrimStart().StartsWith "[" |> should be True
