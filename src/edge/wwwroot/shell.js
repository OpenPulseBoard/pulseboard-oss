(function () {
  const COLLAPSED_KEY = "pb.sidebarCollapsed";
  const THEME_KEY = "pb.theme";

  const NAV_ITEMS = [
    { id: "tab-dashboards", href: "/app#/dashboards", label: "Dashboards",  icon: "\uD83D\uDCCA" },
    { id: "tab-explore",    href: "/app#/explore",    label: "Explore",     icon: "\uD83D\uDD0E" },
    { id: "tab-traces",     href: "/app#/traces",     label: "Traces",      icon: "\uD83E\uDDED" },
    { id: "tab-map",        href: "/app#/map",        label: "Service Map", icon: "\uD83D\uDDFA\uFE0F" },
    { id: "tab-library",    href: "/app#/library",    label: "Library",     icon: "\uD83D\uDCDA" },
    { id: "tab-alerts",     href: "/app#/alerts",     label: "Alerts",      icon: "\uD83D\uDD14", badgeId: "alerts-nav-badge" },
    { id: "tab-agents",     href: "/app#/agents",     label: "Agents",      icon: "\uD83E\uDD16" },
    { id: "tab-synthetics", href: "/app#/uptime",     label: "Uptime",      icon: "\u23F1\uFE0F" },
    { id: "tab-status",     href: "/app#/status",     label: "Status",      icon: "\uD83D\uDEA6" },
    { id: "tab-live",       href: "/live",            label: "Live",        icon: "\uD83D\uDFE2" },
    { id: "tab-admin",      href: "/admin",           label: "Admin",       icon: "\u2699\uFE0F" }
  ];

  function byId(id) { return document.getElementById(id); }

  function activeNavId() {
    const p = location.pathname || "/";
    if (p.indexOf("/admin") === 0) return "tab-admin";
    if (p.indexOf("/live") === 0)  return "tab-live";
    const h = (location.hash || "").replace(/^#\/?/, "").split(/[?\/]/)[0];
    const map = {
      "":           "tab-dashboards",
      "dashboards": "tab-dashboards",
      "explore":    "tab-explore",
      "traces":     "tab-traces",
      "map":        "tab-map",
      "library":    "tab-library",
      "alerts":     "tab-alerts",
      "agents":     "tab-agents",
      "uptime":     "tab-synthetics",
      "synthetics": "tab-synthetics",
      "status":     "tab-status"
    };
    return map[h] || "tab-dashboards";
  }

  function renderSidebar() {
    const nav = byId("main-nav");
    if (!nav) return;
    const active = activeNavId();
    nav.innerHTML = "";
    NAV_ITEMS.forEach(function (item) {
      const a = document.createElement("a");
      a.id = item.id;
      a.href = item.href;
      a.title = item.label;
      if (item.id === active) a.classList.add("active");

      const icon = document.createElement("span");
      icon.className = "nav-icon";
      icon.setAttribute("aria-hidden", "true");
      icon.textContent = item.icon;
      a.appendChild(icon);

      const label = document.createElement("span");
      label.className = "nav-label";
      label.textContent = item.label;
      a.appendChild(label);

      if (item.badgeId) {
        const badge = document.createElement("span");
        badge.id = item.badgeId;
        badge.className = "nav-badge hidden";
        a.appendChild(badge);
      }
      nav.appendChild(a);
    });
  }

  function syncActiveNav() {
    const nav = byId("main-nav");
    if (!nav) return;
    const active = activeNavId();
    NAV_ITEMS.forEach(function (item) {
      const el = byId(item.id);
      if (el) el.classList.toggle("active", item.id === active);
    });
  }

  function applyTheme(isDark) {
    document.documentElement.setAttribute("data-theme", isDark ? "dark" : "light");
    const btn = byId("theme-toggle");
    const logo = byId("brand-logo");
    if (btn) {
      btn.textContent = isDark ? "\uD83C\uDF19" : "\u2600\uFE0F";
      btn.title = isDark ? "Switch to light mode" : "Switch to dark mode";
    }
    if (logo) {
      logo.src = isDark ? "/pulse-board-logo-dark.svg" : "/pulse-board-logo-alternative.svg";
    }
    document.dispatchEvent(new CustomEvent("pb:themechange", {
      detail: { theme: isDark ? "dark" : "light" }
    }));
  }

  function initTheme() {
    if (!byId("theme-toggle")) return;
    const stored = localStorage.getItem(THEME_KEY);
    let dark = stored ? stored !== "light" : !window.matchMedia("(prefers-color-scheme: light)").matches;
    applyTheme(dark);
    byId("theme-toggle").addEventListener("click", function () {
      dark = !dark;
      localStorage.setItem(THEME_KEY, dark ? "dark" : "light");
      applyTheme(dark);
    });
  }

  async function setWorkspaceBadge() {
    const el = byId("workspace-slug");
    const badge = byId("workspace-badge");
    if (!el) return;

    const render = function (slug, host, confirmed) {
      el.textContent = slug;
      if (badge) {
        badge.title =
          (confirmed ? "Workspace: " : "Workspace (unconfirmed): ") + slug +
          (host ? " · host: " + host : "");
      }
    };

    try {
      const tok = sessionStorage.getItem("pb.bearer");
      const headers = tok ? { "Authorization": "Bearer " + tok } : {};
      const r = await fetch("/auth/me", { headers, credentials: "same-origin" });
      if (r.ok) {
        const me = await r.json();
        if (me && me.slug) {
          render(me.slug, location.hostname, true);
          return;
        }
      }
    } catch (_) {}

    const host = location.hostname || "";
    const parts = host.split(".");
    const slug = (parts.length >= 3 && parts[0] !== "www") ? parts[0]
      : (host === "localhost" || /^\d+\.\d+\.\d+\.\d+$/.test(host)) ? "local"
      : host;
    render(slug + " ?", host, false);
  }

  function isMobileSidebar() {
    return window.matchMedia("(max-width: 980px)").matches;
  }

  function setToggleAria(expanded) {
    const btn = byId("sidebar-toggle");
    if (btn) btn.setAttribute("aria-expanded", expanded ? "true" : "false");
  }

  function applySidebarLayoutState() {
    if (!byId("sidebar-toggle") || !document.querySelector("header.topbar")) return;
    if (isMobileSidebar()) {
      document.body.classList.remove("sidebar-collapsed");
      setToggleAria(document.body.classList.contains("sidebar-open"));
      return;
    }
    document.body.classList.remove("sidebar-open");
    const collapsed = localStorage.getItem(COLLAPSED_KEY) === "1";
    document.body.classList.toggle("sidebar-collapsed", collapsed);
    setToggleAria(!collapsed);
  }

  function closeMobileSidebar() {
    if (!isMobileSidebar()) return;
    document.body.classList.remove("sidebar-open");
    setToggleAria(false);
  }

  function initSidebar() {
    const btn = byId("sidebar-toggle");
    const nav = byId("main-nav");
    if (!btn || !nav || !document.querySelector("header.topbar")) return;

    document.body.classList.add("shell-ready");

    btn.addEventListener("click", function () {
      if (isMobileSidebar()) {
        const opening = !document.body.classList.contains("sidebar-open");
        document.body.classList.toggle("sidebar-open", opening);
        setToggleAria(opening);
        return;
      }
      const collapsed = !document.body.classList.contains("sidebar-collapsed");
      document.body.classList.toggle("sidebar-collapsed", collapsed);
      localStorage.setItem(COLLAPSED_KEY, collapsed ? "1" : "0");
      setToggleAria(!collapsed);
    });

    const backdrop = byId("sidebar-backdrop");
    if (backdrop) backdrop.addEventListener("click", closeMobileSidebar);

    nav.addEventListener("click", function (e) {
      if (e.target && e.target.closest("a")) closeMobileSidebar();
    });

    window.addEventListener("resize", applySidebarLayoutState);
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeMobileSidebar();
    });

    applySidebarLayoutState();
  }

  initTheme();
  renderSidebar();
  initSidebar();
  setWorkspaceBadge();
  window.addEventListener("hashchange", syncActiveNav);
})();
