module PulseBoard.Tests.Properties.TenancyPropertyTests

open FsCheck
open FsCheck.Xunit
open PulseBoard.Tenancy

// ---------------------------------------------------------------------------
// hasScope — bitmask laws
// ---------------------------------------------------------------------------

/// Scope.None is satisfied by any held scope (vacuous truth: 0 bits needed).
[<Property>]
let ``hasScope Scope.None is always true``
        (have : int) =
    let scope = enum<Scope> (abs have % 8)
    hasScope scope Scope.None = true

/// Every scope satisfies itself.
[<Property>]
let ``hasScope is reflexive``
        (have : int) =
    let scope = enum<Scope> (abs have % 8)
    hasScope scope scope = true

/// Scope.Admin (4 = 100b) implies Scope.Query (2 = 010b) is NOT implied
/// unless the Query bit is also present — confirms bitwise semantics.
[<Property>]
let ``hasScope Admin does not imply Query unless Query bit is set`` () =
    hasScope Scope.Admin Scope.Query = false

/// If `have` contains all bits of `need`, hasScope returns true.
[<Property>]
let ``hasScope returns true when have contains all need bits``
        (extra : int) =
    let need  = Scope.Ingest ||| Scope.Query
    let have  = need ||| enum<Scope> (abs extra % 8)
    hasScope have need = true

/// hasScope is monotone: adding more bits to `have` cannot flip a true result
/// to false.
[<Property>]
let ``hasScope is monotone in have``
        (base_ : int) (extra : int) =
    let have     = enum<Scope> (abs base_ % 8)
    let need     = enum<Scope> (abs extra % 8)
    let haveMore = have ||| need
    if hasScope have need then hasScope haveMore need
    else true   // only care about the positive direction

// ---------------------------------------------------------------------------
// planToText / tryParsePlan — roundtrip
// ---------------------------------------------------------------------------

type AnyPlan2 = AnyPlan2 of Plan

type TenancyGenerators =
    static member AnyPlan2() : Arbitrary<AnyPlan2> =
        Gen.elements [| Free; Pro; Enterprise |]
        |> Gen.map AnyPlan2
        |> Arb.fromGen

/// planToText then tryParsePlan is identity for all plans.
[<Property(Arbitrary = [| typeof<TenancyGenerators> |])>]
let ``planToText then tryParsePlan roundtrips all plans``
        (AnyPlan2 plan) =
    tryParsePlan (planToText plan) = Some plan

/// tryParsePlan is case-insensitive (uppercase input still parses).
[<Property(Arbitrary = [| typeof<TenancyGenerators> |])>]
let ``tryParsePlan is case-insensitive``
        (AnyPlan2 plan) =
    tryParsePlan (planToText plan |> fun s -> s.ToUpperInvariant()) = Some plan

// ---------------------------------------------------------------------------
// tryParsePresented — format invariants
// ---------------------------------------------------------------------------

/// Any string not starting with "pk_" is rejected.
[<Property>]
let ``tryParsePresented rejects strings not starting with pk_``
        (NonEmptyString s) =
    if s.StartsWith("pk_") then true   // skip — could be valid
    else tryParsePresented s = None

/// A well-formed "pk_<id>.<secret>" string with non-empty id and secret
/// always parses successfully.
[<Property>]
let ``tryParsePresented accepts well-formed pk_ keys``
        (NonEmptyString rawId) (NonEmptyString rawSecret) =
    // Strip dots and spaces from both parts so there is exactly one dot
    // separator and neither part is empty or contains whitespace (which
    // tryParsePresented trims before inspecting length).
    let sanitise (s : string) =
        let t = s.Replace(".", "x").Replace(" ", "x").Trim()
        if t.Length = 0 then "x" else t
    let id     = sanitise rawId
    let secret = sanitise rawSecret
    let presented = sprintf "pk_%s.%s" id secret
    match tryParsePresented presented with
    | Some (ApiKeyId kid, ksec) -> kid = id && ksec = secret
    | None -> false

// ---------------------------------------------------------------------------
// scopesForRole — invariants
// ---------------------------------------------------------------------------

/// Admin has every defined scope bit.
[<Property>]
let ``scopesForRole Admin has all scope bits`` () =
    let s = scopesForRole Admin
    hasScope s Scope.Ingest &&
    hasScope s Scope.Query  &&
    hasScope s Scope.Admin

/// Viewer has only Query.
[<Property>]
let ``scopesForRole Viewer has Query but not Ingest or Admin`` () =
    let s = scopesForRole Viewer
    hasScope s Scope.Query                &&
    not (hasScope s Scope.Ingest)         &&
    not (hasScope s Scope.Admin)

/// Editor has Ingest and Query but not Admin.
[<Property>]
let ``scopesForRole Editor has Ingest and Query but not Admin`` () =
    let s = scopesForRole Editor
    hasScope s Scope.Ingest               &&
    hasScope s Scope.Query                &&
    not (hasScope s Scope.Admin)

/// Billing has no API scopes.
[<Property>]
let ``scopesForRole Billing has no API scopes`` () =
    scopesForRole Billing = Scope.None

/// scopesForRole Admin ⊇ Editor ⊇ Viewer (monotone by role privilege).
[<Property>]
let ``scope bits are monotone: Admin >= Editor >= Viewer`` () =
    let admin  = int (scopesForRole Admin)
    let editor = int (scopesForRole Editor)
    let viewer = int (scopesForRole Viewer)
    (admin &&& editor) = editor &&
    (editor &&& viewer) = viewer
