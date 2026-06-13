module PulseBoard.GitSync

// GitOps "git-sync" mode. When configured, the
// workspace treats a Git repository as the source of truth for
// dashboards and rule groups: every `intervalMs` it pulls the repo,
// reads `<path>/dashboards/*.json` and `<path>/rules/*.json`, and
// reconciles those into the live stores (upserting everything present
// and pruning anything that no longer exists in Git). While git-sync
// is active the dashboard/rule CRUD APIs return `405 Method Not
// Allowed` so operators are forced through Git.
//
// The resource id is the file name (without `.json`) so a given file
// maps to a stable id across syncs regardless of any `id` embedded in
// the body — this keeps reconciliation idempotent.
//
// We shell out to the `git` CLI rather than depend on libgit2; auth
// supports HTTPS personal-access-tokens (injected into the fetch URL,
// never persisted to the on-disk remote) and SSH keys (via
// `GIT_SSH_COMMAND`).

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading
open Suave
open Suave.Operators
open PulseBoard.Tenancy

let private logInfo (msg : string) =
  printfn "[gitsync] %s" msg

let private logWarn (msg : string) =
  eprintfn "[gitsync] WARN %s" msg

let private logErr (msg : string) =
  eprintfn "[gitsync] ERROR %s" msg

// -- config -----------------------------------------------------------------

type Config =
  { url        : string
    branch     : string
    /// Sub-directory within the repo that contains `dashboards/` and
    /// `rules/`. Empty means the repo root.
    subPath    : string
    intervalMs : int
    /// Path to a private SSH key for `git@…` style URLs.
    sshKeyPath : string option
    /// Resolved HTTPS token (already read from its env var).
    token      : string option
    /// Local checkout directory (under the data dir).
    workDir    : string
    /// When true (default) prune store entries not present in Git.
    prune      : bool }

type SyncReport =
  { commit     : string
    dashboards : int
    rules      : int
    deleted    : int
    unchanged  : bool
    errors     : string list }

// -- pure reconcile helpers -------------------------------------------------

/// Read and parse the desired dashboards/rule-groups from a checkout
/// root. The id of each item is taken from its file name (without the
/// `.json` extension), making it stable across syncs. Parse failures
/// are collected into the returned error list rather than aborting.
let readDesired (root : string)
    : (string * PulseBoard.Dashboards.Dashboard) list
      * (string * PulseBoard.Rules.RuleGroup) list
      * string list =
  let errors = ResizeArray<string>()
  let dashDir = Path.Combine(root, "dashboards")
  let ruleDir = Path.Combine(root, "rules")
  let dashboards =
    if Directory.Exists dashDir then
      [ for f in Directory.EnumerateFiles(dashDir, "*.json") |> Seq.sortBy Path.GetFileName do
          let id = Path.GetFileNameWithoutExtension f
          match PulseBoard.Dashboards.parseDashboard (File.ReadAllText f) with
          | Result.Ok d -> yield id, d
          | Result.Error e ->
            let msg = sprintf "dashboards/%s: %s" (Path.GetFileName f) e
            logWarn msg
            errors.Add msg ]
    else
      logInfo (sprintf "no dashboards/ directory under %s (skipping)" root)
      []
  let groups =
    if Directory.Exists ruleDir then
      [ for f in Directory.EnumerateFiles(ruleDir, "*.json") |> Seq.sortBy Path.GetFileName do
          let id = Path.GetFileNameWithoutExtension f
          match PulseBoard.Rules.parseGroup (File.ReadAllText f) with
          | Result.Ok g -> yield id, g
          | Result.Error e ->
            let msg = sprintf "rules/%s: %s" (Path.GetFileName f) e
            logWarn msg
            errors.Add msg ]
    else
      logInfo (sprintf "no rules/ directory under %s (skipping)" root)
      []
  List.ofSeq dashboards, List.ofSeq groups, List.ofSeq errors

/// Reconcile the desired set into the live stores: upsert everything
/// present in Git, then (when pruning) delete any store entry whose id
/// is not in the desired set. Returns the number of entries deleted.
let applyDesired
    (dashRepo : PulseBoard.Dashboards.IDashboardRepo)
    (ruleStore : PulseBoard.Rules.IRuleStore)
    (tid : TenantId)
    (prune : bool)
    (dashboards : (string * PulseBoard.Dashboards.Dashboard) list)
    (groups : (string * PulseBoard.Rules.RuleGroup) list) : int =
  let dashIds = dashboards |> List.map fst |> Set.ofList
  let ruleIds = groups |> List.map fst |> Set.ofList
  for (id, d) in dashboards do
    dashRepo.Upsert(tid, { d with id = id })
  for (id, g) in groups do
    ruleStore.Upsert(tid, { g with id = id })
  let mutable deleted = 0
  if prune then
    for d in dashRepo.List tid do
      if not (Set.contains d.id dashIds) then
        if dashRepo.Delete(tid, d.id) then deleted <- deleted + 1
    for g in ruleStore.List tid do
      if not (Set.contains g.id ruleIds) then
        if ruleStore.Delete(tid, g.id) then deleted <- deleted + 1
  deleted

// -- git plumbing -----------------------------------------------------------

let private runGit (workDir : string option) (env : (string * string) list) (args : string list)
    : Result<string, string> =
  try
    let psi = ProcessStartInfo("git")
    args |> List.iter psi.ArgumentList.Add
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    match workDir with
    | Some d -> psi.WorkingDirectory <- d
    | None -> ()
    for (k, v) in env do psi.Environment.[k] <- v
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode = 0 then Result.Ok (out.Trim())
    else Result.Error (sprintf "git %s failed (exit %d): %s"
                         (List.tryHead args |> Option.defaultValue "") p.ExitCode (err.Trim()))
  with ex -> Result.Error ("git invocation failed: " + ex.Message)

let private gitEnv (cfg : Config) : (string * string) list =
  let baseEnv = [ "GIT_TERMINAL_PROMPT", "0" ]
  match cfg.sshKeyPath with
  | Some key ->
    ("GIT_SSH_COMMAND",
     sprintf "ssh -i %s -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new -o BatchMode=yes" key)
    :: baseEnv
  | None -> baseEnv

/// HTTPS URL with the token injected. Used only for the transient
/// fetch/clone invocation; never written to the persisted remote.
let private authedUrl (cfg : Config) : string =
  match cfg.token with
  | Some t when cfg.url.StartsWith "https://" ->
    "https://x-access-token:" + Uri.EscapeDataString t + "@" + cfg.url.Substring("https://".Length)
  | _ -> cfg.url

let private ensureCheckout (cfg : Config) : Result<unit, string> =
  let env = gitEnv cfg
  let gitDir = Path.Combine(cfg.workDir, ".git")
  if Directory.Exists gitDir then
    logInfo (sprintf "fetching %s (branch=%s) into %s" cfg.url cfg.branch cfg.workDir)
    match runGit (Some cfg.workDir) env [ "fetch"; "--depth"; "1"; authedUrl cfg; cfg.branch ] with
    | Result.Error e ->
      logErr (sprintf "fetch failed: %s" e)
      Result.Error e
    | Result.Ok _ ->
      match runGit (Some cfg.workDir) env [ "reset"; "--hard"; "FETCH_HEAD" ] with
      | Result.Error e ->
        logErr (sprintf "reset --hard FETCH_HEAD failed: %s" e)
        Result.Error e
      | Result.Ok _ -> Result.Ok ()
  else
    let parent = Path.GetDirectoryName cfg.workDir
    if not (String.IsNullOrEmpty parent) then Directory.CreateDirectory parent |> ignore
    logInfo (sprintf "cloning %s (branch=%s) into %s" cfg.url cfg.branch cfg.workDir)
    match runGit None env
            [ "clone"; "--depth"; "1"; "--single-branch"; "--branch"; cfg.branch
              authedUrl cfg; cfg.workDir ] with
    | Result.Error e ->
      logErr (sprintf "clone failed: %s" e)
      Result.Error e
    | Result.Ok _ ->
      // Scrub any embedded token from the persisted remote.
      runGit (Some cfg.workDir) env [ "remote"; "set-url"; "origin"; cfg.url ] |> ignore
      logInfo "clone OK"
      Result.Ok ()

let private currentCommit (cfg : Config) : Result<string, string> =
  runGit (Some cfg.workDir) (gitEnv cfg) [ "rev-parse"; "HEAD" ]

// -- syncer -----------------------------------------------------------------

type Syncer
    ( cfg : Config,
      dashRepo : PulseBoard.Dashboards.IDashboardRepo,
      ruleStore : PulseBoard.Rules.IRuleStore,
      targetTenant : unit -> TenantId ) =

  let cts = new CancellationTokenSource()
  let mutable lastCommit = ""
  let mutable lastReport : SyncReport option = None
  let mutable lastError : string option = None

  member _.LastReport = lastReport
  member _.LastError = lastError
  member _.LastCommit = lastCommit
  member _.Config = cfg

  /// Pull once and reconcile. When the commit is unchanged since the
  /// last successful sync the reconcile is skipped (unless `force`).
  member _.SyncOnce(?force : bool) : Result<SyncReport, string> =
    let force = defaultArg force false
    match ensureCheckout cfg with
    | Result.Error e -> lastError <- Some e; Result.Error e
    | Result.Ok () ->
      match currentCommit cfg with
      | Result.Error e ->
        logErr (sprintf "could not resolve HEAD: %s" e)
        lastError <- Some e
        Result.Error e
      | Result.Ok commit ->
        if commit = lastCommit && not force then
          logInfo (sprintf "commit %s unchanged; skipping reconcile" (commit.Substring(0, min 7 commit.Length)))
          let r = { commit = commit; dashboards = 0; rules = 0; deleted = 0
                    unchanged = true; errors = [] }
          lastReport <- Some r; lastError <- None
          Result.Ok r
        else
          let root =
            if String.IsNullOrWhiteSpace cfg.subPath then cfg.workDir
            else Path.Combine(cfg.workDir, cfg.subPath)
          let shortSha = commit.Substring(0, min 7 commit.Length)
          logInfo (sprintf "reconciling commit %s (path=%s)" shortSha (if cfg.subPath = "" then "/" else cfg.subPath))
          if not (Directory.Exists root) then
            let msg = sprintf "subPath %s not found in checkout %s" cfg.subPath cfg.workDir
            logErr msg
            lastError <- Some msg
            Result.Error msg
          else
            let dashboards, groups, errs = readDesired root
            let tid = targetTenant ()
            let (PulseBoard.Tenancy.TenantId tidStr) = tid
            logInfo (sprintf "found %d dashboard(s), %d rule group(s) on disk (tenant=%s, prune=%b)"
              (List.length dashboards) (List.length groups) tidStr cfg.prune)
            let deleted = applyDesired dashRepo ruleStore tid cfg.prune dashboards groups
            lastCommit <- commit
            let r = { commit = commit; dashboards = List.length dashboards
                      rules = List.length groups; deleted = deleted
                      unchanged = false; errors = errs }
            lastReport <- Some r; lastError <- None
            logInfo (sprintf "reconcile DONE (upserted %d dashboards, %d rules; deleted %d; %d parse error(s))"
              r.dashboards r.rules r.deleted (List.length r.errors))
            Result.Ok r

  member this.Start() =
    logInfo (sprintf "starting background syncer (interval=%dms)" cfg.intervalMs)
    let worker =
      async {
        while not cts.Token.IsCancellationRequested do
          try
            this.SyncOnce() |> ignore
          with ex ->
            logErr (sprintf "background SyncOnce threw: %s" ex.Message)
            lastError <- Some ex.Message
          do! Async.Sleep cfg.intervalMs
      }
    Async.Start(worker, cts.Token)

  member _.Stop() = try cts.Cancel() with _ -> ()

// -- read-only guard --------------------------------------------------------

let private isManaged (p : string) =
  p.StartsWith "/api/dashboards" || p.StartsWith "/api/rules"

/// Intercepts mutating requests (POST/PUT/DELETE) on the git-managed
/// CRUD surfaces and returns 405. Prepend this to the inner route
/// `choose` so it runs before the real handlers.
let readOnlyGuard : WebPart =
  fun ctx ->
    async {
      let m = ctx.request.method
      let mutating =
        m = HttpMethod.POST || m = HttpMethod.PUT ||
        m = HttpMethod.DELETE || m = HttpMethod.PATCH
      if mutating && isManaged ctx.request.path then
        return!
          (Suave.RequestErrors.METHOD_NOT_ALLOWED
             """{"error":"git-sync mode is active; dashboards and rules are managed from Git and are read-only via the API"}"""
           >=> Writers.setMimeType "application/json")
            ctx
      else
        return None
    }

// -- status endpoint --------------------------------------------------------

let private statusJson (syncer : Syncer) : string =
  use ms = new IO.MemoryStream()
  use w = new Utf8JsonWriter(ms)
  w.WriteStartObject()
  w.WriteBoolean("enabled", true)
  w.WriteString("url", syncer.Config.url)
  w.WriteString("branch", syncer.Config.branch)
  w.WriteString("path", syncer.Config.subPath)
  w.WriteNumber("intervalMs", syncer.Config.intervalMs)
  w.WriteBoolean("prune", syncer.Config.prune)
  (match syncer.LastCommit with
   | "" -> w.WriteNull("commit")
   | c -> w.WriteString("commit", c))
  (match syncer.LastReport with
   | Some r ->
     w.WritePropertyName "lastSync"
     w.WriteStartObject()
     w.WriteNumber("dashboards", r.dashboards)
     w.WriteNumber("rules", r.rules)
     w.WriteNumber("deleted", r.deleted)
     w.WriteBoolean("unchanged", r.unchanged)
     w.WritePropertyName "errors"
     w.WriteStartArray()
     for e in r.errors do w.WriteStringValue e
     w.WriteEndArray()
     w.WriteEndObject()
   | None -> w.WriteNull("lastSync"))
  (match syncer.LastError with
   | Some e -> w.WriteString("error", e)
   | None -> w.WriteNull("error"))
  w.WriteEndObject()
  w.Flush()
  Text.Encoding.UTF8.GetString(ms.ToArray())

/// `GET /api/gitops/status` — read-only view of the git-sync state so
/// the SPA can surface a "managed from Git" banner.
let statusWebPart (syncer : Syncer) : WebPart =
  Suave.Filters.GET >=> Suave.Filters.path "/api/gitops/status" >=>
    fun ctx ->
      async {
        return!
          (Suave.Successful.OK (statusJson syncer)
           >=> Writers.setMimeType "application/json")
            ctx
      }
