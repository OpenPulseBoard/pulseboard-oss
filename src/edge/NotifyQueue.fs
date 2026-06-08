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

/// Verbose tracing for the queue -> dispatch -> HTTP path. On by default;
/// set PULSE_NOTIFY_DEBUG=0 (or false/off/no) to silence. Mirrors the
/// `[notify]` prefix used by the routing pipeline so both halves of the
/// notification path share one greppable tag.
let internal notifyDebug =
  match Environment.GetEnvironmentVariable "PULSE_NOTIFY_DEBUG" with
  | "0" | "false" | "off" | "no" -> false
  | _ -> true

let internal nqlog (msg : string) =
  if notifyDebug then eprintfn "[notify] %s" msg

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
    extra        : Map<string,string> // receiver-specific config (e.g. from/to addresses)
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
  w.WritePropertyName "extra"
  w.WriteStartObject()
  for KeyValue(k, v) in m.extra do w.WriteString(k, v)
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

let private readExtra (el : JsonElement) : Map<string,string> =
  match el.TryGetProperty "extra" with
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
        extra = readExtra r
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

/// Extract a brief human-readable summary from the routing envelope so
/// receivers that don't accept arbitrary JSON (email/SMS/Jira) can show
/// something legible.
let private renderSummary (envelopeBody : string) : string * string =
  // Returns (subject, plaintextBody).
  try
    use doc = JsonDocument.Parse envelopeBody
    let r = doc.RootElement
    let receiver =
      match r.TryGetProperty "receiver" with
      | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
      | _ -> ""
    let alerts =
      match r.TryGetProperty "alerts" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.toArray
      | _ -> [||]
    if alerts.Length = 0 then ("PulseBoard alert", envelopeBody)
    else
      let a = alerts.[0]
      let getStr (name : string) =
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""
      let ruleName = getStr "ruleName"
      let severity = getStr "severity"
      let state    = getStr "state"
      let firing   = alerts.Length
      let sb = StringBuilder()
      sb.AppendFormat("[{0}] {1} ({2}) — {3} alert(s)\n",
        severity.ToUpperInvariant(), ruleName, state, firing) |> ignore
      sb.AppendFormat("Receiver: {0}\n\n", receiver) |> ignore
      for ai in alerts do
        let n = match ai.TryGetProperty "ruleName" with
                | true, v -> v.GetString() | _ -> "?"
        let vl = match ai.TryGetProperty "value" with
                 | true, v ->
                   let mutable f = 0.0
                   if v.TryGetDouble &f then f else 0.0
                 | _ -> 0.0
        let lblStr =
          match ai.TryGetProperty "labels" with
          | true, v when v.ValueKind = JsonValueKind.Object ->
            v.EnumerateObject()
            |> Seq.map (fun p -> p.Name + "=" + (try p.Value.GetString() with _ -> ""))
            |> String.concat ", "
          | _ -> ""
        sb.AppendFormat("• {0} value={1} [{2}]\n", n, vl, lblStr) |> ignore
      let subject =
        sprintf "[%s] %s (%d alert%s)"
          (severity.ToUpperInvariant()) ruleName firing
          (if firing = 1 then "" else "s")
      subject, sb.ToString()
  with _ ->
    ("PulseBoard alert", envelopeBody)

let private jsonEscape (s : string) : string =
  JsonSerializer.Serialize s

/// Build the HTTP request for a given message: chooses URL, body,
/// content-type, and any receiver-specific headers.
let private shapeRequest (m : OutboundMessage) : HttpRequestMessage =
  let req = new HttpRequestMessage()
  req.Method <- HttpMethod.Post
  let setUrl (u : string) = req.RequestUri <- Uri u
  match m.receiverType with
  | "sendgrid" ->
    // SendGrid v3: POST https://api.sendgrid.com/v3/mail/send
    let subject, plain = renderSummary m.body
    let fromAddr =
      m.extra |> Map.tryFind "from"
      |> Option.defaultValue "alerts@pulseboard.local"
    let toAddr =
      m.extra |> Map.tryFind "to" |> Option.defaultValue ""
    let body =
      sprintf """{"personalizations":[{"to":[{"email":%s}]}],"from":{"email":%s},"subject":%s,"content":[{"type":"text/plain","value":%s}]}"""
        (jsonEscape toAddr) (jsonEscape fromAddr)
        (jsonEscape subject) (jsonEscape plain)
    let url =
      if String.IsNullOrEmpty m.url
      then "https://api.sendgrid.com/v3/mail/send"
      else m.url
    setUrl url
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    match m.secret with
    | Some k ->
      req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + k) |> ignore
    | None -> ()
  | "mailgun" ->
    // Mailgun Messages API: POST https://api.mailgun.net/v3/<domain>/messages
    // (EU region: api.eu.mailgun.net). Body is x-www-form-urlencoded; auth is
    // HTTP Basic with username "api" and the API key as the password.
    let subject, plain = renderSummary m.body
    let domain   = m.extra |> Map.tryFind "domain" |> Option.defaultValue ""
    let fromAddr =
      m.extra |> Map.tryFind "from"
      |> Option.defaultValue (if domain <> "" then "alerts@" + domain else "alerts@pulseboard.local")
    let toAddr   = m.extra |> Map.tryFind "to" |> Option.defaultValue ""
    let enc s = Uri.EscapeDataString (s : string)
    let body =
      sprintf "from=%s&to=%s&subject=%s&text=%s"
        (enc fromAddr) (enc toAddr) (enc subject) (enc plain)
    let url =
      if String.IsNullOrEmpty m.url
      then sprintf "https://api.mailgun.net/v3/%s/messages" domain
      else m.url
    setUrl url
    req.Content <- new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
    match m.secret with
    | Some key ->
      let cred = "api:" + key
      let b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes cred)
      req.Headers.TryAddWithoutValidation("Authorization", "Basic " + b64) |> ignore
    | None -> ()
  | "twilio" ->
    // POST https://api.twilio.com/2010-04-01/Accounts/<sid>/Messages.json
    let _, plain = renderSummary m.body
    let sid   = m.extra |> Map.tryFind "account_sid" |> Option.defaultValue ""
    let fromN = m.extra |> Map.tryFind "from" |> Option.defaultValue ""
    let toN   = m.extra |> Map.tryFind "to"   |> Option.defaultValue ""
    let body  =
      // Twilio caps SMS at 1600 chars; keep summary short.
      let txt = if plain.Length > 1500 then plain.Substring(0, 1500) else plain
      let enc s = Uri.EscapeDataString (s : string)
      sprintf "From=%s&To=%s&Body=%s" (enc fromN) (enc toN) (enc txt)
    let url =
      if String.IsNullOrEmpty m.url
      then sprintf "https://api.twilio.com/2010-04-01/Accounts/%s/Messages.json" sid
      else m.url
    setUrl url
    req.Content <- new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
    match m.secret with
    | Some token when sid <> "" ->
      let cred = sid + ":" + token
      let b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes cred)
      req.Headers.TryAddWithoutValidation("Authorization", "Basic " + b64) |> ignore
    | _ -> ()
  | "jira" ->
    // POST <baseUrl>/rest/api/3/issue (URL pre-baked to include path)
    let subject, plain = renderSummary m.body
    let project = m.extra |> Map.tryFind "project"  |> Option.defaultValue ""
    let issueT  = m.extra |> Map.tryFind "issueType" |> Option.defaultValue "Task"
    let user    = m.extra |> Map.tryFind "user"      |> Option.defaultValue ""
    let body =
      sprintf """{"fields":{"project":{"key":%s},"summary":%s,"issuetype":{"name":%s},"description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":%s}]}]}}}"""
        (jsonEscape project) (jsonEscape subject)
        (jsonEscape issueT)  (jsonEscape plain)
    setUrl m.url
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    match m.secret with
    | Some token when user <> "" ->
      let cred = user + ":" + token
      let b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes cred)
      req.Headers.TryAddWithoutValidation("Authorization", "Basic " + b64) |> ignore
    | _ -> ()
  | "ses" ->
    // Minimal AWS SES Query API call (region-aware). Requires SigV4 in
    // production; the lite path here uses the Email API host but lets
    // the operator front it with an IAM-authenticated proxy (so the
    // proxy attaches the signature). Body is x-www-form-urlencoded.
    let subject, plain = renderSummary m.body
    let fromAddr = m.extra |> Map.tryFind "from" |> Option.defaultValue ""
    let toAddr   = m.extra |> Map.tryFind "to"   |> Option.defaultValue ""
    let enc s = Uri.EscapeDataString (s : string)
    let body =
      sprintf "Action=SendEmail&Source=%s&Destination.ToAddresses.member.1=%s&Message.Subject.Data=%s&Message.Body.Text.Data=%s"
        (enc fromAddr) (enc toAddr) (enc subject) (enc plain)
    setUrl m.url
    req.Content <- new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
    match m.secret with
    | Some k ->
      // Allow a pre-shared token on a proxy.
      req.Headers.TryAddWithoutValidation("Authorization", k) |> ignore
    | None -> ()
  | "hmac_webhook" ->
    setUrl m.url
    req.Content <- new StringContent(m.body, Encoding.UTF8, "application/json")
    match m.secret with
    | Some s ->
      let sig_ = hmacHex s m.body
      req.Headers.TryAddWithoutValidation("X-PulseBoard-Signature", "sha256=" + sig_) |> ignore
    | None -> ()
  | "pagerduty" ->
    setUrl m.url
    req.Content <- new StringContent(m.body, Encoding.UTF8, "application/json")
    match m.secret with
    | Some key ->
      req.Headers.TryAddWithoutValidation("Authorization", "Token token=" + key) |> ignore
    | None -> ()
  | "opsgenie" ->
    setUrl m.url
    req.Content <- new StringContent(m.body, Encoding.UTF8, "application/json")
    match m.secret with
    | Some key ->
      req.Headers.TryAddWithoutValidation("Authorization", "GenieKey " + key) |> ignore
    | None -> ()
  | _ ->
    // discord / slack / teams / webhook — JSON envelope as-is.
    setUrl m.url
    req.Content <- new StringContent(m.body, Encoding.UTF8, "application/json")
  // Caller-supplied headers always win.
  for KeyValue(k, v) in m.headers do
    try req.Headers.TryAddWithoutValidation(k, v) |> ignore with _ -> ()
  req

/// Dispatch one message. Returns `Ok ()` on 2xx, `Error msg` otherwise.
/// Receiver-specific HTTP shaping lives in `shapeRequest`; this layer
/// only owns the transport (timeout, status handling, error capture).
let dispatch (m : OutboundMessage) : Task<Result<unit, string>> =
  task {
    try
      if m.url = "" && m.receiverType <> "sendgrid" && m.receiverType <> "twilio" && m.receiverType <> "mailgun" then
        return Result.Error "no url"
      else
        use req = shapeRequest m
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
        nqlog (sprintf "worker leased %d message(s) for dispatch" batch.Length)
        for m in batch do
          let (TenantId tidText) = m.tenantId
          nqlog (sprintf "dispatch msgId=%s tenant=%s receiver=%s type=%s url=%s attempt=%d/%d"
                   m.id tidText m.receiverId m.receiverType
                   (if m.url = "" then "(none)" else m.url) (m.attempt + 1) m.maxAttempts)
          let! result = dispatch m
          match metricStore with
          | Some ms ->
            ms.Record("pulse_notify_attempts_total", { ts = now; value = 1.0 })
          | None -> ()
          match result with
          | Result.Ok () ->
            nqlog (sprintf "  -> OK msgId=%s delivered (acked)" m.id)
            queue.Ack m.id
          | Result.Error err ->
            match metricStore with
            | Some ms ->
              ms.Record("pulse_notify_failures_total", { ts = now; value = 1.0 })
            | None -> ()
            if m.attempt + 1 >= m.maxAttempts then
              nqlog (sprintf "  -> DEAD msgId=%s after %d attempts: %s" m.id m.maxAttempts err)
              queue.Dead(m.id, err)
            else
              let exp = baseBackoffMs * (1L <<< (min 10 (m.attempt + 1)))
              let cap = min exp maxBackoffMs
              let jitter = int64 (rnd.Next(int (cap / 4L + 1L)))
              nqlog (sprintf "  -> FAIL msgId=%s: %s (retry in ~%dms, attempt %d/%d)"
                       m.id err (cap + jitter) (m.attempt + 1) m.maxAttempts)
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
