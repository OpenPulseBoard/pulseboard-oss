"use strict";

// =====================================================================
// Panel SDK — pluggable registry (Phase 12.1)
// =====================================================================
//
// Each entry describes one panel type that PulseBoard knows how to render.
//
//   PulseBoard.registerPanel({
//     type       : "heatmap",          // unique string key
//     label      : "Heatmap",          // shown in the editor <select>
//     queryShape : "matrix",           // scalar | vector | matrix | logs | spans | nodes | edges
//     render     : (el, frame, opts, p) => { /* draw into el; return optional cleanup fn */ },
//     editor     : (opts, onChange)    => { /* optional extra fields HTML string */ },
//   })
//
// Built-in panels (timeseries, stat, logs, table) are registered below
// before any user code runs.  Third-party panels can call registerPanel
// any time before the dashboard opens.
//
const PulseBoard = (() => {
  const _registry = new Map();

  function registerPanel(def) {
    if (!def || !def.type) throw new Error("registerPanel: `type` is required");
    _registry.set(def.type, def);
  }

  function getPanel(type) {
    return _registry.get(type);
  }

  function panelTypes() {
    return Array.from(_registry.values()).map(d => ({ type: d.type, label: d.label || d.type }));
  }

  return { registerPanel, getPanel, panelTypes };
})();

// =====================================================================
// State + utilities
// =====================================================================
const $ = (id) => document.getElementById(id);

// --- workspace tenant badge ------------------------------------------
// Asks the server which tenant the current bearer/session is bound to.
// Falls back to the hostname-derived slug (suffixed with "?") if the
// request fails — e.g. before sign-in or on a non-tenant host.
(async function setWorkspaceBadge() {
  const el    = document.getElementById("workspace-slug");
  const badge = document.getElementById("workspace-badge");
  if (!el) return;
  const render = (slug, host, confirmed) => {
    el.textContent = slug;
    if (badge) badge.title =
      (confirmed ? "Tenant: " : "Tenant (unconfirmed): ") + slug +
      (host ? " · host: " + host : "");
  };
  try {
    const tok = sessionStorage.getItem("pb.bearer");
    const headers = tok ? { "Authorization": "Bearer " + tok } : {};
    const r = await fetch("/auth/me", { headers, credentials: "same-origin" });
    if (r.ok) {
      const me = await r.json();
      if (me && me.slug) { render(me.slug, location.hostname, true); return; }
    }
  } catch { /* fall through to hostname-derived fallback */ }
  const host  = location.hostname || "";
  const parts = host.split(".");
  const slug  = (parts.length >= 3 && parts[0] !== "www") ? parts[0]
              : (host === "localhost" || /^\d+\.\d+\.\d+\.\d+$/.test(host)) ? "local"
              : host;
  render(slug + " ?", host, false);
})();
const fmtNum = (n) => {
  if (!Number.isFinite(n)) return String(n);
  const a = Math.abs(n);
  if (a >= 1e9) return (n/1e9).toFixed(2) + "G";
  if (a >= 1e6) return (n/1e6).toFixed(2) + "M";
  if (a >= 1e3) return (n/1e3).toFixed(2) + "k";
  if (a >= 100) return n.toFixed(0);
  if (a >= 1)   return n.toFixed(2);
  return n.toFixed(3);
};
const fmtTs = (ms) => new Date(ms).toISOString().substr(11, 8);

const state = {
  dashboards: [],          // [{id,title,...}]
  current: null,           // active Dashboard (full)
  editMode: false,
  editingPanel: null,      // id of panel in editor
  panels: new Map(),       // panelId -> { dom, uplot, data, raf }
  refreshTimer: null,
  ws: null,
  compareMode:   false,    // 12.3 — compare-time overlay
  compareOffset: 7 * 86400, // seconds (default: 7 days)
  liveMode:      false,    // 12.3 — WS-driven sub-second refresh
  history:       [],       // 12.3 — [{ts, title, snap}] ring buffer
};

function showView(name) {
  $("view-dashboards").classList.toggle("hidden", name !== "dashboards");
  $("view-explore").classList.toggle("hidden",    name !== "explore");
  $("view-traces").classList.toggle("hidden",     name !== "traces");
  $("view-map").classList.toggle("hidden",        name !== "map");
  $("view-library").classList.toggle("hidden",    name !== "library");
  $("view-alerts").classList.toggle("hidden",     name !== "alerts");
  $("view-agents").classList.toggle("hidden",     name !== "agents");
  $("tab-dashboards").classList.toggle("active", name === "dashboards");
  $("tab-explore").classList.toggle("active",    name === "explore");
  $("tab-traces").classList.toggle("active",     name === "traces");
  $("tab-map").classList.toggle("active",        name === "map");
  $("tab-library").classList.toggle("active",    name === "library");
  $("tab-alerts").classList.toggle("active",     name === "alerts");
  $("tab-agents").classList.toggle("active",     name === "agents");
  if (name === "traces")  loadTraces();
  if (name === "map")     loadServiceMap();
  if (name === "library") renderLibrary(_libCatFilter, $("lib-search").value);
  if (name === "alerts")  loadRules();
  if (name === "agents")  loadAgents();
}

function uuid() {
  return Math.random().toString(36).slice(2) + Date.now().toString(36);
}

// =====================================================================
// Query layer — one place that knows how to fetch any panel/explore source
// =====================================================================
async function runQuery(lang, expr, startMs, endMs, stepSec) {
  expr = applyVars(expr);  // substitute template variables before sending
  const startSec = Math.floor(startMs / 1000);
  const endSec   = Math.floor(endMs / 1000);
  if (lang === "promql") {
    const url = `/api/prom/api/v1/query_range?query=${encodeURIComponent(expr)}` +
                `&start=${startSec}&end=${endSec}&step=${stepSec}`;
    const r = await authFetch(url);
    const j = await r.json();
    if (j.status !== "success") throw new Error(j.error || "promql failed");
    return promMatrixToSeries(j.data);
  }
  if (lang === "logql") {
    const url = `/api/loki/api/v1/query_range?query=${encodeURIComponent(expr)}` +
                `&start=${startMs*1e6}&end=${endMs*1e6}&limit=500`;
    const r = await authFetch(url);
    const j = await r.json();
    if (j.status !== "success") throw new Error(j.error || "logql failed");
    return logqlToEntries(j.data);
  }
  if (lang === "native") {
    // Single metric name → /api/metrics/<n>?sinceMs=...
    if (expr.startsWith("__")) {
      // Synthetic sources used by the default dashboard's stat panels.
      return await nativeSynthetic(expr);
    }
    const url = `/api/metrics/${encodeURIComponent(expr.trim())}?sinceMs=${startMs}`;
    const r = await authFetch(url);
    if (!r.ok) throw new Error("native metric not found");
    const arr = await r.json();   // [[ts, value], ...]
    const xs = arr.map(p => p[0] / 1000);
    const ys = arr.map(p => p[1]);
    return { kind: "series", series: [{ name: expr, xs, ys }] };
  }
  throw new Error("unknown query language: " + lang);
}

function promMatrixToSeries(data) {
  const out = [];
  if (data.resultType === "matrix") {
    for (const s of data.result) {
      const name = labelsLine(s.metric);
      const xs = s.values.map(v => +v[0]);
      const ys = s.values.map(v => +v[1]);
      out.push({ name, xs, ys });
    }
    return { kind: "series", series: out };
  }
  if (data.resultType === "vector") {
    for (const s of data.result) {
      out.push({ name: labelsLine(s.metric), xs: [+s.value[0]], ys: [+s.value[1]] });
    }
    return { kind: "series", series: out };
  }
  return { kind: "series", series: [] };
}

function labelsLine(metric) {
  if (!metric) return "{}";
  const name = metric.__name__ || "";
  const kvs  = Object.entries(metric).filter(([k]) => k !== "__name__")
                     .map(([k,v]) => `${k}="${v}"`).join(",");
  return kvs ? `${name}{${kvs}}` : (name || "{}");
}

function logqlToEntries(data) {
  const entries = [];
  if (data.resultType === "streams") {
    for (const s of data.result) {
      const lbls = labelsLine(s.stream);
      for (const v of s.values) {
        entries.push({
          tsMs: Math.floor(+v[0] / 1e6),
          stream: lbls,
          service: s.stream.service || "-",
          level: s.stream.level || "info",
          message: v[1],
        });
      }
    }
    entries.sort((a, b) => a.tsMs - b.tsMs);
  }
  return { kind: "logs", entries };
}

async function nativeSynthetic(key) {
  // Synthetic sources for the default dashboard. Each returns a stat-ready
  // single value rather than a series.
  if (key === "__alerts.firing") {
    // No /api/alerts endpoint yet — derive from WS counters we keep client-side.
    return { kind: "stat", value: state.liveAlerts || 0 };
  }
  if (key === "__listeners.up") {
    // No /api/listeners count in single-tenant mode — fall back to WS metric count.
    return { kind: "stat", value: state.liveMetrics ? state.liveMetrics.size : 0 };
  }
  return { kind: "stat", value: 0 };
}

// =====================================================================
// Panel rendering
// =====================================================================
function placePanel(el, p) {
  el.style.gridColumn = `${(p.x|0) + 1} / span ${Math.max(1, p.w|0)}`;
  el.style.gridRow    = `${(p.y|0) + 1} / span ${Math.max(1, p.h|0)}`;
}

function panelChrome(p) {
  const el = document.createElement("div");
  el.className = "panel"; el.dataset.id = p.id;
  el.innerHTML = `
    <header>
      <span class="title"></span>
      <span class="badge"></span>
      <span class="actions">
        <button data-act="edit">edit</button>
      </span>
    </header>
    <div class="body"></div>
    <div class="resize"></div>`;
  el.querySelector(".title").textContent = p.title || "(untitled)";
  el.querySelector(".badge").textContent = p.queryLang;
  placePanel(el, p);
  el.querySelector('[data-act="edit"]').addEventListener("click", (e) => {
    e.stopPropagation();
    openEditor(p.id);
  });
  // Drilldown links: Cmd+click (Mac) or Ctrl+click anywhere on the panel body.
  const links = p.links || [];
  if (links.length) {
    el.classList.add("has-links");
    el.querySelector(".body").addEventListener("click", (ev) => {
      if (!state.editMode && (ev.metaKey || ev.ctrlKey)) {
        ev.preventDefault();
        const url = resolveLink(links[0].url, "", null);
        if (links[0].newTab !== false) window.open(url, "_blank", "noopener,noreferrer");
        else location.href = url;
      }
    });
  }
  // Right-click → "show logs for this spike" / "show metrics for this service".
  wirePanelCorrelation(el, p);
  return el;
}

async function renderPanel(p) {
  const cached = state.panels.get(p.id);
  let el;
  let isNewPanel = false;
  if (cached && cached.dom && cached.dom.isConnected) {
    el = cached.dom;
    placePanel(el, p);
    el.querySelector(".title").textContent = p.title || "(untitled)";
    el.querySelector(".badge").textContent = p.queryLang;
  } else {
    el = panelChrome(p);
    $("dash-grid").appendChild(el);
    isNewPanel = true;
  }
  const body = el.querySelector(".body");
  // Keep any existing chart visible across refreshes; only swap the
  // body to a "loading…" placeholder on first render or when the panel
  // type changes (in which case the old contents are no longer valid).
  const prevType  = body.dataset.panelType;
  const typeChanged = !!prevType && prevType !== p.type;
  body.dataset.panelType = p.type;
  body.className = "body " + p.type;

  const prev = state.panels.get(p.id) || {};
  if (typeChanged && prev.uplot) {
    try { prev.uplot.destroy(); } catch {}
    prev.uplot = null;
  }
  const hasLivePlot = !!prev.uplot;
  const isEmpty = !hasLivePlot && (!body.firstChild || !!body.querySelector(".empty, .err"));
  if (isNewPanel || typeChanged || isEmpty) {
    body.innerHTML = '<div class="empty">loading…</div>';
  }
  state.panels.set(p.id, { dom: el, uplot: prev.uplot || null });

  const now = Date.now();
  const start = now - state.current.timeRangeSec * 1000;
  const step  = Math.max(15, Math.floor(state.current.timeRangeSec / 120));
  let result;
  try {
    result = await runQuery(p.queryLang, p.expr, start, now, step);
    // Compare-time overlay: run the same query shifted backwards by compareOffset.
    if (state.compareMode && !p.expr.startsWith("__")) {
      try {
        const off = state.compareOffset * 1000;
        const cr  = await runQuery(p.queryLang, p.expr, start - off, now - off, step);
        const lbl = formatCompareOffset(state.compareOffset);
        if (cr.kind === "series" && cr.series.length && result.series) {
          for (const s of cr.series) s.name += ` (${lbl})`;
          result = { ...result, series: [...result.series, ...cr.series] };
        } else if (cr.kind === "stat" && result.kind === "stat") {
          result = { ...result, _compareStat: cr.value, _compareLabel: lbl };
        }
      } catch { /* compare is best-effort */ }
    }
  } catch (err) {
    const cur = state.panels.get(p.id);
    if (cur && cur.uplot) { try { cur.uplot.destroy(); } catch {} }
    body.innerHTML = "";
    const e = document.createElement("div");
    e.className = "err"; e.textContent = err.message;
    body.appendChild(e);
    state.panels.set(p.id, { dom: el });
    return;
  }

  const def = PulseBoard.getPanel(p.type);
  if (def) {
    // Renderers are responsible for clearing/reusing the body so that
    // chart instances can be mutated in place (no destroy/recreate
    // flash on every refresh tick).
    def.render(body, result, p.options || {}, p);
  } else {
    const cur = state.panels.get(p.id);
    if (cur && cur.uplot) { try { cur.uplot.destroy(); } catch {} }
    // Unknown panel type — show a clear message rather than silently nothing.
    body.className = "body";
    const d = document.createElement("div");
    d.className = "err";
    d.textContent = `Unknown panel type: "${p.type}". Has the panel plugin been loaded?`;
    body.appendChild(d);
  }
}

// =====================================================================
// Plot helpers — unit formatting + uPlot series builder
// =====================================================================

// Panel types that use uPlot and show the structured Display-options section.
const PLOT_PANEL_TYPES = new Set(["timeseries", "trend", "xychart"]);

// Returns a value-formatter function for the given unit key.
function makeUnitFmt(unit) {
  const TIERED = {
    bytes:    [[1 << 30, "GiB"], [1 << 20, "MiB"], [1 << 10, "KiB"], [1, "B"]],
    bytes_si: [[1e9, "GB"], [1e6, "MB"], [1e3, "KB"], [1, "B"]],
    bits:     [[1e9, "Gb"], [1e6, "Mb"], [1e3, "Kb"], [1, "b"]],
    bps:      [[1e9, "GBps"], [1e6, "MBps"], [1e3, "KBps"], [1, "Bps"]],
  };
  const SUFFIX = {
    ns: " ns", us: " µs", ms: " ms", s: " s",
    pps: " p/s", ops: " ops/s",
    percent: "%", rpm: " rpm",
    "m/s": " m/s", "km/h": " km/h", mph: " mph",
    "m/s2": " m/s²", deg: "°", rad: " rad",
  };
  if (TIERED[unit]) {
    const tiers = TIERED[unit];
    return (v) => {
      if (!Number.isFinite(v)) return String(v);
      const a = Math.abs(v);
      for (const [thresh, label] of tiers) {
        if (a >= thresh) return (v / thresh).toFixed(2) + " " + label;
      }
      return v.toFixed(0) + " " + tiers[tiers.length - 1][1];
    };
  }
  if (SUFFIX[unit]) {
    const sfx = SUFFIX[unit];
    return (v) => fmtNum(v) + sfx;
  }
  // Custom or empty: use generic formatter with optional suffix.
  return (v) => fmtNum(v) + (unit ? (" " + unit) : "");
}

// Parse a hex color string (3 or 6 chars) and add alpha → "rgba(r,g,b,a)".
function hexToRgba(hex, alpha) {
  const h = hex.replace("#", "");
  const r = parseInt(h.length === 3 ? h[0] + h[0] : h.slice(0, 2), 16);
  const g = parseInt(h.length === 3 ? h[1] + h[1] : h.slice(2, 4), 16);
  const b = parseInt(h.length === 3 ? h[2] + h[2] : h.slice(4, 6), 16);
  return `rgba(${r},${g},${b},${alpha})`;
}

// Build the uPlot series-definition array from panel options.
// seriesNames: string[]  (one per data series, excluding x)
// xs: number[]           (x-axis data, used for default point visibility)
// opts: p.options object
function buildSeriesDefs(seriesNames, xs, opts) {
  const style     = opts.style      || "lines";
  const interp    = opts.interpolation || "linear";
  const lineStyle = opts.lineStyle  || "solid";
  const lineWidth = opts.lineWidth  ? +opts.lineWidth : 1.5;
  const fillAlpha = opts.fill       ? +opts.fill : 0;
  // Colors: prefer per-series keys (color0, color1, …) then fall back to
  // the legacy comma-sep `colors` key, then the palette.
  const legacyColors = (opts.colors || "").split(",").map(c => c.trim()).filter(Boolean);
  const colorOf = (i) => {
    const perSeries = opts["color" + i];
    if (perSeries && perSeries.startsWith("#")) return perSeries;
    return legacyColors[i] || colorFor(i);
  };

  // Determine the uPlot paths function.
  let pathsFn = null;
  if (style === "bars" && typeof uPlot.bars === "function") {
    pathsFn = uPlot.bars({ size: [0.6, 100], gap: 1 });
  } else if (style !== "bars") {
    if (interp === "smooth" && typeof uPlot.spline === "function") {
      pathsFn = uPlot.spline();
    } else if (interp === "stepBefore" && typeof uPlot.stepped === "function") {
      pathsFn = uPlot.stepped({ align: -1 });
    } else if (interp === "stepAfter" && typeof uPlot.stepped === "function") {
      pathsFn = uPlot.stepped({ align: 1 });
    }
  }

  // Dash array for line style.
  const dashArr = lineStyle === "dash" ? [8, 4]
                : lineStyle === "dots" ? [2, 4]
                : undefined;

  const showPoints = style === "points" || (style === "lines" && xs.length < 60);

  return seriesNames.map((name, i) => {
    const stroke = colorOf(i);
    const def = {
      label:  name,
      stroke,
      width:  lineWidth,
      points: { show: showPoints, size: style === "points" ? 5 : 3 },
    };
    if (pathsFn) def.paths = pathsFn;
    if (dashArr) def.dash  = dashArr;
    if (fillAlpha > 0) {
      try { def.fill = hexToRgba(stroke, fillAlpha); } catch { /* ignore invalid hex */ }
    }
    return def;
  });
}

// Build a spec-key string that captures everything that would require a
// uPlot destroy+recreate rather than a simple setData call.
// uPlot plugin — floating tooltip showing values at the cursor x position.
function tooltipPlugin(seriesNames, unitFmt, isTime) {
  let tip;

  function hideTip() { if (tip) tip.style.display = "none"; }

  function updateTip(u) {
    const { left, top, idx } = u.cursor;
    if (idx == null) { hideTip(); return; }
    const xVal = u.data[0][idx];
    if (xVal == null) { hideTip(); return; }
    const hdr = isTime
      ? new Date(xVal * 1000).toLocaleString([], {
          month: "short", day: "numeric",
          hour: "2-digit", minute: "2-digit", second: "2-digit" })
      : fmtNum(xVal);
    let html = `<div class="pt-time">${escapeHtml(hdr)}</div>`;
    for (let i = 0; i < seriesNames.length; i++) {
      const si = i + 1;
      const sdef = u.series[si];
      // _show is the internal boolean uPlot uses; show may be wrapped in a fn
      if (!sdef || sdef._show === false || sdef.show === false) continue;
      const v = u.data[si] ? u.data[si][idx] : null;
      if (v == null) continue;
      const stroke = typeof sdef.stroke === "function"
        ? sdef.stroke(u, si) : (sdef.stroke || "#8a93a1");
      html += `<div class="pt-row">` +
        `<span class="pt-dot" style="background:${escapeHtml(stroke)}"></span>` +
        `<span class="pt-name">${escapeHtml(seriesNames[i])}</span>` +
        `<span class="pt-val">${escapeHtml(unitFmt(v))}</span></div>`;
    }
    // Measure tip while visible-but-transparent so offsetWidth is real.
    // Must use "block" (not "") because the CSS class has display:none —
    // clearing the inline style would just let the class rule win again.
    tip.innerHTML = html;
    tip.style.visibility = "hidden";
    tip.style.display = "block";
    const ow = u.over.offsetWidth;
    const oh = u.over.offsetHeight;
    const tw = tip.offsetWidth;
    const th = tip.offsetHeight;
    tip.style.visibility = "";
    const x = (left + 14 + tw > ow) ? left - tw - 8 : left + 14;
    const y = Math.max(4, Math.min(oh - th - 4, top - th / 2));
    tip.style.left = x + "px";
    tip.style.top  = y + "px";
  }

  return {
    hooks: {
      init(u) {
        tip = document.createElement("div");
        tip.className = "plot-tip";
        u.over.appendChild(tip);
        u.over.addEventListener("mouseleave", hideTip);
      },
      setCursor: updateTip,
    },
  };
}

function plotSpecKey(seriesNames, opts) {
  const ks = ["style","interpolation","lineStyle","lineWidth","fill","colors","legend","yMin","yMax"];
  const perColorKeys = seriesNames.map((_, i) => opts["color" + i] || "").join(",");
  return seriesNames.join("|") + "/" + ks.map(k => opts[k] || "").join(",") + "/" + perColorKeys;
}

function renderTimeseries(body, result, p) {
  const series  = result.series || [];
  const popts   = p.options || {};
  const cached  = state.panels.get(p.id) || {};
  if (series.length === 0) {
    if (cached.uplot) { try { cached.uplot.destroy(); } catch {} state.panels.set(p.id, { dom: cached.dom }); }
    body.innerHTML = '<div class="empty">no data in range</div>'; return;
  }
  // Build common x-axis from union of all xs.
  const xset = new Set();
  for (const s of series) for (const x of s.xs) xset.add(x);
  const xs = Array.from(xset).sort((a, b) => a - b);
  const xi = new Map(); xs.forEach((x, i) => xi.set(x, i));
  const data = [xs];
  for (const s of series) {
    const arr = new Array(xs.length).fill(null);
    for (let i = 0; i < s.xs.length; i++) {
      const idx = xi.get(s.xs[i]); if (idx != null) arr[idx] = s.ys[i];
    }
    data.push(arr);
  }
  // Reuse the existing uPlot if its spec still matches — mutating data in
  // place lets uPlot animate refreshes smoothly without a destroy/recreate flash.
  const specKey = plotSpecKey(series.map(s => s.name), popts);
  const existing = cached.uplot;
  const sameSchema = existing && existing._specKey === specKey && body.contains(existing.root);
  if (sameSchema) {
    try {
      existing.setData(data);
      if (existing.width !== body.clientWidth || existing.height !== body.clientHeight) {
        existing.setSize({ width: body.clientWidth, height: body.clientHeight });
      }
      return;
    } catch { /* fall through to recreate */ }
  }
  if (existing) { try { existing.destroy(); } catch {} }
  body.innerHTML = "";

  const legendOpt = popts.legend || "";
  const showLegend = legendOpt === "show" ? true
                   : legendOpt === "hide" ? false
                   : series.length > 1;
  const unitFmt   = makeUnitFmt(popts.unit || "");
  const yMin      = popts.yMin !== "" && popts.yMin != null ? +popts.yMin : undefined;
  const yMax      = popts.yMax !== "" && popts.yMax != null ? +popts.yMax : undefined;
  const seriesDefs = buildSeriesDefs(series.map(s => s.name), xs, popts);

  const uopts = {
    width:  body.clientWidth  || 300,
    height: body.clientHeight || 100,
    cursor: { drag: { x: true, y: false } },
    legend: { show: showLegend },
    scales: {
      x: { time: true },
      y: { min: yMin, max: yMax },
    },
    axes: [
      { stroke: "#8a93a1", grid: { stroke: "#232833" } },
      { stroke: "#8a93a1", grid: { stroke: "#232833" }, size: makeYAxisSize(unitFmt),
        values: (u, vals) => vals.map(v => unitFmt(v)) },
    ],
    plugins: [tooltipPlugin(series.map(s => s.name), unitFmt, true)],
    series: [{}, ...seriesDefs],
  };
  const plot = new uPlot(uopts, data, body);
  plot._specKey = specKey;
  state.panels.set(p.id, { dom: body.closest(".panel"), uplot: plot });
  // ResizeObserver keeps the chart in sync as the user drags the grid.
  if (!body._ro) {
    body._ro = new ResizeObserver(() => {
      if (state.panels.get(p.id)?.uplot) {
        try { state.panels.get(p.id).uplot.setSize({ width: body.clientWidth, height: body.clientHeight }); } catch {}
      }
    });
    body._ro.observe(body);
  }
}

function renderStat(body, result, p) {
  let value;
  if (result.kind === "stat") value = result.value;
  else if (result.kind === "series" && result.series.length) {
    const last = result.series[0];
    value = last.ys.length ? last.ys[last.ys.length - 1] : null;
  } else value = null;
  const unit = (p.options || {}).unit || "";
  let delta = "";
  if (result._compareStat != null && value != null && result._compareStat !== 0) {
    const pct = ((value - result._compareStat) / Math.abs(result._compareStat)) * 100;
    const sign = pct >= 0 ? "+" : "";
    const cls  = pct >= 0 ? "color:#5cd97a" : "color:#f25f5c";
    delta = `<div style="font-size:11px;${cls};margin-top:2px;">${sign}${pct.toFixed(1)}% vs ${result._compareLabel || "prev"}</div>`;
  }
  body.innerHTML = `<div class="v">${value == null ? "—" : fmtNum(value)}</div><div class="u">${unit}</div>${delta}`;
}

function renderLogs(body, result, p) {
  const entries = (result.entries || []).slice(-((p.options && +p.options.tail) || 200));
  if (entries.length === 0) { body.innerHTML = '<div class="empty">no log lines</div>'; return; }
  const ul = document.createElement("ul"); ul.style.margin = "0"; ul.style.padding = "0";
  for (const e of entries) {
    const li = document.createElement("li");
    li.className = "level-" + (e.level || "info").toLowerCase();
    li.innerHTML = `<span class="ts">${fmtTs(e.tsMs)}</span>` +
                   `<span class="svc">${e.service}</span>` +
                   `<span></span>`;
    li.querySelector("span:last-child").textContent = e.message;
    ul.appendChild(li);
  }
  body.appendChild(ul);
  body.scrollTop = body.scrollHeight;
}

function renderTable(body, result, p) {
  const series = result.series || [];
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }
  const table = document.createElement("table");
  const head  = document.createElement("tr");
  head.innerHTML = "<th>series</th><th>last</th><th>min</th><th>max</th><th>avg</th>";
  table.appendChild(head);
  for (const s of series) {
    const tr = document.createElement("tr");
    const last = s.ys[s.ys.length - 1];
    const min  = Math.min(...s.ys);
    const max  = Math.max(...s.ys);
    const avg  = s.ys.reduce((a, b) => a + b, 0) / (s.ys.length || 1);
    tr.innerHTML = `<td></td><td>${fmtNum(last)}</td><td>${fmtNum(min)}</td>` +
                   `<td>${fmtNum(max)}</td><td>${fmtNum(avg)}</td>`;
    tr.firstChild.textContent = s.name;
    table.appendChild(tr);
  }
  body.appendChild(table);
}

const PALETTE = ["#5ec8ff","#b388ff","#5cd97a","#f0c452","#f25f5c","#79e0c8","#ff9d6c","#d8a0ff"];
const colorFor = (i) => PALETTE[i % PALETTE.length];

// =====================================================================
// Wave A render helpers (Phase 12.2)
// =====================================================================

// ── Bar gauge ─────────────────────────────────────────────────────────
// opts: unit, min (default 0), max (default auto), orientation (h|v)
function renderBarGauge(body, result, p) {
  const opts   = p.options || {};
  const unit   = opts.unit  || "";
  const series = (result.series || []).filter(s => s.ys.length);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }
  const vals   = series.map(s => s.ys[s.ys.length - 1]);
  const min    = +opts.min  || 0;
  const max    = +opts.max  || Math.max(...vals.map(Math.abs)) * 1.15 || 1;
  const rows   = series.map((s, i) => {
    const v    = vals[i];
    const pct  = Math.min(100, Math.max(0, ((v - min) / (max - min)) * 100));
    const col  = colorFor(i);
    return `<div class="bg-row">
      <div class="bg-label" title="${escapeHtml(s.name)}">${escapeHtml(s.name)}</div>
      <div class="bg-track"><div class="bg-fill" style="width:${pct}%;background:${col};"></div></div>
      <div class="bg-val" style="color:${col};">${fmtNum(v)}${escapeHtml(unit)}</div>
    </div>`;
  });
  body.innerHTML = rows.join("");
}

// ── Gauge (radial / arc) ──────────────────────────────────────────────
// opts: unit, min, max, thresholds (e.g. "80=warn,90=err")
function renderGauge(body, result, p) {
  const opts   = p.options || {};
  const unit   = opts.unit  || "";
  const series = result.kind === "stat"
    ? [{ name: "", ys: [result.value] }]
    : (result.series || []).filter(s => s.ys.length);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  // Render one arc per series; in practice dashboards usually put one per gauge panel.
  const svgW = 200, svgH = 120;
  const cx = svgW / 2, cy = svgH - 14;
  const R = 80, strokeW = 16;
  const min = +opts.min || 0;
  const max = +opts.max || 100;

  // Parse threshold string "80=warn,90=err" → [{at, color}]
  const thresholds = [];
  for (const tok of (opts.thresholds || "").split(",")) {
    const [at, name] = tok.split("=");
    if (!isNaN(+at)) thresholds.push({ at: +at, color: name === "err" ? "var(--err)" : "var(--warn)" });
  }
  thresholds.sort((a, b) => a.at - b.at);

  function arcColor(v) {
    let c = "var(--ok)";
    for (const t of thresholds) if (v >= t.at) c = t.color;
    return c;
  }

  function polarXY(angleDeg, r) {
    const a = (angleDeg - 180) * Math.PI / 180;
    return [cx + r * Math.cos(a), cy + r * Math.sin(a)];
  }
  function arcPath(startAngle, endAngle, r) {
    const [x1, y1] = polarXY(startAngle, r);
    const [x2, y2] = polarXY(endAngle,   r);
    const large     = Math.abs(endAngle - startAngle) > 180 ? 1 : 0;
    return `M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2}`;
  }

  const parts = [];
  // Track (background arc 0°→180°)
  parts.push(`<path d="${arcPath(0, 180, R)}" fill="none"
    stroke="var(--bg)" stroke-width="${strokeW}" stroke-linecap="round"/>`);

  for (let i = 0; i < series.length; i++) {
    const v    = series[i].ys[series[i].ys.length - 1];
    const pct  = Math.min(1, Math.max(0, (v - min) / (max - min)));
    const end  = pct * 180;
    const col  = arcColor(v);
    if (end > 0) {
      parts.push(`<path d="${arcPath(0, end, R)}" fill="none"
        stroke="${col}" stroke-width="${strokeW}" stroke-linecap="round"/>`);
    }
    parts.push(`<text x="${cx}" y="${cy - 6}" text-anchor="middle"
      font-size="26" font-weight="bold" fill="${col}">${fmtNum(v)}</text>`);
    parts.push(`<text x="${cx}" y="${cy + 14}" text-anchor="middle"
      font-size="11" fill="var(--muted)">${escapeHtml(unit)}</text>`);
  }
  // Min / max labels
  const [lx, ly] = polarXY(0,   R + strokeW + 6);
  const [rx, ry] = polarXY(180, R + strokeW + 6);
  parts.push(`<text x="${lx}" y="${ly + 4}" text-anchor="middle" font-size="10"
    fill="var(--muted)">${fmtNum(min)}</text>`);
  parts.push(`<text x="${rx}" y="${ry + 4}" text-anchor="middle" font-size="10"
    fill="var(--muted)">${fmtNum(max)}</text>`);

  body.innerHTML =
    `<svg viewBox="0 0 ${svgW} ${svgH}" xmlns="http://www.w3.org/2000/svg">${parts.join("")}</svg>`;
}

// ── Pie / Donut ────────────────────────────────────────────────────────
// opts: donut (boolean-ish), unit
function renderPieChart(body, result, p) {
  const opts   = p.options || {};
  const donut  = opts.donut === "true" || opts.donut === true;
  const unit   = opts.unit  || "";
  const series = (result.series || []).filter(s => s.ys.length);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  const vals  = series.map(s => Math.abs(s.ys[s.ys.length - 1]));
  const total = vals.reduce((a, b) => a + b, 0) || 1;

  const svgSize = 160, cx = svgSize / 2, cy = svgSize / 2, R = 70;
  const innerR  = donut ? 38 : 0;
  let angle     = -Math.PI / 2;
  const slices  = [];
  for (let i = 0; i < series.length; i++) {
    const sweep = (vals[i] / total) * Math.PI * 2;
    const x1 = cx + R * Math.cos(angle);
    const y1 = cy + R * Math.sin(angle);
    angle    += sweep;
    const x2 = cx + R * Math.cos(angle);
    const y2 = cy + R * Math.sin(angle);
    const large = sweep > Math.PI ? 1 : 0;
    const col   = colorFor(i);
    let d;
    if (innerR > 0) {
      const ix1 = cx + innerR * Math.cos(angle - sweep);
      const iy1 = cy + innerR * Math.sin(angle - sweep);
      const ix2 = cx + innerR * Math.cos(angle);
      const iy2 = cy + innerR * Math.sin(angle);
      d = `M ${x1} ${y1} A ${R} ${R} 0 ${large} 1 ${x2} ${y2}
           L ${ix2} ${iy2} A ${innerR} ${innerR} 0 ${large} 0 ${ix1} ${iy1} Z`;
    } else {
      d = `M ${cx} ${cy} L ${x1} ${y1} A ${R} ${R} 0 ${large} 1 ${x2} ${y2} Z`;
    }
    const pct = ((vals[i] / total) * 100).toFixed(1);
    slices.push({ d, col, pct, name: series[i].name, val: vals[i] });
  }

  const paths = slices.map(s =>
    `<path d="${s.d}" fill="${s.col}" stroke="var(--bg)" stroke-width="1.5">
       <title>${escapeHtml(s.name)}: ${fmtNum(s.val)}${escapeHtml(unit)} (${s.pct}%)</title>
     </path>`).join("");
  const legendItems = slices.map(s =>
    `<div class="pie-legend-item">
       <div class="pie-swatch" style="background:${s.col};"></div>
       <span>${escapeHtml(s.name.length > 22 ? s.name.slice(0,21) + "…" : s.name)}</span>
     </div>`).join("");

  body.innerHTML =
    `<svg viewBox="0 0 ${svgSize} ${svgSize}" xmlns="http://www.w3.org/2000/svg"
         style="overflow:visible;">${paths}</svg>` +
    `<div class="pie-legend">${legendItems}</div>`;
}

// ── Bar chart (categorical) ───────────────────────────────────────────
// Uses uPlot in bar mode.  opts: unit, stacked
function renderBarChart(body, result, p) {
  const series = (result.series || []);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }
  // Treat last-value of each series as one categorical bar.
  const labels = series.map(s => s.name);
  const vals   = series.map(s => s.ys.length ? s.ys[s.ys.length - 1] : 0);
  const svgW   = body.clientWidth  || 300;
  const svgH   = body.clientHeight || 120;
  const barW   = Math.max(4, Math.floor((svgW - 30) / series.length) - 4);
  const maxV   = Math.max(...vals.map(Math.abs), 1);
  const parts  = [];
  const scaleH = svgH - 28;
  vals.forEach((v, i) => {
    const h   = Math.max(1, Math.abs(v) / maxV * scaleH);
    const x   = 10 + i * ((svgW - 20) / series.length) + ((svgW - 20) / series.length - barW) / 2;
    const y   = svgH - 20 - h;
    const col = colorFor(i);
    const lbl = labels[i].length > 8 ? labels[i].slice(0, 7) + "…" : labels[i];
    parts.push(`<rect x="${x.toFixed(1)}" y="${y.toFixed(1)}"
      width="${barW}" height="${h.toFixed(1)}"
      fill="${col}" rx="2">
      <title>${escapeHtml(labels[i])}: ${fmtNum(v)}</title></rect>`);
    parts.push(`<text x="${(x + barW/2).toFixed(1)}" y="${(svgH - 4)}"
      text-anchor="middle" font-size="9" fill="var(--muted)">${escapeHtml(lbl)}</text>`);
    parts.push(`<text x="${(x + barW/2).toFixed(1)}" y="${(y - 3).toFixed(1)}"
      text-anchor="middle" font-size="9" fill="${col}">${fmtNum(v)}</text>`);
  });
  body.innerHTML =
    `<svg viewBox="0 0 ${svgW} ${svgH}" xmlns="http://www.w3.org/2000/svg"
         style="width:100%;height:100%;">${parts.join("")}</svg>`;
  // Redraw on resize.
  if (!body._bro) {
    body._bro = new ResizeObserver(() => {
      if (body.isConnected) renderBarChart(body, result, p);
    });
    body._bro.observe(body);
  }
}

// ── Histogram ─────────────────────────────────────────────────────────
// Accepts a matrix result: treats each series as one bucket whose name
// contains the upper bound as a number, matching Prometheus histogram
// label format (le="…"). opts: unit
function renderHistogram(body, result, p) {
  const opts   = p.options || {};
  const unit   = opts.unit  || "";
  const series = (result.series || []).filter(s => s.ys.length);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  // Extract le="<value>" from series names or fall back to index.
  const buckets = series.map((s, i) => {
    const m   = s.name.match(/le="([^"]+)"/);
    const le  = m ? (m[1] === "+Inf" ? Infinity : +m[1]) : i;
    const cnt = s.ys[s.ys.length - 1] || 0;
    return { le, cnt, name: s.name };
  }).filter(b => isFinite(b.le)).sort((a, b) => a.le - b.le);

  // Convert cumulative counts to bucket heights.
  const heights = buckets.map((b, i) => Math.max(0, b.cnt - (i > 0 ? buckets[i-1].cnt : 0)));
  const maxH    = Math.max(...heights, 1);
  const svgW    = body.clientWidth  || 300;
  const svgH    = body.clientHeight || 120;
  const scaleH  = svgH - 28;
  const barW    = Math.max(2, (svgW - 20) / buckets.length - 2);
  const parts   = [];
  buckets.forEach((b, i) => {
    const h   = heights[i] / maxH * scaleH;
    const x   = 10 + i * ((svgW - 20) / buckets.length);
    const y   = svgH - 20 - h;
    const lbl = b.le >= 1e9 ? "∞" : fmtNum(b.le);
    parts.push(`<rect x="${x.toFixed(1)}" y="${y.toFixed(1)}"
      width="${barW}" height="${Math.max(1,h).toFixed(1)}"
      fill="var(--accent)" rx="1" opacity="0.85">
      <title>≤${lbl}${escapeHtml(unit)}: ${fmtNum(heights[i])}</title></rect>`);
    if (i % Math.max(1, Math.floor(buckets.length / 8)) === 0) {
      parts.push(`<text x="${(x + barW/2).toFixed(1)}" y="${(svgH - 4)}"
        text-anchor="middle" font-size="9" fill="var(--muted)">${escapeHtml(lbl)}</text>`);
    }
  });
  body.innerHTML =
    `<svg viewBox="0 0 ${svgW} ${svgH}" xmlns="http://www.w3.org/2000/svg"
         style="width:100%;height:100%;">${parts.join("")}</svg>`;
  if (!body._hro) {
    body._hro = new ResizeObserver(() => { if (body.isConnected) renderHistogram(body, result, p); });
    body._hro.observe(body);
  }
  // Exemplars on by default (PLAN-NEXT 14.4): place trace markers along the
  // latency (le) axis at the x matching each exemplar's durationMs, clickable
  // to open the trace. No opt-in config required.
  overlayHistogramExemplars(body, p, buckets, { svgW, svgH, unit });
}

// Fetch exemplars for the panel's window/service and draw clickable markers
// at the x-position corresponding to each exemplar's latency bucket.
async function overlayHistogramExemplars(body, p, buckets, geom) {
  if (!buckets.length) return;
  const svc = panelServices(p.id)[0] || deriveServiceFromExpr(p.expr) || null;
  const now = Date.now();
  const fromMs = now - (state.current.timeRangeSec || 3600) * 1000;
  const xs = await fetchExemplars(svc, fromMs, now, 100);
  if (!xs.length || !body.isConnected) return;
  const svg = body.querySelector("svg");
  if (!svg) return;
  const { svgW, svgH } = geom;
  const colW = (svgW - 20) / buckets.length;
  const ns = "http://www.w3.org/2000/svg";
  const yBase = svgH - 20;
  for (const ex of xs) {
    // Find the first bucket whose le >= the exemplar latency.
    let idx = buckets.findIndex(b => ex.durationMs <= b.le);
    if (idx < 0) idx = buckets.length - 1;
    const cx = 10 + idx * colW + colW / 2;
    const cy = yBase + 6;
    const half = 4;
    const poly = document.createElementNS(ns, "polygon");
    poly.setAttribute("points",
      `${cx},${cy-half} ${cx+half},${cy} ${cx},${cy+half} ${cx-half},${cy}`);
    poly.setAttribute("fill", ex.error ? "#ff5d5d" : "#34c759");
    poly.setAttribute("stroke", "var(--panel)");
    poly.setAttribute("stroke-width", "0.5");
    poly.style.cursor = "pointer";
    const title = document.createElementNS(ns, "title");
    title.textContent =
      `${ex.service} · ${ex.operation}\n${fmtDur(ex.durationMs)}${ex.error ? " · error" : ""}\nclick to open trace`;
    poly.appendChild(title);
    poly.addEventListener("click", (e) => { e.stopPropagation(); openTrace(ex.traceId); });
    svg.appendChild(poly);
  }
}

// ── State timeline ────────────────────────────────────────────────────
// Each series is expected to be step-interpolated (0/1/2 = states).
// opts: states (comma-sep labels e.g. "off,on,warn"), colors
function renderStateTimeline(body, result, p) {
  const opts   = p.options || {};
  const labels = (opts.states || "0,1").split(",");
  const stateColors = ["var(--panel-3)", "var(--ok)", "var(--warn)", "var(--err)",
                       "var(--accent)", "var(--accent-2)"];
  const series = (result.series || []);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  const rows = series.map(s => {
    if (s.xs.length === 0) return "";
    const t0  = s.xs[0], t1 = s.xs[s.xs.length - 1], span = Math.max(1, t1 - t0);
    const segs = s.xs.map((x, i) => {
      const stateIdx = Math.round(s.ys[i] || 0);
      const col      = stateColors[stateIdx % stateColors.length];
      const left     = ((x - t0) / span * 100).toFixed(2);
      const width    = i + 1 < s.xs.length
        ? (((s.xs[i+1] - x) / span) * 100).toFixed(2)
        : (100 - +left).toFixed(2);
      const lbl      = labels[stateIdx] || String(stateIdx);
      return `<div class="stl-seg" style="left:${left}%;width:${width}%;background:${col};"
        title="${escapeHtml(s.name)}: ${escapeHtml(lbl)}"></div>`;
    }).join("");
    return `<div class="stl-row">
      <div class="stl-label" title="${escapeHtml(s.name)}">${escapeHtml(
        s.name.length > 16 ? s.name.slice(0,15) + "…" : s.name)}</div>
      <div class="stl-track">${segs}</div>
    </div>`;
  }).join("");
  body.innerHTML = rows || '<div class="empty">no data</div>';
}

// ── Status history ────────────────────────────────────────────────────
// Renders a grid of equally-sized coloured cells — one cell per time
// step.  Value ranges map to ok/warn/err colours via thresholds.
// opts: thresholds (e.g. "80=warn,90=err"), buckets (max cells, default 40)
function renderStatusHistory(body, result, p) {
  const opts       = p.options || {};
  const maxCells   = +(opts.buckets || 40);
  const thresholds = [];
  for (const tok of (opts.thresholds || "").split(",")) {
    const [at, name] = tok.split("=");
    if (!isNaN(+at)) thresholds.push({ at: +at,
      color: name === "err" ? "var(--err)" : name === "warn" ? "var(--warn)" : "var(--ok)" });
  }
  thresholds.sort((a, b) => a.at - b.at);
  function cellColor(v) {
    let c = "var(--ok)";
    for (const t of thresholds) if (v >= t.at) c = t.color;
    return c;
  }

  const series = (result.series || []);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }
  const rows = series.map(s => {
    const pts   = s.ys.slice(-maxCells);
    const cells = pts.map((v, i) =>
      `<div class="sh-cell" style="background:${cellColor(v)};"
        title="${fmtTs(s.xs.slice(-maxCells)[i] * 1000)}: ${fmtNum(v)}"></div>`
    ).join("");
    return `<div class="sh-row">
      <div class="sh-label" title="${escapeHtml(s.name)}">${escapeHtml(
        s.name.length > 16 ? s.name.slice(0,15) + "…" : s.name)}</div>
      <div class="sh-cells">${cells}</div>
    </div>`;
  }).join("");
  body.innerHTML = rows || '<div class="empty">no data</div>';
}

// ── Text (markdown) ───────────────────────────────────────────────────
// opts: content (markdown string).  Query is ignored.
// Uses a minimal inline Markdown→HTML renderer (no third-party lib needed
// for the common subset used in runbooks / info panels).
function renderTextPanel(body, result, p) {
  const md  = (p.options || {}).content || "(empty — set `content` in panel options)";
  body.innerHTML = mdToHtml(md);
}

function mdToHtml(src) {
  // Process block by block; keep it simple and XSS-safe (no raw HTML passthrough).
  const lines   = src.split("\n");
  const out     = [];
  let   inPre   = false, preBuf = [];
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    // Fenced code blocks
    if (raw.startsWith("```")) {
      if (!inPre) { inPre = true; preBuf = []; continue; }
      out.push("<pre><code>" + escapeHtml(preBuf.join("\n")) + "</code></pre>");
      inPre = false; preBuf = []; continue;
    }
    if (inPre) { preBuf.push(raw); continue; }
    // Headings
    const hm = raw.match(/^(#{1,3})\s+(.*)/);
    if (hm) { const n = hm[1].length; out.push(`<h${n}>${inlinemd(hm[2])}</h${n}>`); continue; }
    // HR
    if (/^---+$/.test(raw.trim())) { out.push("<hr/>"); continue; }
    // Bullet list
    const li = raw.match(/^[-*]\s+(.*)/);
    if (li) { out.push(`<ul><li>${inlinemd(li[1])}</li></ul>`); continue; }
    // Empty line
    if (raw.trim() === "") { out.push("<br/>"); continue; }
    out.push("<p>" + inlinemd(raw) + "</p>");
  }
  return out.join("");
}
function inlinemd(s) {
  return escapeHtml(s)
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/\*(.+?)\*/g,     "<em>$1</em>")
    .replace(/`(.+?)`/g,       "<code>$1</code>")
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, t, u) =>
      // Only allow safe URL schemes to prevent javascript: injection.
      /^https?:\/\//.test(u) ? `<a href="${escapeHtml(u)}" target="_blank" rel="noopener">${t}</a>` : t
    );
}

// ── Dashboard list ────────────────────────────────────────────────────
// opts: (none).  Renders a linked list of all dashboards.
function renderDashList(body, result, p) {
  const list = state.dashboards || [];
  if (list.length === 0) { body.innerHTML = '<div class="empty">no dashboards</div>'; return; }
  body.innerHTML = list.map(d =>
    `<a href="#/dashboards" data-id="${escapeHtml(d.id)}" tabindex="0">
       <span style="flex:1;">${escapeHtml(d.title)}</span>
     </a>`
  ).join("");
  body.querySelectorAll("a[data-id]").forEach(a => {
    a.addEventListener("click", (e) => {
      e.preventDefault();
      openDashboard(a.dataset.id);
      showView("dashboards");
    });
  });
}

// ── Alert list ────────────────────────────────────────────────────────
// Fetches /api/alerts and shows name + state.
async function renderAlertList(body, result, p) {
  body.innerHTML = '<div class="empty">loading…</div>';
  const editLink = `<div style="text-align:right;margin-bottom:4px;">
       <a href="#/alerts" style="font-size:10px;color:var(--accent);">Edit rules →</a></div>`;
  try {
    const r = await authFetch("/api/alerts");
    if (!r.ok) throw new Error("HTTP " + r.status);
    const alerts = await r.json();
    if (alerts.length === 0) { body.innerHTML = editLink + '<div class="empty">no alerts</div>'; return; }
    body.innerHTML = editLink + alerts.map(a => {
      const name = a.ruleName || a.name || a.alertname || (a.labels && a.labels.alertname) || "—";
      const rb = a.runbook
        ? `<button style="font-size:10px;padding:1px 6px;"
                   onclick='openRunbook(${JSON.stringify(a.fingerprint)}, ${JSON.stringify(name)})'>Runbook</button>`
        : "";
      // Correlated signals (PLAN-NEXT 14.4): offered for active/firing alerts.
      const st = (a.state || "ok").toLowerCase();
      const corr = (st === "firing" || st === "alerting" || st === "active" || st === "pending")
        ? `<button style="font-size:10px;padding:1px 6px;"
                   onclick='openCorrelation(${JSON.stringify(a.fingerprint)}, ${JSON.stringify(name)})'>Correlate</button>`
        : "";
      return `<div class="al-row">
         <span class="al-state ${escapeHtml(a.state || "ok")}">${escapeHtml(a.state || "ok")}</span>
         <span style="flex:1;">${escapeHtml(name)}</span>
         <span style="color:var(--muted);font-size:11px;">${escapeHtml(a.labels && a.labels.severity || "")}</span>
         ${corr}
         ${rb}
       </div>`;
    }).join("");
  } catch (e) {
    body.innerHTML = `<div class="err">${escapeHtml(e.message)}</div>`;
  }
}

// ── Annotations list ─────────────────────────────────────────────────
// Fetches /api/annotations and shows time + text.
async function renderAnnoList(body, result, p) {
  body.innerHTML = '<div class="empty">loading…</div>';
  try {
    const r = await authFetch("/api/annotations?limit=50");
    if (!r.ok) throw new Error("HTTP " + r.status);
    const items = await r.json();
    if (items.length === 0) { body.innerHTML = '<div class="empty">no annotations</div>'; return; }
    body.innerHTML = items.map(a =>
      `<div class="ao-row">
         <span class="ao-ts">${fmtTs(a.timeMs || a.time || 0)}</span>
         <span style="flex:1;">${escapeHtml(a.text || a.message || "—")}</span>
         ${a.tags ? `<span style="color:var(--muted);font-size:10px;">${escapeHtml(a.tags.join(", "))}</span>` : ""}
       </div>`
    ).join("");
  } catch (e) {
    body.innerHTML = `<div class="err">${escapeHtml(e.message)}</div>`;
  }
}

// =====================================================================
// Wave B render helpers (Phase 12.2)
// =====================================================================

// ── Heatmap ──────────────────────────────────────────────────────────
// Expects a Prometheus histogram result (series with le="…" labels).
// Builds a 2D canvas: x = time buckets, y = le buckets, colour = density.
// opts: colorScheme (hot|blues|greens, default hot)
function renderHeatmap(body, result, p) {
  const opts   = p.options || {};
  const scheme = opts.colorScheme || "hot";
  const series = (result.series || []).filter(s => s.ys.length);
  if (series.length === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  // Sort series by le value.
  const buckets = series.map(s => {
    const m  = s.name.match(/le="([^"]+)"/);
    const le = m ? (m[1] === "+Inf" ? Infinity : +m[1]) : NaN;
    return { le, xs: s.xs, ys: s.ys };
  }).filter(b => !isNaN(b.le)).sort((a, b) => a.le - b.le);

  if (buckets.length < 2) { body.innerHTML = '<div class="empty">need ≥2 le buckets</div>'; return; }

  // Convert cumulative counts to per-bucket densities per time step.
  const numT   = buckets[0].xs.length;
  const numB   = buckets.length - 1;  // +Inf excluded from colour range
  const density = Array.from({ length: numB }, (_, bi) =>
    buckets[bi].ys.map((v, ti) => Math.max(0, v - (buckets[bi - 1] ? buckets[bi - 1].ys[ti] : 0)))
  );
  const maxD = Math.max(1, ...density.flatMap(r => r));

  // Create / reuse canvas.
  let canvas = body.querySelector("canvas");
  if (!canvas) { canvas = document.createElement("canvas"); body.appendChild(canvas); }
  const W = body.clientWidth  || 300;
  const H = body.clientHeight || 120;
  canvas.width  = W;
  canvas.height = H;
  const ctx = canvas.getContext("2d");
  const cellW = W / numT;
  const cellH = H / numB;

  function hotColor(t) {
    // t in [0,1] → black→red→yellow→white
    if (scheme === "blues") {
      const v = Math.round(t * 200 + 55);
      return `rgb(${Math.round(t*80)},${Math.round(t*140)},${v})`;
    }
    if (scheme === "greens") {
      const v = Math.round(t * 200 + 55);
      return `rgb(${Math.round(t*80)},${v},${Math.round(t*80)})`;
    }
    // hot (default)
    const r = Math.min(255, Math.round(t * 3 * 255));
    const g = Math.min(255, Math.max(0, Math.round((t * 3 - 1) * 255)));
    const b = Math.min(255, Math.max(0, Math.round((t * 3 - 2) * 255)));
    return `rgb(${r},${g},${b})`;
  }

  ctx.clearRect(0, 0, W, H);
  for (let bi = 0; bi < numB; bi++) {
    for (let ti = 0; ti < numT; ti++) {
      const d = density[bi][ti] / maxD;
      if (d <= 0) continue;
      ctx.fillStyle = hotColor(d);
      ctx.fillRect(
        Math.floor(ti * cellW), Math.floor((numB - 1 - bi) * cellH),
        Math.ceil(cellW) + 1, Math.ceil(cellH) + 1
      );
    }
  }

  // Resize observer.
  if (!body._hmro) {
    body._hmro = new ResizeObserver(() => { if (body.isConnected) renderHeatmap(body, result, p); });
    body._hmro.observe(body);
  }
  // Exemplars on by default (PLAN-NEXT 14.4): overlay clickable trace markers
  // at (time, latency) positions on top of the heatmap canvas.
  overlayHeatmapExemplars(body, p, buckets, { W, H, numT });
}

// Overlay clickable exemplar diamonds on the heatmap canvas, positioned by
// time (x) and latency bucket (y). Uses absolutely-positioned DOM markers so
// they remain interactive (canvas pixels are not).
async function overlayHeatmapExemplars(body, p, buckets, geom) {
  // Clear any markers from a previous render pass.
  body.querySelectorAll(".hm-exemplar").forEach(n => n.remove());
  if (!buckets.length) return;
  const xsArr = buckets[0].xs || [];
  if (xsArr.length < 2) return;
  const svc = panelServices(p.id)[0] || deriveServiceFromExpr(p.expr) || null;
  const t0 = xsArr[0] * 1000, t1 = xsArr[xsArr.length - 1] * 1000;
  const xs = await fetchExemplars(svc, t0, t1, 100);
  if (!xs.length || !body.isConnected) return;
  if (getComputedStyle(body).position === "static") body.style.position = "relative";
  const { W, H } = geom;
  const span = Math.max(1, t1 - t0);
  // Map an exemplar latency to its le-bucket row index (top = largest le).
  const numB = buckets.length - 1;
  for (const ex of xs) {
    const exMs = ex.ts;
    if (exMs < t0 || exMs > t1) continue;
    let bi = buckets.findIndex(b => ex.durationMs <= b.le);
    if (bi < 0) bi = numB;
    const x = ((exMs - t0) / span) * W;
    const y = ((numB - 1 - Math.min(bi, numB - 1)) / Math.max(1, numB)) * H + (H / Math.max(1, numB)) / 2;
    const d = document.createElement("div");
    d.className = "hm-exemplar";
    d.style.cssText =
      `position:absolute;width:8px;height:8px;transform:translate(-50%,-50%) rotate(45deg);` +
      `left:${x.toFixed(1)}px;top:${y.toFixed(1)}px;cursor:pointer;border:1px solid var(--panel);` +
      `background:${ex.error ? "#ff5d5d" : "#34c759"};z-index:2;`;
    d.title = `${ex.service} · ${ex.operation}\n${fmtDur(ex.durationMs)}${ex.error ? " · error" : ""}\nclick to open trace`;
    d.addEventListener("click", (e) => { e.stopPropagation(); openTrace(ex.traceId); });
    body.appendChild(d);
  }
}

// ── Trend (non-time numeric x) ────────────────────────────────────────
// Like timeseries but x-axis is treated as a plain number, not a Unix
// timestamp. opts: unit, xLabel
function renderTrend(body, result, p) {
  const series  = (result.series || []);
  const popts   = p.options || {};
  const cached  = state.panels.get(p.id) || {};
  if (series.length === 0) {
    if (cached.uplot) { try { cached.uplot.destroy(); } catch {} state.panels.set(p.id, { dom: cached.dom }); }
    body.innerHTML = '<div class="empty">no data</div>'; return;
  }

  const xset = new Set();
  for (const s of series) for (const x of s.xs) xset.add(x);
  const xs = Array.from(xset).sort((a, b) => a - b);
  const xi = new Map(); xs.forEach((x, i) => xi.set(x, i));
  const data = [xs];
  for (const s of series) {
    const arr = new Array(xs.length).fill(null);
    for (let i = 0; i < s.xs.length; i++) {
      const idx = xi.get(s.xs[i]); if (idx != null) arr[idx] = s.ys[i];
    }
    data.push(arr);
  }

  const specKey = plotSpecKey(series.map(s => s.name), popts);
  const existing = cached.uplot;
  const sameSchema = existing && existing._specKey === specKey && body.contains(existing.root);
  if (sameSchema) {
    try {
      existing.setData(data);
      if (existing.width !== body.clientWidth || existing.height !== body.clientHeight) {
        existing.setSize({ width: body.clientWidth, height: body.clientHeight });
      }
      return;
    } catch { /* fall through to recreate */ }
  }
  if (existing) { try { existing.destroy(); } catch {} }
  body.innerHTML = "";

  const legendOpt  = popts.legend || "";
  const showLegend = legendOpt === "show" ? true
                   : legendOpt === "hide" ? false
                   : series.length > 1;
  const unitFmt    = makeUnitFmt(popts.unit || "");
  const xLabel     = popts.xLabel || "";
  const yMin       = popts.yMin !== "" && popts.yMin != null ? +popts.yMin : undefined;
  const yMax       = popts.yMax !== "" && popts.yMax != null ? +popts.yMax : undefined;
  const seriesDefs = buildSeriesDefs(series.map(s => s.name), xs, popts);

  const uopts = {
    width:  body.clientWidth  || 300,
    height: body.clientHeight || 100,
    cursor: { drag: { x: true, y: false } },
    legend: { show: showLegend },
    scales: {
      x: { time: false },   // key difference from timeseries
      y: { min: yMin, max: yMax },
    },
    axes: [
      { stroke: "#8a93a1", grid: { stroke: "#232833" },
        label: xLabel || undefined,
        values: (u, vals) => vals.map(v => fmtNum(v)) },
      { stroke: "#8a93a1", grid: { stroke: "#232833" }, size: makeYAxisSize(unitFmt),
        values: (u, vals) => vals.map(v => unitFmt(v)) },
    ],
    plugins: [tooltipPlugin(series.map(s => s.name), unitFmt, false)],
    series: [{}, ...seriesDefs],
  };
  const plot = new uPlot(uopts, data, body);
  plot._specKey = specKey;
  state.panels.set(p.id, { dom: body.closest(".panel"), uplot: plot });
  if (!body._tro) {
    body._tro = new ResizeObserver(() => {
      const cached = state.panels.get(p.id);
      if (cached && cached.uplot) {
        try { cached.uplot.setSize({ width: body.clientWidth, height: body.clientHeight }); } catch {}
      }
    });
    body._tro.observe(body);
  }
}

// ── XY chart (scatter / bubble) ───────────────────────────────────────
// Expects two series: first is x values, second is y values (same index
// pairs). Optional third series drives bubble radius.
// opts: unit, xLabel, yLabel
function renderXYChart(body, result, p) {
  const opts   = p.options || {};
  const series = (result.series || []);
  if (series.length < 2) {
    body.innerHTML = '<div class="empty">need ≥2 series (x, y)</div>'; return;
  }
  const xs  = series[0].ys;
  const ys  = series[1].ys;
  const rs  = series[2] ? series[2].ys : null;
  const n   = Math.min(xs.length, ys.length);
  if (n === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  const W = body.clientWidth  || 300;
  const H = body.clientHeight || 120;
  const PAD = { l: 36, r: 8, t: 8, b: 24 };
  const plotW = W - PAD.l - PAD.r;
  const plotH = H - PAD.t - PAD.b;

  const minX = Math.min(...xs), maxX = Math.max(...xs) || 1;
  const minY = Math.min(...ys), maxY = Math.max(...ys) || 1;
  const maxR = rs ? Math.max(...rs) || 1 : 1;

  function scX(v) { return PAD.l + ((v - minX) / (maxX - minX || 1)) * plotW; }
  function scY(v) { return PAD.t + plotH - ((v - minY) / (maxY - minY || 1)) * plotH; }

  const dots = Array.from({ length: n }, (_, i) => {
    const r   = rs ? Math.max(3, (rs[i] / maxR) * 14) : 4;
    const col = colorFor(0);
    const lbl = `(${fmtNum(xs[i])}, ${fmtNum(ys[i])})`;
    return `<circle cx="${scX(xs[i]).toFixed(1)}" cy="${scY(ys[i]).toFixed(1)}"
      r="${r.toFixed(1)}" fill="${col}" fill-opacity="0.7" stroke="${col}" stroke-width="1">
      <title>${escapeHtml(lbl)}</title></circle>`;
  }).join("");

  // Axis ticks
  const xTicks = [minX, (minX + maxX) / 2, maxX].map(v =>
    `<text x="${scX(v).toFixed(1)}" y="${(H - 6)}" text-anchor="middle"
      font-size="9" fill="var(--muted)">${fmtNum(v)}</text>`).join("");
  const yTicks = [minY, (minY + maxY) / 2, maxY].map(v =>
    `<text x="${(PAD.l - 3)}" y="${scY(v).toFixed(1)}" text-anchor="end"
      dominant-baseline="middle" font-size="9" fill="var(--muted)">${fmtNum(v)}</text>`).join("");
  const xLabel = opts.xLabel
    ? `<text x="${(PAD.l + plotW / 2).toFixed(1)}" y="${H}" text-anchor="middle"
        font-size="10" fill="var(--muted)">${escapeHtml(opts.xLabel)}</text>` : "";

  body.innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" xmlns="http://www.w3.org/2000/svg"
         style="width:100%;height:100%;">
       <line x1="${PAD.l}" y1="${PAD.t}" x2="${PAD.l}" y2="${PAD.t + plotH}"
         stroke="var(--grid)"/>
       <line x1="${PAD.l}" y1="${PAD.t + plotH}" x2="${PAD.l + plotW}" y2="${PAD.t + plotH}"
         stroke="var(--grid)"/>
       ${dots}${xTicks}${yTicks}${xLabel}
     </svg>`;

  if (!body._xyro) {
    body._xyro = new ResizeObserver(() => { if (body.isConnected) renderXYChart(body, result, p); });
    body._xyro.observe(body);
  }
}

// ── Candlestick (OHLC) ────────────────────────────────────────────────
// Expects series named exactly "open", "high", "low", "close" sharing
// the same timestamps (xs from the "open" series).
// opts: unit, upColor, downColor
function renderCandlestick(body, result, p) {
  const opts   = p.options || {};
  const up     = opts.upColor   || "var(--ok)";
  const down   = opts.downColor || "var(--err)";
  const map    = {};
  for (const s of (result.series || [])) {
    const key = s.name.toLowerCase().replace(/^.*\b(open|high|low|close)\b.*$/, "$1");
    if (["open","high","low","close"].includes(key)) map[key] = s;
  }
  if (!map.open || !map.close) {
    body.innerHTML = '<div class="empty">need series named open, high, low, close</div>'; return;
  }
  const n = map.open.xs.length;
  if (n === 0) { body.innerHTML = '<div class="empty">no data</div>'; return; }

  const W = body.clientWidth  || 300;
  const H = body.clientHeight || 120;
  const PAD = { l: 44, r: 8, t: 8, b: 20 };
  const plotW = W - PAD.l - PAD.r;
  const plotH = H - PAD.t - PAD.b;

  const allVals = [...(map.high || map.open).ys, ...(map.low || map.open).ys];
  const minV = Math.min(...allVals);
  const maxV = Math.max(...allVals) || 1;
  function scY(v) { return PAD.t + plotH - ((v - minV) / (maxV - minV || 1)) * plotH; }

  const barW   = Math.max(2, plotW / n * 0.6);
  const parts  = [];
  map.open.xs.forEach((ts, i) => {
    const o = map.open.ys[i];
    const c = map.close.ys[i];
    const h = map.high  ? map.high.ys[i]  : Math.max(o, c);
    const l = map.low   ? map.low.ys[i]   : Math.min(o, c);
    const x = PAD.l + (i + 0.5) * (plotW / n);
    const col    = c >= o ? up : down;
    const bodyY  = scY(Math.max(o, c));
    const bodyH  = Math.max(1, Math.abs(scY(o) - scY(c)));
    // Wick
    parts.push(`<line x1="${x.toFixed(1)}" y1="${scY(h).toFixed(1)}"
      x2="${x.toFixed(1)}" y2="${scY(l).toFixed(1)}" stroke="${col}" stroke-width="1"/>`);
    // Body
    parts.push(`<rect x="${(x - barW/2).toFixed(1)}" y="${bodyY.toFixed(1)}"
      width="${barW.toFixed(1)}" height="${bodyH.toFixed(1)}" fill="${col}" rx="1">
      <title>O:${fmtNum(o)} H:${fmtNum(h)} L:${fmtNum(l)} C:${fmtNum(c)}</title></rect>`);
  });

  // Y axis labels
  const yTicks = [minV, (minV+maxV)/2, maxV].map(v =>
    `<text x="${(PAD.l - 4)}" y="${scY(v).toFixed(1)}" text-anchor="end"
      dominant-baseline="middle" font-size="9" fill="var(--muted)">${fmtNum(v)}</text>`
  ).join("");
  // X axis — first and last timestamp
  const t0 = new Date(map.open.xs[0] * 1000);
  const t1 = new Date(map.open.xs[n - 1] * 1000);
  const fmt = d => d.toISOString().slice(0, 10);
  const xLabels =
    `<text x="${PAD.l}" y="${H - 3}" font-size="9" fill="var(--muted)">${fmt(t0)}</text>` +
    `<text x="${W - PAD.r}" y="${H - 3}" text-anchor="end" font-size="9"
      fill="var(--muted)">${fmt(t1)}</text>`;

  body.innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" xmlns="http://www.w3.org/2000/svg"
         style="width:100%;height:100%;">
       ${parts.join("")}${yTicks}${xLabels}
     </svg>`;

  if (!body._csro) {
    body._csro = new ResizeObserver(() => { if (body.isConnected) renderCandlestick(body, result, p); });
    body._csro.observe(body);
  }
}

// ── Traces panel (embeddable waterfall) ───────────────────────────────
// Renders a compact trace list inside the panel body with inline
// waterfall expansion on click — reuses the existing renderWaterfall().
// opts: limit (default 20), range (seconds, overrides dashboard range)
async function renderTracesPanel(body, result, p) {
  const opts  = p.options || {};
  const limit = +(opts.limit || 20);
  body.innerHTML = '<div class="empty">loading…</div>';
  try {
    const since = Date.now() - (+(opts.range || 3600)) * 1000;
    const r     = await authFetch(`/api/traces?sinceMs=${since}&limit=${limit}`);
    if (!r.ok) throw new Error("HTTP " + r.status);
    const traces = await r.json();
    if (traces.length === 0) { body.innerHTML = '<div class="empty">no traces</div>'; return; }

    const rows = traces.map(t => {
      const errCls = t.errorCount > 0 ? " tp-err" : "";
      return `<div class="tp-row" data-tid="${escapeHtml(t.traceId)}">
        <span class="tp-svc">${escapeHtml(t.rootService)}</span>
        <span class="tp-op">${escapeHtml(t.rootOperation)}</span>
        <span class="tp-dur">${fmtDur(t.durationMs)}</span>
        ${t.errorCount > 0 ? `<span class="tp-err">✕${t.errorCount}</span>` : ""}
      </div>`;
    }).join("");

    body.innerHTML = `<div class="tp-list">${rows}</div>`;

    body.querySelectorAll(".tp-row[data-tid]").forEach(row => {
      row.addEventListener("click", async () => {
        // Toggle: if already open, close it.
        const existing = row.nextElementSibling;
        if (existing && existing.classList.contains("tp-detail")) {
          existing.remove(); return;
        }
        const detail = document.createElement("div");
        detail.className = "tp-detail";
        detail.textContent = "loading…";
        row.insertAdjacentElement("afterend", detail);
        try {
          const dr = await authFetch("/api/traces/" + encodeURIComponent(row.dataset.tid));
          if (!dr.ok) throw new Error("HTTP " + dr.status);
          const data = await dr.json();
          // Reuse the waterfall renderer but target a temp container.
          const tmp = document.createElement("div");
          tmp.className = "waterfall";
          // renderWaterfall writes into trace-modal-body; redirect it temporarily.
          const origBody = document.getElementById("trace-modal-body");
          const savedHTML = origBody.innerHTML;
          renderWaterfall(data);
          tmp.innerHTML = origBody.innerHTML;
          origBody.innerHTML = savedHTML;
          detail.innerHTML = "";
          detail.appendChild(tmp);
        } catch (e) {
          detail.innerHTML = `<span style="color:var(--err)">${escapeHtml(e.message)}</span>`;
        }
      });
    });
  } catch (e) {
    body.innerHTML = `<div class="err">${escapeHtml(e.message)}</div>`;
  }
}

// ── Flame graph ───────────────────────────────────────────────────────
// Accepts a spans result (from /api/traces/<id>) or a synthetic flat
// series where each series name encodes a stack frame path with ">"
// separators, e.g. "main>http.Handler>db.Query", and the last ys value
// is the self-time in ms.  Renders a top-down icicle/flame chart.
// opts: maxDepth (default 12), minWidthPx (default 2)
function renderFlameGraph(body, result, p) {
  const opts     = p.options || {};
  const maxDepth = +(opts.maxDepth || 12);
  const minW     = +(opts.minWidthPx || 2);

  // Build a tree from series whose names encode stacks with ">".
  const root = { name: "total", value: 0, children: {} };
  for (const s of (result.series || [])) {
    const frames = s.name.split(">").map(f => f.trim()).filter(Boolean);
    const val    = s.ys.length ? Math.abs(s.ys[s.ys.length - 1]) : 0;
    if (frames.length === 0 || val === 0) continue;
    let node = root;
    node.value += val;
    for (const frame of frames) {
      if (!node.children[frame]) node.children[frame] = { name: frame, value: 0, children: {} };
      node = node.children[frame];
      node.value += val;
    }
  }

  if (root.value === 0) { body.innerHTML = '<div class="empty">no frame data (use "a>b>c" series names)</div>'; return; }

  const W    = body.clientWidth || 300;
  const ROW  = 18;

  // Flatten tree into layout rows.
  const frames = [];
  function walk(node, depth, x0, x1) {
    if (depth > maxDepth) return;
    const w = x1 - x0;
    if (w * W < minW) return;
    frames.push({ name: node.name, depth, x: x0, w, value: node.value });
    const childTotal = Object.values(node.children).reduce((s, c) => s + c.value, 0) || 1;
    let cx = x0;
    for (const child of Object.values(node.children)) {
      const cw = (child.value / childTotal) * w;
      walk(child, depth + 1, cx, cx + cw);
      cx += cw;
    }
  }
  walk(root, 0, 0, 1);

  const maxDepthSeen = Math.max(...frames.map(f => f.depth));
  const H = (maxDepthSeen + 1) * ROW + 4;

  const rects = frames.map(f => {
    const x  = (f.x * W).toFixed(1);
    const y  = (f.depth * ROW + 2).toFixed(1);
    const fw = Math.max(0, (f.w * W - 1)).toFixed(1);
    const col = colorFor(f.depth + Math.round(f.x * 7));
    const lbl = f.name.length > 16 ? f.name.slice(0, 15) + "…" : f.name;
    const showLabel = f.w * W > 30;
    return `<g class="fg-frame" data-name="${escapeHtml(f.name)}" data-val="${escapeHtml(fmtDur(f.value))}">
      <rect x="${x}" y="${y}" width="${fw}" height="${ROW - 1}" fill="${col}"/>
      ${showLabel ? `<text x="${(+x + 3).toFixed(1)}" y="${(+y + ROW/2).toFixed(1)}"
        clip-path="inset(0 0 0 0)">${escapeHtml(lbl)}</text>` : ""}
      <title>${escapeHtml(f.name)} — ${escapeHtml(fmtDur(f.value))}</title>
    </g>`;
  }).join("");

  body.innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}"
         xmlns="http://www.w3.org/2000/svg">${rects}</svg>`;

  if (!body._fgro) {
    body._fgro = new ResizeObserver(() => { if (body.isConnected) renderFlameGraph(body, result, p); });
    body._fgro.observe(body);
  }
}

// ── News (RSS via server proxy) ───────────────────────────────────────
// opts: url (RSS feed URL), limit (default 10)
// Server must expose GET /api/news?url=<encoded> returning
// [{title, link, pubDate, description}].
async function renderNews(body, result, p) {
  const opts  = p.options || {};
  const feed  = opts.url || "";
  const limit = +(opts.limit || 10);
  if (!feed) {
    body.innerHTML = '<div class="empty">set <code>url</code> in panel options</div>'; return;
  }
  body.innerHTML = '<div class="empty">loading…</div>';
  try {
    const r = await authFetch("/api/news?url=" + encodeURIComponent(feed) + "&limit=" + limit);
    if (!r.ok) throw new Error("HTTP " + r.status);
    const items = await r.json();
    if (items.length === 0) { body.innerHTML = '<div class="empty">no items</div>'; return; }
    body.innerHTML = items.map(item => {
      const date = item.pubDate ? new Date(item.pubDate).toLocaleDateString() : "";
      // Only allow https:// links — block javascript: and other schemes.
      const safeLink = /^https?:\/\//.test(item.link || "") ? item.link : "#";
      return `<div class="nw-item">
        <div class="nw-title">
          <a href="${escapeHtml(safeLink)}" target="_blank" rel="noopener noreferrer">
            ${escapeHtml(item.title || "(no title)")}
          </a>
        </div>
        <div class="nw-meta">${escapeHtml(date)}</div>
      </div>`;
    }).join("");
  } catch (e) {
    body.innerHTML = `<div class="err">${escapeHtml(e.message)}</div>`;
  }
}

PulseBoard.registerPanel({
  type: "timeseries",
  label: "Time series",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderTimeseries(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "stat",
  label: "Stat",
  queryShape: "scalar",
  render(el, frame, opts, p) { renderStat(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "logs",
  label: "Logs",
  queryShape: "logs",
  render(el, frame, opts, p) { renderLogs(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "table",
  label: "Table",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderTable(el, frame, p); },
});

// =====================================================================
// Wave C render helpers (Phase 12.2)
// =====================================================================

// ── Force-directed layout (shared by node-graph) ──────────────────────
// Fruchterman-Reingold; runs synchronously; fine for < ~80 nodes.
// edges: [{si, ti}]  nodes get .x and .y set in place.
function forceLayout(nodes, edges, W, H) {
  const n = nodes.length;
  if (n === 0) return;
  const r0 = Math.min(W, H) * 0.35;
  nodes.forEach((nd, i) => {
    const a = (i / n) * Math.PI * 2 - Math.PI / 2;
    nd.x = W / 2 + r0 * Math.cos(a);
    nd.y = H / 2 + r0 * Math.sin(a);
  });
  if (n === 1) return;
  const k   = Math.sqrt(W * H / n) * 0.8;
  let   tmp = k * 2;
  for (let iter = 0; iter < 220; iter++) {
    // Repulsion
    for (let i = 0; i < n; i++) {
      nodes[i].dx = 0; nodes[i].dy = 0;
      for (let j = 0; j < n; j++) {
        if (i === j) continue;
        const dx = nodes[i].x - nodes[j].x, dy = nodes[i].y - nodes[j].y;
        const d  = Math.hypot(dx, dy) || 0.01;
        const f  = k * k / d;
        nodes[i].dx += (dx / d) * f;
        nodes[i].dy += (dy / d) * f;
      }
    }
    // Attraction
    for (const e of edges) {
      const a = nodes[e.si], b = nodes[e.ti];
      if (!a || !b) continue;
      const dx = a.x - b.x, dy = a.y - b.y;
      const d  = Math.hypot(dx, dy) || 0.01;
      const f  = d * d / k;
      const fx = (dx / d) * f, fy = (dy / d) * f;
      a.dx -= fx; a.dy -= fy;
      b.dx += fx; b.dy += fy;
    }
    for (const nd of nodes) {
      const d = Math.hypot(nd.dx, nd.dy) || 1;
      nd.x = Math.max(36, Math.min(W - 36, nd.x + (nd.dx / d) * Math.min(d, tmp)));
      nd.y = Math.max(36, Math.min(H - 36, nd.y + (nd.dy / d) * Math.min(d, tmp)));
    }
    tmp *= 0.97;
  }
}

// ── Node graph (general force-directed DAG) ───────────────────────────
// opts.source = "servicemap" (default when no series) → fetches /api/servicemap
// Otherwise: series names encode edges as "from→to" or "from->to";
//            last value = edge weight shown as label.
// opts: nodeRadius (22), range (3600), showEdgeLabels (true)
async function renderNodeGraph(body, result, p) {
  const opts = p.options || {};
  const R    = +(opts.nodeRadius || 22);
  let nodes  = [], edges = [];

  if (opts.source === "servicemap" || !(result.series || []).length) {
    try {
      const since = Date.now() - (+(opts.range || 3600)) * 1000;
      const r = await authFetch(`/api/servicemap?sinceMs=${since}`);
      if (!r.ok) throw new Error("HTTP " + r.status);
      const m = await r.json();
      nodes = m.nodes.map(n => ({
        id: n.service, label: n.service,
        errRate: n.spanCount ? n.errorCount / n.spanCount : 0,
        tip: `${n.service}\nspans: ${n.spanCount}  errors: ${n.errorCount}\np50: ${fmtDur(n.p50Ms)}  p95: ${fmtDur(n.p95Ms)}  p99: ${fmtDur(n.p99Ms)}`,
      }));
      const idx = new Map(nodes.map((n, i) => [n.id, i]));
      edges = m.edges.map(e => ({
        si: idx.get(e.from) ?? -1, ti: idx.get(e.to) ?? -1,
        label: opts.showEdgeLabels !== "false" ? `${e.callCount}` : "",
        errRate: e.callCount ? e.errorCount / e.callCount : 0,
        tip: `${e.from} → ${e.to}\ncalls: ${e.callCount}  errors: ${e.errorCount}\np50: ${fmtDur(e.p50Ms)}  p99: ${fmtDur(e.p99Ms)}`,
      })).filter(e => e.si >= 0 && e.ti >= 0);
    } catch (e) {
      body.innerHTML = `<div class="err">${escapeHtml(e.message)}</div>`; return;
    }
  } else {
    const nodeMap = new Map();
    const getNode = id => {
      if (!nodeMap.has(id)) nodeMap.set(id, { id, label: id, errRate: 0, tip: id });
      return nodeMap.get(id);
    };
    for (const s of (result.series || [])) {
      const m = s.name.match(/^(.+?)\s*(?:→|->|>)\s*(.+)$/);
      if (m) {
        getNode(m[1].trim()); getNode(m[2].trim());
        const val = s.ys.length ? s.ys[s.ys.length - 1] : 0;
        edges.push({ fromId: m[1].trim(), toId: m[2].trim(),
          label: fmtNum(val), errRate: 0, tip: `${s.name}: ${fmtNum(val)}` });
      } else { getNode(s.name); }
    }
    nodes = Array.from(nodeMap.values());
    const idx = new Map(nodes.map((n, i) => [n.id, i]));
    edges = edges.map(e => ({ ...e, si: idx.get(e.fromId) ?? -1, ti: idx.get(e.toId) ?? -1 }))
                 .filter(e => e.si >= 0 && e.ti >= 0);
  }

  if (nodes.length === 0) { body.innerHTML = '<div class="empty">no graph data</div>'; return; }

  const W = body.clientWidth  || 400;
  const H = body.clientHeight || 200;
  forceLayout(nodes, edges, W, H);

  body.innerHTML = "";
  const tip = document.createElement("div");
  tip.className = "ng-tip";
  body.appendChild(tip);

  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  svg.setAttribute("width",  W);
  svg.setAttribute("height", H);

  const showTip = (ev, text) => {
    tip.textContent = text; tip.style.display = "block";
    const rect = body.getBoundingClientRect();
    let lx = ev.clientX - rect.left + 14, ty = ev.clientY - rect.top + 14;
    if (lx + 200 > W) lx -= 200; if (ty + 70 > H) ty -= 70;
    tip.style.left = lx + "px"; tip.style.top = ty + "px";
  };
  const hideTip = () => { tip.style.display = "none"; };

  let highlighted = null;
  const edgeEls = [], nodeEls = [];

  // Edges (drawn first so nodes sit on top)
  const NS = "http://www.w3.org/2000/svg";
  for (const e of edges) {
    const a = nodes[e.si], b = nodes[e.ti];
    const hue = Math.round((1 - e.errRate) * 120);
    const col = `hsl(${hue},60%,55%)`;
    const g = document.createElementNS(NS, "g"); g.setAttribute("class", "edge");
    const line = document.createElementNS(NS, "line");
    line.setAttribute("x1", a.x.toFixed(1)); line.setAttribute("y1", a.y.toFixed(1));
    line.setAttribute("x2", b.x.toFixed(1)); line.setAttribute("y2", b.y.toFixed(1));
    line.setAttribute("stroke", col); line.setAttribute("stroke-width", "1.5");
    line.setAttribute("stroke-opacity", "0.6");
    // Arrowhead
    const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy) || 1;
    const ux = dx / len, uy = dy / len;
    const tx = b.x - ux * (R + 4), ty2 = b.y - uy * (R + 4);
    const tri = document.createElementNS(NS, "polygon");
    tri.setAttribute("points",
      `${(tx + ux*7).toFixed(1)},${(ty2 + uy*7).toFixed(1)} ` +
      `${(tx - uy*4).toFixed(1)},${(ty2 + ux*4).toFixed(1)} ` +
      `${(tx + uy*4).toFixed(1)},${(ty2 - ux*4).toFixed(1)}`);
    tri.setAttribute("fill", col);
    g.appendChild(line); g.appendChild(tri);
    if (e.label) {
      const lbl = document.createElementNS(NS, "text");
      lbl.setAttribute("x", ((a.x + b.x) / 2).toFixed(1));
      lbl.setAttribute("y", ((a.y + b.y) / 2).toFixed(1));
      lbl.setAttribute("dy", "-4"); lbl.textContent = e.label;
      g.appendChild(lbl);
    }
    g.addEventListener("mousemove", ev => showTip(ev, e.tip));
    g.addEventListener("mouseleave", hideTip);
    svg.appendChild(g);
    edgeEls.push({ line, tri, e });
  }

  // Nodes
  for (let i = 0; i < nodes.length; i++) {
    const n = nodes[i];
    const g = document.createElementNS(NS, "g"); g.setAttribute("class", "node");
    g.setAttribute("transform", `translate(${n.x.toFixed(1)},${n.y.toFixed(1)})`);
    const circle = document.createElementNS(NS, "circle"); circle.setAttribute("r", R);
    if (n.errRate > 0.05) circle.setAttribute("class", "err");
    const txt = document.createElementNS(NS, "text");
    txt.textContent = n.label.length > 12 ? n.label.slice(0, 11) + "…" : n.label;
    g.appendChild(circle); g.appendChild(txt);
    g.addEventListener("mousemove", ev => showTip(ev, n.tip));
    g.addEventListener("mouseleave", hideTip);
    g.addEventListener("click", () => {
      highlighted = highlighted === n.id ? null : n.id;
      // Dim/highlight edges
      for (const { line, tri, e } of edgeEls) {
        const on = !highlighted || nodes[e.si].id === highlighted || nodes[e.ti].id === highlighted;
        line.setAttribute("stroke-opacity", on ? "0.75" : "0.1");
        tri.setAttribute("opacity", on ? "1" : "0.1");
      }
      // Dim/highlight nodes
      for (const { circle: c, n: nd } of nodeEls) {
        const connected = !highlighted || nd.id === highlighted
          || edges.some(e => (nodes[e.si].id === highlighted && nodes[e.ti].id === nd.id)
                          || (nodes[e.ti].id === highlighted && nodes[e.si].id === nd.id));
        c.setAttribute("opacity", connected ? "1" : "0.25");
        c.classList.toggle("hl", !!highlighted && nd.id === highlighted);
      }
    });
    svg.appendChild(g);
    nodeEls.push({ circle, n });
  }

  body.appendChild(svg);

  if (!body._ngro) {
    body._ngro = new ResizeObserver(() => { if (body.isConnected) renderNodeGraph(body, result, p); });
    body._ngro.observe(body);
  }
}

// ── Canvas (free-form drag-and-drop elements) ─────────────────────────
// Elements stored as JSON array in panel.options.elements.
// Element schema: {id, kind, x, y, w, h, text, color, fill, fontSize,
//                  query, queryLang, unit}
// Kinds: text, metric, rect, ellipse
// Edit mode: elements draggable; delete button on hover; toolbar adds new elements.
async function renderCanvasPanel(body, result, p) {
  let elements;
  try { elements = JSON.parse((p.options || {}).elements || "[]"); }
  catch { elements = []; }

  body.innerHTML = "";

  // Edit-mode toolbar (visibility via CSS .edit-mode .cv-toolbar)
  const toolbar = document.createElement("div");
  toolbar.className = "cv-toolbar";
  ["text", "metric", "rect", "ellipse"].forEach(kind => {
    const btn = document.createElement("button");
    btn.textContent = "+ " + kind.charAt(0).toUpperCase() + kind.slice(1);
    btn.addEventListener("click", () => {
      let els; try { els = JSON.parse((p.options || {}).elements || "[]"); } catch { els = []; }
      const newEl = {
        id:        "cv-" + Math.random().toString(36).slice(2),
        kind,
        x: 16, y: 16 + els.length * 36,
        w: (kind === "rect" || kind === "ellipse") ? 90 : 120,
        h: (kind === "rect" || kind === "ellipse") ? 60 : 36,
        text:      kind === "text" ? "Label" : "",
        color:     "#e6e6e6",
        fill:      (kind === "rect" || kind === "ellipse") ? "#2a3140" : "transparent",
        fontSize:  13, query: "", queryLang: "promql", unit: "",
      };
      els.push(newEl);
      p.options.elements = JSON.stringify(els);
      saveCurrent().catch(() => {});
      body.insertBefore(buildCanvasEl(newEl, p), toolbar);
    });
    toolbar.appendChild(btn);
  });
  body.appendChild(toolbar);

  // Render existing elements
  for (const el of elements) body.insertBefore(buildCanvasEl(el, p), toolbar);

  // Fetch values for metric elements asynchronously
  for (const el of elements.filter(e => e.kind === "metric" && e.query)) {
    (async () => {
      try {
        const now = Date.now(), start = now - 3600_000;
        const r = await runQuery(el.queryLang || "promql", el.query, start, now, 60);
        let val = null;
        if (r.kind === "stat") val = r.value;
        else if (r.series && r.series.length) {
          const s = r.series[0]; val = s.ys.length ? s.ys[s.ys.length - 1] : null;
        }
        const valEl = body.querySelector(`[data-cv-id="${CSS.escape(el.id)}"] .cv-metric-val`);
        if (valEl) valEl.textContent = val == null ? "—" : fmtNum(val) + (el.unit || "");
      } catch {}
    })();
  }
}

function buildCanvasEl(el, p) {
  const div = document.createElement("div");
  div.className = "cv-el cv-" + el.kind;
  div.dataset.cvId = el.id;
  div.style.left   = (el.x || 0) + "px";
  div.style.top    = (el.y || 0) + "px";
  div.style.width  = (el.w || 80) + "px";
  div.style.height = (el.h || 36) + "px";
  if (el.color && el.color !== "transparent") div.style.color      = el.color;
  if (el.fill  && el.fill  !== "transparent") div.style.background = el.fill;
  if (el.fontSize) div.style.fontSize = el.fontSize + "px";
  if (el.kind === "ellipse") div.style.borderRadius = "50%";

  if (el.kind === "text") {
    const span = document.createElement("span");
    span.textContent = el.text || "";
    div.appendChild(span);
  } else if (el.kind === "metric") {
    const val = document.createElement("div"); val.className = "cv-metric-val"; val.textContent = "…";
    const lbl = document.createElement("div"); lbl.className = "cv-metric-lbl";
    lbl.textContent = el.text || el.query || "";
    div.appendChild(val); div.appendChild(lbl);
  }

  // Delete button (visible in edit mode on hover via CSS)
  const del = document.createElement("button");
  del.className = "cv-del"; del.textContent = "×"; del.title = "Remove";
  del.addEventListener("click", e => {
    e.stopPropagation();
    let els; try { els = JSON.parse((p.options || {}).elements || "[]"); } catch { els = []; }
    p.options.elements = JSON.stringify(els.filter(x => x.id !== el.id));
    div.remove();
    saveCurrent().catch(() => {});
  });
  div.appendChild(del);

  // Drag (only active in edit mode)
  let ds = null;
  div.addEventListener("pointerdown", e => {
    if (!state.editMode || e.target === del) return;
    e.stopPropagation(); e.preventDefault();
    div.setPointerCapture(e.pointerId);
    div.classList.add("cv-dragging");
    ds = { startX: e.clientX, startY: e.clientY, ox: el.x || 0, oy: el.y || 0 };
  });
  div.addEventListener("pointermove", e => {
    if (!ds) return;
    el.x = Math.max(0, ds.ox + (e.clientX - ds.startX));
    el.y = Math.max(0, ds.oy + (e.clientY - ds.startY));
    div.style.left = el.x + "px"; div.style.top = el.y + "px";
  });
  div.addEventListener("pointerup", () => {
    if (!ds) return;
    div.classList.remove("cv-dragging"); ds = null;
    let els; try { els = JSON.parse((p.options || {}).elements || "[]"); } catch { els = []; }
    const i = els.findIndex(x => x.id === el.id);
    if (i >= 0) { els[i].x = el.x; els[i].y = el.y; p.options.elements = JSON.stringify(els); }
    saveCurrent().catch(() => {});
  });

  return div;
}

// ── Geomap (Leaflet lazy-loaded from /leaflet.css + /leaflet.js) ──────
// Series names: "lat:lng" or "lat:lng:label". Value = marker size/color.
// opts: lat, lng, zoom, tileUrl, tileAttrib, thresholds, unit,
//       minRadius (4), maxRadius (20)
let _leafletPending = null;
function loadLeaflet() {
  if (window.L) return Promise.resolve(window.L);
  if (_leafletPending) return _leafletPending;
  _leafletPending = new Promise((resolve, reject) => {
    if (!document.querySelector('link[href="/leaflet.css"]')) {
      const lnk = document.createElement("link");
      lnk.rel = "stylesheet"; lnk.href = "/leaflet.css";
      document.head.appendChild(lnk);
    }
    const s = document.createElement("script");
    s.src = "/leaflet.js";
    s.onload  = () => { _leafletPending = null; resolve(window.L); };
    s.onerror = () => { _leafletPending = null;
      reject(new Error("Leaflet not found. Vendor leaflet.js + leaflet.css into src/edge/wwwroot/")); };
    document.head.appendChild(s);
  });
  return _leafletPending;
}

async function renderGeomap(body, result, p) {
  const opts = p.options || {};
  // Parse series names "lat:lng" or "lat:lng:label" into map points.
  const series = (result.series || []).filter(s => s.ys.length);
  const points = series.map((s, i) => {
    const parts = s.name.split(":");
    const lat   = +parts[0], lng = +parts[1];
    if (isNaN(lat) || isNaN(lng)) return null;
    return { lat, lng, label: parts.slice(2).join(":") || s.name,
             val: s.ys[s.ys.length - 1], color: colorFor(i) };
  }).filter(Boolean);

  // Threshold coloring
  const thresholds = [];
  for (const tok of (opts.thresholds || "").split(",")) {
    const [at, name] = tok.split("=");
    if (!isNaN(+at)) thresholds.push({
      at: +at,
      color: name === "err" ? "#f25f5c" : name === "warn" ? "#f0c452" : "#5cd97a",
    });
  }
  thresholds.sort((a, b) => a.at - b.at);
  const ptColor = v => { let c = "#5ec8ff"; for (const t of thresholds) if (v >= t.at) c = t.color; return c; };
  const maxV  = Math.max(...points.map(pt => pt.val), 1);
  const minR  = +(opts.minRadius || 4);
  const maxR  = +(opts.maxRadius || 20);

  // Tear down any previous Leaflet instance on this body.
  if (body._leafletMap) { try { body._leafletMap.remove(); } catch {} body._leafletMap = null; }

  try {
    const L = await loadLeaflet();
    body.innerHTML = "";
    const wrap = document.createElement("div");
    wrap.style.width = "100%"; wrap.style.height = "100%";
    body.appendChild(wrap);

    const map = L.map(wrap, {
      center:      [+(opts.lat || 20), +(opts.lng || 0)],
      zoom:        +(opts.zoom || 2),
      zoomControl: true,
    });
    body._leafletMap = map;

    // Tile layer — user must supply tileUrl; without it, plain grey bg.
    if (opts.tileUrl) {
      L.tileLayer(opts.tileUrl, {
        attribution: opts.tileAttrib || "",
        maxZoom: 19,
      }).addTo(map);
    }

    // Data markers
    for (const pt of points) {
      const r   = minR + (pt.val / maxV) * (maxR - minR);
      const col = thresholds.length ? ptColor(pt.val) : pt.color;
      L.circleMarker([pt.lat, pt.lng], {
        radius: r, color: col, fillColor: col, fillOpacity: 0.7, weight: 1.5,
      }).bindPopup(
        `<strong>${escapeHtml(pt.label)}</strong><br>${fmtNum(pt.val)}${escapeHtml(opts.unit || "")}`
      ).addTo(map);
    }

    if (!body._gmro) {
      body._gmro = new ResizeObserver(() => {
        if (body._leafletMap) body._leafletMap.invalidateSize();
      });
      body._gmro.observe(body);
    }
  } catch (e) {
    body.innerHTML =
      `<div class="err" style="padding:10px;">${escapeHtml(e.message)}<br>
       <span style="color:var(--muted);font-size:11px;">
         Download from <a href="https://leafletjs.com" target="_blank"
         rel="noopener noreferrer">leafletjs.com</a> and place in
         <code>src/edge/wwwroot/</code>
       </span></div>`;
  }
}

// ── Wave A registrations (Phase 12.2) ─────────────────────────────────
PulseBoard.registerPanel({
  type: "bargauge",
  label: "Bar gauge",
  queryShape: "vector",
  render(el, frame, opts, p) { renderBarGauge(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "gauge",
  label: "Gauge",
  queryShape: "scalar",
  render(el, frame, opts, p) { renderGauge(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "piechart",
  label: "Pie / Donut",
  queryShape: "vector",
  render(el, frame, opts, p) { renderPieChart(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "barchart",
  label: "Bar chart",
  queryShape: "vector",
  render(el, frame, opts, p) { renderBarChart(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "histogram",
  label: "Histogram",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderHistogram(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "state-timeline",
  label: "State timeline",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderStateTimeline(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "status-history",
  label: "Status history",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderStatusHistory(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "text",
  label: "Text",
  queryShape: "none",
  render(el, frame, opts, p) { renderTextPanel(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "dashlist",
  label: "Dashboard list",
  queryShape: "none",
  render(el, frame, opts, p) { renderDashList(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "alertlist",
  label: "Alert list",
  queryShape: "none",
  render(el, frame, opts, p) { renderAlertList(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "annolist",
  label: "Annotations list",
  queryShape: "none",
  render(el, frame, opts, p) { renderAnnoList(el, frame, p); },
});

// ── Wave B registrations (Phase 12.2) ─────────────────────────────────
PulseBoard.registerPanel({
  type: "heatmap",
  label: "Heatmap",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderHeatmap(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "trend",
  label: "Trend",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderTrend(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "xychart",
  label: "XY chart",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderXYChart(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "candlestick",
  label: "Candlestick",
  queryShape: "matrix",
  render(el, frame, opts, p) { renderCandlestick(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "traces",
  label: "Traces",
  queryShape: "spans",
  render(el, frame, opts, p) { renderTracesPanel(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "flamegraph",
  label: "Flame graph",
  queryShape: "spans",
  render(el, frame, opts, p) { renderFlameGraph(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "news",
  label: "News",
  queryShape: "none",
  render(el, frame, opts, p) { renderNews(el, frame, p); },
});

// ── Wave C registrations (Phase 12.2) ─────────────────────────────────
PulseBoard.registerPanel({
  type: "nodegraph",
  label: "Node graph",
  queryShape: "nodes",
  render(el, frame, opts, p) { renderNodeGraph(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "canvas",
  label: "Canvas",
  queryShape: "none",
  render(el, frame, opts, p) { renderCanvasPanel(el, frame, p); },
});

PulseBoard.registerPanel({
  type: "geomap",
  label: "Geomap",
  queryShape: "vector",
  render(el, frame, opts, p) { renderGeomap(el, frame, p); },
});

// =====================================================================
// Phase 12.3 — Dashboard Ergonomics helpers
// =====================================================================

// ── 1. Template variables ─────────────────────────────────────────────
// Replace $varName and ${varName} in an expression with the current value
// of the matching dashboard variable.  Multi-select values are joined
// with "|" (Prometheus alternation); "$__all" expands to ".+" (match all).
function applyVars(expr) {
  const vars = (state.current && state.current.vars) || [];
  for (const v of vars) {
    const val = Array.isArray(v.current)
      ? (v.allOption && v.current.includes("$__all") ? ".+" : v.current.join("|"))
      : (v.current === "$__all" ? ".+" : (v.current || ""));
    expr = expr.replace(new RegExp(`\\$\\{${v.name}\\}`, "g"), val);
    expr = expr.replace(new RegExp(`\\$${v.name}(?![a-zA-Z0-9_])`, "g"), val);
  }
  return expr;
}

// Fetch value options for a query-type variable.
// Supports: label_values(metric, label)  label_names()  metrics()
async function fetchVarOptions(query, regex) {
  let values = [];
  const lv = query.match(/^label_values\((.+),\s*([^)]+)\)$/);
  try {
    if (lv) {
      const r = await authFetch(`/api/prom/api/v1/label/${encodeURIComponent(lv[2].trim())}/values`);
      if (r.ok) { const j = await r.json(); values = j.data || []; }
    } else if (/^label_names\(\)$/.test(query)) {
      const r = await authFetch(`/api/prom/api/v1/labels`);
      if (r.ok) { const j = await r.json(); values = j.data || []; }
    } else if (/^metrics\(\)$/.test(query)) {
      const r = await authFetch(`/api/prom/api/v1/label/__name__/values`);
      if (r.ok) { const j = await r.json(); values = j.data || []; }
    }
  } catch { /* leave values empty */ }
  if (regex) {
    try { const re = new RegExp(regex); values = values.filter(v => re.test(v)); } catch {}
  }
  return values;
}

// Build / refresh the variable picker bar below the dashboard toolbar.
async function renderVarsBar(dashboard) {
  const bar = $("vars-bar");
  bar.innerHTML = "";
  const vars = (dashboard.vars || []).filter(v => !v.hide);
  if (!vars.length) { bar.classList.add("hidden"); return; }
  bar.classList.remove("hidden");
  for (const v of vars) {
    const chip = document.createElement("div"); chip.className = "var-chip";
    const lbl  = document.createElement("label"); lbl.textContent = (v.label || v.name) + ":";
    chip.appendChild(lbl);
    if (v.type === "textbox") {
      const inp = document.createElement("input"); inp.type = "text"; inp.value = v.current || "";
      inp.addEventListener("change", () => { v.current = inp.value; refreshAll(); });
      chip.appendChild(inp);
    } else if (v.type === "interval") {
      const sel = document.createElement("select");
      for (const opt of ["1m","5m","15m","30m","1h","6h","24h"]) {
        const o = document.createElement("option"); o.value = opt; o.textContent = opt; sel.appendChild(o);
      }
      sel.value = v.current || "5m";
      sel.addEventListener("change", () => { v.current = sel.value; refreshAll(); });
      chip.appendChild(sel);
    } else {
      let options = [];
      if (v.type === "custom")     options = (v.options || "").split(",").map(s => s.trim()).filter(Boolean);
      else if (v.type === "query") options = await fetchVarOptions(v.query || "", v.regex || "").catch(() => []);
      if (v.allOption) options = ["$__all", ...options];
      const sel = document.createElement("select"); if (v.multi) sel.multiple = true;
      for (const opt of options) {
        const o = document.createElement("option"); o.value = opt;
        o.textContent = opt === "$__all" ? "All" : opt; sel.appendChild(o);
      }
      if (v.multi && Array.isArray(v.current)) {
        for (const o of sel.options) o.selected = v.current.includes(o.value);
      } else if (v.current) {
        sel.value = v.current;
        if (!sel.value && sel.options.length) { sel.selectedIndex = 0; v.current = sel.value; }
      } else if (sel.options.length) { sel.selectedIndex = 0; v.current = sel.value; }
      sel.addEventListener("change", () => {
        v.current = v.multi ? Array.from(sel.selectedOptions).map(o => o.value) : sel.value;
        refreshAll();
      });
      chip.appendChild(sel);
    }
    bar.appendChild(chip);
  }
}

// ── 2. Variables editor modal ─────────────────────────────────────────
function openVarsEditor() {
  if (!state.current) return;
  if (!state.current.vars) state.current.vars = [];
  renderVarsEditorModal();
  $("vars-modal").classList.remove("hidden");
}

function renderVarsEditorModal() {
  const body = $("vars-modal-body");
  body.innerHTML = "";
  const vars = state.current.vars || [];
  if (!vars.length) {
    const p = document.createElement("p");
    p.style.cssText = "color:var(--muted);font-size:12px;padding:8px 0;";
    p.textContent = "No variables yet. Click + Add variable.";
    body.appendChild(p); return;
  }
  // Header row
  const hdr = document.createElement("div"); hdr.className = "var-row";
  hdr.style.fontWeight = "600"; hdr.style.borderBottom = "2px solid var(--border)";
  hdr.innerHTML = "<span>Name</span><span>Type</span><span>Options / Query / Regex</span><span></span>";
  body.appendChild(hdr);
  vars.forEach((v, i) => {
    const row = document.createElement("div"); row.className = "var-row";
    const nameInp = document.createElement("input");
    nameInp.value = v.name || ""; nameInp.placeholder = "var_name";
    nameInp.addEventListener("change", () => { v.name = nameInp.value.replace(/[^a-zA-Z0-9_]/g, ""); });
    const typeSel = document.createElement("select");
    for (const t of ["custom","query","interval","textbox"]) {
      const o = document.createElement("option"); o.value = t; o.textContent = t; typeSel.appendChild(o);
    }
    typeSel.value = v.type || "custom";
    typeSel.addEventListener("change", () => { v.type = typeSel.value; });
    const optInp = document.createElement("input");
    optInp.value = v.query || v.options || ""; optInp.placeholder = "val1,val2 or label_values(m,l)";
    optInp.addEventListener("change", () => {
      if (v.type === "query") v.query = optInp.value; else v.options = optInp.value;
    });
    const del = document.createElement("button"); del.textContent = "✕"; del.className = "danger";
    del.addEventListener("click", () => {
      state.current.vars = state.current.vars.filter((_, j) => j !== i);
      renderVarsEditorModal();
    });
    row.appendChild(nameInp); row.appendChild(typeSel); row.appendChild(optInp); row.appendChild(del);
    body.appendChild(row);
  });
}

// ── 3. Compare-time overlay ───────────────────────────────────────────
// Shows the same query offset by compareOffset seconds (default 7d).
// For series panels: appends the older series with a "(7d ago)" label.
// For stat panels: shows a delta badge.
function formatCompareOffset(sec) {
  if (sec % 86400 === 0) return `${sec / 86400}d ago`;
  if (sec % 3600 === 0)  return `${sec / 3600}h ago`;
  return `${Math.round(sec / 60)}m ago`;
}

async function toggleCompare() {
  state.compareMode = !state.compareMode;
  $("compare-toggle").classList.toggle("btn-active", state.compareMode);
  $("compare-toggle").textContent = state.compareMode ? "\u26ad Compare ON" : "\u26ad Compare";
  await refreshAll();
}

// ── 4. Saved views (per-dashboard, localStorage) ──────────────────────
function savedViewsKey() { return "pb.views." + (state.current && state.current.id || "?"); }
function loadSavedViews() {
  try { return JSON.parse(localStorage.getItem(savedViewsKey()) || "[]"); } catch { return []; }
}
function saveSavedViews(v) { localStorage.setItem(savedViewsKey(), JSON.stringify(v)); }

function updateSavedViewsDropdown() {
  const sel = $("saved-views"); if (!sel) return;
  const cur = sel.value;
  sel.innerHTML = '<option value="">\u2014 Views \u2014</option>';
  for (const v of loadSavedViews()) {
    const o = document.createElement("option"); o.value = v.name; o.textContent = v.name; sel.appendChild(o);
  }
  sel.value = cur;
}

function saveCurrentView() {
  const name = prompt("View name (saves time range + variable values):");
  if (!name) return;
  const views = loadSavedViews();
  const vars = {};
  for (const v of (state.current.vars || [])) vars[v.name] = v.current;
  const entry = { name, timeRangeSec: state.current.timeRangeSec, vars };
  const i = views.findIndex(x => x.name === name);
  if (i >= 0) views[i] = entry; else views.push(entry);
  saveSavedViews(views); updateSavedViewsDropdown();
}

function applyView(name) {
  const v = loadSavedViews().find(x => x.name === name);
  if (!v) return;
  state.current.timeRangeSec = v.timeRangeSec;
  $("time-range").value = String(v.timeRangeSec);
  for (const [vn, val] of Object.entries(v.vars || {})) {
    const vd = (state.current.vars || []).find(x => x.name === vn);
    if (vd) vd.current = val;
  }
  renderVarsBar(state.current).then(() => refreshAll());
}

// ── 5. Version history (client-side ring buffer, last 15 saves) ───────
function pushHistory(dashboard) {
  const snap = JSON.parse(JSON.stringify(dashboard));
  state.history.unshift({ ts: Date.now(), title: dashboard.title || "untitled", snap });
  if (state.history.length > 15) state.history.length = 15;
}

function openHistory() {
  const body = $("hist-body"); body.innerHTML = "";
  $("hist-count").textContent = `(${state.history.length})`;
  if (!state.history.length) {
    body.innerHTML = '<p style="color:var(--muted);font-size:12px;padding:8px 0;">No history yet \u2014 revisions accumulate as you save.</p>';
    $("history-modal").classList.remove("hidden"); return;
  }
  for (let i = 0; i < state.history.length; i++) {
    const h = state.history[i];
    const row = document.createElement("div"); row.className = "rev-row";
    const ts  = document.createElement("span"); ts.className = "rev-ts";
    ts.textContent = new Date(h.ts).toLocaleString();
    const lbl = document.createElement("span"); lbl.className = "rev-lbl";
    lbl.textContent = `${h.title}  \u00b7  ${(h.snap.panels || []).length} panels`;
    const btn = document.createElement("button"); btn.textContent = "Restore";
    btn.addEventListener("click", async () => {
      if (!confirm(`Restore revision from ${ts.textContent}?`)) return;
      try {
        const r = await api("PUT", "/api/dashboards/" + encodeURIComponent(state.current.id), h.snap);
        state.current = r || h.snap;
        await openDashboard(state.current.id);
      } catch (e) { alert("Restore failed: " + e.message); }
      $("history-modal").classList.add("hidden");
    });
    row.appendChild(ts); row.appendChild(lbl); row.appendChild(btn);
    body.appendChild(row);
  }
  $("history-modal").classList.remove("hidden");
}

// ── 6. Sharing — snapshot URL + export/import ─────────────────────────
// Snapshot URL encodes the full dashboard JSON (deflate-raw compressed,
// base64url encoded) in the hash so anyone can view it read-only.
// Falls back to plain base64 on browsers without CompressionStream (<2023).
async function compressToBase64url(str) {
  const bytes = new TextEncoder().encode(str);
  if (typeof CompressionStream !== "undefined") {
    const cs = new CompressionStream("deflate-raw");
    const w = cs.writable.getWriter(); w.write(bytes); w.close();
    const chunks = []; const r = cs.readable.getReader();
    for (;;) { const { done, value } = await r.read(); if (done) break; chunks.push(value); }
    const out = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
    let p = 0; for (const c of chunks) { out.set(c, p); p += c.length; }
    return btoa(String.fromCharCode(...out)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=/g, "");
  }
  return btoa(unescape(encodeURIComponent(str))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=/g, "");
}

async function decompressFromBase64url(b64) {
  const raw = b64.replace(/-/g, "+").replace(/_/g, "/");
  const bin = atob(raw);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  if (typeof DecompressionStream !== "undefined") {
    try {
      const ds = new DecompressionStream("deflate-raw");
      const w = ds.writable.getWriter(); w.write(bytes); w.close();
      const chunks = []; const r = ds.readable.getReader();
      for (;;) { const { done, value } = await r.read(); if (done) break; chunks.push(value); }
      const out = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
      let p = 0; for (const c of chunks) { out.set(c, p); p += c.length; }
      return new TextDecoder().decode(out);
    } catch { /* fall through to plain base64 */ }
  }
  return decodeURIComponent(escape(atob(raw)));
}

function openShare() {
  if (!state.current) return;
  $("share-url-inp").value = ""; $("share-embed-inp").value = ""; $("share-import-ta").value = "";
  $("share-modal").classList.remove("hidden");
}

// Handles #/snapshot/... in the router — renders a read-only snapshot.
async function loadSnapshot(encoded) {
  try {
    const json = await decompressFromBase64url(encoded);
    state.current = JSON.parse(json);
    state.current.id = state.current.id || "snapshot";
    showView("dashboards");
    $("dash-grid").innerHTML = ""; state.panels.clear();
    $("time-range").value = String(state.current.timeRangeSec || 3600);
    await renderVarsBar(state.current);
    for (const p of state.current.panels || []) await renderPanel(p);
    // Hide CRUD controls in read-only snapshot mode.
    for (const id of ["dash-new","dash-rename","dash-delete","save-dash","edit-toggle"])
      $(id).classList.add("hidden");
    const note = document.createElement("span");
    note.style.cssText = "font-size:11px;color:var(--muted);margin-right:8px;";
    note.textContent = "\uD83D\uDCF8 Snapshot: " + (state.current.title || "Untitled");
    $("dash-picker").insertAdjacentElement("afterend", note);
  } catch (e) { alert("Failed to load snapshot: " + e.message); location.hash = "#/dashboards"; }
}

// ── 7. Live mode (WS-driven sub-second refresh) ───────────────────────
function toggleLive() {
  state.liveMode = !state.liveMode;
  $("live-toggle").classList.toggle("btn-active", state.liveMode);
  $("live-toggle").textContent = state.liveMode ? "\u29bf Live ON" : "\u29bf Live";
  if (state.liveMode) {
    if (state.refreshTimer) { clearInterval(state.refreshTimer); state.refreshTimer = null; }
  } else { scheduleRefresh(); }
}

// ── 8. Drilldown links ────────────────────────────────────────────────
// Resolves link URL template tokens:
//   ${__value}   last numeric value of the panel series
//   ${__series}  series/metric name
//   ${__from}    range start epoch ms
//   ${__to}      range end epoch ms
//   ${__dash}    current dashboard id
//   $varName     any template variable
function resolveLink(url, seriesName, value) {
  const now  = Date.now();
  const from = now - ((state.current && state.current.timeRangeSec) || 3600) * 1000;
  url = url
    .replace(/\$\{__value\}/g,  value   != null  ? encodeURIComponent(String(value)) : "")
    .replace(/\$\{__series\}/g, seriesName        ? encodeURIComponent(seriesName)   : "")
    .replace(/\$\{__from\}/g,   String(from))
    .replace(/\$\{__to\}/g,     String(now))
    .replace(/\$\{__dash\}/g,   (state.current && state.current.id) || "");
  for (const v of ((state.current && state.current.vars) || [])) {
    const val = Array.isArray(v.current) ? v.current.join(",") : (v.current || "");
    url = url.replace(new RegExp(`\\$\\{${v.name}\\}`, "g"), encodeURIComponent(val));
    url = url.replace(new RegExp(`\\$${v.name}(?![a-zA-Z0-9_])`, "g"), encodeURIComponent(val));
  }
  return url;
}

function parseLinks(text) {
  return (text || "").split("\n").map(line => {
    const p = line.split("|"); if (p.length < 2) return null;
    return { title: p[0].trim(), url: p[1].trim(), newTab: p[2]?.trim() !== "false" };
  }).filter(Boolean);
}

// =====================================================================
// Dashboard loading + persistence
// =====================================================================
function pbBearer() { return sessionStorage.getItem("pb.bearer"); }
function pbAuthHeaders(extra) {
  const h = Object.assign({}, extra || {});
  const tok = pbBearer();
  if (tok) h["Authorization"] = "Bearer " + tok;
  return h;
}
function pbRedirectSignin() {
  sessionStorage.removeItem("pb.bearer");
  const back = location.pathname + location.search + location.hash;
  location.href = "/signin?returnTo=" + encodeURIComponent(back);
}
async function authFetch(url, opts) {
  opts = opts || {};
  opts.headers = pbAuthHeaders(opts.headers);
  const r = await fetch(url, opts);
  if (r.status === 401 || r.status === 403) {
    pbRedirectSignin();
    throw new Error("unauthorized");
  }
  return r;
}

async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  opts.headers = pbAuthHeaders(opts.headers);
  const r = await fetch(path, opts);
  if (r.status === 204) return null;
  if (r.status === 401 || r.status === 403) {
    pbRedirectSignin();
    throw new Error("unauthorized");
  }
  if (!r.ok) {
    let msg = r.statusText;
    try { msg = (await r.json()).error || msg; } catch {}
    throw new Error(msg);
  }
  const ct = r.headers.get("content-type") || "";
  return ct.includes("json") ? r.json() : r.text();
}

async function reloadList(selectId) {
  const list = await api("GET", "/api/dashboards");
  state.dashboards = list;
  const sel = $("dash-picker");
  sel.innerHTML = "";
  for (const d of list) {
    const o = document.createElement("option");
    o.value = d.id; o.textContent = d.title;
    sel.appendChild(o);
  }
  const target = selectId || (state.current && state.current.id) || (list[0] && list[0].id);
  if (target) { sel.value = target; await openDashboard(target); }
}

async function openDashboard(id) {
  const d = await api("GET", "/api/dashboards/" + encodeURIComponent(id));
  state.current = d;
  for (const cached of state.panels.values()) {
    if (cached.uplot) { try { cached.uplot.destroy(); } catch {} }
  }
  state.panels.clear();
  $("dash-grid").innerHTML = "";
  $("time-range").value = String(d.timeRangeSec);
  $("refresh-int").value = String(d.refreshSec);
  await renderVarsBar(d);
  updateSavedViewsDropdown();
  for (const p of d.panels) await renderPanel(p);
  scheduleRefresh();
}

async function refreshAll() {
  if (!state.current) return;
  // Reentrancy guard: panels render sequentially, but if a refresh tick
  // (or a WS-driven nudge) fires while we're still drawing, we'd stack
  // up overlapping passes that hammer /api/metrics. Coalesce instead:
  // remember that another pass was requested and run it exactly once
  // after the current one finishes.
  if (state._refreshing) { state._refreshPending = true; return; }
  state._refreshing = true;
  try {
    do {
      state._refreshPending = false;
      for (const p of state.current.panels) await renderPanel(p);
    } while (state._refreshPending);
  } finally {
    state._refreshing = false;
  }
}

function scheduleRefresh() {
  if (state.refreshTimer) { clearInterval(state.refreshTimer); state.refreshTimer = null; }
  if (state.liveMode) return;  // WS events drive refreshes in live mode
  const sec = +$("refresh-int").value;
  if (sec > 0) state.refreshTimer = setInterval(refreshAll, sec * 1000);
}

async function saveCurrent() {
  if (!state.current) return;
  pushHistory(state.current);
  const saved = await api("PUT", "/api/dashboards/" + encodeURIComponent(state.current.id),
                          state.current);
  state.current = saved;
  // Update the picker label in-place — no need to refetch/re-render all panels.
  const opt = $('dash-picker').querySelector(`option[value="${CSS.escape(saved.id)}"]`);
  if (opt) opt.textContent = saved.title;
}

// =====================================================================
// Editor drawer — display-options helpers
// =====================================================================

// Option keys that are managed by the structured Display-options section.
// These are kept out of the raw `ed-opts` textarea to avoid duplication.
const DISPLAY_OPT_KEYS = new Set([
  "style","interpolation","lineStyle","lineWidth","fill","legend","yMin","yMax","unit",
]);
// Per-series color keys match /^color\d+$/.
function isDisplayKey(k) { return DISPLAY_OPT_KEYS.has(k) || /^color\d+$/.test(k); }

// Returns a uPlot axis `size` function that measures the widest formatted
// tick label using canvas measureText so the Y-axis is never truncated.
function makeYAxisSize(unitFmt) {
  const ctx = document.createElement('canvas').getContext('2d');
  ctx.font = '12px system-ui, sans-serif';
  return (_u, vals) => {
    if (!vals || vals.length === 0) return 50;
    const maxW = Math.max(...vals.map(v => ctx.measureText(v).width));
    return Math.ceil(maxW) + 24; // 24 = tick(6) + gap(4) + padding(14)
  };
}

// Show or hide the Display-options section based on the current panel type.
function syncEdDisplay(panelType) {
  const show = PLOT_PANEL_TYPES.has(panelType || $("ed-type").value);
  $("ed-display").style.display = show ? "" : "none";
}

// Populate the Display-options fields from a panel options map.
// Also rebuilds the per-series color rows based on the series already
// in the live rendered chart (if any), falling back to the options map.
function loadPlotFields(opts, panelId) {
  const o = opts || {};

  // Unit: check if the value matches a known key or is custom.
  const unitEl  = $("ed-unit");
  const unitVal = o.unit || "";
  const knownUnits = Array.from(unitEl.options).map(opt => opt.value);
  if (knownUnits.includes(unitVal)) {
    unitEl.value = unitVal;
    $("ed-unit-custom-wrap").style.display = "none";
  } else if (unitVal) {
    unitEl.value = "custom";
    $("ed-unit-custom").value = unitVal;
    $("ed-unit-custom-wrap").style.display = "";
  } else {
    unitEl.value = "";
    $("ed-unit-custom-wrap").style.display = "none";
  }

  $("ed-style").value     = o.style       || "lines";
  $("ed-interp").value    = o.interpolation || "linear";
  $("ed-linestyle").value = o.lineStyle   || "solid";
  $("ed-legend").value    = o.legend      || "";
  $("ed-linewidth").value = o.lineWidth   || "";
  $("ed-fill").value      = o.fill        || "";
  $("ed-ymin").value      = o.yMin        != null ? o.yMin : "";
  $("ed-ymax").value      = o.yMax        != null ? o.yMax : "";

  // Per-series color rows — derive series names from live chart if possible.
  const cached  = panelId ? (state.panels.get(panelId) || {}) : {};
  const uplot   = cached.uplot;
  const seriesNames = uplot && uplot.series
    ? uplot.series.slice(1).map(s => s.label || "")
    : [];
  rebuildColorRows(seriesNames, o);
}

// Build the per-series color picker rows inside #ed-colors-rows.
function rebuildColorRows(seriesNames, opts) {
  const container = $("ed-colors-rows");
  container.innerHTML = "";
  if (seriesNames.length === 0) {
    container.innerHTML = '<span style="font-size:11px;color:var(--muted);">Preview the panel first to see series</span>';
    return;
  }
  seriesNames.forEach((name, i) => {
    const currentColor = opts["color" + i] || colorFor(i);
    const row = document.createElement("div");
    row.className = "ed-color-row";
    // Color swatch + hex text input pair — kept in sync with each other.
    row.innerHTML =
      `<label title="${escapeHtml(name)}">${escapeHtml(name.length > 22 ? name.slice(0,21)+"…" : name)}</label>` +
      `<input type="color" id="ed-color-swatch-${i}" value="${escapeHtml(currentColor.startsWith("#") ? currentColor : colorFor(i))}" />` +
      `<input type="text"  id="ed-color-hex-${i}"   value="${escapeHtml(currentColor)}" placeholder="${colorFor(i)}" style="width:76px;" />`;
    container.appendChild(row);
    const swatch = row.querySelector(`#ed-color-swatch-${i}`);
    const hex    = row.querySelector(`#ed-color-hex-${i}`);
    swatch.addEventListener("input", () => { hex.value = swatch.value; });
    hex.addEventListener("input", () => {
      if (/^#[0-9a-fA-F]{6}$/.test(hex.value)) swatch.value = hex.value;
    });
  });
}

// Collect the current Display-options field values into a partial opts map.
function collectPlotFields() {
  const out = {};
  const unitSel = $("ed-unit").value;
  if (unitSel === "custom") {
    const custom = $("ed-unit-custom").value.trim();
    if (custom) out.unit = custom;
  } else if (unitSel) {
    out.unit = unitSel;
  }

  const style = $("ed-style").value;
  if (style && style !== "lines") out.style = style;
  else if (style) out.style = style;  // always write so reset works

  const interp = $("ed-interp").value;
  if (interp && interp !== "linear") out.interpolation = interp;
  else if (interp) out.interpolation = interp;

  const ls = $("ed-linestyle").value;
  if (ls && ls !== "solid") out.lineStyle = ls;
  else if (ls) out.lineStyle = ls;

  const legend = $("ed-legend").value;
  if (legend) out.legend = legend;

  const lw = $("ed-linewidth").value.trim();
  if (lw) out.lineWidth = lw;

  const fill = $("ed-fill").value.trim();
  if (fill) out.fill = fill;

  const ymin = $("ed-ymin").value.trim();
  if (ymin !== "") out.yMin = ymin;

  const ymax = $("ed-ymax").value.trim();
  if (ymax !== "") out.yMax = ymax;

  // Per-series color keys.
  const container = $("ed-colors-rows");
  container.querySelectorAll("input[type=text][id^='ed-color-hex-']").forEach(inp => {
    const idx = inp.id.replace("ed-color-hex-", "");
    const val = inp.value.trim();
    if (val) out["color" + idx] = val;
  });

  return out;
}

// =====================================================================
// Editor drawer
// =====================================================================
function openEditor(panelId) {
  state.editingPanel = panelId;
  const p = state.current.panels.find(x => x.id === panelId);
  if (!p) return;
  $("editor-title").textContent = "Edit panel · " + p.title;
  $("ed-title").value = p.title;
  $("ed-type").value  = p.type;
  $("ed-lang").value  = p.queryLang;
  $("ed-expr").value  = p.expr;
  $("ed-w").value     = p.w;
  $("ed-h").value     = p.h;
  // Raw opts textarea: omit keys handled by the structured display section.
  const rawOpts = Object.entries(p.options || {})
    .filter(([k]) => !isDisplayKey(k))
    .map(([k, v]) => `${k}=${v}`)
    .join("\n");
  $("ed-opts").value  = rawOpts;
  $("ed-links").value = (p.links || []).map(lk => `${lk.title}|${lk.url}`).join("\n");
  $("ed-preview").innerHTML = "";
  syncEdDisplay(p.type);
  loadPlotFields(p.options || {}, panelId);
  $("editor").classList.add("open");
}

function closeEditor() {
  $("editor").classList.remove("open");
  state.editingPanel = null;
}

function parseOpts(text) {
  const out = {};
  for (const line of (text || "").split("\n")) {
    const i = line.indexOf("=");
    if (i <= 0) continue;
    out[line.slice(0, i).trim()] = line.slice(i + 1).trim();
  }
  return out;
}

function readEditorPanel() {
  const id = state.editingPanel;
  const base = state.current.panels.find(x => x.id === id);
  // Merge: raw textarea opts + structured display-options fields.
  // The display section only contributes keys when its panel type is shown.
  const rawOpts     = parseOpts($("ed-opts").value);
  const displayOpts = PLOT_PANEL_TYPES.has($("ed-type").value) ? collectPlotFields() : {};
  return {
    ...base,
    title:     $("ed-title").value || "untitled",
    type: $("ed-type").value,
    queryLang: $("ed-lang").value,
    expr:      $("ed-expr").value,
    w:         Math.max(1, Math.min(12, +$("ed-w").value || base.w)),
    h:         Math.max(1, +$("ed-h").value || base.h),
    options:   { ...rawOpts, ...displayOpts },
    links:     parseLinks($("ed-links").value),
  };
}

async function applyEditor() {
  const next = readEditorPanel();
  state.current.panels = state.current.panels.map(p => p.id === next.id ? next : p);
  await renderPanel(next);
  closeEditor();
  await saveCurrent();
}

async function previewEditor() {
  const p = readEditorPanel();
  const host = $("ed-preview");
  host.innerHTML = '<div class="empty" style="padding:8px;">loading…</div>';
  const now = Date.now();
  const start = now - state.current.timeRangeSec * 1000;
  const step  = Math.max(15, Math.floor(state.current.timeRangeSec / 60));
  try {
    const r = await runQuery(p.queryLang, p.expr, start, now, step);
    host.innerHTML = "";
    const def = PulseBoard.getPanel(p.type);
    if (def) {
      def.render(host, r, p.options || {}, p);
    } else {
      renderTimeseries(host, r, p);
    }
    // Refresh per-series color rows now that we know the series names.
    if (PLOT_PANEL_TYPES.has(p.type) && r.series) {
      rebuildColorRows(r.series.map(s => s.name), p.options || {});
    }
  } catch (err) {
    host.innerHTML = `<div class="err" style="padding:8px;">${err.message}</div>`;
  }
}

async function deleteEditorPanel() {
  if (!state.editingPanel) return;
  if (!confirm("Delete this panel?")) return;
  state.current.panels = state.current.panels.filter(p => p.id !== state.editingPanel);
  closeEditor();
  await openDashboard(state.current.id);  // re-render
  await saveCurrent();
}

function addPanel() {
  if (!state.current) {
    alert("Open or create a dashboard first.");
    return;
  }
  // Find first free row at the bottom.
  const maxY = state.current.panels.reduce((m, p) => Math.max(m, p.y + p.h), 0);
  const p = {
    id: "p-" + uuid(),
    title: "New panel",
    type: "timeseries",
    queryLang: "promql",
    expr: "",
    x: 0, y: maxY, w: 6, h: 3,
    options: {},
  };
  state.current.panels.push(p);
  renderPanel(p);
  openEditor(p.id);
}

// =====================================================================
// Drag + resize (edit mode)
// =====================================================================
let drag = null;

function gridUnitPx() {
  const grid = $("dash-grid");
  const cols = 12;
  const style = getComputedStyle(grid);
  const gap = parseFloat(style.gap) || 10;
  const colW = (grid.clientWidth - (cols - 1) * gap - 20) / cols;
  const rowH = parseFloat(style.gridAutoRows) || 70;
  return { colW: colW + gap, rowH: rowH + gap };
}

$("dash-grid").addEventListener("pointerdown", (e) => {
  if (!state.editMode) return;
  const panelEl = e.target.closest(".panel");
  if (!panelEl) return;
  const id = panelEl.dataset.id;
  const p = state.current.panels.find(x => x.id === id);
  if (!p) return;
  const isResize = e.target.classList.contains("resize");
  if (!isResize && (e.target.closest("button") || e.target.closest(".body"))) return;
  e.preventDefault();
  panelEl.setPointerCapture(e.pointerId);
  panelEl.classList.add("dragging");
  drag = {
    p, panelEl, mode: isResize ? "resize" : "move",
    startX: e.clientX, startY: e.clientY,
    startGx: p.x, startGy: p.y, startGw: p.w, startGh: p.h,
    units: gridUnitPx(),
  };
});

window.addEventListener("pointermove", (e) => {
  if (!drag) return;
  const dx = Math.round((e.clientX - drag.startX) / drag.units.colW);
  const dy = Math.round((e.clientY - drag.startY) / drag.units.rowH);
  const p = drag.p;
  if (drag.mode === "move") {
    p.x = Math.max(0, Math.min(12 - p.w, drag.startGx + dx));
    p.y = Math.max(0, drag.startGy + dy);
  } else {
    p.w = Math.max(1, Math.min(12 - p.x, drag.startGw + dx));
    p.h = Math.max(1, drag.startGh + dy);
  }
  placePanel(drag.panelEl, p);
  // Keep uPlot sized to the new dimensions live.
  const cached = state.panels.get(p.id);
  if (cached && cached.uplot) {
    const body = drag.panelEl.querySelector(".body");
    try { cached.uplot.setSize({ width: body.clientWidth, height: body.clientHeight }); } catch {}
  }
});

window.addEventListener("pointerup", async (e) => {
  if (!drag) return;
  drag.panelEl.classList.remove("dragging");
  try { drag.panelEl.releasePointerCapture(e.pointerId); } catch {}
  await saveCurrent();
  drag = null;
});

// =====================================================================
// Explore view
// =====================================================================
let explorePlot = null;
async function runExplore() {
  const lang  = $("explore-lang").value;
  const expr  = $("explore-expr").value.trim();
  const range = +$("explore-range").value;
  if (!expr) return;
  const meta = $("explore-meta");
  const out  = $("explore-out");
  meta.textContent = "running…";
  out.innerHTML = "";
  if (explorePlot) { try { explorePlot.destroy(); } catch {} explorePlot = null; }
  const now = Date.now(), start = now - range * 1000;
  const step = Math.max(15, Math.floor(range / 240));
  try {
    const t0 = performance.now();
    const r  = await runQuery(lang, expr, start, now, step);
    const dt = (performance.now() - t0).toFixed(0);
    if (r.kind === "logs") {
      meta.textContent = `${r.entries.length} log lines · ${dt}ms`;
      renderLogs(out, r, { options: { tail: "1000" } });
    } else if (r.kind === "stat") {
      meta.textContent = `stat · ${dt}ms`;
      renderStat(out, r, { options: {} });
    } else {
      const total = r.series.reduce((n, s) => n + s.xs.length, 0);
      meta.textContent = `${r.series.length} series · ${total} samples · ${dt}ms`;
      const host = document.createElement("div");
      host.style.height = "60vh"; host.style.background = "var(--panel)";
      host.style.borderRadius = "6px"; host.style.padding = "8px";
      out.appendChild(host);
      renderTimeseries(host, r, { type: "timeseries", id: "explore", options: {} });
    }
  } catch (err) {
    meta.textContent = "error";
    out.innerHTML = `<div class="err" style="padding:8px;">${err.message}</div>`;
  }
}

// =====================================================================
// End-to-end correlation (PLAN-NEXT 14.4)
// =====================================================================
// One place that ties metrics ↔ logs ↔ traces together:
//   • gotoExplore       — programmatically open the Explore tab on a query
//   • panel right-click  — "show logs for this spike" (metric → logs)
//   • trace span link    — "metrics →" (trace → metrics)
//   • openCorrelation    — alert detail: top logs + slowest trace
//   • fetchExemplars     — trace exemplars for histogram/heatmap panels

// Recognised service label keys, mirroring Correlation.deriveService server-side.
const SERVICE_LABEL_KEYS = ["service","service_name","service.name","job","app",
                            "application","container","deployment","pod"];

// Parse `metric{a="b",service="x"}` → { __name__, a, service, … }.
function parseSeriesLabels(name) {
  const out = {};
  if (!name) return out;
  const brace = name.indexOf("{");
  if (brace < 0) { out.__name__ = name.trim(); return out; }
  out.__name__ = name.slice(0, brace).trim();
  let inner = name.slice(brace + 1);
  if (inner.endsWith("}")) inner = inner.slice(0, -1);
  // key="value" pairs, tolerant of escaped quotes.
  const re = /([A-Za-z_][\w.]*)\s*=\s*"((?:[^"\\]|\\.)*)"/g;
  let m;
  while ((m = re.exec(inner)) !== null) out[m[1]] = m[2].replace(/\\"/g, '"');
  return out;
}

// Best-effort service name from a series label-set (or a panel expr fallback).
function deriveServiceFromLabels(labels) {
  for (const k of SERVICE_LABEL_KEYS) {
    if (labels[k] && labels[k].trim()) return labels[k].trim();
  }
  return null;
}

// Best-effort service name from a raw query expression, e.g.
//   histogram_quantile(0.9, rate(http_dur_bucket{service="checkout"}[5m]))
//   {service="checkout"} |= "error"
function deriveServiceFromExpr(expr) {
  if (!expr) return null;
  for (const k of SERVICE_LABEL_KEYS) {
    const re = new RegExp(k.replace(/[.]/g, "\\.") + '\\s*=~?\\s*"([^"]+)"');
    const m = expr.match(re);
    if (m && m[1] && !m[1].includes("|") && !/[.+*]/.test(m[1])) return m[1];
  }
  return null;
}

// Switch to Explore and run a query. rangeSec optional (defaults to current).
function gotoExplore(lang, expr, rangeSec) {
  $("explore-lang").value = lang;
  $("explore-expr").value = expr;
  if (rangeSec) {
    // Snap to the closest preset ≥ requested range so the dropdown stays valid.
    const opts = Array.from($("explore-range").options).map(o => +o.value);
    const pick = opts.filter(v => v >= rangeSec).sort((a,b)=>a-b)[0] || opts[opts.length-1];
    $("explore-range").value = String(pick);
  }
  location.hash = "#/explore";
  showView("explore");
  runExplore();
}

// Open Explore showing logs for a service around a spike timestamp.
function showLogsForSpike(service, spikeMs) {
  const expr = service ? `{service="${service}"}` : `{service=~".+"}`;
  // Window the Explore range so the spike sits inside it (use 15m around).
  gotoExplore("logql", expr, 900);
  if (spikeMs) {
    $("explore-meta").textContent =
      `logs near ${new Date(spikeMs).toISOString().substr(11,8)}` +
      (service ? ` for ${service}` : "");
  }
}

// Open Explore showing the rate/throughput-style metrics for a service.
function showMetricsForService(service) {
  if (!service) return;
  // Prefer a labelled selector; the embedded PromQL surface matches on labels.
  gotoExplore("promql", `{service="${service}"}`, 3600);
}

// ── Panel right-click context menu ───────────────────────────────────
// Attached to every panel body; derives a service from the panel's series
// and offers "show logs for this spike" + "show metrics for this service".
const _corrCtx = () => document.getElementById("corr-ctx");

function hideCorrCtx() { const m = _corrCtx(); if (m) m.classList.remove("open"); }
document.addEventListener("click", (e) => {
  if (!e.target.closest("#corr-ctx")) hideCorrCtx();
});
window.addEventListener("blur", hideCorrCtx);
window.addEventListener("scroll", hideCorrCtx, true);

// Collect the distinct candidate services for a panel from its cached frame.
function panelServices(panelId) {
  const cache = state.panels.get(panelId);
  const set = new Set();
  // uPlot series labels live on the live plot; fall back to the dom legend.
  const plot = cache && cache.uplot;
  if (plot && plot.series) {
    for (let i = 1; i < plot.series.length; i++) {
      const svc = deriveServiceFromLabels(parseSeriesLabels(plot.series[i].label || ""));
      if (svc) set.add(svc);
    }
  }
  // Fall back to a service named explicitly in the panel's query expression.
  if (set.size === 0) {
    const def = (state.current.panels || []).find(p => p.id === panelId);
    const svc = def && deriveServiceFromExpr(def.expr);
    if (svc) set.add(svc);
  }
  return Array.from(set);
}

function showPanelContextMenu(ev, p, spikeMs) {
  ev.preventDefault();
  const menu = _corrCtx();
  if (!menu) return;
  const services = panelServices(p.id);
  let html = `<div class="ctx-sub">Correlate (${spikeMs ? new Date(spikeMs).toISOString().substr(11,8) : "panel"})</div>`;
  if (services.length) {
    for (const svc of services) {
      html += `<button data-act="logs" data-svc="${escapeHtml(svc)}">Show logs for “${escapeHtml(svc)}” spike</button>`;
      html += `<button data-act="metrics" data-svc="${escapeHtml(svc)}">Show metrics for “${escapeHtml(svc)}”</button>`;
    }
  } else {
    html += `<button data-act="logs" data-svc="">Show logs in this window</button>`;
  }
  menu.innerHTML = html;
  menu.querySelectorAll("button").forEach(btn => {
    btn.addEventListener("click", () => {
      const svc = btn.getAttribute("data-svc") || null;
      if (btn.getAttribute("data-act") === "logs") showLogsForSpike(svc, spikeMs);
      else showMetricsForService(svc);
      hideCorrCtx();
    });
  });
  // Position within the viewport.
  menu.style.left = Math.min(ev.clientX, window.innerWidth - 240) + "px";
  menu.style.top  = Math.min(ev.clientY, window.innerHeight - 120) + "px";
  menu.classList.add("open");
}

// Wire a panel body for right-click correlation. Called from panelChrome.
function wirePanelCorrelation(el, p) {
  const body = el.querySelector(".body");
  if (!body || body._corrWired) return;
  body._corrWired = true;
  body.addEventListener("contextmenu", (ev) => {
    if (state.editMode) return;          // don't hijack while editing layout
    // Map the cursor x back to a timestamp via the live uPlot, if any.
    let spikeMs = null;
    const cache = state.panels.get(p.id);
    const plot = cache && cache.uplot;
    if (plot && typeof plot.posToVal === "function") {
      try {
        const rect = plot.over.getBoundingClientRect();
        const xVal = plot.posToVal(ev.clientX - rect.left, "x");
        if (Number.isFinite(xVal)) spikeMs = Math.round(xVal * 1000);
      } catch { /* ignore */ }
    }
    showPanelContextMenu(ev, p, spikeMs);
  });
}

// ── Alert correlation detail ─────────────────────────────────────────
window.openCorrelation = async function (fp, name) {
  $("corr-modal-title").textContent = "Correlated signals — " + (name || fp);
  $("corr-modal-body").innerHTML = "<div class='corr-empty'>loading…</div>";
  $("corr-modal").classList.add("open");
  try {
    const r = await authFetch("/api/alerts/" + encodeURIComponent(fp) + "/correlation");
    if (r.status === 404) {
      $("corr-modal-body").innerHTML =
        "<div class='corr-empty'>No correlated signals captured for this alert yet.</div>";
      return;
    }
    if (!r.ok) throw new Error("HTTP " + r.status);
    renderCorrelation(await r.json());
  } catch (e) {
    $("corr-modal-body").innerHTML =
      "<div class='corr-empty'>error: " + escapeHtml(e.message) + "</div>";
  }
};

function renderCorrelation(snap) {
  const body = $("corr-modal-body");
  const svc = snap.service ? escapeHtml(snap.service) : "all services";
  const win = `${new Date(snap.fromMs).toISOString().substr(11,8)}–${new Date(snap.toMs).toISOString().substr(11,8)}`;
  const logs = snap.logs || [];
  const logRows = logs.length
    ? logs.map(e => {
        const lvl = (e.level || "info").toLowerCase();
        return `<div class="corr-log">
          <span class="lvl ${escapeHtml(lvl)}">${escapeHtml(e.level || "log")}</span>
          <span class="msg">${escapeHtml(e.message)}</span>
          <span class="ts">${new Date(e.ts).toISOString().substr(11,12)}</span>
        </div>`;
      }).join("")
    : `<div class="corr-empty">No logs in the breach window.</div>`;

  let traceHtml;
  const t = snap.slowestTrace;
  if (t && t.summary) {
    const s = t.summary;
    traceHtml = `<div style="font-size:12px;line-height:1.6;">
        <span class="corr-trace-link" data-trace="${escapeHtml(s.traceId)}">${escapeHtml(s.traceId.substring(0,16))}…</span>
        — ${escapeHtml(s.rootService)} · ${escapeHtml(s.rootOperation)}<br/>
        ${fmtDur(s.durationMs)} · ${s.spanCount} spans ·
        <span style="${s.errorCount>0?"color:#ff7a7a;":""}">${s.errorCount} error${s.errorCount===1?"":"s"}</span> ·
        services: ${(s.services||[]).map(escapeHtml).join(", ")}
      </div>`;
  } else {
    traceHtml = `<div class="corr-empty">No trace touched this service in the window.</div>`;
  }

  body.innerHTML = `
    <div class="corr-section">
      <div style="font-size:11px;color:var(--muted);margin-bottom:8px;">
        ${svc} · window ${win}
        <button id="corr-explore-logs" style="font-size:10px;padding:1px 6px;margin-left:8px;">Open in Explore →</button>
      </div>
    </div>
    <div class="corr-section">
      <h4>Top log lines</h4>
      ${logRows}
    </div>
    <div class="corr-section">
      <h4>Slowest trace</h4>
      ${traceHtml}
    </div>`;

  const exploreBtn = $("corr-explore-logs");
  if (exploreBtn) exploreBtn.addEventListener("click", () => {
    $("corr-modal").classList.remove("open");
    showLogsForSpike(snap.service, snap.fromMs);
  });
  const traceLink = body.querySelector(".corr-trace-link");
  if (traceLink) traceLink.addEventListener("click", () => {
    $("corr-modal").classList.remove("open");
    openTrace(traceLink.getAttribute("data-trace"));
  });
}

$("corr-modal-close").addEventListener("click", () => $("corr-modal").classList.remove("open"));
$("corr-modal").addEventListener("click", (e) => {
  if (e.target === $("corr-modal")) $("corr-modal").classList.remove("open");
});

// ── Exemplars (PLAN-NEXT 14.4) ───────────────────────────────────────
// Trace exemplars for a (service, window) — surfaced on histogram/heatmap
// panels by default (no exemplar-ingest path or opt-in config required).
async function fetchExemplars(service, fromMs, toMs, limit) {
  const params = new URLSearchParams();
  if (service) params.set("service", service);
  if (fromMs)  params.set("fromMs", String(Math.round(fromMs)));
  if (toMs)    params.set("toMs", String(Math.round(toMs)));
  params.set("limit", String(limit || 100));
  try {
    const r = await authFetch("/api/exemplars?" + params.toString());
    if (!r.ok) return [];
    return await r.json();
  } catch { return []; }
}


state.liveMetrics = new Map();
state.liveAlerts  = 0;
function connectWs() {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const ws = new WebSocket(`${proto}://${location.host}/ws`);
  state.ws = ws;
  ws.onopen  = () => { $("dot").classList.add("live");  $("conn").textContent = "live"; };
  ws.onclose = () => { $("dot").classList.remove("live"); $("conn").textContent = "reconnecting…";
                       setTimeout(connectWs, 1500); };
  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === "metric") state.liveMetrics.set(msg.name, msg.value);
    else if (msg.type === "alert") state.liveAlerts++;
    // Live mode: debounce a full dashboard refresh when WS events arrive.
    if (state.liveMode) {
      clearTimeout(state._liveTmo);
      state._liveTmo = setTimeout(refreshAll, 150);
    }
  };
}


// =====================================================================
// Phase 12.4 — Dashboard Library
// =====================================================================
let _libCatFilter = "";
let _libKey = null;

const LIBRARY_CATALOG = [
  {
    id: "linux-host", icon: "🐧", title: "Linux Host", category: "Infrastructure",
    description: "CPU, memory, disk I/O, and network throughput via node_exporter. Multi-host aware via the $host variable (auto-populated from the `instance` label that pulseagent stamps on every sample). Requires: pulseagent v0.3+ (auto-stamps `instance`/`host.name`).",
    metrics: ["node_cpu_seconds_total","node_memory_MemTotal_bytes","node_disk_read_bytes_total","node_network_receive_bytes_total"],
    alerts: { name:"Linux Host Alerts", intervalMs:15000, rules:[
      { name:"High CPU", lang:"promql", expr:'100 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle"}[5m])) * 100', cmp:">", threshold:90, forMs:300000, severity:"warning", labels:{integration:"linux-host"}, annotations:{summary:"CPU > 90% on {{ $labels.instance }}"} },
      { name:"Low disk space", lang:"promql", expr:'(node_filesystem_avail_bytes{fstype!~"tmpfs"} / node_filesystem_size_bytes{fstype!~"tmpfs"}) * 100', cmp:"<", threshold:10, forMs:300000, severity:"critical", labels:{integration:"linux-host"}, annotations:{summary:"Disk free < 10% on {{ $labels.instance }}"} },
      { name:"High memory", lang:"promql", expr:'(1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes) * 100', cmp:">", threshold:90, forMs:300000, severity:"warning", labels:{integration:"linux-host"}, annotations:{summary:"Memory > 90% on {{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Linux Host", timeRangeSec:3600, refreshSec:15,
      vars:[
        // Populated from any node_exporter / pulseagent series. The agent
        // auto-stamps `instance=<hostname>` so this list reflects every
        // host reporting to the workspace.
        { name:"host", type:"query", label:"Host",
          query:"label_values(node_cpu_seconds_total, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"CPU Usage % (by host)", type:"timeseries", queryLang:"promql",
          expr:'100 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle",instance=~"$host"}[5m])) * 100',
          x:0,y:0,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p2", title:"Memory Usage % (by host)", type:"timeseries", queryLang:"promql",
          expr:'(1 - node_memory_MemAvailable_bytes{instance=~"$host"} / node_memory_MemTotal_bytes{instance=~"$host"}) * 100',
          x:6,y:0,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p3", title:"System Load 1m", type:"timeseries", queryLang:"promql",
          expr:'node_load1{instance=~"$host"}',
          x:0,y:3,w:3,h:2, options:{}, links:[] },
        { id:"p4", title:"Disk Read", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(node_disk_read_bytes_total{instance=~"$host"}[5m]))',
          x:3,y:3,w:4,h:2, options:{unit:"bytes/s"}, links:[] },
        { id:"p5", title:"Disk Write", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(node_disk_written_bytes_total{instance=~"$host"}[5m]))',
          x:7,y:3,w:5,h:2, options:{unit:"bytes/s"}, links:[] },
        { id:"p6", title:"Network Rx", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(node_network_receive_bytes_total{device!="lo",instance=~"$host"}[5m]))',
          x:0,y:5,w:6,h:3, options:{unit:"bytes/s"}, links:[] },
        { id:"p7", title:"Network Tx", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(node_network_transmit_bytes_total{device!="lo",instance=~"$host"}[5m]))',
          x:6,y:5,w:6,h:3, options:{unit:"bytes/s"}, links:[] },
      ]}
  },
  {
    id: "docker", icon: "🐳", title: "Docker", category: "Container",
    description: "Container CPU, memory, and network usage via cadvisor or Docker stats exporter. Pick a host and/or container with the toolbar variables. Requires: cadvisor (or equivalent) scraped via `prom_scrape` with `extra_labels = { instance = \"<host>\" }` on each agent.",
    metrics: ["container_cpu_usage_seconds_total","container_memory_usage_bytes","container_network_receive_bytes_total"],
    alerts: { name:"Docker Alerts", intervalMs:15000, rules:[
      { name:"Container high CPU", lang:"promql", expr:'sum by (instance, name) (rate(container_cpu_usage_seconds_total{name!=""}[5m])) * 100', cmp:">", threshold:80, forMs:300000, severity:"warning", labels:{integration:"docker"}, annotations:{summary:"Container {{ $labels.name }} CPU > 80% on {{ $labels.instance }}"} },
      { name:"Container high memory", lang:"promql", expr:'container_memory_usage_bytes{name!=""} / container_spec_memory_limit_bytes{name!=""} * 100', cmp:">", threshold:90, forMs:300000, severity:"critical", labels:{integration:"docker"}, annotations:{summary:"Container {{ $labels.name }} memory > 90% of limit on {{ $labels.instance }}"} },
      { name:"Container OOM", lang:"promql", expr:'sum by (instance, name) (rate(container_oom_events_total[5m]))', cmp:">", threshold:0, forMs:0, severity:"critical", labels:{integration:"docker"}, annotations:{summary:"Container {{ $labels.name }} OOM-killed on {{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Docker Containers", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"host", type:"query", label:"Host",
          query:"label_values(container_last_seen, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"container", type:"query", label:"Container",
          query:"label_values(container_last_seen{instance=~\"$host\"}, name)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Container CPU % (per container)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, name) (rate(container_cpu_usage_seconds_total{name=~"$container",instance=~"$host"}[5m])) * 100',
          x:0,y:0,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p2", title:"Container Memory", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, name) (container_memory_usage_bytes{name=~"$container",instance=~"$host"})',
          x:6,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"Running Containers (in selection)", type:"stat", queryLang:"promql",
          expr:'count(container_last_seen{name=~"$container",instance=~"$host"})',
          x:0,y:3,w:3,h:2, options:{}, links:[] },
        { id:"p4", title:"Network Rx (per container)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, name) (rate(container_network_receive_bytes_total{name=~"$container",instance=~"$host"}[5m]))',
          x:3,y:3,w:4,h:2, options:{unit:"bytes/s"}, links:[] },
        { id:"p5", title:"Network Tx (per container)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, name) (rate(container_network_transmit_bytes_total{name=~"$container",instance=~"$host"}[5m]))',
          x:7,y:3,w:5,h:2, options:{unit:"bytes/s"}, links:[] },
        { id:"p6", title:"Block I/O Read (per container)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, name) (rate(container_blkio_device_usage_total{op="Read",name=~"$container",instance=~"$host"}[5m]))',
          x:0,y:5,w:12,h:3, options:{unit:"bytes/s"}, links:[] },
      ]}
  },
  {
    id: "kubernetes", icon: "☸️", title: "Kubernetes", category: "Container",
    description: "Pod phases, node status, cluster CPU/memory via kube-state-metrics and kubelet. Slice across clusters and namespaces with the toolbar variables. Requires: kube-state-metrics + kubelet cadvisor scrape; for multi-cluster, set Prometheus `external_labels: { cluster: <name> }` on each cluster's federating Prometheus.",
    metrics: ["kube_pod_status_phase","kube_node_status_condition","container_cpu_usage_seconds_total","kube_node_status_allocatable"],
    alerts: { name:"Kubernetes Alerts", intervalMs:15000, rules:[
      { name:"Pod crash-looping", lang:"promql", expr:"sum by (cluster, namespace, pod) (rate(kube_pod_container_status_restarts_total[15m])) * 60", cmp:">", threshold:3, forMs:300000, severity:"warning", labels:{integration:"kubernetes"}, annotations:{summary:"Pod {{ $labels.namespace }}/{{ $labels.pod }} restarting > 3×/min in cluster {{ $labels.cluster }}"} },
      { name:"Node not ready", lang:"promql", expr:'kube_node_status_condition{condition="Ready",status="true"}', cmp:"<", threshold:1, forMs:60000, severity:"critical", labels:{integration:"kubernetes"}, annotations:{summary:"Node {{ $labels.node }} not ready in cluster {{ $labels.cluster }}"} },
      { name:"High cluster CPU", lang:"promql", expr:'sum by (cluster) (rate(container_cpu_usage_seconds_total[5m])) / sum by (cluster) (kube_node_status_allocatable{resource="cpu"}) * 100', cmp:">", threshold:85, forMs:300000, severity:"warning", labels:{integration:"kubernetes"}, annotations:{summary:"Cluster {{ $labels.cluster }} CPU > 85%"} },
    ]},
    dashboard: { title:"Kubernetes Cluster", timeRangeSec:3600, refreshSec:30,
      vars:[
        // `cluster` is the recommended external_labels value Prometheus
        // adds when federating across clusters. If you only run one
        // cluster the dropdown will simply show one entry.
        { name:"cluster", type:"query", label:"Cluster",
          query:"label_values(kube_node_info, cluster)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"namespace", type:"query", label:"Namespace",
          query:"label_values(kube_pod_info{cluster=~\"$cluster\"}, namespace)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Running Pods", type:"stat", queryLang:"promql",
          expr:'sum(kube_pod_status_phase{phase="Running",cluster=~"$cluster",namespace=~"$namespace"})',
          x:0,y:0,w:3,h:2, options:{}, links:[] },
        { id:"p2", title:"Failed Pods", type:"stat", queryLang:"promql",
          expr:'sum(kube_pod_status_phase{phase="Failed",cluster=~"$cluster",namespace=~"$namespace"})',
          x:3,y:0,w:3,h:2, options:{}, links:[] },
        { id:"p3", title:"Ready Nodes", type:"stat", queryLang:"promql",
          expr:'sum(kube_node_status_condition{condition="Ready",status="true",cluster=~"$cluster"})',
          x:6,y:0,w:3,h:2, options:{}, links:[] },
        { id:"p4", title:"Cluster CPU % (by cluster)", type:"timeseries", queryLang:"promql",
          expr:'sum by (cluster) (rate(container_cpu_usage_seconds_total{cluster=~"$cluster",namespace=~"$namespace"}[5m])) / sum by (cluster) (kube_node_status_allocatable{resource="cpu",cluster=~"$cluster"}) * 100',
          x:0,y:2,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p5", title:"Cluster Memory % (by cluster)", type:"timeseries", queryLang:"promql",
          expr:'sum by (cluster) (container_memory_working_set_bytes{cluster=~"$cluster",namespace=~"$namespace"}) / sum by (cluster) (kube_node_status_allocatable{resource="memory",cluster=~"$cluster"}) * 100',
          x:6,y:2,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p6", title:"Pod Restarts/min (by namespace)", type:"timeseries", queryLang:"promql",
          expr:"sum by (cluster, namespace) (rate(kube_pod_container_status_restarts_total{cluster=~\"$cluster\",namespace=~\"$namespace\"}[5m])) * 60",
          x:0,y:5,w:12,h:3, options:{}, links:[] },
      ]}
  },
  {
    id: "postgres", icon: "🐘", title: "PostgreSQL", category: "Database",
    description: "Connections, transactions, cache hit rate, and query throughput via postgres_exporter. Multi-instance / multi-database via toolbar variables. Requires: postgres_exporter scraped via `prom_scrape` with `extra_labels = { instance = \"<host:port>\" }` per target.",
    metrics: ["pg_up","pg_stat_activity_count","pg_stat_database_xact_commit","pg_stat_database_blks_hit"],
    alerts: { name:"PostgreSQL Alerts", intervalMs:15000, rules:[
      { name:"Postgres down", lang:"promql", expr:"pg_up", cmp:"<", threshold:1, forMs:60000, severity:"critical", labels:{integration:"postgres"}, annotations:{summary:"PostgreSQL down on {{ $labels.instance }}"} },
      { name:"High connection count", lang:"promql", expr:"pg_stat_activity_count", cmp:">", threshold:80, forMs:300000, severity:"warning", labels:{integration:"postgres"}, annotations:{summary:"Postgres connections > 80 on {{ $labels.instance }}"} },
      { name:"Low cache hit rate", lang:"promql", expr:"sum by (instance, datname) (pg_stat_database_blks_hit) / (sum by (instance, datname) (pg_stat_database_blks_hit) + sum by (instance, datname) (pg_stat_database_blks_read) + 1)", cmp:"<", threshold:0.95, forMs:600000, severity:"warning", labels:{integration:"postgres"}, annotations:{summary:"Cache hit rate < 95% on {{ $labels.instance }}/{{ $labels.datname }}"} },
    ]},
    dashboard: { title:"PostgreSQL", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(pg_up, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"datname", type:"query", label:"Database",
          query:"label_values(pg_stat_database_xact_commit{instance=~\"$instance\"}, datname)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Active Connections (by instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (pg_stat_activity_count{instance=~"$instance"})',
          x:0,y:0,w:6,h:3, options:{}, links:[] },
        { id:"p2", title:"Cache Hit Rate (avg in selection)", type:"stat", queryLang:"promql",
          expr:'sum(pg_stat_database_blks_hit{instance=~"$instance",datname=~"$datname"}) / (sum(pg_stat_database_blks_hit{instance=~"$instance",datname=~"$datname"}) + sum(pg_stat_database_blks_read{instance=~"$instance",datname=~"$datname"}) + 1)',
          x:6,y:0,w:3,h:3, options:{unit:"%"}, links:[] },
        { id:"p3", title:"Postgres Up (instances)", type:"stat", queryLang:"promql",
          expr:'sum(pg_up{instance=~"$instance"})',
          x:9,y:0,w:3,h:3, options:{}, links:[] },
        { id:"p4", title:"Commits/s (per database)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, datname) (rate(pg_stat_database_xact_commit{instance=~"$instance",datname=~"$datname"}[5m]))',
          x:0,y:3,w:6,h:3, options:{}, links:[] },
        { id:"p5", title:"Rollbacks/s (per database)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, datname) (rate(pg_stat_database_xact_rollback{instance=~"$instance",datname=~"$datname"}[5m]))',
          x:6,y:3,w:6,h:3, options:{}, links:[] },
        { id:"p6", title:"Rows Fetched/s (per database)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, datname) (rate(pg_stat_database_tup_fetched{instance=~"$instance",datname=~"$datname"}[5m]))',
          x:0,y:6,w:6,h:3, options:{}, links:[] },
        { id:"p7", title:"Rows Written/s (per database)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance, datname) (rate(pg_stat_database_tup_inserted{instance=~"$instance",datname=~"$datname"}[5m]) + rate(pg_stat_database_tup_updated{instance=~"$instance",datname=~"$datname"}[5m]))',
          x:6,y:6,w:6,h:3, options:{}, links:[] },
      ]}
  },
  {
    id: "redis", icon: "🟥", title: "Redis", category: "Database",
    description: "Connections, memory, hit rate, and ops/sec via redis_exporter. Multi-instance via the $instance toolbar variable. Requires: redis_exporter scraped via `prom_scrape` with `extra_labels = { instance = \"<host:port>\" }` per target.",
    metrics: ["redis_up","redis_connected_clients","redis_used_memory_bytes","redis_commands_processed_total"],
    alerts: { name:"Redis Alerts", intervalMs:15000, rules:[
      { name:"Redis down", lang:"promql", expr:"redis_up", cmp:"<", threshold:1, forMs:60000, severity:"critical", labels:{integration:"redis"}, annotations:{summary:"Redis down on {{ $labels.instance }}"} },
      { name:"Redis memory > 90%", lang:"promql", expr:"redis_used_memory_bytes / redis_total_system_memory_bytes * 100", cmp:">", threshold:90, forMs:300000, severity:"warning", labels:{integration:"redis"}, annotations:{summary:"Redis memory > 90% on {{ $labels.instance }}"} },
      { name:"Redis low hit rate", lang:"promql", expr:"sum by (instance) (redis_keyspace_hits_total) / (sum by (instance) (redis_keyspace_hits_total) + sum by (instance) (redis_keyspace_misses_total) + 1)", cmp:"<", threshold:0.8, forMs:600000, severity:"warning", labels:{integration:"redis"}, annotations:{summary:"Cache hit rate < 80% on {{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Redis", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(redis_up, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Connected Clients (sum)", type:"stat", queryLang:"promql",
          expr:'sum(redis_connected_clients{instance=~"$instance"})',
          x:0,y:0,w:3,h:2, options:{}, links:[] },
        { id:"p2", title:"Memory Used (sum)", type:"stat", queryLang:"promql",
          expr:'sum(redis_used_memory_bytes{instance=~"$instance"})',
          x:3,y:0,w:3,h:2, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"Ops/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(redis_commands_processed_total{instance=~"$instance"}[5m]))',
          x:6,y:0,w:6,h:2, options:{}, links:[] },
        { id:"p4", title:"Hit Rate (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (redis_keyspace_hits_total{instance=~"$instance"}) / (sum by (instance) (redis_keyspace_hits_total{instance=~"$instance"}) + sum by (instance) (redis_keyspace_misses_total{instance=~"$instance"}) + 1)',
          x:0,y:2,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p5", title:"Evictions/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(redis_evicted_keys_total{instance=~"$instance"}[5m]))',
          x:6,y:2,w:6,h:3, options:{}, links:[] },
        { id:"p6", title:"Memory Used (per instance)", type:"timeseries", queryLang:"promql",
          expr:'redis_used_memory_bytes{instance=~"$instance"}',
          x:0,y:5,w:12,h:3, options:{unit:"bytes"}, links:[] },
      ]}
  },
  {
    id: "nginx", icon: "🔀", title: "NGINX", category: "Infrastructure",
    description: "Request rate, active connections, status codes, and error rate via nginx_exporter. Multi-instance via the $instance toolbar variable. Requires: nginx-prometheus-exporter scraped via `prom_scrape` with `extra_labels = { instance = \"<host:port>\" }` per target.",
    metrics: ["nginx_http_requests_total","nginx_connections_active","nginx_connections_waiting"],
    alerts: { name:"NGINX Alerts", intervalMs:15000, rules:[
      { name:"NGINX high error rate", lang:"promql", expr:'sum by (instance) (rate(nginx_http_requests_total{status=~"5.."}[5m])) / (sum by (instance) (rate(nginx_http_requests_total[5m])) + 1) * 100', cmp:">", threshold:5, forMs:300000, severity:"warning", labels:{integration:"nginx"}, annotations:{summary:"NGINX 5xx > 5% on {{ $labels.instance }}"} },
      { name:"NGINX down", lang:"promql", expr:"nginx_up", cmp:"<", threshold:1, forMs:60000, severity:"critical", labels:{integration:"nginx"}, annotations:{summary:"NGINX down on {{ $labels.instance }}"} },
      { name:"NGINX connection spike", lang:"promql", expr:"nginx_connections_active", cmp:">", threshold:1000, forMs:300000, severity:"warning", labels:{integration:"nginx"}, annotations:{summary:"NGINX active conns > 1000 on {{ $labels.instance }}"} },
    ]},
    dashboard: { title:"NGINX", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(nginx_up, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Requests/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(nginx_http_requests_total{instance=~"$instance"}[5m]))',
          x:0,y:0,w:6,h:3, options:{}, links:[] },
        { id:"p2", title:"Active Connections (sum)", type:"stat", queryLang:"promql",
          expr:'sum(nginx_connections_active{instance=~"$instance"})',
          x:6,y:0,w:3,h:3, options:{}, links:[] },
        { id:"p3", title:"Waiting Connections (sum)", type:"stat", queryLang:"promql",
          expr:'sum(nginx_connections_waiting{instance=~"$instance"})',
          x:9,y:0,w:3,h:3, options:{}, links:[] },
        { id:"p4", title:"5xx Errors/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(nginx_http_requests_total{status=~"5..",instance=~"$instance"}[5m]))',
          x:0,y:3,w:6,h:3, options:{}, links:[] },
        { id:"p5", title:"4xx Errors/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (instance) (rate(nginx_http_requests_total{status=~"4..",instance=~"$instance"}[5m]))',
          x:6,y:3,w:6,h:3, options:{}, links:[] },
      ]}
  },
  {
    id: "nodejs", icon: "⬡", title: "Node.js", category: "Application",
    description: "Event loop lag, heap usage, GC, and HTTP request latency via prom-client middleware. Pick a service ($job) and replica ($instance) from the toolbar. Requires: `prom_scrape` target per app with `extra_labels = { job = \"<service>\", instance = \"<host:port>\" }`.",
    metrics: ["nodejs_eventloop_lag_seconds","nodejs_heap_size_used_bytes","process_cpu_seconds_total","http_request_duration_seconds"],
    alerts: { name:"Node.js Alerts", intervalMs:15000, rules:[
      { name:"High event loop lag", lang:"promql", expr:"nodejs_eventloop_lag_seconds * 1000", cmp:">", threshold:100, forMs:300000, severity:"warning", labels:{integration:"nodejs"}, annotations:{summary:"Event loop lag > 100ms on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"High heap usage", lang:"promql", expr:"nodejs_heap_size_used_bytes / nodejs_heap_size_total_bytes * 100", cmp:">", threshold:90, forMs:300000, severity:"warning", labels:{integration:"nodejs"}, annotations:{summary:"Heap > 90% on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"High HTTP p99 latency", lang:"promql", expr:"histogram_quantile(0.99, sum by (job, instance, le) (rate(http_request_duration_seconds_bucket[5m]))) * 1000", cmp:">", threshold:2000, forMs:300000, severity:"warning", labels:{integration:"nodejs"}, annotations:{summary:"HTTP p99 > 2s on {{ $labels.job }}/{{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Node.js", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"job", type:"query", label:"Service",
          query:"label_values(nodejs_eventloop_lag_seconds, job)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(nodejs_eventloop_lag_seconds{job=~\"$job\"}, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Event Loop Lag (ms, per instance)", type:"timeseries", queryLang:"promql",
          expr:'nodejs_eventloop_lag_seconds{job=~"$job",instance=~"$instance"} * 1000',
          x:0,y:0,w:6,h:3, options:{unit:"ms"}, links:[] },
        { id:"p2", title:"Heap Used (per instance)", type:"timeseries", queryLang:"promql",
          expr:'nodejs_heap_size_used_bytes{job=~"$job",instance=~"$instance"}',
          x:6,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"CPU % (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(process_cpu_seconds_total{job=~"$job",instance=~"$instance"}[5m]) * 100',
          x:0,y:3,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p4", title:"HTTP Req/s (per service)", type:"timeseries", queryLang:"promql",
          expr:'sum by (job) (rate(http_request_duration_seconds_count{job=~"$job",instance=~"$instance"}[5m]))',
          x:6,y:3,w:6,h:3, options:{}, links:[] },
        { id:"p5", title:"HTTP p99 (ms, per service)", type:"timeseries", queryLang:"promql",
          expr:'histogram_quantile(0.99, sum by (job, le) (rate(http_request_duration_seconds_bucket{job=~"$job",instance=~"$instance"}[5m]))) * 1000',
          x:0,y:6,w:12,h:3, options:{unit:"ms"}, links:[] },
      ]}
  },
  {
    id: "golang", icon: "🐹", title: "Go", category: "Application",
    description: "Goroutines, GC pause, heap usage, and CPU via Go's standard prometheus metrics. Pick a service ($job) and replica ($instance) from the toolbar. Requires: `prom_scrape` target per app with `extra_labels = { job = \"<service>\", instance = \"<host:port>\" }`.",
    metrics: ["go_goroutines","go_gc_duration_seconds","go_memstats_heap_inuse_bytes","process_cpu_seconds_total"],
    alerts: { name:"Go Alerts", intervalMs:15000, rules:[
      { name:"Goroutine leak", lang:"promql", expr:"go_goroutines", cmp:">", threshold:10000, forMs:300000, severity:"warning", labels:{integration:"golang"}, annotations:{summary:"Goroutines > 10 000 on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"High GC pressure", lang:"promql", expr:"histogram_quantile(0.99, sum by (job, instance, le) (rate(go_gc_duration_seconds_bucket[5m]))) * 1000", cmp:">", threshold:100, forMs:300000, severity:"warning", labels:{integration:"golang"}, annotations:{summary:"GC p99 > 100ms on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"High heap usage", lang:"promql", expr:"go_memstats_heap_inuse_bytes / go_memstats_sys_bytes * 100", cmp:">", threshold:85, forMs:300000, severity:"warning", labels:{integration:"golang"}, annotations:{summary:"Heap util > 85% on {{ $labels.job }}/{{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Go Runtime", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"job", type:"query", label:"Service",
          query:"label_values(go_goroutines, job)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(go_goroutines{job=~\"$job\"}, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Goroutines (per instance)", type:"timeseries", queryLang:"promql",
          expr:'go_goroutines{job=~"$job",instance=~"$instance"}',
          x:0,y:0,w:6,h:3, options:{}, links:[] },
        { id:"p2", title:"Heap In-Use (per instance)", type:"timeseries", queryLang:"promql",
          expr:'go_memstats_heap_inuse_bytes{job=~"$job",instance=~"$instance"}',
          x:6,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"CPU % (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(process_cpu_seconds_total{job=~"$job",instance=~"$instance"}[5m]) * 100',
          x:0,y:3,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p4", title:"GC Runs/min (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(go_gc_duration_seconds_count{job=~"$job",instance=~"$instance"}[5m]) * 60',
          x:6,y:3,w:6,h:3, options:{}, links:[] },
        { id:"p5", title:"GC p99 (ms, per service)", type:"timeseries", queryLang:"promql",
          expr:'histogram_quantile(0.99, sum by (job, le) (rate(go_gc_duration_seconds_bucket{job=~"$job",instance=~"$instance"}[5m]))) * 1000',
          x:0,y:6,w:12,h:3, options:{unit:"ms"}, links:[] },
      ]}
  },
  {
    id: "python", icon: "🐍", title: "Python", category: "Application",
    description: "CPU, memory RSS, GC collection counts, and open FDs via prometheus_client. Pick a service ($job) and replica ($instance) from the toolbar. Requires: `prom_scrape` target per app with `extra_labels = { job = \"<service>\", instance = \"<host:port>\" }`.",
    metrics: ["process_cpu_seconds_total","process_resident_memory_bytes","python_gc_objects_collected_total","process_open_fds"],
    alerts: { name:"Python Alerts", intervalMs:15000, rules:[
      { name:"High CPU", lang:"promql", expr:"rate(process_cpu_seconds_total[5m]) * 100", cmp:">", threshold:85, forMs:300000, severity:"warning", labels:{integration:"python"}, annotations:{summary:"Python CPU > 85% on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"High memory RSS", lang:"promql", expr:"process_resident_memory_bytes", cmp:">", threshold:2147483648, forMs:300000, severity:"warning", labels:{integration:"python"}, annotations:{summary:"Python RSS > 2 GiB on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"FD exhaustion", lang:"promql", expr:"process_open_fds / process_max_fds * 100", cmp:">", threshold:80, forMs:300000, severity:"warning", labels:{integration:"python"}, annotations:{summary:"Open FDs > 80% of limit on {{ $labels.job }}/{{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Python Runtime", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"job", type:"query", label:"Service",
          query:"label_values(python_info, job)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(python_info{job=~\"$job\"}, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"CPU % (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(process_cpu_seconds_total{job=~"$job",instance=~"$instance"}[5m]) * 100',
          x:0,y:0,w:6,h:3, options:{unit:"%"}, links:[] },
        { id:"p2", title:"Memory RSS (per instance)", type:"timeseries", queryLang:"promql",
          expr:'process_resident_memory_bytes{job=~"$job",instance=~"$instance"}',
          x:6,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"Open FDs (sum)", type:"stat", queryLang:"promql",
          expr:'sum(process_open_fds{job=~"$job",instance=~"$instance"})',
          x:0,y:3,w:3,h:2, options:{}, links:[] },
        { id:"p4", title:"GC Gen0/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(python_gc_objects_collected_total{generation="0",job=~"$job",instance=~"$instance"}[5m])',
          x:3,y:3,w:4,h:2, options:{}, links:[] },
        { id:"p5", title:"GC Gen1+2/s (per instance)", type:"timeseries", queryLang:"promql",
          expr:'rate(python_gc_objects_collected_total{generation!="0",job=~"$job",instance=~"$instance"}[5m])',
          x:7,y:3,w:5,h:2, options:{}, links:[] },
      ]}
  },
  {
    id: "java-jvm", icon: "☕", title: "Java JVM", category: "Application",
    description: "Heap/non-heap memory, GC pause, thread count, and class loading via Micrometer or JMX exporter. Pick a service ($job) and replica ($instance) from the toolbar. Requires: `prom_scrape` target per app with `extra_labels = { job = \"<service>\", instance = \"<host:port>\" }`.",
    metrics: ["jvm_memory_used_bytes","jvm_gc_pause_seconds","jvm_threads_live_threads","jvm_classes_loaded_classes"],
    alerts: { name:"Java JVM Alerts", intervalMs:15000, rules:[
      { name:"JVM heap > 90%", lang:"promql", expr:'jvm_memory_used_bytes{area="heap"} / jvm_memory_max_bytes{area="heap"} * 100', cmp:">", threshold:90, forMs:300000, severity:"critical", labels:{integration:"java-jvm"}, annotations:{summary:"JVM heap > 90% on {{ $labels.job }}/{{ $labels.instance }} — OOM risk"} },
      { name:"High GC pause", lang:"promql", expr:"histogram_quantile(0.99, sum by (job, instance, le) (rate(jvm_gc_pause_seconds_bucket[5m]))) * 1000", cmp:">", threshold:500, forMs:300000, severity:"warning", labels:{integration:"java-jvm"}, annotations:{summary:"GC p99 > 500ms on {{ $labels.job }}/{{ $labels.instance }}"} },
      { name:"Thread spike", lang:"promql", expr:"jvm_threads_live_threads", cmp:">", threshold:500, forMs:300000, severity:"warning", labels:{integration:"java-jvm"}, annotations:{summary:"JVM live threads > 500 on {{ $labels.job }}/{{ $labels.instance }}"} },
    ]},
    dashboard: { title:"Java JVM", timeRangeSec:3600, refreshSec:15,
      vars:[
        { name:"job", type:"query", label:"Service",
          query:"label_values(jvm_memory_used_bytes, job)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
        { name:"instance", type:"query", label:"Instance",
          query:"label_values(jvm_memory_used_bytes{job=~\"$job\"}, instance)",
          options:"", regex:"",
          multi:true, allOption:true, current:["$__all"], hide:false },
      ],
      panels:[
        { id:"p1", title:"Heap Used (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (job, instance) (jvm_memory_used_bytes{area="heap",job=~"$job",instance=~"$instance"})',
          x:0,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p2", title:"Non-Heap Used (per instance)", type:"timeseries", queryLang:"promql",
          expr:'sum by (job, instance) (jvm_memory_used_bytes{area="nonheap",job=~"$job",instance=~"$instance"})',
          x:6,y:0,w:6,h:3, options:{unit:"bytes"}, links:[] },
        { id:"p3", title:"Live Threads (sum)", type:"stat", queryLang:"promql",
          expr:'sum(jvm_threads_live_threads{job=~"$job",instance=~"$instance"})',
          x:0,y:3,w:3,h:2, options:{}, links:[] },
        { id:"p4", title:"Classes Loaded (sum)", type:"stat", queryLang:"promql",
          expr:'sum(jvm_classes_loaded_classes{job=~"$job",instance=~"$instance"})',
          x:3,y:3,w:3,h:2, options:{}, links:[] },
        { id:"p5", title:"GC p99 (ms, per instance)", type:"timeseries", queryLang:"promql",
          expr:'histogram_quantile(0.99, sum by (job, instance, le) (rate(jvm_gc_pause_seconds_bucket{job=~"$job",instance=~"$instance"}[5m]))) * 1000',
          x:6,y:3,w:6,h:2, options:{unit:"ms"}, links:[] },
        { id:"p6", title:"GC Throughput % (per instance)", type:"timeseries", queryLang:"promql",
          expr:'(1 - sum by (job, instance) (rate(jvm_gc_pause_seconds_sum{job=~"$job",instance=~"$instance"}[5m])) / 15) * 100',
          x:0,y:5,w:12,h:3, options:{unit:"%"}, links:[] },
      ]}
  },
];

function renderLibrary(catFilter, search) {
  const lower = (search || "").toLowerCase();
  const cards = LIBRARY_CATALOG.filter(e =>
    (!catFilter || e.category === catFilter) &&
    (!lower || e.title.toLowerCase().includes(lower) ||
               e.description.toLowerCase().includes(lower) ||
               e.category.toLowerCase().includes(lower))
  );
  const grid = $("lib-grid");
  grid.innerHTML = cards.length
    ? ""
    : "<p style='padding:20px;color:var(--muted)'>No matching integrations.</p>";
  for (const e of cards) {
    const card = document.createElement("div");
    card.className = "lib-card";
    card.innerHTML = `
      <div class="lib-card-head">
        <span class="lib-card-icon">${e.icon}</span>
        <div>
          <p class="lib-card-name">${escapeHtml(e.title)}</p>
          <span class="lib-card-cat">${escapeHtml(e.category)}</span>
        </div>
      </div>
      <p class="lib-card-desc">${escapeHtml(e.description)}</p>
      <div class="lib-card-foot">
        <span class="lib-metric-cnt">${e.metrics.length} metrics &middot; ${e.dashboard.panels.length} panels &middot; ${e.alerts.rules.length} alerts</span>
        <button class="primary">Import</button>
      </div>`;
    const importBtn = card.querySelector("button");
    importBtn.addEventListener("click", (ev) => { ev.stopPropagation(); openLibraryImport(e.id); });
    card.addEventListener("click", () => openLibraryImport(e.id));
    grid.appendChild(card);
  }
}

async function checkLibraryMetrics(metrics) {
  try {
    const r = await authFetch("/api/prom/api/v1/label/__name__/values");
    if (!r.ok) return null;
    const data = await r.json();
    const have = new Set(data.data || []);
    return metrics.map(m => ({
      name: m,
      present: [...have].some(n => n === m || n.startsWith(m + "_") || n.startsWith(m + "{"))
    }));
  } catch { return null; }
}

async function openLibraryImport(id) {
  const entry = LIBRARY_CATALOG.find(e => e.id === id);
  if (!entry) return;
  _libKey = id;
  $("lib-modal-title").textContent = "Import: " + entry.title;
  $("lib-modal-desc").textContent = entry.description;
  $("lib-metrics-list").innerHTML = "<em style='color:var(--muted);font-size:12px'>Checking metrics…</em>";
  $("lib-metrics-note").textContent = "";
  $("lib-modal-status").textContent = "";
  $("lib-import-confirm").disabled = false;
  $("lib-import-confirm").classList.remove("hidden");
  $("lib-view-btn").classList.add("hidden");
  $("lib-step1").classList.remove("hidden");
  $("lib-step2").classList.add("hidden");
  $("lib-modal").classList.remove("hidden");

  const results = await checkLibraryMetrics(entry.metrics);
  const list = $("lib-metrics-list");
  if (!results) {
    list.innerHTML = "<em style='color:var(--muted);font-size:12px'>Could not check — Prometheus API unavailable.</em>";
    return;
  }
  const missing = results.filter(r => !r.present).length;
  list.innerHTML = results.map(r => `
    <div class="lib-check-row">
      <span class="${r.present ? "lib-check-ok" : "lib-check-miss"}">${r.present ? "✓" : "✗"}</span>
      <span class="lib-check-name">${escapeHtml(r.name)}</span>
      <span class="lib-check-hint">${r.present ? "found" : "not yet detected"}</span>
    </div>`).join("");
  $("lib-metrics-note").textContent = missing
    ? missing + " metric(s) not yet detected. The dashboard will still import; panels will show \u201cNo data\u201d until you start sending those metrics."
    : "All required metrics detected. You\u2019re ready to go!";
}

async function importLibraryDashboard() {
  const entry = LIBRARY_CATALOG.find(e => e.id === _libKey);
  if (!entry) return;
  $("lib-import-confirm").disabled = true;
  $("lib-modal-status").textContent = "Importing\u2026";
  try {
    const dash = {
      ...entry.dashboard,
      id: "d-" + uuid(),
      panels: entry.dashboard.panels.map(p => ({ ...p, id: "p-" + uuid() })),
    };
    const saved = await api("POST", "/api/dashboards", dash);
    // Best-effort alert rule creation
    try { await api("POST", "/api/rules", entry.alerts); } catch {}
    $("lib-step1").classList.add("hidden");
    $("lib-step2").classList.remove("hidden");
    $("lib-done-msg").textContent =
      "Dashboard \u201c" + dash.title + "\u201d created with " + dash.panels.length +
      " panels and " + entry.alerts.rules.length + " alert rules. Open it to start exploring.";
    $("lib-view-btn").classList.remove("hidden");
    $("lib-import-confirm").classList.add("hidden");
    $("lib-modal-status").textContent = "";
    await reloadList(saved.id);
  } catch (e) {
    $("lib-modal-status").textContent = "Error: " + e.message;
    $("lib-import-confirm").disabled = false;
  }
}

// =====================================================================
// Wiring
// =====================================================================
function router() {
  const h = location.hash || "#/dashboards";
  if (h.startsWith("#/snapshot/"))    loadSnapshot(h.slice("#/snapshot/".length));
  else if (h.startsWith("#/explore")) showView("explore");
  else if (h.startsWith("#/traces"))  showView("traces");
  else if (h.startsWith("#/library")) showView("library");
  else if (h.startsWith("#/alerts"))  showView("alerts");
  else if (h.startsWith("#/agents"))  showView("agents");
  else if (h.startsWith("#/map"))     showView("map");
  else                                showView("dashboards");
}

$("dash-picker").addEventListener("change", (e) => openDashboard(e.target.value));
$("dash-new").addEventListener("click", async () => {
  const title = prompt("Dashboard title", "New dashboard");
  if (!title) return;
  const d = await api("POST", "/api/dashboards", {
    title, timeRangeSec: 3600, refreshSec: 15, panels: [],
  });
  await reloadList(d.id);
});
$("dash-rename").addEventListener("click", async () => {
  if (!state.current) return;
  const t = prompt("Dashboard title", state.current.title);
  if (!t) return;
  state.current.title = t;
  await saveCurrent();
});
$("dash-delete").addEventListener("click", async () => {
  if (!state.current) return;
  if (!confirm(`Delete dashboard "${state.current.title}"?`)) return;
  await api("DELETE", "/api/dashboards/" + encodeURIComponent(state.current.id));
  state.current = null;
  await reloadList();
});
$("time-range").addEventListener("change", async () => {
  if (!state.current) return;
  state.current.timeRangeSec = +$("time-range").value;
  await refreshAll();
  await saveCurrent();
});
$("refresh-int").addEventListener("change", async () => {
  if (!state.current) return;
  state.current.refreshSec = +$("refresh-int").value;
  scheduleRefresh();
  await saveCurrent();
});
$("refresh-now").addEventListener("click", refreshAll);
$("compare-toggle").addEventListener("click", toggleCompare);
$("live-toggle").addEventListener("click", toggleLive);
$("share-btn").addEventListener("click", openShare);
$("history-btn").addEventListener("click", openHistory);
$("save-view-btn").addEventListener("click", saveCurrentView);
$("saved-views").addEventListener("change", (e) => { if (e.target.value) applyView(e.target.value); });
$("edit-toggle").addEventListener("click", () => {
  if (!state.current) {
    alert("Open or create a dashboard first.");
    return;
  }
  state.editMode = !state.editMode;
  document.body.classList.toggle("edit-mode", state.editMode);
  $("edit-toggle").textContent = state.editMode ? "✓ Done" : "✎ Edit";
  $("add-panel").classList.toggle("hidden", !state.editMode);
  $("save-dash").classList.toggle("hidden", !state.editMode);
  $("vars-edit-btn").classList.toggle("hidden", !state.editMode);
});
$("vars-edit-btn").addEventListener("click", openVarsEditor);
$("add-panel").addEventListener("click", addPanel);
$("save-dash").addEventListener("click", async () => {
  const btn  = $("save-dash");
  const orig = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Saving\u2026";
  btn.classList.remove("saved");
  try {
    await saveCurrent();
    btn.textContent = "Saved \u2713";
    btn.classList.add("saved");
    setTimeout(() => {
      btn.textContent = orig;
      btn.classList.remove("saved");
      btn.disabled = false;
    }, 1800);
  } catch (err) {
    btn.textContent = "Error";
    setTimeout(() => { btn.textContent = orig; btn.disabled = false; }, 2200);
  }
});

// History modal
$("hist-close").addEventListener("click", () => $("history-modal").classList.add("hidden"));
$("history-modal").addEventListener("click", (e) => {
  if (e.target === $("history-modal")) $("history-modal").classList.add("hidden");
});

// Share modal
$("share-close").addEventListener("click", () => $("share-modal").classList.add("hidden"));
$("share-modal").addEventListener("click", (e) => {
  if (e.target === $("share-modal")) $("share-modal").classList.add("hidden");
});
$("share-gen-btn").addEventListener("click", async () => {
  if (!state.current) return;
  const enc = await compressToBase64url(JSON.stringify(state.current));
  const url = location.origin + location.pathname + "#/snapshot/" + enc;
  $("share-url-inp").value = url;
  $("share-embed-inp").value = `<iframe src="${url}" width="1200" height="600" frameborder="0"></iframe>`;
});
$("share-copy-url-btn").addEventListener("click", () => {
  const v = $("share-url-inp").value; if (v) navigator.clipboard.writeText(v).catch(() => {});
});
$("share-copy-embed-btn").addEventListener("click", () => {
  const v = $("share-embed-inp").value; if (v) navigator.clipboard.writeText(v).catch(() => {});
});
$("share-export-btn").addEventListener("click", () => {
  if (!state.current) return;
  navigator.clipboard.writeText(JSON.stringify(state.current, null, 2)).catch(() => {});
});
$("share-download-btn").addEventListener("click", () => {
  if (!state.current) return;
  const blob = new Blob([JSON.stringify(state.current, null, 2)], { type: "application/json" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = (state.current.title || "dashboard").replace(/[^a-zA-Z0-9_-]/g, "_") + ".json";
  a.click(); URL.revokeObjectURL(a.href);
});
$("share-import-btn").addEventListener("click", async () => {
  const text = $("share-import-ta").value.trim(); if (!text) return;
  let dash; try { dash = JSON.parse(text); } catch { alert("Invalid JSON"); return; }
  dash.id = "d-" + uuid();
  dash.title = (dash.title || "Imported") + " (copy)";
  try {
    const saved = await api("POST", "/api/dashboards", dash);
    $("share-modal").classList.add("hidden");
    await reloadList(saved.id);
  } catch (e) { alert("Import failed: " + e.message); }
});

// Variables editor modal
$("vars-modal-close").addEventListener("click", async () => {
  $("vars-modal").classList.add("hidden");
  await renderVarsBar(state.current);
  await saveCurrent();
});
$("vars-modal").addEventListener("click", (e) => {
  if (e.target === $("vars-modal")) $("vars-modal").classList.add("hidden");
});
$("vars-add-btn").addEventListener("click", () => {
  if (!state.current.vars) state.current.vars = [];
  state.current.vars.push({
    name: "var" + (state.current.vars.length + 1), label: "", type: "custom",
    options: "", query: "", current: "", multi: false, allOption: false, regex: "", hide: false,
  });
  renderVarsEditorModal();
});

$("editor-close").addEventListener("click", closeEditor);
$("ed-cancel").addEventListener("click", closeEditor);
$("ed-delete").addEventListener("click", deleteEditorPanel);
$("ed-preview-btn").addEventListener("click", previewEditor);

$("ed-apply").addEventListener("click", async () => {
  const btn = $("ed-apply");
  const orig = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Applying\u2026";
  try {
    await applyEditor();
  } catch {
    btn.textContent = orig;
    btn.disabled = false;
  }
});

// Show/hide Display-options section when panel type changes.
$("ed-type").addEventListener("change", () => syncEdDisplay());

// Toggle custom unit input visibility.
$("ed-unit").addEventListener("change", () => {
  $("ed-unit-custom-wrap").style.display = $("ed-unit").value === "custom" ? "" : "none";
});

// Library
$("lib-search").addEventListener("input", (e) => renderLibrary(_libCatFilter, e.target.value));
$("lib-filters").addEventListener("click", (e) => {
  const btn = e.target.closest("button[data-cat]");
  if (!btn) return;
  _libCatFilter = btn.dataset.cat;
  $("lib-filters").querySelectorAll("button").forEach(b =>
    b.classList.toggle("btn-active", b.dataset.cat === _libCatFilter)
  );
  renderLibrary(_libCatFilter, $("lib-search").value);
});
$("lib-modal-close").addEventListener("click", () => $("lib-modal").classList.add("hidden"));
$("lib-modal-close2").addEventListener("click", () => $("lib-modal").classList.add("hidden"));
$("lib-modal").addEventListener("click", (e) => {
  if (e.target === $("lib-modal")) $("lib-modal").classList.add("hidden");
});
$("lib-import-confirm").addEventListener("click", importLibraryDashboard);
$("lib-view-btn").addEventListener("click", () => {
  $("lib-modal").classList.add("hidden");
  showView("dashboards");
  location.hash = "#/dashboards";
});

$("explore-run").addEventListener("click", runExplore);
$("explore-expr").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) runExplore();
});

// =====================================================================
// Traces tab — list summaries + waterfall modal (Phase 4 #4)
// =====================================================================
function sinceMsFrom(rangeSec) {
  if (!rangeSec || rangeSec === "0") return 1;            // "All" → epoch+1
  return Date.now() - (+rangeSec) * 1000;
}
function fmtAge(ms) {
  const d = Date.now() - ms;
  if (d < 60_000)         return Math.floor(d/1000) + "s ago";
  if (d < 3_600_000)      return Math.floor(d/60_000) + "m ago";
  if (d < 86_400_000)     return Math.floor(d/3_600_000) + "h ago";
  return Math.floor(d/86_400_000) + "d ago";
}
function fmtDur(ms) {
  if (ms < 1)     return ms.toFixed(2) + "ms";
  if (ms < 1000)  return ms.toFixed(1) + "ms";
  return (ms/1000).toFixed(2) + "s";
}
// Deterministic colour per service name — keeps the same bar colour
// stable across refreshes so the eye can track it.
function svcColor(name) {
  let h = 0;
  for (let i = 0; i < name.length; i++) h = (h*31 + name.charCodeAt(i)) | 0;
  const hue = ((h % 360) + 360) % 360;
  return `hsl(${hue}, 55%, 55%)`;
}

async function loadTraces() {
  const since = sinceMsFrom($("traces-range").value);
  $("traces-meta").textContent = "loading…";
  try {
    const r = await authFetch(`/api/traces?sinceMs=${since}&limit=200`);
    if (!r.ok) throw new Error("HTTP " + r.status);
    const traces = await r.json();
    renderTraces(traces);
    $("traces-meta").textContent = `${traces.length} trace${traces.length===1?"":"s"}`;
  } catch (e) {
    $("traces-meta").textContent = "error: " + e.message;
  }
}
function renderTraces(traces) {
  const tbody = $("traces-rows");
  tbody.innerHTML = "";
  for (const t of traces) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td><code>${t.traceId.substring(0,16)}…</code></td>
      <td>${escapeHtml(t.rootService)}</td>
      <td>${escapeHtml(t.rootOperation)}</td>
      <td>${fmtDur(t.durationMs)}</td>
      <td>${t.spanCount}</td>
      <td class="${t.errorCount>0?"errcol":""}">${t.errorCount}</td>
      <td>${t.services.map(escapeHtml).join(", ")}</td>
      <td>${fmtAge(t.startMs)}</td>`;
    tr.addEventListener("click", () => openTrace(t.traceId));
    tbody.appendChild(tr);
  }
  if (traces.length === 0) {
    tbody.innerHTML = `<tr><td colspan="8" style="text-align:center;color:var(--muted);padding:20px;">No traces in window.</td></tr>`;
  }
}
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({
    "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"
  })[c]);
}

async function openTrace(traceId) {
  $("trace-modal-title").textContent = "Trace " + traceId;
  $("trace-modal-body").innerHTML = "<div class='meta'>loading…</div>";
  $("trace-modal").classList.add("open");
  try {
    const r = await authFetch("/api/traces/" + encodeURIComponent(traceId));
    if (!r.ok) throw new Error("HTTP " + r.status);
    const data = await r.json();
    renderWaterfall(data);
  } catch (e) {
    $("trace-modal-body").innerHTML = "<div class='meta'>error: " + escapeHtml(e.message) + "</div>";
  }
}
function renderWaterfall(data) {
  const spans = data.spans.slice().sort((a, b) => a.startMs - b.startMs);
  if (spans.length === 0) {
    $("trace-modal-body").innerHTML = "<div class='meta'>no spans</div>";
    return;
  }
  const t0 = Math.min(...spans.map(s => s.startMs));
  const t1 = Math.max(...spans.map(s => s.endMs));
  const span = Math.max(1, t1 - t0);
  const rows = spans.map(s => {
    const leftPct  = ((s.startMs - t0) / span) * 100;
    const widthPct = Math.max(0.4, ((s.endMs - s.startMs) / span) * 100);
    const colour = svcColor(s.service);
    const lbl = `${s.service} · ${s.operation}`;
    return `
      <div class="row" title="${escapeHtml(lbl)} — ${fmtDur(s.durationMs)} (status=${s.statusCode})">
        <div class="lbl"><span class="lbl-txt">${escapeHtml(lbl)}</span><a class="wf-metrics-link" data-svc="${escapeHtml(s.service)}" title="Show metrics for ${escapeHtml(s.service)}">metrics →</a></div>
        <div class="bar-track">
          <div class="bar ${s.error?"err":""}"
               style="left:${leftPct}%;width:${widthPct}%;background:${colour};"></div>
        </div>
        <div class="dur">${fmtDur(s.durationMs)}</div>
      </div>`;
  }).join("");
  const summary = data.summary;
  $("trace-modal-body").innerHTML = `
    <div class="meta">
      ${escapeHtml(summary.rootService)} · ${escapeHtml(summary.rootOperation)}
      — ${fmtDur(summary.durationMs)} total, ${summary.spanCount} spans,
      ${summary.errorCount} error${summary.errorCount===1?"":"s"},
      services: ${summary.services.map(escapeHtml).join(", ")}
    </div>
    <div class="waterfall">${rows}</div>`;
  // Trace → metrics jump (PLAN-NEXT 14.4): each span links to its service's metrics.
  $("trace-modal-body").querySelectorAll(".wf-metrics-link").forEach(a => {
    a.addEventListener("click", (ev) => {
      ev.stopPropagation();
      $("trace-modal").classList.remove("open");
      showMetricsForService(a.getAttribute("data-svc"));
    });
  });
}

$("traces-refresh").addEventListener("click", loadTraces);
$("traces-range").addEventListener("change", loadTraces);
$("trace-modal-close").addEventListener("click", () => $("trace-modal").classList.remove("open"));
$("trace-modal").addEventListener("click", (e) => {
  if (e.target === $("trace-modal")) $("trace-modal").classList.remove("open");
});

// ── Inline runbook (PLAN-NEXT 14.1) ──────────────────────────────────
// Presents the acker with the alert's runbook as a tracked checklist.
// Toggling a step PATCHes progress server-side (which records the
// `pulse_runbook_step_seconds` metric); an "Acknowledge" button reuses
// the existing on-call ack endpoint.
let _runbookFp = null;

window.openRunbook = async function (fp, name) {
  _runbookFp = fp;
  $("runbook-modal-title").textContent = "Runbook — " + (name || fp);
  $("runbook-modal-body").innerHTML = "<div class='meta'>loading…</div>";
  $("runbook-modal").classList.add("open");
  await refreshRunbook();
};

async function refreshRunbook() {
  const fp = _runbookFp;
  try {
    const r = await authFetch("/api/alerts/" + encodeURIComponent(fp) + "/runbook");
    if (!r.ok) throw new Error("HTTP " + r.status);
    renderRunbook(await r.json());
  } catch (e) {
    $("runbook-modal-body").innerHTML =
      "<div class='meta'>error: " + escapeHtml(e.message) + "</div>";
  }
}

function renderRunbook(p) {
  const steps = p.steps || [];
  const done  = steps.filter(s => s.done).length;
  const pct   = steps.length ? Math.round((done / steps.length) * 100) : 0;
  const mttr  = p.resolvedAt
    ? "MTTR " + fmtDur(p.resolvedAt - p.firedAt)
    : "open " + fmtDur(Date.now() - p.firedAt);
  const rows = steps.length
    ? steps.map(s => `
        <label style="display:flex;gap:8px;align-items:flex-start;padding:6px 4px;
               border-bottom:1px solid var(--border);cursor:pointer;">
          <input type="checkbox" data-idx="${s.idx}" ${s.done ? "checked" : ""}
                 style="margin-top:2px;" />
          <span style="flex:1;${s.done ? "color:var(--muted);text-decoration:line-through;" : ""}">
            ${escapeHtml(s.text)}
            ${s.done ? `<span style="color:var(--muted);font-size:10px;">
                          — ${escapeHtml(s.user || "")} ${fmtTs(s.at)}</span>` : ""}
          </span>
        </label>`).join("")
    : `<div class="meta">This runbook has no checklist steps.</div>`;
  $("runbook-modal-body").innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px;">
      <span style="font-size:12px;color:var(--muted);">${done}/${steps.length} steps · ${pct}% · ${escapeHtml(mttr)}</span>
      <button id="runbook-ack" class="primary">Acknowledge</button>
    </div>
    <div style="height:6px;background:var(--bg);border-radius:3px;margin-bottom:10px;">
      <div style="height:100%;width:${pct}%;background:var(--accent);border-radius:3px;"></div>
    </div>
    <div>${rows}</div>
    <details style="margin-top:12px;">
      <summary style="cursor:pointer;font-size:12px;color:var(--muted);">Full runbook</summary>
      <pre style="background:var(--panel-2);border-radius:6px;padding:10px;font-size:12px;
           margin-top:8px;overflow-x:auto;white-space:pre-wrap;">${escapeHtml(p.runbook || "")}</pre>
    </details>`;

  $("runbook-modal-body").querySelectorAll("input[type=checkbox]").forEach(cb => {
    cb.addEventListener("change", async () => {
      const idx = +cb.getAttribute("data-idx");
      cb.disabled = true;
      try {
        const r = await authFetch(
          "/api/alerts/" + encodeURIComponent(_runbookFp) + "/runbook/step",
          { method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ idx, done: cb.checked, user: currentUser() }) });
        if (!r.ok) throw new Error("HTTP " + r.status);
        renderRunbook(await r.json());
      } catch (e) {
        cb.disabled = false;
        alert("Failed to update step: " + e.message);
      }
    });
  });

  const ackBtn = $("runbook-ack");
  if (ackBtn) ackBtn.addEventListener("click", async () => {
    ackBtn.disabled = true;
    try {
      const r = await authFetch(
        "/api/alerts/" + encodeURIComponent(_runbookFp) + "/ack",
        { method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ user: currentUser() }) });
      if (!r.ok) throw new Error("HTTP " + r.status);
      ackBtn.textContent = "Acknowledged";
      ackBtn.classList.remove("primary");
      ackBtn.classList.add("saved");
    } catch (e) {
      ackBtn.disabled = false;
      alert("Failed to acknowledge: " + e.message);
    }
  });
}

function currentUser() {
  try { return (window.PULSE_USER || localStorage.getItem("pb_user") || "operator"); }
  catch { return "operator"; }
}

$("runbook-modal-close").addEventListener("click", () => $("runbook-modal").classList.remove("open"));
$("runbook-modal").addEventListener("click", (e) => {
  if (e.target === $("runbook-modal")) $("runbook-modal").classList.remove("open");
});

// ── Alert rule + runbook editor ──────────────────────────────────────
// CRUD over /api/rules. Rules are nested inside groups, so editing a
// single rule means mutating its group's `rules` array and PUTting the
// whole group back. The runbook is just the rule's `runbook` markdown.
let _ruleGroups = [];               // cached [{id,name,intervalMs,rules,...}]
let _ruleEditCtx = null;            // { groupId, ruleIndex }  (ruleIndex -1 = new)

function mapToLines(m) {
  return Object.entries(m || {}).map(([k, v]) => k + "=" + v).join("\n");
}
function linesToMap(text) {
  const m = {};
  for (const raw of (text || "").split("\n")) {
    const line = raw.trim();
    if (!line) continue;
    const eq = line.indexOf("=");
    if (eq < 0) continue;
    const k = line.slice(0, eq).trim();
    const v = line.slice(eq + 1).trim();
    if (k) m[k] = v;
  }
  return m;
}

async function loadRules() {
  const host = $("rules-list");
  host.innerHTML = '<div class="rules-empty" style="padding:0 20px;">Loading…</div>';
  try {
    const groups = await api("GET", "/api/rules");
    _ruleGroups = Array.isArray(groups) ? groups : [];
    renderRules();
  } catch (e) {
    host.innerHTML = `<div class="rules-empty" style="padding:0 20px;color:var(--err);">Failed to load rules: ${escapeHtml(e.message)}</div>`;
  }
}

function renderRules() {
  const host = $("rules-list");
  if (!_ruleGroups.length) {
    host.innerHTML = '<div class="rules-empty" style="padding:0 20px;">No rule groups yet. Create one to get started.</div>';
    return;
  }
  host.innerHTML = _ruleGroups.map(g => {
    const rules = g.rules || [];
    const rows = rules.length
      ? rules.map((r, i) => {
          const sev = (r.severity || "warning").toLowerCase();
          return `<div class="rule-row">
            <div class="grow">
              <div class="name">${escapeHtml(r.name || "(unnamed)")}
                ${r.runbook ? '<span class="rb-flag" title="Has a runbook">● runbook</span>' : ""}
              </div>
              <div class="expr">${escapeHtml(r.expr || "")} ${escapeHtml(r.cmp || "")} ${escapeHtml(String(r.threshold ?? ""))}</div>
            </div>
            <span class="sev ${sev}">${escapeHtml(sev)}</span>
            <button data-act="edit-rule" data-g="${escapeHtml(g.id)}" data-i="${i}">Edit</button>
            <button class="danger" data-act="del-rule" data-g="${escapeHtml(g.id)}" data-i="${i}">Delete</button>
          </div>`;
        }).join("")
      : '<div class="rule-row"><span class="rules-empty">No rules in this group yet.</span></div>';
    return `<div class="rules-group">
      <div class="rules-group-head">
        <h3>${escapeHtml(g.name || g.id)}</h3>
        <span class="meta">every ${Math.round((g.intervalMs || 0) / 1000)}s · ${rules.length} rule(s)</span>
        <span class="grow"></span>
        <button data-act="add-rule" data-g="${escapeHtml(g.id)}">+ Add rule</button>
        <button data-act="interval" data-g="${escapeHtml(g.id)}">Interval</button>
        <button data-act="rename" data-g="${escapeHtml(g.id)}">Rename</button>
        <button class="danger" data-act="del-group" data-g="${escapeHtml(g.id)}">Delete group</button>
      </div>
      ${rows}
    </div>`;
  }).join("");

  host.querySelectorAll("button[data-act]").forEach(btn => {
    btn.addEventListener("click", () => onRulesAction(
      btn.getAttribute("data-act"),
      btn.getAttribute("data-g"),
      btn.hasAttribute("data-i") ? +btn.getAttribute("data-i") : -1));
  });
}

function groupById(id) { return _ruleGroups.find(g => g.id === id); }

async function onRulesAction(act, groupId, ruleIndex) {
  const g = groupById(groupId);
  try {
    if (act === "add-rule")  { openRuleEditor(groupId, -1); return; }
    if (act === "edit-rule") { openRuleEditor(groupId, ruleIndex); return; }
    if (act === "del-rule") {
      if (!g) return;
      const r = g.rules[ruleIndex];
      if (!confirm(`Delete rule "${r && r.name}"?`)) return;
      const next = { ...g, rules: g.rules.filter((_, i) => i !== ruleIndex) };
      await api("PUT", "/api/rules/" + encodeURIComponent(groupId), next);
      await loadRules();
      return;
    }
    if (act === "rename") {
      if (!g) return;
      const name = prompt("Group name", g.name || "");
      if (!name) return;
      await api("PUT", "/api/rules/" + encodeURIComponent(groupId), { ...g, name });
      await loadRules();
      return;
    }
    if (act === "interval") {
      if (!g) return;
      const sec = prompt("Evaluation interval (seconds)", String(Math.round((g.intervalMs || 30000) / 1000)));
      if (sec === null) return;
      const n = Math.max(1, Math.round(+sec || 0));
      await api("PUT", "/api/rules/" + encodeURIComponent(groupId), { ...g, intervalMs: n * 1000 });
      await loadRules();
      return;
    }
    if (act === "del-group") {
      if (!confirm(`Delete group "${g && g.name}" and all its rules?`)) return;
      await api("DELETE", "/api/rules/" + encodeURIComponent(groupId));
      await loadRules();
      return;
    }
  } catch (e) {
    alert("Action failed: " + e.message);
  }
}

function openRuleEditor(groupId, ruleIndex) {
  const g = groupById(groupId);
  if (!g) return;
  _ruleEditCtx = { groupId, ruleIndex };
  const r = ruleIndex >= 0 ? g.rules[ruleIndex] : {};
  $("rule-modal-title").textContent =
    (ruleIndex >= 0 ? "Edit rule" : "New rule") + " — " + (g.name || g.id);
  $("rule-f-name").value        = r.name || "";
  $("rule-f-lang").value        = r.lang || "promql";
  $("rule-f-severity").value    = (r.severity || "warning").toLowerCase();
  $("rule-f-expr").value        = r.expr || "";
  $("rule-f-cmp").value         = r.cmp || ">";
  $("rule-f-threshold").value   = r.threshold ?? 0;
  $("rule-f-for").value         = Math.round((r.forMs || 0) / 1000);
  $("rule-f-labels").value      = mapToLines(r.labels);
  $("rule-f-annotations").value = mapToLines(r.annotations);
  $("rule-f-runbook").value     = r.runbook || "";
  $("rule-f-err").textContent   = "";
  $("rule-modal").classList.add("open");
  $("rule-f-name").focus();
}

async function saveRuleFromEditor() {
  if (!_ruleEditCtx) return;
  const { groupId, ruleIndex } = _ruleEditCtx;
  const g = groupById(groupId);
  if (!g) return;
  const name = $("rule-f-name").value.trim();
  const expr = $("rule-f-expr").value.trim();
  if (!name) { $("rule-f-err").textContent = "Name is required."; return; }
  if (!expr) { $("rule-f-err").textContent = "Expression is required."; return; }
  const runbook = $("rule-f-runbook").value;
  const rule = {
    id:          ruleIndex >= 0 ? (g.rules[ruleIndex].id || ("r-" + uuid())) : ("r-" + uuid()),
    name,
    lang:        $("rule-f-lang").value,
    expr,
    cmp:         $("rule-f-cmp").value,
    threshold:   +$("rule-f-threshold").value || 0,
    forMs:       Math.max(0, Math.round(+$("rule-f-for").value || 0)) * 1000,
    severity:    $("rule-f-severity").value,
    labels:      linesToMap($("rule-f-labels").value),
    annotations: linesToMap($("rule-f-annotations").value),
    runbook:     runbook.trim() ? runbook : null,
  };
  const rules = (g.rules || []).slice();
  if (ruleIndex >= 0) rules[ruleIndex] = rule; else rules.push(rule);
  const btn = $("rule-f-save");
  btn.disabled = true;
  try {
    await api("PUT", "/api/rules/" + encodeURIComponent(groupId), { ...g, rules });
    $("rule-modal").classList.remove("open");
    await loadRules();
  } catch (e) {
    $("rule-f-err").textContent = "Save failed: " + e.message;
  } finally {
    btn.disabled = false;
  }
}

async function newRuleGroup() {
  const name = prompt("New group name", "alerts");
  if (!name) return;
  try {
    await api("POST", "/api/rules", { name, intervalMs: 30000, rules: [] });
    await loadRules();
  } catch (e) {
    alert("Could not create group: " + e.message);
  }
}

$("rules-new-group-btn").addEventListener("click", newRuleGroup);
$("rule-f-save").addEventListener("click", saveRuleFromEditor);
$("rule-modal-close").addEventListener("click", () => $("rule-modal").classList.remove("open"));
$("rule-modal").addEventListener("click", (e) => {
  if (e.target === $("rule-modal")) $("rule-modal").classList.remove("open");
});


// =====================================================================
// Service Map tab — SVG with nodes on a circle (Phase 4 #4)
// =====================================================================
// ── Phase 13: Agents fleet ──────────────────────────────────────────────────
async function loadAgents() {
  const tbody = $("agents-body");
  tbody.innerHTML = '<tr><td colspan="5" style="color:var(--muted)">Loading…</td></tr>';
  try {
    const list = await api("GET", "/api/agents");
    renderAgents(Array.isArray(list) ? list : []);
  } catch (e) {
    tbody.innerHTML = `<tr><td colspan="5" style="color:var(--danger)">Failed: ${e.message}</td></tr>`;
  }
}

function agentStatus(lastSeenMs) {
  const age = Date.now() - lastSeenMs;
  if (age < 90_000)   return "online";
  if (age < 300_000)  return "degraded";
  return "offline";
}

function fmtAgo(ms) {
  if (!ms) return "never";
  const s = Math.floor((Date.now() - ms) / 1000);
  if (s < 5)   return "just now";
  if (s < 90)  return s + "s ago";
  if (s < 3600) return Math.floor(s/60) + "m ago";
  if (s < 86400) return Math.floor(s/3600) + "h ago";
  return Math.floor(s/86400) + "d ago";
}

function renderAgents(list) {
  const tbody = $("agents-body");
  if (!list.length) {
    tbody.innerHTML = '<tr><td colspan="5" style="color:var(--muted)">No agents enrolled yet. Generate an enrollment token and run install.sh on your hosts.</td></tr>';
    return;
  }
  tbody.innerHTML = list.map(a => {
    const st = agentStatus(a.lastSeen);
    return `<tr>
      <td><span class="agent-dot ${st}" title="${st}"></span></td>
      <td>${escapeHtml(a.hostname || a.id)}</td>
      <td style="color:var(--muted);font-size:12px;">${escapeHtml(a.version || "—")}</td>
      <td title="${new Date(a.lastSeen).toISOString()}">${fmtAgo(a.lastSeen)}</td>
      <td style="color:var(--muted);font-size:12px;" title="${new Date(a.enrolledAt).toISOString()}">${fmtAgo(a.enrolledAt)}</td>
    </tr>`;
  }).join("");
}

async function generateEnrollToken() {
  try {
    const data = await api("POST", "/api/agents/token");
    const tok = (data && data.token) || "";
    const baseUrl = location.origin;
    $("agents-token-value").textContent = tok;
    $("agents-token-snippet").textContent =
`[agent]
workspace_url = "${baseUrl}"
enroll_token  = "${tok}"

[sources.host_metrics]
interval = "15s"`;
    $("agents-token-modal").classList.add("open");
  } catch(e) {
    alert("Could not generate token: " + e.message);
  }
}

$("agents-gen-token-btn").addEventListener("click", generateEnrollToken);
$("agents-token-modal-close").addEventListener("click", () => $("agents-token-modal").classList.remove("open"));
$("agents-token-modal").addEventListener("click", (e) => {
  if (e.target === $("agents-token-modal")) $("agents-token-modal").classList.remove("open");
});

// ── End Phase 13 agents ─────────────────────────────────────────────────────
async function loadServiceMap() {
  const since = sinceMsFrom($("map-range").value);
  $("map-meta").textContent = "loading…";
  try {
    const r = await authFetch(`/api/servicemap?sinceMs=${since}`);
    if (!r.ok) throw new Error("HTTP " + r.status);
    const m = await r.json();
    renderServiceMap(m);
    $("map-meta").textContent =
      `${m.nodes.length} service${m.nodes.length===1?"":"s"}, ${m.edges.length} edge${m.edges.length===1?"":"s"}`;
  } catch (e) {
    $("map-meta").textContent = "error: " + e.message;
  }
}
function renderServiceMap(m) {
  const svg = $("map-svg");
  svg.innerHTML = "";
  const tip = $("map-tip");
  tip.style.display = "none";
  if (m.nodes.length === 0) {
    svg.innerHTML = `<text x="450" y="300" fill="#888" text-anchor="middle"
      font-size="14">No spans in window.</text>`;
    return;
  }
  // Lay nodes out on a circle.
  const cx = 450, cy = 300;
  const r = Math.min(220, 80 + 25 * m.nodes.length);
  const pos = {};
  m.nodes.forEach((n, i) => {
    const a = (i / m.nodes.length) * Math.PI * 2 - Math.PI/2;
    pos[n.service] = { x: cx + r * Math.cos(a), y: cy + r * Math.sin(a) };
  });

  // Edges first so circles draw on top.
  const maxCalls = Math.max(1, ...m.edges.map(e => e.callCount));
  for (const e of m.edges) {
    const a = pos[e.from], b = pos[e.to];
    if (!a || !b) continue;
    const errRate = e.callCount === 0 ? 0 : e.errorCount / e.callCount;
    const hue = (1 - errRate) * 120;   // 120=green, 0=red
    const width = 1 + 4 * (e.callCount / maxCalls);
    const g = document.createElementNS("http://www.w3.org/2000/svg", "g");
    g.setAttribute("class", "edge");
    const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
    line.setAttribute("x1", a.x); line.setAttribute("y1", a.y);
    line.setAttribute("x2", b.x); line.setAttribute("y2", b.y);
    line.setAttribute("stroke", `hsl(${hue},65%,55%)`);
    line.setAttribute("stroke-width", width);
    // Arrowhead via a triangle 14px from target.
    const dx = b.x - a.x, dy = b.y - a.y;
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx/len, uy = dy/len;
    const tx = b.x - ux * 38, ty = b.y - uy * 38;
    const px = -uy, py = ux;
    const tri = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
    tri.setAttribute("points",
      `${tx+ux*8},${ty+uy*8} ${tx+px*5},${ty+py*5} ${tx-px*5},${ty-py*5}`);
    tri.setAttribute("fill", `hsl(${hue},65%,55%)`);
    g.appendChild(line); g.appendChild(tri);
    const tt =
`${e.from} → ${e.to}
calls: ${e.callCount}   errors: ${e.errorCount}
p50: ${fmtDur(e.p50Ms)}   p95: ${fmtDur(e.p95Ms)}   p99: ${fmtDur(e.p99Ms)}`;
    g.addEventListener("mousemove", ev => showMapTip(ev, tt));
    g.addEventListener("mouseleave", () => tip.style.display = "none");
    svg.appendChild(g);
  }

  // Nodes.
  for (const n of m.nodes) {
    const p = pos[n.service];
    const g = document.createElementNS("http://www.w3.org/2000/svg", "g");
    g.setAttribute("class", "node");
    g.setAttribute("transform", `translate(${p.x},${p.y})`);
    const errRate = n.spanCount === 0 ? 0 : n.errorCount / n.spanCount;
    const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
    circle.setAttribute("r", "28");
    if (errRate > 0.05) circle.setAttribute("class", "err");
    const txt = document.createElementNS("http://www.w3.org/2000/svg", "text");
    txt.setAttribute("dy", "4");
    txt.textContent = n.service.length > 12 ? n.service.substring(0,11) + "…" : n.service;
    g.appendChild(circle); g.appendChild(txt);
    const tt =
`${n.service}
spans: ${n.spanCount}   errors: ${n.errorCount}
p50: ${fmtDur(n.p50Ms)}   p95: ${fmtDur(n.p95Ms)}   p99: ${fmtDur(n.p99Ms)}`;
    g.addEventListener("mousemove", ev => showMapTip(ev, tt));
    g.addEventListener("mouseleave", () => tip.style.display = "none");
    svg.appendChild(g);
  }
}
function showMapTip(ev, text) {
  const tip = $("map-tip");
  tip.textContent = text;
  tip.style.display = "block";
  const body = $("view-map").querySelector(".body");
  const rect = body.getBoundingClientRect();
  tip.style.left = (ev.clientX - rect.left + 12) + "px";
  tip.style.top  = (ev.clientY - rect.top  + 12) + "px";
}

$("map-refresh").addEventListener("click", loadServiceMap);
$("map-range").addEventListener("change", loadServiceMap);

window.addEventListener("hashchange", router);
router();

// Populate panel-type <select> from the registry so new panel plugins
// appear automatically without touching HTML.
(function populatePanelTypeSelect() {
  const sel = $("ed-type");
  sel.innerHTML = "";
  for (const { type, label } of PulseBoard.panelTypes()) {
    const o = document.createElement("option");
    o.value = type; o.textContent = label;
    sel.appendChild(o);
  }
})();

(async () => {
  if (!pbBearer()) { pbRedirectSignin(); return; }
  try { await reloadList(); }
  catch (e) { console.warn("dashboard load failed", e); }
  connectWs();
})();
