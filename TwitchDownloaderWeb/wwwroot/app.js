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
    renderBrowseList();
    refresh();
  });
}

// ---------- Twitch player ----------
//
// VODs play straight from Twitch through the official embed (player.twitch.tv); nothing goes through the server.
// Twitch only plays the embed on the "parent" host it is told about, so the page passes its own host name
// (localhost works; a bare IP address may not).

const TWITCH_EMBED_SRC = "https://player.twitch.tv/js/embed/v1.js";
let embedScript = null;

function loadTwitchEmbed() {
  if (window.Twitch?.Player) return Promise.resolve();
  embedScript ??= new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = TWITCH_EMBED_SRC;
    script.async = true;
    script.onload = resolve;
    script.onerror = () => {
      embedScript = null;
      script.remove();
      reject(new Error("Couldn't load the Twitch player. Check the internet connection."));
    };
    document.head.append(script);
  });
  return embedScript;
}

// "123456789" or a twitch.tv/videos/123456789 link → the VOD ID, else null. The server does the real validation.
function vodIdFromText(text) {
  const value = text.trim();
  if (/^\d+$/.test(value)) return value;
  return value.match(/twitch\.tv\/videos\/(\d+)/i)?.[1] ?? null;
}

// Trim-field format with hundredths when they matter, e.g. "1:02:03.45"
function formatTrimValue(seconds) {
  const hundredths = Math.round(seconds * 100);
  const text = formatDuration(Math.floor(hundredths / 100));
  const fraction = hundredths % 100;
  if (!fraction) return text;
  return `${text}.${String(fraction).padStart(2, "0").replace(/0$/, "")}`;
}

// The embed's "time" option: "1h2m3s"
function embedTime(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 3600)}h${Math.floor((s % 3600) / 60)}m${s % 60}s`;
}

// The embed's getCurrentTime() is a cached value that the player only refreshes every so often (about once a
// second), so on its own it lags playback by up to a second, by a different amount each time, and still shows the
// old offset for a moment after a seek. position() tracks a base offset (the last refreshed value, or a seek target)
// plus the time played since, and the tick runs often enough to pause clip previews within a few frames of their end.
const PLAYER_TICK_MS = 40;
const MAX_EXTRAPOLATION_SECONDS = 1.5; // don't run ahead while buffering; the next refresh corrects it

class VodPlayer {
  constructor(containerId, timeId) {
    this.containerId = containerId;
    this.timeId = timeId;
    this.player = null;
    this.vodId = null;
    this.ready = false;
    this.stopAt = null; // pause once playback reaches this offset (clip previews)
    this.timer = null;
    this.onMessage = null;
    this.lastReported = null; // last value seen from getCurrentTime(); a different value means a fresh refresh
    this.base = 0; // offset at baseAt
    this.baseAt = null; // performance.now() the base was taken while playing, null while not advancing
  }

  async open(vodId, startSeconds = 0, stopAt = null) {
    vodId = String(vodId);
    if (this.player && this.vodId === vodId) {
      this.playRange(startSeconds, stopAt);
      return;
    }

    await loadTwitchEmbed();
    this.close();
    this.vodId = vodId;
    this.stopAt = stopAt;
    this.base = startSeconds;
    this.player = new Twitch.Player(this.containerId, {
      width: "100%",
      height: "100%",
      video: `v${vodId}`,
      time: embedTime(startSeconds),
      parent: [location.hostname],
      autoplay: true,
    });
    const player = this.player;
    player.addEventListener(Twitch.Player.READY, () => {
      if (player !== this.player) return;
      this.ready = true;
      this.lastReported = player.getCurrentTime();
      // The "time" option is whole seconds; land on the exact offset
      if (startSeconds % 1) player.seek(startSeconds);
    });
    player.addEventListener(Twitch.Player.PLAYING, () => {
      if (player !== this.player) return;
      this.baseAt = performance.now();
    });
    player.addEventListener(Twitch.Player.PAUSE, () => {
      if (player !== this.player) return;
      this.freeze();
    });
    player.addEventListener(Twitch.Player.SEEK, (event) => {
      if (player !== this.player) return;
      // Seeks from the player's own controls; hold still until it plays from the new spot
      if (typeof event?.position === "number") this.base = event.position;
      this.baseAt = null;
    });
    // The embed refreshes its cached time when a message from the player iframe arrives. Its own listener runs
    // first (it was added earlier), so reading the time here catches each refresh the moment it lands instead of
    // up to a tick later.
    this.onMessage = (event) => {
      if (event.origin === "https://player.twitch.tv" && player === this.player) this.position();
    };
    window.addEventListener("message", this.onMessage);
    this.timer = setInterval(() => this.tick(), PLAYER_TICK_MS);
  }

  close() {
    clearInterval(this.timer);
    window.removeEventListener("message", this.onMessage);
    this.timer = null;
    this.player = null;
    this.vodId = null;
    this.ready = false;
    this.stopAt = null;
    this.lastReported = null;
    this.baseAt = null;
    $(this.containerId).replaceChildren();
    $(this.timeId).textContent = "–";
  }

  // Estimated current offset into the VOD in seconds, or null before the player is ready
  position() {
    if (!this.ready) return null;
    const reported = this.player.getCurrentTime();
    if (reported !== this.lastReported) {
      // A fresh refresh: trust it, and keep counting from here if playing
      this.lastReported = reported;
      this.base = reported;
      if (this.baseAt != null) this.baseAt = performance.now();
    }
    if (this.baseAt == null || this.player.isPaused()) return this.base;
    return this.base + Math.min((performance.now() - this.baseAt) / 1000, MAX_EXTRAPOLATION_SECONDS);
  }

  freeze() {
    this.base = this.position() ?? this.base;
    this.baseAt = null;
  }

  playRange(startSeconds, stopAt = null) {
    if (!this.ready) return;
    this.stopAt = stopAt;
    // The cached time still shows the old spot until the next refresh; don't mistake that for the new position
    this.lastReported = this.player.getCurrentTime();
    this.base = startSeconds;
    this.baseAt = null;
    this.player.seek(startSeconds);
    this.player.play();
  }

  tick() {
    const position = this.position();
    if (position == null) return;
    $(this.timeId).textContent = formatTrimValue(position);
    this.onTick?.(position);
    if (this.stopAt != null && position >= this.stopAt) {
      this.stopAt = null;
      this.player.pause();
      this.freeze();
    }
  }
}

const watchPlayer = new VodPlayer("watch-player", "watch-time");
const clipPlayer = new VodPlayer("clip-player", "clip-time");

async function openWatch(vodId) {
  $("watch").hidden = false;
  $("watch-button").textContent = "Hide player";
  try {
    await watchPlayer.open(vodId, parseTrim($("start").value) || 0);
    $("watch").scrollIntoView({ behavior: "smooth", block: "nearest" });
  } catch (error) {
    closeWatch();
    setMessage($("add-message"), error.message, "error");
  }
}

function closeWatch() {
  watchPlayer.close();
  $("watch").hidden = true;
  $("watch-button").textContent = "Watch";
}

function setTrimFromPlayer(fieldId) {
  const now = watchPlayer.position();
  if (now == null) return;
  $(fieldId).value = formatTrimValue(now);
  updateRulePreview();
}

$("watch-button").addEventListener("click", () => {
  if (!$("watch").hidden) {
    closeWatch();
    return;
  }
  const vodId = vodIdFromText($("vod").value);
  if (!vodId) {
    setMessage($("add-message"), "Enter a VOD URL or ID first.", "error");
    return;
  }
  setMessage($("add-message"), "");
  openWatch(vodId);
});
$("watch-close").addEventListener("click", closeWatch);
$("watch-set-start").addEventListener("click", () => setTrimFromPlayer("start"));
$("watch-set-end").addEventListener("click", () => setTrimFromPlayer("end"));
$("watch-preview").addEventListener("click", () => {
  const start = parseTrim($("start").value) ?? 0;
  const end = parseTrim($("end").value);
  if (Number.isNaN(start) || Number.isNaN(end)) {
    setMessage($("add-message"), "Times use h:mm:ss, m:ss or seconds.", "error");
    return;
  }
  watchPlayer.playRange(start, end);
});

// ---------- clip review (library entries) ----------
//
// Plays an entry's clip from Twitch with a trim bar around it. The bar shows the clip plus some VOD on each side so
// the cut can be widened as well as narrowed; handles drag, ±1s/±0.1s buttons nudge, and Save re-cuts the entry on
// the server (PUT /trim), which downloads it again.

const NUDGE_MIN_LENGTH = 0.1; // keep end after start

let review = null; // { entry, start, end, vodLength, from, to }

function entryRange(entry) {
  return {
    start: parseTrim(entry.startTime) || 0,
    end: entry.endTime ? parseTrim(entry.endTime) : entry.vodLengthSeconds,
  };
}

function roundHundredths(seconds) {
  return Math.round(seconds * 100) / 100;
}

function reviewDirty() {
  if (!review) return false;
  const saved = entryRange(review.entry);
  return review.start !== saved.start || review.end !== saved.end;
}

// The bar spans the clip plus a margin on each side; widen it when a handle leaves it
function fitReviewWindow(force) {
  const { start, end, vodLength } = review;
  if (!force && start >= review.from && end <= review.to) return;
  const pad = Math.min(60, Math.max(5, (end - start) * 0.25));
  review.from = Math.max(0, start - pad);
  review.to = Math.min(vodLength, end + pad);
}

function barPercent(seconds) {
  return ((seconds - review.from) / (review.to - review.from)) * 100;
}

function renderReview() {
  if (!review) return;
  const startPct = barPercent(review.start);
  const endPct = barPercent(review.end);
  $("trim-range").style.left = `${startPct}%`;
  $("trim-range").style.width = `${endPct - startPct}%`;
  $("trim-handle-start").style.left = `${startPct}%`;
  $("trim-handle-end").style.left = `${endPct}%`;
  $("trim-scale-from").textContent = formatTrimValue(review.from);
  $("trim-scale-to").textContent = formatTrimValue(review.to);

  for (const [edge, id] of [["start", "trim-start"], ["end", "trim-end"]]) {
    const input = $(id);
    if (document.activeElement !== input) input.value = formatTrimValue(review[edge]);
    input.classList.remove("invalid");
  }

  const dirty = reviewDirty();
  $("trim-length").textContent = `Length ${formatTrimValue(review.end - review.start)}`;
  $("trim-save").disabled = !dirty || review.saving;
  $("trim-revert").disabled = !dirty || review.saving;
}

function setReviewEdge(edge, seconds) {
  if (!review || seconds == null || Number.isNaN(seconds)) return;
  let value = roundHundredths(seconds);
  if (edge === "start") value = Math.min(Math.max(0, value), roundHundredths(review.end - NUDGE_MIN_LENGTH));
  else value = Math.max(Math.min(review.vodLength, value), roundHundredths(review.start + NUDGE_MIN_LENGTH));
  review[edge] = value;
  // Keep the preview's stop in step with the end being edited
  if (edge === "end" && clipPlayer.stopAt != null) clipPlayer.stopAt = value;
  fitReviewWindow(false);
  setMessage($("trim-message"), "");
  renderReview();
}

function barSeconds(clientX) {
  const rect = $("trim-bar").getBoundingClientRect();
  const fraction = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
  return review.from + fraction * (review.to - review.from);
}

function updatePlayhead(position) {
  const playhead = $("trim-playhead");
  if (!review || position < review.from || position > review.to) {
    playhead.hidden = true;
    return;
  }
  playhead.hidden = false;
  playhead.style.left = `${barPercent(position)}%`;
}

clipPlayer.onTick = updatePlayhead;

function openReview(entry) {
  const { start, end } = entryRange(entry);
  review = { entry, start, end, vodLength: entry.vodLengthSeconds, from: 0, to: 0, saving: false };
  fitReviewWindow(true);
  $("clip-dialog-title").textContent = entry.title;
  $("trim-playhead").hidden = true;
  setMessage($("trim-message"), "");
  renderReview();
  if (!$("clip-dialog").open) $("clip-dialog").showModal();
  clipPlayer.open(entry.vodId, start, end).catch((error) => {
    review = null;
    $("clip-dialog").close();
    setMessage($("library-message"), error.message, "error");
  });
}

async function saveReview() {
  if (!review || !reviewDirty()) return;
  const { entry } = review;
  // An entry that ran to the end of the VOD keeps "end of VOD" unless the end was moved
  const endTime = entry.endTime == null && review.end === review.vodLength ? null : formatTrimValue(review.end);
  review.saving = true;
  renderReview();
  setMessage($("trim-message"), "Saving…");
  try {
    const updated = await api("PUT", `/api/entries/${entry.id}/trim`, { startTime: formatTrimValue(review.start), endTime });
    if (!review || review.entry.id !== updated.id) return;
    review.entry = updated;
    setMessage($("trim-message"), updated.isReferenceOnly
      ? "Saved. It's over the stored-clip limit, so it's kept as a reference; generate the file when you need it."
      : "Saved. The clip is being downloaded again with the new cut.", "ok");
    refresh();
  } catch (error) {
    setMessage($("trim-message"), error.message, "error");
  } finally {
    if (review) {
      review.saving = false;
      renderReview();
    }
  }
}

function closeReview() {
  if (reviewDirty() && !confirm("Discard the changes to this clip's start and end?")) return false;
  $("clip-dialog").close();
  return true;
}

for (const button of document.querySelectorAll(".nudge")) {
  button.addEventListener("click", () => {
    if (!review) return;
    setReviewEdge(button.dataset.edge, review[button.dataset.edge] + parseFloat(button.dataset.delta));
  });
}

for (const button of document.querySelectorAll("[data-set-edge]")) {
  button.addEventListener("click", () => setReviewEdge(button.dataset.setEdge, clipPlayer.position()));
}

for (const [edge, id] of [["start", "trim-start"], ["end", "trim-end"]]) {
  const input = $(id);
  const apply = () => {
    const value = parseTrim(input.value);
    if (value == null || Number.isNaN(value)) {
      input.classList.add("invalid");
      return;
    }
    setReviewEdge(edge, value);
    input.value = formatTrimValue(review[edge]);
  };
  input.addEventListener("change", apply);
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      apply();
    }
  });
}

// Dragging a handle moves that edge; clicking elsewhere on the bar seeks the player there
for (const [edge, id] of [["start", "trim-handle-start"], ["end", "trim-handle-end"]]) {
  const handle = $(id);
  handle.addEventListener("pointerdown", (event) => {
    if (!review) return;
    event.preventDefault();
    event.stopPropagation();
    handle.setPointerCapture(event.pointerId);
    handle.classList.add("dragging");
  });
  handle.addEventListener("pointermove", (event) => {
    if (handle.hasPointerCapture(event.pointerId)) setReviewEdge(edge, barSeconds(event.clientX));
  });
  const stop = (event) => {
    if (!handle.hasPointerCapture(event.pointerId)) return;
    handle.releasePointerCapture(event.pointerId);
    handle.classList.remove("dragging");
    // Let the editor hear the new edge
    if (edge === "start") clipPlayer.playRange(review.start, review.end);
    else clipPlayer.playRange(Math.max(review.start, review.end - 3), review.end);
  };
  handle.addEventListener("pointerup", stop);
  handle.addEventListener("pointercancel", stop);
  // Arrow keys nudge by 0.1s (1s with Shift) when a handle has focus
  handle.addEventListener("keydown", (event) => {
    if (!review || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
    event.preventDefault();
    const step = (event.shiftKey ? 1 : 0.1) * (event.key === "ArrowLeft" ? -1 : 1);
    setReviewEdge(edge, review[edge] + step);
  });
}

$("trim-bar").addEventListener("pointerdown", (event) => {
  if (!review || event.target.classList.contains("trim-handle")) return;
  const seconds = barSeconds(event.clientX);
  clipPlayer.playRange(seconds, seconds < review.end ? review.end : null);
});

$("clip-replay").addEventListener("click", () => review && clipPlayer.playRange(review.start, review.end));
$("clip-play-end").addEventListener("click", () => review && clipPlayer.playRange(Math.max(review.start, review.end - 3), review.end));
$("trim-save").addEventListener("click", saveReview);
$("trim-revert").addEventListener("click", () => {
  if (!review) return;
  Object.assign(review, entryRange(review.entry));
  fitReviewWindow(true);
  setMessage($("trim-message"), "");
  renderReview();
});

$("clip-close").addEventListener("click", closeReview);
// Escape fires "cancel" first; ask before throwing away unsaved changes
$("clip-dialog").addEventListener("cancel", (event) => {
  if (reviewDirty() && !confirm("Discard the changes to this clip's start and end?")) event.preventDefault();
});
// Either way stop playback
$("clip-dialog").addEventListener("close", () => {
  review = null;
  clipPlayer.close();
});

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
$("vod").addEventListener("change", () => {
  if (prefilledTitle !== null && $("title").value === prefilledTitle) $("title").value = "";
  prefilledTitle = null;
  browse.selectedId = null;
  renderBrowseList();
  resetVodInfo();
  if (watchPlayer.vodId && watchPlayer.vodId !== vodIdFromText($("vod").value)) closeWatch();
});
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
    // Check the cut right away; the review dialog plays it and can adjust it
    closeWatch();
    openReview(entry);
    $("title").value = "";
    prefilledTitle = null;
    $("start").value = "";
    $("end").value = "";
    updateRulePreview();
    await refresh();
  } catch (error) {
    setMessage($("add-message"), error.message, "error");
  } finally {
    button.disabled = false;
  }
});

// ---------- channel VOD browser ----------
//
// Lists a channel's VODs (newest first, as Twitch returns them) so a VOD can be picked instead of pasted.
// Picking one fills the VOD field and title, then runs the normal Get Info flow.

const BROWSE_CHANNEL_STORAGE_KEY = "clipLibrary.browseChannel";
const BROWSE_TYPE_STORAGE_KEY = "clipLibrary.browseType";
const VOD_TYPES = ["archive", "highlight", "upload", "all"];

let savedChannels = [];
let browse = { login: null, type: loadBrowseType(), items: [], nextCursor: null, loading: false, selectedId: null };
let prefilledTitle = null;
let browseRequest = 0;

function loadBrowseType() {
  try {
    const type = localStorage.getItem(BROWSE_TYPE_STORAGE_KEY);
    return VOD_TYPES.includes(type) ? type : "archive";
  } catch {
    return "archive";
  }
}

function saveBrowsePrefs() {
  try {
    localStorage.setItem(BROWSE_TYPE_STORAGE_KEY, browse.type);
    localStorage.setItem(BROWSE_CHANNEL_STORAGE_KEY, $("browse-channel").value.trim());
  } catch {
    // Storage unavailable; the choice just isn't remembered
  }
}

function loadBrowseChannel() {
  try {
    return localStorage.getItem(BROWSE_CHANNEL_STORAGE_KEY) || "";
  } catch {
    return "";
  }
}

// Mirrors the server's ChannelService.NormalizeLogin closely enough to match saved channels; the server still validates.
function normalizeLogin(text) {
  let value = text.trim();
  const match = /^(?:https?:\/\/)?(?:www\.|m\.)?twitch\.tv\/([^/?#]+)/i.exec(value);
  if (match) value = match[1];
  return value.replace(/^@/, "").toLowerCase();
}

function currentSavedChannel() {
  const login = normalizeLogin($("browse-channel").value);
  return savedChannels.find((c) => c.login === login) ?? null;
}

function renderSavedChannels() {
  const select = $("browse-saved");
  const current = currentSavedChannel();
  select.replaceChildren(el("option", { value: "" }, savedChannels.length ? "Choose…" : "None saved yet"),
    ...savedChannels.map((c) => el("option", { value: c.login }, c.displayName)));
  select.value = current?.login ?? "";

  const save = $("browse-save");
  save.textContent = current ? "Remove saved" : "Save channel";
  save.classList.toggle("danger", Boolean(current));
  save.disabled = !current && !normalizeLogin($("browse-channel").value);
}

async function loadSavedChannels() {
  try {
    savedChannels = await api("GET", "/api/channels");
  } catch (error) {
    setMessage($("browse-message"), `Couldn't load saved channels: ${error.message}`, "error");
  }
  renderSavedChannels();
}

async function toggleSavedChannel() {
  const current = currentSavedChannel();
  const button = $("browse-save");
  button.disabled = true;
  try {
    if (current) {
      if (!confirm(`Remove ${current.displayName} from the saved channels?`)) return;
      await api("DELETE", `/api/channels/${current.id}`);
      setMessage($("browse-message"), `Removed ${current.displayName}.`, "ok");
    } else {
      const channel = await api("POST", "/api/channels", { login: $("browse-channel").value.trim() });
      setMessage($("browse-message"), `Saved ${channel.displayName}.`, "ok");
    }
    await loadSavedChannels();
  } catch (error) {
    setMessage($("browse-message"), error.message, "error");
  } finally {
    renderSavedChannels();
  }
}

function renderVodTypeToggle() {
  for (const button of document.querySelectorAll(".seg[data-vod-type]")) {
    button.setAttribute("aria-pressed", String(button.dataset.vodType === browse.type));
  }
}

const RESCUE_BADGES = {
  Grabbing: ["badge busy", "Saving audio…", "Saving this VOD's audio while Twitch still has clean copies of muted parts"],
  WaitingToPrune: ["badge ok", "Audio saved", "Full audio saved; only the muted parts are kept after a couple of days"],
  Pruned: ["badge ok", "Muted audio saved", "Clean audio for the muted parts is saved and swapped in on download"],
  Failed: ["badge error", "Audio rescue failed", "Saving the audio failed; you can try again"],
};

function rescueBadge(status) {
  const badge = RESCUE_BADGES[status];
  return badge ? el("span", { class: badge[0], title: badge[2] }, badge[1]) : null;
}

// Muted parts Twitch wouldn't give a clean copy of (usually muted before the rescue started), so they stay muted
function lostAudioBadge(vod) {
  const lost = vod.rescueUnrecoverableCount;
  if (!lost) return null;
  const text = lost === vod.rescueMutedCount ? "Muted parts not saved" : `${lost} of ${vod.rescueMutedCount} muted parts not saved`;
  return el("span", { class: "badge error", title: "Twitch had already muted these parts when saving started, so they stay muted in downloads" }, text);
}

async function startRescue(vod, button) {
  button.disabled = true;
  try {
    const rescue = await api("POST", `/api/vods/${vod.vodId}/rescue`);
    vod.rescueStatus = rescue.status;
    setMessage($("browse-message"), `Saving the audio of "${vod.title}". This runs in the background.`, "ok");
  } catch (error) {
    setMessage($("browse-message"), error.message, "error");
  }
  renderBrowseList();
}

function vodCard(vod) {
  const thumb = vod.thumbnailUrl
    ? el("img", { src: vod.thumbnailUrl, alt: "", width: "320", height: "180", loading: "lazy" })
    : el("div", { class: "thumb-missing" }, "No thumbnail yet");

  const meta = [formatDate(vod.createdAtUtc), vod.length, vod.game].filter(Boolean).join(" · ");

  // Offer a manual rescue for VODs that are still in the unmute window (or whose rescue failed)
  const canRescue = (vod.unmuteWindowOpen && !vod.rescueStatus) || vod.rescueStatus === "Failed";
  const rescueButton = canRescue
    ? el("button", { type: "button", class: "small secondary", onclick: (event) => startRescue(vod, event.currentTarget) },
      vod.rescueStatus === "Failed" ? "Retry saving audio" : "Save audio now")
    : null;

  return el("li", { class: "vod-item" },
    el("button", { type: "button", class: "vod-card", "aria-pressed": String(browse.selectedId === vod.vodId), onclick: () => pickVod(vod) },
      thumb,
      el("span", { class: "vod-card-title" }, vod.title),
      el("span", { class: "entry-meta" }, meta),
      el("span", { class: "vod-card-badges" },
        vod.unmuteWindowOpen
          ? el("span", { class: "badge ref", title: "Ended less than 24 hours ago: Twitch may still have unmuted audio for muted parts" }, "Unmute window open")
          : null,
        rescueBadge(vod.rescueStatus),
        lostAudioBadge(vod))),
    el("div", { class: "vod-item-actions" },
      el("button", { type: "button", class: "small secondary", onclick: () => watchVod(vod) }, "Watch"),
      rescueButton));
}

function renderBrowseList() {
  const list = $("browse-list");
  list.hidden = browse.items.length === 0;
  list.replaceChildren(...browse.items.map(vodCard));
  $("browse-more").hidden = !browse.nextCursor;
  $("browse-more").disabled = browse.loading;
  $("browse-hide").hidden = browse.items.length === 0;
}

const VOD_TYPE_NAMES = { archive: "past broadcasts", highlight: "highlights", upload: "uploads", all: "videos" };

async function loadChannelVods(append) {
  const login = append ? browse.login : normalizeLogin($("browse-channel").value);
  if (!login) {
    setMessage($("browse-message"), "Choose a saved channel or type a channel name first.", "error");
    return;
  }

  if (!append) {
    browse = { ...browse, login, items: [], nextCursor: null };
    renderBrowseList();
  }

  // Switching channel or type while a page loads must not mix the old response into the new list
  const request = ++browseRequest;
  browse.loading = true;
  $("browse-show").disabled = true;
  renderBrowseList();
  setMessage($("browse-message"), append ? "Loading more…" : "Loading VODs…");
  try {
    const params = new URLSearchParams({ type: browse.type });
    if (append && browse.nextCursor) params.set("cursor", browse.nextCursor);
    const page = await api("GET", `/api/channels/${encodeURIComponent(login)}/vods?${params}`);
    if (request !== browseRequest) return;

    // Guard against repeats in case Twitch's cursor overlaps the previous page
    const seen = new Set(browse.items.map((x) => x.vodId));
    browse.items = browse.items.concat(page.items.filter((x) => !seen.has(x.vodId)));
    browse.nextCursor = page.nextCursor;
    saveBrowsePrefs();

    const typeName = VOD_TYPE_NAMES[browse.type];
    setMessage($("browse-message"), browse.items.length
      ? `${login}: showing ${browse.items.length} of ${page.totalCount} ${typeName}. Pick one to fill in the form.`
      : `${login} has no ${typeName}.`);
  } catch (error) {
    if (request === browseRequest) setMessage($("browse-message"), error.message, "error");
  } finally {
    if (request !== browseRequest) return;
    browse.loading = false;
    $("browse-show").disabled = false;
    renderBrowseList();
  }
}

function pickVod(vod) {
  browse.selectedId = vod.vodId;
  renderBrowseList();

  $("vod").value = String(vod.vodId);
  // Pre-fill the title (still editable). Only replace a title we filled in ourselves, never one the user typed.
  if (!$("title").value.trim() || $("title").value === prefilledTitle) {
    $("title").value = vod.title;
    prefilledTitle = vod.title;
  }
  resetVodInfo();
  getInfo();
  if (!$("watch").hidden && watchPlayer.vodId !== String(vod.vodId)) openWatch(vod.vodId);
  $("vod-info").scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function watchVod(vod) {
  pickVod(vod);
  if ($("watch").hidden) openWatch(vod.vodId);
}

$("browse-saved").addEventListener("change", () => {
  const login = $("browse-saved").value;
  if (!login) return;
  $("browse-channel").value = login;
  renderSavedChannels();
  loadChannelVods(false);
});
$("browse-channel").addEventListener("input", renderSavedChannels);
$("browse-channel").addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    // The browser lives inside the add form's panel; don't let Enter submit anything
    event.preventDefault();
    loadChannelVods(false);
  }
});
$("browse-save").addEventListener("click", toggleSavedChannel);
$("browse-show").addEventListener("click", () => loadChannelVods(false));
$("browse-more").addEventListener("click", () => loadChannelVods(true));
$("browse-hide").addEventListener("click", () => {
  browseRequest++;
  browse = { ...browse, login: null, items: [], nextCursor: null, loading: false };
  $("browse-show").disabled = false;
  setMessage($("browse-message"), "");
  renderBrowseList();
});

for (const button of document.querySelectorAll(".seg[data-vod-type]")) {
  button.addEventListener("click", () => {
    if (browse.type === button.dataset.vodType) return;
    browse.type = button.dataset.vodType;
    renderVodTypeToggle();
    saveBrowsePrefs();
    // Reload with the new type if a list is showing
    if (browse.items.length || browse.login) loadChannelVods(false);
  });
}

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

  // Twitch deleted the VOD, so there is nothing left to stream
  if (entry.status !== "VodUnavailable") {
    actions.push(el("button", { type: "button", class: "secondary", onclick: () => openReview(entry) }, "Review"));
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
      el("div", { class: "entry-status" }, statusBadge(entry),
        entry.audioRestoredSeconds > 0
          ? el("span", { class: "badge ok", title: "Muted parts were replaced with saved clean audio" }, `Audio restored · ${formatDuration(entry.audioRestoredSeconds)}`)
          : null,
        entry.audioMutedSeconds > 0
          ? el("span", { class: "badge error", title: "No clean audio was saved for these muted parts (Twitch had already muted them)" }, `Still muted · ${formatDuration(entry.audioMutedSeconds)}`)
          : null,
        expiry),
      entry.error ? el("div", { class: "entry-error" }, entry.error) : null,
      renamingId === entry.id ? null : el("div", { class: "entry-actions" }, ...entryActions(entry))));
}

// ---------- calendar ----------
//
// Calendar days are dates in the time zone being shown (local or Pacific). To ask the server for "Oct 9" we
// convert that zone's midnights to UTC instants and send them as from/to, so a clip lands on the day it
// happened in whichever zone is shown.

const CAL_FIELD_STORAGE_KEY = "clipLibrary.calendarField";
const CALENDAR_LIMIT = 1000;

const CAL_OPEN_STORAGE_KEY = "clipLibrary.calendarOpen";

let calendarField = loadCalendarField();
let calendarOpen = loadCalendarOpen();
let calendarMonth = null; // { year, month } with month 1-12, in the shown time zone
let selectedDay = null; // { year, month, day } or null

function loadCalendarField() {
  try {
    return localStorage.getItem(CAL_FIELD_STORAGE_KEY) === "dateAdded" ? "dateAdded" : "streamDate";
  } catch {
    return "streamDate";
  }
}

function saveCalendarField(field) {
  try {
    localStorage.setItem(CAL_FIELD_STORAGE_KEY, field);
  } catch {
    // See saveTimeZoneMode
  }
}

// Closed by default
function loadCalendarOpen() {
  try {
    return localStorage.getItem(CAL_OPEN_STORAGE_KEY) === "open";
  } catch {
    return false;
  }
}

function saveCalendarOpen(open) {
  try {
    localStorage.setItem(CAL_OPEN_STORAGE_KEY, open ? "open" : "closed");
  } catch {
    // See saveTimeZoneMode
  }
}

function shownTimeZone() {
  return timeZoneMode === "pacific" ? PACIFIC : Intl.DateTimeFormat().resolvedOptions().timeZone;
}

const partsFormatters = new Map();

// Wall-clock parts of an instant in a time zone
function zonedParts(ms, timeZone) {
  let formatter = partsFormatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("en-US", {
      timeZone, hourCycle: "h23", year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric",
    });
    partsFormatters.set(timeZone, formatter);
  }
  const parts = Object.fromEntries(formatter.formatToParts(new Date(ms)).map((p) => [p.type, p.value]));
  return { year: +parts.year, month: +parts.month, day: +parts.day, hour: +parts.hour % 24, minute: +parts.minute, second: +parts.second };
}

// UTC instant (ms) of midnight at the start of year-month-day in a time zone. Day/month overflow rolls over,
// so (2026, 10, 32) is Nov 1. Two passes settle the offset even when it differs between the guess and the answer.
function zonedMidnightUtc(year, month, day, timeZone) {
  const wall = Date.UTC(year, month - 1, day);
  let utc = wall;
  for (let i = 0; i < 3; i++) {
    const p = zonedParts(utc, timeZone);
    const offset = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - utc;
    utc = wall - offset;
  }
  return utc;
}

function dayKey(year, month, day) {
  return `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}

function dayKeyOf(utcString, timeZone) {
  const p = zonedParts(new Date(utcString).getTime(), timeZone);
  return dayKey(p.year, p.month, p.day);
}

function dayRange(day, timeZone) {
  return {
    from: new Date(zonedMidnightUtc(day.year, day.month, day.day, timeZone)).toISOString(),
    to: new Date(zonedMidnightUtc(day.year, day.month, day.day + 1, timeZone)).toISOString(),
  };
}

function firstDayOfWeek() {
  try {
    const locale = new Intl.Locale(navigator.language);
    const info = locale.getWeekInfo?.() ?? locale.weekInfo;
    if (info?.firstDay) return info.firstDay % 7; // 1 = Monday ... 7 = Sunday -> JS 0 = Sunday
  } catch {
    // Older browsers: fall through
  }
  return 0;
}

function timeZoneLabel() {
  return timeZoneMode === "pacific" ? "Pacific time" : "local time";
}

function formatCalendarDay(day) {
  return new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" })
    .format(Date.UTC(day.year, day.month - 1, day.day));
}

function baseListParams() {
  const params = new URLSearchParams();
  const search = $("search").value.trim();
  if (search) params.set("search", search);
  return params;
}

let calendarSequence = 0;

async function loadCalendar() {
  const sequence = ++calendarSequence;
  const timeZone = shownTimeZone();
  if (!calendarMonth) {
    const today = zonedParts(Date.now(), timeZone);
    calendarMonth = { year: today.year, month: today.month };
  }

  const { year, month } = calendarMonth;
  const params = baseListParams();
  params.set("dateField", calendarField);
  params.set("sort", calendarField);
  params.set("from", new Date(zonedMidnightUtc(year, month, 1, timeZone)).toISOString());
  params.set("to", new Date(zonedMidnightUtc(year, month + 1, 1, timeZone)).toISOString());
  params.set("limit", String(CALENDAR_LIMIT));

  try {
    const entries = await api("GET", `/api/entries?${params}`);
    if (sequence !== calendarSequence) return;

    const counts = new Map();
    const field = calendarField === "streamDate" ? "streamDateUtc" : "addedAtUtc";
    for (const entry of entries) {
      const key = dayKeyOf(entry[field], timeZone);
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
    renderCalendar(counts, timeZone);
    $("cal-note").textContent = entries.length >= CALENDAR_LIMIT ? `Counts cover the first ${CALENDAR_LIMIT} clips this month.` : "";
  } catch (error) {
    $("cal-note").textContent = error.message;
  }
}

function renderCalendar(counts, timeZone) {
  const { year, month } = calendarMonth;
  const monthName = new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric", timeZone: "UTC" }).format(Date.UTC(year, month - 1, 1));
  $("cal-title").textContent = monthName;
  $("cal-zone").textContent = `${calendarField === "streamDate" ? "Stream dates" : "Dates added"}, ${timeZoneLabel()}`;

  for (const button of document.querySelectorAll(".seg[data-cal-field]")) {
    button.setAttribute("aria-pressed", String(button.dataset.calField === calendarField));
  }

  const weekStart = firstDayOfWeek();
  const weekdayFormat = new Intl.DateTimeFormat(undefined, { weekday: "short", timeZone: "UTC" });
  // Jan 4 2026 is a Sunday
  const headers = Array.from({ length: 7 }, (_, i) => el("div", { class: "cal-weekday", "aria-hidden": "true" },
    weekdayFormat.format(Date.UTC(2026, 0, 4 + ((weekStart + i) % 7)))));

  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const leadingBlanks = (new Date(Date.UTC(year, month - 1, 1)).getUTCDay() - weekStart + 7) % 7;
  const verb = calendarField === "streamDate" ? "streamed" : "added";

  const cells = Array.from({ length: leadingBlanks }, () => el("div", { class: "cal-blank" }));
  for (let day = 1; day <= daysInMonth; day++) {
    const count = counts.get(dayKey(year, month, day)) ?? 0;
    const isSelected = selectedDay?.year === year && selectedDay?.month === month && selectedDay?.day === day;
    const label = `${formatCalendarDay({ year, month, day })}: ${count} clip${count === 1 ? "" : "s"} ${verb}`;

    cells.push(el("button", {
      type: "button",
      class: "cal-day" + (count ? " has-clips" : "") + (isSelected ? " selected" : ""),
      "aria-label": label,
      "aria-pressed": String(isSelected),
      title: label,
      disabled: count === 0 && !isSelected ? "" : undefined,
      onclick: () => selectDay({ year, month, day }),
    }, el("span", { class: "cal-date" }, day), count ? el("span", { class: "cal-count" }, count) : null));
  }

  $("cal-grid").replaceChildren(...headers, ...cells);
}

function selectDay(day) {
  const same = selectedDay && selectedDay.year === day.year && selectedDay.month === day.month && selectedDay.day === day.day;
  selectedDay = same ? null : day;
  if (selectedDay) recentOnly = false;
  refresh();
}

// ---------- last 24 hours ----------
//
// Follows the Date added / Stream date dropdown next to the search box (not the calendar's own switch).
// It and a calendar day are mutually exclusive: turning one on clears the other.

const RECENT_WINDOW_MS = 24 * 60 * 60 * 1000;

let recentOnly = false;

function recentField() {
  return $("sort").value;
}

function fieldVerb() {
  return recentField() === "streamDate" ? "streamed" : "added";
}

function renderRecentToggle() {
  const button = $("recent-toggle");
  button.setAttribute("aria-pressed", String(recentOnly));
  button.title = recentOnly
    ? `Showing clips ${fieldVerb()} in the last 24 hours. Click to show all.`
    : `Show clips ${fieldVerb()} in the last 24 hours`;
}

$("recent-toggle").addEventListener("click", () => {
  recentOnly = !recentOnly;
  if (recentOnly) selectedDay = null;
  refresh();
});

// With the calendar open the filter is described in a bar under the filters; with it closed, a compact chip
// next to the search box keeps it visible that the list is filtered.
function renderDayFilter() {
  $("day-filter").hidden = !(selectedDay && calendarOpen);
  $("day-chip").hidden = !(selectedDay && !calendarOpen);
  if (!selectedDay) return;

  const description = `${calendarField === "streamDate" ? "streamed" : "added"} on ${formatCalendarDay(selectedDay)} (${timeZoneLabel()})`;
  $("day-filter-text").textContent = `Showing clips ${description}`;

  const thisYear = zonedParts(Date.now(), shownTimeZone()).year;
  const short = new Intl.DateTimeFormat(undefined, {
    month: "short", day: "numeric", year: selectedDay.year === thisYear ? undefined : "numeric", timeZone: "UTC",
  }).format(Date.UTC(selectedDay.year, selectedDay.month - 1, selectedDay.day));

  const chip = $("day-chip");
  chip.replaceChildren(el("span", {}, short), el("span", { class: "chip-x", "aria-hidden": "true" }, "✕"));
  chip.setAttribute("aria-label", `Clear day filter: clips ${description}`);
  chip.title = `Showing clips ${description}. Click to clear.`;
}

function clearDayFilter() {
  selectedDay = null;
  refresh();
}

function moveMonth(delta) {
  const index = calendarMonth.year * 12 + (calendarMonth.month - 1) + delta;
  calendarMonth = { year: Math.floor(index / 12), month: (index % 12) + 1 };
  loadCalendar();
}

$("cal-prev").addEventListener("click", () => moveMonth(-1));
$("cal-next").addEventListener("click", () => moveMonth(1));
$("day-filter-clear").addEventListener("click", clearDayFilter);
$("day-chip").addEventListener("click", () => {
  clearDayFilter();
  $("search").focus(); // the chip disappears, so don't leave focus on nothing
});

function renderCalendarOpen() {
  $("calendar").hidden = !calendarOpen;
  $("cal-toggle").setAttribute("aria-expanded", String(calendarOpen));
  renderDayFilter();
}

$("cal-toggle").addEventListener("click", () => {
  calendarOpen = !calendarOpen;
  saveCalendarOpen(calendarOpen);
  renderCalendarOpen();
  if (calendarOpen) loadCalendar();
});
for (const button of document.querySelectorAll(".seg[data-cal-field]")) {
  button.addEventListener("click", () => {
    calendarField = button.dataset.calField;
    saveCalendarField(calendarField);
    refresh();
  });
}

// ---------- list ----------

let loadSequence = 0;

async function loadEntries() {
  const sequence = ++loadSequence;
  const params = baseListParams();
  params.set("sort", $("sort").value);
  params.set("order", $("order").value);
  if (recentOnly) {
    // Rolling window, recomputed on every load (including polls)
    params.set("dateField", recentField());
    params.set("from", new Date(Date.now() - RECENT_WINDOW_MS).toISOString());
  } else if (selectedDay) {
    const range = dayRange(selectedDay, shownTimeZone());
    params.set("dateField", calendarField);
    params.set("from", range.from);
    params.set("to", range.to);
  }
  renderDayFilter();
  renderRecentToggle();

  try {
    const entries = await api("GET", `/api/entries?${params}`);
    if (sequence !== loadSequence) return; // a newer search/sort superseded this one

    $("entries").replaceChildren(...entries.map(renderEntry));
    $("empty").hidden = entries.length > 0;
    $("empty").textContent = recentOnly ? `No clips ${fieldVerb()} in the last 24 hours.`
      : selectedDay ? "No clips on that day."
      : params.has("search") ? "No entries match that search." : "No entries yet.";
    setMessage($("library-message"), "");
    schedulePoll(entries.some(isBusy));
  } catch (error) {
    setMessage($("library-message"), error.message, "error");
    schedulePoll(true);
  }
}

// Reloads the list and, if it is open, the calendar counts (after adds, deletes, searches, or a time zone/field change)
function refresh() {
  return Promise.all([loadEntries(), calendarOpen ? loadCalendar() : null]);
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
  await refresh();
}

let searchTimer = null;
$("search").addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(refresh, 250);
});
$("sort").addEventListener("change", loadEntries);
$("order").addEventListener("change", loadEntries);

renderTimeZoneToggle();
renderVodTypeToggle();
$("browse-channel").value = loadBrowseChannel();
loadSavedChannels();
renderCalendarOpen();
refresh();
