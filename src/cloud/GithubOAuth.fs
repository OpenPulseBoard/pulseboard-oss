module PulseBoard.GithubOAuth

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text.Json
open PulseBoard.CustomerAuth

// Phase 10 step 3 — GitHub OAuth.
//
// We use the classic OAuth 2.0 authorisation-code flow (not OIDC —
// GitHub doesn't ship an `id_token`), with the `user:email` scope so
// we can pick the user's primary verified email when creating a fresh
// PulseBoard customer.
//
// CSRF is defeated by a `state` parameter that's:
//   * stored server-side under a short-lived (10 min) cache, and
//   * mirrored into an HttpOnly cookie (`pb_gh_state`)
// so callbacks must both present the state in the URL AND carry the
// matching cookie — neither attacker-controlled side alone is enough.
//
// The pending entry also carries `linkToCustomerId`: when the user
// clicked "Continue with GitHub" while already signed in, the callback
// just stamps the GH id onto the existing customer instead of trying
// to create or sign in a different one.

// -- types ------------------------------------------------------------------

[<NoComparison; NoEquality>]
type GithubConfig =
  { clientId     : string
    clientSecret : string
    /// Absolute callback URL, e.g. "https://pulseboard.cloud/api/auth/github/callback".
    /// MUST match what's registered in the GitHub OAuth app.
    callbackUrl  : string }

[<NoComparison; NoEquality>]
type GithubUserInfo =
  { id        : int64
    login     : string
    name      : string option
    /// Primary verified email if `user:email` scope was granted and at
    /// least one verified address exists.
    email     : string option
    avatarUrl : string option }

[<NoComparison; NoEquality>]
type PendingState =
  { state             : string
    createdAt         : DateTimeOffset
    returnTo          : string
    linkToCustomerId  : CustomerId option }

let pendingLifetime = TimeSpan.FromMinutes 10.0

// -- state cache ------------------------------------------------------------
//
// In-memory cache of pending OAuth handshakes. Surviving a process
// restart isn't important: anyone with an in-flight `state` simply
// retries the dance. The cache is per-binary which is fine for the
// apex (singleton).

type StateCache () =
  let entries = ConcurrentDictionary<string, PendingState>()
  /// Periodic sweep happens lazily on every `tryConsume`.
  let sweep (now : DateTimeOffset) =
    for kv in entries do
      if now - kv.Value.createdAt > pendingLifetime then
        entries.TryRemove kv.Key |> ignore
  member _.Insert (p : PendingState) =
    entries.[p.state] <- p
  member _.TryConsume (state : string) : PendingState option =
    let now = DateTimeOffset.UtcNow
    sweep now
    match entries.TryRemove state with
    | true, p when now - p.createdAt <= pendingLifetime -> Some p
    | _ -> None

// -- helpers ----------------------------------------------------------------

/// Generate a 32-byte URL-safe state token.
let generateState () : string =
  let bytes = Array.zeroCreate 32
  RandomNumberGenerator.Fill(Span bytes)
  Convert.ToBase64String(bytes)
    .Replace('+', '-').Replace('/', '_').TrimEnd('=')

/// Build the authorisation URL the browser is redirected to.
let buildAuthorizeUrl (cfg : GithubConfig) (state : string) : string =
  let qp =
    [ "client_id",     cfg.clientId
      "redirect_uri",  cfg.callbackUrl
      "scope",         "read:user user:email"
      "state",         state
      "allow_signup",  "true" ]
    |> List.map (fun (k, v) ->
         sprintf "%s=%s" (Uri.EscapeDataString k) (Uri.EscapeDataString v))
    |> String.concat "&"
  sprintf "https://github.com/login/oauth/authorize?%s" qp

// -- HTTP exchange ----------------------------------------------------------
//
// One shared HttpClient. GitHub's OAuth and REST endpoints both
// require a User-Agent header — we set "PulseBoard" globally.

let private http =
  let h = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)
  h.DefaultRequestHeaders.UserAgent.Add(
    ProductInfoHeaderValue("PulseBoard", "1.0"))
  h.DefaultRequestHeaders.Accept.Add(
    MediaTypeWithQualityHeaderValue("application/json"))
  h

[<NoComparison; NoEquality>]
type ExchangeError =
  | TransportError of string
  | ProviderError  of string

let exchangeCode (cfg : GithubConfig) (code : string) : Async<Result<string, ExchangeError>> =
  async {
    try
      let form =
        new FormUrlEncodedContent(
          [ KeyValuePair("client_id",     cfg.clientId)
            KeyValuePair("client_secret", cfg.clientSecret)
            KeyValuePair("code",          code)
            KeyValuePair("redirect_uri",  cfg.callbackUrl) ])
      use req =
        new HttpRequestMessage(
          HttpMethod.Post, "https://github.com/login/oauth/access_token")
      req.Content <- form
      req.Headers.Accept.Clear()
      req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if not resp.IsSuccessStatusCode then
        return Result.Error (ProviderError (sprintf "HTTP %d: %s" (int resp.StatusCode) body))
      else
        try
          use doc = JsonDocument.Parse body
          let root = doc.RootElement
          match root.TryGetProperty "access_token" with
          | true, v when v.ValueKind = JsonValueKind.String ->
            let token = v.GetString()
            if String.IsNullOrWhiteSpace token then
              // GitHub returns `{"error":"...","error_description":"..."}`
              // with 200 OK when the code is bad — surface that.
              let err =
                match root.TryGetProperty "error_description" with
                | true, ed when ed.ValueKind = JsonValueKind.String -> ed.GetString()
                | _ -> "no access_token in response"
              return Result.Error (ProviderError err)
            else
              return Result.Ok token
          | _ ->
            let err =
              match root.TryGetProperty "error_description" with
              | true, ed when ed.ValueKind = JsonValueKind.String -> ed.GetString()
              | _ -> "no access_token in response"
            return Result.Error (ProviderError err)
        with ex ->
          return Result.Error (ProviderError (sprintf "bad JSON: %s" ex.Message))
    with ex ->
      return Result.Error (TransportError ex.Message)
  }

let private apiGet (token : string) (path : string) : Async<Result<string, ExchangeError>> =
  async {
    try
      use req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path)
      req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
      req.Headers.Accept.Clear()
      req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
      // GitHub asks integrations to pin an API version. 2022-11-28 is
      // the latest stable as of this writing.
      req.Headers.Add("X-GitHub-Api-Version", "2022-11-28")
      use! resp = http.SendAsync req |> Async.AwaitTask
      let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
      if resp.IsSuccessStatusCode then return Result.Ok body
      else return Result.Error (ProviderError (sprintf "HTTP %d: %s" (int resp.StatusCode) body))
    with ex ->
      return Result.Error (TransportError ex.Message)
  }

let fetchUser (token : string) : Async<Result<GithubUserInfo, ExchangeError>> =
  async {
    let! r = apiGet token "/user"
    match r with
    | Result.Error e -> return Result.Error e
    | Result.Ok body ->
      try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let getStr (n : string) =
          match root.TryGetProperty n with
          | true, v when v.ValueKind = JsonValueKind.String ->
            let s = v.GetString()
            if String.IsNullOrWhiteSpace s then None else Some s
          | _ -> None
        let id =
          match root.TryGetProperty "id" with
          | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64()
          | _ -> 0L
        if id = 0L then
          return Result.Error (ProviderError "GitHub /user response missing numeric id")
        else
          let info =
            { id        = id
              login     = getStr "login" |> Option.defaultValue (string id)
              name      = getStr "name"
              email     = getStr "email"
              avatarUrl = getStr "avatar_url" }
          // /user.email is only populated if the user marked an email
          // public OR token has `user:email`; fall through to
          // /user/emails to pick the primary verified address.
          if info.email.IsSome then return Result.Ok info
          else
            let! er = apiGet token "/user/emails"
            match er with
            | Result.Error _ -> return Result.Ok info  // non-fatal
            | Result.Ok ebody ->
              try
                use edoc = JsonDocument.Parse ebody
                let primary =
                  edoc.RootElement.EnumerateArray()
                  |> Seq.tryFind (fun el ->
                       let primary = el.TryGetProperty "primary"
                       let verified = el.TryGetProperty "verified"
                       match primary, verified with
                       | (true, p), (true, v) ->
                         p.ValueKind = JsonValueKind.True
                         && v.ValueKind = JsonValueKind.True
                       | _ -> false)
                let email =
                  primary |> Option.bind (fun el ->
                    match el.TryGetProperty "email" with
                    | true, e when e.ValueKind = JsonValueKind.String ->
                      let s = e.GetString()
                      if String.IsNullOrWhiteSpace s then None else Some s
                    | _ -> None)
                return Result.Ok { info with email = email }
              with _ ->
                return Result.Ok info
      with ex ->
        return Result.Error (ProviderError (sprintf "bad /user JSON: %s" ex.Message))
  }
