(function () {
  const COLLAPSED_KEY = "pb.sidebarCollapsed";
  const THEME_KEY = "pb.theme";

  function byId(id) { return document.getElementById(id); }

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
  initSidebar();
  setWorkspaceBadge();
})();
