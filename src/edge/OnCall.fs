module PulseBoard.OnCall

// PLAN.md Phase 5 #4 — On-call schedules + escalation policies + acks.
//
// This module owns three pieces:
//
//   1.  Catalog persistence.  Per-tenant `users`, `schedules`, and
//       `policies` are stored as a single JSON document at
//       `<root>/<tenant>.json`.  Operators PUT a new catalog to replace
//       it wholesale, mirroring the Alertmanager config endpoint shape.
//
//   2.  Ack log.  Each acknowledgement is appended to an NDJSON journal
//       at `<root>/<tenant>.ndjson` and held in an in-memory `HashSet`
//       of fingerprints for fast `IsAcked` lookups by the routing
//       pipeline.  An ack suppresses further escalation steps and
//       routine group sends for that fingerprint until the alert
//       resolves and a new outbreak begins.
//
//   3.  Escalator.  An adapter that resolves a (tenant, policy, step)
//       triple into a delay and a list of `receiverId`s.  Targets in a
//       step can be receivers, individual users, or on-call schedules;
//       schedules resolve to whoever is on call at the current
//       wall-clock time, and users resolve to their declared
//       `receiverIds`.  Implements `Routing.IEscalator` so `Pipeline`
//       can drive multi-step escalation without taking a hard
//       dependency on this module.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open System.Collections.Concurrent
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open PulseBoard.Tenancy
open PulseBoard.Routing

// -- domain types -----------------------------------------------------------

[<NoComparison>]
type User =
  { id          : string
    name        : string
    email       : string
    receiverIds : string[] }

[<NoComparison>]
type Rotation =
  { id        : string
    members   : string[]    // user ids
    periodMs  : int64       // shift length
    startAt   : int64 }     // unix ms anchor for the rotation

[<NoComparison>]
type ScheduleOverride =
  { userId   : string
    startsAt : int64
    endsAt   : int64 }

[<NoComparison>]
type Schedule =
  { id        : string
    name      : string
    rotations : Rotation[]
    overrides : ScheduleOverride[] }

[<NoComparison>]
type Target =
  | TgtReceiver of string
  | TgtSchedule of string
  | TgtUser     of string

[<NoComparison>]
type EscalationStep =
  { delayMs : int64
    targets : Target[] }

[<NoComparison>]
type EscalationPolicy =
  { id    : string
    name  : string
    steps : EscalationStep[] }

[<NoComparison>]
type Catalog =
  { users     : User[]
    schedules : Schedule[]
    policies  : EscalationPolicy[] }

[<NoComparison>]
type Acknowledgement =
  { fingerprint : string
    user        : string
    ackedAt     : int64 }

let emptyCatalog : Catalog =
  { users = [||]; schedules = [||]; policies = [||] }

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- JSON codec -------------------------------------------------------------

let private readStr (el : JsonElement) (name : string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
  | _ -> None

let private readStrOr (el : JsonElement) (name : string) (dflt : string) =
  readStr el name |> Option.defaultValue dflt

let private readInt64 (el : JsonElement) (name : string) (dflt : int64) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number ->
    let mutable n = 0L in (if v.TryGetInt64 &n then n else dflt)
  | _ -> dflt

let private readStringArr (el : JsonElement) (name : string) : string[] =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Array ->
    v.EnumerateArray()
    |> Seq.choose (fun e ->
      if e.ValueKind = JsonValueKind.String then Some (e.GetString())
      else None)
    |> Seq.toArray
  | _ -> [||]

let private writeStringArr (w : Utf8JsonWriter) (name : string) (xs : string[]) =
  w.WritePropertyName name
  w.WriteStartArray()
  for x in xs do w.WriteStringValue x
  w.WriteEndArray()

let private writeUser (w : Utf8JsonWriter) (u : User) =
  w.WriteStartObject()
  w.WriteString("id", u.id)
  w.WriteString("name", u.name)
  w.WriteString("email", u.email)
  writeStringArr w "receiverIds" u.receiverIds
  w.WriteEndObject()

let private parseUser (el : JsonElement) : User option =
  match readStr el "id" with
  | Some id ->
    Some { id = id
           name = readStrOr el "name" ""
           email = readStrOr el "email" ""
           receiverIds = readStringArr el "receiverIds" }
  | None -> None

let private writeRotation (w : Utf8JsonWriter) (r : Rotation) =
  w.WriteStartObject()
  w.WriteString("id", r.id)
  writeStringArr w "members" r.members
  w.WriteNumber("periodMs", r.periodMs)
  w.WriteNumber("startAt",  r.startAt)
  w.WriteEndObject()

let private parseRotation (el : JsonElement) : Rotation option =
  match readStr el "id" with
  | Some id ->
    Some { id = id
           members  = readStringArr el "members"
           periodMs = readInt64 el "periodMs" 86_400_000L
           startAt  = readInt64 el "startAt"  0L }
  | None -> None

let private writeOverride (w : Utf8JsonWriter) (o : ScheduleOverride) =
  w.WriteStartObject()
  w.WriteString("userId",   o.userId)
  w.WriteNumber("startsAt", o.startsAt)
  w.WriteNumber("endsAt",   o.endsAt)
  w.WriteEndObject()

let private parseOverride (el : JsonElement) : ScheduleOverride option =
  match readStr el "userId" with
  | Some uid ->
    Some { userId   = uid
           startsAt = readInt64 el "startsAt" 0L
           endsAt   = readInt64 el "endsAt"   0L }
  | None -> None

let private writeSchedule (w : Utf8JsonWriter) (s : Schedule) =
  w.WriteStartObject()
  w.WriteString("id",   s.id)
  w.WriteString("name", s.name)
  w.WritePropertyName "rotations"
  w.WriteStartArray()
  for r in s.rotations do writeRotation w r
  w.WriteEndArray()
  w.WritePropertyName "overrides"
  w.WriteStartArray()
  for o in s.overrides do writeOverride w o
  w.WriteEndArray()
  w.WriteEndObject()

let private parseSchedule (el : JsonElement) : Schedule option =
  match readStr el "id" with
  | Some id ->
    let rotations =
      match el.TryGetProperty "rotations" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseRotation |> Seq.toArray
      | _ -> [||]
    let overrides =
      match el.TryGetProperty "overrides" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseOverride |> Seq.toArray
      | _ -> [||]
    Some { id = id
           name = readStrOr el "name" id
           rotations = rotations
           overrides = overrides }
  | None -> None

let private writeTarget (w : Utf8JsonWriter) (t : Target) =
  w.WriteStartObject()
  (match t with
   | TgtReceiver id -> w.WriteString("kind", "receiver"); w.WriteString("id", id)
   | TgtSchedule id -> w.WriteString("kind", "schedule"); w.WriteString("id", id)
   | TgtUser     id -> w.WriteString("kind", "user");     w.WriteString("id", id))
  w.WriteEndObject()

let private parseTarget (el : JsonElement) : Target option =
  match readStr el "kind", readStr el "id" with
  | Some "receiver", Some id -> Some (TgtReceiver id)
  | Some "schedule", Some id -> Some (TgtSchedule id)
  | Some "user",     Some id -> Some (TgtUser id)
  | _ -> None

let private writeStep (w : Utf8JsonWriter) (s : EscalationStep) =
  w.WriteStartObject()
  w.WriteNumber("delayMs", s.delayMs)
  w.WritePropertyName "targets"
  w.WriteStartArray()
  for t in s.targets do writeTarget w t
  w.WriteEndArray()
  w.WriteEndObject()

let private parseStep (el : JsonElement) : EscalationStep =
  let targets =
    match el.TryGetProperty "targets" with
    | true, v when v.ValueKind = JsonValueKind.Array ->
      v.EnumerateArray() |> Seq.choose parseTarget |> Seq.toArray
    | _ -> [||]
  { delayMs = readInt64 el "delayMs" 0L
    targets = targets }

let private writePolicy (w : Utf8JsonWriter) (p : EscalationPolicy) =
  w.WriteStartObject()
  w.WriteString("id", p.id)
  w.WriteString("name", p.name)
  w.WritePropertyName "steps"
  w.WriteStartArray()
  for s in p.steps do writeStep w s
  w.WriteEndArray()
  w.WriteEndObject()

let private parsePolicy (el : JsonElement) : EscalationPolicy option =
  match readStr el "id" with
  | Some id ->
    let steps =
      match el.TryGetProperty "steps" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.map parseStep |> Seq.toArray
      | _ -> [||]
    Some { id = id; name = readStrOr el "name" id; steps = steps }
  | None -> None

let serialiseCatalog (c : Catalog) : string =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WritePropertyName "users"
    w.WriteStartArray()
    for u in c.users do writeUser w u
    w.WriteEndArray()
    w.WritePropertyName "schedules"
    w.WriteStartArray()
    for s in c.schedules do writeSchedule w s
    w.WriteEndArray()
    w.WritePropertyName "policies"
    w.WriteStartArray()
    for p in c.policies do writePolicy w p
    w.WriteEndArray()
    w.WriteEndObject())
  Encoding.UTF8.GetString(ms.ToArray())

let parseCatalog (json : string) : Result<Catalog, string> =
  try
    use doc = JsonDocument.Parse json
    let r = doc.RootElement
    let users =
      match r.TryGetProperty "users" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseUser |> Seq.toArray
      | _ -> [||]
    let schedules =
      match r.TryGetProperty "schedules" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parseSchedule |> Seq.toArray
      | _ -> [||]
    let policies =
      match r.TryGetProperty "policies" with
      | true, v when v.ValueKind = JsonValueKind.Array ->
        v.EnumerateArray() |> Seq.choose parsePolicy |> Seq.toArray
      | _ -> [||]
    Result.Ok { users = users; schedules = schedules; policies = policies }
  with ex -> Result.Error ex.Message

// -- catalog store ----------------------------------------------------------

type ICatalogStore =
  abstract Get : tenantId:TenantId -> Catalog
  abstract Set : tenantId:TenantId * catalog:Catalog -> unit

type FileCatalogStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, Catalog>()
  let sync  = obj()
  let pathFor (TenantId t) = Path.Combine(root, t + ".json")
  let load (tid : TenantId) =
    let p = pathFor tid
    if File.Exists p then
      try
        match parseCatalog (File.ReadAllText p) with
        | Result.Ok c -> c
        | Result.Error _ -> emptyCatalog
      with _ -> emptyCatalog
    else emptyCatalog
  interface ICatalogStore with
    member _.Get tid =
      cache.GetOrAdd((let (TenantId t) = tid in t), fun _ -> load tid)
    member _.Set(tid, c) =
      lock sync (fun () ->
        cache.[(let (TenantId t) = tid in t)] <- c
        File.WriteAllText(pathFor tid, serialiseCatalog c))

// -- ack store --------------------------------------------------------------

type IAckStore =
  abstract Ack    : tenantId:TenantId * ack:Acknowledgement -> unit
  abstract IsAcked: tenantId:TenantId * fingerprint:string -> bool
  abstract List   : tenantId:TenantId * fingerprint:string -> Acknowledgement[]

type FileAckStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  // per-tenant set of acked fingerprints
  let acked = ConcurrentDictionary<string, ConcurrentDictionary<string, Acknowledgement>>()
  let sync  = obj()
  let pathFor (TenantId t) = Path.Combine(root, t + ".ndjson")
  let writeAck (w : Utf8JsonWriter) (a : Acknowledgement) =
    w.WriteStartObject()
    w.WriteString("fingerprint", a.fingerprint)
    w.WriteString("user",        a.user)
    w.WriteNumber("ackedAt",     a.ackedAt)
    w.WriteEndObject()
  let serialiseAck (a : Acknowledgement) =
    use ms = new MemoryStream()
    (
      use w = new Utf8JsonWriter(ms)
      writeAck w a)
    Encoding.UTF8.GetString(ms.ToArray())
  let parseAck (line : string) : Acknowledgement option =
    try
      use doc = JsonDocument.Parse line
      let r = doc.RootElement
      match readStr r "fingerprint", readStr r "user" with
      | Some fp, Some u ->
        Some { fingerprint = fp; user = u
               ackedAt = readInt64 r "ackedAt" 0L }
      | _ -> None
    with _ -> None
  let bucket (TenantId t) =
    acked.GetOrAdd(t, fun _ ->
      let m = ConcurrentDictionary<string, Acknowledgement>()
      let p = Path.Combine(root, t + ".ndjson")
      if File.Exists p then
        try
          for line in File.ReadAllLines p do
            match parseAck line with
            | Some a -> m.[a.fingerprint] <- a
            | None -> ()
        with _ -> ()
      m)
  interface IAckStore with
    member _.Ack(tid, a) =
      lock sync (fun () ->
        let b = bucket tid
        b.[a.fingerprint] <- a
        try
          File.AppendAllText(pathFor tid, serialiseAck a + "\n")
        with _ -> ())
    member _.IsAcked(tid, fp) =
      let b = bucket tid
      b.ContainsKey fp
    member _.List(tid, fp) =
      let b = bucket tid
      match b.TryGetValue fp with
      | true, a -> [| a |]
      | _ -> [||]

// -- on-call resolution -----------------------------------------------------

/// Returns the user-id on call for `sched` at `now`. Active overrides
/// win; otherwise the first rotation round-robins through its members.
let whoIsOnCall (sched : Schedule) (now : int64) : string option =
  let active =
    sched.overrides
    |> Array.tryFind (fun o -> o.startsAt <= now && now <= o.endsAt)
  match active with
  | Some o -> Some o.userId
  | None ->
    sched.rotations
    |> Array.tryPick (fun r ->
      if r.members.Length = 0 || r.periodMs <= 0L then None
      else
        let elapsed = max 0L (now - r.startAt)
        let idx = int ((elapsed / r.periodMs) % int64 r.members.Length)
        Some r.members.[idx])

let private resolveTargets (catalog : Catalog) (targets : Target[]) (now : int64) : string[] =
  let usersById =
    catalog.users |> Array.map (fun u -> u.id, u) |> Map.ofArray
  let schedsById =
    catalog.schedules |> Array.map (fun s -> s.id, s) |> Map.ofArray
  let out = ResizeArray<string>()
  let seen = HashSet<string>()
  let pushUser uid =
    match Map.tryFind uid usersById with
    | Some u -> for rid in u.receiverIds do if seen.Add rid then out.Add rid
    | None -> ()
  for t in targets do
    match t with
    | TgtReceiver rid -> if seen.Add rid then out.Add rid
    | TgtUser uid -> pushUser uid
    | TgtSchedule sid ->
      match Map.tryFind sid schedsById with
      | Some s ->
        match whoIsOnCall s now with
        | Some uid -> pushUser uid
        | None -> ()
      | None -> ()
  out.ToArray()

// -- Escalator (implements Routing.IEscalator) ------------------------------

type Escalator(catalogStore : ICatalogStore, ackStore : IAckStore) =
  let policyOf (tid : TenantId) (pid : string) =
    let c = catalogStore.Get tid
    c.policies |> Array.tryFind (fun p -> p.id = pid), c
  interface IEscalator with
    member _.StepCount(tid, pid) =
      match policyOf tid pid with
      | Some p, _ -> p.steps.Length
      | None,  _ -> 0
    member _.ResolveStep(tid, pid, idx) =
      match policyOf tid pid with
      | Some p, c when idx >= 0 && idx < p.steps.Length ->
        let step = p.steps.[idx]
        Some (step.delayMs, resolveTargets c step.targets (nowMs ()))
      | _ -> None
    member _.IsAcked(tid, fingerprints) =
      fingerprints |> Set.exists (fun fp -> ackStore.IsAcked(tid, fp))

// -- REST -------------------------------------------------------------------

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK | 201 -> Suave.Successful.CREATED
    | 204 -> fun _ -> Suave.Successful.NO_CONTENT
    | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
    | _   -> Suave.ServerErrors.INTERNAL_ERROR
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

let private serialiseWhoIs (sid : string) (userId : string option) =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("scheduleId", sid)
    (match userId with
     | Some u -> w.WriteString("userId", u)
     | None   -> w.WriteNull "userId")
    w.WriteNumber("ts", nowMs ())
    w.WriteEndObject())
  Encoding.UTF8.GetString(ms.ToArray())

let private serialiseAcks (acks : Acknowledgement[]) =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartArray()
    for a in acks do
      w.WriteStartObject()
      w.WriteString("fingerprint", a.fingerprint)
      w.WriteString("user", a.user)
      w.WriteNumber("ackedAt", a.ackedAt)
      w.WriteEndObject()
    w.WriteEndArray())
  Encoding.UTF8.GetString(ms.ToArray())

let private parseAckBody (json : string) : Result<string, string> =
  try
    use doc = JsonDocument.Parse json
    match readStr doc.RootElement "user" with
    | Some u when u.Trim() <> "" -> Result.Ok u
    | _ -> Result.Error "missing user"
  with ex -> Result.Error ex.Message

let webPart (multiTenant : bool)
            (catalogStore : ICatalogStore)
            (ackStore     : IAckStore) : WebPart =
  let withTenant (handler : TenantId -> WebPart) : WebPart =
    fun ctx -> async {
      match resolveTenant multiTenant ctx with
      | None -> return! errJson 401 "no tenant" ctx
      | Some tid -> return! handler tid ctx
    }
  choose [
    GET >=> path "/api/oncall/catalog" >=>
      withTenant (fun tid ->
        jsonResp 200 (serialiseCatalog (catalogStore.Get tid)))
    PUT >=> path "/api/oncall/catalog" >=>
      withTenant (fun tid ->
        fun ctx -> async {
          match parseCatalog (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid catalog: " + e) ctx
          | Result.Ok c ->
            catalogStore.Set(tid, c)
            return! jsonResp 200 (serialiseCatalog c) ctx
        })
    GET >=> pathScan "/api/oncall/whoison/%s" (fun sid ->
      withTenant (fun tid ->
        let cat = catalogStore.Get tid
        match cat.schedules |> Array.tryFind (fun s -> s.id = sid) with
        | None -> errJson 404 "no such schedule"
        | Some s -> jsonResp 200 (serialiseWhoIs sid (whoIsOnCall s (nowMs ())))))
    POST >=> pathScan "/api/alerts/%s/ack" (fun fp ->
      withTenant (fun tid ->
        fun ctx -> async {
          match parseAckBody (readBody ctx.request) with
          | Result.Error e -> return! errJson 400 ("invalid ack: " + e) ctx
          | Result.Ok u ->
            let a = { fingerprint = fp; user = u; ackedAt = nowMs () }
            ackStore.Ack(tid, a)
            return! jsonResp 201 (serialiseAcks [| a |]) ctx
        }))
    GET >=> pathScan "/api/alerts/%s/acks" (fun fp ->
      withTenant (fun tid ->
        jsonResp 200 (serialiseAcks (ackStore.List(tid, fp)))))
  ]
