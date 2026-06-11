module PulseBoard.Otel

open System
open System.Diagnostics
open Suave
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Resources
open OpenTelemetry.Trace

// Manual OpenTelemetry wiring for the PulseBoard edge / workspace
// runtime. Exposes ActivitySource "pulseboard.edge" plus init,
// withTracing, inSpan/inSpanAsync, tagCurrent and registerShutdown
// helpers so request handlers can emit server + internal spans without
// taking a direct dependency on the OpenTelemetry SDK. The sidecar
// pulseagent baked into the image owns auth + retry + buffering
// upstream, so we only configure a localhost HTTP/protobuf exporter
// pointing at 127.0.0.1:4318.

[<Literal>]
let SourceName = "pulseboard.edge"

let source : ActivitySource = new ActivitySource(SourceName)

let private mutableProvider : TracerProvider option ref = ref None

let private envFlag (name : string) =
  match Environment.GetEnvironmentVariable name with
  | null | "" -> false
  | s ->
    match s.Trim().ToLowerInvariant() with
    | "1" | "true" | "yes" | "on" -> true
    | _ -> false

let init (defaultServiceName : string) : unit =
  if envFlag "OTEL_SDK_DISABLED" then
    printfn "  Otel:        disabled (OTEL_SDK_DISABLED=true)"
  elif mutableProvider.Value.IsSome then
    ()
  else
    let serviceName =
      match Environment.GetEnvironmentVariable "OTEL_SERVICE_NAME" with
      | null | "" -> defaultServiceName
      | s -> s
    let endpoint =
      match Environment.GetEnvironmentVariable "OTEL_EXPORTER_OTLP_ENDPOINT" with
      | null | "" -> "http://127.0.0.1:4318"
      | s -> s
    let provider =
      Sdk.CreateTracerProviderBuilder()
        .AddSource(SourceName)
        .AddHttpClientInstrumentation()
        .SetResourceBuilder(
          ResourceBuilder.CreateDefault()
            .AddService(serviceName = serviceName)
            .AddEnvironmentVariableDetector())
        .AddOtlpExporter(fun opts ->
          opts.Endpoint <- Uri endpoint
          opts.Protocol <- OtlpExportProtocol.HttpProtobuf)
        .Build()
    mutableProvider.Value <- Some provider
    printfn "  Otel:        exporting OTLP to %s (service.name=%s)" endpoint serviceName

let withTracing (inner : WebPart) : WebPart =
  fun ctx -> async {
    let req = ctx.request
    let methodName =
      match req.``method`` with
      | HttpMethod.GET -> "GET" | HttpMethod.POST -> "POST"
      | HttpMethod.PUT -> "PUT" | HttpMethod.DELETE -> "DELETE"
      | HttpMethod.PATCH -> "PATCH" | HttpMethod.OPTIONS -> "OPTIONS"
      | HttpMethod.HEAD -> "HEAD" | HttpMethod.TRACE -> "TRACE"
      | HttpMethod.CONNECT -> "CONNECT" | _ -> "OTHER"
    let target = req.url.PathAndQuery
    let spanName = sprintf "HTTP %s %s" methodName req.url.AbsolutePath
    use activity = source.StartActivity(spanName, ActivityKind.Server)
    if not (isNull activity) then
      activity.SetTag("http.request.method", methodName) |> ignore
      activity.SetTag("url.path", req.url.AbsolutePath) |> ignore
      activity.SetTag("url.scheme", req.url.Scheme) |> ignore
      activity.SetTag("server.address", req.url.Host) |> ignore
      activity.SetTag("url.full", target) |> ignore
    try
      let! result = inner ctx
      if not (isNull activity) then
        match result with
        | Some ctx' ->
          let code = ctx'.response.status.code
          activity.SetTag("http.response.status_code", code) |> ignore
          if code >= 500 then
            activity.SetStatus(ActivityStatusCode.Error, sprintf "HTTP %d" code) |> ignore
        | None ->
          activity.SetTag("http.response.status_code", 404) |> ignore
      return result
    with ex ->
      if not (isNull activity) then
        activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
        activity.AddTag("exception.type", ex.GetType().FullName) |> ignore
        activity.AddTag("exception.message", ex.Message) |> ignore
      return raise ex
  }

let inSpanAsync (name : string) (work : Activity option -> Async<'T>) : Async<'T> = async {
  use activity = source.StartActivity(name, ActivityKind.Internal)
  let act = if isNull activity then None else Some activity
  try
    return! work act
  with ex ->
    match act with
    | Some a ->
      a.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
      a.AddTag("exception.type", ex.GetType().FullName) |> ignore
      a.AddTag("exception.message", ex.Message) |> ignore
    | None -> ()
    return raise ex
}

let inSpan (name : string) (work : Activity option -> 'T) : 'T =
  use activity = source.StartActivity(name, ActivityKind.Internal)
  let act = if isNull activity then None else Some activity
  try
    work act
  with ex ->
    match act with
    | Some a ->
      a.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
      a.AddTag("exception.type", ex.GetType().FullName) |> ignore
      a.AddTag("exception.message", ex.Message) |> ignore
    | None -> ()
    reraise ()

let tagCurrent (key : string) (value : obj) : unit =
  let cur = Activity.Current
  if not (isNull cur) then cur.SetTag(key, value) |> ignore

let registerShutdown () : unit =
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    match mutableProvider.Value with
    | Some p ->
      try p.Dispose() with _ -> ()
      mutableProvider.Value <- None
    | None -> ())
