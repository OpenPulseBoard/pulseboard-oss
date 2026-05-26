module PulseBoard.EmailSender

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text

// Phase 10 — outbound email transport for customer-account flows
// (verification links, password resets, billing receipts later on).
//
// Two implementations ship today:
//   * `ConsoleEmailSender`  — logs the rendered message to stderr.
//                             The default for dev/test; no external deps.
//   * `MailgunEmailSender`  — POSTs to the Mailgun HTTP API
//                             (`https://api.mailgun.net/v3/<domain>/messages`).
//
// The interface is deliberately tiny — `Send` returns Async<Result> so
// callers can decide whether to fail the request or fire-and-forget.
// The auth WebParts always fire-and-forget (an SMTP hiccup must not
// fail signup), but they log the error so it is visible in operator
// dashboards.

[<NoComparison; NoEquality>]
type EmailMessage =
  { fromAddress : string
    toAddress   : string
    subject     : string
    /// Plaintext body. The HTML body is the same content wrapped in a
    /// trivial <pre>; we don't ship a templating engine here.
    body        : string }

type EmailSendError =
  | TransportError of string
  | ProviderError  of int * string

type IEmailSender =
  abstract Send : EmailMessage -> Async<Result<unit, EmailSendError>>

// -- console (dev) ----------------------------------------------------------

type ConsoleEmailSender () =
  interface IEmailSender with
    member _.Send msg = async {
      eprintfn "  [email/console] %s -> %s :: %s" msg.fromAddress msg.toAddress msg.subject
      eprintfn "----- begin body -----"
      eprintfn "%s" msg.body
      eprintfn "----- end body -----"
      return Ok ()
    }

// -- Mailgun ----------------------------------------------------------------
//
// The Mailgun "messages" endpoint takes form-urlencoded data and Basic
// auth with username `api` + the API key. We deliberately don't pull in
// a full Mailgun SDK — the surface we need is two fields + one POST.

/// Configuration extracted from CLI/env in Program.fs.
type MailgunConfig =
  { apiKey      : string                // "key-..." or "<API key>" depending on plan
    domain      : string                // verified sending domain, e.g. "mg.pulseboard.cloud"
    /// Optional EU region toggle. When `true` we hit `api.eu.mailgun.net`.
    euRegion    : bool
    /// Default `from` if a message doesn't set one. Customer-auth
    /// templates always pass a `fromAddress`, but keeping a default
    /// here means future callers can omit it without surprises.
    defaultFrom : string }

type MailgunEmailSender (cfg : MailgunConfig) =
  let baseUrl =
    if cfg.euRegion then "https://api.eu.mailgun.net/v3/"
    else "https://api.mailgun.net/v3/"
  let http =
    let h = new HttpClient(BaseAddress = Uri baseUrl,
                           Timeout = TimeSpan.FromSeconds 15.0)
    let basic =
      "api:" + cfg.apiKey
      |> Encoding.UTF8.GetBytes
      |> Convert.ToBase64String
    h.DefaultRequestHeaders.Authorization <-
      AuthenticationHeaderValue("Basic", basic)
    h

  interface IEmailSender with
    member _.Send msg = async {
      let fromAddr =
        if String.IsNullOrWhiteSpace msg.fromAddress then cfg.defaultFrom
        else msg.fromAddress
      let form =
        Dictionary<string, string>()
        |> fun d ->
          d.["from"]    <- fromAddr
          d.["to"]      <- msg.toAddress
          d.["subject"] <- msg.subject
          d.["text"]    <- msg.body
          d
      try
        use content = new FormUrlEncodedContent(form)
        let path = sprintf "%s/messages" (Uri.EscapeDataString cfg.domain)
        let! resp = http.PostAsync(path, content) |> Async.AwaitTask
        let! txt  = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        if resp.IsSuccessStatusCode then
          return Ok ()
        else
          return Error (ProviderError (int resp.StatusCode, txt))
      with ex ->
        return Error (TransportError ex.Message)
    }

// -- helper: fire-and-forget with stderr logging ----------------------------
//
// Used by the auth WebParts: they want to return 202 to the caller
// before the SMTP/HTTP round-trip completes, but they also want
// failures visible. This wrapper kicks the send off on the thread
// pool and logs the result.

let fireAndForget (sender : IEmailSender) (msg : EmailMessage) : unit =
  Async.Start (async {
    try
      let! r = sender.Send msg
      match r with
      | Ok () ->
        eprintfn "  [email] sent to %s :: %s" msg.toAddress msg.subject
      | Error (TransportError m) ->
        eprintfn "  [email] transport error to %s: %s" msg.toAddress m
      | Error (ProviderError (code, body)) ->
        eprintfn "  [email] provider %d to %s: %s" code msg.toAddress body
    with ex ->
      eprintfn "  [email] unexpected: %s" ex.Message
  })
