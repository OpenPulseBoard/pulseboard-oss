module PulseBoard.Tenancy

open System
open System.Collections.Concurrent
open System.Security.Cryptography

// Phase 1 (foundations). In-memory tenant model + scoped API keys. Postgres
// implementation of `ITenantStore` lands later behind the same interface —
// see PLAN.md Phase 1 step 1.

type TenantId = TenantId of string
type ApiKeyId = ApiKeyId of string
type UserId   = UserId of string

type Role =
  | Viewer
  | Editor
  | Admin
  | Billing

/// Scopes presented at request time. Stored as a [Flags] bitfield so a
/// single API key can carry, e.g., `Ingest ||| Query` without the indirection
/// of a set type.
[<Flags>]
type Scope =
  | None    = 0
  | Ingest  = 1
  | Query   = 2
  | Admin   = 4

/// Canonical role → scope mapping. Single source of truth used by both the
/// Admin REST default-scope picker and the OIDC session minter so the two
/// surfaces can't drift.
let scopesForRole = function
  | Viewer  -> Scope.Query
  | Editor  -> Scope.Ingest ||| Scope.Query
  | Admin   -> Scope.Ingest ||| Scope.Query ||| Scope.Admin
  | Billing -> Scope.None

[<NoComparison; NoEquality>]
type Tenant =
  { id        : TenantId
    slug      : string
    createdAt : DateTimeOffset }

[<NoComparison; NoEquality>]
type ApiKeyRecord =
  { id            : ApiKeyId
    tenantId      : TenantId
    label         : string
    role          : Role
    scopes        : Scope
    hashAlgorithm : string
    iterations    : int
    salt          : byte[]
    hash          : byte[]
    createdAt     : DateTimeOffset
    lastUsedAt    : DateTimeOffset option ref }

[<NoComparison; NoEquality>]
type TenantCtx =
  { tenant   : Tenant
    apiKeyId : ApiKeyId
    role     : Role
    scopes   : Scope }

/// Persistent record for an SSO-authenticated user. Identity is the
/// (issuer, subject) tuple from the upstream id_token — stable per IdP and
/// independent of email rename. Email is captured for display/override
/// matching only.
[<NoComparison; NoEquality>]
type UserRecord =
  { id          : UserId
    tenantId    : TenantId
    issuer      : string
    subject     : string
    email       : string option
    role        : Role
    createdAt   : DateTimeOffset
    lastLoginAt : DateTimeOffset option ref }

[<NoComparison; NoEquality>]
type IssuedKey =
  { record    : ApiKeyRecord
    /// Plaintext `pk_<id>.<secret>` representation. Shown once at creation;
    /// never recoverable afterwards.
    plaintext : string }

let private rng = RandomNumberGenerator.Create()

let private genBytes (n : int) =
  let b = Array.zeroCreate n
  rng.GetBytes b
  b

let private toBase64Url (b : byte[]) =
  Convert.ToBase64String(b)
    .Replace('+', '-')
    .Replace('/', '_')
    .TrimEnd('=')

let private newTenantId () = TenantId (toBase64Url (genBytes 9))
let private newApiKeyId () = ApiKeyId (toBase64Url (genBytes 9))
let private newUserId ()   = UserId   (toBase64Url (genBytes 9))

let private defaultIterations = 100_000

let private pbkdf2 (secret : string) (salt : byte[]) (iterations : int) =
  Rfc2898DeriveBytes.Pbkdf2(
    password = secret,
    salt = ReadOnlySpan salt,
    iterations = iterations,
    hashAlgorithm = HashAlgorithmName.SHA256,
    outputLength = 32)

/// Parse a presented `pk_<id>.<secret>` token. Returns `None` for any
/// malformed input (wrong prefix, missing/empty halves, etc.).
let tryParsePresented (raw : string) : (ApiKeyId * string) option =
  if isNull raw then None
  else
    let s = raw.Trim()
    if not (s.StartsWith("pk_", StringComparison.Ordinal)) then None
    else
      let rest = s.Substring 3
      let dot = rest.IndexOf '.'
      if dot <= 0 || dot = rest.Length - 1 then None
      else
        Some (ApiKeyId (rest.Substring(0, dot)), rest.Substring(dot + 1))

/// `true` iff `have` contains every bit set in `need`.
let hasScope (have : Scope) (need : Scope) =
  (int have &&& int need) = int need

type ITenantStore =
  abstract CreateTenant       : slug : string -> Tenant
  abstract TryGetTenant       : TenantId -> Tenant option
  abstract TryGetTenantBySlug : string -> Tenant option
  abstract Tenants            : unit -> Tenant[]
  abstract IssueApiKey        :
    tenantId : TenantId * label : string * role : Role * scopes : Scope ->
      IssuedKey
  abstract TryGetApiKey       : ApiKeyId -> ApiKeyRecord option
  abstract ApiKeysFor         : TenantId -> ApiKeyRecord[]
  abstract MarkUsed           : ApiKeyId -> unit
  // -- SSO users ------------------------------------------------------------
  /// Lookup an existing user by (issuer, subject). Returns `None` for a
  /// first-time login.
  abstract TryGetUser         : issuer : string * subject : string -> UserRecord option
  abstract TryGetUserById     : UserId -> UserRecord option
  /// Insert-or-update on first login. Email is refreshed each time; role
  /// is sticky (use `UpdateUserRole` to change it). Updates `lastLoginAt`.
  abstract UpsertUser         :
    tenantId : TenantId * issuer : string * subject : string *
    email : string option * roleIfNew : Role -> UserRecord
  abstract UpdateUserRole     : UserId * Role -> UserRecord option
  abstract UsersFor           : TenantId -> UserRecord[]

type InMemoryTenantStore () =
  let tenants = ConcurrentDictionary<TenantId, Tenant>()
  let bySlug  = ConcurrentDictionary<string, TenantId>(StringComparer.OrdinalIgnoreCase)
  let keys    = ConcurrentDictionary<ApiKeyId, ApiKeyRecord>()
  let users   = ConcurrentDictionary<UserId, UserRecord>()
  // Secondary index: "<issuer>|<subject>" -> UserId. Case-sensitive (both
  // halves are opaque IdP-issued strings).
  let userBySub = ConcurrentDictionary<string, UserId>(StringComparer.Ordinal)
  let subKey (issuer : string) (subject : string) = issuer + "|" + subject

  interface ITenantStore with
    member _.CreateTenant slug =
      let slug = (if isNull slug then "" else slug.Trim().ToLowerInvariant())
      if slug.Length = 0 then invalidArg "slug" "empty"
      // Idempotent: re-creating by slug returns the existing record.
      match bySlug.TryGetValue slug with
      | true, id -> tenants.[id]
      | _ ->
        let t =
          { id = newTenantId ()
            slug = slug
            createdAt = DateTimeOffset.UtcNow }
        tenants.[t.id] <- t
        bySlug.[slug]  <- t.id
        t

    member _.TryGetTenant id =
      match tenants.TryGetValue id with
      | true, t -> Some t
      | _ -> None

    member _.TryGetTenantBySlug slug =
      let slug = (if isNull slug then "" else slug.Trim().ToLowerInvariant())
      match bySlug.TryGetValue slug with
      | true, id ->
        match tenants.TryGetValue id with
        | true, t -> Some t
        | _ -> None
      | _ -> None

    member _.Tenants () = tenants.Values |> Seq.toArray

    member _.IssueApiKey (tenantId, label, role, scopes) =
      if not (tenants.ContainsKey tenantId) then
        invalidArg "tenantId" "tenant not found"
      let id = newApiKeyId ()
      let (ApiKeyId idStr) = id
      let secret = toBase64Url (genBytes 32)
      let salt   = genBytes 16
      let hash   = pbkdf2 secret salt defaultIterations
      let record =
        { id            = id
          tenantId      = tenantId
          label         = label
          role          = role
          scopes        = scopes
          hashAlgorithm = "PBKDF2-HMACSHA256"
          iterations    = defaultIterations
          salt          = salt
          hash          = hash
          createdAt     = DateTimeOffset.UtcNow
          lastUsedAt    = ref None }
      keys.[id] <- record
      { record = record; plaintext = sprintf "pk_%s.%s" idStr secret }

    member _.TryGetApiKey id =
      match keys.TryGetValue id with
      | true, r -> Some r
      | _ -> None

    member _.ApiKeysFor tenantId =
      keys.Values
      |> Seq.filter (fun r -> r.tenantId = tenantId)
      |> Seq.sortBy (fun r -> r.createdAt)
      |> Seq.toArray

    member _.MarkUsed id =
      match keys.TryGetValue id with
      | true, r -> r.lastUsedAt := Some DateTimeOffset.UtcNow
      | _ -> ()

    member _.TryGetUser (issuer, subject) =
      match userBySub.TryGetValue (subKey issuer subject) with
      | true, uid ->
        match users.TryGetValue uid with
        | true, u -> Some u
        | _ -> None
      | _ -> None

    member _.TryGetUserById id =
      match users.TryGetValue id with
      | true, u -> Some u
      | _ -> None

    member this.UpsertUser (tenantId, issuer, subject, email, roleIfNew) =
      if not (tenants.ContainsKey tenantId) then
        invalidArg "tenantId" "tenant not found"
      let now = DateTimeOffset.UtcNow
      match (this :> ITenantStore).TryGetUser (issuer, subject) with
      | Some existing ->
        // Refresh email opportunistically; role is sticky.
        let updated = { existing with email = email; tenantId = tenantId }
        updated.lastLoginAt := Some now
        users.[existing.id] <- updated
        updated
      | None ->
        let id = newUserId ()
        let rec' =
          { id          = id
            tenantId    = tenantId
            issuer      = issuer
            subject     = subject
            email       = email
            role        = roleIfNew
            createdAt   = now
            lastLoginAt = ref (Some now) }
        users.[id] <- rec'
        userBySub.[subKey issuer subject] <- id
        rec'

    member _.UpdateUserRole (id, role) =
      match users.TryGetValue id with
      | true, u ->
        let updated = { u with role = role }
        users.[id] <- updated
        Some updated
      | _ -> None

    member _.UsersFor tenantId =
      users.Values
      |> Seq.filter (fun u -> u.tenantId = tenantId)
      |> Seq.sortBy (fun u -> u.createdAt)
      |> Seq.toArray

/// Verify a presented `pk_<id>.<secret>` against `store`. Performs a
/// fixed-cost PBKDF2 on an unknown key id to avoid trivially revealing
/// id-existence via response timing.
let verify (store : ITenantStore) (presented : string) : TenantCtx option =
  match tryParsePresented presented with
  | None -> None
  | Some (id, secret) ->
    match store.TryGetApiKey id with
    | None ->
      let salt = Array.zeroCreate 16
      pbkdf2 secret salt defaultIterations |> ignore
      None
    | Some record ->
      let candidate = pbkdf2 secret record.salt record.iterations
      if CryptographicOperations.FixedTimeEquals(
           ReadOnlySpan candidate, ReadOnlySpan record.hash) then
        match store.TryGetTenant record.tenantId with
        | Some t ->
          store.MarkUsed record.id
          Some
            { tenant   = t
              apiKeyId = record.id
              role     = record.role
              scopes   = record.scopes }
        | None -> None
      else
        None
