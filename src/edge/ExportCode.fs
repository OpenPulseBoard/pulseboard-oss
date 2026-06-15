module PulseBoard.ExportCode

// Export-as-code. Pure serialisers that render a
// dashboard / rule-group / routing config as either Terraform (HCL,
// matching the `pulseboard/pulseboard` provider schema) or YAML (the
// git-sync on-disk surface). Exposed read-only over
// `GET /api/export/...` and surfaced as "copy as Terraform / YAML"
// buttons in the UI.

open System
open System.Text
open Suave
open Suave.Operators
open PulseBoard.Tenancy

// -- escaping helpers -------------------------------------------------------

/// Double-quoted YAML scalar with JSON-style escapes (always quoting
/// keeps us safe from YAML's many special leading characters).
let private yq (s : string) : string =
  let sb = StringBuilder()
  sb.Append('"') |> ignore
  for c in s do
    match c with
    | '"'  -> sb.Append "\\\"" |> ignore
    | '\\' -> sb.Append "\\\\" |> ignore
    | '\n' -> sb.Append "\\n" |> ignore
    | '\r' -> sb.Append "\\r" |> ignore
    | '\t' -> sb.Append "\\t" |> ignore
    | c    -> sb.Append c |> ignore
  sb.Append('"') |> ignore
  sb.ToString()

/// Double-quoted HCL string. Escapes backslash/quote/newlines and the
/// `${`/`%{` interpolation sequences so literal content is preserved.
let private hq (s : string) : string =
  let e =
    s.Replace("\\", "\\\\")
     .Replace("\"", "\\\"")
     .Replace("\n", "\\n")
     .Replace("\r", "\\r")
     .Replace("\t", "\\t")
     .Replace("${", "$${")
     .Replace("%{", "%%{")
  "\"" + e + "\""

/// Sanitise an arbitrary id into a valid HCL resource-name token.
let private hclName (s : string) : string =
  let chars =
    s |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '_' || c = '-' then c else '_')
      |> Seq.toArray
  let n = String(chars).Replace("-", "_")
  if n.Length = 0 then "r"
  elif Char.IsDigit n.[0] then "r_" + n
  else n

// -- string-map serialisers -------------------------------------------------

let private yamlMap (indent : string) (m : Map<string, string>) : string list =
  m |> Map.toList |> List.map (fun (k, v) -> sprintf "%s%s: %s" indent (yq k) (yq v))

let private hclMap (indent : string) (m : Map<string, string>) : string list =
  m |> Map.toList |> List.map (fun (k, v) -> sprintf "%s%s = %s" indent (hq k) (hq v))

// -- dashboards -------------------------------------------------------------

let dashboardToYaml (d : PulseBoard.Dashboards.Dashboard) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  line "apiVersion: pulseboard/v1"
  line "kind: Dashboard"
  line "metadata:"
  line (sprintf "  id: %s" (yq d.id))
  line (sprintf "  title: %s" (yq d.title))
  line "spec:"
  line (sprintf "  timeRangeSec: %d" d.timeRangeSec)
  line (sprintf "  refreshSec: %d" d.refreshSec)
  line (sprintf "  vars: %s" (yq d.vars))
  if d.panels.Length = 0 then
    line "  panels: []"
  else
    line "  panels:"
    for p in d.panels do
      line (sprintf "    - id: %s" (yq p.id))
      line (sprintf "      title: %s" (yq p.title))
      line (sprintf "      type: %s" (yq p.panelType))
      line (sprintf "      queryLang: %s" (yq p.queryLang))
      line (sprintf "      expr: %s" (yq p.expr))
      line (sprintf "      gridPos: { x: %d, y: %d, w: %d, h: %d }" p.x p.y p.w p.h)
      if p.options.IsEmpty then
        line "      options: {}"
      else
        line "      options:"
        for l in yamlMap "        " p.options do line l
  sb.ToString().TrimEnd() + "\n"

let dashboardToTf (d : PulseBoard.Dashboards.Dashboard) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  line (sprintf "resource \"pulseboard_dashboard\" %s {" (hq (hclName d.id)))
  line (sprintf "  id             = %s" (hq d.id))
  line (sprintf "  title          = %s" (hq d.title))
  line (sprintf "  time_range_sec = %d" d.timeRangeSec)
  line (sprintf "  refresh_sec    = %d" d.refreshSec)
  line (sprintf "  vars           = %s" (hq d.vars))
  for p in d.panels do
    line ""
    line "  panel {"
    line (sprintf "    id         = %s" (hq p.id))
    line (sprintf "    title      = %s" (hq p.title))
    line (sprintf "    type       = %s" (hq p.panelType))
    line (sprintf "    query_lang = %s" (hq p.queryLang))
    line (sprintf "    expr       = %s" (hq p.expr))
    line (sprintf "    x          = %d" p.x)
    line (sprintf "    y          = %d" p.y)
    line (sprintf "    w          = %d" p.w)
    line (sprintf "    h          = %d" p.h)
    if not p.options.IsEmpty then
      line "    options = {"
      for l in hclMap "      " p.options do line l
      line "    }"
    line "  }"
  line "}"
  sb.ToString()

// -- rule groups ------------------------------------------------------------

let private cmpStr =
  function
  | PulseBoard.Rules.Gt -> ">"  | PulseBoard.Rules.Lt -> "<"
  | PulseBoard.Rules.Gte -> ">=" | PulseBoard.Rules.Lte -> "<="
  | PulseBoard.Rules.Eq -> "==" | PulseBoard.Rules.Neq -> "!="

let private langStr =
  function
  | PulseBoard.Rules.PromQL -> "promql"
  | PulseBoard.Rules.LogQL -> "logql"
  | PulseBoard.Rules.Budget -> "budget"

let ruleGroupToYaml (g : PulseBoard.Rules.RuleGroup) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  line "apiVersion: pulseboard/v1"
  line "kind: RuleGroup"
  line "metadata:"
  line (sprintf "  id: %s" (yq g.id))
  line (sprintf "  name: %s" (yq g.name))
  line "spec:"
  line (sprintf "  intervalMs: %d" g.intervalMs)
  if g.rules.Length = 0 then
    line "  rules: []"
  else
    line "  rules:"
    for r in g.rules do
      line (sprintf "    - id: %s" (yq r.id))
      line (sprintf "      name: %s" (yq r.name))
      line (sprintf "      lang: %s" (yq (langStr r.lang)))
      line (sprintf "      expr: %s" (yq r.expr))
      line (sprintf "      cmp: %s" (yq (cmpStr r.cmp)))
      line (sprintf "      threshold: %s" (r.threshold.ToString(Globalization.CultureInfo.InvariantCulture)))
      line (sprintf "      forMs: %d" r.forMs)
      line (sprintf "      severity: %s" (yq (PulseBoard.Rules.severityToStr r.severity)))
      if r.labels.IsEmpty then line "      labels: {}"
      else
        line "      labels:"
        for l in yamlMap "        " r.labels do line l
      if r.annotations.IsEmpty then line "      annotations: {}"
      else
        line "      annotations:"
        for l in yamlMap "        " r.annotations do line l
      match r.runbook with
      | Some rb -> line (sprintf "      runbook: %s" (yq rb))
      | None -> ()
  sb.ToString().TrimEnd() + "\n"

let ruleGroupToTf (g : PulseBoard.Rules.RuleGroup) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  line (sprintf "resource \"pulseboard_rule_group\" %s {" (hq (hclName g.id)))
  line (sprintf "  id          = %s" (hq g.id))
  line (sprintf "  name        = %s" (hq g.name))
  line (sprintf "  interval_ms = %d" g.intervalMs)
  for r in g.rules do
    line ""
    line "  rule {"
    line (sprintf "    id        = %s" (hq r.id))
    line (sprintf "    name      = %s" (hq r.name))
    line (sprintf "    lang      = %s" (hq (langStr r.lang)))
    line (sprintf "    expr      = %s" (hq r.expr))
    line (sprintf "    cmp       = %s" (hq (cmpStr r.cmp)))
    line (sprintf "    threshold = %s" (r.threshold.ToString(Globalization.CultureInfo.InvariantCulture)))
    line (sprintf "    for_ms    = %d" r.forMs)
    line (sprintf "    severity  = %s" (hq (PulseBoard.Rules.severityToStr r.severity)))
    if not r.labels.IsEmpty then
      line "    labels = {"
      for l in hclMap "      " r.labels do line l
      line "    }"
    if not r.annotations.IsEmpty then
      line "    annotations = {"
      for l in hclMap "      " r.annotations do line l
      line "    }"
    match r.runbook with
    | Some rb -> line (sprintf "    runbook   = %s" (hq rb))
    | None -> ()
    line "  }"
  line "}"
  sb.ToString()

// -- routing ----------------------------------------------------------------

let private matchOpStr =
  function
  | PulseBoard.Routing.MEq -> "=" | PulseBoard.Routing.MNeq -> "!="
  | PulseBoard.Routing.MRe -> "=~" | PulseBoard.Routing.MNRe -> "!~"

let routingToYaml (c : PulseBoard.Routing.Config) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  line "apiVersion: pulseboard/v1"
  line "kind: Routing"
  line "spec:"
  if c.receivers.Length = 0 then line "  receivers: []"
  else
    line "  receivers:"
    for rcv in c.receivers do
      line (sprintf "    - id: %s" (yq rcv.id))
      line (sprintf "      name: %s" (yq rcv.name))
      line (sprintf "      type: %s" (yq rcv.type_))
      match rcv.url with Some u -> line (sprintf "      url: %s" (yq u)) | None -> ()
      if not rcv.extra.IsEmpty then
        line "      extra:"
        for l in yamlMap "        " rcv.extra do line l
  let rec emitRoute (indent : string) (r : PulseBoard.Routing.Route) =
    line (sprintf "%sid: %s" indent (yq r.id))
    match r.receiverId with Some rid -> line (sprintf "%sreceiverId: %s" indent (yq rid)) | None -> ()
    match r.policyId with Some pid -> line (sprintf "%spolicyId: %s" indent (yq pid)) | None -> ()
    if r.matchers.Length > 0 then
      line (sprintf "%smatchers:" indent)
      for m in r.matchers do
        line (sprintf "%s  - %s" indent (yq (sprintf "%s%s%s" m.name (matchOpStr m.op) m.value)))
    if r.groupBy.Length > 0 then
      line (sprintf "%sgroupBy: [%s]" indent (r.groupBy |> Array.map yq |> String.concat ", "))
    line (sprintf "%sgroupWaitMs: %d" indent r.groupWaitMs)
    line (sprintf "%sgroupIntervalMs: %d" indent r.groupIntervalMs)
    line (sprintf "%srepeatIntervalMs: %d" indent r.repeatIntervalMs)
    line (sprintf "%scontinue: %b" indent r.continue_)
    if r.children.Length > 0 then
      line (sprintf "%sroutes:" indent)
      for child in r.children do
        line (sprintf "%s  -" indent)
        emitRoute (indent + "    ") child
  line "  route:"
  emitRoute "    " c.route
  sb.ToString().TrimEnd() + "\n"

let routingToTf (c : PulseBoard.Routing.Config) : string =
  let sb = StringBuilder()
  let line (s : string) = sb.AppendLine s |> ignore
  for rcv in c.receivers do
    line (sprintf "resource \"pulseboard_receiver\" %s {" (hq (hclName rcv.id)))
    line (sprintf "  id   = %s" (hq rcv.id))
    line (sprintf "  name = %s" (hq rcv.name))
    line (sprintf "  type = %s" (hq rcv.type_))
    match rcv.url with Some u -> line (sprintf "  url  = %s" (hq u)) | None -> ()
    if not rcv.extra.IsEmpty then
      line "  extra = {"
      for l in hclMap "    " rcv.extra do line l
      line "  }"
    line "}"
    line ""
  let rec emitRoute (indent : string) (label : string) (r : PulseBoard.Routing.Route) =
    line (sprintf "%s%s {" indent label)
    let i2 = indent + "  "
    line (sprintf "%sid = %s" i2 (hq r.id))
    match r.receiverId with Some rid -> line (sprintf "%sreceiver_id = %s" i2 (hq rid)) | None -> ()
    match r.policyId with Some pid -> line (sprintf "%spolicy_id = %s" i2 (hq pid)) | None -> ()
    if r.matchers.Length > 0 then
      let ms = r.matchers |> Array.map (fun m -> hq (sprintf "%s%s%s" m.name (matchOpStr m.op) m.value))
      line (sprintf "%smatchers = [%s]" i2 (String.concat ", " ms))
    if r.groupBy.Length > 0 then
      line (sprintf "%sgroup_by = [%s]" i2 (r.groupBy |> Array.map hq |> String.concat ", "))
    line (sprintf "%sgroup_wait_ms = %d" i2 r.groupWaitMs)
    line (sprintf "%sgroup_interval_ms = %d" i2 r.groupIntervalMs)
    line (sprintf "%srepeat_interval_ms = %d" i2 r.repeatIntervalMs)
    line (sprintf "%scontinue = %b" i2 r.continue_)
    for child in r.children do
      emitRoute i2 "route" child
    line (sprintf "%s}" indent)
  emitRoute "" "resource \"pulseboard_route\" \"root\"" c.route
  sb.ToString().TrimEnd() + "\n"

// -- web part ---------------------------------------------------------------

let private fmtOf (ctx : HttpContext) : string =
  ctx.request.queryParam "format"
  |> function Choice1Of2 v -> v.ToLowerInvariant() | _ -> "yaml"

let private respond (fmt : string) (tf : unit -> string) (yaml : unit -> string) : WebPart =
  match fmt with
  | "tf" | "hcl" | "terraform" ->
    Suave.Successful.OK (tf ()) >=> Writers.setMimeType "text/plain; charset=utf-8"
  | _ ->
    Suave.Successful.OK (yaml ()) >=> Writers.setMimeType "application/yaml; charset=utf-8"

/// Read-only `GET /api/export/...` endpoints. `resolveTenant` maps the
/// request context to the tenant whose resources are being exported.
let webPart
    (resolveTenant : HttpContext -> TenantId)
    (dashRepo : PulseBoard.Dashboards.IDashboardRepo)
    (ruleStore : PulseBoard.Rules.IRuleStore)
    (routingStore : PulseBoard.Routing.IConfigStore) : WebPart =
  Suave.Filters.GET >=>
  Suave.WebPart.choose [
    Suave.Filters.pathScan "/api/export/dashboards/%s" (fun id ->
      fun ctx ->
        async {
          let tid = resolveTenant ctx
          match dashRepo.TryGet(tid, id) with
          | Some d -> return! respond (fmtOf ctx) (fun () -> dashboardToTf d) (fun () -> dashboardToYaml d) ctx
          | None -> return! Suave.RequestErrors.NOT_FOUND "dashboard not found" ctx
        })
    Suave.Filters.pathScan "/api/export/rules/%s" (fun id ->
      fun ctx ->
        async {
          let tid = resolveTenant ctx
          match ruleStore.TryGet(tid, id) with
          | Some g -> return! respond (fmtOf ctx) (fun () -> ruleGroupToTf g) (fun () -> ruleGroupToYaml g) ctx
          | None -> return! Suave.RequestErrors.NOT_FOUND "rule group not found" ctx
        })
    Suave.Filters.path "/api/export/routing" >=>
      fun ctx ->
        async {
          let tid = resolveTenant ctx
          let c = routingStore.Get tid
          return! respond (fmtOf ctx) (fun () -> routingToTf c) (fun () -> routingToYaml c) ctx
        }
  ]
