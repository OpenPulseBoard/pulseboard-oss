module PulseBoard.Tests.Properties.RulesPropertyTests

open System
open FsCheck
open FsCheck.Xunit
open PulseBoard.Rules
open PulseBoard.Tenancy

// ---------------------------------------------------------------------------
// Custom generators
// ---------------------------------------------------------------------------

type AnyCmp  = AnyCmp  of Cmp
type AnyLang = AnyLang of RuleLang

type RulesGenerators =
    static member AnyCmp() : Arbitrary<AnyCmp> =
        Gen.elements [| Gt; Lt; Gte; Lte; Eq; Neq |]
        |> Gen.map AnyCmp
        |> Arb.fromGen

    static member AnyLang() : Arbitrary<AnyLang> =
        Gen.elements [| PromQL; LogQL |]
        |> Gen.map AnyLang
        |> Arb.fromGen

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Sanitise an arbitrary string to printable ASCII, at least 1 char.
let private safe (s : string) : string =
    let src = if isNull s then "" else s
    let clean =
        src
        |> Seq.filter (fun c -> c >= ' ' && c <= '~')
        |> Seq.truncate 60
        |> String.Concat
    if clean.Length = 0 then "x" else clean

let private makeRule (name : string) (lang : RuleLang) (expr : string)
                     (cmp : Cmp) (threshold : float) : Rule =
    { id          = Guid.NewGuid().ToString "N"
      name        = name
      lang        = lang
      expr        = expr
      cmp         = cmp
      threshold   = threshold
      forMs       = 0L
      severity    = Severity.Warning
      labels      = Map.empty
      annotations = Map.empty
      runbook     = None }

let private makeGroup (name : string) (rules : Rule[]) : RuleGroup =
    let now = DateTimeOffset.UtcNow
    { id         = Guid.NewGuid().ToString "N"
      name       = name
      intervalMs = 15_000L
      rules      = rules
      createdAt  = now
      updatedAt  = now }

// ---------------------------------------------------------------------------
// fingerprint — algebraic invariants
// ---------------------------------------------------------------------------

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``fingerprint always returns a 16-char lowercase hex string``
        (NonEmptyString ruleId)
        (pairs : (NonEmptyString * NonEmptyString) list) =
    let labels =
        pairs
        |> List.truncate 10
        |> List.map (fun (NonEmptyString k, NonEmptyString v) -> safe k, safe v)
        |> Map.ofList
    let fp = fingerprint (safe ruleId) labels
    fp.Length = 16 &&
    fp |> Seq.forall (fun c -> "0123456789abcdef".Contains(string c))

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``fingerprint is deterministic for the same ruleId and labels``
        (NonEmptyString ruleId) (NonEmptyString k) (NonEmptyString v) =
    let rid    = safe ruleId
    let labels = Map.ofList [ safe k, safe v ]
    fingerprint rid labels = fingerprint rid labels

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``fingerprint does not throw for arbitrary safe ruleId and labels``
        (NonEmptyString a) (NonEmptyString b) =
    let fa = fingerprint (safe a) Map.empty
    let fb = fingerprint (safe b) Map.empty
    fa.Length = 16 && fb.Length = 16

// ---------------------------------------------------------------------------
// serialiseGroup / parseGroup — roundtrip invariants
// ---------------------------------------------------------------------------

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``serialiseGroup then parseGroup preserves rule count``
        (NonEmptyString gname)
        (rules : (NonEmptyString * AnyLang * NonEmptyString * AnyCmp * NormalFloat) list) =
    let rs =
        rules
        |> List.truncate 8
        |> List.map (fun (NonEmptyString n, AnyLang l, NonEmptyString e, AnyCmp c, NormalFloat t) ->
            makeRule (safe n) l (safe e) c t)
        |> Array.ofList
    let g = makeGroup (safe gname) rs
    match parseGroup (serialiseGroup g) with
    | Result.Ok g2 -> g2.rules.Length = rs.Length
    | Result.Error _ -> false

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``serialiseGroup then parseGroup preserves rule names``
        (NonEmptyString gname) (NonEmptyString rname)
        (AnyLang lang) (NonEmptyString expr) (AnyCmp cmp) (NormalFloat threshold) =
    let r = makeRule (safe rname) lang (safe expr) cmp threshold
    let g = makeGroup (safe gname) [| r |]
    match parseGroup (serialiseGroup g) with
    | Result.Ok g2 -> g2.rules.Length = 1 && g2.rules.[0].name = r.name
    | Result.Error _ -> false

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``serialiseGroup output is a non-empty JSON object``
        (NonEmptyString gname) =
    let g   = makeGroup (safe gname) [||]
    let out = serialiseGroup g
    out.TrimStart().[0] = '{' &&
    out.TrimEnd().[out.TrimEnd().Length - 1] = '}'

[<Property(Arbitrary = [| typeof<RulesGenerators> |])>]
let ``parseGroup preserves intervalMs when it is already at least 1000``
        (NonEmptyString gname) (PositiveInt raw) =
    let ms = int64 (max 1 raw) * 1_000L
    let g  = { makeGroup (safe gname) [||] with intervalMs = ms }
    match parseGroup (serialiseGroup g) with
    | Result.Ok g2 -> g2.intervalMs = ms
    | Result.Error _ -> false
