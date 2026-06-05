module PulseBoard.Tests.Unit.GitSyncTests

open System
open System.IO
open System.Diagnostics
open Xunit
open FsUnit.Xunit
open PulseBoard.GitSync
open PulseBoard.Tenancy

let private tid = TenantId "t1"

let private tmpDir () =
    let d = Path.Combine(Path.GetTempPath(), "gitsync-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory d |> ignore
    d

let private dashboard id title : PulseBoard.Dashboards.Dashboard =
    { id = id; title = title; timeRangeSec = 3600; refreshSec = 15
      panels = [||]; vars = "[]"
      createdAt = DateTimeOffset.UnixEpoch; updatedAt = DateTimeOffset.UnixEpoch }

let private group id name : PulseBoard.Rules.RuleGroup =
    { id = id; name = name; intervalMs = 15000L; rules = [||]
      createdAt = DateTimeOffset.UnixEpoch; updatedAt = DateTimeOffset.UnixEpoch }

// -- readDesired ------------------------------------------------------------

[<Fact>]
let ``readDesired parses files and derives id from file name`` () =
    let root = tmpDir ()
    Directory.CreateDirectory(Path.Combine(root, "dashboards")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "rules")) |> ignore
    File.WriteAllText(Path.Combine(root, "dashboards", "web.json"),
                      PulseBoard.Dashboards.serialiseDashboard (dashboard "ignored" "Web"))
    File.WriteAllText(Path.Combine(root, "rules", "infra.json"),
                      PulseBoard.Rules.serialiseGroup (group "ignored" "Infra"))
    let dashboards, groups, errors = readDesired root
    errors |> should be Empty
    dashboards |> List.map fst |> should equal [ "web" ]
    groups |> List.map fst |> should equal [ "infra" ]

[<Fact>]
let ``readDesired collects parse errors without aborting`` () =
    let root = tmpDir ()
    Directory.CreateDirectory(Path.Combine(root, "dashboards")) |> ignore
    File.WriteAllText(Path.Combine(root, "dashboards", "good.json"),
                      PulseBoard.Dashboards.serialiseDashboard (dashboard "x" "Good"))
    File.WriteAllText(Path.Combine(root, "dashboards", "bad.json"), "{ not json")
    let dashboards, _, errors = readDesired root
    dashboards |> List.map fst |> should equal [ "good" ]
    errors |> List.length |> should equal 1

// -- applyDesired -----------------------------------------------------------

[<Fact>]
let ``applyDesired upserts desired and prunes the rest`` () =
    let dashRepo = PulseBoard.Dashboards.FileDashboardRepo(tmpDir ()) :> PulseBoard.Dashboards.IDashboardRepo
    let ruleStore = PulseBoard.Rules.FileRuleStore(tmpDir ()) :> PulseBoard.Rules.IRuleStore
    // pre-existing entries that are NOT in the desired set
    dashRepo.Upsert(tid, dashboard "old-dash" "Old")
    ruleStore.Upsert(tid, group "old-rules" "Old")
    let deleted =
        applyDesired dashRepo ruleStore tid true
            [ "web", dashboard "web" "Web" ]
            [ "infra", group "infra" "Infra" ]
    deleted |> should equal 2
    dashRepo.List tid |> Array.map (fun d -> d.id) |> should equal [| "web" |]
    ruleStore.List tid |> Array.map (fun g -> g.id) |> should equal [| "infra" |]

[<Fact>]
let ``applyDesired without prune keeps existing entries`` () =
    let dashRepo = PulseBoard.Dashboards.FileDashboardRepo(tmpDir ()) :> PulseBoard.Dashboards.IDashboardRepo
    let ruleStore = PulseBoard.Rules.FileRuleStore(tmpDir ()) :> PulseBoard.Rules.IRuleStore
    dashRepo.Upsert(tid, dashboard "keep" "Keep")
    let deleted =
        applyDesired dashRepo ruleStore tid false
            [ "web", dashboard "web" "Web" ] []
    deleted |> should equal 0
    dashRepo.List tid |> Array.map (fun d -> d.id) |> Array.sort |> should equal [| "keep"; "web" |]

// -- full sync against a real local git repo --------------------------------

let private git (workDir : string) (args : string list) =
    let psi = ProcessStartInfo("git")
    args |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start psi
    p.StandardOutput.ReadToEnd() |> ignore
    p.StandardError.ReadToEnd() |> ignore
    p.WaitForExit()
    if p.ExitCode <> 0 then failwithf "git %A failed" args

let private commitFiles (repo : string) (msg : string) (files : (string * string) list) =
    for (rel, content) in files do
        let full = Path.Combine(repo, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
    git repo [ "add"; "-A" ]
    git repo [ "commit"; "-m"; msg ]

[<Fact>]
let ``Syncer clones a repo and reconciles dashboards and rules`` () =
    let repo = tmpDir ()
    git repo [ "init"; "-b"; "main" ]
    git repo [ "config"; "user.email"; "t@t" ]
    git repo [ "config"; "user.name"; "t" ]
    commitFiles repo "init"
        [ "dashboards/web.json", PulseBoard.Dashboards.serialiseDashboard (dashboard "x" "Web")
          "rules/infra.json",    PulseBoard.Rules.serialiseGroup (group "x" "Infra") ]

    let dashRepo = PulseBoard.Dashboards.FileDashboardRepo(tmpDir ()) :> PulseBoard.Dashboards.IDashboardRepo
    let ruleStore = PulseBoard.Rules.FileRuleStore(tmpDir ()) :> PulseBoard.Rules.IRuleStore
    let cfg =
        { url = repo; branch = "main"; subPath = ""; intervalMs = 30000
          sshKeyPath = None; token = None
          workDir = Path.Combine(tmpDir (), "checkout"); prune = true }
    let s = Syncer(cfg, dashRepo, ruleStore, fun () -> tid)

    match s.SyncOnce(force = true) with
    | Result.Ok r ->
        r.dashboards |> should equal 1
        r.rules |> should equal 1
    | Result.Error e -> failwith e
    dashRepo.List tid |> Array.map (fun d -> d.id) |> should equal [| "web" |]
    ruleStore.List tid |> Array.map (fun g -> g.id) |> should equal [| "infra" |]

    // second commit removes the rule file and adds a dashboard → prune + add
    File.Delete(Path.Combine(repo, "rules", "infra.json"))
    commitFiles repo "update"
        [ "dashboards/api.json", PulseBoard.Dashboards.serialiseDashboard (dashboard "x" "Api") ]
    match s.SyncOnce(force = true) with
    | Result.Ok r -> r.deleted |> should equal 1
    | Result.Error e -> failwith e
    dashRepo.List tid |> Array.map (fun d -> d.id) |> Array.sort |> should equal [| "api"; "web" |]
    ruleStore.List tid |> Array.length |> should equal 0
    s.Stop()

// -- read-only guard --------------------------------------------------------

let private runGuard (rawMethod : string) (path : string) =
    let baseCtx = Suave.Http.HttpContext.empty
    let req = { baseCtx.request with rawPath = path; rawMethod = rawMethod }
    let ctx = { baseCtx with request = req }
    readOnlyGuard ctx |> Async.RunSynchronously

[<Fact>]
let ``readOnlyGuard returns 405 for mutating dashboard and rule requests`` () =
    match runGuard "POST" "/api/dashboards" with
    | Some ctx -> int ctx.response.status.code |> should equal 405
    | None -> failwith "expected 405"
    match runGuard "DELETE" "/api/rules/abc" with
    | Some ctx -> int ctx.response.status.code |> should equal 405
    | None -> failwith "expected 405"

[<Fact>]
let ``readOnlyGuard ignores reads and unmanaged paths`` () =
    runGuard "GET" "/api/dashboards" |> Option.isNone |> should equal true
    runGuard "POST" "/api/other" |> Option.isNone |> should equal true
