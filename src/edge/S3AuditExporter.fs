module PulseBoard.S3AuditExporter

open System
open System.IO
open System.Text
open System.Threading
open Amazon
open Amazon.S3
open Amazon.S3.Model
open PulseBoard.Audit

// Nightly S3 export of the Postgres-backed audit log. One object per
// UTC calendar day, newline-delimited JSON, key shape
// `<prefix>YYYY/MM/DD.ndjson`. Idempotent across restarts via the
// `pb_audit_exports` table managed in `PgAuditLog`.
//
// Credentials are resolved by the AWS default credential chain
// (environment, shared config, IAM role) — we do not accept inline keys
// to keep secrets out of process arguments. For S3-compatible stores
// (MinIO, Ceph) point `--audit-s3-endpoint=` at the gateway.

type Config =
  { connectionString : string
    bucket           : string
    prefix           : string         // e.g. "audit/" or ""
    region           : string option
    endpoint         : string option  // S3-compatible override
    intervalMinutes  : int }

let private mkClient (cfg : Config) : AmazonS3Client =
  let s3 = AmazonS3Config()
  match cfg.endpoint with
  | Some url ->
    // S3-compatible store (MinIO/Ceph/localstack/etc). RegionEndpoint
    // would override ServiceURL, so don't set it; carry the region (if
    // any) through AuthenticationRegion just for SigV4 signing.
    s3.ServiceURL <- url
    s3.ForcePathStyle <- true
    match cfg.region with
    | Some r -> s3.AuthenticationRegion <- r
    | None   -> s3.AuthenticationRegion <- "us-east-1"
  | None ->
    match cfg.region with
    | Some r ->
      try s3.RegionEndpoint <- RegionEndpoint.GetBySystemName r
      with _ -> ()
    | None -> ()
  new AmazonS3Client(s3)

let private objectKey (prefix : string) (day : DateTime) =
  let p =
    if String.IsNullOrEmpty prefix then ""
    else prefix.TrimEnd('/') + "/"
  sprintf "%s%04d/%02d/%02d.ndjson" p day.Year day.Month day.Day

let private exportDay (client : AmazonS3Client) (cfg : Config) (day : DateTime) =
  let fromTs  = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc)
  let untilTs = fromTs.AddDays 1.0
  let events  = PulseBoard.PgAuditLog.readWindow cfg.connectionString fromTs untilTs
  use ms = new MemoryStream()
  do
    use writer = new StreamWriter(ms, UTF8Encoding(false), 1 <<< 14, leaveOpen = true)
    for ev in events do
      writer.Write(serialize ev)
      writer.Write '\n'
  let body = ms.ToArray()
  let key  = objectKey cfg.prefix day
  if body.Length > 0 then
    use upload = new MemoryStream(body)
    let req = PutObjectRequest()
    req.BucketName  <- cfg.bucket
    req.Key         <- key
    req.InputStream <- upload
    req.ContentType <- "application/x-ndjson"
    client.PutObjectAsync(req).GetAwaiter().GetResult() |> ignore
  // Record even an empty day so the exporter doesn't re-scan it.
  PulseBoard.PgAuditLog.recordExport
    cfg.connectionString day key (int64 events.Length) (int64 body.Length)
  printfn "  [audit-export] day=%s rows=%d bytes=%d key=s3://%s/%s"
    (day.ToString "yyyy-MM-dd") events.Length body.Length cfg.bucket key

let private runOnce (cfg : Config) =
  try
    let days = PulseBoard.PgAuditLog.pendingExportDays cfg.connectionString
    if days.Length > 0 then
      use client = mkClient cfg
      for d in days do
        try exportDay client cfg d
        with ex ->
          eprintfn "  [audit-export] failed day=%s: %s"
            (d.ToString "yyyy-MM-dd") ex.Message
  with ex ->
    eprintfn "  [audit-export] tick failed: %s" ex.Message

/// Start the periodic exporter. First run is delayed by 30s so it never
/// races startup logging; subsequent runs honour `intervalMinutes`.
/// Caller owns the returned `Timer` (dispose at shutdown).
let start (cfg : Config) : Timer =
  let interval = TimeSpan.FromMinutes(float (max 1 cfg.intervalMinutes))
  new Timer(TimerCallback(fun _ -> runOnce cfg),
            null, TimeSpan.FromSeconds 30., interval)
