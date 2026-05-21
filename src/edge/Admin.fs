module PulseBoard.Admin

open System
open System.IO
open System.Text
open System.Text.Json
open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.RequestErrors
open Suave.ServerErrors
open PulseBoard.Tenancy
open PulseBoard.Audit

// REST surface for tenant + API key management. All routes require the
// `Admin` scope; gating is composed at the call site in Program.fs via
// `Rbac.requireScope`. Handlers here append a per-action audit event with
// richer details (slug, key id, role, scopes) on top of the gate event so
// the audit trail captures the business decision, not just the access.

// -- helpers ----------------------------------------------------------------

let private readBody (req : HttpRequest) : string =
  if isNull req.rawForm || req.rawForm.Length = 0 then ""
  else Encoding.UTF8.GetString req.rawForm

let private jsonResp (status : int) (body : string) : WebPart =
  let writer =
    match status with
    | 200 -> OK
    | 201 -> Suave.Successful.CREATED
    | 400 -> BAD_REQUEST
    | 404 -> NOT_FOUND
    | 409 -> Suave.RequestErrors.CONFLICT
    | _   -> INTERNAL_ERROR
  writer body >=> Writers.setMimeType "application/json"

let private errJson (status : int) (msg : string) : WebPart =
  jsonResp status (sprintf """{"error":%s}""" (JsonSerializer.Serialize msg))

let private tryParseJson (body : string) : JsonDocument option =
  if String.IsNullOrWhiteSpace body then None
  else
    try Some (JsonDocument.Parse body)
    with _ -> None

let private tryGetString (el : JsonElement) (name : string) : string option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String ->
    let s = v.GetString()
    if String.IsNullOrWhiteSpace s then None else Some (s.Trim())
  | _ -> None

let private parseRole (s : string) : Role option =
  match s.Trim().ToLowerInvariant() with
  | "viewer"  -> Some Viewer
  | "editor"  -> Some Editor
  | "admin"   -> Some Admin
  | "billing" -> Some Billing
  | _ -> None

let private roleStr = function
  | Viewer  -> "viewer"
  | Editor  -> "editor"
  | Admin   -> "admin"
  | Billing -> "billing"

let private parseScope (s : string) : Scope option =
  match s.Trim().ToLowerInvariant() with
  | "ingest" -> Some Scope.Ingest
  | "query"  -> Some Scope.Query
  | "admin"  -> Some Scope.Admin
  | _ -> None

let private scopesToList (have : Scope) : string list =
  [ if hasScope have Scope.Ingest then yield "ingest"
    if hasScope have Scope.Query  then yield "query"
    if hasScope have Scope.Admin  then yield "admin" ]

let private jsonArr (xs : string seq) =
  xs
  |> Seq.map JsonSerializer.Serialize
  |> String.concat ","
  |> sprintf "[%s]"

let private tenantJson (t : Tenant) =
  let (TenantId id) = t.id
  sprintf """{"id":%s,"slug":%s,"createdAt":"%s"}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize t.slug)
    (t.createdAt.ToString("o"))

let private apiKeySummaryJson (r : ApiKeyRecord) =
  let (ApiKeyId id)     = r.id
  let (TenantId tid)    = r.tenantId
  let lastUsed =
    match !r.lastUsedAt with
    | Some ts -> sprintf "\"%s\"" (ts.ToString("o"))
    | None    -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"label":%s,"role":"%s","scopes":%s,"createdAt":"%s","lastUsedAt":%s}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize r.label)
    (roleStr r.role)
    (jsonArr (scopesToList r.scopes))
    (r.createdAt.ToString("o"))
    lastUsed

let private issuedKeyJson (issued : IssuedKey) =
  let r = issued.record
  let (ApiKeyId id)  = r.id
  let (TenantId tid) = r.tenantId
  sprintf
    """{"id":%s,"tenantId":%s,"label":%s,"role":"%s","scopes":%s,"createdAt":"%s","plaintext":%s,"warning":"plaintext is shown once and cannot be recovered"}"""
    (JsonSerializer.Serialize id)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize r.label)
    (roleStr r.role)
    (jsonArr (scopesToList r.scopes))
    (r.createdAt.ToString("o"))
    (JsonSerializer.Serialize issued.plaintext)

let private userJson (u : UserRecord) =
  let (UserId uid)   = u.id
  let (TenantId tid) = u.tenantId
  let lastLogin =
    match !u.lastLoginAt with
    | Some ts -> sprintf "\"%s\"" (ts.ToString("o"))
    | None    -> "null"
  sprintf
    """{"id":%s,"tenantId":%s,"issuer":%s,"subject":%s,"email":%s,"role":"%s","createdAt":"%s","lastLoginAt":%s}"""
    (JsonSerializer.Serialize uid)
    (JsonSerializer.Serialize tid)
    (JsonSerializer.Serialize u.issuer)
    (JsonSerializer.Serialize u.subject)
    (match u.email with Some e -> JsonSerializer.Serialize e | None -> "null")
    (roleStr u.role)
    (u.createdAt.ToString("o"))
    lastLogin

// -- audit helpers ----------------------------------------------------------

let private auditEvent (log : IAuditLog) (ctx : HttpContext)
                       (action : string) (outcome : Outcome)
                       (details : string option) =
  let t = PulseBoard.Rbac.tryGetTenant ctx
  let ev : AuditEvent =
    { ts       = DateTimeOffset.UtcNow
      tenant   = t |> Option.map (fun x -> x.tenant.id)
      apiKeyId = t |> Option.map (fun x -> x.apiKeyId)
      action   = action
      resource = ctx.request.path
      outcome  = outcome
      remoteIp = None
      details  = details }
  try log.Append ev with _ -> ()

// -- handlers ---------------------------------------------------------------

let private listTenants (store : ITenantStore) : WebPart =
  fun ctx -> async {
    let body =
      store.Tenants()
      |> Array.sortBy (fun t -> t.createdAt)
      |> Array.map tenantJson
      |> String.concat ","
      |> sprintf "[%s]"
    return! jsonResp 200 body ctx
  }

let private createTenant (store : ITenantStore) (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let body = readBody ctx.request
    match tryParseJson body with
    | None ->
      auditEvent log ctx "admin.tenant.create" Deny (Some "invalid json")
      return! errJson 400 "invalid JSON body" ctx
    | Some doc ->
      use _ = doc
      match tryGetString doc.RootElement "slug" with
      | None ->
        auditEvent log ctx "admin.tenant.create" Deny (Some "missing slug")
        return! errJson 400 "field 'slug' is required" ctx
      | Some slug ->
        try
          let existed = store.TryGetTenantBySlug slug |> Option.isSome
          let t = store.CreateTenant slug
          let (TenantId tid) = t.id
          let outcome = if existed then Allow else Allow
          let detail =
            if existed then sprintf "slug=%s id=%s (existing)" slug tid
            else            sprintf "slug=%s id=%s (created)"  slug tid
          auditEvent log ctx "admin.tenant.create" outcome (Some detail)
          let status = if existed then 200 else 201
          return! jsonResp status (tenantJson t) ctx
        with ex ->
          auditEvent log ctx "admin.tenant.create" Error (Some ex.Message)
          return! errJson 400 ex.Message ctx
  }

let private listApiKeys (store : ITenantStore) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        store.ApiKeysFor (TenantId tenantId)
        |> Array.map apiKeySummaryJson
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private issueApiKey (store : ITenantStore) (log : IAuditLog)
                        (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      auditEvent log ctx "admin.apikey.issue" Deny
        (Some (sprintf "tenantId=%s not found" tenantId))
      return! errJson 404 "tenant not found" ctx
    | Some t ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.apikey.issue" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        let root = doc.RootElement
        let label = tryGetString root "label" |> Option.defaultValue "unnamed"
        let roleRaw =
          tryGetString root "role" |> Option.defaultValue "viewer"
        match parseRole roleRaw with
        | None ->
          auditEvent log ctx "admin.apikey.issue" Deny
            (Some (sprintf "bad role=%s" roleRaw))
          return! errJson 400
            "field 'role' must be one of: viewer|editor|admin|billing" ctx
        | Some role ->
          // `scopes`: optional array of strings. Defaults derived from role
          // so the common case ("give me an editor key") needs no scopes
          // field.
          let scopesFromBody =
            match root.TryGetProperty "scopes" with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
              let mutable acc = Scope.None
              let mutable bad : string option = None
              for el in arr.EnumerateArray() do
                if bad.IsNone then
                  if el.ValueKind = JsonValueKind.String then
                    match parseScope (el.GetString()) with
                    | Some s -> acc <- acc ||| s
                    | None   -> bad <- Some (el.GetString())
                  else bad <- Some (el.ToString())
              match bad with
              | Some b -> Result.Error b
              | None   -> Result.Ok (Some acc)
            | _ -> Result.Ok None
          match scopesFromBody with
          | Result.Error b ->
            auditEvent log ctx "admin.apikey.issue" Deny
              (Some (sprintf "bad scope=%s" b))
            return! errJson 400
              (sprintf "unknown scope '%s' (allowed: ingest|query|admin)" b) ctx
          | Result.Ok scopesOpt ->
            let defaultScopes =
              match role with
              | Viewer  -> Scope.Query
              | Editor  -> Scope.Ingest ||| Scope.Query
              | Admin   -> Scope.Ingest ||| Scope.Query ||| Scope.Admin
              | Billing -> Scope.None
            let scopes =
              match scopesOpt with
              | Some s when s <> Scope.None -> s
              | _ -> defaultScopes
            try
              let issued = store.IssueApiKey(t.id, label, role, scopes)
              let (ApiKeyId kid) = issued.record.id
              let (TenantId tid) = t.id
              auditEvent log ctx "admin.apikey.issue" Allow
                (Some (sprintf "tenantId=%s apiKeyId=%s role=%s scopes=%s"
                         tid kid (roleStr role)
                         (String.concat "|" (scopesToList scopes))))
              return! jsonResp 201 (issuedKeyJson issued) ctx
            with ex ->
              auditEvent log ctx "admin.apikey.issue" Error (Some ex.Message)
              return! errJson 400 ex.Message ctx
  }

let private listUsers (store : ITenantStore) (tenantId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetTenant (TenantId tenantId) with
    | None ->
      return! errJson 404 "tenant not found" ctx
    | Some _ ->
      let body =
        store.UsersFor (TenantId tenantId)
        |> Array.map userJson
        |> String.concat ","
        |> sprintf "[%s]"
      return! jsonResp 200 body ctx
  }

let private updateUserRole (store : ITenantStore) (log : IAuditLog)
                           (userId : string) : WebPart =
  fun ctx -> async {
    match store.TryGetUserById (UserId userId) with
    | None ->
      auditEvent log ctx "admin.user.update" Deny
        (Some (sprintf "userId=%s not found" userId))
      return! errJson 404 "user not found" ctx
    | Some _ ->
      let body = readBody ctx.request
      match tryParseJson body with
      | None ->
        auditEvent log ctx "admin.user.update" Deny (Some "invalid json")
        return! errJson 400 "invalid JSON body" ctx
      | Some doc ->
        use _ = doc
        match tryGetString doc.RootElement "role" with
        | None ->
          auditEvent log ctx "admin.user.update" Deny (Some "missing role")
          return! errJson 400 "field 'role' is required" ctx
        | Some roleRaw ->
          match parseRole roleRaw with
          | None ->
            auditEvent log ctx "admin.user.update" Deny
              (Some (sprintf "bad role=%s" roleRaw))
            return! errJson 400
              "field 'role' must be one of: viewer|editor|admin|billing" ctx
          | Some role ->
            match store.UpdateUserRole(UserId userId, role) with
            | None ->
              auditEvent log ctx "admin.user.update" Error
                (Some "update returned None")
              return! errJson 500 "failed to update user" ctx
            | Some updated ->
              let (UserId uid) = updated.id
              auditEvent log ctx "admin.user.update" Allow
                (Some (sprintf "userId=%s role=%s" uid (roleStr role)))
              return! jsonResp 200 (userJson updated) ctx
  }

let private auditTail (log : IAuditLog) : WebPart =
  fun ctx -> async {
    let tail =
      match ctx.request.queryParam "tail" with
      | Choice1Of2 v ->
        match Int32.TryParse v with
        | true, n when n > 0 -> min n 1000
        | _ -> 100
      | _ -> 100
    let events = log.Tail tail
    let body =
      events
      |> Array.map serialize
      |> String.concat ","
      |> sprintf "[%s]"
    return! jsonResp 200 body ctx
  }

/// Complete admin WebPart. Gating (`Admin` scope) is applied by the caller.
let webPart (store : ITenantStore) (log : IAuditLog) : WebPart =
  choose [
    GET  >=> path "/api/admin/audit"        >=> auditTail log
    GET  >=> path "/api/admin/tenants"      >=> listTenants store
    POST >=> path "/api/admin/tenants"      >=> createTenant store log
    GET  >=> pathScan "/api/admin/tenants/%s/api-keys" (listApiKeys store)
    POST >=> pathScan "/api/admin/tenants/%s/api-keys" (issueApiKey store log)
    GET  >=> pathScan "/api/admin/tenants/%s/users"    (listUsers store)
    PATCH >=> pathScan "/api/admin/users/%s"           (updateUserRole store log)
    NOT_FOUND """{"error":"unknown admin endpoint"}"""
      >=> Writers.setMimeType "application/json"
  ]
