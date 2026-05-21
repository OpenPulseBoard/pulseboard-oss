module PulseBoard.SiteOnly

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors

// Phase 9 — `--site-only` mode.
//
// Strips PulseBoard down to just the public marketing site. Used to run
// the apex host (`pulseboard.cloud`) which exists only to:
//   * serve home / docs / pricing / signup / signin static pages,
//   * forward POST /api/signup to a separately-deployed provisioner.
//
// The site-only binary has NO tenant store, NO quota state, NO ingest /
// query routes, NO admin UI — those live on per-customer workspaces.

let private proxySignup (provisionerUrl : string) (http : HttpClient) : WebPart =
  fun ctx -> async {
    if isNull ctx.request.rawForm then
      return! Suave.RequestErrors.BAD_REQUEST "empty body" ctx
    else
      let body = ctx.request.rawForm
      let target = provisionerUrl.TrimEnd('/') + "/api/provision"
      use content = new ByteArrayContent(body)
      content.Headers.ContentType <-
        System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
      try
        let! resp = http.PostAsync(target, content) |> Async.AwaitTask
        let! txt = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        let writer =
          match int resp.StatusCode with
          | 200 -> OK | 201 -> Suave.Successful.CREATED
          | 400 -> BAD_REQUEST | 404 -> NOT_FOUND
          | 409 -> Suave.RequestErrors.CONFLICT
          | 429 -> Suave.RequestErrors.TOO_MANY_REQUESTS
          | _   -> Suave.ServerErrors.INTERNAL_ERROR
        return! (writer txt >=> Writers.setMimeType "application/json") ctx
      with ex ->
        eprintfn "  [site-only] provisioner proxy error: %s" ex.Message
        return!
          (Suave.ServerErrors.SERVICE_UNAVAILABLE
             (sprintf """{"error":"provisioner unreachable: %s"}""" ex.Message)
           >=> Writers.setMimeType "application/json") ctx
  }

/// Build the Suave webpart for site-only mode. Pass `None` for
/// provisionerUrl to surface a clear 503 on signup attempts (useful when
/// staging a deployment without the provisioner ready yet).
let webPart (wwwroot : string) (provisionerUrl : string option) : WebPart =
  let http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
  let signupPart =
    match provisionerUrl with
    | Some u -> POST >=> path "/api/signup" >=> proxySignup u http
    | None ->
      POST >=> path "/api/signup" >=>
        (Suave.ServerErrors.SERVICE_UNAVAILABLE
           """{"error":"site-only deployment: no --provisioner-url configured"}"""
         >=> Writers.setMimeType "application/json")
  choose [
    signupPart
    GET >=> path "/"             >=> Files.browseFile wwwroot "home.html"
    GET >=> path "/index.html"   >=> Files.browseFile wwwroot "home.html"
    GET >=> path "/home"         >=> Files.browseFile wwwroot "home.html"
    GET >=> path "/docs"         >=> Files.browseFile wwwroot "docs.html"
    GET >=> path "/docs.html"    >=> Files.browseFile wwwroot "docs.html"
    GET >=> path "/pricing"      >=> Files.browseFile wwwroot "pricing.html"
    GET >=> path "/pricing.html" >=> Files.browseFile wwwroot "pricing.html"
    GET >=> path "/signup"       >=> Files.browseFile wwwroot "signup.html"
    GET >=> path "/signup.html"  >=> Files.browseFile wwwroot "signup.html"
    GET >=> path "/signin"       >=> Files.browseFile wwwroot "signin.html"
    GET >=> path "/signin.html"  >=> Files.browseFile wwwroot "signin.html"
    GET >=> Files.browse wwwroot
    NOT_FOUND "Not found."
  ]

/// Run a standalone site-only server. Returns once the server exits.
let run (port : int) (wwwroot : string) (provisionerUrl : string option) : unit =
  let config =
    { defaultConfig with
        bindings   = [ HttpBinding.create HTTP IPAddress.Loopback (uint16 port) ]
        homeFolder = Some wwwroot }
  printfn "PulseBoard (site-only) listening on http://127.0.0.1:%d" port
  match provisionerUrl with
  | Some u -> printfn "  Provisioner: %s (signup is proxied)" u
  | None   -> printfn "  Provisioner: <unset> (signup returns 503)"
  startWebServer config (webPart wwwroot provisionerUrl)
