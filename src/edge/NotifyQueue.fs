module PulseBoard.NotifyQueue

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Collections.Generic
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries

// Persistent outbound notification queue (PLAN.md Phase 5 step 5).
// Replaces `Notify.postJson`'s fire-and-forget posture with a durable
// enqueue → lease → ack/fail/dead pipeline. The queue itself is
// transport-agnostic: each message carries `receiverType` + `url` + body,
// and the worker picks a per-type dispatcher.
//
// Two backends ship by default:
//   * `FileNotifyQueue` — append-only NDJSON journal under
//     `<dataDir>/notify/queue.ndjson` plus a sibling `dlq.ndjson` for
//     dead letters. State is rebuilt at startup by replaying the journal.
//     Compaction rewrites the journal whenever live entries fall below
//     half of total lines.
//   * (Postgres-backed implementation deferred — the interface is wired
//     so it can be slotted in without touching the call sites.)
//
// Retry strategy is exponential with jitter, capped: backoff_ms =
// base * 2^attempt + rand(0, base/2), capped at `maxBackoffMs`. After
// `maxAttempts` failures the message moves to DLQ.

// -- model ------------------------------------------------------------------

[<NoComparison>]
type OutboundMessage =
  { id           : string
    tenantId     : TenantId
    receiverId   : string
    receiverType : string             // "slack" | "webhook" | "hmac_webhook" | ...
    url          : string             // target URL (or "" for stubs)
    secret       : string option      // HMAC secret / integration key
    body         : string             // JSON envelope
    headers      : Map<string,string>
    attempt      : int
    maxAttempts  : int
    enqueuedAt   : int64
    nextRunAt    : int64
    lastError    : string option }

[<NoComparison>]
type DeadLetter =
  { msg     : OutboundMessage
    deadAt  : int64
    reason  : string }

type INotifyQueue =
  abstract Enqueue       : OutboundMessage -> unit
  abstract Lease         : batchSize:int * nowMs:int64 -> OutboundMessage[]
  abstract Ack           : id:string -> unit
  abstract Fail          : id:string * err:string * nextRunAt:int64 -> unit
  abstract Dead          : id:string * reason:string -> unit
  abstract Pending       : tenantId:TenantId option -> OutboundMessage[]
  abstract DeadLetters   : tenantId:TenantId option -> DeadLetter[]
  abstract ReplayDead    : id:string -> bool
  abstract PurgeDead     : id:string -> bool

// -- JSON codec -------------------------------------------------------------

let private writeMsg (w : Utf8JsonWriter) (m : OutboundMessage) =
  let (TenantId tid) = m.tenantId
  w.WriteStartObject()
  w.WriteString("id", m.id)
  w.WriteString("tenantId", tid)
  w.WriteString("receiverId", m.receiverId)
  w.WriteString("receiverType", m.receiverType)
  w.WriteString("url", m.url)
  match m.secret with
  | Some s -> w.WriteString("secret", s)
  | None   -> ()
  w.WriteString("body", m.body)
  w.WritePropertyName "headers"
  w.WriteStartObject()
  for KeyValue(k, v) in m.headers do w.WriteString(k, v)
  w.WriteEndObject()
  w.WriteNumber("attempt",     m.attempt)
  w.WriteNumber("maxAttempts", m.maxAttempts)
  w.WriteNumber("enqueuedAt",  m.enqueuedAt)
  w.WriteNumber("nextRunAt",   m.nextRunAt)
  match m.lastError with
  | Some e -> w.WriteString("lastError", e)
  | None   -> ()
  w.WriteEndObject()

let serialiseMsg (m : OutboundMessage) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    writeMsg w m)
  Encoding.UTF8.GetString(ms.ToArray())

let private readStr (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if isNull s then None else Some s
  | _ -> None

let private readNum (el : JsonElement) (name : string) (dflt : int64) : int64 =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L
    if v.TryGetInt64 &n then n else dflt
  | _ -> dflt

let private readHeaders (el : JsonElement) : Map<string,string> =
  match el.TryGetProperty "headers" with
  | true, v when v.ValueKind = JsonValueKind.Object ->
    v.EnumerateObject()
    |> Seq.choose (fun p ->
      if p.Value.ValueKind = JsonValueKind.String
      then Some (p.Name, p.Value.GetString())
      else None)
    |> Map.ofSeq
  | _ -> Map.empty

let parseMsg (line : string) : OutboundMessage option =
  try
    use doc = JsonDocument.Parse line
    let r = doc.RootElement
    match readStr r "id", readStr r "tenantId", readStr r "receiverId",
          readStr r "receiverType", readStr r "url", readStr r "body" with
    | Some id, Some tid, Some rid, Some rt, Some url, Some body ->
      Some {
        id = id
        tenantId = TenantId tid
        receiverId = rid
        receiverType = rt
        url = url
        secret = readStr r "secret"
        body = body
        headers = readHeaders r
        attempt = int (readNum r "attempt" 0L)
        maxAttempts = int (readNum r "maxAttempts" 5L)
        enqueuedAt = readNum r "enqueuedAt" 0L
        nextRunAt = readNum r "nextRunAt" 0L
        lastError = readStr r "lastError" }
    | _ -> None
  with _ -> None

// -- file-backed queue ------------------------------------------------------

/// Append-only NDJSON queue. Format per line is one of:
///   {"op":"enq",  "msg": <OutboundMessage>}
///   {"op":"ack",  "id": "..."}
///   {"op":"fail", "id": "...", "msg": <updated OutboundMessage>}
///   {"op":"dead", "id": "...", "deadAt": ts, "reason": "...", "msg": <OutboundMessage>}
///
/// Live messages live in-memory keyed by id; on startup we replay the
/// journal to rebuild the in-memory set and the DLQ. The journal is
/// compacted (rewritten with only live + DLQ entries) when more than
/// half the journal lines are tombstones for already-removed ids.
type FileNotifyQueue(root : string) =
  do Directory.CreateDirectory root |> ignore
  let journalPath = Path.Combine(root, "queue.ndjson")
  let dlqPath     = Path.Combine(root, "dlq.ndjson")

  let live = Dictionary<string, OutboundMessage>()
  let dead = Dictionary<string, DeadLetter>()
  let leased = HashSet<string>()
  let sync = obj()
  let mutable journalLines  = 0
  let mutable tombstoneCount = 0

  let appendLine (path : string) (s : string) =
    use fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    use sw = new StreamWriter(fs)
    sw.WriteLine s
    sw.Flush()

  let writeMsgRecord (op : string) (m : OutboundMessage) : string =
    use ms = new MemoryStream()
    (
      use w = new Utf8JsonWriter(ms)
      w.WriteStartObject()
      w.WriteString("op", op)
      w.WritePropertyName "msg"
      writeMsg w m
      w.WriteEndObject())
    Encoding.UTF8.GetString(ms.ToArray())

  let writeAck (id : string) : string =
    sprintf """{"op":"ack","id":%s}""" (JsonSerializer.Serialize id)

  let writeDeadRecord (m : OutboundMessage) (deadAt : int64) (reason : string) : string =
    use ms = new MemoryStream()
    (
      use w = new Utf8JsonWriter(ms)
      w.WriteStartObject()
      w.WriteString("op", "dead")
      w.WriteString("id", m.id)
      w.WriteNumber("deadAt", deadAt)
      w.WriteString("reason", reason)
      w.WritePropertyName "msg"
      writeMsg w m
      w.WriteEndObject())
    Encoding.UTF8.GetString(ms.ToArray())

  let parseDeadLetter (line : string) : DeadLetter option =
    try
      use doc = JsonDocument.Parse line
      let r = doc.RootElement
      let deadAt = readNum r "deadAt" 0L
      let reason = readStr r "reason" |> Option.defaultValue ""
      match r.TryGetProperty "msg" with
      | true, mEl ->
        // serialise msg sub-object then re-parse
        let raw = mEl.GetRawText()
        match parseMsg raw with
        | Some m -> Some { msg = m; deadAt = deadAt; reason = reason }
        | None -> None
      | _ -> None
    with _ -> None

  let replay () =
    if File.Exists journalPath then
      for line in File.ReadLines journalPath do
        journalLines <- journalLines + 1
        try
          use doc = JsonDocument.Parse line
          let r = doc.RootElement
          let op = readStr r "op" |> Option.defaultValue ""
          match op with
          | "enq" | "fail" ->
            match r.TryGetProperty "msg" with
            | true, m ->
              match parseMsg (m.GetRawText()) with
              | Some msg -> live.[msg.id] <- msg
              | None -> ()
            | _ -> ()
          | "ack" ->
            match readStr r "id" with
            | Some id ->
              if live.Remove id then tombstoneCount <- tombstoneCount + 1
            | None -> ()
          | "dead" ->
            match readStr r "id" with
            | Some id ->
              if live.Remove id then tombstoneCount <- tombstoneCount + 1
            | None -> ()
          | _ -> ()
        with _ -> ()
    if File.Exists dlqPath then
      for line in File.ReadLines dlqPath do
        match parseDeadLetter line with
        | Some dl -> dead.[dl.msg.id] <- dl
        | None -> ()

  do replay ()

  /// Rewrite the journal as a single `enq` per live message, then truncate
  /// to drop accumulated tombstones. Called whenever tombstones exceed
  /// half of total journal lines.
  let maybeCompact () =
    if journalLines > 256 && tombstoneCount * 2 > journalLines then
      let tmp = journalPath + ".tmp"
      use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)
      use sw = new StreamWriter(fs)
      let mutable n = 0
      for kv in live do
        sw.WriteLine (writeMsgRecord "enq" kv.Value)
        n <- n + 1
      sw.Flush()
      fs.Flush(true)
      sw.Dispose()
      fs.Dispose()
      if File.Exists journalPath then File.Delete journalPath
      File.Move(tmp, journalPath)
      journalLines  <- n
      tombstoneCount <- 0

  interface INotifyQueue with

    member _.Enqueue m =
      lock sync (fun () ->
        live.[m.id] <- m
        appendLine journalPath (writeMsgRecord "enq" m)
        journalLines <- journalLines + 1)

    member _.Lease(batchSize, nowMs) =
      lock sync (fun () ->
        let out = ResizeArray()
        let mutable i = 0
        let iter = live.Values |> Seq.toArray
        for m in iter do
          if i < batchSize && m.nextRunAt <= nowMs && not (leased.Contains m.id) then
            leased.Add m.id |> ignore
            out.Add m
            i <- i + 1
        out.ToArray())

    member _.Ack id =
      lock sync (fun () ->
        if live.Remove id then
          tombstoneCount <- tombstoneCount + 1
          appendLine journalPath (writeAck id)
          journalLines <- journalLines + 1
          leased.Remove id |> ignore
          maybeCompact ())

    member _.Fail(id, err, nextRunAt) =
      lock sync (fun () ->
        match live.TryGetValue id with
        | true, m ->
          let updated =
            { m with attempt = m.attempt + 1
                     lastError = Some err
                     nextRunAt = nextRunAt }
          live.[id] <- updated
          appendLine journalPath (writeMsgRecord "fail" updated)
          journalLines <- journalLines + 1
          leased.Remove id |> ignore
        | _ -> ())

    member _.Dead(id, reason) =
      lock sync (fun () ->
        match live.TryGetValue id with
        | true, m ->
          let dl = { msg = m; deadAt = nowMs (); reason = reason }
          dead.[id] <- dl
          live.Remove id |> ignore
          tombstoneCount <- tombstoneCount + 1
          appendLine journalPath (writeDeadRecord m dl.deadAt reason)
          appendLine dlqPath (writeDeadRecord m dl.deadAt reason)
          journalLines <- journalLines + 1
          leased.Remove id |> ignore
          maybeCompact ()
        | _ -> ())

    member _.Pending(tid) =
      lock sync (fun () ->
        live.Values
        |> Seq.filter (fun m ->
          match tid with Some t -> m.tenantId = t | None -> true)
        |> Seq.sortBy (fun m -> m.nextRunAt)
        |> Seq.toArray)

    member _.DeadLetters(tid) =
      lock sync (fun () ->
        dead.Values
        |> Seq.filter (fun d ->
          match tid with Some t -> d.msg.tenantId = t | None -> true)
        |> Seq.sortByDescending (fun d -> d.deadAt)
        |> Seq.toArray)

    member q.ReplayDead id =
      lock sync (fun () ->
        match dead.TryGetValue id with
        | true, dl ->
          dead.Remove id |> ignore
          let revived =
            { dl.msg with
                attempt = 0
                lastError = None
                nextRunAt = nowMs () }
          (q :> INotifyQueue).Enqueue revived
          // Tombstone the DLQ line by rewriting on next compaction;
          // simplest is to rewrite the DLQ now.
          let tmp = dlqPath + ".tmp"
          use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)
          use sw = new StreamWriter(fs)
          for kv in dead do
            sw.WriteLine (writeDeadRecord kv.Value.msg kv.Value.deadAt kv.Value.reason)
          sw.Flush(); fs.Flush(true); sw.Dispose(); fs.Dispose()
          if File.Exists dlqPath then File.Delete dlqPath
          File.Move(tmp, dlqPath)
          true
        | _ -> false)

    member _.PurgeDead id =
      lock sync (fun () ->
        if dead.Remove id then
          let tmp = dlqPath + ".tmp"
          use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)
          use sw = new StreamWriter(fs)
          for kv in dead do
            sw.WriteLine (writeDeadRecord kv.Value.msg kv.Value.deadAt kv.Value.reason)
          sw.Flush(); fs.Flush(true); sw.Dispose(); fs.Dispose()
          if File.Exists dlqPath then File.Delete dlqPath
          File.Move(tmp, dlqPath)
          true
        else false)

// -- worker / dispatcher ----------------------------------------------------

let private http =
  let h = new HttpClient()
  h.Timeout <- TimeSpan.FromSeconds 10.
  h

let private hmacHex (secret : string) (body : string) : string =
  use mac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes secret)
  let bytes = mac.ComputeHash(Encoding.UTF8.GetBytes body)
  let sb = StringBuilder(bytes.Length * 2)
  for b in bytes do sb.AppendFormat("{0:x2}", int b) |> ignore
  sb.ToString()

/// Dispatch one message. Returns `Ok ()` on 2xx, `Error msg` otherwise.
/// Each receiver type formats the HTTP request a bit differently. The
/// envelope body is shaped by the routing pipeline (`Routing.fs`); this
/// layer only owns the transport.
let dispatch (m : OutboundMessage) : Task<Result<unit, string>> =
  task {
    try
      if m.url = "" then
        return Result.Error "no url"
      else
        use req = new HttpRequestMessage(HttpMethod.Post, m.url)
        let mediaType =
          match m.receiverType with
          | "discord" | "slack" | "teams" -> "application/json"
          | _ -> "application/json"
        req.Content <- new StringContent(m.body, Encoding.UTF8, mediaType)
        for KeyValue(k, v) in m.headers do
          try req.Headers.TryAddWithoutValidation(k, v) |> ignore with _ -> ()
        match m.receiverType, m.secret with
        | "hmac_webhook", Some s ->
          let sig_ = hmacHex s m.body
          req.Headers.TryAddWithoutValidation("X-PulseBoard-Signature", "sha256=" + sig_) |> ignore
        | "pagerduty", Some key ->
          // Events API v2 uses `routing_key` in the body; if the caller
          // already baked it into the envelope, leave the body alone. For
          // safety we always attach an `Authorization: Token` header too —
          // PagerDuty Events API ignores it but custom proxies use it.
          req.Headers.TryAddWithoutValidation("Authorization", "Token token=" + key) |> ignore
        | "opsgenie", Some key ->
          req.Headers.TryAddWithoutValidation("Authorization", "GenieKey " + key) |> ignore
        | _ -> ()
        let! resp = http.SendAsync(req)
        if int resp.StatusCode >= 200 && int resp.StatusCode < 300 then
          return Result.Ok ()
        else
          let! txt = resp.Content.ReadAsStringAsync()
          let head = if txt.Length > 200 then txt.Substring(0, 200) else txt
          return Result.Error (sprintf "%d %s" (int resp.StatusCode) head)
    with ex ->
      return Result.Error ex.Message
  }

/// Run the worker loop until `ct` is cancelled. `metricStore` (when
/// provided) gets `pulse_notify_attempts_total` / `pulse_notify_failures_total`
/// counters for self-observability.
let runWorker (queue : INotifyQueue)
              (metricStore : MetricStore option)
              (baseBackoffMs : int64)
              (maxBackoffMs : int64)
              (ct : CancellationToken) : Task =
  task {
    let rnd = Random()
    while not ct.IsCancellationRequested do
      let now = nowMs ()
      let batch = queue.Lease(16, now)
      if batch.Length = 0 then
        do! Task.Delay(500, ct)
      else
        for m in batch do
          let! result = dispatch m
          match metricStore with
          | Some ms ->
            ms.Record("pulse_notify_attempts_total", { ts = now; value = 1.0 })
          | None -> ()
          match result with
          | Result.Ok () ->
            queue.Ack m.id
          | Result.Error err ->
            match metricStore with
            | Some ms ->
              ms.Record("pulse_notify_failures_total", { ts = now; value = 1.0 })
            | None -> ()
            if m.attempt + 1 >= m.maxAttempts then
              queue.Dead(m.id, err)
            else
              let exp = baseBackoffMs * (1L <<< (min 10 (m.attempt + 1)))
              let cap = min exp maxBackoffMs
              let jitter = int64 (rnd.Next(int (cap / 4L + 1L)))
              queue.Fail(m.id, err, now + cap + jitter)
  } :> Task

// -- REST surface (DLQ inspection / replay) ---------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK
    | 204 -> fun _ -> Suave.Successful.NO_CONTENT
    | 400 -> BAD_REQUEST
    | 404 -> NOT_FOUND
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private resolveTenant multiTenant (ctx : HttpContext) =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else Some (TenantId "__local__")

let private serialiseList (msgs : OutboundMessage[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for m in msgs do writeMsg w m
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let private serialiseDlq (xs : DeadLetter[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for d in xs do
      w.WriteStartObject()
      w.WriteNumber("deadAt", d.deadAt)
      w.WriteString("reason", d.reason)
      w.WritePropertyName "msg"
      writeMsg w d.msg
      w.WriteEndObject()
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let webPart (multiTenant : bool) (queue : INotifyQueue) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! jsonResp 401 """{"error":"no tenant"}""" ctx
      | Some tid -> return! handler tid ctx
    }
  choose [
    GET >=> path "/api/notify/queue" >=>
      withTenant (fun tid -> jsonResp 200 (serialiseList (queue.Pending(Some tid))))
    GET >=> path "/api/notify/dlq" >=>
      withTenant (fun tid -> jsonResp 200 (serialiseDlq (queue.DeadLetters(Some tid))))
    POST >=> pathScan "/api/notify/dlq/%s/replay" (fun id ->
      withTenant (fun _ ->
        if queue.ReplayDead id then jsonResp 200 """{"replayed":true}"""
        else jsonResp 404 """{"error":"not found"}"""))
    DELETE >=> pathScan "/api/notify/dlq/%s" (fun id ->
      withTenant (fun _ ->
        if queue.PurgeDead id then jsonResp 204 ""
        else jsonResp 404 """{"error":"not found"}"""))
  ]
