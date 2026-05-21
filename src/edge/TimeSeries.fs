module PulseBoard.TimeSeries

open System
open System.Collections.Concurrent

/// A single observation in time.
type Point =
  { ts     : int64    // unix milliseconds
    value  : float }

/// A log line attached to a service.
type LogEntry =
  { ts      : int64
    service : string
    level   : string
    message : string }

/// Fixed-size ring buffer of Points, lock-free for readers via snapshot copy.
type RingBuffer(capacity : int) =
  let buffer : Point array = Array.zeroCreate capacity
  let mutable head = 0     // next write index
  let mutable count = 0
  let sync = obj()

  member _.Capacity = capacity

  member _.Add(p : Point) =
    lock sync (fun () ->
      buffer.[head] <- p
      head <- (head + 1) % capacity
      if count < capacity then count <- count + 1)

  /// Snapshot of points in chronological order.
  member _.Snapshot() : Point array =
    lock sync (fun () ->
      let n = count
      let result = Array.zeroCreate n
      let start = if count < capacity then 0 else head
      for i in 0 .. n - 1 do
        result.[i] <- buffer.[(start + i) % capacity]
      result)

  /// Snapshot of points whose `ts >= sinceMs`.
  member x.Since(sinceMs : int64) : Point array =
    x.Snapshot() |> Array.filter (fun p -> p.ts >= sinceMs)

/// Per-metric ring-buffered store. Optional hooks let an external persistor
/// (e.g. a disk segment writer) observe writes and contribute historical data
/// older than what the ring still holds.
type MetricStore(capacityPerMetric : int) =
  let metrics = ConcurrentDictionary<string, RingBuffer>()
  let mutable onAppend   : (string -> Point -> unit) option = None
  let mutable history    : (string -> int64 -> Point array) option = None
  let mutable extraNames : (unit -> string array) option = None

  /// Register a callback invoked after every successful Record.
  member _.SetOnAppend(f : string -> Point -> unit) = onAppend <- Some f

  /// Register a provider that returns historical points older than the ring.
  member _.SetHistory(f : string -> int64 -> Point array) = history <- Some f

  /// Register a provider of known metric names from outside the ring buffer.
  member _.SetExtraNames(f : unit -> string array) = extraNames <- Some f

  member _.Names() =
    let live  = metrics.Keys |> Seq.toArray
    let extra = match extraNames with Some f -> f () | None -> [||]
    Array.append live extra |> Array.distinct |> Array.sort

  member _.Record(name : string, p : Point) =
    let rb = metrics.GetOrAdd(name, fun _ -> RingBuffer(capacityPerMetric))
    rb.Add p
    match onAppend with
    | Some f -> f name p
    | None   -> ()

  member _.Get(name : string) : Point array =
    match metrics.TryGetValue name with
    | true, rb -> rb.Snapshot()
    | _        -> [||]

  member _.GetSince(name : string, sinceMs : int64) : Point array =
    let ring =
      match metrics.TryGetValue name with
      | true, rb -> rb.Snapshot()
      | _        -> [||]
    // Does the ring already cover the requested window?
    let ringCovers =
      ring.Length > 0 && ring.[0].ts <= sinceMs
    match history with
    | Some readDisk when not ringCovers ->
      let disk = readDisk name sinceMs
      // Merge: anything from disk with ts < oldest-ring ts, then ring filtered.
      let cutoff = if ring.Length > 0 then ring.[0].ts else Int64.MaxValue
      let diskHead = disk |> Array.filter (fun p -> p.ts < cutoff && p.ts >= sinceMs)
      let ringTail = ring |> Array.filter (fun p -> p.ts >= sinceMs)
      Array.append diskHead ringTail
    | _ ->
      ring |> Array.filter (fun p -> p.ts >= sinceMs)

/// Ring-buffered log store (single shared buffer across services).
type LogStore(capacity : int) =
  let buffer : LogEntry array = Array.zeroCreate capacity
  let mutable head = 0
  let mutable count = 0
  let sync = obj()

  member _.Add(e : LogEntry) =
    lock sync (fun () ->
      buffer.[head] <- e
      head <- (head + 1) % capacity
      if count < capacity then count <- count + 1)

  member _.Snapshot() : LogEntry array =
    lock sync (fun () ->
      let n = count
      let result = Array.zeroCreate n
      let start = if count < capacity then 0 else head
      for i in 0 .. n - 1 do
        result.[i] <- buffer.[(start + i) % capacity]
      result)

  member x.Tail(maxCount : int) : LogEntry array =
    let snap = x.Snapshot()
    if snap.Length <= maxCount then snap
    else snap.[snap.Length - maxCount ..]

let nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
