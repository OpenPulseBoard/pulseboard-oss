module PulseBoard.Tests.Unit.SecretsTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.Secrets

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Create a fresh 32-byte KEK for each test.
let private freshKek () =
    let b = Array.zeroCreate 32
    Security.Cryptography.RandomNumberGenerator.Fill(Span b)
    b

/// Spin up a FileSecretsStore in a temp directory; delete it on dispose.
let private withStore (f : FileSecretsStore -> 'a) : 'a =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    try
        f (FileSecretsStore(dir, freshKek()))
    finally
        if Directory.Exists dir then Directory.Delete(dir, recursive = true)

let private containsStr (sub : string) (s : string) =
    s.Contains(sub) |> should be True

let private notContainsStr (sub : string) (s : string) =
    s.Contains(sub) |> should be False

let private startsWith (prefix : string) (s : string) =
    s.StartsWith(prefix) |> should be True

let private endsWith (suffix : string) (s : string) =
    s.EndsWith(suffix) |> should be True
// ---------------------------------------------------------------------------

[<Fact>]
let ``Encrypt then Decrypt roundtrips the plaintext`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        let ct = store.Encrypt("t1", "hello world")
        store.Decrypt("t1", ct) |> should equal (Some "hello world"))

[<Fact>]
let ``Encrypt then Decrypt roundtrips an empty string`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        let ct = store.Encrypt("t1", "")
        store.Decrypt("t1", ct) |> should equal (Some ""))

[<Fact>]
let ``Encrypt then Decrypt roundtrips a unicode string`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        let plain = "user@example.com — こんにちは 🔐"
        let ct = store.Encrypt("t1", plain)
        store.Decrypt("t1", ct) |> should equal (Some plain))

[<Fact>]
let ``Two Encrypt calls for the same plaintext produce different ciphertexts`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        let ct1 = store.Encrypt("t1", "secret")
        let ct2 = store.Encrypt("t1", "secret")
        ct1 |> should not' (equal ct2))

[<Fact>]
let ``Decrypt of a malformed token returns None`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        store.Decrypt("t1", "not-a-valid-token") |> should equal None)

[<Fact>]
let ``Decrypt of null returns None`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        store.Decrypt("t1", null) |> should equal None)

[<Fact>]
let ``Decrypt with the wrong tenant key returns None`` () =
    // Each tenant gets its own DEK; ciphertext encrypted under tenant-1 is
    // garbage when decrypted under tenant-2.
    withStore (fun store ->
        let store = store :> ISecretsStore
        let ct = store.Encrypt("tenant-1", "sensitive")
        store.Decrypt("tenant-2", ct) |> should equal None)

[<Fact>]
let ``Encrypt token starts with enc:v1: prefix`` () =
    withStore (fun store ->
        let store = store :> ISecretsStore
        let ct = store.Encrypt("t1", "test")
        ct.StartsWith("enc:v1:") |> should be True)

// ---------------------------------------------------------------------------
// DEK persistence: survives store reconstruction
// ---------------------------------------------------------------------------

[<Fact>]
let ``Ciphertext decrypts correctly after store is reconstructed with same KEK`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let kek = freshKek()
    try
        let ct =
            let s = FileSecretsStore(dir, kek) :> ISecretsStore
            s.Encrypt("t1", "persistent secret")
        // Reconstruct with same KEK and directory — DEK loaded from disk
        let s2 = FileSecretsStore(dir, kek) :> ISecretsStore
        s2.Decrypt("t1", ct) |> should equal (Some "persistent secret")
    finally
        if Directory.Exists dir then Directory.Delete(dir, recursive = true)

// ---------------------------------------------------------------------------
// Inline PII marker substitution
// ---------------------------------------------------------------------------

[<Fact>]
let ``encryptInlineMarkers replaces a single pii marker with enc:v1: token`` () =
    withStore (fun store ->
        let result = encryptInlineMarkers (store :> ISecretsStore) "t1" "user [[pii:alice@example.com]] signed in"
        result |> startsWith "user enc:v1:"
        result |> endsWith " signed in"
        result |> notContainsStr "alice@example.com")

[<Fact>]
let ``encryptInlineMarkers replaces multiple pii markers in one pass`` () =
    withStore (fun store ->
        let input = "email=[[pii:a@b.com]], ssn=[[pii:123-45-6789]]"
        let result = encryptInlineMarkers (store :> ISecretsStore) "t1" input
        result |> notContainsStr "a@b.com"
        result |> notContainsStr "123-45-6789")

[<Fact>]
let ``encryptInlineMarkers leaves a string without markers unchanged`` () =
    withStore (fun store ->
        let plain = "no pii here"
        encryptInlineMarkers (store :> ISecretsStore) "t1" plain
        |> should equal plain)

[<Fact>]
let ``encryptInlineMarkers handles null input without throwing`` () =
    withStore (fun store ->
        encryptInlineMarkers (store :> ISecretsStore) "t1" null |> should equal null)

[<Fact>]
let ``encryptInlineMarkers is idempotent on already-encrypted tokens`` () =
    // An enc:v1: token does not contain [[ so a second pass is a no-op.
    withStore (fun store ->
        let first  = encryptInlineMarkers (store :> ISecretsStore) "t1" "[[pii:secret]]"
        let second = encryptInlineMarkers (store :> ISecretsStore) "t1" first
        first |> should equal second)
