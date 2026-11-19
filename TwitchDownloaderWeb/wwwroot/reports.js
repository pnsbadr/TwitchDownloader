"use strict";

// Error and bug reports (badrfly, 2026-11-18). Loaded first on every page. Sends the page's own crashes (script errors and unhandled
// promise rejections) to POST /api/auth/reports, and opens the "Report a problem" box (window.clipcoReports.openBugDialog()).
// Each report carries what was going on: the page, screen size, what the page says it has open (setContext), and the last
// requests and errors (breadcrumbs). The server removes tokens; the URL's fragment (link tokens) is never sent.
(() => {
  const MAX_AUTO_REPORTS = 15;          // per page load
  const REPEAT_SECONDS = 60;            // the same error again within this is not sent again
  const MAX_CRUMBS = 30;
  const visitId = (crypto.randomUUID?.() ?? `${Date.now()}-${Math.random()}`).slice(0, 36);
  const loadedAt = Date.now();
  const crumbs = [];
  const lastSent = new Map();
  let autoSent = 0;
  let contextProvider = null;

  function crumb(kind, text) {
    crumbs.push({ at: new Date().toISOString(), kind, text: String(text).slice(0, 300) });
    if (crumbs.length > MAX_CRUMBS) crumbs.shift();
  }

  function pageUrl() {
    return location.pathname + location.search;
  }

  function context() {
    const base = {
      page: location.pathname,
      secondsOnPage: Math.round((Date.now() - loadedAt) / 1000),
      screen: `${screen.width}x${screen.height}`,
      window: `${innerWidth}x${innerHeight}`,
      pixelRatio: devicePixelRatio,
      language: navigator.language,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      online: navigator.onLine,
      visible: document.visibilityState,
    };
    let page = null;
    try { page = contextProvider?.() ?? null; } catch (error) { page = { contextFailed: String(error) }; }
    return { ...base, ...(page ? { app: page } : {}), recent: crumbs.slice() };
  }

  async function send(body) {
    const response = await fetch("/api/auth/reports", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...body, url: pageUrl(), visitId, context: context() }),
      keepalive: true,
    });
    if (!response.ok) {
      const error = new Error(response.status === 429 ? "Too many reports from here. Try again in a few minutes." : `Couldn't send it (${response.status}).`);
      error.status = response.status;
      throw error;
    }
    return response.json();
  }

  function reportError(error, fallbackMessage, extra = {}) {
    const message = String(error?.message ?? fallbackMessage ?? error ?? "Unknown error").slice(0, 2000);
    // Noise browsers raise that isn't a bug of the page
    if (/ResizeObserver loop|^Script error\.?$/i.test(message)) return;
    if (error?.name === "AbortError") return;
    const stack = typeof error?.stack === "string" ? error.stack : "";
    const key = `${message}|${stack.split("\n")[1] ?? ""}`;
    const now = Date.now();
    if (lastSent.has(key) && now - lastSent.get(key) < REPEAT_SECONDS * 1000) return;
    if (autoSent >= MAX_AUTO_REPORTS) return;
    lastSent.set(key, now);
    autoSent += 1;
    const app = (() => { try { return contextProvider?.() ?? {}; } catch { return {}; } })();
    send({
      kind: "error",
      message,
      errorType: error?.name ?? (typeof error === "object" ? error?.constructor?.name : typeof error) ?? "Error",
      details: [stack, extra.where].filter(Boolean).join("\n\n") || null,
      vodId: Number(app.vodId) || null,
      entryId: Number(app.entryId) || null,
    }).catch(() => { /* reporting must never break the page */ });
    crumb("error", message);
  }

  // A script of the app that didn't load (the page then doesn't work at all): seen in the capture phase on the element
  const failedScripts = new Set();
  window.addEventListener("error", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLScriptElement) || !target.src || failedScripts.has(target.src)) return;
    failedScripts.add(target.src);
    reportError({ name: "ScriptLoadError", message: `A script didn't load: ${target.src}`, stack: "" }, null, { where: "script load" });
  }, true);

  window.addEventListener("error", (event) => {
    if (!(event instanceof ErrorEvent)) return; // a failed image or script load, not a crash
    reportError(event.error, event.message, { where: event.filename ? `${event.filename}:${event.lineno}:${event.colno}` : "" });
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason;
    // A failed API call the page didn't catch: the server already reported a server error; only report the page side of it
    reportError(reason instanceof Error ? reason : new Error(typeof reason === "string" ? reason : JSON.stringify(reason) ?? "Promise rejected"),
      "Unhandled promise rejection", { where: "unhandled promise rejection" });
  });
  const consoleError = console.error.bind(console);
  console.error = (...args) => {
    try { crumb("console", args.map((a) => (a instanceof Error ? a.message : typeof a === "string" ? a : JSON.stringify(a))).join(" ")); } catch { /* ignore */ }
    consoleError(...args);
  };

  // ---------- "Report a problem" ----------

  function openBugDialog() {
    const css = `
      .report-dialog { background: var(--bg-1, #1b1d21); color: var(--text, #f4f5f7); border: 1px solid var(--line, #33373e);
        border-radius: var(--radius, 6px); padding: 20px; width: min(520px, calc(100vw - 32px)); font: 15px/1.4 Barlow, system-ui, sans-serif; }
      .report-dialog::backdrop { background: rgb(0 0 0 / 0.55); }
      .report-dialog h2 { margin: 0 0 8px; font: 600 22px "Barlow Condensed", system-ui, sans-serif; }
      .report-dialog p { margin: 0 0 12px; color: var(--text-dim, #b4b9c2); font-size: 14px; }
      .report-dialog textarea { width: 100%; box-sizing: border-box; min-height: 140px; resize: vertical; background: var(--bg-3, #2c3036);
        color: inherit; border: 1px solid var(--line, #33373e); border-radius: var(--radius-small, 4px); padding: 8px; font: inherit; }
      .report-dialog .report-row { display: flex; gap: 8px; justify-content: flex-end; margin-top: 12px; align-items: center; }
      .report-dialog .report-status { margin-right: auto; font-size: 14px; color: var(--text-dim, #b4b9c2); }
      .report-dialog button { font: inherit; padding: 6px 14px; border-radius: var(--radius-small, 4px); border: 1px solid var(--line, #33373e);
        background: var(--bg-3, #2c3036); color: inherit; cursor: pointer; }
      .report-dialog button.primary { background: var(--accent, #8b7cf6); border-color: transparent; color: #fff; }
      .report-dialog button:disabled { opacity: 0.6; cursor: default; }`;
    if (!document.getElementById("report-dialog-style")) {
      const style = document.createElement("style");
      style.id = "report-dialog-style";
      style.textContent = css;
      document.head.append(style);
    }
    const dialog = document.createElement("dialog");
    dialog.className = "report-dialog";
    dialog.innerHTML = `
      <form method="dialog">
        <h2>Report a problem</h2>
        <p>What happened, and what did you expect? What were you doing just before? The page adds what it had open and its recent
          errors, so the app Owner can see where it went wrong.</p>
        <textarea maxlength="4000" required aria-label="What went wrong"></textarea>
        <div class="report-row">
          <span class="report-status" role="status"></span>
          <button type="button" value="cancel">Cancel</button>
          <button type="submit" class="primary">Send</button>
        </div>
      </form>`;
    const text = dialog.querySelector("textarea");
    const status = dialog.querySelector(".report-status");
    const sendButton = dialog.querySelector("button[type=submit]");
    dialog.querySelector("button[value=cancel]").addEventListener("click", () => dialog.close());
    dialog.addEventListener("close", () => dialog.remove());
    // Keys typed here are for the text box, not the editor's shortcuts
    dialog.addEventListener("keydown", (event) => { if (event.key !== "Escape") event.stopPropagation(); });
    dialog.querySelector("form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const note = text.value.trim();
      if (!note) { status.textContent = "Say what went wrong first."; text.focus(); return; }
      sendButton.disabled = true;
      status.textContent = "Sending…";
      const app = (() => { try { return contextProvider?.() ?? {}; } catch { return {}; } })();
      try {
        await send({ kind: "bug", note, vodId: Number(app.vodId) || null, entryId: Number(app.entryId) || null });
        status.textContent = "Sent. Thank you!";
        setTimeout(() => dialog.close(), 1200);
      } catch (error) {
        status.textContent = error.message;
        sendButton.disabled = false;
      }
    });
    document.body.append(dialog);
    dialog.showModal();
    text.focus();
  }

  window.clipcoReports = {
    /** A function returning what the page has open ({ view, vodId, entryId, ... }); added to every report. */
    setContext(fn) { contextProvider = fn; },
    /** Remember a request for the next report: method, path, status, milliseconds. */
    request(method, url, status, ms) {
      let path = url;
      try { const u = new URL(url, location.origin); path = u.pathname + u.search; } catch { /* keep as is */ }
      crumb("request", `${method} ${path} → ${status}${ms != null ? ` (${Math.round(ms)} ms)` : ""}`);
    },
    /** Remember something the person did (switched tab, loaded a VOD…). */
    note(text) { crumb("action", text); },
    /** Send an error the page caught but shouldn't have happened. */
    reportError,
    openBugDialog,
  };
})();
