module PulseBoard.Segments

open System
open System.IO
open System.Collections.Concurrent
open PulseBoard.TimeSeries

/// Binary record layout (little-endian, matches BitConverter on x64/ARM64):
///   [ int64 ts ][ float64 value ]   -> 16 bytes
[<Literal>]
let recordSize = 16

let private invalidChars =
  let arr =
    Array.append (Path.GetInvalidFileNameChars()) [| '/'; '\\'; ':' |]
  Set.ofArray arr

/// Map an arbitrary metric name to a safe directory name.
let sanitize (name : string) : string =
  if String.IsNullOrEmpty name then "_"
  else
    name
    |> Seq.map (fun c -> if Set.contains c invalidChars then '_' else c)
    |> Seq.toArray
    |> String

let private segPattern = "seg-*.bin"

let private parseStartTs (path : string) : int64 option =
  let name = Path.GetFileNameWithoutExtension path
  if name.StartsWith "seg-" then
    match Int64.TryParse(name.Substring 4) with
    | true, n -> Some n
    | _       -> None
  else None

/// Append-only writer for a single metric. Rotates to a new file once the
/// current segment exceeds `maxBytesPerFile`. Thread-safe.
type SegmentWriter(rootDir : string, metricName : string, maxBytesPerFile : int64) =
  let metricDir = Path.Combine(rootDir, sanitize metricName)
  do Directory.CreateDirectory metricDir |> ignore

  let sync = obj()
  let mutable currentStream : FileStream option = None
  let mutable currentSize   : int64 = 0L

  let openNewSegment (firstTs : int64) =
    let path = Path.Combine(metricDir, sprintf "seg-%020d.bin" firstTs)
    let fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                            FileShare.Read, 8192)
    currentStream <- Some fs
    currentSize <- 0L

  let ensureStream (ts : int64) =
    match currentStream with
    | Some _ when currentSize < maxBytesPerFile -> ()
    | _ ->
      currentStream |> Option.iter (fun s -> s.Flush(); s.Dispose())
      openNewSegment ts

  member _.MetricName = metricName

  member _.Append(p : Point) =
    lock sync (fun () ->
      ensureStream p.ts
      let fs  = currentStream.Value
      let buf = Array.zeroCreate recordSize
      Buffer.BlockCopy(BitConverter.GetBytes p.ts,    0, buf, 0, 8)
      Buffer.BlockCopy(BitConverter.GetBytes p.value, 0, buf, 8, 8)
      fs.Write(buf, 0, recordSize)
      currentSize <- currentSize + int64 recordSize)

  member _.Flush() =
    lock sync (fun () -> currentStream |> Option.iter (fun s -> s.Flush()))

  interface IDisposable with
    member _.Dispose() =
      lock sync (fun () ->
        currentStream |> Option.iter (fun s -> s.Flush(); s.Dispose())
        currentStream <- None)

module Reader =

  /// Names of every metric that has at least one segment directory on disk.
  let listMetrics (rootDir : string) : string array =
    if not (Directory.Exists rootDir) then [||]
    else
      Directory.GetDirectories rootDir
      |> Array.map Path.GetFileName
      |> Array.sort

  /// Read all points for `metricName` whose ts >= sinceMs, in chronological order.
  let readSince (rootDir : string) (metricName : string) (sinceMs : int64) : Point array =
    let dir = Path.Combine(rootDir, sanitize metricName)
    if not (Directory.Exists dir) then [||]
    else
      let segs =
        Directory.GetFiles(dir, segPattern)
        |> Array.choose (fun p -> parseStartTs p |> Option.map (fun ts -> ts, p))
        |> Array.sortBy fst
      // Start from the latest segment whose startTs <= sinceMs (to cover a
      // window that begins partway through that file); fall back to first.
      let startIdx =
        let lastBefore =
          segs
          |> Array.mapi (fun i (ts, _) -> i, ts)
          |> Array.filter (fun (_, ts) -> ts <= sinceMs)
          |> Array.tryLast
        match lastBefore with
        | Some (i, _) -> i
        | None        -> 0
      let result = ResizeArray<Point>()
      let buf = Array.zeroCreate recordSize
      for i in startIdx .. segs.Length - 1 do
        let _, path = segs.[i]
        use fs =
          new FileStream(path, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite, 8192)
        let mutable keepReading = true
        while keepReading do
          let mutable read = 0
          while read < recordSize && keepReading do
            let n = fs.Read(buf, read, recordSize - read)
            if n <= 0 then keepReading <- false
            else read <- read + n
          if keepReading then
            let ts = BitConverter.ToInt64(buf, 0)
            let v  = BitConverter.ToDouble(buf, 8)
            if ts >= sinceMs then
              result.Add { ts = ts; value = v }
      result.ToArray()

/// Convenience facade: keeps one SegmentWriter per metric, exposes the three
/// hooks expected by MetricStore.
type SegmentStore(rootDir : string, ?maxBytesPerFile : int64) =
  let maxBytes = defaultArg maxBytesPerFile (1L <<< 20)  // 1 MiB ≈ 65 536 points
  do Directory.CreateDirectory rootDir |> ignore

  let writers = ConcurrentDictionary<string, SegmentWriter>()

  let getWriter (name : string) : SegmentWriter =
    writers.GetOrAdd(name, fun n -> new SegmentWriter(rootDir, n, maxBytes))

  member _.RootDir = rootDir

  /// Hook for MetricStore.SetOnAppend.
  member _.Append (name : string) (p : Point) =
    (getWriter name).Append p

  /// Hook for MetricStore.SetHistory.
  member _.ReadSince (name : string) (sinceMs : int64) : Point array =
    Reader.readSince rootDir name sinceMs

  /// Hook for MetricStore.SetExtraNames.
  member _.KnownNames () : string array =
    Reader.listMetrics rootDir

  member _.Flush() =
    for kv in writers do kv.Value.Flush()

  interface IDisposable with
    member _.Dispose() =
      for kv in writers do (kv.Value :> IDisposable).Dispose()
      writers.Clear()
