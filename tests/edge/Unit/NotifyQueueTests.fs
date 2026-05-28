module PulseBoard.Tests.Unit.NotifyQueueTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.NotifyQueue
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

let private tid = TenantId "t1"

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private makeMsg () : OutboundMessage =
    let now = nowMs ()
    { id           = Guid.NewGuid().ToString "N"
      tenantId     = tid
      receiverId   = "recv-1"
      receiverType = "webhook"
      url          = "http://localhost/hook"
      secret       = None
      body         = """{"test":true}"""
      headers      = Map.empty
      extra        = Map.empty
      attempt      = 0
      maxAttempts  = 3
      enqueuedAt   = now
      nextRunAt    = now
      lastError    = None }

let private withTempQueue (f : INotifyQueue * string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        f (FileNotifyQueue dir :> INotifyQueue, dir)
    finally
        try Directory.Delete(dir, true) with _ -> ()

// -- serialiseMsg / parseMsg roundtrip --------------------------------------

[<Fact>]
let ``serialiseMsg then parseMsg roundtrips all fields`` () =
    let m = makeMsg ()
    match parseMsg (serialiseMsg m) with
    | None -> failwith "parseMsg returned None"
    | Some m2 ->
        m2.id           |> should equal m.id
        m2.tenantId     |> should equal m.tenantId
        m2.receiverId   |> should equal m.receiverId
        m2.receiverType |> should equal m.receiverType
        m2.url          |> should equal m.url
        m2.body         |> should equal m.body
        m2.maxAttempts  |> should equal m.maxAttempts

[<Fact>]
let ``serialiseMsg roundtrips optional secret and lastError`` () =
    let m = { makeMsg () with secret = Some "tok"; lastError = Some "timeout" }
    match parseMsg (serialiseMsg m) with
    | None -> failwith "None"
    | Some m2 ->
        m2.secret    |> should equal (Some "tok")
        m2.lastError |> should equal (Some "timeout")

[<Fact>]
let ``parseMsg returns None for malformed JSON`` () =
    parseMsg "not json" |> should equal None

[<Fact>]
let ``parseMsg returns None when required fields are missing`` () =
    parseMsg """{"id":"x"}""" |> should equal None

// -- Enqueue / Pending / Lease / Ack ----------------------------------------

[<Fact>]
let ``Enqueue then Pending returns the message`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        q.Pending(Some tid) |> should haveLength 1)

[<Fact>]
let ``Pending returns empty when no messages are enqueued`` () =
    withTempQueue (fun (q, _) ->
        q.Pending(None) |> should haveLength 0)

[<Fact>]
let ``Pending filters by tenant id`` () =
    withTempQueue (fun (q, _) ->
        let m1 = makeMsg ()
        let m2 = { makeMsg () with tenantId = TenantId "other" }
        q.Enqueue m1
        q.Enqueue m2
        q.Pending(Some tid)             |> should haveLength 1
        q.Pending(Some (TenantId "other")) |> should haveLength 1
        q.Pending(None)                 |> should haveLength 2)

[<Fact>]
let ``Lease returns message and Ack removes it from Pending`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        let leased = q.Lease(10, nowMs ())
        leased |> should haveLength 1
        q.Ack leased.[0].id
        q.Pending(None) |> should haveLength 0)

[<Fact>]
let ``Lease respects nextRunAt — does not return messages scheduled in the future`` () =
    withTempQueue (fun (q, _) ->
        let m = { makeMsg () with nextRunAt = nowMs () + 60_000L }
        q.Enqueue m
        q.Lease(10, nowMs ()) |> should haveLength 0)

[<Fact>]
let ``Lease does not return the same message twice concurrently`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        let first  = q.Lease(10, nowMs ())
        let second = q.Lease(10, nowMs ())
        first  |> should haveLength 1
        second |> should haveLength 0)

// -- Fail / retry -----------------------------------------------------------

[<Fact>]
let ``Fail increments attempt count and sets nextRunAt`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        let future = nowMs () + 30_000L
        q.Fail(m.id, "timeout", future)
        let pending = q.Pending(None)
        pending |> should haveLength 1
        pending.[0].attempt    |> should equal 1
        pending.[0].lastError  |> should equal (Some "timeout")
        pending.[0].nextRunAt  |> should equal future)

[<Fact>]
let ``Fail-ed message with future nextRunAt is not leasable immediately`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        q.Lease(1, nowMs ()) |> ignore     // lease it first
        q.Fail(m.id, "err", nowMs () + 60_000L)
        q.Lease(10, nowMs ()) |> should haveLength 0)

// -- Dead letter queue ------------------------------------------------------

[<Fact>]
let ``Dead moves message from Pending to DLQ`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        q.Dead(m.id, "max retries exceeded")
        q.Pending(None)    |> should haveLength 0
        q.DeadLetters(None)|> should haveLength 1)

[<Fact>]
let ``DeadLetters returns empty when no dead messages`` () =
    withTempQueue (fun (q, _) ->
        q.DeadLetters(None) |> should haveLength 0)

[<Fact>]
let ``ReplayDead moves message from DLQ back to live queue`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        q.Dead(m.id, "err")
        q.ReplayDead m.id  |> should be True
        q.Pending(None)    |> should haveLength 1
        q.DeadLetters(None)|> should haveLength 0)

[<Fact>]
let ``ReplayDead resets attempt counter to 0`` () =
    withTempQueue (fun (q, _) ->
        let m = { makeMsg () with attempt = 2 }
        q.Enqueue m
        q.Dead(m.id, "err")
        q.ReplayDead m.id |> ignore
        q.Pending(None).[0].attempt |> should equal 0)

[<Fact>]
let ``ReplayDead returns false for unknown id`` () =
    withTempQueue (fun (q, _) ->
        q.ReplayDead "ghost" |> should be False)

[<Fact>]
let ``PurgeDead removes message from DLQ permanently`` () =
    withTempQueue (fun (q, _) ->
        let m = makeMsg ()
        q.Enqueue m
        q.Dead(m.id, "err")
        q.PurgeDead m.id    |> should be True
        q.DeadLetters(None) |> should haveLength 0
        q.Pending(None)     |> should haveLength 0)

[<Fact>]
let ``PurgeDead returns false for unknown id`` () =
    withTempQueue (fun (q, _) ->
        q.PurgeDead "ghost" |> should be False)

// -- Journal replay ---------------------------------------------------------

[<Fact>]
let ``FileNotifyQueue rebuilds live state after restart`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        let m1 = makeMsg ()
        let m2 = makeMsg ()
        let q1 = FileNotifyQueue dir :> INotifyQueue
        q1.Enqueue m1
        q1.Enqueue m2
        q1.Ack m1.id   // m1 acked before "restart"
        // Recreate queue from same directory
        let q2 = FileNotifyQueue dir :> INotifyQueue
        let pending = q2.Pending(None)
        pending |> should haveLength 1
        pending.[0].id |> should equal m2.id
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``FileNotifyQueue rebuilds DLQ after restart`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        let m = makeMsg ()
        let q1 = FileNotifyQueue dir :> INotifyQueue
        q1.Enqueue m
        q1.Dead(m.id, "permanent failure")
        // Recreate
        let q2 = FileNotifyQueue dir :> INotifyQueue
        q2.DeadLetters(None) |> should haveLength 1
        q2.Pending(None)     |> should haveLength 0
    finally
        try Directory.Delete(dir, true) with _ -> ()
