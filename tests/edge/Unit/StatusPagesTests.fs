module PulseBoard.Tests.Unit.StatusPagesTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open PulseBoard.StatusPages
open PulseBoard.Tenancy
open PulseBoard.TimeSeries

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private metricComp name selector cmp thr : StatusComponent =
    { id = Guid.NewGuid().ToString "N"
      name = name; description = ""
      source = Metric(selector, cmp, thr) }

let private mkPage slug title comps : StatusPage =
    { id = Guid.NewGuid().ToString "N"
      slug = slug; title = title; description = "desc"
      components = comps; maintenances = []
      createdAt = 0L; updatedAt = 0L }

let private withStore (f : IStatusStore * TenantId -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try f (FileStatusStore dir :> IStatusStore, TenantId "t1")
    finally try Directory.Delete(dir, true) with _ -> ()

// -- validation -------------------------------------------------------------

[<Fact>]
let ``validate rejects empty slug and title`` () =
    match validate { mkPage "" "T" [] with slug = "" } with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mkPage "s" "" []) with
    | Error _ -> () | Ok _ -> failwith "expected error"

[<Fact>]
let ``validate rejects unsafe slug`` () =
    match validate (mkPage "bad slug!" "T" []) with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mkPage "good-slug_1" "T" []) with
    | Ok _ -> () | Error e -> failwith e

[<Fact>]
let ``validate rejects bad comparison and empty selector`` () =
    match validate (mkPage "s" "T" [ metricComp "c" "m" "~=" 1.0 ]) with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mkPage "s" "T" [ metricComp "c" "" ">=" 1.0 ]) with
    | Error _ -> () | Ok _ -> failwith "expected error"
    match validate (mkPage "s" "T" [ metricComp "c" "m" ">=" 1.0 ]) with
    | Ok _ -> () | Error e -> failwith e

[<Fact>]
let ``validate rejects component with no name`` () =
    match validate (mkPage "s" "T" [ metricComp "" "m" ">=" 1.0 ]) with
    | Error _ -> () | Ok _ -> failwith "expected error"

// -- request parsing --------------------------------------------------------

[<Fact>]
let ``parseRequest assigns id and timestamps on create`` () =
    match parseRequest None """{"slug":"main","title":"My status"}""" with
    | Ok p ->
        p.id.Length |> should be (greaterThan 0)
        p.createdAt |> should be (greaterThan 0L)
        p.slug |> should equal "main"
    | Error e -> failwith e

[<Fact>]
let ``parseRequest preserves id slug and createdAt on update`` () =
    let existing = { mkPage "orig" "T" [] with id = "keep"; createdAt = 42L }
    match parseRequest (Some existing) """{"title":"Renamed"}""" with
    | Ok p ->
        p.id |> should equal "keep"
        p.slug |> should equal "orig"
        p.createdAt |> should equal 42L
        p.title |> should equal "Renamed"
    | Error e -> failwith e

[<Fact>]
let ``parseRequest parses synthetic and metric components`` () =
    let body =
        """{"slug":"s","title":"T","components":[
              {"name":"API","sourceKind":"synthetic","checkId":"abc"},
              {"name":"Ingest","sourceKind":"metric","selector":"pulse_x","cmp":">","threshold":0.5}
           ]}"""
    match parseRequest None body with
    | Ok p ->
        p.components.Length |> should equal 2
        match p.components.[0].source with
        | Synthetic id -> id |> should equal "abc"
        | _ -> failwith "expected synthetic"
        match p.components.[1].source with
        | Metric(sel, cmp, thr) ->
            sel |> should equal "pulse_x"
            cmp |> should equal ">"
            thr |> should equal 0.5
        | _ -> failwith "expected metric"
    | Error e -> failwith e

// -- JSON roundtrip ---------------------------------------------------------

[<Fact>]
let ``serialise then parse roundtrips a page`` () =
    let p =
        { mkPage "main" "Title" [ metricComp "API" "pulse_up" ">=" 0.5 ] with
            maintenances = [ { id = "m1"; title = "Upgrade"; body = "b"
                               startsAt = 100L; endsAt = 200L } ] }
    match parsePage (serialisePage p) with
    | Some r ->
        r.slug |> should equal "main"
        r.components.Length |> should equal 1
        r.maintenances.Length |> should equal 1
        r.maintenances.[0].title |> should equal "Upgrade"
    | None -> failwith "parse failed"

// -- store roundtrip --------------------------------------------------------

[<Fact>]
let ``FileStatusStore upserts lists and deletes`` () =
    withStore (fun (store, tid) ->
        let p = mkPage "main" "Title" [ metricComp "API" "pulse_up" ">=" 0.5 ]
        store.Upsert(tid, p)
        store.List tid |> Array.length |> should equal 1
        (store.TryGet(tid, p.id)).IsSome |> should equal true
        store.Delete(tid, p.id) |> should equal true
        store.List tid |> Array.length |> should equal 0)

[<Fact>]
let ``FileStatusStore reloads from disk`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString "N")
    try
        let tid = TenantId "t1"
        let p = mkPage "main" "Title" [ metricComp "API" "pulse_up" ">=" 0.5 ]
        (FileStatusStore dir :> IStatusStore).Upsert(tid, p)
        let fresh = FileStatusStore dir :> IStatusStore
        fresh.List tid |> Array.length |> should equal 1
        (fresh.TryGet(tid, p.id)).Value.slug |> should equal "main"
    finally try Directory.Delete(dir, true) with _ -> ()

// -- live status ------------------------------------------------------------

let private parseLive (json : string) = System.Text.Json.JsonDocument.Parse json

[<Fact>]
let ``renderLive reports operational and uptime from a metric series`` () =
    let metrics = MetricStore 1000
    let now = nowMs ()
    let series = "pulse_status_demo"
    // 4 up, 1 down => 80% uptime; last point is up
    metrics.Record(series, { ts = now - 4000L; value = 1.0 })
    metrics.Record(series, { ts = now - 3000L; value = 0.0 })
    metrics.Record(series, { ts = now - 2000L; value = 1.0 })
    metrics.Record(series, { ts = now - 1000L; value = 1.0 })
    metrics.Record(series, { ts = now;         value = 1.0 })
    let page = mkPage "s" "T" [ metricComp "Demo" series ">=" 0.5 ]
    let json = renderLive metrics (fun _ -> None) [||] (60L * 60L * 1000L) page
    use doc = parseLive json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "operational"
    let comp = root.GetProperty("components").[0]
    comp.GetProperty("status").GetString() |> should equal "operational"
    comp.GetProperty("uptime").GetDouble() |> should (equalWithin 0.001) 0.8

[<Fact>]
let ``renderLive reports down when last sample fails comparison`` () =
    let metrics = MetricStore 1000
    let now = nowMs ()
    let series = "pulse_status_down"
    metrics.Record(series, { ts = now - 1000L; value = 1.0 })
    metrics.Record(series, { ts = now;         value = 0.0 })
    let page = mkPage "s" "T" [ metricComp "Demo" series ">=" 0.5 ]
    let json = renderLive metrics (fun _ -> None) [||] (60L * 60L * 1000L) page
    use doc = parseLive json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "major_outage"
    root.GetProperty("components").[0].GetProperty("status").GetString()
    |> should equal "down"

[<Fact>]
let ``renderLive resolves synthetic components by check name`` () =
    let metrics = MetricStore 1000
    let now = nowMs ()
    let series = "pulse_synthetic_up{check=\"My API\",kind=\"http\",region=\"edge\"}"
    metrics.Record(series, { ts = now; value = 1.0 })
    let comp : StatusComponent =
        { id = "c1"; name = "API"; description = ""; source = Synthetic "check-1" }
    let page = mkPage "s" "T" [ comp ]
    let json = renderLive metrics (fun id -> if id = "check-1" then Some "My API" else None)
                          [||] (60L * 60L * 1000L) page
    use doc = parseLive json
    doc.RootElement.GetProperty("components").[0].GetProperty("status").GetString()
    |> should equal "operational"

[<Fact>]
let ``renderLive reports unknown when no data backs a component`` () =
    let metrics = MetricStore 1000
    let page = mkPage "s" "T" [ metricComp "Nope" "pulse_missing" ">=" 0.5 ]
    let json = renderLive metrics (fun _ -> None) [||] (60L * 60L * 1000L) page
    use doc = parseLive json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "unknown"
    root.GetProperty("components").[0].GetProperty("status").GetString()
    |> should equal "unknown"

[<Fact>]
let ``renderLive includes incidents and degrades status`` () =
    let metrics = MetricStore 1000
    let now = nowMs ()
    let series = "pulse_ok"
    metrics.Record(series, { ts = now; value = 1.0 })
    let inc : Incident =
        { title = "High latency"; severity = "critical"; summary = "p99 up"
          since = now - 5000L; labels = Map.empty }
    let page = mkPage "s" "T" [ metricComp "Demo" series ">=" 0.5 ]
    let json = renderLive metrics (fun _ -> None) [| inc |] (60L * 60L * 1000L) page
    use doc = parseLive json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "degraded"
    let incidents = root.GetProperty("incidents")
    incidents.GetArrayLength() |> should equal 1
    incidents.[0].GetProperty("title").GetString() |> should equal "High latency"

[<Fact>]
let ``renderLive omits expired maintenance windows`` () =
    let metrics = MetricStore 1000
    let now = nowMs ()
    let past : Maintenance =
        { id = "m1"; title = "Old"; body = ""; startsAt = now - 10000L; endsAt = now - 5000L }
    let upcoming : Maintenance =
        { id = "m2"; title = "Soon"; body = ""; startsAt = now + 5000L; endsAt = now + 10000L }
    let page = { mkPage "s" "T" [] with maintenances = [ past; upcoming ] }
    let json = renderLive metrics (fun _ -> None) [||] (60L * 60L * 1000L) page
    use doc = parseLive json
    let mnts = doc.RootElement.GetProperty("maintenances")
    mnts.GetArrayLength() |> should equal 1
    mnts.[0].GetProperty("title").GetString() |> should equal "Soon"
