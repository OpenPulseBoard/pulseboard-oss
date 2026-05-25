module PulseBoard.LokiPush

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Successful
open Suave.RequestErrors
open Google.Protobuf
open Snappier
open PulseBoard.TimeSeries
open PulseBoard.Tenancy
open PulseBoard.Quotas
open PulseBoard.Audit
open PulseBoard.Gateway
open PulseBoard.Ingest

// Grafana Loki push receiver. Loki agents (Promtail, Grafana Agent /
// Alloy, Vector's loki sink, fluent-bit's loki output) POST to
// /loki/api/v1/push in one of two encodings:
//
//   * application/json — plain JSON:
//       { "streams": [
//           { "stream": { "label": "v", ... },
//             "values": [ ["<unix_nano_string>", "<line>"], ... ] } ] }
//
//   * application/x-protobuf (default for Promtail / Alloy) — snappy-
//     compressed `logproto.PushRequest`:
//       PushRequest     { repeated StreamAdapter streams = 1; }
//       StreamAdapter   { string labels = 1;                   // "{job=\"foo\"}"
//                         repeated EntryAdapter entries = 2;
//                         uint64 hash = 3;                     // skipped }
//       EntryAdapter    { Timestamp timestamp = 1;
//                         string line = 2;
//                         /* structured metadata = 3, parsed = 4: skipped */ }
//       Timestamp       { int64 seconds = 1; int32 nanos = 2; }
//
// The protobuf labels string uses the same `{k="v",...}` syntax we already
// canonicalise for Prom / OTLP series names, so we parse it back into a
// label map and project `service_name`/`level` onto our LogEntry.

let private fieldOf (tag : uint32) = int (tag >>> 3)

[<Struct>] type private Entry = { tsMs : int64; line : string }
type private Stream = { labels : string; entries : Entry[] }

// ---------- Protobuf decode ----------

let private decodeTimestamp (bytes : ByteString) : int64 =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable secs  = 0L
  let mutable nanos = 0
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> secs  <- input.ReadInt64()
    | 2 -> nanos <- input.ReadInt32()
    | _ -> input.SkipLastField()
  secs * 1000L + int64 (nanos / 1_000_000)

let private decodeEntry (bytes : ByteString) : Entry =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable tsMs = 0L
  let mutable line = ""
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> tsMs <- decodeTimestamp (input.ReadBytes())
    | 2 -> line <- input.ReadString()
    | _ -> input.SkipLastField()
  { tsMs = tsMs; line = line }

let private decodeStream (bytes : ByteString) : Stream =
  let input = new CodedInputStream(bytes.ToByteArray())
  let mutable labels = ""
  let entries = ResizeArray<Entry>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> labels <- input.ReadString()
    | 2 -> entries.Add(decodeEntry (input.ReadBytes()))
    | _ -> input.SkipLastField()
  { labels = labels; entries = entries.ToArray() }

let private decodePushRequest (input : CodedInputStream) : Stream[] =
  let streams = ResizeArray<Stream>()
  while not input.IsAtEnd do
    let tag = input.ReadTag()
    match fieldOf tag with
    | 1 -> streams.Add(decodeStream (input.ReadBytes()))
    | _ -> input.SkipLastField()
  streams.ToArray()

// ---------- Label-string parser ----------
// Reverse of the canonical `name="value"` serialisation used by Prom /
// OTLP. Accepts both `{k="v",k2="v2"}` and a bare `k="v"` form. Unknown
// escapes (`\X`) are passed through as `X` for forwards-compat.

let private parseLabelString (s : string) : Map<string, string> =
  let mutable i = 0
  let n = s.Length
  let skipWs () =
    while i < n && (s.[i] = ' ' || s.[i] = '\t') do i <- i + 1
  let result = ResizeArray<string * string>()
  skipWs ()
  if i < n && s.[i] = '{' then i <- i + 1
  let mutable failed = false
  while not failed && i < n do
    skipWs ()
    if i < n && (s.[i] = '}' || s.[i] = ',') then
      i <- i + 1
    else
      let keyStart = i
      while i < n && s.[i] <> '=' && s.[i] <> '}' do i <- i + 1
      if i >= n || s.[i] <> '=' then failed <- true
      else
        let key = s.Substring(keyStart, i - keyStart).Trim()
        i <- i + 1 // skip '='
        skipWs ()
        if i >= n || s.[i] <> '"' then failed <- true
        else
          i <- i + 1 // opening quote
          let sb = StringBuilder()
          let mutable closed = false
          while not closed && i < n do
            let c = s.[i]
            if c = '"' then closed <- true; i <- i + 1
            elif c = '\\' && i + 1 < n then
              let esc = s.[i + 1]
              match esc with
              | '\\' -> sb.Append '\\' |> ignore
              | '"'  -> sb.Append '"'  |> ignore
              | 'n'  -> sb.Append '\n' |> ignore
              | 't'  -> sb.Append '\t' |> ignore
              | other -> sb.Append other |> ignore
              i <- i + 2
            else
              sb.Append c |> ignore
              i <- i + 1
          if not closed then failed <- true
          else
            if key.Length > 0 then
              result.Add (key, sb.ToString())
  result |> Map.ofSeq

// ---------- Audit / publish helpers ----------

let private auditDeny (q : IngestQuotas) (ctx : HttpContext)
                      (action : string) (details : string) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = Deny
      remoteIp = None
      details  = Some details }
  try q.auditLog.Append ev with _ -> ()

let private serviceFromLabels (labels : Map<string, string>) : string =
  match Map.tryFind "service_name" labels with
  | Some v when v.Length > 0 -> v
  | _ ->
    match Map.tryFind "service" labels with
    | Some v when v.Length > 0 -> v
    | _ ->
      match Map.tryFind "job" labels with
      | Some v when v.Length > 0 -> v
      | _ ->
        match Map.tryFind "app" labels with
        | Some v when v.Length > 0 -> v
        | _ -> "unknown"

let private levelFromLabels (labels : Map<string, string>) : string =
  match Map.tryFind "level" labels with
  | Some v when v.Length > 0 -> v.ToLowerInvariant()
  | _ ->
    match Map.tryFind "severity" labels with
    | Some v when v.Length > 0 -> v.ToLowerInvariant()
    | _ -> "info"

// ---------- JSON variant ----------

let private parseUnixNano (s : string) : int64 =
  match Int64.TryParse s with
  | true, v -> v / 1_000_000L
  | _ -> DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private streamsFromJson (raw : byte[]) : Stream[] =
  use doc = JsonDocument.Parse(ReadOnlyMemory(raw))
  let root = doc.RootElement
  let mutable streamsEl = Unchecked.defaultof<JsonElement>
  if root.TryGetProperty("streams", &streamsEl)
     && streamsEl.ValueKind = JsonValueKind.Array
  then
    [|
      for s in streamsEl.EnumerateArray() do
        let mutable streamObj = Unchecked.defaultof<JsonElement>
        let labels =
          if s.TryGetProperty("stream", &streamObj)
             && streamObj.ValueKind = JsonValueKind.Object then
            let sb = StringBuilder("{")
            let mutable first = true
            for prop in streamObj.EnumerateObject() do
              if not first then sb.Append ',' |> ignore
              first <- false
              sb.Append prop.Name |> ignore
              sb.Append "=\"" |> ignore
              let v = prop.Value.GetString()
              if not (isNull v) then
                for c in v do
                  match c with
                  | '\\' -> sb.Append "\\\\" |> ignore
                  | '"'  -> sb.Append "\\\"" |> ignore
                  | '\n' -> sb.Append "\\n"  |> ignore
                  | _    -> sb.Append c       |> ignore
              sb.Append '"' |> ignore
            sb.Append '}' |> ignore
            sb.ToString()
          else "{}"
        let mutable valuesEl = Unchecked.defaultof<JsonElement>
        let entries =
          if s.TryGetProperty("values", &valuesEl)
             && valuesEl.ValueKind = JsonValueKind.Array then
            [|
              for v in valuesEl.EnumerateArray() do
                if v.ValueKind = JsonValueKind.Array
                   && v.GetArrayLength() >= 2 then
                  let ts   = v.[0].GetString()
                  let line = v.[1].GetString()
                  yield { tsMs = parseUnixNano (if isNull ts then "" else ts)
                          line = if isNull line then "" else line } |]
          else [||]
        yield { labels = labels; entries = entries } |]
  else [||]

// ---------- Handler ----------

/// POST /loki/api/v1/push — accepts both JSON and snappy-protobuf
/// payloads. Body length is charged against the tenant's LogBytes
/// bucket before parsing; over-quota → 429. Loki convention is to
/// respond 204 on success — that's what most agents check.
let handler (storage : IStorageClient)
            (quotas : IngestQuotas option) : WebPart =
  fun ctx -> async {
    PulseBoard.HeartbeatClient.bump ()
    try
      let raw = ctx.request.rawForm
      if isNull raw || raw.Length = 0 then
        return! BAD_REQUEST """{"error":"empty body"}""" ctx
      else
      let tenantId =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun t -> t.tenant.id)
      let throttle =
        match quotas, tenantId with
        | Some q, Some tid ->
          match q.limiter.TryAcquire(tid, LogBytes, float raw.Length) with
          | AcquireResult.Ok -> None
          | AcquireResult.Throttled ms ->
            auditDeny q ctx "quota.logBytes"
              (sprintf "bytes=%d retryAfterMs=%d" raw.Length ms)
            Some ms
        | _ -> None
      match throttle with
      | Some ms ->
        let retrySec = max 1 (int (ceil (float ms / 1000.0)))
        let body =
          sprintf
            """{"error":"rate limit exceeded","kind":"logBytes","retryAfterMs":%d}""" ms
        return!
          (TOO_MANY_REQUESTS body
           >=> Writers.setMimeType "application/json"
           >=> Writers.setHeader "Retry-After" (string retrySec)) ctx
      | None ->
        let contentType =
          ctx.request.headers
          |> Seq.tryFind (fun (k, _) -> String.Equals(k, "content-type", StringComparison.OrdinalIgnoreCase))
          |> Option.map (fun (_, v) -> v.ToLowerInvariant())
          |> Option.defaultValue ""
        let isJson = contentType.Contains "application/json"
        let streams =
          if isJson then
            streamsFromJson raw
          else
            // Loki agents typically snappy-compress the protobuf body.
            // Some setups (esp. relay sidecars) post raw protobuf — fall
            // back to raw bytes if snappy decode fails.
            let decompressed =
              try Snappy.DecompressToArray(ReadOnlySpan(raw))
              with _ -> raw
            use ms = new MemoryStream(decompressed)
            let input = new CodedInputStream(ms)
            decodePushRequest input
        let entries = ResizeArray<LogEntry>()
        for s in streams do
          let labelMap = parseLabelString s.labels
          let service = serviceFromLabels labelMap
          let level   = levelFromLabels   labelMap
          for e in s.entries do
            let ts = if e.tsMs > 0L then e.tsMs else DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            entries.Add { ts = ts; service = service; level = level; message = e.line }
        let tid =
          PulseBoard.Rbac.tryGetTenant ctx
          |> Option.map (fun t -> let (TenantId s) = t.tenant.id in s)
          |> Option.defaultValue ""
        do! storage.WriteLogs(tid, entries)
        // Loki convention: 204 No Content on success.
        return!
          (NO_CONTENT
           >=> Writers.setHeader "X-PulseBoard-Accepted" (string entries.Count)) ctx
    with ex ->
      return!
        BAD_REQUEST
          (sprintf """{"error":%s}"""
             (JsonSerializer.Serialize ex.Message)) ctx
  }
