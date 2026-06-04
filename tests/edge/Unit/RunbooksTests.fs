module PulseBoard.Tests.Unit.RunbooksTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.Runbooks
open PulseBoard.Rules
open PulseBoard.Tenancy

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private withStore (f : IRunbookStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try f (FileRunbookStore dir :> IRunbookStore, TenantId "t1")
    finally try Directory.Delete(dir, true) with _ -> ()

// -- parseSteps -------------------------------------------------------------

[<Fact>]
let ``parseSteps extracts GFM task-list items in order`` () =
    let md = "## Title\n\n- [ ] First\n- [x] Second\nsome prose\n- [ ] Third"
    let steps = parseSteps md
    steps.Length |> should equal 3
    steps.[0].text |> should equal "First"
    steps.[2].idx  |> should equal 2

[<Fact>]
let ``parseSteps falls back to ordered list when no task items`` () =
    let steps = parseSteps "1. alpha\n2. beta"
    steps.Length |> should equal 2
    steps.[1].text |> should equal "beta"

[<Fact>]
let ``parseSteps returns empty for prose-only runbook`` () =
    parseSteps "just some text\nno steps here" |> Array.isEmpty |> should equal true

[<Fact>]
let ``parseSteps returns empty for blank input`` () =
    parseSteps "" |> Array.isEmpty |> should equal true

// -- store roundtrip --------------------------------------------------------

[<Fact>]
let ``FileRunbookStore upsert then get roundtrips`` () =
    withStore (fun (store, tid) ->
        let p =
            { fingerprint = "fp1"; ruleId = "r1"; ruleName = "cpu"
              runbook = "- [ ] a"; stepTexts = [| "a" |]
              firedAt = 1_000L; startedAt = 1_000L; resolvedAt = None
              completions = Map.empty }
        store.Upsert(tid, p)
        match store.Get(tid, "fp1") with
        | Some got -> got.ruleName |> should equal "cpu"
        | None -> failwith "expected record")

[<Fact>]
let ``FileRunbookStore latest write wins on same fingerprint`` () =
    withStore (fun (store, tid) ->
        let baseRec =
            { fingerprint = "fp"; ruleId = "r"; ruleName = "n"
              runbook = ""; stepTexts = [| "a" |]
              firedAt = 0L; startedAt = 0L; resolvedAt = None
              completions = Map.empty }
        store.Upsert(tid, baseRec)
        store.Upsert(tid, { baseRec with resolvedAt = Some 5_000L })
        (store.Get(tid, "fp")).Value.resolvedAt |> should equal (Some 5_000L))

// -- tracker ----------------------------------------------------------------

let private alert (state : AlertState) (rb : string option) : AlertInstance =
    { fingerprint = "afp"; tenantId = TenantId "t1"; ruleId = "r1"
      ruleName = "disk-full"; groupId = "g"; severity = Severity.Warning
      labels = Map.empty; annotations = Map.empty; value = 1.0
      state = state; activeAt = 100L
      firedAt = Some 100L; resolvedAt = (if state = AlertState.Resolved then Some 900L else None)
      lastEvalAt = 100L; runbook = rb }

[<Fact>]
let ``Tracker materialises a record on first firing with runbook`` () =
    withStore (fun (store, tid) ->
        let t = Tracker(store)
        t.Observe(alert AlertState.Firing (Some "- [ ] step one\n- [ ] step two"))
        match store.Get(tid, "afp") with
        | Some p -> p.stepTexts.Length |> should equal 2
        | None -> failwith "expected materialised record")

[<Fact>]
let ``Tracker ignores firing alerts without a runbook`` () =
    withStore (fun (store, tid) ->
        let t = Tracker(store)
        t.Observe(alert AlertState.Firing None)
        store.Get(tid, "afp") |> should equal None)

[<Fact>]
let ``Tracker stamps resolvedAt on resolution`` () =
    withStore (fun (store, tid) ->
        let t = Tracker(store)
        t.Observe(alert AlertState.Firing (Some "- [ ] a"))
        t.Observe(alert AlertState.Resolved (Some "- [ ] a"))
        (store.Get(tid, "afp")).Value.resolvedAt |> should equal (Some 900L))
