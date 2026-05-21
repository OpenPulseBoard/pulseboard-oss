module PulseBoard.Alerts

open System
open System.Collections.Concurrent
open PulseBoard.TimeSeries

/// Comparison operator for an alert threshold.
type Cmp = Gt | Lt | Gte | Lte

let private eval cmp (a : float) (b : float) =
  match cmp with
  | Gt -> a > b | Lt -> a < b | Gte -> a >= b | Lte -> a <= b

/// Rule: "when <metric> <cmp> <threshold> for <durationMs> -> fire"
type Rule =
  { name        : string
    metric      : string
    cmp         : Cmp
    threshold   : float
    durationMs  : int64 }

type Alert =
  { rule    : string
    metric  : string
    firedAt : int64
    value   : float }

type Engine(store : MetricStore, onFire : Alert -> unit) =
  let rules = ConcurrentBag<Rule>()
  let firedSince = ConcurrentDictionary<string, int64>() // rule -> first-breach ts
  let active = ConcurrentDictionary<string, bool>()
  // Optional gate consulted before each `Tick`. Returns `false` to skip
  // this evaluation cycle (e.g. when a per-tenant alert-eval RPS bucket
  // is empty). `None` means always-allow, matching legacy behaviour.
  let mutable gate : (unit -> bool) option = None

  member _.Add(r : Rule) = rules.Add r

  member _.Rules() = rules |> Seq.toArray

  /// Install a pre-tick gate. Callers that don't enforce alert-eval
  /// throttling can leave this unset.
  member _.SetEvalGate(f : unit -> bool) = gate <- Some f

  /// Evaluate every rule against the latest snapshot. Call periodically.
  member _.Tick() =
    let allow =
      match gate with
      | Some g -> try g () with _ -> true
      | None   -> true
    if not allow then () else
    let now = nowMs ()
    for r in rules do
      let points = store.GetSince(r.metric, now - r.durationMs)
      let breaching =
        points.Length > 0
        && points |> Array.forall (fun p -> eval r.cmp p.value r.threshold)
      if breaching then
        let started =
          firedSince.GetOrAdd(r.name, fun _ ->
            if points.Length > 0 then points.[0].ts else now)
        if now - started >= r.durationMs && not (active.ContainsKey r.name) then
          active.[r.name] <- true
          let last = points.[points.Length - 1]
          onFire { rule = r.name; metric = r.metric; firedAt = now; value = last.value }
      else
        firedSince.TryRemove(r.name) |> ignore
        active.TryRemove(r.name) |> ignore
