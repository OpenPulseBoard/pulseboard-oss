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

  let drop (id : Guid) =
    match subscribers.TryRemove id with
    | true, s ->
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
        | Result.Error _ -> drop id
      with _ -> drop id
    } :> System.Threading.Tasks.Task

  let tick _ =
    let now = nowMs ()
    let empty = ByteSegment [||]
    for KeyValue(id, s) in subscribers |> Seq.toArray do
      if now - s.lastSeenMs > int64 idleTimeoutMs then
        // No frame from the peer in the idle window. Even on a
        // half-closed TCP socket the client should have answered our
        // last Ping with a Pong; absence means the connection is dead.
        drop id
      else
        trySend id s Ping empty |> ignore

  let heartbeat =
    new Timer(TimerCallback(tick), null, heartbeatMs, heartbeatMs)

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
  member _.Publish(json : string) =
    let bytes = Encoding.UTF8.GetBytes json
    let segment = ByteSegment bytes
    for KeyValue(id, s) in subscribers |> Seq.toArray do
      trySend id s Text segment |> ignore

  interface IDisposable with
    member _.Dispose() = heartbeat.Dispose()

/// WebSocket handler that registers the client and parks until close.
///
/// IMPORTANT: every opcode must be matched explicitly. A wildcard `| _ -> ()`
/// would turn a half-closed socket into a 100%-CPU spin because `ws.read()`
/// can return non-data frames with no I/O wait. We also `Touch` on every
/// frame so the watchdog knows the connection is alive.
let handler (hub : Broadcaster) (ws : WebSocket) (_ctx : Suave.Http.HttpContext) =
  socket {
    let id = hub.Subscribe ws
    let mutable loop = true
    // Frames received with no intervening I/O wait. If the per-iteration
    // body of the outer socket-monad while-loop completes synchronously
    // forever (a Suave reader spin), this counter saturates fast and we
    // bail. 1000 chosen well above any plausible legitimate client rate
    // (the hub is push-only; clients should only ever send Ping/Pong/Close).
    let mutable busyStreak = 0
    try
      while loop do
        let! msg = ws.read()
        match msg with
        | (Close, _, _) ->
          let empty = ByteSegment [||]
          do! ws.send Close empty true
          loop <- false
        | (Ping, data, _) ->
          // Real client liveness signal — reset the streak counter and
          // tell the watchdog the connection is alive.
          busyStreak <- 0
          hub.Touch id
          do! ws.send Pong data true
        | (Pong, _, _) ->
          // Reply to our own heartbeat Ping — same treatment.
          busyStreak <- 0
          hub.Touch id
        | (Text, _, _) | (Binary, _, _) ->
          // Hub is push-only; a client has no reason to send data frames.
          // Do NOT Touch — if these arrive at machine-gun speed they are
          // the signature of a Suave parser spin, and Touch would mask
          // the watchdog. Count them; bail if they flood.
          busyStreak <- busyStreak + 1
          if busyStreak > 1000 then loop <- false
        | (Continuation, _, _) ->
          // An unsolicited Continuation frame is a protocol violation
          // (RFC 6455 §5.4 — only valid after a Text/Binary start frame,
          // and we never start a fragmented receive). Also the signature
          // of Suave's half-closed-socket spin where a zero-filled 2-byte
          // header decodes as opcode=0/len=0 and the read loop returns
          // instantly with no I/O wait. Bail immediately.
          loop <- false
        | (Reserved, _, _) ->
          // Protocol violation — drop the connection rather than spin.
          loop <- false
    finally
      hub.Unsubscribe id
  }
