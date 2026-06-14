module PulseBoard.AgentGroups

// Phase 13.5 — desired-config push for the agent fleet.
//
// Each tenant has zero-or-more `AgentGroup` records. A group carries a
// TOML "overlay" — a partial pulseagent config that should be merged on
// top of the agent's local /etc/pulseagent/agent.toml at runtime. Every
// edit bumps the group's `version` so the agent's `configHash` can be
// compared cheaply to detect drift.
//
// Every agent belongs to exactly one group (default = `"default"`).
// `GET /api/agent/v1/config` (in AgentApi.fs) resolves the calling
// agent's group via its tenant_id and returns the overlay + version +
// signature. The agent's `config_poller` polls the endpoint, verifies
// the signature against the shared HMAC key it learned at enrollment,
// and triggers an in-process pipeline rebuild when the version moves.
//
// Signing: HMAC-SHA256 keyed on a per-process server secret. The agent
// receives the key once during enrollment (added to the enroll response
// alongside agentId/apiKey) and stores it next to credentials.json.
// Using HMAC (vs Ed25519) avoids a new crypto dependency on either side
// and is sufficient because the agent is already mutually authenticated
// via its bearer; the signature is defence-in-depth against an upstream
// proxy mangling the body.
//
// Tenant-default-group rule: the `"default"` group is auto-materialised
// on first read for any tenant that doesn't have one. This is the path
// the dogfood / Fly deployments rely on, where agents enrol with a
// shared tenant API key and are never assigned to a specific group.

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open PulseBoard.Tenancy

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

// -- model ------------------------------------------------------------------

/// Stable identifier for the auto-materialised default group.
[<Literal>]
let DefaultGroupId = "default"

[<NoComparison>]
type AgentGroup =
  { id          : string        // stable per-tenant id, e.g. "default"
    name        : string        // human label
    overlayToml : string        // partial agent.toml; "" = no overlay
    version     : int           // monotonic, bumped on every Upsert
    updatedAt   : int64 }       // unix ms

let emptyDefaultGroup () : AgentGroup =
  { id          = DefaultGroupId
    name        = "Default"
    overlayToml = ""
    version     = 1
    updatedAt   = nowMs() }

type IAgentGroupStore =
  /// List groups for a tenant. The default group is included even if
  /// the tenant has never explicitly created one (auto-materialised on
  /// first read).
  abstract List   : tenant:TenantId -> AgentGroup[]
  /// Look up a single group. Returns the default group for `"default"`
  /// even when the tenant has no row yet.
  abstract TryGet : tenant:TenantId * id:string -> AgentGroup option
  /// Upsert; bumps `version` and `updatedAt`. Returns the stored record.
  /// Group id `"default"` is preserved; any other id is taken verbatim.
  abstract Upsert : tenant:TenantId * group:AgentGroup -> AgentGroup
  /// Delete a non-default group. Returns true if a row was removed.
  /// Refuses to delete the default group (it must always exist).
  abstract Delete : tenant:TenantId * id:string -> bool

// -- in-memory --------------------------------------------------------------

type InMemoryAgentGroupStore() =
  // (tenantId, groupId) -> group
  let groups = ConcurrentDictionary<string * string, AgentGroup>()

  let key (TenantId t) id = (t, id)

  let getOrDefault (tenant : TenantId) (id : string) : AgentGroup option =
    match groups.TryGetValue (key tenant id) with
    | true, g -> Some g
    | _ when id = DefaultGroupId -> Some (emptyDefaultGroup ())
    | _ -> None

  interface IAgentGroupStore with
    member _.List tenant =
      let (TenantId t) = tenant
      let explicit =
        groups
        |> Seq.choose (fun kv ->
            let (tid, _) = kv.Key
            if tid = t then Some kv.Value else None)
        |> Seq.toArray
      if explicit |> Array.exists (fun g -> g.id = DefaultGroupId) then explicit
      else Array.append [| emptyDefaultGroup () |] explicit

    member _.TryGet (tenant, id) = getOrDefault tenant id

    member _.Upsert (tenant, group) =
      let id = if String.IsNullOrWhiteSpace group.id then DefaultGroupId else group.id
      let k  = key tenant id
      let stored =
        groups.AddOrUpdate(
          k,
          (fun _ ->
            { group with
                id        = id
                version   = max 1 group.version
                updatedAt = nowMs() }),
          (fun _ prev ->
            { group with
                id        = id
                version   = prev.version + 1
                updatedAt = nowMs() }))
      stored

    member _.Delete (tenant, id) =
      if id = DefaultGroupId then false
      else groups.TryRemove(key tenant id) |> fst

// -- signing ----------------------------------------------------------------

/// Canonical bytes signed when serving a config to an agent. Both sides
/// must produce identical bytes or verification fails.
let canonicalBytes (tenantId : string) (groupId : string) (version : int) (body : string) : byte[] =
  let s = sprintf "v1|%s|%s|%d|%s" tenantId groupId version body
  Encoding.UTF8.GetBytes s

/// Hex HMAC-SHA256 of `canonicalBytes` using `secret` (raw bytes).
let signCanonical (secret : byte[]) (tenantId : string) (groupId : string)
                  (version : int) (body : string) : string =
  use h = new HMACSHA256(secret)
  let mac = h.ComputeHash(canonicalBytes tenantId groupId version body)
  Convert.ToHexString(mac)

/// Constant-time hex comparison.
let verifyHex (expectedHex : string) (actualHex : string) : bool =
  if isNull expectedHex || isNull actualHex then false
  elif expectedHex.Length <> actualHex.Length then false
  else
    let a = Encoding.ASCII.GetBytes expectedHex
    let b = Encoding.ASCII.GetBytes actualHex
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

/// Generate a 32-byte HMAC key, base64-encoded for env / file storage.
let generateSecret () : string =
  let bytes = RandomNumberGenerator.GetBytes 32
  Convert.ToBase64String bytes

/// Load or initialise the per-process HMAC key. If `envValue` is a
/// non-empty base64 string we use it; otherwise we mint and persist a
/// fresh one to `path`. Returns the raw key bytes plus the base64 form
/// (the latter is handed to the agent at enrollment so it can verify
/// signatures without a second round-trip).
let loadOrInitSecret (envValue : string option) (path : string) : byte[] * string =
  let fromB64 (s : string) =
    try
      let raw = Convert.FromBase64String s
      if raw.Length >= 16 then Some (raw, s) else None
    with _ -> None
  match envValue |> Option.bind fromB64 with
  | Some kv -> kv
  | None ->
    if File.Exists path then
      match fromB64 (File.ReadAllText(path).Trim()) with
      | Some kv -> kv
      | None ->
        let b64 = generateSecret ()
        File.WriteAllText(path, b64)
        Convert.FromBase64String b64, b64
    else
      let dir = Path.GetDirectoryName path
      if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
      let b64 = generateSecret ()
      File.WriteAllText(path, b64)
      Convert.FromBase64String b64, b64

// -- JSON codecs ------------------------------------------------------------

let private writeGroup (w : Utf8JsonWriter) (g : AgentGroup) =
  w.WriteStartObject()
  w.WriteString("id",          g.id)
  w.WriteString("name",        g.name)
  w.WriteString("overlayToml", g.overlayToml)
  w.WriteNumber("version",     g.version)
  w.WriteNumber("updatedAt",   g.updatedAt)
  w.WriteEndObject()

let serialiseGroup (g : AgentGroup) : string =
  use ms = new MemoryStream()
  use w  = new Utf8JsonWriter(ms)
  writeGroup w g
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

let serialiseGroups (gs : AgentGroup[]) : string =
  use ms = new MemoryStream()
  use w  = new Utf8JsonWriter(ms)
  w.WriteStartArray()
  for g in gs do writeGroup w g
  w.WriteEndArray()
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

/// Parse an inbound PUT/POST body. Updates preserve the existing id (if
/// present) and version (the store bumps it on write).
let parseGroup (existing : AgentGroup option) (json : string) : Result<AgentGroup, string> =
  try
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let str (n : string) =
      match root.TryGetProperty n with
      | true, el when el.ValueKind = JsonValueKind.String -> el.GetString()
      | _ -> ""
    let id =
      match existing with
      | Some e -> e.id
      | None ->
        let v = str "id"
        if String.IsNullOrWhiteSpace v then Guid.NewGuid().ToString("N") else v
    let name =
      let v = str "name"
      if String.IsNullOrWhiteSpace v then id else v
    let overlay = str "overlayToml"
    Ok { id          = id
         name        = name
         overlayToml = overlay
         version     = match existing with Some e -> e.version | None -> 0
         updatedAt   = nowMs() }
  with ex -> Error (sprintf "invalid JSON: %s" ex.Message)
