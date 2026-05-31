module PulseBoard.Hub

open System
open System.Collections.Concurrent
open System.Reflection
open System.Text
open System.Threading
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

/// Per-subscriber bookkeeping. `lastSeenMs` is bumped every time the
/// handler observes a frame (any opcode); the watchdog uses it to
/// detect zombies. `forceShutdown` reaches into Suave's `WebSocket` via
/// reflection to find the underlying `Connection.transport` and slams
/// it shut — this is the only known way to unblock a Suave reader that
/// has wedged itself spinning on a half-closed TCP socket (a bug in
/// Suave's `HttpReader.readMoreData` which treats `transport.read = Ok 0`
/// as "got nothing, try again" instead of EOF).
type private Subscriber =
  { ws            : WebSocket
    mutable lastSeenMs : int64
    forceShutdown : unit -> unit }

/// Build a best-effort transport.shutdown thunk for a WebSocket by
/// reflection. Returns a no-op if Suave's internal layout changes; we
/// never want this to throw.
let private buildForceShutdown (ws : WebSocket) : unit -> unit =
  try
    let connField =
      ws.GetType().GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic)
      |> Array.tryFind (fun f -> f.FieldType = typeof<Connection>)
    match connField with
    | None -> fun () -> ()
    | Some f ->
      fun () ->
        try
          let conn = f.GetValue(ws) :?> Connection
          // ITransport.shutdown returns a ValueTask in Suave 3.x; ignore
          // the result. Any failure means the socket is already gone.
          conn.transport.shutdown() |> ignore
        with _ -> ()
  with _ -> fun () -> ()

/// Bump this when changing the spin-breaker logic so we can confirm
/// from inside the deployed container which build is actually loaded:
///   strings -el /app/PulseBoard.dll | grep PULSEBOARD_HUB_REV
let HubRev = "PULSEBOARD_HUB_REV=2026-05-31-deep-trace"

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// Broadcasts JSON-encoded events to every connected WebSocket client.
/// Slow / failed clients are dropped silently. A background heartbeat
/// pings every subscriber every `heartbeatMs` and a watchdog force-closes
/// any connection that has not produced a frame in `idleTimeoutMs` —
/// guaranteeing that a half-closed TCP socket (browser tab closed without
/// a Close frame) cannot pin a thread in Suave's reader spin-loop.
type Broadcaster(?heartbeatMs : int, ?idleTimeoutMs : int) =
  let subscribers = ConcurrentDictionary<Guid, Subscriber>()
  let heartbeatMs   = defaultArg heartbeatMs   30_000
  let idleTimeoutMs = defaultArg idleTimeoutMs 90_000
  // Anchor HubRev into the .cctor so the literal survives into the
  // compiled assembly even with aggressive optimisation, and surface
  // it once in the process log.
  do eprintfn "[hub] %s" HubRev

  let drop (id : Guid) (reason : string) =
    match subscribers.TryRemove id with
    | true, s ->
      eprintfn "[hub-drop] id=%O reason=%s remaining=%d" id reason subscribers.Count
      // Force the underlying socket closed so a wedged reader unblocks
      // and the handler's `finally` actually runs. Safe to call even if
      // the socket is already gone.
      s.forceShutdown ()
    | _ -> ()

  let trySend (id : Guid) (s : Subscriber) (op : Opcode) (seg : ByteSegment) =
    task {
      try
        let! r = s.ws.send op seg true
        match r with
        | Ok () -> ()
        | Result.Error _ -> drop id "send-error"
      with _ -> drop id "send-exception"
    } :> System.Threading.Tasks.Task

  let tick _ =
    let now = nowMs ()
    let empty = ByteSegment [||]
    let snapshot = subscribers |> Seq.toArray
    let mutable pinged = 0
    let mutable dropped = 0
    for KeyValue(id, s) in snapshot do
      if now - s.lastSeenMs > int64 idleTimeoutMs then
        // No frame from the peer in the idle window. Even on a
        // half-closed TCP socket the client should have answered our
        // last Ping with a Pong; absence means the connection is dead.
        drop id "idle-watchdog"
        dropped <- dropped + 1
      else
        trySend id s Ping empty |> ignore
        pinged <- pinged + 1
    eprintfn "[hub-tick] count=%d pinged=%d dropped=%d" subscribers.Count pinged dropped

  let heartbeat =
    new Timer(TimerCallback(tick), null, heartbeatMs, heartbeatMs)

  // Counters so we can sample Publish-rate without per-call logging.
  let mutable publishCount = 0L
  let mutable lastPublishLogMs = nowMs ()

  member internal _.Touch(id : Guid) =
    match subscribers.TryGetValue id with
    | true, s -> s.lastSeenMs <- nowMs ()
    | _ -> ()

  member _.Subscribe(ws : WebSocket) : Guid =
    let id = Guid.NewGuid()
    let s =
      { ws            = ws
        lastSeenMs    = nowMs ()
        forceShutdown = buildForceShutdown ws }
    subscribers.[id] <- s
    id

  member _.Unsubscribe(id : Guid) =
    subscribers.TryRemove(id) |> ignore

  member _.Count = subscribers.Count

  /// Fire-and-forget broadcast of a UTF-8 text payload.
  member this.Publish(json : string) =
    let bytes = Encoding.UTF8.GetBytes json
    let segment = ByteSegment bytes
    let snapshot = subscribers |> Seq.toArray
    for KeyValue(id, s) in snapshot do
      trySend id s Text segment |> ignore
    let n = System.Threading.Interlocked.Increment(&publishCount)
    // Log once a second with rate + fan-out size so we can see how much
    // work zombies multiply.
    let now = nowMs ()
    if now - lastPublishLogMs >= 1000L then
      lastPublishLogMs <- now
      eprintfn "[hub-publish] total=%d subs=%d bytes=%d" n snapshot.Length bytes.Length

  interface IDisposable with
    member _.Dispose() = heartbeat.Dispose()

/// WebSocket handler that registers the client and parks until close.
///
/// IMPORTANT: every opcode must be matched explicitly. A wildcard `| _ -> ()`
/// would turn a half-closed socket into a 100%-CPU spin because `ws.read()`
/// can return non-data frames with no I/O wait. We also `Touch` on every
/// frame so the watchdog knows the connection is alive.
let private handlerInvocationCount = ref 0L

let handler (hub : Broadcaster) (ws : WebSocket) (_ctx : Suave.Http.HttpContext) =
  // Outside-socket{} log so we know definitively how often Suave invokes
  // our handler function (vs how often the socket{} body runs).
  let invN = System.Threading.Interlocked.Increment(handlerInvocationCount)
  eprintfn "[hub-handler] FN-INVOKE n=%d" invN
  socket {
    let id = hub.Subscribe ws
    let mutable loop = true
    let mutable windowStartMs = nowMs ()
    let mutable framesInWindow = 0
    let mutable totalFrames = 0L
    let mutable iterCount = 0L
    let mutable lastIterLogMs = nowMs ()
    eprintfn "[hub-handler] enter id=%O invN=%d" id invN
    try
      while loop do
        iterCount <- iterCount + 1L
        // Capture ws.read()'s Result without the socket{} bind
        // short-circuiting on Error: that's how we lose the `exit` log
        // when the read returns Error.
        let! readResult =
          SocketOp.ofTask (task {
            let task = (ws.read ()).AsTask()
            let! r = task
            return Ok r
          })
        let nowIter = nowMs ()
        // Log first 5 iterations always (handshake / settle window),
        // and at most one line per second thereafter, with the result
        // kind. This is the canary that proves whether the while-loop
        // is iterating fast or parked.
        if iterCount <= 5L || nowIter - lastIterLogMs >= 1000L then
          lastIterLogMs <- nowIter
          let tag =
            match readResult with
            | Ok (Close,_,_) -> "Ok/Close"
            | Ok (Ping,_,_) -> "Ok/Ping"
            | Ok (Pong,_,_) -> "Ok/Pong"
            | Ok (Text,_,_) -> "Ok/Text"
            | Ok (Binary,_,_) -> "Ok/Binary"
            | Ok (Continuation,_,_) -> "Ok/Cont"
            | Ok (Reserved,_,_) -> "Ok/Reserved"
            | Result.Error e -> sprintf "Error/%A" e
          eprintfn "[hub-handler] iter id=%O n=%d total=%d result=%s"
            id iterCount totalFrames tag
        match readResult with
        | Result.Error e ->
          // ws.read failed: stop the loop ourselves so the finally
          // runs and we get an exit log. This was previously hidden
          // because the socket{} `let!` returns the Error from the
          // whole computation, skipping our finally's eprintfn under
          // some builder paths.
          eprintfn "[hub-handler] id=%O READ-ERROR %A total=%d iter=%d" id e totalFrames iterCount
          loop <- false
        | Ok msg ->
          let now = nowMs ()
          totalFrames <- totalFrames + 1L
          if now - windowStartMs >= 1000L then
            let op =
              match msg with
              | (Close,_,_) -> "Close" | (Ping,_,_) -> "Ping" | (Pong,_,_) -> "Pong"
              | (Text,_,_) -> "Text" | (Binary,_,_) -> "Binary"
              | (Continuation,_,_) -> "Cont" | (Reserved,_,_) -> "Reserved"
            eprintfn "[hub-handler] id=%O rate=%d/s total=%d last=%s"
              id framesInWindow totalFrames op
            windowStartMs <- now
            framesInWindow <- 1
          else
            framesInWindow <- framesInWindow + 1
          if framesInWindow > 100 then
            eprintfn "[hub-handler] id=%O BAIL spin: %d frames in <1s (total=%d)"
              id framesInWindow totalFrames
            loop <- false
          else
            match msg with
            | (Close, _, _) ->
              let empty = ByteSegment [||]
              do! ws.send Close empty true
              loop <- false
            | (Ping, data, _) ->
              hub.Touch id
              do! ws.send Pong data true
            | (Pong, _, _) ->
              hub.Touch id
            | (Text, _, _) | (Binary, _, _) ->
              ()
            | (Continuation, _, _) ->
              eprintfn "[hub-handler] id=%O BAIL unsolicited Continuation (total=%d)" id totalFrames
              loop <- false
            | (Reserved, _, _) ->
              eprintfn "[hub-handler] id=%O BAIL Reserved opcode (total=%d)" id totalFrames
              loop <- false
    finally
      eprintfn "[hub-handler] exit id=%O total=%d iter=%d invN=%d" id totalFrames iterCount invN
      hub.Unsubscribe id
  }
