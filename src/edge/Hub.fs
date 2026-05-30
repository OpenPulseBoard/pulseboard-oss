module PulseBoard.Hub

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

/// Broadcasts JSON-encoded events to every connected WebSocket client.
/// Slow / failed clients are dropped silently. A background heartbeat
/// pings every subscriber every `heartbeatMs` so half-closed connections
/// (e.g. a browser tab closed without a Close frame) are reaped instead
/// of leaking and pinning a thread in the receive loop.
type Broadcaster(?heartbeatMs : int) =
  let subscribers = ConcurrentDictionary<Guid, WebSocket>()
  let heartbeatMs = defaultArg heartbeatMs 30_000

  let trySend (id : Guid) (ws : WebSocket) (op : Opcode) (seg : ByteSegment) =
    task {
      try
        let! r = ws.send op seg true
        match r with
        | Ok () -> ()
        | Result.Error _ -> subscribers.TryRemove(id) |> ignore
      with _ ->
        subscribers.TryRemove(id) |> ignore
    } :> System.Threading.Tasks.Task

  let pingAll _ =
    let empty = ByteSegment [||]
    for KeyValue(id, ws) in subscribers |> Seq.toArray do
      trySend id ws Ping empty |> ignore

  let heartbeat =
    new Timer(TimerCallback(pingAll), null, heartbeatMs, heartbeatMs)

  member _.Subscribe(ws : WebSocket) : Guid =
    let id = Guid.NewGuid()
    subscribers.[id] <- ws
    id

  member _.Unsubscribe(id : Guid) =
    subscribers.TryRemove(id) |> ignore

  member _.Count = subscribers.Count

  /// Fire-and-forget broadcast of a UTF-8 text payload.
  member _.Publish(json : string) =
    let bytes = Encoding.UTF8.GetBytes json
    let segment = ByteSegment bytes
    for KeyValue(id, ws) in subscribers |> Seq.toArray do
      trySend id ws Text segment |> ignore

  interface IDisposable with
    member _.Dispose() = heartbeat.Dispose()

/// WebSocket handler that registers the client and parks until close.
///
/// IMPORTANT: every opcode must be matched explicitly. A wildcard `| _ -> ()`
/// turns a half-closed socket into a 100%-CPU spin because `ws.read()` keeps
/// returning the same non-data frame with no I/O wait.
let handler (hub : Broadcaster) (ws : WebSocket) (_ctx : Suave.Http.HttpContext) =
  socket {
    let id = hub.Subscribe ws
    let mutable loop = true
    try
      while loop do
        let! msg = ws.read()
        match msg with
        | (Close, _, _) ->
          let empty = ByteSegment [||]
          do! ws.send Close empty true
          loop <- false
        | (Ping, data, _) ->
          do! ws.send Pong data true
        | (Pong, _, _) ->
          ()
        | (Text, _, _) | (Binary, _, _) | (Continuation, _, _) ->
          // Hub is push-only; ignore anything the client sends.
          ()
        | (Reserved, _, _) ->
          // Protocol violation — drop the connection rather than spin.
          loop <- false
    finally
      hub.Unsubscribe id
  }
