module PulseBoard.Tests.Integration.ProtocolIngestTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Google.Protobuf
open Snappier
open Xunit
open FsUnit.Xunit
open PulseBoard.Tests.Helpers.TestEdge

// ---------------------------------------------------------------------------
// Protocol-level ingest integration tests (Category=Integration).
//
// #2 — OTLP/HTTP metric → /api/prom/api/v1/query returns it.
//   POST /v1/metrics (Content-Type: application/x-protobuf) with a
//   hand-encoded ExportMetricsServiceRequest, then issue an instant
//   Prom query and verify the series is present.
//
// #3 — Loki push → /api/loki/api/v1/query_range returns it.
//   POST /loki/api/v1/push (Content-Type: application/json) with a
//   Loki push payload, then query_range and verify the stream result.
//
// #4 — Prom remote_write (real snappy-protobuf) → stored.
//   Encode a WriteRequest protobuf by hand, snappy-compress it, POST
//   to /api/v1/write, then verify via /api/prom/api/v1/query.
//
// All three use the same unauthenticated single-tenant TestEdge (auth
// disabled = no API key required, single-tenant mode).
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Minimal protobuf hand-encoders.
//
// We build just enough of each wire format to exercise the server-side
// decoders without pulling in generated code or heavy dependencies.
// Every byte appended below follows the proto3 TLV encoding:
//
//   tag  = (field_number << 3) | wire_type
//   LEN  wire_type = 2
//   VARINT wire_type = 0
//   I64  wire_type = 1  (double / fixed64)
// ---------------------------------------------------------------------------

module private Proto =

    let private varintOf (n : uint64) : byte[] =
        let buf = Array.zeroCreate 10
        let mutable v = n
        let mutable i = 0
        while v > 127UL do
            buf.[i] <- byte (v ||| 0x80UL)
            v <- v >>> 7
            i <- i + 1
        buf.[i] <- byte v
        Array.sub buf 0 (i + 1)

    let private uint64LE (n : uint64) : byte[] =
        [| byte n; byte (n >>> 8); byte (n >>> 16); byte (n >>> 24)
           byte (n >>> 32); byte (n >>> 40); byte (n >>> 48); byte (n >>> 56) |]

    let tag (field : int) (wireType : int) : byte[] =
        varintOf (uint64 ((field <<< 3) ||| wireType))

    let lengthDelimited (field : int) (data : byte[]) : byte[] =
        Array.concat [ tag field 2; varintOf (uint64 data.Length); data ]

    let stringField (field : int) (s : string) : byte[] =
        lengthDelimited field (Encoding.UTF8.GetBytes s)

    let doubleField (field : int) (v : float) : byte[] =
        Array.concat [ tag field 1; uint64LE (BitConverter.ToUInt64(BitConverter.GetBytes v, 0)) ]

    let int64Field (field : int) (v : int64) : byte[] =
        Array.concat [ tag field 0; varintOf (uint64 v) ]

    let fixed64Field (field : int) (v : uint64) : byte[] =
        Array.concat [ tag field 1; uint64LE v ]

    // OTLP ExportMetricsServiceRequest(field 1) ->
    //   ResourceMetrics(field 1) ->
    //     ScopeMetrics(field 2) ->
    //       Metric(field 2): name(1), Gauge(5)->data_points(1)->NumberDataPoint
    //         NumberDataPoint: time_unix_nano(3), as_double(4)
    let buildOtlpMetrics (metricName : string) (value : float) (tsNano : uint64) : byte[] =
        // NumberDataPoint: field3=time_unix_nano, field4=as_double
        let ndp =
            Array.concat [
                fixed64Field 3 tsNano
                doubleField  4 value ]
        // Gauge: field1=data_points (repeated)
        let gauge = lengthDelimited 1 ndp
        // Metric: field1=name, field5=gauge
        let metric =
            Array.concat [
                stringField       1 metricName
                lengthDelimited   5 gauge ]
        // ScopeMetrics: field2=metrics (repeated)
        let scopeMetrics = lengthDelimited 2 metric
        // ResourceMetrics: field2=scope_metrics (repeated)
        let resourceMetrics = lengthDelimited 2 scopeMetrics
        // ExportMetricsServiceRequest: field1=resource_metrics (repeated)
        lengthDelimited 1 resourceMetrics

    // Prometheus WriteRequest:
    //   message WriteRequest  { repeated TimeSeries timeseries = 1; }
    //   message TimeSeries    { repeated Label  labels  = 1;
    //                           repeated Sample samples = 2; }
    //   message Label         { string name = 1; string value = 2; }
    //   message Sample        { double value = 1; int64  timestamp = 2; }
    let buildPromWriteRequest (metricName : string) (value : float) (tsMs : int64) : byte[] =
        let nameLabel =
            Array.concat [
                stringField 1 "__name__"
                stringField 2 metricName ]
        let label = lengthDelimited 1 nameLabel
        let sample =
            Array.concat [
                doubleField 1 value
                int64Field  2 tsMs ]
        let samplePb = lengthDelimited 2 sample
        let timeSeries = Array.concat [ label; samplePb ]
        lengthDelimited 1 timeSeries

// ---------------------------------------------------------------------------
// Helpers for building Loki JSON push payloads.
// ---------------------------------------------------------------------------

module private Loki =
    let buildJsonPush (service : string) (line : string) (tsNano : int64) : string =
        // {"streams":[{"stream":{"service_name":"..."},"values":[["<ns>","<line>"]]}]}
        let ns = string tsNano
        sprintf
            """{"streams":[{"stream":{"service_name":"%s","level":"info"},"values":[["%s","%s"]]}]}"""
            service ns line

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Trait("Category", "Integration")>]
type ProtocolIngestTests () =

    let postBinary (client : HttpClient) (path : string) (contentType : string) (body : byte[]) =
        use content = new ByteArrayContent(body)
        content.Headers.ContentType <- System.Net.Http.Headers.MediaTypeHeaderValue.Parse contentType
        client.PostAsync(path, content).GetAwaiter().GetResult()

    let postJsonStr (client : HttpClient) (path : string) (body : string) =
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        client.PostAsync(path, content).GetAwaiter().GetResult()

    let getString (client : HttpClient) (path : string) =
        client.GetStringAsync(path).GetAwaiter().GetResult()

    // -- #2: OTLP/HTTP metric --------------------------------------------------

    [<Fact>]
    member _.``#2 OTLP metric ingest then Prom instant query returns the series`` () =
        use env  = create ()
        use http = env.Http
        let name = sprintf "otlp_%s" (Guid.NewGuid().ToString("N").[..5])
        let now  = uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000UL
        let pb   = Proto.buildOtlpMetrics name 7.77 now
        let resp = postBinary http "/v1/metrics" "application/x-protobuf" pb
        resp.StatusCode |> should equal HttpStatusCode.OK
        // Query
        let query = Uri.EscapeDataString(name)
        let json  = getString http (sprintf "/api/prom/api/v1/query?query=%s" query)
        let doc   = JsonDocument.Parse json
        let status = doc.RootElement.GetProperty("status").GetString()
        status |> should equal "success"
        let result = doc.RootElement.GetProperty("data").GetProperty("result")
        result.GetArrayLength() |> should be (greaterThan 0)

    [<Fact>]
    member _.``#2 OTLP metric accepted header reflects sample count`` () =
        use env  = create ()
        use http = env.Http
        let name = sprintf "otlp_cnt_%s" (Guid.NewGuid().ToString("N").[..5])
        let now  = uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1_000_000UL
        let pb   = Proto.buildOtlpMetrics name 1.0 now
        let resp = postBinary http "/v1/metrics" "application/x-protobuf" pb
        resp.StatusCode |> should equal HttpStatusCode.OK
        let accepted =
            resp.Headers.TryGetValues("X-PulseBoard-Accepted")
            |> (fun (ok, vs) -> if ok then Seq.tryHead vs else None)
            |> Option.defaultValue "0"
            |> int
        accepted |> should be (greaterThan 0)

    // -- #3: Loki push ---------------------------------------------------------

    [<Fact>]
    member _.``#3 Loki JSON push then query_range returns the log line`` () =
        use env  = create ()
        use http = env.Http
        let svc  = sprintf "svc_%s" (Guid.NewGuid().ToString("N").[..5])
        let line = sprintf "hello from %s" svc
        let tsNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L
        let body = Loki.buildJsonPush svc line tsNano
        let resp = postJsonStr http "/loki/api/v1/push" body
        // Loki returns 204 on success
        resp.StatusCode |> should equal HttpStatusCode.NoContent
        // Query — use the 'service' label (logMatches maps LogEntry.service to "service")
        let expr  = Uri.EscapeDataString(sprintf """{service="%s"}""" svc)
        let nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L
        let startS = string (nowNs - 120_000_000_000L)  // -2 minutes in ns
        let endS   = string (nowNs + 120_000_000_000L)  // +2 minutes in ns
        let url = sprintf "/api/loki/api/v1/query_range?query=%s&start=%s&end=%s&limit=10" expr startS endS
        let json = getString http url
        let doc  = JsonDocument.Parse json
        let status = doc.RootElement.GetProperty("status").GetString()
        status |> should equal "success"
        let result = doc.RootElement.GetProperty("data").GetProperty("result")
        result.GetArrayLength() |> should be (greaterThan 0)

    [<Fact>]
    member _.``#3 Loki push accepted header reflects entry count`` () =
        use env  = create ()
        use http = env.Http
        let tsNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L
        let body = Loki.buildJsonPush "probe" "entry" tsNano
        let resp = postJsonStr http "/loki/api/v1/push" body
        resp.StatusCode |> should equal HttpStatusCode.NoContent
        let accepted =
            resp.Headers.TryGetValues("X-PulseBoard-Accepted")
            |> (fun (ok, vs) -> if ok then Seq.tryHead vs else None)
            |> Option.defaultValue "0"
            |> int
        accepted |> should be (greaterThan 0)

    // -- #4: Prom remote_write -------------------------------------------------

    [<Fact>]
    member _.``#4 Prom remote_write (snappy-protobuf) then Prom query returns the series`` () =
        use env  = create ()
        use http = env.Http
        let name  = sprintf "rw_%s" (Guid.NewGuid().ToString("N").[..5])
        let tsMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let proto = Proto.buildPromWriteRequest name 42.0 tsMs
        let compressed = Snappy.CompressToArray(ReadOnlySpan proto)
        let resp = postBinary http "/api/v1/write" "application/x-protobuf" compressed
        resp.StatusCode |> should equal HttpStatusCode.OK
        let query = Uri.EscapeDataString(name)
        let json  = getString http (sprintf "/api/prom/api/v1/query?query=%s" query)
        let doc   = JsonDocument.Parse json
        let status = doc.RootElement.GetProperty("status").GetString()
        status |> should equal "success"
        let result = doc.RootElement.GetProperty("data").GetProperty("result")
        result.GetArrayLength() |> should be (greaterThan 0)

    [<Fact>]
    member _.``#4 Prom remote_write accepted header reflects sample count`` () =
        use env  = create ()
        use http = env.Http
        let name  = sprintf "rw_cnt_%s" (Guid.NewGuid().ToString("N").[..5])
        let tsMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let proto = Proto.buildPromWriteRequest name 1.0 tsMs
        let compressed = Snappy.CompressToArray(ReadOnlySpan proto)
        let resp = postBinary http "/api/v1/write" "application/x-protobuf" compressed
        resp.StatusCode |> should equal HttpStatusCode.OK
        // PromRemoteWrite returns acceptedSamples in the JSON body
        let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        let doc  = JsonDocument.Parse body
        let accepted = doc.RootElement.GetProperty("acceptedSamples").GetInt32()
        accepted |> should be (greaterThan 0)

    [<Fact>]
    member _.``#4 Prom remote_write also accepts Cortex /api/prom/push alias`` () =
        use env  = create ()
        use http = env.Http
        let name  = sprintf "rw_alias_%s" (Guid.NewGuid().ToString("N").[..5])
        let tsMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let proto = Proto.buildPromWriteRequest name 3.14 tsMs
        let compressed = Snappy.CompressToArray(ReadOnlySpan proto)
        let resp = postBinary http "/api/prom/push" "application/x-protobuf" compressed
        resp.StatusCode |> should equal HttpStatusCode.OK
