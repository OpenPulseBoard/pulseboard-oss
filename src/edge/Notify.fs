module PulseBoard.Notify

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open PulseBoard.Alerts

/// A `Sink` is just an `Alert -> unit`. Delivery should be non-blocking so
/// the alert-engine tick never stalls waiting on the network; sinks here
/// hand the actual HTTP call off to the thread pool and swallow failures
/// after a console log.
type Sink = Alert -> unit

// One shared client: connection pooling + sensible default timeout.
let private http =
  let h = new HttpClient()
  h.Timeout <- TimeSpan.FromSeconds 5.
  h

let private jsonOpts =
  let o = JsonSerializerOptions()
  o.WriteIndented <- false
  o

let private serializeAlert (a : Alert) =
  // Hand-roll the envelope so the JSON shape matches what `Program.fs`
  // pushes through the WebSocket hub. Keep this in lock-step.
  let sb = StringBuilder()
  sb.Append "{\"type\":\"alert\",\"rule\":" |> ignore
  sb.Append (JsonSerializer.Serialize(a.rule, jsonOpts)) |> ignore
  sb.Append ",\"metric\":" |> ignore
  sb.Append (JsonSerializer.Serialize(a.metric, jsonOpts)) |> ignore
  sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                  ",\"value\":{0},\"firedAt\":{1}}}", a.value, a.firedAt) |> ignore
  sb.ToString()

let private postJson (url : string) (body : string) : Task =
  task {
    try
      use content = new StringContent(body, Encoding.UTF8, "application/json")
      let! resp = http.PostAsync(url, content)
      if not resp.IsSuccessStatusCode then
        let! text = resp.Content.ReadAsStringAsync()
        eprintfn "[notify] %s -> %d %s" url (int resp.StatusCode)
                 (text.Substring(0, min 200 text.Length))
    with ex ->
      eprintfn "[notify] %s -> %s" url ex.Message
  } :> Task

/// Generic webhook: POSTs the same JSON envelope the WebSocket hub uses.
/// Suitable for anything that accepts JSON (Discord-compatible endpoints,
/// custom receivers, etc.).
let webhook (url : string) : Sink =
  fun alert ->
    let body = serializeAlert alert
    // Fire-and-forget; we never await so the engine tick stays snappy.
    postJson url body |> ignore

/// Slack incoming-webhook sink. Slack expects `{ "text": "..." }`; we render
/// a one-line human summary plus the structured fields.
let slack (url : string) : Sink =
  fun alert ->
    let firedAtIso =
      DateTimeOffset.FromUnixTimeMilliseconds(alert.firedAt)
                    .ToString("o")
    let text =
      sprintf ":rotating_light: *%s* — `%s` = %g at %s"
        alert.rule alert.metric alert.value firedAtIso
    let payload =
      sprintf """{"text":%s}""" (JsonSerializer.Serialize(text, jsonOpts))
    postJson url payload |> ignore

/// Combine many sinks into one. Each sink is invoked independently; a
/// throw from one sink does not stop the others.
let fanout (sinks : Sink list) : Sink =
  fun alert ->
    for s in sinks do
      try s alert
      with ex -> eprintfn "[notify] sink threw: %s" ex.Message

/// Parse a comma/newline-separated list of URLs, stripping blanks and
/// `#`-prefixed comments. Used for both `--webhook=` and `--slack=` env
/// fallbacks so operators can pass several endpoints in one variable.
let parseUrls (raw : string) : string list =
  if String.IsNullOrWhiteSpace raw then []
  else
    raw.Split([| ','; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s.Length > 0 && not (s.StartsWith "#"))
    |> Array.toList
