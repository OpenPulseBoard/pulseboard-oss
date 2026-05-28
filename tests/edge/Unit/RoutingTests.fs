module PulseBoard.Tests.Unit.RoutingTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open FsUnit.Xunit
open PulseBoard.Routing
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

/// Build a Matcher record directly (compileMatcher is private).
let private m name op value =
    let re =
        match op with
        | MRe | MNRe -> try Some (Regex("^" + value + "$")) with _ -> None
        | _          -> None
    { name = name; op = op; value = value; re = re }

let private lbl pairs = pairs |> Map.ofList

let private defaultRoute (recvId : string) : Route =
    { id              = "root"
      matchers        = [||]
      receiverId      = Some recvId
      policyId        = None
      groupBy         = [| "alertname" |]
      groupWaitMs     = 30_000L
      groupIntervalMs = 300_000L
      repeatIntervalMs= 3_600_000L
      continue_       = false
      muteTimeIds     = [||]
      children        = [||] }

let private emptyConfig recvId : Config =
    { route       = defaultRoute recvId
      receivers   = [||]
      silences    = [||]
      inhibitions = [||]
      muteTimes   = [||] }

// -- matcherMatches ---------------------------------------------------------

[<Fact>]
let ``MEq matches when label equals value`` () =
    matcherMatches (m "env" MEq "prod") (lbl [ "env", "prod" ]) |> should be True

[<Fact>]
let ``MEq does not match when label differs`` () =
    matcherMatches (m "env" MEq "prod") (lbl [ "env", "staging" ]) |> should be False

[<Fact>]
let ``MEq matches empty string when label is absent`` () =
    matcherMatches (m "env" MEq "") Map.empty |> should be True

[<Fact>]
let ``MNeq matches when label differs from value`` () =
    matcherMatches (m "env" MNeq "prod") (lbl [ "env", "staging" ]) |> should be True

[<Fact>]
let ``MNeq does not match when label equals value`` () =
    matcherMatches (m "env" MNeq "prod") (lbl [ "env", "prod" ]) |> should be False

[<Fact>]
let ``MRe matches when label satisfies regex`` () =
    matcherMatches (m "svc" MRe "api.*") (lbl [ "svc", "api-gw" ]) |> should be True
    matcherMatches (m "svc" MRe "api.*") (lbl [ "svc", "worker" ]) |> should be False

[<Fact>]
let ``MNRe matches when label does NOT satisfy regex`` () =
    matcherMatches (m "svc" MNRe "api.*") (lbl [ "svc", "worker"  ]) |> should be True
    matcherMatches (m "svc" MNRe "api.*") (lbl [ "svc", "api-gw"  ]) |> should be False

[<Fact>]
let ``matchersMatch is conjunction — all must pass`` () =
    let ms = [| m "env" MEq "prod"; m "team" MEq "infra" |]
    matchersMatch ms (lbl [ "env", "prod"; "team", "infra" ]) |> should be True
    matchersMatch ms (lbl [ "env", "prod"; "team", "sre"   ]) |> should be False

[<Fact>]
let ``matchersMatch returns true for empty matcher array`` () =
    matchersMatch [||] Map.empty |> should be True

// -- serialiseConfig / parseConfig roundtrip --------------------------------

let private round (c : Config) =
    match parseConfig (serialiseConfig c) with
    | Result.Ok c2 -> c2
    | Result.Error e -> failwith e

[<Fact>]
let ``parseConfig roundtrips receiverId on root route`` () =
    (round (emptyConfig "recv-1")).route.receiverId |> should equal (Some "recv-1")

[<Fact>]
let ``parseConfig roundtrips groupBy labels`` () =
    let c = { emptyConfig "r" with
                route = { defaultRoute "r" with groupBy = [| "alertname"; "service" |] } }
    (round c).route.groupBy |> should equal [| "alertname"; "service" |]

[<Fact>]
let ``parseConfig roundtrips receivers`` () =
    let recv = { id = "r1"; name = "Slack"; type_ = "slack"
                 url = Some "https://hooks.slack.com/x"; secret = None
                 extra = Map.ofList [ "channel", "#alerts" ] }
    let c2 = round { emptyConfig "r1" with receivers = [| recv |] }
    c2.receivers |> should haveLength 1
    c2.receivers.[0].name |> should equal "Slack"
    c2.receivers.[0].url  |> should equal (Some "https://hooks.slack.com/x")

[<Fact>]
let ``parseConfig roundtrips silences with matchers`` () =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let s = { id = "sil-1"; matchers = [| m "env" MEq "prod" |]
              startsAt = now; endsAt = now + 3_600_000L
              createdBy = "alice"; comment = "maintenance"; createdAt = now }
    let c2 = round { emptyConfig "r" with silences = [| s |] }
    c2.silences |> should haveLength 1
    c2.silences.[0].id              |> should equal "sil-1"
    c2.silences.[0].matchers.Length |> should equal 1

[<Fact>]
let ``parseConfig roundtrips inhibitions`` () =
    let inh = { id = "inh-1"
                sourceMatchers = [| m "severity" MEq "critical" |]
                targetMatchers = [| m "severity" MEq "warning"  |]
                equal          = [| "alertname" |] }
    let c2 = round { emptyConfig "r" with inhibitions = [| inh |] }
    c2.inhibitions |> should haveLength 1
    c2.inhibitions.[0].equal |> should equal [| "alertname" |]

[<Fact>]
let ``parseConfig roundtrips mute time intervals`` () =
    let mt = { id = "mt-1"; name = "weekends"
               windows = [| { startMinute = 0; endMinute = 1440; daysOfWeek = 0x41 } |] }
    let c2 = round { emptyConfig "r" with muteTimes = [| mt |] }
    c2.muteTimes |> should haveLength 1
    c2.muteTimes.[0].windows.[0].startMinute |> should equal 0
    c2.muteTimes.[0].windows.[0].daysOfWeek  |> should equal 0x41

[<Fact>]
let ``parseConfig roundtrips child routes`` () =
    let child = { defaultRoute "child-recv" with
                    matchers = [| m "team" MEq "infra" |]
                    id       = "child-1" }
    let root = { defaultRoute "root-recv" with children = [| child |] }
    let c = { emptyConfig "root-recv" with route = root }
    let c2 = round c
    c2.route.children |> should haveLength 1
    c2.route.children.[0].id |> should equal "child-1"

[<Fact>]
let ``parseConfig returns Error for empty body`` () =
    parseConfig "" |> function Result.Error _ -> () | _ -> failwith "expected error"

[<Fact>]
let ``parseConfig defaults repeatIntervalMs to 3600000 when absent`` () =
    match parseConfig """{"route":{}}""" with
    | Result.Ok c -> c.route.repeatIntervalMs |> should equal 3_600_000L
    | Result.Error e -> failwith e

// -- parseSilenceBody -------------------------------------------------------

[<Fact>]
let ``parseSilenceBody returns Error for empty matchers`` () =
    parseSilenceBody """{"startsAt":0,"endsAt":99999}"""
    |> function Result.Error _ -> () | _ -> failwith "expected error"

[<Fact>]
let ``parseSilenceBody parses a valid silence body`` () =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let json =
        sprintf """{"matchers":[{"name":"env","op":"=","value":"prod"}],"endsAt":%d,"createdBy":"alice"}"""
                (now + 60_000L)
    match parseSilenceBody json with
    | Result.Ok s ->
        s.matchers        |> should haveLength 1
        s.matchers.[0].name  |> should equal "env"
        s.matchers.[0].value |> should equal "prod"
    | Result.Error e -> failwith e

// -- FileConfigStore --------------------------------------------------------

let private withTempCfgStore (f : IConfigStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        f (FileConfigStore dir :> IConfigStore, TenantId "t1")
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``FileConfigStore Get returns default config (no silences) for fresh tenant`` () =
    withTempCfgStore (fun (s, tid) ->
        let c = s.Get tid
        c.silences    |> should haveLength 0
        c.receivers   |> should haveLength 0
        c.inhibitions |> should haveLength 0)

[<Fact>]
let ``FileConfigStore Set then Get roundtrips receiverId`` () =
    withTempCfgStore (fun (s, tid) ->
        s.Set(tid, emptyConfig "recv-x")
        s.Get(tid).route.receiverId |> should equal (Some "recv-x"))

[<Fact>]
let ``FileConfigStore UpsertSilence adds the silence`` () =
    withTempCfgStore (fun (s, tid) ->
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let sil = { id = "s-1"; matchers = [| m "env" MEq "prod" |]
                    startsAt = now; endsAt = now + 3_600_000L
                    createdBy = "ops"; comment = ""; createdAt = now }
        s.UpsertSilence(tid, sil)
        s.Get(tid).silences |> should haveLength 1)

[<Fact>]
let ``FileConfigStore UpsertSilence with same id replaces the existing silence`` () =
    withTempCfgStore (fun (s, tid) ->
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let mk comment =
            { id = "s-dup"; matchers = [| m "x" MEq "y" |]
              startsAt = now; endsAt = now + 3_600_000L
              createdBy = "u"; comment = comment; createdAt = now }
        s.UpsertSilence(tid, mk "first")
        s.UpsertSilence(tid, mk "second")
        s.Get(tid).silences |> should haveLength 1)

[<Fact>]
let ``FileConfigStore DeleteSilence removes the silence and returns true`` () =
    withTempCfgStore (fun (s, tid) ->
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let sil = { id = "s-del"; matchers = [| m "x" MEq "y" |]
                    startsAt = now; endsAt = now + 1000L
                    createdBy = "u"; comment = ""; createdAt = now }
        s.UpsertSilence(tid, sil)
        s.DeleteSilence(tid, "s-del") |> should be True
        s.Get(tid).silences           |> should haveLength 0)

[<Fact>]
let ``FileConfigStore DeleteSilence returns false for unknown id`` () =
    withTempCfgStore (fun (s, tid) ->
        s.DeleteSilence(tid, "ghost") |> should be False)
