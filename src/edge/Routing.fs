module PulseBoard.Routing

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Collections.Concurrent
open System.Threading
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.TimeSeries
open PulseBoard.Rules
open PulseBoard.NotifyQueue

/// Verbose tracing for the alert -> route -> group -> enqueue path.
/// On by default; set PULSE_NOTIFY_DEBUG=0 (or false/off/no) to silence.
/// Lines go to stderr with a `[notify]` prefix so an operator can follow
/// exactly why a firing alert did (or did not) turn into a delivery.
let internal notifyDebug =
  match Environment.GetEnvironmentVariable "PULSE_NOTIFY_DEBUG" with
  | "0" | "false" | "off" | "no" -> false
  | _ -> true

let internal nlog (msg : string) =
  if notifyDebug then eprintfn "[notify] %s" msg

// Alertmanager-equivalent: a single per-tenant
// config document drives routing, grouping, dedup, inhibition, silences,
// and time-based mutes. We deliberately mirror Alertmanager's data model
// because operators already know it, and because translating an existing
// `alertmanager.yml` to our JSON shape is mostly mechanical.
//
//   Route tree.  An incoming `AlertInstance` walks the root route's
//   children; the first child whose matchers match takes the alert
//   unless its `continue` flag is set, in which case sibling children
//   are also tried. The receiver attached to the matching leaf (or to
//   the root if no child matched) names the destination. Defaults are
//   inherited top-down — children that omit a `groupBy` / `groupWaitMs`
//   etc. take the parent's.
//
//   Grouping & dedup.  Per `(receiverId, groupKey)` we accumulate a set
//   of currently-firing fingerprints. After `groupWaitMs` since the
//   first new alert in the group, or `groupIntervalMs` since the last
//   send, we flush a notification carrying the active set. A repeated
//   identical set within `repeatIntervalMs` is dropped (dedup).
//
//   Silences.  A silence's matchers are evaluated against the alert's
//   labels. If they match and `now ∈ [startsAt, endsAt)`, the alert is
//   dropped before routing.
//
//   Inhibition.  For each inhibition rule, if any *firing* alert matches
//   the `source` matchers AND the candidate alert matches the `target`
//   matchers AND the values of all `equal` labels are identical between
//   source and target, the target is dropped.
//
//   Mute time intervals.  Each interval is a list of (startMin,endMin,
//   daysOfWeekBitmask) windows. If the current UTC time falls inside any
//   window of any interval referenced by the matching route, the alert
//   is muted.

// -- matchers ---------------------------------------------------------------

type MatchOp = MEq | MNeq | MRe | MNRe

let private opToStr = function
  | MEq -> "=" | MNeq -> "!=" | MRe -> "=~" | MNRe -> "!~"
let private strToOp = function
  | "="  -> Some MEq | "!=" -> Some MNeq
  | "=~" -> Some MRe | "!~" -> Some MNRe
  | _    -> None

[<NoComparison>]
type Matcher =
  { name  : string
    op    : MatchOp
    value : string
    /// Cached regex for `=~` / `!~`.
    re    : Regex option }

let private compileMatcher name op value =
  let re =
    match op with
    | MRe | MNRe ->
      try Some (Regex("^" + value + "$", RegexOptions.Compiled))
      with _ -> None
    | _ -> None
  { name = name; op = op; value = value; re = re }

let matcherMatches (m : Matcher) (labels : Map<string,string>) : bool =
  let actual = labels |> Map.tryFind m.name |> Option.defaultValue ""
  match m.op with
  | MEq  -> actual = m.value
  | MNeq -> actual <> m.value
  | MRe  -> match m.re with Some r -> r.IsMatch actual | None -> false
  | MNRe -> match m.re with Some r -> not (r.IsMatch actual) | None -> true

let matchersMatch (ms : Matcher[]) (labels : Map<string,string>) : bool =
  ms |> Array.forall (fun m -> matcherMatches m labels)

// -- receivers / route / silences / inhibitions / mutes --------------------

[<NoComparison>]
type Receiver =
  { id     : string
    name   : string
    type_  : string                  // slack | webhook | hmac_webhook | pagerduty | opsgenie | discord | teams | email
    url    : string option
    secret : string option
    extra  : Map<string,string> }

[<NoComparison>]
type Route =
  { id              : string
    matchers        : Matcher[]
    receiverId      : string option
    policyId        : string option   // Escalation policy
    groupBy         : string[]
    groupWaitMs     : int64
    groupIntervalMs : int64
    repeatIntervalMs: int64
    continue_       : bool
    muteTimeIds     : string[]
    children        : Route[] }

[<NoComparison>]
type Silence =
  { id        : string
    matchers  : Matcher[]
    startsAt  : int64
    endsAt    : int64
    createdBy : string
    comment   : string
    createdAt : int64 }

[<NoComparison>]
type Inhibition =
  { id             : string
    sourceMatchers : Matcher[]
    targetMatchers : Matcher[]
    equal          : string[] }

[<NoComparison>]
type MuteWindow =
  { startMinute : int       // 0..1440
    endMinute   : int
    daysOfWeek  : int }     // bitmask: bit0=Sun..bit6=Sat

[<NoComparison>]
type MuteTimeInterval =
  { id      : string
    name    : string
    windows : MuteWindow[] }

[<NoComparison>]
type Config =
  { route       : Route
    receivers   : Receiver[]
    silences    : Silence[]
    inhibitions : Inhibition[]
    muteTimes   : MuteTimeInterval[] }

// -- JSON ------------------------------------------------------------------

let private writeMatchers (w : Utf8JsonWriter) (name : string) (ms : Matcher[]) =
  w.WritePropertyName name
  w.WriteStartArray()
  for m in ms do
    w.WriteStartObject()
    w.WriteString("name",  m.name)
    w.WriteString("op",    opToStr m.op)
    w.WriteString("value", m.value)
    w.WriteEndObject()
  w.WriteEndArray()

let private writeStringArray (w : Utf8JsonWriter) (name : string) (xs : string[]) =
  w.WritePropertyName name
  w.WriteStartArray()
  for x in xs do w.WriteStringValue x
  w.WriteEndArray()

let private writeStringMap (w : Utf8JsonWriter) (name : string) (m : Map<string,string>) =
  w.WritePropertyName name
  w.WriteStartObject()
  for KeyValue(k, v) in m do w.WriteString(k, v)
  w.WriteEndObject()

let private writeReceiver (w : Utf8JsonWriter) (r : Receiver) =
  w.WriteStartObject()
  w.WriteString("id",   r.id)
  w.WriteString("name", r.name)
  w.WriteString("type", r.type_)
  match r.url    with Some u -> w.WriteString("url", u)       | None -> ()
  match r.secret with Some s -> w.WriteString("secret", s)    | None -> ()
  writeStringMap w "extra" r.extra
  w.WriteEndObject()

let rec private writeRoute (w : Utf8JsonWriter) (r : Route) =
  w.WriteStartObject()
  w.WriteString("id", r.id)
  writeMatchers w "matchers" r.matchers
  (match r.receiverId with Some rid -> w.WriteString("receiverId", rid) | None -> ())
  (match r.policyId   with Some pid -> w.WriteString("policyId",   pid) | None -> ())
  writeStringArray w "groupBy" r.groupBy
  w.WriteNumber("groupWaitMs",      r.groupWaitMs)
  w.WriteNumber("groupIntervalMs",  r.groupIntervalMs)
  w.WriteNumber("repeatIntervalMs", r.repeatIntervalMs)
  w.WriteBoolean("continue",        r.continue_)
  writeStringArray w "muteTimeIds"  r.muteTimeIds
  w.WritePropertyName "routes"
  w.WriteStartArray()
  for c in r.children do writeRoute w c
  w.WriteEndArray()
  w.WriteEndObject()

let private writeSilence (w : Utf8JsonWriter) (s : Silence) =
  w.WriteStartObject()
  w.WriteString("id", s.id)
  writeMatchers w "matchers" s.matchers
  w.WriteNumber("startsAt",  s.startsAt)
  w.WriteNumber("endsAt",    s.endsAt)
  w.WriteString("createdBy", s.createdBy)
  w.WriteString("comment",   s.comment)
  w.WriteNumber("createdAt", s.createdAt)
  w.WriteEndObject()

let private writeInhibition (w : Utf8JsonWriter) (i : Inhibition) =
  w.WriteStartObject()
  w.WriteString("id", i.id)
  writeMatchers w "source" i.sourceMatchers
  writeMatchers w "target" i.targetMatchers
  writeStringArray w "equal" i.equal
  w.WriteEndObject()

let private writeMute (w : Utf8JsonWriter) (m : MuteTimeInterval) =
  w.WriteStartObject()
  w.WriteString("id",   m.id)
  w.WriteString("name", m.name)
  w.WritePropertyName "windows"
  w.WriteStartArray()
  for win in m.windows do
    w.WriteStartObject()
    w.WriteNumber("startMinute", win.startMinute)
    w.WriteNumber("endMinute",   win.endMinute)
    w.WriteNumber("daysOfWeek",  win.daysOfWeek)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()

let serialiseConfig (c : Config) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WritePropertyName "route"
    writeRoute w c.route
    w.WritePropertyName "receivers"
    w.WriteStartArray()
    for r in c.receivers do writeReceiver w r
    w.WriteEndArray()
    w.WritePropertyName "silences"
    w.WriteStartArray()
    for s in c.silences do writeSilence w s
    w.WriteEndArray()
    w.WritePropertyName "inhibitions"
    w.WriteStartArray()
    for i in c.inhibitions do writeInhibition w i
    w.WriteEndArray()
    w.WritePropertyName "muteTimes"
    w.WriteStartArray()
    for m in c.muteTimes do writeMute w m
    w.WriteEndArray()
    w.WriteEndObject())
  Encoding.UTF8.GetString(ms.ToArray())

let serialiseSilences (xs : Silence seq) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for s in xs do writeSilence w s
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

// JSON readers
let private readStr (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString() in if isNull s then None else Some s
  | _ -> None

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) : int64 =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L
    if v.TryGetInt64 &n then n else dflt
  | _ -> dflt

let private readInt (el : JsonElement) (name : string) (dflt : int) : int = int (readInt64 el name (int64 dflt))

let private readBool (el : JsonElement) (name : string) (dflt : bool) : bool =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.True  -> true
  | true, v when v.ValueKind = JsonValueKind.False -> false
  | _ -> dflt

let private readStringArr (el : JsonElement) (name : string) : string[] =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Array ->
    v.EnumerateArray()
    |> Seq.choose (fun e ->
      if e.ValueKind = JsonValueKind.String
      then Some (e.GetString())
      else None)
    |> Seq.toArray
  | _ -> [||]

let private readMap (el : JsonElement) (name : string) : Map<string,string> =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Object ->
    v.EnumerateObject()
    |> Seq.choose (fun p ->
      if p.Value.ValueKind = JsonValueKind.String
      then Some (p.Name, p.Value.GetString())
      else None)
    |> Map.ofSeq
  | _ -> Map.empty

let private parseMatcher (el : JsonElement) : Matcher option =
  match readStr el "name", readStr el "op", readStr el "value" with
  | Some n, Some op, Some v ->
    match strToOp op with
    | Some o -> Some (compileMatcher n o v)
    | None -> None
  | _ -> None

let private parseMatchers (el : JsonElement) (name : string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Array ->
    v.EnumerateArray() |> Seq.choose parseMatcher |> Seq.toArray
  | _ -> [||]

let private parseReceiver (el : JsonElement) : Receiver option =
  match readStr el "id", readStr el "name", readStr el "type" with
  | Some id, Some name, Some t ->
    Some {
      id = id; name = name; type_ = t
      url    = readStr el "url"
      secret = readStr el "secret"
      extra  = readMap el "extra" }
  | _ -> None

let rec private parseRoute (el : JsonElement) : Route =
  let children =
    match el.TryGetProperty "routes" with
    | true, v when v.ValueKind = JsonValueKind.Array ->
      v.EnumerateArray() |> Seq.map parseRoute |> Seq.toArray
    | _ -> [||]
  { id = readStr el "id" |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
    matchers         = parseMatchers el "matchers"
    receiverId       = readStr el "receiverId"
    policyId         = readStr el "policyId"
    groupBy          = readStringArr el "groupBy"
    groupWaitMs      = readInt64 el "groupWaitMs"      30_000L
    groupIntervalMs  = readInt64 el "groupIntervalMs"  300_000L
    repeatIntervalMs = readInt64 el "repeatIntervalMs" 3_600_000L
    continue_        = readBool  el "continue"         false
    muteTimeIds      = readStringArr el "muteTimeIds"
    children         = children }

let private parseSilence (el : JsonElement) : Silence option =
  match readStr el "id" with
  | Some id ->
    Some {
      id = id
      matchers  = parseMatchers el "matchers"
      startsAt  = readInt64 el "startsAt" 0L
      endsAt    = readInt64 el "endsAt"   0L
      createdBy = readStr   el "createdBy" |> Option.defaultValue "system"
      comment   = readStr   el "comment"   |> Option.defaultValue ""
      createdAt = readInt64 el "createdAt" (nowMs ()) }
  | None -> None

let private parseInhibition (el : JsonElement) : Inhibition option =
  match readStr el "id" with
  | Some id ->
    Some {
      id = id
      sourceMatchers = parseMatchers el "source"
      targetMatchers = parseMatchers el "target"
      equal          = readStringArr el "equal" }
  | None -> None

let private parseMuteWindow (el : JsonElement) : MuteWindow =
  { startMinute = readInt el "startMinute" 0
    endMinute   = readInt el "endMinute"   1440
    daysOfWeek  = readInt el "daysOfWeek"  0x7F }

let private parseMute (el : JsonElement) : MuteTimeInterval option =
  match readStr el "id", readStr el "name" with
  | Some id, Some name ->
    let windows =
      match el.TryGetProperty "windows" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.map parseMuteWindow |> Seq.toArray
      | _ -> [||]
    Some { id = id; name = name; windows = windows }
  | _ -> None

let parseConfig (body : string) : Result<Config, string> =
  if String.IsNullOrWhiteSpace body then Result.Error "empty body" else
  try
    use doc = JsonDocument.Parse body
    let r = doc.RootElement
    let route =
      match r.TryGetProperty "route" with
      | true, v -> parseRoute v
      | _ ->
        // Default empty root: matches everything, no receiver, no children.
        { id = "root"; matchers = [||]; receiverId = None; policyId = None
          groupBy = [| "alertname" |]
          groupWaitMs = 30_000L; groupIntervalMs = 300_000L
          repeatIntervalMs = 3_600_000L
          continue_ = false; muteTimeIds = [||]; children = [||] }
    let receivers =
      match r.TryGetProperty "receivers" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseReceiver |> Seq.toArray
      | _ -> [||]
    let silences =
      match r.TryGetProperty "silences" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseSilence |> Seq.toArray
      | _ -> [||]
    let inhibitions =
      match r.TryGetProperty "inhibitions" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseInhibition |> Seq.toArray
      | _ -> [||]
    let muteTimes =
      match r.TryGetProperty "muteTimes" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseMute |> Seq.toArray
      | _ -> [||]
    Result.Ok {
      route = route
      receivers = receivers
      silences = silences
      inhibitions = inhibitions
      muteTimes = muteTimes }
  with ex -> Result.Error ex.Message

let parseSilenceBody (body : string) : Result<Silence, string> =
  if String.IsNullOrWhiteSpace body then Result.Error "empty body" else
  try
    use doc = JsonDocument.Parse body
    let r = doc.RootElement
    let id = readStr r "id" |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
    let matchers = parseMatchers r "matchers"
    if matchers.Length = 0 then Result.Error "matchers required" else
    Result.Ok {
      id = id
      matchers = matchers
      startsAt = readInt64 r "startsAt" (nowMs ())
      endsAt   = readInt64 r "endsAt"   (nowMs () + 3_600_000L)
      createdBy = readStr r "createdBy" |> Option.defaultValue "ui"
      comment   = readStr r "comment"   |> Option.defaultValue ""
      createdAt = nowMs () }
  with ex -> Result.Error ex.Message

// -- store ------------------------------------------------------------------

type IConfigStore =
  abstract Get  : TenantId -> Config
  abstract Set  : TenantId * Config -> unit
  abstract UpsertSilence : TenantId * Silence -> unit
  abstract DeleteSilence : TenantId * string -> bool

let private sanitize (s : string) =
  let sb = StringBuilder()
  for c in s do
    if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
      sb.Append c |> ignore else sb.Append '_' |> ignore
  let out = sb.ToString() in if out.Length = 0 then "_" else out

let private defaultConfig () : Config =
  { route =
      { id = "root"; matchers = [||]; receiverId = None; policyId = None
        groupBy = [| "alertname"; "service" |]
        groupWaitMs = 30_000L; groupIntervalMs = 300_000L
        repeatIntervalMs = 3_600_000L
        continue_ = false; muteTimeIds = [||]; children = [||] }
    receivers   = [||]
    silences    = [||]
    inhibitions = [||]
    muteTimes   = [||] }

type FileConfigStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, Config>()
  let sync  = obj()

  let pathFor (TenantId tid) = Path.Combine(root, sanitize tid + ".json")

  let load (tid : TenantId) =
    let p = pathFor tid
    if File.Exists p then
      match parseConfig (File.ReadAllText p) with
      | Result.Ok c -> c
      | Result.Error _ -> defaultConfig ()
    else defaultConfig ()

  do
    for f in Directory.EnumerateFiles(root, "*.json") do
      let tid = TenantId (Path.GetFileNameWithoutExtension f)
      cache.[Path.GetFileNameWithoutExtension f] <- load tid

  let cacheKey (TenantId tid) = sanitize tid

  let save (tid : TenantId) (c : Config) =
    let p = pathFor tid
    let tmp = p + ".tmp"
    File.WriteAllText(tmp, serialiseConfig c)
    if File.Exists p then File.Delete p
    File.Move(tmp, p)
    cache.[cacheKey tid] <- c

  interface IConfigStore with
    member _.Get tid =
      lock sync (fun () ->
        match cache.TryGetValue(cacheKey tid) with
        | true, c -> c
        | _ -> let c = load tid in cache.[cacheKey tid] <- c ; c)

    member s.Set(tid, c) = lock sync (fun () -> save tid c)

    member s.UpsertSilence(tid, sil) =
      lock sync (fun () ->
        let c = (s :> IConfigStore).Get tid
        let others = c.silences |> Array.filter (fun x -> x.id <> sil.id)
        save tid { c with silences = Array.append others [| sil |] })

    member s.DeleteSilence(tid, id) =
      lock sync (fun () ->
        let c = (s :> IConfigStore).Get tid
        let next = c.silences |> Array.filter (fun x -> x.id <> id)
        if next.Length = c.silences.Length then false
        else save tid { c with silences = next } ; true)

// -- pipeline ---------------------------------------------------------------

let private silenceActive (now : int64) (s : Silence) (labels : Map<string,string>) =
  now >= s.startsAt && now < s.endsAt && matchersMatch s.matchers labels

let private isMuted (now : DateTimeOffset) (mts : MuteTimeInterval[]) (ids : string[]) =
  if ids.Length = 0 then false else
  let dow = int now.DayOfWeek          // Sun = 0 .. Sat = 6
  let minute = now.Hour * 60 + now.Minute
  let dowBit = 1 <<< dow
  ids
  |> Array.exists (fun id ->
    match mts |> Array.tryFind (fun m -> m.id = id) with
    | None -> false
    | Some m ->
      m.windows
      |> Array.exists (fun w ->
        (w.daysOfWeek &&& dowBit) <> 0
        && minute >= w.startMinute
        && minute <  w.endMinute))

/// Walk the route tree; return (matched route, ancestors) in best-match
/// order. The root is always included as the fallback.
let private collectMatchingRoutes (root : Route) (labels : Map<string,string>)
                                  : Route list =
  let rec walk acc (r : Route) =
    let matched =
      r.matchers.Length = 0 || matchersMatch r.matchers labels
    if not matched then acc else
      // Recurse into children; if any child matches we may stop based on
      // `continue` flag.
      let mutable hits = []
      let mutable stop = false
      for child in r.children do
        if not stop then
          let childHits = walk [] child
          if not (List.isEmpty childHits) then
            hits <- hits @ childHits
            if not child.continue_ then stop <- true
      if List.isEmpty hits then r :: acc else hits @ acc
  walk [] root |> List.rev

/// Resolve receiver id by walking from a matched leaf back up to the
/// root: the leaf's `receiverId` wins, otherwise the first ancestor
/// that has one. Returns None if no route in the chain has a receiver
/// — alert is silently dropped (Alertmanager parity).
let private resolveReceiver (path : Route list) : string option =
  path |> List.tryPick (fun r -> r.receiverId)

let private resolvePolicy (path : Route list) : string option =
  path |> List.tryPick (fun r -> r.policyId)

/// Hook supplied by the on-call layer (`OnCall.fs`). Pipeline uses it,
/// when present, to drive multi-step escalation in place of the normal
/// group-wait/group-interval cadence.
type IEscalator =
  /// Number of steps defined by the policy (0 if unknown).
  abstract StepCount  : tenantId:TenantId * policyId:string -> int
  /// Returns `(delayMs, receiverIds)` for the given step. `delayMs` is
  /// the time to wait before *this* step fires, measured from the
  /// previous step's send time (or from group firstSeen for step 0).
  abstract ResolveStep : tenantId:TenantId * policyId:string * stepIndex:int -> (int64 * string[]) option
  /// True if any fingerprint in the set has been acknowledged.
  abstract IsAcked    : tenantId:TenantId * fingerprints:Set<string> -> bool

[<NoComparison>]
type private GroupState =
  { mutable firstSeenAt    : int64
    mutable lastSentAt     : int64
    mutable lastFlushAt    : int64  // last due-flush attempt (sent OR deduped)
    mutable fingerprints   : Set<string>
    mutable lastSentSet    : Set<string>
    mutable policyId       : string option
    mutable escalationStep : int    // -1 = no step fired yet
    mutable stepStartedAt  : int64 }

[<NoComparison>]
type private TenantGroups =
  { groups : ConcurrentDictionary<string, GroupState> }   // key: receiverId + "|" + groupKey

/// Envelope that goes onto the outbound queue. The transport layer in
/// `NotifyQueue.fs` does receiver-specific HTTP shaping.
///
/// When an alert carries an inline runbook we attach a
/// top-level `runbooks` array carrying a truncated excerpt plus a deep
/// link into the portal so the on-call engineer can open the full
/// checklist straight from the notification body.
let private runbookExcerptLen = 280

let private deepLinkFor (publicUrl : string) (fp : string) =
  if String.IsNullOrWhiteSpace publicUrl then "#/alerts/" + fp
  else publicUrl.TrimEnd('/') + "/#/alerts/" + fp

let private envelope (publicUrl : string)
                     (correlationJson : AlertInstance -> string option)
                     (recv : Receiver) (groupKey : string) (alerts : AlertInstance[]) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("receiver", recv.name)
    w.WriteString("groupKey", groupKey)
    w.WriteNumber("ts", nowMs ())
    w.WritePropertyName "alerts"
    w.WriteStartArray()
    for a in alerts do writeAlert w a
    w.WriteEndArray()
    let withRunbooks =
      alerts |> Array.filter (fun a ->
        match a.runbook with
        | Some rb -> not (String.IsNullOrWhiteSpace rb)
        | None -> false)
    if withRunbooks.Length > 0 then
      w.WritePropertyName "runbooks"
      w.WriteStartArray()
      for a in withRunbooks do
        let rb = a.runbook |> Option.defaultValue ""
        let excerpt =
          if rb.Length <= runbookExcerptLen then rb
          else rb.Substring(0, runbookExcerptLen) + "\u2026"
        w.WriteStartObject()
        w.WriteString("fingerprint", a.fingerprint)
        w.WriteString("ruleName", a.ruleName)
        w.WriteString("excerpt", excerpt)
        w.WriteBoolean("truncated", rb.Length > runbookExcerptLen)
        w.WriteString("deepLink", deepLinkFor publicUrl a.fingerprint)
        w.WriteEndObject()
      w.WriteEndArray()
    // End-to-end correlation: attach the fire-time snapshot
    // (top log lines + slowest trace) captured by the correlation
    // snapshotter. The provider returns a ready-made JSON object string so
    // Routing stays decoupled from the Correlation module.
    let correlations =
      alerts
      |> Array.choose (fun a -> correlationJson a |> Option.map (fun j -> a.fingerprint, j))
    if correlations.Length > 0 then
      w.WritePropertyName "correlations"
      w.WriteStartArray()
      for (fp, j) in correlations do
        w.WriteStartObject()
        w.WriteString("fingerprint", fp)
        w.WriteString("deepLink", deepLinkFor publicUrl fp)
        w.WritePropertyName "snapshot"
        w.WriteRawValue(j, skipInputValidation = false)
        w.WriteEndObject()
      w.WriteEndArray()
    w.WriteEndObject())
  Encoding.UTF8.GetString(ms.ToArray())

type Pipeline(configStore : IConfigStore,
              queue       : INotifyQueue,
              selfMetrics : MetricStore) =

  // Portal base URL used to build runbook deep links in notification
  // bodies. Empty → emit a relative `#/alerts/<fp>` hash link.
  let mutable publicUrl = ""

  // Correlation snapshot provider. Returns a ready-made
  // JSON object string (the serialized fire-time snapshot) for an alert, or
  // None when no snapshot is available. Wired post-construction by
  // `Program.fs` so Routing stays decoupled from the Correlation module.
  let mutable correlationProvider : (AlertInstance -> string option) = fun _ -> None

  // (tenant) -> grouping state
  let perTenant = ConcurrentDictionary<string, TenantGroups>()
  // active firing alerts per tenant, for inhibition source lookups
  let firing = ConcurrentDictionary<string, ConcurrentDictionary<string, AlertInstance>>()
  // periodic flush timer state
  let flushSync = obj()
  // on-call/escalation hook, wired post-construction by Program.fs
  let mutable escalator : IEscalator option = None

  let tenantBucket (TenantId t) =
    let key = t
    perTenant.GetOrAdd(key, fun _ ->
      { groups = ConcurrentDictionary() })

  let firingBucket (TenantId t) =
    firing.GetOrAdd(t, fun _ -> ConcurrentDictionary())

  let groupKeyOf (labels : Map<string,string>) (by : string[]) =
    if by.Length = 0 then "{}" else
    by
    |> Array.map (fun k -> k + "=" + (labels |> Map.tryFind k |> Option.defaultValue ""))
    |> String.concat ","

  /// Returns true when this alert should be inhibited by some firing
  /// source alert in the same tenant.
  let inhibited (tid : TenantId) (cfg : Config) (cand : AlertInstance) =
    let bucket = firingBucket tid
    cfg.inhibitions
    |> Array.exists (fun inh ->
      if not (matchersMatch inh.targetMatchers cand.labels) then false
      else
        bucket.Values
        |> Seq.exists (fun src ->
          src.state = AlertState.Firing
          && src.fingerprint <> cand.fingerprint
          && matchersMatch inh.sourceMatchers src.labels
          && inh.equal
             |> Array.forall (fun lbl ->
               (src.labels |> Map.tryFind lbl) = (cand.labels |> Map.tryFind lbl))))

  /// Flush groups whose `groupWait` since first-seen has elapsed (initial
  /// notification) or whose `groupInterval` since last-send has elapsed
  /// (follow-up). Repeated identical fingerprint sets within
  /// `repeatIntervalMs` are deduped. Groups bound to an escalation
  /// policy bypass this cadence and step through the policy instead.
  let flushDue (now : int64) =
    lock flushSync (fun () ->
      for kvT in perTenant do
        let tid = TenantId kvT.Key
        let cfg = configStore.Get tid
        let recvById =
          cfg.receivers
          |> Array.map (fun r -> r.id, r)
          |> Map.ofArray
        let bucket = firingBucket tid
        let enqueueFor (recv : Receiver) (state : GroupState) (groupId : string)
                       (alerts : AlertInstance[]) =
          try
            let body = envelope publicUrl correlationProvider recv groupId alerts
            let msg : OutboundMessage =
              { id = Guid.NewGuid().ToString("N")
                tenantId = tid
                receiverId = recv.id
                receiverType = recv.type_
                url = recv.url |> Option.defaultValue ""
                secret = recv.secret
                body = body
                headers = Map.empty
                extra = recv.extra
                attempt = 0
                maxAttempts = 5
                enqueuedAt = now
                nextRunAt = now
                lastError = None }
            queue.Enqueue msg
            state.lastSentAt <- now
            state.lastSentSet <- state.fingerprints
            nlog (sprintf "ENQUEUE tenant=%s receiver=%s type=%s url=%s group=%s alerts=%d msgId=%s"
                    kvT.Key recv.id recv.type_
                    (recv.url |> Option.defaultValue "(none)") groupId alerts.Length msg.id)
            selfMetrics.Record(
              "pulse_notify_enqueued_total",
              { ts = now; value = 1.0 })
          with ex ->
            // Surface the failure instead of letting the flush timer's
            // blanket catch swallow it (the classic ROUTED-but-no-ENQUEUE
            // symptom). lastSentAt is left untouched so the next tick
            // retries — and keeps logging — until the cause is fixed.
            nlog (sprintf "ENQUEUE FAILED tenant=%s receiver=%s type=%s url=%s group=%s alerts=%d: %s | %s"
                    kvT.Key recv.id recv.type_
                    (recv.url |> Option.defaultValue "(none)") groupId alerts.Length
                    ex.Message (ex.GetType().Name))
            nlog (sprintf "ENQUEUE FAILED stack: %s" ex.StackTrace)
        for kvG in kvT.Value.groups do
          let gkey  = kvG.Key
          let state = kvG.Value
          if state.fingerprints.Count = 0 then
            // Group drained — reset escalation so a fresh outbreak starts over.
            state.escalationStep <- -1
            state.stepStartedAt  <- 0L
          else
          // Resolve receiver: gkey starts with "<recvId>|<group>".
          let pipe = gkey.IndexOf '|'
          if pipe < 0 then () else
          let recvId  = gkey.Substring(0, pipe)
          let groupId = gkey.Substring(pipe + 1)
          match Map.tryFind recvId recvById with
          | None ->
            nlog (sprintf "flushDue tenant=%s group=%s -> receiverId=%s is NOT defined in receivers (defined: [%s]); skipping, nothing sent"
                    kvT.Key gkey recvId
                    (recvById |> Map.toSeq |> Seq.map fst |> String.concat ", "))
          | Some recv ->
            // Re-check gating on every flush. OnAlert only fires on
            // Pending→Firing transitions, so a silence/mute/inhibition
            // created AFTER the alert was routed would otherwise be
            // ignored and the group would keep re-sending every
            // groupIntervalMs until the alert resolved.
            let routeMuted =
              isMuted
                (DateTimeOffset.FromUnixTimeMilliseconds now)
                cfg.muteTimes cfg.route.muteTimeIds
            if routeMuted then
              selfMetrics.Record(
                "pulse_alerts_muted_total", { ts = now; value = 1.0 })
            else
            let rawAlerts =
              state.fingerprints
              |> Seq.choose (fun fp ->
                match bucket.TryGetValue fp with
                | true, a -> Some a | _ -> None)
              |> Seq.toArray
            let alerts =
              rawAlerts
              |> Array.filter (fun a ->
                let silenced =
                  cfg.silences
                  |> Array.exists (fun s -> silenceActive now s a.labels)
                if silenced then
                  selfMetrics.Record(
                    "pulse_alerts_silenced_total",
                    { ts = now; value = 1.0 })
                  false
                elif inhibited tid cfg a then
                  selfMetrics.Record(
                    "pulse_alerts_inhibited_total",
                    { ts = now; value = 1.0 })
                  false
                else true)
            if alerts.Length = 0 then
              if rawAlerts.Length > 0 then
                nlog (sprintf "flushDue tenant=%s group=%s -> SUPPRESSED (all %d alert(s) silenced/inhibited; group cadence preserved)"
                        kvT.Key gkey rawAlerts.Length)
            else
            // Acked? Suppress further sends (escalation halts; routine
            // group flush would also be noisy).
            let acked =
              match escalator, state.policyId with
              | Some esc, Some _ -> esc.IsAcked(tid, state.fingerprints)
              | _ -> false
            if acked then
              selfMetrics.Record(
                "pulse_alerts_acked_total", { ts = now; value = 1.0 })
            elif state.policyId.IsSome && escalator.IsSome then
              let pid = state.policyId.Value
              let esc = escalator.Value
              let steps = esc.StepCount(tid, pid)
              if steps <= 0 then () else
              let nextIdx = state.escalationStep + 1
              if nextIdx >= steps then () else
              match esc.ResolveStep(tid, pid, nextIdx) with
              | None -> ()
              | Some (delayMs, receiverIds) ->
                let anchor =
                  if state.escalationStep < 0 then state.firstSeenAt
                  else state.stepStartedAt
                if now - anchor < delayMs then () else
                let mutable any = false
                for rid in receiverIds do
                  match Map.tryFind rid recvById with
                  | Some r ->
                    enqueueFor r state groupId alerts
                    any <- true
                  | None -> ()
                if any then
                  state.escalationStep <- nextIdx
                  state.stepStartedAt  <- now
                  selfMetrics.Record(
                    "pulse_escalation_step_total",
                    { ts = now; value = float (nextIdx + 1) })
            else
              // Standard Alertmanager-style cadence.
              let route = cfg.route
              let waitOk =
                now - state.firstSeenAt >= route.groupWaitMs
                && state.lastSentAt = 0L
              // Followup cadence is anchored on the last flush ATTEMPT
              // (sent or deduped), not the last actual send — otherwise a
              // deduped group re-evaluates on every 1s tick because
              // lastSentAt never advances.
              let followOk =
                state.lastSentAt > 0L
                && now - state.lastFlushAt >= route.groupIntervalMs
              if waitOk || followOk then
                state.lastFlushAt <- now
                let fps = state.fingerprints
                let identical = fps = state.lastSentSet
                let withinRepeat =
                  state.lastSentAt > 0L
                  && now - state.lastSentAt < route.repeatIntervalMs
                if identical && withinRepeat then
                  nlog (sprintf "flushDue tenant=%s group=%s -> deduped (identical fingerprint set within repeatIntervalMs=%d); next check in ~%dms"
                          kvT.Key gkey route.repeatIntervalMs route.groupIntervalMs)
                else
                  enqueueFor recv state groupId alerts)

  let flushTimer =
    new Timer((fun _ ->
                try flushDue (nowMs ())
                with ex ->
                  // Last line of defense: anything thrown outside the
                  // per-group enqueueFor guard (e.g. config load, bucket
                  // resolution) is logged rather than silently dropped.
                  nlog (sprintf "flushDue FAILED: %s | %s" ex.Message (ex.GetType().Name))
                  nlog (sprintf "flushDue FAILED stack: %s" ex.StackTrace)),
              null, TimeSpan.FromSeconds 1., TimeSpan.FromSeconds 1.)

  interface IAlertSink with
    member s.OnAlert a = s.OnAlert a

  member _.OnAlert(a : AlertInstance) =
    let tid = a.tenantId
    let (TenantId tidText) = tid
    let cfg = configStore.Get tid
    let now = nowMs ()

    // Track firing set for inhibition source lookups + group bookkeeping.
    let bucket = firingBucket tid
    match a.state with
    | AlertState.Firing -> bucket.[a.fingerprint] <- a
    | AlertState.Resolved | AlertState.Pending ->
      bucket.TryRemove a.fingerprint |> ignore

    if a.state <> AlertState.Firing then
      // Resolutions remove the alert from any active group; they don't
      // currently emit their own notification (TODO: optional
      // `send_resolved` per receiver).
      nlog (sprintf "OnAlert tenant=%s fp=%s rule=%s state=%A -> not firing; pulling from any active group"
              tidText a.fingerprint a.ruleName a.state)
      let tg = tenantBucket tid
      for kv in tg.groups do
        if kv.Value.fingerprints.Contains a.fingerprint then
          kv.Value.fingerprints <- kv.Value.fingerprints.Remove a.fingerprint
          selfMetrics.Record(
            "pulse_alerts_resolved_total",
            { ts = now; value = 1.0 })
    else
      nlog (sprintf "OnAlert tenant=%s fp=%s rule=%s state=Firing labels=%A | cfg: receivers=%d silences=%d inhibitions=%d rootReceiver=%A"
              tidText a.fingerprint a.ruleName a.labels
              cfg.receivers.Length cfg.silences.Length cfg.inhibitions.Length cfg.route.receiverId)
      // Silence?
      if cfg.silences |> Array.exists (fun s -> silenceActive now s a.labels) then
        nlog (sprintf "  -> SILENCED fp=%s (matched an active silence window)" a.fingerprint)
        selfMetrics.Record(
          "pulse_alerts_silenced_total", { ts = now; value = 1.0 })
      // Mute window on root?
      elif isMuted (DateTimeOffset.FromUnixTimeMilliseconds now) cfg.muteTimes cfg.route.muteTimeIds then
        nlog (sprintf "  -> MUTED fp=%s (active mute-time window on root route)" a.fingerprint)
        selfMetrics.Record(
          "pulse_alerts_muted_total", { ts = now; value = 1.0 })
      // Inhibited?
      elif inhibited tid cfg a then
        nlog (sprintf "  -> INHIBITED fp=%s (suppressed by a firing source alert)" a.fingerprint)
        selfMetrics.Record(
          "pulse_alerts_inhibited_total", { ts = now; value = 1.0 })
      else
        let path = collectMatchingRoutes cfg.route a.labels
        match resolveReceiver path with
        | None ->
          nlog (sprintf "  -> DROPPED fp=%s: route tree matched %d node(s) but NONE carries a receiverId. Set a receiver on the root route (or a matching child) so the alert has somewhere to go."
                  a.fingerprint (List.length path))
        | Some recvId ->
          let definedIds = cfg.receivers |> Array.map (fun r -> r.id)
          if not (Array.contains recvId definedIds) then
            nlog (sprintf "  -> WARNING fp=%s: route resolved receiverId=%s but no receiver with that id is defined (defined ids: [%s]). The group is created, but flushDue will skip it and nothing is sent. Fix the receiverId/receivers mismatch."
                    a.fingerprint recvId (String.concat ", " definedIds))
          let policyId = resolvePolicy path
          // Inherit groupBy from the deepest route in `path` that
          // declares one; otherwise the root.
          let groupBy =
            path
            |> List.tryFind (fun r -> r.groupBy.Length > 0)
            |> Option.map (fun r -> r.groupBy)
            |> Option.defaultValue cfg.route.groupBy
          let gkey = recvId + "|" + groupKeyOf a.labels groupBy
          let tg = tenantBucket tid
          let st =
            tg.groups.GetOrAdd(gkey, fun _ ->
              { firstSeenAt = now; lastSentAt = 0L; lastFlushAt = 0L
                fingerprints = Set.empty; lastSentSet = Set.empty
                policyId = policyId; escalationStep = -1; stepStartedAt = 0L })
          if st.fingerprints.Count = 0 then
            st.firstSeenAt <- now
            st.escalationStep <- -1
            st.stepStartedAt  <- 0L
          // Allow late policy attachment if a later route revision adds one.
          if st.policyId.IsNone && policyId.IsSome then st.policyId <- policyId
          st.fingerprints <- st.fingerprints.Add a.fingerprint
          nlog (sprintf "  -> ROUTED fp=%s receiverId=%s group=%s policy=%A groupSize=%d (first send after groupWaitMs=%d; followups every groupIntervalMs=%d)"
                  a.fingerprint recvId gkey policyId st.fingerprints.Count
                  cfg.route.groupWaitMs cfg.route.groupIntervalMs)
          selfMetrics.Record(
            "pulse_alerts_routed_total", { ts = now; value = 1.0 })

  member _.SetEscalator(esc : IEscalator) =
    escalator <- Some esc

  /// Portal base URL used to render runbook deep links in notification
  /// bodies. Empty disables the absolute prefix.
  member _.SetPublicUrl(url : string) =
    publicUrl <- (if isNull url then "" else url)

  /// Correlation snapshot provider: maps a firing alert to
  /// a serialized fire-time snapshot JSON object embedded into notification
  /// bodies. Pass `fun _ -> None` to disable.
  member _.SetCorrelationProvider(f : AlertInstance -> string option) =
    correlationProvider <- (if obj.ReferenceEquals(f, null) then (fun _ -> None) else f)

  member _.Stop() = flushTimer.Dispose()

// -- REST -------------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 204 -> fun _ -> Suave.Successful.NO_CONTENT
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private resolveTenant multiTenant (ctx : HttpContext) =
  if multiTenant then
    PulseBoard.Rbac.tryGetTenant ctx |> Option.map (fun t -> t.tenant.id)
  else Some (TenantId "__local__")

let private readBody (req : HttpRequest) =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let webPart (multiTenant : bool) (store : IConfigStore) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }
  choose [
    GET >=> path "/api/alertmanager/config" >=>
      withTenant (fun tid -> jsonResp 200 (serialiseConfig (store.Get tid)))
    PUT >=> path "/api/alertmanager/config" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseConfig (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid config: " + e) ctx
          | Result.Ok c ->
            store.Set(tid, c)
            return! jsonResp 200 (serialiseConfig c) ctx
        })
    GET >=> path "/api/silences" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseSilences (store.Get(tid).silences)))
    POST >=> path "/api/silences" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseSilenceBody (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid silence: " + e) ctx
          | Result.Ok s ->
            store.UpsertSilence(tid, s)
            return! jsonResp 201 (serialiseSilences [| s |]) ctx
        })
    DELETE >=> pathScan "/api/silences/%s" (fun id ->
      withTenant (fun tid ->
        if store.DeleteSilence(tid, id) then Suave.Successful.NO_CONTENT
        else errJson 404 "no such silence"))
  ]
