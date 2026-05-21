module PulseBoard.Tenancy

open System
open System.Collections.Concurrent
open System.Security.Cryptography

// Phase 1 (foundations). In-memory tenant model + scoped API keys. Postgres
// implementation of `ITenantStore` lands later behind the same interface —
// see PLAN.md Phase 1 step 1.

type TenantId = TenantId of string
type ApiKeyId = ApiKeyId of string

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
  abstract MarkUsed           : ApiKeyId -> unit

type InMemoryTenantStore () =
  let tenants = ConcurrentDictionary<TenantId, Tenant>()
  let bySlug  = ConcurrentDictionary<string, TenantId>(StringComparer.OrdinalIgnoreCase)
  let keys    = ConcurrentDictionary<ApiKeyId, ApiKeyRecord>()

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

    member _.MarkUsed id =
      match keys.TryGetValue id with
      | true, r -> r.lastUsedAt := Some DateTimeOffset.UtcNow
      | _ -> ()

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
