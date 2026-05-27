module PulseBoard.CustomerAuth

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text

// Phase 10 — customer account identity for the hosted control plane.
//
// A *customer* is the person (or org) who owns N workspaces. Distinct from
// `users` / `memberships` in `Tenancy.fs`, which live on each workspace and
// model intra-tenant role assignments. This module is mounted on the apex
// host (`pulseboard.cloud`) and the provisioner — not on workspaces.
//
// This first cut intentionally ships:
//   * the core record types (`Customer`, `EmailToken`, `CustomerSession`),
//   * password hashing / verification (Argon2id, cloud-owned but kept
//     wire-compatible with the workspace-side algorithm tags),
//   * an `ICustomerStore` interface,
//   * an in-memory implementation suitable for dev + tests.
//
// The Postgres-backed impl ships in `PgCustomerStore.fs`; WebParts (signup,
// signin, email verify, GitHub OAuth) ship in subsequent modules. The
// schema migration lives next to the Postgres impl so dev builds with no
// Postgres still type-check and run.

// -- identifiers ------------------------------------------------------------

type CustomerId = CustomerId of string

let newCustomerId () : CustomerId =
  CustomerId (Guid.NewGuid().ToString "N")

/// Canonicalise an email address for equality/lookup. Trim + lowercase the
/// whole string. Deliberately conservative — we don't strip dots / +tags
/// since some providers honour those as separate addresses; an upstream
/// validator should reject obvious garbage before the value reaches here.
let canonEmail (raw : string) : string =
  if isNull raw then "" else raw.Trim().ToLowerInvariant()

// -- types ------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Customer =
  { id              : CustomerId
    /// Canonicalised (`canonEmail`); UNIQUE in the database.
    email           : string
    /// `None` until the customer clicks the verification link; gates
    /// signin until set when the customer has a password.
    emailVerifiedAt : DateTimeOffset option
    /// `None` for GitHub-only accounts. 32-byte Argon2id output.
    passwordHash    : byte[] option
    /// 16-byte random salt. `Some` iff `passwordHash` is `Some`.
    passwordSalt    : byte[] option
    /// Algorithm tag, e.g. `"argon2id:t=3,m=65536,p=2"`. `Some` iff
    /// `passwordHash` is `Some`. Stored alongside so OWASP parameter
    /// bumps don't break existing hashes.
    passwordAlgo    : string option
    /// GitHub numeric user id (the `id` field on `/user`, stable across
    /// renames). UNIQUE in the database; `None` if not linked.
    githubUserId    : int64 option
    createdAt       : DateTimeOffset
    updatedAt       : DateTimeOffset }

type EmailTokenPurpose =
  | Verify
  | Reset

let emailTokenPurposeText = function
  | Verify -> "verify"
  | Reset  -> "reset"

let tryParseEmailTokenPurpose (s : string) : EmailTokenPurpose option =
  match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
  | "verify" -> Some Verify
  | "reset"  -> Some Reset
  | _        -> None

[<NoComparison; NoEquality>]
type EmailToken =
  /// 32-byte SHA-256 of the random token handed to the user. The
  /// plaintext token is never persisted — it lives only in the email
  /// link. `tokenHash` is the lookup key.
  { tokenHash  : byte[]
    customerId : CustomerId
    purpose    : EmailTokenPurpose
    createdAt  : DateTimeOffset
    expiresAt  : DateTimeOffset
    consumedAt : DateTimeOffset option }

[<NoComparison; NoEquality>]
type CustomerSession =
  /// Server-side row that backs a refresh-token cookie. The short-lived
  /// access JWT does NOT have a session row — only refresh tokens do,
  /// so a stolen access token expires on its own and a stolen refresh
  /// token can be revoked here.
  { id               : string                  // opaque session UUID
    customerId       : CustomerId
    /// SHA-256 of the refresh-token plaintext (the plaintext only
    /// lives in the cookie, never in the database).
    refreshTokenHash : byte[]
    createdAt        : DateTimeOffset
    expiresAt        : DateTimeOffset
    revokedAt        : DateTimeOffset option
    userAgent        : string option
    ip               : string option }

// -- password hashing -------------------------------------------------------

/// 16 cryptographically-random bytes; sized to fit the Argon2id salt
/// recommendation (RFC 9106 §3.1).
let private newSalt () : byte[] =
  let s = Array.zeroCreate 16
  RandomNumberGenerator.Fill(Span s)
  s

/// Result of hashing a fresh password: the parameters needed to verify
/// it later. The caller persists all three on the `Customer` record.
[<NoComparison; NoEquality>]
type PasswordHash =
  { hash : byte[]
    salt : byte[]
    algo : string }

// Keep the cloud-side password hashing tags byte-for-byte compatible with
// the workspace-side tenant/api-key hashing tags so future migrations can
// reason about one shared on-disk shape even after the repo split.
let private argon2DefaultTime  = 3
let private argon2DefaultMemKb = 65_536
let private argon2DefaultPara  = 2

let private argon2idTag (time : int) (memKb : int) (para : int) =
  sprintf "argon2id:t=%d,m=%d,p=%d" time memKb para

let private argon2idHash (secret : string) (salt : byte[])
                         (time : int) (memKb : int) (para : int) : byte[] =
  use a =
    new Konscious.Security.Cryptography.Argon2id(
      Encoding.UTF8.GetBytes secret,
      Salt = salt,
      Iterations = time,
      MemorySize = memKb,
      DegreeOfParallelism = para)
  a.GetBytes 32

/// Hash a fresh password under the current Argon2id parameters
/// (OWASP 2024 baseline).
/// Generates a fresh random salt. The plaintext is never retained.
let hashPassword (plaintext : string) : PasswordHash =
  if isNull plaintext then
    nullArg "plaintext"
  let salt = newSalt ()
  let hash =
    argon2idHash
      plaintext
      salt
      argon2DefaultTime
      argon2DefaultMemKb
      argon2DefaultPara
  { hash = hash
    salt = salt
    algo =
      argon2idTag
        argon2DefaultTime
        argon2DefaultMemKb
        argon2DefaultPara }

/// Verify `plaintext` against a stored hash in constant time. Dispatches
/// on the algorithm tag so a future parameter bump (e.g. m=128 MiB)
/// keeps verifying historical hashes. Returns `false` on any parse
/// failure rather than throwing — callers should always do a fixed-cost
/// dummy verification on unknown-user paths to defeat timing oracles
/// (see `verifyDummy`).
let verifyPassword (plaintext : string) (salt : byte[]) (algo : string)
                   (expected : byte[]) : bool =
  if isNull plaintext || isNull salt || isNull algo || isNull expected then
    false
  else
    // Parse the self-describing algo tag so parameter bumps keep
    // verifying historical hashes without widening the public surface.
    if not (algo.StartsWith("argon2id", StringComparison.OrdinalIgnoreCase)) then
      false
    else
      let mutable t = argon2DefaultTime
      let mutable m = argon2DefaultMemKb
      let mutable p = argon2DefaultPara
      let payload =
        let i = algo.IndexOf ':'
        if i < 0 then "" else algo.Substring(i + 1)
      for chunk in payload.Split ',' do
        let kv = chunk.Trim().Split '='
        if kv.Length = 2 then
          let v = ref 0
          if Int32.TryParse(kv.[1], v) then
            match kv.[0].Trim().ToLowerInvariant() with
            | "t" -> t <- !v
            | "m" -> m <- !v
            | "p" -> p <- !v
            | _   -> ()
      let candidate = argon2idHash plaintext salt t m p
      CryptographicOperations.FixedTimeEquals(
        ReadOnlySpan candidate, ReadOnlySpan expected)

/// Fixed-cost dummy verification. Call on the "no such email" path so
/// the time-to-respond is indistinguishable from a real failed login.
/// Uses a constant dummy salt and the default parameters.
let verifyDummy (plaintext : string) : unit =
  let salt = Array.zeroCreate 16  // all-zero is fine — we discard the result
  argon2idHash
    (if isNull plaintext then "" else plaintext)
    salt
    argon2DefaultTime
    argon2DefaultMemKb
    argon2DefaultPara
  |> ignore

// -- email + refresh token helpers ------------------------------------------

/// Mint a fresh URL-safe random token of the requested byte length
/// (default 32 = 256 bits). Returns the plaintext (handed to the user
/// in the email link or session cookie) and its SHA-256 digest (what
/// the database row holds).
let mintToken (byteLen : int) : string * byte[] =
  let len = if byteLen <= 0 then 32 else byteLen
  let bytes = Array.zeroCreate len
  RandomNumberGenerator.Fill(Span bytes)
  let plaintext =
    Convert.ToBase64String(bytes)
      .TrimEnd '='
      |> fun s -> s.Replace('+', '-').Replace('/', '_')
  let digest = SHA256.HashData(ReadOnlySpan bytes)
  plaintext, digest

/// Hash a presented token from the URL/cookie back into the lookup
/// digest. Must use the same input bytes as `mintToken` produced — we
/// reverse the URL-safe transformation here.
let hashPresentedToken (plaintext : string) : byte[] =
  if isNull plaintext then SHA256.HashData(ReadOnlySpan(Array.empty : byte[]))
  else
    let s = plaintext.Trim().Replace('-', '+').Replace('_', '/')
    let padded =
      match s.Length % 4 with
      | 0 -> s
      | 2 -> s + "=="
      | 3 -> s + "="
      | _ -> s + "==="
    try
      let bytes = Convert.FromBase64String padded
      SHA256.HashData(ReadOnlySpan bytes)
    with _ ->
      // Garbage input — still return a digest of length 32 so the
      // caller's constant-time compare runs identically.
      SHA256.HashData(ReadOnlySpan(Encoding.UTF8.GetBytes plaintext))

// -- store interface --------------------------------------------------------

type ICustomerStore =
  // Customers
  abstract Insert        : Customer -> unit
  abstract Update        : CustomerId -> (Customer -> Customer) -> Customer option
  abstract TryGetById    : CustomerId -> Customer option
  abstract TryGetByEmail : string -> Customer option
  abstract TryGetByGithub: int64 -> Customer option
  abstract List          : unit -> Customer list

  // Email tokens (verify + reset)
  abstract InsertEmailToken  : EmailToken -> unit
  abstract TryGetEmailToken  : digest:byte[] -> EmailToken option
  /// Atomically mark the token as consumed at `at`. Returns `true` iff
  /// the token existed, was unexpired, and was not already consumed.
  abstract ConsumeEmailToken : digest:byte[] * at:DateTimeOffset -> bool

  // Sessions (refresh tokens)
  abstract InsertSession  : CustomerSession -> unit
  abstract TryGetSession  : id:string -> CustomerSession option
  abstract RevokeSession  : id:string * at:DateTimeOffset -> unit
  abstract ListSessions   : CustomerId -> CustomerSession list

// -- in-memory implementation ----------------------------------------------
//
// Used by:
//   * the dev binary running without `--postgres=`,
//   * smoke tests,
//   * the `--site-only` mode when no provisioner DB is configured (the
//     binary refuses to mint customers in that mode, but the empty store
//     keeps the WebParts type-checking).
//
// Concurrent-dictionary-backed; consistent with `InMemoryWorkspaceRegistry`
// and `InMemoryTenantStore`.

type InMemoryCustomerStore () =
  let byId       = ConcurrentDictionary<string, Customer>()
  let byEmail    = ConcurrentDictionary<string, string>()   // canonical → id
  let byGithub   = ConcurrentDictionary<int64,  string>()
  let tokensByDigest = ConcurrentDictionary<string, EmailToken>()
  let sessionsById   = ConcurrentDictionary<string, CustomerSession>()
  let digestKey (b : byte[]) = Convert.ToHexString b

  interface ICustomerStore with

    member _.Insert c =
      let (CustomerId cid) = c.id
      byId.[cid] <- c
      byEmail.[c.email] <- cid
      match c.githubUserId with
      | Some g -> byGithub.[g] <- cid
      | None -> ()

    member this.Update id f =
      let (CustomerId cid) = id
      match byId.TryGetValue cid with
      | true, cur ->
        let next  = f cur
        // Re-index if email/github changed.
        if not (String.Equals(cur.email, next.email, StringComparison.Ordinal)) then
          byEmail.TryRemove cur.email |> ignore
          byEmail.[next.email] <- cid
        match cur.githubUserId, next.githubUserId with
        | Some g, ng when ng <> Some g ->
          byGithub.TryRemove g |> ignore
        | _ -> ()
        match next.githubUserId with
        | Some g -> byGithub.[g] <- cid
        | None -> ()
        byId.[cid] <- next
        Some next
      | _ -> None

    member _.TryGetById (CustomerId cid) =
      match byId.TryGetValue cid with true, c -> Some c | _ -> None

    member _.TryGetByEmail raw =
      let canon = canonEmail raw
      match byEmail.TryGetValue canon with
      | true, cid ->
        match byId.TryGetValue cid with true, c -> Some c | _ -> None
      | _ -> None

    member _.TryGetByGithub g =
      match byGithub.TryGetValue g with
      | true, cid ->
        match byId.TryGetValue cid with true, c -> Some c | _ -> None
      | _ -> None

    member _.List () =
      byId.Values
      |> Seq.sortByDescending (fun c -> c.createdAt)
      |> List.ofSeq

    member _.InsertEmailToken t =
      tokensByDigest.[digestKey t.tokenHash] <- t

    member _.TryGetEmailToken digest =
      match tokensByDigest.TryGetValue (digestKey digest) with
      | true, t -> Some t
      | _ -> None

    member _.ConsumeEmailToken (digest, at) =
      let k = digestKey digest
      match tokensByDigest.TryGetValue k with
      | false, _ -> false
      | true, cur ->
        if cur.consumedAt.IsSome || cur.expiresAt <= at then false
        else
          let next = { cur with consumedAt = Some at }
          tokensByDigest.TryUpdate(k, next, cur)

    member _.InsertSession s =
      sessionsById.[s.id] <- s

    member _.TryGetSession id =
      match sessionsById.TryGetValue id with true, s -> Some s | _ -> None

    member _.RevokeSession (id, at) =
      match sessionsById.TryGetValue id with
      | true, cur when cur.revokedAt.IsNone ->
        sessionsById.TryUpdate(id, { cur with revokedAt = Some at }, cur)
        |> ignore
      | _ -> ()

    member _.ListSessions (CustomerId cid) =
      sessionsById.Values
      |> Seq.filter (fun s ->
           let (CustomerId scid) = s.customerId in scid = cid)
      |> Seq.sortByDescending (fun s -> s.createdAt)
      |> List.ofSeq
