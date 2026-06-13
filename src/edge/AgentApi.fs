module PulseBoard.AgentApi

// Phase 13 — PulseAgent server side
//
// Provides:
//   POST /api/agent/v1/enroll    (unauthenticated) — token exchange → agentId + apiKey
//   POST /api/agent/v1/checkin   (bearer auth)     — heartbeat + metadata
//   GET  /api/agent/v1/config    (bearer auth)     — desired config push
//   GET  /api/agents             (tenant auth)     — fleet list
//   POST /api/agents/token       (tenant auth)     — generate enrollment token (30-min TTL)

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy

// ---------------------------------------------------------------------------
// Domain types
// ---------------------------------------------------------------------------

type AgentId = AgentId of string

type AgentRecord =
  { id         : string
    tenantId   : string
    hostname   : string
    version    : string
    configHash : string
    lastSeen   : int64   // unix ms
    enrolledAt : int64   // unix ms
  }

type EnrollToken =
  { token     : string
    tenantId  : string
    expiresAt : int64  // unix ms
  }

// ---------------------------------------------------------------------------
// Store interface
// ---------------------------------------------------------------------------

type IAgentStore =
  /// Idempotent by (tenantId, hostname): if a record already exists for
  /// that pair the api_key is rotated and the existing id is returned, so
  /// repeat enrollments from the same host don't accumulate duplicate
  /// rows on the agents page.
  abstract Enroll       : tenantId:string * hostname:string * version:string -> AgentRecord
  abstract Checkin      : agentId:string * version:string * configHash:string * metaJson:string -> bool
  abstract List         : tenantId:string -> AgentRecord list
  abstract TryGet       : agentId:string -> AgentRecord option
  abstract ValidateKey  : agentId:string * apiKey:string -> bool
  abstract GenerateToken: tenantId:string -> EnrollToken
  abstract RedeemToken  : token:string -> string option   // returns tenantId

// ---------------------------------------------------------------------------
// In-memory implementation
// ---------------------------------------------------------------------------

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private generateId    () = Guid.NewGuid().ToString("N")
let private generateKey   () =
  let bytes = RandomNumberGenerator.GetBytes 32
  "ak_" + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')
let private generateToken () =
  let bytes = RandomNumberGenerator.GetBytes 20
  "et_" + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')

let private constantTimeEquals (a : string) (b : string) =
  let ab = Encoding.UTF8.GetBytes a
  let bb = Encoding.UTF8.GetBytes b
  if ab.Length = bb.Length then
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan ab, ReadOnlySpan bb)
  else
    CryptographicOperations.FixedTimeEquals(ReadOnlySpan ab, ReadOnlySpan ab) |> ignore
    false

type InMemoryAgentStore() =
  // agentId → (record, apiKeyHash)
  let agents  = ConcurrentDictionary<string, AgentRecord * string>()
  // token → EnrollToken
  let tokens  = ConcurrentDictionary<string, EnrollToken>()

  interface IAgentStore with
    member _.Enroll(tenantId, hostname, version) =
      let apiKey  = generateKey()
      let keyHash = SHA256.HashData(Encoding.UTF8.GetBytes apiKey) |> Convert.ToHexString
      let existing =
        agents.Values
        |> Seq.tryFind (fun (r, _) -> r.tenantId = tenantId && r.hostname = hostname)
      match existing with
      | Some (r, _) ->
        let updated = { r with version = version; lastSeen = nowMs() }
        agents.[r.id] <- (updated, keyHash)
        { updated with configHash = apiKey }
      | None ->
        let id = generateId()
        let record =
          { id         = id
            tenantId   = tenantId
            hostname   = hostname
            version    = version
            configHash = ""
            lastSeen   = nowMs()
            enrolledAt = nowMs() }
        agents.[id] <- (record, keyHash)
        // Return record with the raw api key embedded temporarily in `configHash`
        // field so the caller can extract it before discarding.
        { record with configHash = apiKey }

    member _.Checkin(agentId, version, configHash, _metaJson) =
      match agents.TryGetValue agentId with
      | true, (r, k) ->
        let updated = { r with version = version; configHash = configHash; lastSeen = nowMs() }
        agents.[agentId] <- (updated, k)
        true
      | _ -> false

    member _.List(tenantId) =
      agents.Values
      |> Seq.choose (fun (r, _) -> if r.tenantId = tenantId then Some r else None)
      |> Seq.toList

    member _.TryGet(agentId) =
      match agents.TryGetValue agentId with
      | true, (r, _) -> Some r
      | _ -> None

    member _.ValidateKey(agentId, apiKey) =
      match agents.TryGetValue agentId with
      | true, (_, storedHash) ->
        let candidateHash =
          SHA256.HashData(Encoding.UTF8.GetBytes apiKey) |> Convert.ToHexString
        constantTimeEquals storedHash candidateHash
      | _ -> false

    member _.GenerateToken(tenantId) =
      let t =
        { token     = generateToken()
          tenantId  = tenantId
          expiresAt = nowMs() + 30L * 60L * 1000L }
      tokens.[t.token] <- t
      t

    member _.RedeemToken(token) =
      match tokens.TryGetValue token with
      | true, t ->
        if nowMs() <= t.expiresAt then
          tokens.TryRemove token |> ignore
          Some t.tenantId
        else
          tokens.TryRemove token |> ignore
          None
      | _ -> None

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------

let private readBody (ctx : HttpContext) =
  Encoding.UTF8.GetString ctx.request.rawForm

let private jsonOk (obj : obj) : WebPart =
  let body = JsonSerializer.Serialize obj
  Writers.setHeader "Content-Type" "application/json; charset=utf-8"
  >=> OK body

let private parseBearerToken (ctx : HttpContext) : (string * string) option =
  // Expects: Authorization: Bearer <agentId>:<apiKey>
  match ctx.request.header "authorization" with
  | Choice1Of2 v when v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
    let raw = v.Substring 7
    let i   = raw.IndexOf ':'
    if i > 0 then Some (raw.Substring(0, i), raw.Substring(i + 1))
    else None
  | _ -> None

let private agentAuth (store : IAgentStore) (handler : AgentRecord -> WebPart) : WebPart =
  fun ctx ->
    async {
      match parseBearerToken ctx with
      | None -> return! FORBIDDEN "Missing or invalid Authorization header" ctx
      | Some (agentId, key) ->
        if store.ValidateKey(agentId, key) then
          match store.TryGet agentId with
          | Some r -> return! handler r ctx
          | None   -> return! NOT_FOUND "Agent not found" ctx
        else
          return! FORBIDDEN "Invalid credentials" ctx
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

// POST /api/agent/v1/enroll
// Two ways to authenticate:
//   1. Body carries `token` — single-use 30-min enrollment token previously
//      issued by an operator via POST /api/agents/token.
//   2. `Authorization: Bearer <tenant pk_...>` — a tenant API key already
//      attached upstream by `Auth.resolveApiKey`. Useful when the same
//      shared key is planted on every machine (dogfood / Fly secrets).
// Idempotent by (tenantId, hostname): repeat enrollments rotate the
// per-agent api_key but reuse the same agentId, keeping the agents page
// clean across machine redeploys.
let private handleEnroll (store : IAgentStore) : WebPart =
  fun ctx -> async {
    let body = Encoding.UTF8.GetString ctx.request.rawForm
    try
      use doc      = JsonDocument.Parse body
      let root     = doc.RootElement
      let getStr (n : string) =
        match root.TryGetProperty n with
        | true, el -> el.GetString()
        | _ -> ""
      let token    = getStr "token"
      let hostname = getStr "hostname"
      let version  = getStr "version"

      let tenantIdFromKey =
        PulseBoard.Rbac.tryGetTenant ctx
        |> Option.map (fun tc -> let (TenantId t) = tc.tenant.id in t)
      let tenantIdFromToken =
        if String.IsNullOrEmpty token then None
        else store.RedeemToken token

      match tenantIdFromToken |> Option.orElse tenantIdFromKey with
      | None ->
        return! UNAUTHORIZED
                  "Provide an enrollment token in the request body or a tenant API key via Authorization: Bearer"
                  ctx
      | Some tenantId ->
        let record = store.Enroll(tenantId, hostname, version)
        // configHash field temporarily carries the raw API key — extract and
        // clear it from the response object.
        let apiKey = record.configHash
        return! jsonOk {| agentId = record.id; apiKey = apiKey |} ctx
    with ex ->
      return! BAD_REQUEST (sprintf "Invalid JSON: %s" ex.Message) ctx
  }

// POST /api/agent/v1/checkin
let private handleCheckin (store : IAgentStore) : WebPart =
  agentAuth store (fun agentRecord ->
    request (fun req ->
      let body       = Encoding.UTF8.GetString req.rawForm
      let configHash =
        try
          use doc = JsonDocument.Parse body
          match doc.RootElement.TryGetProperty "configHash" with
          | true, el -> el.GetString()
          | _ -> ""
        with _ -> ""
      let version =
        try
          use doc = JsonDocument.Parse body
          match doc.RootElement.TryGetProperty "version" with
          | true, el -> el.GetString()
          | _ -> agentRecord.version
        with _ -> agentRecord.version
      store.Checkin(agentRecord.id, version, configHash, body) |> ignore
      OK ""))

// GET /api/agent/v1/config
// Currently returns an empty config; in a future phase this would return
// a diff of the desired configuration for remote config push.
let private handleGetConfig (store : IAgentStore) : WebPart =
  agentAuth store (fun _ ->
    jsonOk {| config = {||} |})

// GET /api/agents  (tenant-scoped; requires PulseBoard tenant auth)
let private handleListAgents (store : IAgentStore) (tid : TenantId) : WebPart =
  let (TenantId tenantIdStr) = tid
  let agents =
    store.List tenantIdStr
    |> List.map (fun r ->
        {| id         = r.id
           hostname   = r.hostname
           version    = r.version
           configHash = r.configHash
           lastSeen   = r.lastSeen
           enrolledAt = r.enrolledAt |})
  jsonOk agents

// POST /api/agents/token  (tenant-scoped)
let private handleGenerateToken (store : IAgentStore) (tid : TenantId) : WebPart =
  let (TenantId tenantIdStr) = tid
  let t = store.GenerateToken tenantIdStr
  jsonOk {| token = t.token; expiresAt = t.expiresAt |}

// ---------------------------------------------------------------------------
// Route wiring
// ---------------------------------------------------------------------------

/// `multiTenant` — when true, /api/agents/* requires a valid tenant session.
/// The enrollment endpoint (/api/agent/v1/enroll) is always unauthenticated
/// because the agent has no credentials yet at that point.
let webPart (multiTenant : bool) (store : IAgentStore) : WebPart =

  // Tenant-gated inner webpart (used only when multi-tenant is on).
  // When multi-tenant is off we still expose the endpoints but skip the
  // tenant lookup (single-tenant "admin" mode).
  let tenantGuard (handler : TenantId -> WebPart) : WebPart =
    if multiTenant then
      fun ctx ->
        async {
          match PulseBoard.Rbac.tryGetTenant ctx with
          | Some tc -> return! handler tc.tenant.id ctx
          | None    -> return! FORBIDDEN "Authentication required" ctx
        }
    else
      // Single-tenant: use a fixed tenant id
      handler (TenantId "default")

  choose [
    // Agent self-service (called by the agent binary)
    POST >=> path "/api/agent/v1/enroll"  >=> handleEnroll  store
    POST >=> path "/api/agent/v1/checkin" >=> handleCheckin store
    GET  >=> path "/api/agent/v1/config"  >=> handleGetConfig store

    // Portal fleet management (called by the SPA)
    GET  >=> path "/api/agents"       >=> tenantGuard (handleListAgents       store)
    POST >=> path "/api/agents/token" >=> tenantGuard (handleGenerateToken    store)
  ]
