"use strict";

// All API timestamps are UTC; the time zone setting only changes how they are displayed.
const TZ_STORAGE_KEY = "clipLibrary.timeZone";
const PACIFIC = "America/Los_Angeles";
const POLL_MS = 3000;

const $ = (id) => document.getElementById(id);

let timeZoneMode = loadTimeZoneMode();
let vodInfo = null;
let pollTimer = null;
let renamingId = null;

// ---------- helpers ----------

function loadTimeZoneMode() {
  try {
    return localStorage.getItem(TZ_STORAGE_KEY) === "pacific" ? "pacific" : "local";
  } catch {
    return "local";
  }
}

function saveTimeZoneMode(mode) {
  try {
    localStorage.setItem(TZ_STORAGE_KEY, mode);
  } catch {
    // Storage can be unavailable (private mode, blocked site data); the toggle still works for this visit
  }
}

function formatDate(utcString) {
  if (!utcString) return "";
  // dateStyle/timeStyle can't be combined with timeZoneName, so spell the parts out. The zone name (e.g. PDT/PST) makes the toggle visible.
  const options = { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit", timeZoneName: "short" };
  if (timeZoneMode === "pacific") options.timeZone = PACIFIC;
  return new Intl.DateTimeFormat(undefined, options).format(new Date(utcString));
}

function formatDuration(totalSeconds) {
  const s = Math.round(totalSeconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`;
}

function formatSize(bytes) {
  if (bytes == null) return "";
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
}

// Mirrors the server's TrimTime parser: "h:mm:ss", "m:ss" or seconds. Returns null for blank, NaN if invalid.
function parseTrim(text) {
  const value = text.trim();
  if (!value) return null;
  const parts = value.split(":");
  if (parts.length > 3) return NaN;
  const secondsPart = parts[parts.length - 1];
  if (!/^\d+(\.\d+)?$/.test(secondsPart)) return NaN;
  let total = parseFloat(secondsPart);
  if (parts.length > 1 && total >= 60) return NaN;
  let multiplier = 60;
  for (let i = parts.length - 2; i >= 0; i--) {
    if (!/^\d+$/.test(parts[i])) return NaN;
    const unit = parseInt(parts[i], 10);
    if (i === parts.length - 2 && parts.length === 3 && unit >= 60) return NaN;
    total += unit * multiplier;
    multiplier *= 60;
  }
  return total;
}

async function api(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(problemMessage(data, response.status));
  return data;
}

function problemMessage(problem, status) {
  if (problem?.errors) return Object.values(problem.errors).flat().join(" ");
  return problem?.detail || problem?.title || `Request failed (${status}).`;
}

function setMessage(element, text, kind) {
  element.textContent = text || "";
  element.className = "message" + (kind ? ` ${kind}` : "");
}

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (value !== undefined && value !== null) node.setAttribute(key, value);
  }
  for (const child of children) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// ---------- time zone toggle ----------

function renderTimeZoneToggle() {
  for (const button of document.querySelectorAll(".seg[data-tz]")) {
    button.setAttribute("aria-pressed", String(button.dataset.tz === timeZoneMode));
  }
}

for (const button of document.querySelectorAll(".seg[data-tz]")) {
  button.addEventListener("click", () => {
    timeZoneMode = button.dataset.tz;
    saveTimeZoneMode(timeZoneMode);
    renderTimeZoneToggle();
    renderVodInfo();
    loadEntries();
  });
}

// ---------- add form ----------

function resetVodInfo() {
  vodInfo = null;
  $("vod-info").hidden = true;
  const quality = $("quality");
  quality.replaceChildren(el("option", { value: "" }, "Best available"));
  $("title").placeholder = "Leave blank to use the Twitch title";
  $("end").placeholder = "End of VOD";
  updateRulePreview();
}

function renderVodInfo() {
  if (!vodInfo) return;
  $("vod-info").hidden = false;
  $("vod-thumb").src = vodInfo.thumbnailUrl || "";
  $("vod-title").textContent = vodInfo.title;
  $("vod-meta").textContent = [vodInfo.channelName, `streamed ${formatDate(vodInfo.streamDateUtc)}`, `length ${vodInfo.length}`]
    .filter(Boolean).join(" · ");
}

async function getInfo() {
  const vod = $("vod").value.trim();
  if (!vod) {
    setMessage($("add-message"), "Enter a VOD URL or ID first.", "error");
    return;
  }

  const button = $("get-info");
  button.disabled = true;
  setMessage($("add-message"), "Looking up VOD…");
  try {
    vodInfo = await api("GET", `/api/vods/info?vod=${encodeURIComponent(vod)}`);
    const quality = $("quality");
    quality.replaceChildren(el("option", { value: "" }, "Best available"),
      ...vodInfo.qualities.map((q) => el("option", { value: q }, q)));
    $("title").placeholder = vodInfo.title;
    $("end").placeholder = `${vodInfo.length} (end of VOD)`;
    renderVodInfo();
    updateRulePreview();
    setMessage($("add-message"), vodInfo.restricted ? "This VOD is restricted (e.g. subscriber-only) and can't be added yet." : "",
      vodInfo.restricted ? "error" : undefined);
  } catch (error) {
    resetVodInfo();
    setMessage($("add-message"), error.message, "error");
  } finally {
    button.disabled = false;
  }
}

// Preview only; the server decides stored vs reference-only from the same numbers.
function updateRulePreview() {
  const preview = $("rule-preview");
  if (!vodInfo) {
    preview.textContent = "";
    return;
  }

  const start = parseTrim($("start").value) ?? 0;
  const end = parseTrim($("end").value) ?? vodInfo.lengthSeconds;
  if (Number.isNaN(start) || Number.isNaN(end)) {
    preview.textContent = "Times use h:mm:ss, m:ss or seconds.";
    return;
  }
  if (end <= start) {
    preview.textContent = "End must be after start.";
    return;
  }
  if (end > vodInfo.lengthSeconds) {
    preview.textContent = `End is past the end of the VOD (${vodInfo.length}).`;
    return;
  }

  const length = end - start;
  const limit = vodInfo.maxStoredClipSeconds;
  preview.textContent = length <= limit
    ? `Clip length ${formatDuration(length)}: will be downloaded and stored (limit ${formatDuration(limit)}).`
    : `Clip length ${formatDuration(length)}: over the ${formatDuration(limit)} limit, so it will be saved as a reference and generated on demand.`;
}

$("get-info").addEventListener("click", getInfo);
$("vod").addEventListener("change", resetVodInfo);
$("start").addEventListener("input", updateRulePreview);
$("end").addEventListener("input", updateRulePreview);

$("add-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = $("add-button");
  button.disabled = true;
  setMessage($("add-message"), "Adding…");
  try {
    const entry = await api("POST", "/api/entries", {
      vodId: $("vod").value.trim(),
      title: $("title").value.trim() || null,
      quality: $("quality").value || null,
      trimMode: $("trim-mode").value,
      startTime: $("start").value.trim() || null,
      endTime: $("end").value.trim() || null,
    });
    setMessage($("add-message"),
      `Added "${entry.title}" ${entry.isReferenceOnly ? "as a reference (generated on demand)" : "and queued for download"}.`, "ok");
    $("title").value = "";
    $("start").value = "";
    $("end").value = "";
    updateRulePreview();
    await loadEntries();
  } catch (error) {
    setMessage($("add-message"), error.message, "error");
  } finally {
    button.disabled = false;
  }
});

// ---------- library ----------

function isBusy(entry) {
  return entry.status === "Queued" || entry.status === "Downloading"
    || entry.generationStatus === "Queued" || entry.generationStatus === "Generating";
}

function statusBadge(entry) {
  switch (entry.status) {
    case "Queued": return el("span", { class: "badge busy" }, "Queued");
    case "Downloading": return el("span", { class: "badge busy" }, "Downloading…");
    case "Ready": return el("span", { class: "badge ok" }, `Stored · ${formatSize(entry.fileSizeBytes)}`);
    case "Failed": return el("span", { class: "badge error" }, "Failed");
    case "VodUnavailable": return el("span", { class: "badge error" }, "VOD unavailable");
    case "ReferenceOnly": {
      const text = {
        Queued: "Reference · generation queued",
        Generating: "Reference · generating…",
        Failed: "Reference · generation failed",
      }[entry.generationStatus] ?? (entry.canDownload ? `Reference · file ready · ${formatSize(entry.generatedFileSizeBytes)}` : "Reference only");
      return el("span", { class: entry.generationStatus === "Failed" ? "badge error" : "badge ref" }, text);
    }
    default: return el("span", { class: "badge" }, entry.status);
  }
}

function entryActions(entry) {
  const actions = [];

  if (entry.canDownload) {
    actions.push(el("a", { class: "button", href: `/api/entries/${entry.id}/file`, download: "" }, "Download"));
  } else if (entry.status === "ReferenceOnly" && !isBusy(entry)) {
    actions.push(el("button", { type: "button", onclick: () => generate(entry) },
      entry.generationStatus === "Failed" ? "Retry generate" : "Generate file"));
  } else if (entry.status === "Failed") {
    actions.push(el("button", { type: "button", onclick: () => generate(entry) }, "Retry download"));
  }

  actions.push(el("button", { type: "button", class: "link", onclick: () => startRename(entry) }, "Rename"));
  actions.push(el("button", { type: "button", class: "link danger", onclick: () => deleteEntry(entry) }, "Delete"));
  return actions;
}

function titleNode(entry) {
  if (renamingId !== entry.id) return el("div", { class: "entry-title" }, entry.title);

  const input = el("input", { value: entry.title, maxlength: "500", "aria-label": "New title" });
  const save = () => rename(entry, input.value);
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") save();
    if (event.key === "Escape") cancelRename();
  });
  queueMicrotask(() => { input.focus(); input.select(); });

  return el("div", { class: "entry-title" },
    input,
    el("div", { class: "entry-actions" },
      el("button", { type: "button", onclick: save }, "Save"),
      el("button", { type: "button", class: "link", onclick: cancelRename }, "Cancel")));
}

function renderEntry(entry) {
  const trim = `${entry.startTime}–${entry.endTime ?? formatDuration(entry.vodLengthSeconds)}`;
  const details = [
    `${trim} (${formatDuration(entry.clipLengthSeconds)})`,
    entry.quality ?? "best quality",
    entry.trimMode,
  ].join(" · ");

  const dates = [
    entry.channelName,
    `streamed ${formatDate(entry.streamDateUtc)}`,
    `added ${formatDate(entry.addedAtUtc)}`,
  ].filter(Boolean).join(" · ");

  const expiry = entry.canDownload && entry.generatedExpiresAtUtc
    ? el("span", { class: "muted" }, `file kept until ${formatDate(entry.generatedExpiresAtUtc)}`)
    : null;

  return el("li", { class: "entry" },
    el("img", { class: "thumb", src: entry.thumbnailUrl || "", alt: "", width: "160", height: "90", loading: "lazy" }),
    el("div", { class: "entry-body" },
      titleNode(entry),
      el("div", { class: "entry-meta" }, dates),
      el("div", { class: "entry-meta" }, details),
      el("div", { class: "entry-status" }, statusBadge(entry), expiry),
      entry.error ? el("div", { class: "entry-error" }, entry.error) : null,
      renamingId === entry.id ? null : el("div", { class: "entry-actions" }, ...entryActions(entry))));
}

let loadSequence = 0;

async function loadEntries() {
  const sequence = ++loadSequence;
  const params = new URLSearchParams({ sort: $("sort").value, order: $("order").value });
  const search = $("search").value.trim();
  if (search) params.set("search", search);

  try {
    const entries = await api("GET", `/api/entries?${params}`);
    if (sequence !== loadSequence) return; // a newer search/sort superseded this one

    $("entries").replaceChildren(...entries.map(renderEntry));
    $("empty").hidden = entries.length > 0;
    $("empty").textContent = search ? "No entries match that search." : "No entries yet.";
    setMessage($("library-message"), "");
    schedulePoll(entries.some(isBusy));
  } catch (error) {
    setMessage($("library-message"), error.message, "error");
    schedulePoll(true);
  }
}

function schedulePoll(needed) {
  clearTimeout(pollTimer);
  // Don't redraw under someone who is typing a new title
  if (needed || renamingId !== null) pollTimer = setTimeout(() => (renamingId === null ? loadEntries() : schedulePoll(true)), POLL_MS);
}

async function generate(entry) {
  try {
    await api("POST", `/api/entries/${entry.id}/generate`);
  } catch (error) {
    setMessage($("library-message"), error.message, "error");
  }
  await loadEntries();
}

function startRename(entry) {
  renamingId = entry.id;
  loadEntries();
}

function cancelRename() {
  renamingId = null;
  loadEntries();
}

async function rename(entry, title) {
  try {
    await api("PATCH", `/api/entries/${entry.id}`, { title });
    renamingId = null;
    setMessage($("library-message"), "");
  } catch (error) {
    setMessage($("library-message"), error.message, "error");
    return;
  }
  await loadEntries();
}

async function deleteEntry(entry) {
  const what = entry.canDownload || entry.status === "Downloading" ? " and its file" : "";
  if (!confirm(`Delete "${entry.title}"${what}? This can't be undone.`)) return;
  try {
    await api("DELETE", `/api/entries/${entry.id}`);
  } catch (error) {
    setMessage($("library-message"), error.message, "error");
  }
  await loadEntries();
}

let searchTimer = null;
$("search").addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(loadEntries, 250);
});
$("sort").addEventListener("change", loadEntries);
$("order").addEventListener("change", loadEntries);

renderTimeZoneToggle();
loadEntries();
