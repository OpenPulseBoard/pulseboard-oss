module PulseBoard.Secrets

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Collections.Concurrent

// -- PLAN.md Phase 6 #4 -----------------------------------------------------
// Envelope encryption for sensitive log fields. A single root KEK
// (master key) wraps a per-tenant DEK; PII values inside log messages get
// encrypted with the DEK using AES-256-GCM and persisted in the wire form
//
//     enc:v1:<nonceB64Url>:<ctTagB64Url>
//
// Callers (clients) mark values inline using the convention
//
//     [[pii:<plaintext>]]
//
// On ingest the marker substring is replaced with its encrypted form before
// the row touches storage. Decryption is exposed through an Admin-scoped
// REST surface (`POST /api/secrets/decrypt`) — there is no automatic
// decryption on the query path, so leaked log dumps stay opaque.
//
// The KEK is loaded from the `PULSE_MASTER_KEY` env var (base64, 32 bytes)
// when present, otherwise auto-generated and persisted at
// `<dataDir>/keys/master.key` with mode 0600 on first start.

// ---------------------------------------------------------------------------
// KEK / DEK key management
// ---------------------------------------------------------------------------

let private b64url (b : byte[]) =
  Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=')

let private fromB64url (s : string) =
  let pad = (4 - (s.Length % 4)) % 4
  Convert.FromBase64String((s.Replace('-', '+').Replace('_', '/')) + String('=', pad))

let private rng = RandomNumberGenerator.Create()

let private genBytes (n : int) =
  let b = Array.zeroCreate n
  rng.GetBytes b
  b

/// Locate the master KEK. Order:
///   1. `PULSE_MASTER_KEY` env var (base64, 32 bytes) — typical prod path.
///   2. Persisted at `<root>/master.key`. If absent, a fresh 32-byte key is
///      generated and written with restrictive permissions.
let loadOrCreateKek (root : string) : byte[] =
  Directory.CreateDirectory root |> ignore
  match Environment.GetEnvironmentVariable "PULSE_MASTER_KEY" with
  | s when not (String.IsNullOrWhiteSpace s) ->
    let raw = fromB64url (s.Trim())
    if raw.Length <> 32 then
      invalidOp "PULSE_MASTER_KEY must decode to 32 bytes"
    raw
  | _ ->
    let p = Path.Combine(root, "master.key")
    if File.Exists p then
      let raw = File.ReadAllBytes p
      if raw.Length <> 32 then invalidOp (sprintf "%s: expected 32-byte key" p)
      raw
    else
      let k = genBytes 32
      File.WriteAllBytes(p, k)
      try
        let info = System.IO.FileInfo p
        info.UnixFileMode <-
          UnixFileMode.UserRead ||| UnixFileMode.UserWrite
      with _ -> ()
      k

// AES-GCM wrap format on disk for the DEK envelope:
//   { "v":1, "nonce":"<b64url>", "ct":"<b64url>" }   // ct includes tag

[<NoComparison; NoEquality>]
type private DekFile =
  { v : int; nonce : string; ct : string }

let private wrap (kek : byte[]) (plaintext : byte[]) : DekFile =
  let nonce = genBytes 12
  let ct    = Array.zeroCreate plaintext.Length
  let tag   = Array.zeroCreate 16
  use aes = new AesGcm(kek, 16)
  aes.Encrypt(ReadOnlySpan nonce,
              ReadOnlySpan plaintext,
              Span ct,
              Span tag)
  { v = 1
    nonce = b64url nonce
    ct    = b64url (Array.append ct tag) }

let private unwrap (kek : byte[]) (f : DekFile) : byte[] =
  if f.v <> 1 then invalidOp "unsupported DEK envelope version"
  let nonce = fromB64url f.nonce
  let blob  = fromB64url f.ct
  if blob.Length < 16 then invalidOp "DEK ciphertext truncated"
  let ct  = blob.[ 0 .. blob.Length - 17 ]
  let tag = blob.[ blob.Length - 16 .. blob.Length - 1 ]
  let plain = Array.zeroCreate ct.Length
  use aes = new AesGcm(kek, 16)
  aes.Decrypt(ReadOnlySpan nonce,
              ReadOnlySpan ct,
              ReadOnlySpan tag,
              Span plain)
  plain

let private writeJson (path : string) (obj : DekFile) =
  use ms = new MemoryStream()
  (
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteNumber("v", obj.v)
    w.WriteString("nonce", obj.nonce)
    w.WriteString("ct", obj.ct)
    w.WriteEndObject()
  )
  let bytes = ms.ToArray()
  let tmp = path + ".tmp"
  File.WriteAllBytes(tmp, bytes)
  File.Move(tmp, path, overwrite = true)

let private readJson (path : string) : DekFile =
  use doc = JsonDocument.Parse(File.ReadAllBytes path : byte[])
  let r = doc.RootElement
  let getStr (name : string) =
    match r.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> ""
  let v =
    match r.TryGetProperty "v" with
    | true, x when x.ValueKind = JsonValueKind.Number ->
      let mutable n = 0
      if x.TryGetInt32 &n then n else 0
    | _ -> 0
  { v = v; nonce = getStr "nonce"; ct = getStr "ct" }

// -- public helpers for DB-backed stores ------------------------------------

/// Wrap a DEK with the KEK and return the JSON envelope as a string
/// suitable for storing in a TEXT column.
let serialiseDekEnvelope (kek : byte[]) (dek : byte[]) : string =
  let env = wrap kek dek
  sprintf """{"v":%d,"nonce":"%s","ct":"%s"}""" env.v env.nonce env.ct

/// Parse and unwrap a DEK from a JSON envelope string (from a TEXT column).
let deserialiseDekEnvelope (kek : byte[]) (json : string) : byte[] =
  use doc = JsonDocument.Parse json
  let r = doc.RootElement
  let getStr (name : string) =
    match r.TryGetProperty(name) with
    | true, el when el.ValueKind = JsonValueKind.String -> el.GetString()
    | _ -> ""
  let ver =
    match r.TryGetProperty("v") with
    | true, x when x.ValueKind = JsonValueKind.Number ->
      let mutable n = 0
      if x.TryGetInt32 &n then n else 1
    | _ -> 1
  unwrap kek { v = ver; nonce = getStr "nonce"; ct = getStr "ct" }

/// Encrypt `plaintext` with `dek` (AES-256-GCM). Returns `enc:v1:...`.
let encryptWithDek (dek : byte[]) (plaintext : string) : string =
  let nonce = genBytes 12
  let pt    = Encoding.UTF8.GetBytes plaintext
  let ct    = Array.zeroCreate pt.Length
  let tag   = Array.zeroCreate 16
  use aes = new AesGcm(dek, 16)
  aes.Encrypt(ReadOnlySpan nonce, ReadOnlySpan pt, Span ct, Span tag)
  "enc:v1:" + b64url nonce + ":" + b64url (Array.append ct tag)

/// Decrypt a token produced by `encryptWithDek`. Returns `None` on failure.
let decryptWithDek (dek : byte[]) (token : string) : string option =
  try
    if isNull token || not (token.StartsWith("enc:v1:", StringComparison.Ordinal)) then None
    else
      let rest = token.Substring 7
      let i = rest.IndexOf ':'
      if i <= 0 then None
      else
        let nonce = fromB64url (rest.Substring(0, i))
        let blob  = fromB64url (rest.Substring(i + 1))
        if blob.Length < 16 then None
        else
          let ct  = blob.[0 .. blob.Length - 17]
          let tag = blob.[blob.Length - 16 .. blob.Length - 1]
          let pt  = Array.zeroCreate ct.Length
          use aes = new AesGcm(dek, 16)
          aes.Decrypt(ReadOnlySpan nonce, ReadOnlySpan ct, ReadOnlySpan tag, Span pt)
          Some (Encoding.UTF8.GetString pt)
  with _ -> None

/// Per-tenant DEK store. DEKs are 32-byte AES-256 keys, wrapped with the
/// KEK and persisted at `<root>/<tenantId>.dek.json`. The unwrapped value
/// is cached in memory for the process lifetime.
type ISecretsStore =
  abstract GetOrCreateDek : tenantId : string -> byte[]
  /// Encrypt a UTF-8 string with the tenant's DEK. Returns the
  /// `enc:v1:...` wire form.
  abstract Encrypt        : tenantId : string * plaintext : string -> string
  /// Decrypt an `enc:v1:...` token. Returns `None` for malformed input or
  /// tag mismatch.
  abstract Decrypt        : tenantId : string * token : string -> string option

type FileSecretsStore(root : string, kek : byte[]) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal)
  let pathFor (tid : string) = Path.Combine(root, tid + ".dek.json")
  let getDek (tid : string) =
    cache.GetOrAdd(tid, fun _ ->
      let p = pathFor tid
      if File.Exists p then unwrap kek (readJson p)
      else
        let dek = genBytes 32
        writeJson p (wrap kek dek)
        dek)

  static let prefix = "enc:v1:"

  member _.Root = root

  interface ISecretsStore with
    member _.GetOrCreateDek tid = getDek tid

    member _.Encrypt (tid, plaintext) = encryptWithDek (getDek tid) plaintext

    member _.Decrypt (tid, token) = decryptWithDek (getDek tid) token

// ---------------------------------------------------------------------------
// Per-tenant PII policy: a list of field names that should be encrypted on
// ingest. Stored as a tiny JSON array; loaded lazily and cached.
// ---------------------------------------------------------------------------

type IPiiPolicyStore =
  abstract Get : tenantId : string -> string[]
  abstract Put : tenantId : string * fields : string[] -> unit

type FilePiiPolicyStore(root : string) =
  do Directory.CreateDirectory root |> ignore
  let cache = ConcurrentDictionary<string, string[]>(StringComparer.Ordinal)
  let pathFor (tid : string) = Path.Combine(root, tid + ".pii.json")
  let load (tid : string) =
    let p = pathFor tid
    if File.Exists p then
      try
        use doc = JsonDocument.Parse(File.ReadAllBytes p : byte[])
        match doc.RootElement.TryGetProperty "fields" with
        | true, v when v.ValueKind = JsonValueKind.Array ->
          [| for f in v.EnumerateArray() do
               if f.ValueKind = JsonValueKind.String then yield f.GetString() |]
        | _ ->
          if doc.RootElement.ValueKind = JsonValueKind.Array then
            [| for f in doc.RootElement.EnumerateArray() do
                 if f.ValueKind = JsonValueKind.String then yield f.GetString() |]
          else [||]
      with _ -> [||]
    else [||]

  interface IPiiPolicyStore with
    member _.Get tid =
      cache.GetOrAdd(tid, fun _ -> load tid)
    member _.Put (tid, fields) =
      let cleaned =
        fields
        |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Array.map (fun s -> s.Trim())
        |> Array.distinct
      use ms = new MemoryStream()
      (
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        w.WritePropertyName "fields"
        w.WriteStartArray()
        for f in cleaned do w.WriteStringValue (f : string)
        w.WriteEndArray()
        w.WriteEndObject()
      )
      let p = pathFor tid
      let tmp = p + ".tmp"
      File.WriteAllBytes(tmp, ms.ToArray())
      File.Move(tmp, p, overwrite = true)
      cache.[tid] <- cleaned

// ---------------------------------------------------------------------------
// Inline PII marker substitution. Clients embed `[[pii:<value>]]` substrings
// inside the log `message`; on ingest we replace each marker with its
// encrypted `enc:v1:...` token using the tenant DEK. The transform is a
// straight substring scan — no regex backtracking — so it stays cheap on
// the hot path.
// ---------------------------------------------------------------------------

let encryptInlineMarkers (secrets : ISecretsStore) (tid : string) (s : string) =
  if isNull s then s
  else
    let openTag  = "[[pii:"
    let closeTag = "]]"
    let mutable i = 0
    let mutable out : StringBuilder = null
    let mutable touched = false
    while i < s.Length do
      let lo = s.IndexOf(openTag, i, StringComparison.Ordinal)
      if lo < 0 then
        if touched then out.Append(s, i, s.Length - i) |> ignore
        i <- s.Length
      else
        let hi = s.IndexOf(closeTag, lo + openTag.Length, StringComparison.Ordinal)
        if hi < 0 then
          if touched then out.Append(s, i, s.Length - i) |> ignore
          i <- s.Length
        else
          if not touched then
            out <- StringBuilder(s.Length + 32)
            touched <- true
          out.Append(s, i, lo - i) |> ignore
          let inner = s.Substring(lo + openTag.Length, hi - (lo + openTag.Length))
          out.Append(secrets.Encrypt(tid, inner)) |> ignore
          i <- hi + closeTag.Length
    if touched then out.ToString() else s
