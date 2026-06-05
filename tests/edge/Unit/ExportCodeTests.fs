module PulseBoard.Tests.Unit.ExportCodeTests

open System
open Xunit
open FsUnit.Xunit
open PulseBoard.ExportCode

// -- fixtures ---------------------------------------------------------------

let private panel id : PulseBoard.Dashboards.Panel =
    { id = id; title = "CPU \"usage\""; panelType = "timeseries"
      queryLang = "promql"; expr = "rate(cpu[5m]) > ${threshold}"
      x = 0; y = 0; w = 12; h = 8
      options = Map [ "unit", "percent" ] }

let private dashboard : PulseBoard.Dashboards.Dashboard =
    { id = "ops-overview"; title = "Ops Overview"
      timeRangeSec = 3600; refreshSec = 15
      panels = [| panel "p1" |]
      vars = "[]"
      createdAt = DateTimeOffset.UnixEpoch; updatedAt = DateTimeOffset.UnixEpoch }

let private rule : PulseBoard.Rules.Rule =
    { id = "r1"; name = "High CPU"; lang = PulseBoard.Rules.PromQL
      expr = "avg(cpu)"; cmp = PulseBoard.Rules.Gt; threshold = 0.9
      forMs = 60000L; severity = PulseBoard.Rules.Severity.Critical
      labels = Map [ "team", "sre" ]; annotations = Map [ "summary", "hot" ]
      runbook = Some "restart it" }

let private group : PulseBoard.Rules.RuleGroup =
    { id = "infra"; name = "Infra"; intervalMs = 15000L
      rules = [| rule |]
      createdAt = DateTimeOffset.UnixEpoch; updatedAt = DateTimeOffset.UnixEpoch }

let private routing : PulseBoard.Routing.Config =
    { route =
        { id = "root"; matchers = [||]; receiverId = Some "rcv1"; policyId = None
          groupBy = [| "alertname"; "service" |]
          groupWaitMs = 30000L; groupIntervalMs = 300000L; repeatIntervalMs = 3600000L
          continue_ = false; muteTimeIds = [||]
          children =
            [| { id = "child"; receiverId = Some "rcv1"; policyId = None
                 matchers = [| { name = "severity"; op = PulseBoard.Routing.MEq
                                 value = "critical"; re = None } |]
                 groupBy = [||]; groupWaitMs = 0L; groupIntervalMs = 0L
                 repeatIntervalMs = 0L; continue_ = true; muteTimeIds = [||]
                 children = [||] } |] }
      receivers =
        [| { id = "rcv1"; name = "Slack"; type_ = "slack"
             url = Some "https://hooks.slack.com/x"; secret = None
             extra = Map [ "channel", "#alerts" ] } |]
      silences = [||]; inhibitions = [||]; muteTimes = [||] }

// -- dashboard --------------------------------------------------------------

[<Fact>]
let ``dashboard YAML carries id title and panel`` () =
    let y = dashboardToYaml dashboard
    y |> should haveSubstring "kind: Dashboard"
    y |> should haveSubstring "id: \"ops-overview\""
    y |> should haveSubstring "title: \"Ops Overview\""
    y |> should haveSubstring "timeRangeSec: 3600"
    y |> should haveSubstring "type: \"timeseries\""
    y |> should haveSubstring "gridPos: { x: 0, y: 0, w: 12, h: 8 }"
    // embedded quotes are escaped, never raw
    y |> should haveSubstring "title: \"CPU \\\"usage\\\"\""

[<Fact>]
let ``dashboard TF escapes interpolation and quotes`` () =
    let tf = dashboardToTf dashboard
    tf |> should haveSubstring "resource \"pulseboard_dashboard\" \"ops_overview\""
    tf |> should haveSubstring "time_range_sec = 3600"
    tf |> should haveSubstring "panel {"
    // ${ must be neutralised to $${ so HCL does not interpolate
    tf |> should haveSubstring "$${threshold}"
    tf |> should not' (haveSubstring "[5m]) > ${threshold}")

// -- rule group -------------------------------------------------------------

[<Fact>]
let ``rule group YAML carries cmp severity and threshold`` () =
    let y = ruleGroupToYaml group
    y |> should haveSubstring "kind: RuleGroup"
    y |> should haveSubstring "name: \"Infra\""
    y |> should haveSubstring "cmp: \">\""
    y |> should haveSubstring "severity: \"critical\""
    y |> should haveSubstring "threshold: 0.9"
    y |> should haveSubstring "lang: \"promql\""
    y |> should haveSubstring "runbook: \"restart it\""

[<Fact>]
let ``rule group TF carries rule block`` () =
    let tf = ruleGroupToTf group
    tf |> should haveSubstring "resource \"pulseboard_rule_group\" \"infra\""
    tf |> should haveSubstring "rule {"
    tf |> should haveSubstring "cmp       = \">\""
    tf |> should haveSubstring "threshold = 0.9"
    tf |> should haveSubstring "labels = {"

// -- routing ----------------------------------------------------------------

[<Fact>]
let ``routing YAML carries receiver and route tree`` () =
    let y = routingToYaml routing
    y |> should haveSubstring "kind: Routing"
    y |> should haveSubstring "type: \"slack\""
    y |> should haveSubstring "groupWaitMs: 30000"
    y |> should haveSubstring "severity=critical"

[<Fact>]
let ``routing TF carries receiver and route resources`` () =
    let tf = routingToTf routing
    tf |> should haveSubstring "resource \"pulseboard_receiver\" \"rcv1\""
    tf |> should haveSubstring "resource \"pulseboard_route\" \"root\""
    tf |> should haveSubstring "matchers = [\"severity=critical\"]"
    tf |> should haveSubstring "receiver_id = \"rcv1\""
