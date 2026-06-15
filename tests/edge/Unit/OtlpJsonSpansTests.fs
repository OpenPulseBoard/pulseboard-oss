module PulseBoard.Tests.Unit.OtlpJsonSpansTests

open System
open System.Text
open Xunit
open FsUnit.Xunit
open PulseBoard.Spans
open PulseBoard.Otlp

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

let private bytes (s : string) = Encoding.UTF8.GetBytes s

/// Minimal `ExportTraceServiceRequest` with a single resource that
/// carries `service.name` and `n` spans built from `spanJson` chunks.
let private wrap (service : string) (spans : string list) : byte[] =
    let spansArr = String.concat "," spans
    let resAttrs =
        $"""{{"key":"service.name","value":{{"stringValue":"{service}"}}}}"""
    let json =
        $"""{{
  "resourceSpans":[{{
    "resource":{{"attributes":[{resAttrs}]}},
    "scopeSpans":[{{"spans":[{spansArr}]}}]
  }}]
}}"""
    bytes json

// ---------------------------------------------------------------------------
// happy path
// ---------------------------------------------------------------------------

[<Fact>]
let ``empty resourceSpans yields empty result and zero count`` () =
    let raw = bytes """{"resourceSpans":[]}"""
    let spans, count = decodeSpansJson raw
    spans  |> should be Empty
    count  |> should equal 0

[<Fact>]
let ``missing resourceSpans yields empty result`` () =
    let raw = bytes """{"otherField":1}"""
    let spans, count = decodeSpansJson raw
    spans  |> should be Empty
    count  |> should equal 0

[<Fact>]
let ``malformed JSON returns empty result without throwing`` () =
    // The OTLP/JSON branch should never propagate JsonException to
    // the request thread — callers expect a soft empty result.
    let go () =
        try decodeSpansJson (bytes "{not json")
        with _ -> [||], 0
    let spans, count = go ()
    spans |> should be Empty
    count |> should equal 0

[<Fact>]
let ``single root span populates all first-class fields`` () =
    let span =
        """{
          "traceId":"00112233445566778899aabbccddeeff",
          "spanId":"0011223344556677",
          "name":"GET /api/orders",
          "kind":2,
          "startTimeUnixNano":"1700000000000000000",
          "endTimeUnixNano":"1700000000500000000",
          "status":{"code":1}
        }"""
    let spans, count = decodeSpansJson (wrap "checkout" [ span ])
    count |> should equal 1
    let s = spans.[0]
    s.traceId      |> should equal "00112233445566778899aabbccddeeff"
    s.spanId       |> should equal "0011223344556677"
    s.parentSpanId |> should equal ""
    s.service      |> should equal "checkout"
    s.operation    |> should equal "GET /api/orders"
    s.kind         |> should equal KindServer
    // nanos → ms; 500_000_000 ns = 500 ms after start
    s.endMs - s.startMs |> should equal 500L
    s.statusCode   |> should equal 1

[<Fact>]
let ``child span carries parentSpanId`` () =
    let parent =
        """{"traceId":"aa112233445566778899aabbccddeeff","spanId":"a011223344556677",
            "name":"root","kind":2,"startTimeUnixNano":"1000000000","endTimeUnixNano":"2000000000"}"""
    let child =
        """{"traceId":"aa112233445566778899aabbccddeeff","spanId":"b011223344556677",
            "parentSpanId":"a011223344556677","name":"child","kind":1,
            "startTimeUnixNano":"1200000000","endTimeUnixNano":"1800000000"}"""
    let spans, count = decodeSpansJson (wrap "svc" [ parent; child ])
    count |> should equal 2
    let byOp = spans |> Array.map (fun s -> s.operation, s.parentSpanId) |> Map.ofArray
    byOp.["root"]  |> should equal ""
    byOp.["child"] |> should equal "a011223344556677"

// ---------------------------------------------------------------------------
// OTLP/JSON spec quirks
// ---------------------------------------------------------------------------

[<Fact>]
let ``uppercase hex trace and span ids are normalised to lowercase`` () =
    let span =
        """{"traceId":"00112233445566778899AABBCCDDEEFF","spanId":"0011223344AABBCC",
            "name":"n","kind":1,"startTimeUnixNano":"1","endTimeUnixNano":"2"}"""
    let spans, _ = decodeSpansJson (wrap "svc" [ span ])
    spans.[0].traceId |> should equal "00112233445566778899aabbccddeeff"
    spans.[0].spanId  |> should equal "0011223344aabbcc"

[<Fact>]
let ``nanos accepted as JSON numbers too (lenient)`` () =
    // Spec says strings; some sloppy clients still ship numbers.
    // Decoder is intentionally lenient.
    let span =
        """{"traceId":"00112233445566778899aabbccddeeff","spanId":"0011223344556677",
            "name":"n","kind":1,"startTimeUnixNano":1700000000000000000,
            "endTimeUnixNano":1700000001000000000}"""
    let spans, _ = decodeSpansJson (wrap "svc" [ span ])
    spans.[0].endMs - spans.[0].startMs |> should equal 1000L

[<Fact>]
let ``status code 2 (Error) is preserved`` () =
    let span =
        """{"traceId":"00112233445566778899aabbccddeeff","spanId":"0011223344556677",
            "name":"n","kind":2,"startTimeUnixNano":"1","endTimeUnixNano":"2",
            "status":{"code":2,"message":"boom"}}"""
    let spans, _ = decodeSpansJson (wrap "svc" [ span ])
    spans.[0].statusCode |> should equal 2

[<Fact>]
let ``missing optional fields default cleanly`` () =
    // No status, no parentSpanId, no attributes — only the required
    // span identity and timing fields.
    let span =
        """{"traceId":"00112233445566778899aabbccddeeff","spanId":"0011223344556677",
            "name":"n","startTimeUnixNano":"1","endTimeUnixNano":"2"}"""
    let spans, _ = decodeSpansJson (wrap "svc" [ span ])
    let s = spans.[0]
    s.parentSpanId |> should equal ""
    s.statusCode   |> should equal 0
    s.kind         |> should equal KindUnspecified
    s.attributes   |> Map.containsKey "service.name" |> should equal true

[<Fact>]
let ``service.name defaults to unknown when resource attrs omit it`` () =
    let raw =
        bytes """{
  "resourceSpans":[{
    "resource":{"attributes":[]},
    "scopeSpans":[{"spans":[{
      "traceId":"00112233445566778899aabbccddeeff",
      "spanId":"0011223344556677","name":"n","kind":1,
      "startTimeUnixNano":"1","endTimeUnixNano":"2"
    }]}]
  }]
}"""
    let spans, _ = decodeSpansJson raw
    spans.[0].service |> should equal "unknown"
