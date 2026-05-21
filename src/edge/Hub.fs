module PulseBoard.Hub

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

/// Broadcasts JSON-encoded events to every connected WebSocket client.
/// Slow / failed clients are dropped silently.
type Broadcaster() =
  let subscribers = ConcurrentDictionary<Guid, WebSocket>()

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
    // Snapshot to avoid mutation while iterating
    let pairs = subscribers |> Seq.toArray
    for KeyValue(id, ws) in pairs do
      // Schedule the send; do not wait. Errors -> drop subscriber.
      let _ =
        task {
          try
            let! r = ws.send Text segment true
            match r with
            | Ok () -> ()
            | Result.Error _ -> subscribers.TryRemove(id) |> ignore
          with _ ->
            subscribers.TryRemove(id) |> ignore
        }
      ()

/// WebSocket handler that registers the client and parks until close.
let handler (hub : Broadcaster) (ws : WebSocket) (_ctx : Suave.Http.HttpContext) =
  socket {
    let id = hub.Subscribe ws
    let mutable loop = true
    try
      while loop do
        let! msg = ws.read()
        match msg with
        | (Close, _, _) ->
          let empty = [||] |> ByteSegment
          do! ws.send Close empty true
          loop <- false
        | _ -> ()
    finally
      hub.Unsubscribe id
  }
