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
///
/// `customerAuth` is the Phase 10 customer-account surface. When supplied
/// the apex serves the new email/password + GitHub auth endpoints under
/// `/api/auth/*` and the legacy anonymous `/api/signup` is removed (a
/// 404 makes the deprecation visible to anyone still calling it). When
/// `None`, the legacy proxy-to-provisioner behaviour is preserved so
/// existing deployments keep working until they opt in.
let webPart (wwwroot : string)
            (provisionerUrl : string option)
            (customerAuth : PulseBoard.CustomerAuthApi.CustomerAuthConfig option)
            (portal : PulseBoard.PortalApi.PortalApiConfig option)
            : WebPart =
  let http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
  let signupOrAuth : WebPart =
    match customerAuth with
    | Some cfg ->
      // New world: anonymous /api/signup is gone; the customer-auth
      // surface owns signup.
      choose [
        PulseBoard.CustomerAuthApi.webPart cfg
        POST >=> path "/api/signup" >=>
          (Suave.RequestErrors.GONE
             """{"error":"deprecated: use POST /api/auth/signup to create a customer account, then POST /api/portal/workspaces to create a workspace"}"""
           >=> Writers.setMimeType "application/json")
      ]
    | None ->
      // Legacy path: forward /api/signup to the provisioner.
      match provisionerUrl with
      | Some u -> POST >=> path "/api/signup" >=> proxySignup u http
      | None ->
        POST >=> path "/api/signup" >=>
          (Suave.ServerErrors.SERVICE_UNAVAILABLE
             """{"error":"site-only deployment: no --provisioner-url configured"}"""
           >=> Writers.setMimeType "application/json")
  choose [
    GET >=> path "/healthz" >=>
      (OK """{"status":"ok","role":"site-only"}"""
       >=> Writers.setMimeType "application/json")
    signupOrAuth
    (match portal with
     | Some pcfg -> PulseBoard.PortalApi.webPart pcfg
     | None      -> (fun _ -> async.Return None))
    GET >=> path "/portal"        >=> Files.browseFile wwwroot "portal.html"
    GET >=> path "/portal.html"   >=> Files.browseFile wwwroot "portal.html"
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
    GET >=> path "/forgot"       >=> Files.browseFile wwwroot "forgot.html"
    GET >=> path "/auth/reset"   >=> Files.browseFile wwwroot "reset.html"
    GET >=> Files.browse wwwroot
    NOT_FOUND "Not found."
  ]

/// Run a standalone site-only server. Returns once the server exits.
let run (port : int) (wwwroot : string)
        (provisionerUrl : string option)
        (customerAuth : PulseBoard.CustomerAuthApi.CustomerAuthConfig option)
        (portal : PulseBoard.PortalApi.PortalApiConfig option)
        : unit =
  // Accepts a comma-separated list, e.g. PULSE_BIND_ADDR="::,0.0.0.0".
  // See the matching block in Program.fs for the rationale (.NET on
  // Linux defaults AF_INET6 sockets to IPV6_V6ONLY=1, so a single `::`
  // listener does NOT accept the IPv4 loopback that Fly's health check
  // uses; we need both an IPv6 and an IPv4 binding to be truly
  // dual-stack).
  let bindAddrs =
    match Environment.GetEnvironmentVariable "PULSE_BIND_ADDR" with
    | null | "" -> [ IPAddress.Loopback ]
    | s ->
      s.Split([| ',' ; ';' ; ' ' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.choose (fun raw ->
           let t = raw.Trim()
           match IPAddress.TryParse t with
           | true, ip -> Some ip
           | _ ->
             eprintfn "  [WARN] PULSE_BIND_ADDR entry %s is not a valid IP; ignoring" t
             None)
      |> Array.toList
      |> function
         | []  ->
           eprintfn "  [WARN] PULSE_BIND_ADDR=%s yielded no valid IPs; falling back to 127.0.0.1" s
           [ IPAddress.Loopback ]
         | ips -> ips
  let config =
    { defaultConfig with
        bindings   = bindAddrs |> List.map (fun ip -> HttpBinding.create HTTP ip (uint16 port))
        homeFolder = Some wwwroot }
  for ip in bindAddrs do
    printfn "PulseBoard (site-only) listening on http://%O:%d" ip port
  match provisionerUrl with
  | Some u -> printfn "  Provisioner: %s (signup is proxied)" u
  | None   -> printfn "  Provisioner: <unset> (signup returns 503)"
  match customerAuth with
  | Some _ -> printfn "  CustomerAuth: enabled (email+password + GitHub)"
  | None   -> printfn "  CustomerAuth: disabled (legacy anonymous signup path)"
  match portal with
  | Some pc ->
    printfn "  Portal API: enabled (provisioner=%s, token=%s)"
      pc.provisioner.baseUrl
      (if pc.provisioner.token.IsSome then "set" else "<unset> -> 503")
  | None    -> printfn "  Portal API: disabled"
  startWebServer config (webPart wwwroot provisionerUrl customerAuth portal)
