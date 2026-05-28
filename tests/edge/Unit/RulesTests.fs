module PulseBoard.Tests.Unit.RulesTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.Rules
open PulseBoard.Tenancy

// -- helpers ----------------------------------------------------------------

let private makeRule name lang expr cmp threshold : Rule =
    { id          = Guid.NewGuid().ToString "N"
      name        = name
      lang        = lang
      expr        = expr
      cmp         = cmp
      threshold   = threshold
      forMs       = 0L
      severity    = Severity.Warning
      labels      = Map.empty
      annotations = Map.empty }

let private makeGroup name (rules : Rule[]) : RuleGroup =
    let now = DateTimeOffset.UtcNow
    { id         = Guid.NewGuid().ToString "N"
      name       = name
      intervalMs = 15_000L
      rules      = rules
      createdAt  = now
      updatedAt  = now }

// -- severity serialisation -------------------------------------------------

[<Fact>]
let ``severityToStr / strToSeverity roundtrip for all variants`` () =
    for s in [ Severity.Info; Severity.Warning; Severity.Critical; Severity.Page ] do
        strToSeverity (severityToStr s) |> should equal s

[<Fact>]
let ``severityToStr produces expected strings`` () =
    severityToStr Severity.Info     |> should equal "info"
    severityToStr Severity.Warning  |> should equal "warning"
    severityToStr Severity.Critical |> should equal "critical"
    severityToStr Severity.Page     |> should equal "page"

[<Fact>]
let ``strToSeverity falls back to Warning for unknown string`` () =
    strToSeverity "unknown" |> should equal Severity.Warning
    strToSeverity ""        |> should equal Severity.Warning

// -- alert state ------------------------------------------------------------

[<Fact>]
let ``alertStateToStr maps all variants`` () =
    alertStateToStr AlertState.Pending  |> should equal "pending"
    alertStateToStr AlertState.Firing   |> should equal "firing"
    alertStateToStr AlertState.Resolved |> should equal "resolved"

// -- fingerprint ------------------------------------------------------------

[<Fact>]
let ``fingerprint is deterministic`` () =
    let labels = Map.ofList [ "alertname", "cpu-high"; "service", "api" ]
    fingerprint "rule-1" labels |> should equal (fingerprint "rule-1" labels)

[<Fact>]
let ``fingerprint is label-order-independent`` () =
    let a = fingerprint "r" (Map.ofList [ "b", "2"; "a", "1" ])
    let b = fingerprint "r" (Map.ofList [ "a", "1"; "b", "2" ])
    a |> should equal b

[<Fact>]
let ``fingerprint differs for different label values`` () =
    let a = fingerprint "r" (Map.ofList [ "service", "api" ])
    let b = fingerprint "r" (Map.ofList [ "service", "worker" ])
    a |> should not' (equal b)

[<Fact>]
let ``fingerprint differs for different rule ids`` () =
    let labels = Map.ofList [ "service", "api" ]
    fingerprint "rule-1" labels |> should not' (equal (fingerprint "rule-2" labels))

[<Fact>]
let ``fingerprint is 16 lowercase hex characters`` () =
    let fp = fingerprint "x" Map.empty
    fp.Length |> should equal 16
    fp |> Seq.forall (fun c -> "0123456789abcdef".Contains(string c)) |> should be True

// -- parseGroup / serialiseGroup roundtrip ----------------------------------

[<Fact>]
let ``parseGroup returns Error for empty body`` () =
    parseGroup "" |> function Result.Error _ -> () | Result.Ok _ -> failwith "expected error"

[<Fact>]
let ``parseGroup returns Error when name is missing`` () =
    parseGroup """{"intervalMs":5000,"rules":[]}"""
    |> function Result.Error _ -> () | Result.Ok _ -> failwith "expected error"

[<Fact>]
let ``serialiseGroup then parseGroup roundtrips name and rule count`` () =
    let g = makeGroup "my-group" [| makeRule "cpu" PromQL "cpu" Gt 0.9 |]
    let json = serialiseGroup g
    match parseGroup json with
    | Result.Error e -> failwith e
    | Result.Ok g2 ->
        g2.name          |> should equal "my-group"
        g2.rules.Length  |> should equal 1
        g2.rules.[0].name |> should equal "cpu"
        g2.rules.[0].lang |> should equal PromQL
        g2.rules.[0].cmp  |> should equal Gt

[<Fact>]
let ``serialiseGroup roundtrips threshold and forMs`` () =
    let r = { makeRule "r" LogQL "error" Lte 42.5 with forMs = 30_000L }
    let g = makeGroup "g" [| r |]
    match parseGroup (serialiseGroup g) with
    | Result.Ok g2 ->
        g2.rules.[0].threshold |> should equal 42.5
        g2.rules.[0].forMs     |> should equal 30_000L
    | Result.Error e -> failwith e

[<Fact>]
let ``serialiseGroup roundtrips labels and annotations`` () =
    let r = { makeRule "r" PromQL "cpu" Gt 0.9
              with labels = Map.ofList [ "team", "infra" ]
                   annotations = Map.ofList [ "summary", "CPU high" ] }
    match parseGroup (serialiseGroup (makeGroup "g" [| r |])) with
    | Result.Ok g2 ->
        g2.rules.[0].labels      |> should equal (Map.ofList [ "team", "infra" ])
        g2.rules.[0].annotations |> should equal (Map.ofList [ "summary", "CPU high" ])
    | Result.Error e -> failwith e

[<Fact>]
let ``parseGroup clamps intervalMs below 1000 to 1000`` () =
    let json = """{"name":"g","intervalMs":0,"rules":[]}"""
    match parseGroup json with
    | Result.Ok g -> g.intervalMs |> should equal 1_000L
    | Result.Error e -> failwith e

[<Fact>]
let ``parseGroup ignores rules with invalid lang`` () =
    let json = """{"name":"g","rules":[{"name":"r","lang":"bogus","expr":"x","cmp":">","threshold":1}]}"""
    match parseGroup json with
    | Result.Ok g -> g.rules.Length |> should equal 0
    | Result.Error e -> failwith e

[<Fact>]
let ``parseGroup ignores rules with invalid cmp operator`` () =
    let json = """{"name":"g","rules":[{"name":"r","lang":"promql","expr":"x","cmp":"??","threshold":1}]}"""
    match parseGroup json with
    | Result.Ok g -> g.rules.Length |> should equal 0
    | Result.Error e -> failwith e

[<Fact>]
let ``serialiseGroups produces a JSON array with one element per group`` () =
    let gs = [| makeGroup "g1" [||]; makeGroup "g2" [| makeRule "r" LogQL "err" Gt 0.0 |] |]
    let json = serialiseGroups gs
    json.TrimStart().[0]                              |> should equal '['
    json.TrimEnd().[json.TrimEnd().Length - 1]        |> should equal ']'
    // Both group names should appear in the output
    json.Contains("g1") |> should be True
    json.Contains("g2") |> should be True

// -- FileRuleStore ----------------------------------------------------------

let private withTempStore (f : IRuleStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        f (FileRuleStore dir :> IRuleStore, TenantId "tenant")
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``FileRuleStore List returns empty for a fresh tenant`` () =
    withTempStore (fun (s, tid) ->
        s.List tid |> should haveLength 0)

[<Fact>]
let ``FileRuleStore Upsert then List returns the group`` () =
    withTempStore (fun (s, tid) ->
        let g = makeGroup "ops" [| makeRule "cpu" PromQL "cpu" Gt 0.5 |]
        s.Upsert(tid, g)
        let gs = s.List tid
        gs |> should haveLength 1
        gs.[0].name |> should equal "ops")

[<Fact>]
let ``FileRuleStore TryGet returns None for unknown id`` () =
    withTempStore (fun (s, tid) ->
        s.TryGet(tid, "ghost") |> should equal None)

[<Fact>]
let ``FileRuleStore TryGet returns Some after Upsert`` () =
    withTempStore (fun (s, tid) ->
        let g = makeGroup "grp" [||]
        s.Upsert(tid, g)
        s.TryGet(tid, g.id) |> Option.map (fun x -> x.id) |> should equal (Some g.id))

[<Fact>]
let ``FileRuleStore Delete returns false for unknown id`` () =
    withTempStore (fun (s, tid) ->
        s.Delete(tid, "ghost") |> should be False)

[<Fact>]
let ``FileRuleStore Delete removes the group and returns true`` () =
    withTempStore (fun (s, tid) ->
        let g = makeGroup "del" [||]
        s.Upsert(tid, g)
        s.Delete(tid, g.id)  |> should be True
        s.List tid           |> should haveLength 0)

[<Fact>]
let ``FileRuleStore persists across reconstruction from same directory`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        let g = makeGroup "persisted" [| makeRule "r1" PromQL "mem" Lte 0.8 |]
        let s1 = FileRuleStore dir :> IRuleStore
        s1.Upsert(TenantId "t", g)
        // Construct a second store over the same directory
        let s2 = FileRuleStore dir :> IRuleStore
        s2.TryGet(TenantId "t", g.id)
        |> Option.map (fun x -> x.name)
        |> should equal (Some "persisted")
    finally
        try Directory.Delete(dir, true) with _ -> ()
