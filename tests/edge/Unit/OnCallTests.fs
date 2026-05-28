module PulseBoard.Tests.Unit.OnCallTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.OnCall
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private rotation id (members : string[]) periodMs startAt : Rotation =
    { id = id; members = members; periodMs = periodMs; startAt = startAt }

let private schedule id (rotations : Rotation[]) (overrides : ScheduleOverride[]) : Schedule =
    { id = id; name = id; rotations = rotations; overrides = overrides }

let private withTempStores (f : ICatalogStore * IAckStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        f (FileCatalogStore dir :> ICatalogStore,
           FileAckStore     dir :> IAckStore,
           TenantId "t1")
    finally
        try Directory.Delete(dir, true) with _ -> ()

// -- whoIsOnCall -----------------------------------------------------------

[<Fact>]
let ``whoIsOnCall returns None for schedule with no rotations`` () =
    let s = schedule "s" [||] [||]
    whoIsOnCall s (nowMs ()) |> should equal None

[<Fact>]
let ``whoIsOnCall returns None for rotation with empty member list`` () =
    let rot = rotation "r" [||] 86_400_000L 0L
    let s   = schedule "s" [| rot |] [||]
    whoIsOnCall s (nowMs ()) |> should equal None

[<Fact>]
let ``whoIsOnCall returns the only member when member list has length 1`` () =
    let rot = rotation "r" [| "alice" |] 86_400_000L 0L
    let s   = schedule "s" [| rot |] [||]
    whoIsOnCall s (nowMs ()) |> should equal (Some "alice")

[<Fact>]
let ``whoIsOnCall round-robins through members based on elapsed periods`` () =
    // 3 members with a 1-second period. The epoch is ms=0.
    let members = [| "alice"; "bob"; "carol" |]
    let rot = rotation "r" members 1_000L 0L
    let s   = schedule "s" [| rot |] [||]
    // Elapsed 0 ms → index 0 → alice
    whoIsOnCall s 0L          |> should equal (Some "alice")
    // Elapsed 1000 ms → index 1 → bob
    whoIsOnCall s 1_000L      |> should equal (Some "bob")
    // Elapsed 2000 ms → index 2 → carol
    whoIsOnCall s 2_000L      |> should equal (Some "carol")
    // Elapsed 3000 ms → wraps to index 0 → alice
    whoIsOnCall s 3_000L      |> should equal (Some "alice")

[<Fact>]
let ``whoIsOnCall respects schedule override when override is active`` () =
    let rot = rotation "r" [| "alice" |] 86_400_000L 0L
    let now = nowMs ()
    let ov  = { userId = "dave"; startsAt = now - 1000L; endsAt = now + 60_000L }
    let s   = schedule "s" [| rot |] [| ov |]
    whoIsOnCall s now |> should equal (Some "dave")

[<Fact>]
let ``whoIsOnCall falls back to rotation when override has expired`` () =
    let rot = rotation "r" [| "alice" |] 86_400_000L 0L
    let now = nowMs ()
    let ov  = { userId = "dave"; startsAt = now - 120_000L; endsAt = now - 60_000L }
    let s   = schedule "s" [| rot |] [| ov |]
    whoIsOnCall s now |> should equal (Some "alice")

[<Fact>]
let ``whoIsOnCall falls back to rotation when override has not started`` () =
    let rot = rotation "r" [| "alice" |] 86_400_000L 0L
    let now = nowMs ()
    let ov  = { userId = "dave"; startsAt = now + 3_600_000L; endsAt = now + 7_200_000L }
    let s   = schedule "s" [| rot |] [| ov |]
    whoIsOnCall s now |> should equal (Some "alice")

// -- parseCatalog / serialiseCatalog roundtrip ------------------------------

[<Fact>]
let ``serialiseCatalog then parseCatalog roundtrips users`` () =
    let c =
        { users =
            [| { id = "u1"; name = "Alice"; email = "alice@x.com"
                 receiverIds = [| "r1"; "r2" |] } |]
          schedules = [||]
          policies  = [||] }
    match parseCatalog (serialiseCatalog c) with
    | Result.Ok c2 ->
        c2.users |> should haveLength 1
        c2.users.[0].name        |> should equal "Alice"
        c2.users.[0].receiverIds |> should equal [| "r1"; "r2" |]
    | Result.Error e -> failwith e

[<Fact>]
let ``serialiseCatalog roundtrips schedules and overrides`` () =
    let rot = rotation "r1" [| "u1"; "u2" |] 86_400_000L 0L
    let ov  = { userId = "u3"; startsAt = 1000L; endsAt = 2000L }
    let s   = { id = "sched-1"; name = "primary"; rotations = [| rot |]; overrides = [| ov |] }
    let c   = { emptyCatalog with schedules = [| s |] }
    match parseCatalog (serialiseCatalog c) with
    | Result.Ok c2 ->
        c2.schedules |> should haveLength 1
        c2.schedules.[0].rotations |> should haveLength 1
        c2.schedules.[0].overrides |> should haveLength 1
        c2.schedules.[0].overrides.[0].userId |> should equal "u3"
    | Result.Error e -> failwith e

[<Fact>]
let ``serialiseCatalog roundtrips escalation policies and steps`` () =
    let step = { delayMs = 5_000L; targets = [| TgtReceiver "r1"; TgtUser "u1" |] }
    let pol  = { id = "p1"; name = "default"; steps = [| step |] }
    let c    = { emptyCatalog with policies = [| pol |] }
    match parseCatalog (serialiseCatalog c) with
    | Result.Ok c2 ->
        c2.policies |> should haveLength 1
        c2.policies.[0].steps |> should haveLength 1
        c2.policies.[0].steps.[0].delayMs |> should equal 5_000L
        c2.policies.[0].steps.[0].targets.[0] |> should equal (TgtReceiver "r1")
    | Result.Error e -> failwith e

[<Fact>]
let ``parseCatalog returns Error for invalid JSON`` () =
    parseCatalog "not json" |> function Result.Error _ -> () | _ -> failwith "expected error"

// -- FileCatalogStore -------------------------------------------------------

[<Fact>]
let ``FileCatalogStore Get returns empty catalog for unknown tenant`` () =
    withTempStores (fun (cs, _, tid) ->
        let c = cs.Get tid
        c.users     |> should haveLength 0
        c.schedules |> should haveLength 0
        c.policies  |> should haveLength 0)

[<Fact>]
let ``FileCatalogStore Set then Get roundtrips`` () =
    withTempStores (fun (cs, _, tid) ->
        let u = { id = "u1"; name = "Bob"; email = "bob@x.com"; receiverIds = [||] }
        let c = { emptyCatalog with users = [| u |] }
        cs.Set(tid, c)
        cs.Get(tid).users |> should haveLength 1)

// -- FileAckStore -----------------------------------------------------------

[<Fact>]
let ``FileAckStore IsAcked returns false before Ack`` () =
    withTempStores (fun (_, acks, tid) ->
        acks.IsAcked(tid, "fp-1") |> should be False)

[<Fact>]
let ``FileAckStore Ack then IsAcked returns true`` () =
    withTempStores (fun (_, acks, tid) ->
        let a = { fingerprint = "fp-1"; user = "alice"; ackedAt = nowMs () }
        acks.Ack(tid, a)
        acks.IsAcked(tid, "fp-1") |> should be True)

[<Fact>]
let ``FileAckStore List returns the acknowledgement after Ack`` () =
    withTempStores (fun (_, acks, tid) ->
        let a = { fingerprint = "fp-2"; user = "bob"; ackedAt = nowMs () }
        acks.Ack(tid, a)
        let result = acks.List(tid, "fp-2")
        result |> should haveLength 1
        result.[0].user |> should equal "bob")

[<Fact>]
let ``FileAckStore List returns empty for unknown fingerprint`` () =
    withTempStores (fun (_, acks, tid) ->
        acks.List(tid, "no-such-fp") |> should haveLength 0)
