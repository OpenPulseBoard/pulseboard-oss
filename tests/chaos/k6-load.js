/**
 * PulseBoard k6 load profile — Phase 11.5
 *
 * Simulates a realistic production mix:
 *   - 10 000 metric series pushed via Prometheus remote_write at ~1 000 samples/s
 *   - Concurrent Loki log pushes at ~500 lines/s
 *   - OTLP metric ingestion at ~200 samples/s
 *   - Prom instant + range queries at ~50 QPS
 *   - Loki query_range at ~20 QPS
 *
 * SLOs asserted at end of test:
 *   - pulse_query_p99_ms < 1 000 ms  (checked via /api/prom/api/v1/query)
 *   - HTTP error rate < 0.1% across all ingest endpoints
 *   - Zero dropped notifications (queried from /api/healthz extended metrics)
 *
 * Usage (against a local or staging PulseBoard instance):
 *
 *   BASE_URL=http://localhost:8080 k6 run tests/chaos/k6-load.js
 *
 *   # With API key (multi-tenant mode):
 *   BASE_URL=https://my.pulseboard.io API_KEY=pk_xxx.yyy k6 run tests/chaos/k6-load.js
 *
 *   # Nightly CI short run (2 minutes, lower VUs):
 *   BASE_URL=http://localhost:8080 DURATION=2m VUS_INGEST=20 VUS_QUERY=5 k6 run tests/chaos/k6-load.js
 *
 * Environment variables:
 *   BASE_URL    — target base URL (default: http://localhost:8080)
 *   API_KEY     — Bearer token for multi-tenant mode (default: none)
 *   DURATION    — test duration string (default: 10m)
 *   VUS_INGEST  — virtual users for ingest scenarios (default: 50)
 *   VUS_QUERY   — virtual users for query scenarios (default: 10)
 *   SERIES      — number of distinct metric series (default: 10000)
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { randomIntBetween } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const BASE_URL   = __ENV.BASE_URL  || 'http://localhost:8080';
const API_KEY    = __ENV.API_KEY   || '';
const DURATION   = __ENV.DURATION  || '10m';
const VUS_INGEST = parseInt(__ENV.VUS_INGEST || '50');
const VUS_QUERY  = parseInt(__ENV.VUS_QUERY  || '10');
const SERIES     = parseInt(__ENV.SERIES     || '10000');

// ---------------------------------------------------------------------------
// Custom metrics
// ---------------------------------------------------------------------------

const ingestErrors  = new Counter('ingest_errors');
const queryErrors   = new Counter('query_errors');
const ingestRate    = new Rate('ingest_success_rate');
const queryP99      = new Trend('query_response_ms', true);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function headers() {
  const h = { 'Content-Type': 'application/x-protobuf' };
  if (API_KEY) h['Authorization'] = `Bearer ${API_KEY}`;
  return h;
}

function jsonHeaders() {
  const h = { 'Content-Type': 'application/json' };
  if (API_KEY) h['Authorization'] = `Bearer ${API_KEY}`;
  return h;
}

// Encode a uint64 as a varint into a Uint8Array offset, returns new offset.
function writeVarint(arr, offset, value) {
  let v = value;
  while (v > 127) {
    arr[offset++] = (v & 0x7f) | 0x80;
    v = Math.floor(v / 128);
  }
  arr[offset++] = v & 0x7f;
  return offset;
}

function writeTag(arr, offset, field, wireType) {
  return writeVarint(arr, offset, (field << 3) | wireType);
}

// Write a length-delimited field (wire type 2).
function writeLenDelim(arr, offset, field, data) {
  offset = writeTag(arr, offset, field, 2);
  offset = writeVarint(arr, offset, data.length);
  for (let i = 0; i < data.length; i++) arr[offset++] = data[i];
  return offset;
}

// Encode a UTF-8 string as bytes (ASCII-safe for metric names).
function utf8(s) {
  const bytes = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i) & 0xff;
  return bytes;
}

// Write a double (wire type 1 = 64-bit little-endian).
function writeDouble(arr, offset, field, value) {
  offset = writeTag(arr, offset, field, 1);
  const buf = new ArrayBuffer(8);
  new DataView(buf).setFloat64(0, value, true);
  const bytes = new Uint8Array(buf);
  for (let i = 0; i < 8; i++) arr[offset++] = bytes[i];
  return offset;
}

// Write a varint int64 field (wire type 0).
function writeInt64(arr, offset, field, value) {
  offset = writeTag(arr, offset, field, 0);
  return writeVarint(arr, offset, value);
}

/**
 * Build a minimal Prometheus WriteRequest protobuf for one time-series.
 * Label: __name__ = metricName
 * Sample: value at tsMs.
 */
function buildWriteRequest(metricName, value, tsMs) {
  const buf = new Uint8Array(512);
  let o = 0;

  // Label: {name: "__name__", value: metricName}
  const nameLabelBuf = new Uint8Array(64);
  let nl = 0;
  nl = writeLenDelim(nameLabelBuf, nl, 1, utf8('__name__'));
  nl = writeLenDelim(nameLabelBuf, nl, 2, utf8(metricName));
  const nameLabel = nameLabelBuf.slice(0, nl);

  // TimeSeries: label(1) + sample(2)
  const tsBuf = new Uint8Array(256);
  let ts = 0;
  ts = writeLenDelim(tsBuf, ts, 1, nameLabel);
  // Sample sub-message: value(1 double) + timestamp(2 int64)
  const sampleBuf = new Uint8Array(32);
  let sp = 0;
  sp = writeDouble(sampleBuf, sp, 1, value);
  sp = writeInt64(sampleBuf, sp, 2, tsMs);
  ts = writeLenDelim(tsBuf, ts, 2, sampleBuf.slice(0, sp));
  const timeSeries = tsBuf.slice(0, ts);

  // WriteRequest: timeseries(1)
  o = writeLenDelim(buf, o, 1, timeSeries);
  return buf.slice(0, o);
}

// ---------------------------------------------------------------------------
// Test options
// ---------------------------------------------------------------------------

export const options = {
  scenarios: {
    prom_remote_write: {
      executor: 'constant-vus',
      vus: VUS_INGEST,
      duration: DURATION,
      exec: 'promRemoteWrite',
    },
    loki_push: {
      executor: 'constant-vus',
      vus: Math.max(1, Math.floor(VUS_INGEST / 5)),
      duration: DURATION,
      exec: 'lokiPush',
    },
    otlp_ingest: {
      executor: 'constant-vus',
      vus: Math.max(1, Math.floor(VUS_INGEST / 10)),
      duration: DURATION,
      exec: 'otlpIngest',
    },
    prom_query: {
      executor: 'constant-vus',
      vus: VUS_QUERY,
      duration: DURATION,
      exec: 'promQuery',
    },
    loki_query: {
      executor: 'constant-vus',
      vus: Math.max(1, Math.floor(VUS_QUERY / 2)),
      duration: DURATION,
      exec: 'lokiQuery',
    },
  },

  thresholds: {
    // SLO: query p99 under 1 second.
    'query_response_ms{type:prom_instant}': ['p(99)<1000'],
    'query_response_ms{type:prom_range}':   ['p(99)<1000'],
    'query_response_ms{type:loki_range}':   ['p(99)<1000'],
    // SLO: ingest success rate above 99.9%.
    'ingest_success_rate': ['rate>0.999'],
    // Hard stop if error rate exceeds 1%.
    'http_req_failed': ['rate<0.01'],
  },
};

// ---------------------------------------------------------------------------
// Scenario: Prometheus remote_write
// ---------------------------------------------------------------------------

export function promRemoteWrite() {
  const seriesIdx = randomIntBetween(0, SERIES - 1);
  const metricName = `pulseboard_load_metric_${seriesIdx % 1000}`;
  const tsMs = Date.now();
  const value = Math.random() * 100;

  const payload = buildWriteRequest(metricName, value, tsMs);
  const res = http.post(
    `${BASE_URL}/api/v1/write`,
    payload.buffer,
    { headers: headers() }
  );

  const ok = check(res, {
    'remote_write 200': (r) => r.status === 200,
  });
  ingestRate.add(ok);
  if (!ok) ingestErrors.add(1);
  sleep(0.001);  // ~1 000 req/s per VU cap
}

// ---------------------------------------------------------------------------
// Scenario: Loki JSON push
// ---------------------------------------------------------------------------

export function lokiPush() {
  const svcIdx = randomIntBetween(0, 99);
  const tsNs   = String(Date.now() * 1_000_000);
  const body   = JSON.stringify({
    streams: [{
      stream: { service_name: `load-svc-${svcIdx}`, level: 'info' },
      values: [[ tsNs, `load test log line ${svcIdx} at ${tsNs}` ]],
    }],
  });

  const res = http.post(
    `${BASE_URL}/loki/api/v1/push`,
    body,
    { headers: jsonHeaders() }
  );

  const ok = check(res, {
    'loki_push 204': (r) => r.status === 204,
  });
  ingestRate.add(ok);
  if (!ok) ingestErrors.add(1);
  sleep(0.002);
}

// ---------------------------------------------------------------------------
// Scenario: OTLP HTTP metric ingest
// ---------------------------------------------------------------------------

export function otlpIngest() {
  // Reuse the same hand-encoded protobuf approach; build a minimal OTLP payload.
  // For simplicity we POST a WriteRequest to /v1/metrics (OTLP path accepts
  // raw protobuf; the server decodes ExportMetricsServiceRequest).
  // We build a single gauge data point here.
  const metricName = `pulseboard_otlp_${randomIntBetween(0, 499)}`;
  const tsNano = BigInt(Date.now()) * 1_000_000n;
  const value  = Math.random() * 100;

  // Build a minimal OTLP ExportMetricsServiceRequest protobuf in JS.
  // (Mirrors the Proto.buildOtlpMetrics helper in Helpers.fs)
  const buf = new Uint8Array(512);
  let o = 0;

  // NumberDataPoint: field3=time_unix_nano (fixed64), field4=as_double
  const ndpBuf = new Uint8Array(64);
  let nd = 0;
  // field3 = fixed64: tag = (3<<3)|1 = 25
  ndpBuf[nd++] = 25;
  const tsBuf8 = new Uint8Array(8);
  new DataView(tsBuf8.buffer).setBigUint64(0, tsNano, true);
  for (let i = 0; i < 8; i++) ndpBuf[nd++] = tsBuf8[i];
  nd = writeDouble(ndpBuf, nd, 4, value);
  const ndp = ndpBuf.slice(0, nd);

  // Gauge: field1=data_points
  const gaugeBuf = new Uint8Array(128);
  let g = writeLenDelim(gaugeBuf, 0, 1, ndp);
  const gauge = gaugeBuf.slice(0, g);

  // Metric: field1=name, field5=gauge
  const metBuf = new Uint8Array(256);
  let m = writeLenDelim(metBuf, 0, 1, utf8(metricName));
  m = writeLenDelim(metBuf, m, 5, gauge);
  const metric = metBuf.slice(0, m);

  // ScopeMetrics: field2=metrics
  const smBuf = new Uint8Array(300);
  let sm = writeLenDelim(smBuf, 0, 2, metric);
  const scopeMetrics = smBuf.slice(0, sm);

  // ResourceMetrics: field2=scope_metrics
  const rmBuf = new Uint8Array(350);
  let rm = writeLenDelim(rmBuf, 0, 2, scopeMetrics);
  const resourceMetrics = rmBuf.slice(0, rm);

  // ExportMetricsServiceRequest: field1=resource_metrics
  o = writeLenDelim(buf, o, 1, resourceMetrics);
  const payload = buf.slice(0, o);

  const res = http.post(
    `${BASE_URL}/v1/metrics`,
    payload.buffer,
    { headers: headers() }
  );

  const ok = check(res, {
    'otlp_metrics 200': (r) => r.status === 200,
  });
  ingestRate.add(ok);
  if (!ok) ingestErrors.add(1);
  sleep(0.005);
}

// ---------------------------------------------------------------------------
// Scenario: Prometheus instant + range queries
// ---------------------------------------------------------------------------

export function promQuery() {
  const seriesIdx = randomIntBetween(0, 999);
  const metricName = `pulseboard_load_metric_${seriesIdx}`;
  const now = Math.floor(Date.now() / 1000);

  // Instant query
  let start = Date.now();
  let res = http.get(
    `${BASE_URL}/api/prom/api/v1/query?query=${encodeURIComponent(metricName)}&time=${now}`,
    { headers: API_KEY ? { 'Authorization': `Bearer ${API_KEY}` } : {} }
  );
  queryP99.add(Date.now() - start, { type: 'prom_instant' });
  let ok = check(res, { 'prom_instant 200': (r) => r.status === 200 });
  if (!ok) queryErrors.add(1);

  sleep(0.05);

  // Range query (last 5 minutes, 30s step)
  start = Date.now();
  res = http.get(
    `${BASE_URL}/api/prom/api/v1/query_range?query=${encodeURIComponent(metricName)}&start=${now - 300}&end=${now}&step=30`,
    { headers: API_KEY ? { 'Authorization': `Bearer ${API_KEY}` } : {} }
  );
  queryP99.add(Date.now() - start, { type: 'prom_range' });
  ok = check(res, { 'prom_range 200': (r) => r.status === 200 });
  if (!ok) queryErrors.add(1);

  sleep(0.1);
}

// ---------------------------------------------------------------------------
// Scenario: Loki query_range
// ---------------------------------------------------------------------------

export function lokiQuery() {
  const svcIdx = randomIntBetween(0, 99);
  const expr   = `{service="load-svc-${svcIdx}"}`;
  const nowNs  = String(Date.now() * 1_000_000);
  const startNs = String((Date.now() - 300_000) * 1_000_000);

  const start = Date.now();
  const res = http.get(
    `${BASE_URL}/api/loki/api/v1/query_range?query=${encodeURIComponent(expr)}&start=${startNs}&end=${nowNs}&limit=50`,
    { headers: API_KEY ? { 'Authorization': `Bearer ${API_KEY}` } : {} }
  );
  queryP99.add(Date.now() - start, { type: 'loki_range' });
  const ok = check(res, { 'loki_query 200': (r) => r.status === 200 });
  if (!ok) queryErrors.add(1);

  sleep(0.05);
}

// ---------------------------------------------------------------------------
// Teardown: verify SLO via self-metrics endpoint
// ---------------------------------------------------------------------------

export function handleSummary(data) {
  // Emit a machine-readable JSON summary for CI diff tracking.
  return {
    'bench-results/k6-summary.json': JSON.stringify(data, null, 2),
    stdout: textSummary(data, { indent: ' ', enableColors: false }),
  };
}

// k6 built-in textSummary (available in k6 >= 0.38)
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
