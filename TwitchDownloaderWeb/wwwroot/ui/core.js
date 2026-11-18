// Shared helpers for the new editor (frontend rework). Plain ES module, no build step.
// keybinds.js is a classic script loaded before this one, so KEYBINDS / keybindLabel are globals.

export const $ = (id) => document.getElementById(id);

// GET /api/auth/me, filled by loadMe(). The server enforces permissions; the page only hides what wouldn't work.
export let me = null;

export function can(permission) {
  return Boolean(me?.permissions.includes(permission));
}

// Shown on the account menu's "Team page" entry when the member has any of these
export const TEAM_PAGE_PERMISSIONS = ["InviteMembers", "AssignRoles", "ManageRoles", "RemoveMembers", "ResetPasswords", "ViewActivity", "ManageAudioSettings"];

// What the page does with a session problem; editor.js sets it so the API helper can show the gate
export const sessionHandlers = { gate: (_text, _heading) => {} };

export async function loadMe() {
  me = await api("GET", "/api/auth/me");
  return me;
}

export async function api(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const started = performance.now();
  let response;
  try {
    response = await fetch(url, options);
  } catch (error) {
    window.clipcoReports?.request(method, url, "network error", performance.now() - started);
    throw error;
  }
  window.clipcoReports?.request(method, url, response.status, performance.now() - started);
  // Player 2 edits that followed a clip or segment this request changed (the server's ProjectSourceSync): editor.js keeps the open
  // edit and its undo history in step
  const synced = response.headers.get("X-Source-Sync");
  if (synced) {
    try {
      window.dispatchEvent(new CustomEvent("clipco:source-sync", { detail: JSON.parse(synced) }));
    } catch {
      // Only keeps the open edit in step; its next save would bring it in line anyway
    }
  }
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (response.status === 401) {
    // Session ended (logged out elsewhere, password changed, account disabled)
    location.href = "/login.html";
  } else if (response.status === 403 && (data?.code === "no-access" || data?.code === "no-team")) {
    sessionHandlers.gate(data.detail);
  } else if (response.status === 403 && data?.code === "support-ended") {
    sessionHandlers.gate(data.detail, "Support view ended");
  }
  if (!response.ok) {
    const error = new Error(problemMessage(data, response.status));
    error.status = response.status;
    error.code = data?.code ?? null;
    error.problem = data;
    throw error;
  }
  return data;
}

export function problemMessage(problem, status) {
  if (problem?.errors) return Object.values(problem.errors).flat().join(" ");
  return problem?.detail || problem?.title || `Request failed (${status}).`;
}

// el("div", { class: "x", onclick: fn, "aria-label": "y" }, child, "text", ...)
export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
    else if (value === true) node.setAttribute(key, "");
    else if (value !== undefined && value !== null && value !== false) node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// ---------- storage (per-viewer conveniences only; the page must work without it) ----------

// A Twitch thumbnail URL at card size; null for templates Twitch hasn't filled yet ("404_processing" while live = a "?" picture)
export function twitchThumb(url) {
  if (!url || url.includes("404_processing")) return null;
  return url.replace("%{width}", "320").replace("%{height}", "180");
}

// Inline style for a thumbnail from several images, first on top: a clip's own frame over the VOD's thumbnail, which shows while
// the frame loads or if it can't be made. Null when there are none.
export function thumbStyle(...urls) {
  const list = urls.flat().filter(Boolean);
  return list.length ? `background-image:${list.map((url) => `url("${url}")`).join(", ")}` : null;
}

export function loadSetting(key, fallback) {
  try {
    const value = localStorage.getItem(key);
    return value === null ? fallback : JSON.parse(value);
  } catch {
    return fallback;
  }
}

export function saveSetting(key, value) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage blocked (private window, site data off): the setting only lasts this visit
  }
}

// ---------- time formatting (storage is always UTC; this is display only) ----------

const PACIFIC = "America/Los_Angeles";

export function timeZoneOption() {
  return me?.timeZone === "pacific" ? { timeZone: PACIFIC } : {};
}

export function formatDate(utcString, withTime = true) {
  if (!utcString) return "";
  // dateStyle/timeStyle can't be combined with timeZoneName, so the parts are spelled out
  const options = { year: "numeric", month: "short", day: "numeric", ...timeZoneOption() };
  if (withTime) Object.assign(options, { hour: "numeric", minute: "2-digit", timeZoneName: "short" });
  return new Intl.DateTimeFormat(undefined, options).format(new Date(utcString));
}

// 3725 -> "1:02:05", 65 -> "1:05" (short) or "0:01:05"
export function formatDuration(totalSeconds, { short = true } = {}) {
  const s = Math.max(0, Math.round(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, "0");
  if (short && h === 0) return `${m}:${sec}`;
  return `${h}:${String(m).padStart(2, "0")}:${sec}`;
}

// ---------- icon buttons and tooltips ----------

// "Pointer (V)" for a button: its name plus the shortcut from keybinds.js, so the shortcut list stays the one source
export function tooltipText(label, keybindId) {
  const keys = keybindId && typeof keybindLabel === "function" ? keybindLabel(keybindId) : "";
  return keys ? `${label}  (${keys})` : label;
}

// An icon-only button: Phosphor icon, screen-reader name, and a tooltip with the shortcut
export function iconButton({ icon, label, keybind, onClick, disabled = false, id, className } = {}) {
  const button = el("sl-icon-button", { library: "ph", name: icon, label, id, class: className, disabled });
  if (keybind) button.dataset.keybind = keybind;
  if (onClick) button.addEventListener("click", onClick);
  return withTooltip(button, label, keybind);
}

export function withTooltip(target, label, keybind, placement = "top") {
  return el("sl-tooltip", { content: tooltipText(label, keybind), placement, hoist: true }, target);
}

// Icon buttons written in the HTML (<sl-icon-button name=… label=… data-keybind=…>) get the same tooltip as iconButton()
export function wireIconButtons(root = document) {
  for (const button of root.querySelectorAll("sl-icon-button[label]")) {
    if (!button.hasAttribute("library")) button.setAttribute("library", "ph");
    if (button.parentElement?.localName === "sl-tooltip") continue;
    if (button.hasAttribute("slot")) {
      // A dropdown trigger can't be wrapped (it must stay a direct child with its slot); a plain title shows the name
      button.title = button.getAttribute("label");
      continue;
    }
    const placement = button.closest("[data-tip-placement]")?.dataset.tipPlacement || "top";
    const tip = el("sl-tooltip", { content: tooltipText(button.getAttribute("label"), button.dataset.keybind), placement, hoist: true });
    button.replaceWith(tip);
    tip.append(button);
  }
}

// Undo steps across the editor get increasing numbers, so with the vertical editor open Ctrl+Z can undo whichever of it
// and Player 2's edit changed last
let undoSeq = 0;
export function nextUndoSeq() {
  return ++undoSeq;
}

// ---------- toasts ----------

export function toast(text, kind = "") {
  let box = document.querySelector(".toasts");
  if (!box) {
    box = el("div", { class: "toasts", role: "status", "aria-live": "polite" });
    document.body.append(box);
  }
  const item = el("div", { class: `toast ${kind}` }, text);
  box.append(item);
  setTimeout(() => item.remove(), kind === "error" ? 6000 : 3500);
}

/** "Re-encoding for the exact cut · 42% · about 38 min left" */
export function stepText({ step, percent, etaSeconds }) {
  return [step, percent != null ? `${Math.floor(percent)}%` : null, etaSeconds != null ? timeLeft(etaSeconds) : null].filter(Boolean).join(" · ");
}

function timeLeft(seconds) {
  if (seconds >= 3600) return `about ${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min left`;
  if (seconds >= 90) return `about ${Math.round(seconds / 60)} min left`;
  return `about ${Math.max(1, Math.round(seconds))} s left`;
}

// ---------- live stream updates ----------
// GET /api/live/events (server-sent events): the server hears from Twitch when one of the team's channels starts or stops streaming
// (EventSub) and tells the page at once, so nothing here checks on a timer. Fires window events:
//   clipco:live-hello  { watching, channels: [{ login, live, startedAtUtc, watched }] }  on every (re)connect: the state now
//   clipco:stream      { login, live, startedAtUtc, atUtc }                              a channel started (live) or stopped
// Hidden tabs close the stream (browsers allow only ~6 connections per server over HTTP/1.1) and reopen it when shown; the hello
// then brings them up to date.
let liveSource = null;
export const liveChannels = new Map(); // login → { live, startedAtUtc, watched }
export let liveWatching = false;

export function startLiveEvents() {
  if (typeof EventSource === "undefined") return;
  const open = () => {
    if (liveSource || document.hidden) return;
    liveSource = new EventSource("/api/live/events");
    liveSource.addEventListener("hello", (event) => {
      const data = parseEvent(event);
      if (!data) return;
      liveWatching = Boolean(data.watching);
      liveChannels.clear();
      for (const channel of data.channels ?? []) liveChannels.set(channel.login, channel);
      window.dispatchEvent(new CustomEvent("clipco:live-hello", { detail: data }));
    });
    liveSource.addEventListener("stream", (event) => {
      const data = parseEvent(event);
      if (!data) return;
      const known = liveChannels.get(data.login);
      liveChannels.set(data.login, { ...known, login: data.login, live: data.live, startedAtUtc: data.startedAtUtc });
      window.dispatchEvent(new CustomEvent("clipco:stream", { detail: data }));
    });
    // EventSource reconnects by itself after a dropped connection (and gets a new hello)
  };
  const close = () => {
    liveSource?.close();
    liveSource = null;
  };
  document.addEventListener("visibilitychange", () => (document.hidden ? close() : open()));
  window.addEventListener("pagehide", close);
  open();
}

function parseEvent(event) {
  try {
    return JSON.parse(event.data);
  } catch {
    return null;
  }
}
