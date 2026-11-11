"use strict";

// ---------- the app player's own control bar ----------
//
// Replaces the browser's video controls for the app player (hls.js) so the timeline can show what matters to editors:
//  - parts Twitch muted (red) and parts that play with saved clean audio instead (green), read from the playlist the
//    player loaded (a muted segment's URL ends in "-muted.ts"; the server points ones with saved audio at ".../clean/")
//  - the team's library entries cut from this VOD (purple lane above the track)
//  - the team's markers and segments (GET/POST /api/vods/{id}/timeline, /markers), shared with everyone in the team. A segment
//    is a marker with an end ("intro", "Arky meets Sunny"): a hatched box with brackets at both ends
// The timeline zooms (wheel, +/−, Fit) so a 24-hour VOD can still be scrubbed to the second; a strip above it shows the
// whole VOD and the zoomed window. The Twitch embed fallback keeps Twitch's own controls; the bar is hidden then.
//
// Editing (tools bar to the right of the video): the pointer selects a segment (click it: the timeline zooms to it), then
// play only plays it, ±10s/±1s change its end, its edges can be dragged, × or Del deletes it, and its title and color can be
// changed. The cut tool splits a segment where it's clicked; the zoom tool zooms in on a click and out on a right click.
// Undo/redo (last 10 changes) is per browser tab: it only replays this user's own changes.
// Snapping (magnet at the top of the tools bar, on by default): a dragged edge sticks to any other time on the timeline within
// a few pixels (markers, segment edges, library cards' start/end, the reviewed clip, the playhead); keep dragging to pass it.
// New kinds of timeline items must add their times in snapPoints(). Reference cards' start/end (the purple lane, and the
// reviewed entry's edges) can be dragged too, and a segment dragged past a reference card's range stretches the card.
//
// Keys: see keybinds.js (the list of every shortcut; add new ones there first).
//
// Uses the page's helpers from app.js ($, api, can, me, formatTrimValue, formatDuration), which load after this file but
// before anything here runs.

const BAR_MIN_SPAN_SECONDS = 10;
const BAR_TRACK = { clipTop: 0, clipHeight: 7, top: 10, height: 22, labelTop: 34, height_total: 48 };
const BAR_TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600];
// Same names and order as VodMarker.Colors on the server
const MARKER_COLORS = {
  yellow: "#f2c230", blue: "#3d8bfd", pink: "#ff5fa2", orange: "#ff8a3d",
  red: "#ff4d4d", lime: "#9be15d", teal: "#2ec4b6", violet: "#a78bfa", gray: "#a1a1aa",
};
const MARKER_HIT_PX = 6;
const EDGE_HIT_PX = 6;
const MIN_SEGMENT_SECONDS = 0.5;
const MIN_CLIP_SECONDS = 1;
const SNAP_PX = 8;
const SNAP_KEY = "clipLibrary.snap";
const HISTORY_LIMIT = 10;

// Undo/redo per VOD, kept in this tab only: each user undoes their own changes, nothing is shared with the team
const markerHistory = new Map(); // VOD ID -> { undo: [steps], redo: [steps] }
// History steps hold a ref ({ id }) instead of a marker ID: undoing a delete makes the marker again with a new ID, and every
// step that points at it must follow
const markerRefs = new Map();

// True while the user is typing in a field (so the player doesn't take the focus away)
// Snapping is a per-browser choice, on unless turned off
function loadSnap() {
  try {
    return localStorage.getItem(SNAP_KEY) !== "off";
  } catch {
    return true;
  }
}

function isTyping(element) {
  if (!element) return false;
  if (element.isContentEditable || element.tagName === "TEXTAREA") return true;
  return element.tagName === "INPUT" && !["button", "checkbox", "radio", "range", "color", "submit", "reset"].includes(element.type);
}

function markerRef(id) {
  let ref = markerRefs.get(id);
  if (!ref) {
    ref = { id };
    markerRefs.set(id, ref);
  }
  return ref;
}

function markerData(marker) {
  return { seconds: marker.seconds, endSeconds: marker.endSeconds ?? null, label: marker.label ?? "", color: marker.color ?? "yellow" };
}

function sameMarkerData(a, b) {
  return a.seconds === b.seconds && (a.endSeconds ?? null) === (b.endSeconds ?? null) && a.label === b.label && a.color === b.color;
}

// "Segment A" -> "Segment A1"/"Segment A2"; "Intro" -> "Intro 1"/"Intro 2"; blank stays blank
function splitLabel(label, part) {
  if (!label) return "";
  const lastWord = label.split(/\s+/).pop();
  return lastWord.length === 1 ? `${label}${part}` : `${label} ${part}`;
}

function markerColor(marker) {
  return MARKER_COLORS[marker.color] ?? MARKER_COLORS.yellow;
}

function isSegment(marker) {
  return marker.endSeconds != null;
}

// "1:02:03 – 1:04:00 (1:57)" for a segment, "1:02:03" for a moment
function markerTimeText(marker) {
  if (!isSegment(marker)) return formatTrimValue(marker.seconds);
  return `${formatTrimValue(marker.seconds)} – ${formatTrimValue(marker.endSeconds)} (${formatTrimValue(marker.endSeconds - marker.seconds)})`;
}

// Hover text for a marker or segment, wherever it's shown
function markerTitle(marker) {
  const name = marker.label || (isSegment(marker) ? "Segment" : "Marker");
  return `${name}\n${markerTimeText(marker)}${marker.createdBy ? `\nAdded by ${marker.createdBy}` : ""}`;
}

// A segment on a canvas: light fill, diagonal hatching and brackets reaching past the track at both ends
function drawSegmentBox(ctx, a, b, top, height, color, dashed = false) {
  const w = Math.max(2, b - a);
  ctx.save();
  ctx.fillStyle = color;
  ctx.globalAlpha = 0.16;
  ctx.fillRect(a, top, w, height);
  ctx.beginPath();
  ctx.rect(a, top, w, height);
  ctx.clip();
  ctx.globalAlpha = 0.85;
  ctx.strokeStyle = color;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  for (let hx = a - height; hx < a + w; hx += 7) {
    ctx.moveTo(hx, top + height);
    ctx.lineTo(hx + height, top);
  }
  ctx.stroke();
  ctx.restore();
  ctx.save();
  ctx.strokeStyle = color;
  ctx.lineWidth = 2;
  if (dashed) ctx.setLineDash([4, 3]);
  ctx.strokeRect(a + 1, top + 1, Math.max(0, w - 2), height - 2);
  ctx.setLineDash([]);
  ctx.beginPath();
  ctx.moveTo(a + 1, top - 4);
  ctx.lineTo(a + 1, top + height + 4);
  ctx.moveTo(a + w - 1, top - 4);
  ctx.lineTo(a + w - 1, top + height + 4);
  ctx.stroke();
  ctx.restore();
}

// Chips for a library entry (like the list under the player): the markers and segments between from and to. Null when none.
function markerChips(markers, from, to) {
  const inside = markers.filter((m) => (isSegment(m) ? m.endSeconds > from && m.seconds < to : m.seconds >= from && m.seconds <= to));
  if (!inside.length) return null;
  const list = document.createElement("div");
  list.className = "vp-markers entry-markers";
  list.setAttribute("aria-label", "Team markers and segments");
  for (const marker of inside) {
    const chip = document.createElement("span");
    chip.className = "vp-chip";
    chip.title = markerTitle(marker);
    const body = document.createElement("span");
    body.className = "vp-chip-go";
    const dot = document.createElement("i");
    dot.style.background = markerColor(marker);
    if (isSegment(marker)) dot.className = "vp-chip-segment";
    const time = document.createElement("strong");
    time.textContent = isSegment(marker) ? `${formatTrimValue(marker.seconds)}–${formatTrimValue(marker.endSeconds)}` : formatTrimValue(marker.seconds);
    body.append(dot, time, marker.label ? ` ${marker.label}` : "");
    if (marker.createdBy) {
      const by = document.createElement("span");
      by.className = "muted";
      by.textContent = ` · ${marker.createdBy}`;
      body.append(by);
    }
    chip.append(body);
    list.append(chip);
  }
  return list;
}

// A small strip for a library entry or VOD card: the markers and segments that fall between from and to (VOD seconds).
// Null when there are none. Hovering one shows its title and times.
function segmentStrip(markers, from, to) {
  const span = to - from;
  if (!(span > 0)) return null;
  const inside = markers.filter((m) => (isSegment(m) ? m.endSeconds > from && m.seconds < to : m.seconds >= from && m.seconds <= to));
  if (!inside.length) return null;
  const strip = document.createElement("div");
  strip.className = "seg-strip";
  strip.setAttribute("role", "list");
  strip.setAttribute("aria-label", "Team markers and segments");
  const pct = (t) => `${(Math.min(Math.max(t, from), to) - from) / span * 100}%`;
  for (const marker of inside) {
    const item = document.createElement("span");
    item.setAttribute("role", "listitem");
    item.style.setProperty("--seg-color", markerColor(marker));
    item.title = markerTitle(marker);
    item.setAttribute("aria-label", markerTitle(marker).replace(/\n/g, ", "));
    item.style.left = pct(marker.seconds);
    if (isSegment(marker)) {
      // A segment that runs on past this clip's edge gets no bracket on that side
      item.className = `seg-box${marker.seconds < from ? " seg-open-start" : ""}${marker.endSeconds > to ? " seg-open-end" : ""}`;
      item.style.width = `calc(${pct(marker.endSeconds)} - ${pct(marker.seconds)})`;
    } else {
      item.className = "seg-mark";
    }
    strip.append(item);
  }
  return strip;
}

class PlayerBar {
  constructor(wrapId, barId) {
    this.wrap = $(wrapId);
    this.el = $(barId);
    this.backend = null;
    this.vodId = null;
    this.ranges = []; // [{ start, end, clean }] muted parts, merged
    this.clips = [];
    this.markers = [];
    this.view = { from: 0, to: 0 };
    this.zoomed = false;
    // True while the view comes from an automatic zoom (zoomTo: selecting a segment, opening a review), false once the user
    // zooms or pans. An automatic view never cuts off the selected segment; only the user's own zooming may go deeper.
    this.autoView = false;
    this.pan = null; // { x, from, to } while dragging the view with the hand tool
    this.snap = loadSnap(); // dragged edges stick to other times on the timeline
    this.snapAt = null; // the time a dragged edge is stuck to right now (drawn as a guide)
    this.manualPan = false; // the user scrolled the view away from the playhead (hand, Shift+wheel): don't follow it
    this.listDuration = 0; // playlist length (for live VODs, where video.duration is Infinity)
    this.dirty = true;
    this.frame = null;
    this.scrubbing = false;
    this.hoverX = null;
    this.pendingMarker = null; // true while the add form is open
    this.segmentStart = null; // seconds where a segment being marked starts (S pressed once)
    // Set by the page before a player opens (see configure): the clip review shows the bar's controls without the VOD timeline
    // for a short clip ("compact", with a Full VOD timeline button), or starts zoomed in on the entry for a long reference
    this.options = {};
    this.compact = false;
    this.focus = null; // { start, end } of the entry being reviewed, highlighted on the timeline
    this.focusWindow = null; // { from, to } the review's trim bar shows; compact chips list only what's in it
    this.pendingZoom = null; // { from, to } to zoom to once the duration is known
    this.onMarkersChanged = null;
    // Editing
    this.tool = "pointer"; // pointer | cut | zoom
    this.selectedRef = null; // ref ({ id }) of the selected segment
    this.drag = null; // { ref, edge: "start"|"end", before } while an edge of the selected segment is dragged
    this.pendingEdit = null; // { ref, before } while ±10s/±1s presses are collected into one change
    this.pendingEditTimer = null;
    this.playUntil = null; // pause here (playing the selected segment)
    this.work = Promise.resolve(); // marker requests run one after another
    this.build();
  }

  // ---- setup ----

  build() {
    this.el.innerHTML = `
      <canvas class="vp-overview" height="12" aria-hidden="true"></canvas>
      <div class="vp-timeline-wrap">
        <canvas class="vp-timeline" aria-label="Timeline: click or drag to seek, wheel to zoom"></canvas>
        <div class="vp-tip" hidden></div>
      </div>
      <div class="vp-controls">
        <button type="button" class="vp-btn vp-play" data-act="play" aria-label="Play" title="Play / pause (Space, K)">▶</button>
        <button type="button" class="vp-btn vp-len" data-act="back10" title="Back 10 s (J)">−10s</button>
        <button type="button" class="vp-btn vp-len" data-act="back1" title="Back 1 s">−1s</button>
        <button type="button" class="vp-btn" data-act="frameback" title="One frame back (,)">‹</button>
        <button type="button" class="vp-btn" data-act="framefwd" title="One frame forward (.)">›</button>
        <button type="button" class="vp-btn vp-len" data-act="fwd1" title="Forward 1 s">+1s</button>
        <button type="button" class="vp-btn vp-len" data-act="fwd10" title="Forward 10 s (L)">+10s</button>
        <span class="vp-time"><strong class="vp-now">0:00:00</strong> / <span class="vp-total">–</span></span>
        <button type="button" class="vp-btn vp-mute" data-act="mute" aria-label="Mute" title="Mute">🔊</button>
        <input type="range" class="vp-volume" min="0" max="100" step="1" aria-label="Volume">
        <span class="vp-spacer"></span>
        <span class="vp-zoom">
          <button type="button" class="vp-btn" data-act="zoomout" aria-label="Zoom out" title="Zoom out (−)">−</button>
          <span class="vp-zoom-label">All</span>
          <button type="button" class="vp-btn" data-act="zoomin" aria-label="Zoom in" title="Zoom in (+, or the mouse wheel on the timeline)">+</button>
          <button type="button" class="vp-btn" data-act="fit" title="Show the whole VOD (0)">Fit</button>
        </span>
        <button type="button" class="vp-btn vp-add-marker" data-act="marker" title="Add a marker here (M)">+ Marker</button>
        <button type="button" class="vp-btn vp-add-segment" data-act="segment" title="Mark a segment: press at its start, then at its end (S)">[ Segment</button>
        <button type="button" class="vp-btn vp-expand" data-act="expand" title="Show the whole VOD's timeline, zoomed in on this clip" hidden>Full VOD timeline</button>
        <button type="button" class="vp-btn" data-act="fullscreen" aria-label="Fullscreen" title="Fullscreen (F)">⛶</button>
      </div>
      <div class="vp-marker-form" hidden>
        <strong class="vp-marker-kind">Marker</strong>
        <label class="vp-time-field">Start <input class="vp-marker-start" autocomplete="off" spellcheck="false">
          <button type="button" class="link small" data-act="startnow" title="Use the player's position">here</button></label>
        <label class="vp-time-field">End <input class="vp-marker-end" placeholder="none" autocomplete="off" spellcheck="false">
          <button type="button" class="link small" data-act="endnow" title="Use the player's position">here</button></label>
        <input class="vp-marker-label" maxlength="200" placeholder="Title, e.g. intro (optional)" autocomplete="off">
        <span class="vp-swatches" role="radiogroup" aria-label="Marker color">
          ${Object.entries(MARKER_COLORS).map(([name, color], i) => `
            <label class="vp-swatch" title="${name}"><input type="radio" name="vp-color" value="${name}" ${i === 0 ? "checked" : ""}>
              <span style="background:${color}"></span></label>`).join("")}
        </span>
        <button type="button" class="small" data-act="savemarker">Save marker</button>
        <button type="button" class="link small" data-act="cancelmarker">Cancel</button>
        <span class="vp-marker-message message" role="status"></span>
      </div>
      <div class="vp-selection" hidden>
        <strong class="vp-sel-kind">Selected segment</strong>
        <span class="vp-sel-time"></span>
        <input class="vp-sel-label" maxlength="200" placeholder="Title" autocomplete="off" aria-label="Segment title">
        <span class="vp-swatches vp-sel-swatches" role="radiogroup" aria-label="Segment color">
          ${Object.entries(MARKER_COLORS).map(([name, color]) => `
            <label class="vp-swatch" title="${name}"><input type="radio" name="vp-sel-color" value="${name}">
              <span style="background:${color}"></span></label>`).join("")}
        </span>
        <span class="vp-sel-hint muted"></span>
        <button type="button" class="link small" data-act="deselect">Done</button>
        <span class="vp-sel-message message" role="status"></span>
      </div>
      <div class="vp-markers"></div>
      <div class="vp-legend muted">
        <span><i class="vp-key vp-key-muted"></i>Muted by Twitch</span>
        <span><i class="vp-key vp-key-clean"></i>Plays saved clean audio</span>
        <span><i class="vp-key vp-key-clip"></i>Library clips</span>
        <span><i class="vp-key vp-key-marker"></i>Team markers</span>
        <span><i class="vp-key vp-key-segment"></i>Team segments</span>
      </div>`;

    this.overview = this.el.querySelector(".vp-overview");
    this.canvas = this.el.querySelector(".vp-timeline");
    this.tip = this.el.querySelector(".vp-tip");
    this.form = this.el.querySelector(".vp-marker-form");
    this.timelineWrap = this.el.querySelector(".vp-timeline-wrap");
    this.legend = this.el.querySelector(".vp-legend");
    this.selection = this.el.querySelector(".vp-selection");
    // Clicking anywhere else in the Watch / Review window unselects the segment (the timeline has its own rules; the parts that
    // work on the selection keep it)
    const area = this.wrap.closest("dialog") ?? this.wrap.closest(".watch") ?? this.wrap;
    area.addEventListener("pointerdown", (event) => {
      if (!this.selectedRef || !(event.target instanceof Element)) return;
      if (event.target.closest(".vp-timeline, .vp-overview, .vp-selection, .vp-markers, .vp-tools, .vp-sel-x, .vp-marker-form, " +
        "[data-act=play], [data-act=back10], [data-act=back1], [data-act=fwd1], [data-act=fwd10]")) return;
      this.deselect();
    });
    this.buildTools();
    this.buildSelection();

    this.el.querySelector(".vp-controls").addEventListener("click", (event) => {
      const act = event.target.closest("[data-act]")?.dataset.act;
      if (act) this.act(act);
    });
    this.el.querySelector(".vp-volume").addEventListener("input", (event) => {
      if (!this.video) return;
      this.video.volume = Number(event.target.value) / 100;
      this.video.muted = this.video.volume === 0;
    });
    this.form.querySelector("[data-act=savemarker]").addEventListener("click", () => this.saveMarker());
    this.form.querySelector("[data-act=cancelmarker]").addEventListener("click", () => this.closeMarkerForm());
    this.form.querySelector("[data-act=startnow]").addEventListener("click", () => this.setFormTime(".vp-marker-start"));
    this.form.querySelector("[data-act=endnow]").addEventListener("click", () => this.setFormTime(".vp-marker-end"));
    this.form.querySelector(".vp-marker-end").addEventListener("input", () => this.renderFormKind());
    // The player can sit inside the page's add form: Enter must save the marker, not submit that form
    this.form.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        event.stopPropagation();
        this.saveMarker();
      } else if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        this.closeMarkerForm();
      }
    });

    this.canvas.addEventListener("pointerdown", (event) => this.onPointerDown(event));
    this.canvas.addEventListener("pointermove", (event) => this.onPointerMove(event));
    this.canvas.addEventListener("pointerup", (event) => this.onPointerUp(event));
    this.canvas.addEventListener("pointercancel", () => {
      this.scrubbing = false;
      this.pan = null;
      if (this.drag) this.endDrag();
    });
    this.canvas.addEventListener("pointerleave", () => {
      this.hoverX = null;
      this.tip.hidden = true;
      this.dirty = true;
    });
    this.canvas.addEventListener("wheel", (event) => this.onWheel(event), { passive: false });
    // The zoom tool zooms out on a right click
    this.canvas.addEventListener("contextmenu", (event) => {
      if (this.tool === "zoom") event.preventDefault();
    });
    this.overview.addEventListener("pointerdown", (event) => {
      const time = this.overviewTime(event);
      if (time == null) return;
      // Jumping somewhere on the whole-VOD strip leaves the selected segment
      this.deselect();
      this.centerOn(time);
      this.seek(time);
    });

    this.onKey = (event) => this.onKeyDown(event);
    this.wrap.addEventListener("keydown", this.onKey);
    this.onResize = () => (this.dirty = true);
    window.addEventListener("resize", this.onResize);
    this.wrap.addEventListener("fullscreenchange", () => (this.dirty = true));
  }

  // The vertical tools bar to the right of the video (pointer, cut, zoom, undo, redo)
  buildTools() {
    this.tools = document.createElement("div");
    this.tools.className = "vp-tools";
    this.tools.hidden = true;
    this.tools.setAttribute("role", "toolbar");
    this.tools.setAttribute("aria-label", "Timeline tools");
    this.tools.setAttribute("aria-orientation", "vertical");
    this.tools.innerHTML = `
      <button type="button" class="vp-tool vp-tool-snap" data-act="snap" title="Snapping (${keybindLabel("toggleSnap")}): dragged edges stick to markers, segments, cards and the playhead; keep dragging to pass" aria-label="Snapping">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 3h4v7a2 2 0 0 0 4 0V3h4v7a6 6 0 0 1-12 0z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/><path d="M4 6.5h4M12 6.5h4" stroke="currentColor" stroke-width="1.8"/></svg></button>
      <span class="vp-tools-gap"></span>
      <button type="button" class="vp-tool" data-tool="pointer" title="Pointer (${keybindLabel("toolPointer")}): click to seek, click a segment to select it" aria-label="Pointer">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5 2.5v14l3.6-3.4 2.6 5.4 2.2-1-2.6-5.3H16z" fill="currentColor"/></svg></button>
      <button type="button" class="vp-tool vp-tool-cut" data-tool="cut" title="Cut (${keybindLabel("toolCut")}): click a segment to split it in two" aria-label="Cut">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="5.5" cy="14.5" r="2.6" fill="none" stroke="currentColor" stroke-width="1.8"/><circle cx="14.5" cy="14.5" r="2.6" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M7.3 12.6 15 2.5M12.7 12.6 5 2.5" stroke="currentColor" stroke-width="1.8" fill="none"/></svg></button>
      <button type="button" class="vp-tool" data-tool="zoom" title="Zoom (${keybindLabel("toolZoom")}): click the timeline to zoom in, right click to zoom out" aria-label="Zoom">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" fill="none" stroke="currentColor" stroke-width="2"/><path d="M12.5 12.5 18 18M6 8.5h5M8.5 6v5" stroke="currentColor" stroke-width="2"/></svg></button>
      <button type="button" class="vp-tool" data-tool="hand" title="Hand (${keybindLabel("toolHand")}): drag the zoomed timeline to scroll it (only moves the view)" aria-label="Hand">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6.2 10.5V4.8a1.2 1.2 0 0 1 2.4 0v4.7M8.6 9.2V3.4a1.2 1.2 0 0 1 2.4 0v5.8M11 9.2V4.2a1.2 1.2 0 0 1 2.4 0v5.6M13.4 9.6V6.4a1.2 1.2 0 0 1 2.4 0v5.4c0 3.4-2.3 5.7-5.4 5.7-2.2 0-3.5-.9-4.7-2.6L3.6 11.7a1.2 1.2 0 0 1 1.9-1.4l.7.9" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"/></svg></button>
      <span class="vp-tools-gap"></span>
      <button type="button" class="vp-tool" data-act="undo" title="Undo your last change (${keybindLabel("undo")})" aria-label="Undo" disabled>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M8 4 3.5 8.5 8 13" fill="none" stroke="currentColor" stroke-width="2"/><path d="M4 8.5h7.5a4.5 4.5 0 0 1 0 9H9" fill="none" stroke="currentColor" stroke-width="2"/></svg></button>
      <button type="button" class="vp-tool" data-act="redo" title="Redo (${keybindLabel("redo")})" aria-label="Redo" disabled>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12 4l4.5 4.5L12 13" fill="none" stroke="currentColor" stroke-width="2"/><path d="M16 8.5H8.5a4.5 4.5 0 0 0 0 9H11" fill="none" stroke="currentColor" stroke-width="2"/></svg></button>`;
    this.tools.addEventListener("click", (event) => {
      const button = event.target.closest("button");
      if (!button) return;
      if (button.dataset.tool) this.setTool(button.dataset.tool);
      else if (button.dataset.act === "snap") this.toggleSnap();
      else if (button.dataset.act === "undo") this.undo();
      else if (button.dataset.act === "redo") this.redo();
      this.wrap.focus({ preventScroll: true });
    });
    // Next to the video: the frame moves into a row with the tools bar
    const frame = this.wrap.querySelector(".player-frame");
    const stage = document.createElement("div");
    stage.className = "vp-stage";
    frame.before(stage);
    stage.append(frame, this.tools);

    // The selected segment's delete button sits on the timeline, a little past the segment's end
    this.selX = document.createElement("button");
    this.selX.type = "button";
    this.selX.className = "vp-sel-x";
    this.selX.textContent = "✕";
    this.selX.hidden = true;
    this.selX.title = `Delete this segment (${keybindLabel("deleteSelected")})`;
    this.selX.setAttribute("aria-label", "Delete the selected segment");
    this.selX.addEventListener("click", () => this.deleteSelected());
    this.timelineWrap.append(this.selX);
  }

  buildSelection() {
    const label = this.selection.querySelector(".vp-sel-label");
    label.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault(); // the player can sit inside the page's add form
        event.stopPropagation();
        label.blur();
      } else if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        label.value = this.selected()?.label ?? "";
        label.blur();
      }
    });
    label.addEventListener("change", () => this.editSelected({ label: label.value.trim() }));
    this.selection.querySelector(".vp-sel-swatches").addEventListener("change", (event) => {
      if (event.target.name === "vp-sel-color") this.editSelected({ color: event.target.value });
    });
    this.selection.querySelector("[data-act=deselect]").addEventListener("click", () => this.deselect());
  }

  // Options for the next attach: { compact, focus: {start,end}, focusWindow: {from,to}, zoom: {from,to}, expandZoom: () => {from,to} }
  configure(options) {
    this.options = options ?? {};
  }

  // Shows the bar for an app-player backend (HlsBackend) playing a VOD
  attach(backend, vodId) {
    this.detach();
    this.backend = backend;
    this.vodId = String(vodId);
    this.video = backend.video;
    this.video.controls = false;
    this.ranges = [];
    this.clips = [];
    this.markers = [];
    this.zoomed = false;
    this.autoView = false;
    this.manualPan = false;
    this.compact = Boolean(this.options.compact);
    this.focus = this.options.focus ?? null;
    this.focusWindow = this.options.focusWindow ?? null;
    this.focusEntryId = this.options.focusEntryId ?? null; // the entry being reviewed (its edges are this.focus)
    this.pendingZoom = this.options.zoom ?? null;
    this.selectedRef = null;
    this.drag = null;
    this.playUntil = null;
    this.closeMarkerForm();
    this.cancelSegment();
    this.el.hidden = false;
    this.wrap.classList.add("has-bar");
    const canAdd = can("AddEntries") && !me?.support;
    this.el.querySelector(".vp-add-marker").hidden = !canAdd;
    this.el.querySelector(".vp-add-segment").hidden = !canAdd;
    this.tools.querySelector(".vp-tool-cut").hidden = !canAdd;
    // Every opening starts with the pointer tool (badrfly)
    this.tool = "pointer";
    this.pan = null;
    this.canvas.style.cursor = "";
    this.applyLayout();
    this.renderTools();
    this.renderSelection();
    // Keys work right away after Watch / Review, without clicking the player first (unless the user is typing somewhere)
    if (!isTyping(document.activeElement)) this.wrap.focus({ preventScroll: true });

    backend.onDetails = (details) => this.readPlaylist(details);
    if (backend.details) this.readPlaylist(backend.details);

    const video = this.video;
    this.videoListeners = {
      play: () => this.renderButtons(),
      pause: () => {
        this.playUntil = null;
        this.renderButtons();
      },
      volumechange: () => this.renderButtons(),
      durationchange: () => this.fitIfNeeded(),
      progress: () => (this.dirty = true),
      seeked: () => (this.dirty = true),
      click: () => this.togglePlay(),
      dblclick: () => this.toggleFullscreen(),
    };
    for (const [name, handler] of Object.entries(this.videoListeners)) video.addEventListener(name, handler);

    this.renderButtons();
    this.fitIfNeeded();
    this.loadTimeline();
    const loop = () => {
      if (!this.backend) return;
      this.update();
      this.frame = requestAnimationFrame(loop);
    };
    this.frame = requestAnimationFrame(loop);
  }

  detach() {
    if (this.frame) cancelAnimationFrame(this.frame);
    this.frame = null;
    if (this.video && this.videoListeners) {
      for (const [name, handler] of Object.entries(this.videoListeners)) this.video.removeEventListener(name, handler);
    }
    if (this.backend) this.backend.onDetails = null;
    if (document.fullscreenElement === this.wrap) document.exitFullscreen().catch(() => {});
    this.backend = null;
    this.video = null;
    this.vodId = null;
    this.el.hidden = true;
    this.tools.hidden = true;
    this.selX.hidden = true;
    this.wrap.classList.remove("has-bar");
    this.tip.hidden = true;
    this.flushPendingEdit();
  }

  // ---- data ----

  // Muted parts from the playlist hls.js loaded (all renditions are muted in the same places)
  readPlaylist(details) {
    const ranges = [];
    for (const frag of details?.fragments ?? []) {
      const url = frag.url || frag.relurl || "";
      const clean = url.includes("/clean/");
      const muted = clean || /-muted\.(ts|mp4)(\?|$)/i.test(url);
      if (!muted) continue;
      const last = ranges[ranges.length - 1];
      if (last && last.clean === clean && Math.abs(last.end - frag.start) < 0.05) last.end = frag.start + frag.duration;
      else ranges.push({ start: frag.start, end: frag.start + frag.duration, clean });
    }
    this.ranges = ranges;
    const fragments = details?.fragments ?? [];
    const lastFrag = fragments[fragments.length - 1];
    this.listDuration = lastFrag ? lastFrag.start + lastFrag.duration : 0;
    this.fitIfNeeded();
    this.dirty = true;
  }

  async loadTimeline() {
    const vodId = this.vodId;
    try {
      const timeline = await api("GET", `/api/vods/${encodeURIComponent(vodId)}/timeline`);
      if (vodId !== this.vodId) return;
      this.clips = timeline.clips;
      this.markers = timeline.markers;
      this.syncFocusClip();
    } catch (error) {
      console.warn("Couldn't load the timeline's clips and markers:", error);
    }
    this.renderSelection();
    this.renderMarkers();
    this.dirty = true;
    this.onMarkersChanged?.();
  }

  // Called by the page after entries change (added, re-cut, deleted)
  reload() {
    if (this.backend) this.loadTimeline();
  }

  duration() {
    const d = this.video?.duration;
    if (Number.isFinite(d) && d > 0) return d;
    return this.listDuration || 0;
  }

  applyLayout() {
    this.timelineWrap.hidden = this.compact;
    this.legend.hidden = this.compact;
    this.el.querySelector(".vp-zoom").hidden = this.compact;
    this.el.querySelector(".vp-expand").hidden = !this.compact;
    // The tools work on the timeline, which a compact bar doesn't show
    this.tools.hidden = this.compact || !this.backend;
    if (this.compact) this.deselect();
    this.dirty = true;
  }

  // The review dialog's short-clip mode: show the whole VOD's timeline, zoomed in on the clip
  expand() {
    this.compact = false;
    this.applyLayout();
    this.zoomTo(this.options.expandZoom?.() ?? this.focusWindow ?? this.focus);
    this.renderMarkers();
  }

  // The entry being reviewed (highlighted on the timeline) and the part of the VOD the review's trim bar shows
  setFocus(focus, focusWindow) {
    this.focus = focus;
    this.syncFocusClip();
    const windowChanged = this.focusWindow?.from !== focusWindow?.from || this.focusWindow?.to !== focusWindow?.to;
    this.focusWindow = focusWindow;
    if (windowChanged && this.compact) this.renderMarkers();
    this.dirty = true;
  }

  // The automatic zoom: every zoom the app makes by itself goes through here. It always shows the whole range (never
  // zooms in past it); a range shorter than the minimum span is centered.
  zoomTo(range) {
    if (!range) return;
    const d = this.duration();
    if (!d) {
      this.pendingZoom = range;
      return;
    }
    const length = range.to - range.from;
    const span = Math.max(BAR_MIN_SPAN_SECONDS, length);
    this.autoView = true;
    this.manualPan = false;
    if (span >= d) {
      this.zoomed = false;
      this.fitIfNeeded();
      return;
    }
    const from = range.from - (span - length) / 2;
    this.view = { from, to: from + span };
    this.zoomed = true;
    this.clampView();
    this.dirty = true;
  }

  // A segment with some room before and after (20 %, at least 2 s), optionally stretched to also show a time
  segmentView(segment, alsoShow) {
    const margin = Math.max(2, (segment.endSeconds - segment.seconds) * 0.2);
    let from = segment.seconds - margin;
    let to = segment.endSeconds + margin;
    if (alsoShow != null) {
      from = Math.min(from, alsoShow - margin);
      to = Math.max(to, alsoShow + margin);
    }
    return { from: Math.max(0, from), to };
  }

  fitIfNeeded() {
    const d = this.duration();
    if (!d) return;
    if (this.pendingZoom) {
      const range = this.pendingZoom;
      this.pendingZoom = null;
      this.zoomTo(range);
    }
    if (!this.zoomed) this.view = { from: 0, to: d };
    else this.clampView();
    this.el.querySelector(".vp-total").textContent = formatDuration(d);
    this.dirty = true;
  }

  // ---- actions ----

  act(act) {
    const video = this.video;
    if (!video) return;
    switch (act) {
      case "play": return this.togglePlay();
      // With a segment selected, these change its length (its end) instead of seeking
      case "back10": return this.selectedEditable() ? this.nudgeSelected(-10) : this.seek(this.now() - 10);
      case "fwd10": return this.selectedEditable() ? this.nudgeSelected(10) : this.seek(this.now() + 10);
      case "back1": return this.selectedEditable() ? this.nudgeSelected(-1) : this.seek(this.now() - 1);
      case "fwd1": return this.selectedEditable() ? this.nudgeSelected(1) : this.seek(this.now() + 1);
      case "frameback": return this.step(-1);
      case "framefwd": return this.step(1);
      case "mute":
        if (video.muted || video.volume === 0) {
          video.muted = false;
          if (video.volume === 0) video.volume = 0.5;
        } else {
          video.muted = true;
        }
        return;
      case "zoomin": return this.zoomBy(0.5, this.now());
      case "zoomout": return this.zoomBy(2, this.now());
      case "fit": return this.fit();
      case "marker": return this.openMarkerForm();
      case "segment": return this.toggleSegment();
      case "fullscreen": return this.toggleFullscreen();
      case "expand": return this.expand();
    }
  }

  now() {
    return this.video?.currentTime ?? 0;
  }

  seek(seconds) {
    this.manualPan = false; // the view follows the playhead again
    const d = this.duration();
    this.video.currentTime = Math.max(0, d ? Math.min(seconds, d - 0.05) : seconds);
    this.dirty = true;
  }

  togglePlay() {
    if (!this.video) return;
    if (!this.video.paused) return this.video.pause();
    // A selected segment plays from its start (unless the playhead is already inside it) and stops at its end
    const segment = this.selected();
    if (segment) {
      const now = this.now();
      if (now < segment.seconds || now >= segment.endSeconds - 0.05) this.seek(segment.seconds);
      this.video.play().then(() => (this.playUntil = segment.endSeconds)).catch(() => {});
      return;
    }
    this.video.play().catch(() => {});
  }

  // One frame at the playing rendition's frame rate
  step(direction) {
    this.video.pause();
    const hls = this.backend?.hls;
    const level = hls?.levels?.[hls.currentLevel >= 0 ? hls.currentLevel : 0];
    const fps = level?.frameRate || 30;
    this.seek(this.now() + direction / fps);
  }

  toggleFullscreen() {
    if (document.fullscreenElement === this.wrap) document.exitFullscreen().catch(() => {});
    else this.wrap.requestFullscreen?.().catch(() => {});
  }

  fit() {
    this.zoomed = false;
    this.autoView = false;
    this.fitIfNeeded();
  }

  zoomBy(factor, anchor) {
    const d = this.duration();
    if (!d) return;
    const span = this.view.to - this.view.from;
    const next = Math.min(d, Math.max(BAR_MIN_SPAN_SECONDS, span * factor));
    this.autoView = false;
    this.manualPan = false;
    if (next >= d) return this.fit();
    // Keep the anchor at the same place on screen
    const ratio = span > 0 ? (anchor - this.view.from) / span : 0.5;
    this.view = { from: anchor - next * ratio, to: anchor - next * ratio + next };
    this.zoomed = true;
    this.clampView();
    this.dirty = true;
  }

  centerOn(seconds) {
    if (!this.zoomed) return;
    this.autoView = false;
    const span = this.view.to - this.view.from;
    this.view = { from: seconds - span / 2, to: seconds + span / 2 };
    this.clampView();
  }

  clampView() {
    const d = this.duration();
    let { from, to } = this.view;
    const span = Math.min(to - from, d);
    if (from < 0) from = 0;
    if (from + span > d) from = d - span;
    this.view = { from, to: from + span };
  }

  // ---- markers ----

  canAdd() {
    return Boolean(this.video) && !this.el.querySelector(".vp-add-marker").hidden;
  }

  // First press: the segment starts here. Second press: it ends here, and the form opens to name it.
  toggleSegment() {
    if (!this.canAdd()) return;
    if (this.segmentStart == null) {
      this.closeMarkerForm();
      this.segmentStart = this.now();
      this.renderSegmentButton();
      this.dirty = true;
      return;
    }
    const a = this.segmentStart;
    const b = this.now();
    this.cancelSegment();
    this.openMarkerForm(Math.min(a, b), Math.max(a, b));
  }

  cancelSegment() {
    this.segmentStart = null;
    this.renderSegmentButton();
    this.dirty = true;
  }

  renderSegmentButton() {
    const button = this.el.querySelector(".vp-add-segment");
    button.textContent = this.segmentStart == null ? "[ Segment" : `] End segment (from ${formatTrimValue(this.segmentStart)})`;
    button.classList.toggle("vp-btn-active", this.segmentStart != null);
  }

  openMarkerForm(start = this.now(), end = null) {
    if (!this.canAdd()) return;
    this.cancelSegment();
    this.video.pause();
    this.pendingMarker = true;
    this.form.querySelector(".vp-marker-start").value = formatTrimValue(start);
    this.form.querySelector(".vp-marker-end").value = end == null ? "" : formatTrimValue(end);
    this.form.querySelector(".vp-marker-message").textContent = "";
    this.form.querySelector(".vp-marker-label").value = "";
    this.renderFormKind();
    this.form.hidden = false;
    this.form.querySelector(".vp-marker-label").focus();
  }

  renderFormKind() {
    const segment = this.form.querySelector(".vp-marker-end").value.trim() !== "";
    this.form.querySelector(".vp-marker-kind").textContent = segment ? "Segment" : "Marker";
    this.form.querySelector("[data-act=savemarker]").textContent = segment ? "Save segment" : "Save marker";
  }

  setFormTime(selector) {
    this.form.querySelector(selector).value = formatTrimValue(this.now());
    this.renderFormKind();
  }

  closeMarkerForm() {
    this.pendingMarker = null;
    this.form.hidden = true;
  }

  // The form's start/end, or an error message
  formTimes() {
    const start = parseTrim(this.form.querySelector(".vp-marker-start").value);
    const end = parseTrim(this.form.querySelector(".vp-marker-end").value);
    if (start == null || Number.isNaN(start)) return { error: "Give the start as h:mm:ss." };
    if (Number.isNaN(end)) return { error: "Give the end as h:mm:ss, or leave it blank for a single moment." };
    if (end != null && end < start + MIN_SEGMENT_SECONDS) return { error: `The end must be at least ${MIN_SEGMENT_SECONDS} s after the start.` };
    return { start, end };
  }

  async saveMarker() {
    if (this.pendingMarker == null) return;
    const label = this.form.querySelector(".vp-marker-label").value.trim();
    const color = this.form.querySelector("input[name=vp-color]:checked")?.value ?? "yellow";
    const message = this.form.querySelector(".vp-marker-message");
    const times = this.formTimes();
    if (times.error) {
      message.textContent = times.error;
      return;
    }
    message.textContent = "Saving…";
    const data = { seconds: times.start, endSeconds: times.end, label, color };
    try {
      await this.run(async () => {
        const ref = { id: null };
        await this.createMarker(ref, data);
        this.record({ kind: "add", ref, after: data });
      });
      this.closeMarkerForm();
      this.wrap.focus({ preventScroll: true });
    } catch (error) {
      message.textContent = error.message;
    }
  }

  // From a chip's ✕ (asks first; undo brings it back)
  async deleteMarker(marker) {
    const what = isSegment(marker) ? "segment" : "marker";
    if (!confirm(`Delete the ${what} ${isSegment(marker) ? "from" : "at"} ${isSegment(marker)
      ? `${formatTrimValue(marker.seconds)} to ${formatTrimValue(marker.endSeconds)}` : formatTrimValue(marker.seconds)}${marker.label ? ` ("${marker.label}")` : ""}?`)) return;
    await this.removeWithHistory(marker);
  }

  async removeWithHistory(marker) {
    const ref = markerRef(marker.id);
    const before = markerData(marker);
    try {
      await this.run(async () => {
        await this.removeMarker(ref);
        this.record({ kind: "delete", ref, before });
      });
    } catch (error) {
      this.flash(error.message);
    }
  }

  // ---- editing: selection, tools, undo ----

  selected() {
    if (!this.selectedRef) return null;
    return this.markers.find((m) => m.id === this.selectedRef.id && isSegment(m)) ?? null;
  }

  selectedEditable() {
    const segment = this.selected();
    return segment?.canEdit ? segment : null;
  }

  // Selects a segment and zooms the timeline to it, with some room before and after. While playing (another segment, or
  // anything), playback jumps to the new segment's start and stops at its end.
  select(segment) {
    this.flushPendingEdit();
    const playing = Boolean(this.video && !this.video.paused);
    this.selectedRef = markerRef(segment.id);
    this.playUntil = null;
    this.zoomTo(this.segmentView(segment));
    if (playing) {
      this.seek(segment.seconds);
      this.playUntil = segment.endSeconds;
    }
    this.renderSelection();
    this.renderMarkers();
    this.dirty = true;
  }

  deselect() {
    if (!this.selectedRef) return;
    this.flushPendingEdit();
    this.selectedRef = null;
    this.playUntil = null;
    this.renderSelection();
    this.renderMarkers();
    this.dirty = true;
  }

  renderSelection() {
    const segment = this.selected();
    if (this.selectedRef && !segment && !this.pendingEdit) this.selectedRef = null; // deleted (here or by someone else)
    this.selection.hidden = !segment;
    this.el.querySelector(".vp-controls").classList.toggle("vp-seg-mode", Boolean(segment?.canEdit));
    for (const [act, seconds] of [["back10", -10], ["back1", -1], ["fwd1", 1], ["fwd10", 10]]) {
      this.el.querySelector(`[data-act=${act}]`).title = segment?.canEdit
        ? `${seconds > 0 ? "Lengthen" : "Shorten"} the selected segment by ${Math.abs(seconds)} s (moves its end)`
        : { back10: "Back 10 s (J)", back1: "Back 1 s", fwd1: "Forward 1 s", fwd10: "Forward 10 s (L)" }[act];
    }
    if (!segment) {
      this.selX.hidden = true;
      return;
    }
    this.selection.querySelector(".vp-sel-time").textContent = markerTimeText(segment);
    const label = this.selection.querySelector(".vp-sel-label");
    if (document.activeElement !== label) label.value = segment.label ?? "";
    label.disabled = !segment.canEdit;
    for (const radio of this.selection.querySelectorAll("input[name=vp-sel-color]")) {
      radio.checked = radio.value === (segment.color ?? "yellow");
      radio.disabled = !segment.canEdit;
    }
    this.selection.querySelector(".vp-sel-hint").textContent = segment.canEdit
      ? "Play plays only this · ±10s/±1s change its end · drag its edges · Del deletes"
      : `Added by ${segment.createdBy ?? "someone else"}: your role can't change it`;
  }

  setTool(tool) {
    if (tool === "cut" && !this.canAdd()) return;
    this.tool = tool;
    this.renderTools();
    this.canvas.style.cursor = this.toolCursor();
    this.dirty = true;
  }

  toggleSnap() {
    this.snap = !this.snap;
    try {
      localStorage.setItem(SNAP_KEY, this.snap ? "on" : "off");
    } catch {}
    this.renderTools();
    this.flash(this.snap ? "Snapping on" : "Snapping off");
  }

  toolCursor() {
    return { zoom: "zoom-in", cut: "crosshair", hand: this.pan ? "grabbing" : "grab" }[this.tool] ?? "";
  }

  renderTools() {
    for (const button of this.tools.querySelectorAll("[data-tool]")) {
      const active = button.dataset.tool === this.tool;
      button.classList.toggle("vp-tool-active", active);
      button.setAttribute("aria-pressed", String(active));
    }
    const snap = this.tools.querySelector("[data-act=snap]");
    snap.classList.toggle("vp-tool-active", this.snap);
    snap.setAttribute("aria-pressed", String(this.snap));
    const history = this.history();
    this.tools.querySelector("[data-act=undo]").disabled = !history?.undo.length;
    this.tools.querySelector("[data-act=redo]").disabled = !history?.redo.length;
  }

  // A short note under the bar (errors, hints)
  flash(text) {
    const message = this.selection.hidden ? this.form.querySelector(".vp-marker-message") : this.selection.querySelector(".vp-sel-message");
    if (this.selection.hidden && this.form.hidden) {
      this.tip.textContent = text;
      this.tip.hidden = false;
      clearTimeout(this.flashTimer);
      this.flashTimer = setTimeout(() => (this.tip.hidden = true), 2500);
      return;
    }
    message.textContent = text;
    clearTimeout(this.flashTimer);
    this.flashTimer = setTimeout(() => (message.textContent = ""), 4000);
  }

  // ±10s / ±1s with a segment selected: move its end. Presses close together become one change (one undo step).
  nudgeSelected(seconds) {
    const segment = this.selectedEditable();
    if (!segment) return;
    if (!this.pendingEdit) this.pendingEdit = { ref: markerRef(segment.id), before: markerData(segment) };
    const d = this.duration() || Infinity;
    segment.endSeconds = roundHundredths(Math.min(d, Math.max(segment.seconds + MIN_SEGMENT_SECONDS, segment.endSeconds + seconds)));
    this.afterLocalChange();
    clearTimeout(this.pendingEditTimer);
    this.pendingEditTimer = setTimeout(() => this.flushPendingEdit(), 600);
  }

  flushPendingEdit() {
    clearTimeout(this.pendingEditTimer);
    const pending = this.pendingEdit;
    this.pendingEdit = null;
    if (pending) this.commitEdit(pending.ref, pending.before);
  }

  dragEdgeTo(seconds) {
    if (this.drag.clip) return this.dragClipEdgeTo(seconds);
    const segment = this.markers.find((m) => m.id === this.drag.ref.id);
    if (!segment) return;
    const d = this.duration() || Infinity;
    const t = roundHundredths(Math.min(d, Math.max(0, this.snapTime(seconds, { markerId: segment.id }))));
    if (this.drag.edge === "start") segment.seconds = Math.min(t, roundHundredths(segment.endSeconds - MIN_SEGMENT_SECONDS));
    else segment.endSeconds = Math.max(t, roundHundredths(segment.seconds + MIN_SEGMENT_SECONDS));
    // Only the timeline is redrawn while dragging; the chips and the selection row update on release (no flicker)
    this.dirty = true;
  }

  // Title or color from the selection row
  editSelected(changes) {
    const segment = this.selectedEditable();
    if (!segment) return;
    this.flushPendingEdit();
    const before = markerData(segment);
    Object.assign(segment, changes);
    this.afterLocalChange();
    this.commitEdit(markerRef(segment.id), before);
  }

  // Saves a segment changed on screen (resize, nudge, title, color) as one undo step; puts it back if saving fails
  commitEdit(ref, before) {
    const segment = this.markers.find((m) => m.id === ref.id);
    if (!segment) return;
    const after = markerData(segment);
    if (sameMarkerData(before, after)) return;
    // A segment that now reaches past a reference card it was on stretches the card to cover it (one undo step with it)
    const trims = this.cardStretchesFor(before, after);
    this.run(async () => {
      try {
        await this.updateMarker(ref, after);
        for (const trim of trims) {
          try {
            await this.trimClip(trim.clipId, trim.after);
          } catch (error) {
            trims.splice(trims.indexOf(trim), 1);
            this.flash(`Couldn't stretch the card: ${error.message}`);
          }
        }
        this.record({ kind: "update", ref, before, after, trims });
      } catch (error) {
        const current = this.markers.find((m) => m.id === ref.id);
        if (current) Object.assign(current, before);
        this.afterLocalChange();
        this.flash(error.message);
      }
    });
  }

  deleteSelected() {
    const segment = this.selected();
    if (!segment) return false;
    if (!segment.canDelete) {
      this.flash("Your role only lets you delete segments you added.");
      return;
    }
    this.flushPendingEdit();
    this.removeWithHistory(segment);
  }

  // The cut tool: [start, at] keeps the segment, [at, end] is a new one with the same color ("Segment A" -> A1 + A2)
  async splitSegment(segment, at) {
    if (!segment.canEdit || !this.canAdd()) {
      this.flash("Your role can't cut this segment.");
      return;
    }
    const t = roundHundredths(at);
    if (t < segment.seconds + MIN_SEGMENT_SECONDS || t > segment.endSeconds - MIN_SEGMENT_SECONDS) {
      this.flash(`Cut at least ${MIN_SEGMENT_SECONDS} s from either end.`);
      return;
    }
    this.flushPendingEdit();
    const ref = markerRef(segment.id);
    const before = markerData(segment);
    const after = { ...before, endSeconds: t, label: splitLabel(before.label, 1) };
    const extra = { ...before, seconds: t, label: splitLabel(before.label, 2) };
    const extraRef = { id: null };
    try {
      await this.run(async () => {
        await this.updateMarker(ref, after);
        try {
          await this.createMarker(extraRef, extra);
        } catch (error) {
          await this.updateMarker(ref, before).catch(() => {});
          throw error;
        }
        this.record({ kind: "split", ref, before, after, extraRef, extra });
      });
    } catch (error) {
      this.flash(error.message);
    }
  }

  // ---- snapping ----

  // Every time on the timeline a dragged edge can stick to. Add new kinds of timeline items here.
  snapPoints(skip = {}) {
    const points = [];
    for (const marker of this.markers) {
      if (marker.id === skip.markerId) continue;
      points.push(marker.seconds);
      if (isSegment(marker)) points.push(marker.endSeconds);
    }
    for (const clip of this.clips) {
      if (clip.id === skip.clipId) continue;
      points.push(clip.start);
      if (clip.end != null) points.push(clip.end);
    }
    if (this.focus && skip.clipId !== this.focusEntryId) points.push(this.focus.start, this.focus.end);
    points.push(this.now());
    return points;
  }

  // The time a dragged edge lands on: the nearest snap point within a few pixels, else where the pointer is
  snapTime(seconds, skip) {
    this.snapAt = null;
    if (!this.snap) return seconds;
    const pxPerSecond = (this.canvas.clientWidth || 1) / (this.view.to - this.view.from || 1);
    let best = null;
    for (const point of this.snapPoints(skip)) {
      const distance = Math.abs(point - seconds) * pxPerSecond;
      if (distance <= SNAP_PX && (!best || distance < best.distance)) best = { point, distance };
    }
    if (!best) return seconds;
    this.snapAt = best.point;
    return best.point;
  }

  // ---- library cards (reference entries) on the timeline ----

  clipEnd(clip) {
    return clip.end ?? this.duration();
  }

  // The reviewed entry is one object on the timeline: its card on the purple lane always has the review's range (this.focus,
  // the vertical lines), so both move and snap together. Dragging either edge goes through the card (dragClipEdgeTo).
  syncFocusClip() {
    const clip = this.focus && this.clips.find((c) => c.id === this.focusEntryId);
    if (!clip) return;
    clip.start = this.focus.start;
    clip.end = clip.end == null && this.focus.end >= this.duration() - 0.01 ? null : this.focus.end;
  }

  // Reference VOD cards this member may re-cut by dragging (stored ones re-download, so they keep the review dialog)
  draggableClip(clip) {
    return clip.canEdit && clip.referenceOnly && clip.kind === "Vod";
  }

  // { clip, edge } when x is on an edge of a draggable card: on the purple lane, or anywhere on the track for the reviewed one
  clipEdgeAt(x, y) {
    if (this.tool !== "pointer" || y > BAR_TRACK.top + BAR_TRACK.height + 3) return null;
    const width = this.canvas.clientWidth;
    let best = null;
    for (const clip of this.clips) {
      if (!this.draggableClip(clip)) continue;
      if (y >= BAR_TRACK.top - 1 && clip.id !== this.focusEntryId) continue;
      for (const [edge, t] of [["start", clip.start], ["end", this.clipEnd(clip)]]) {
        const distance = Math.abs(this.timeToX(t, width) - x);
        if (distance <= EDGE_HIT_PX && (!best || distance < best.distance)) best = { clip, edge, distance };
      }
    }
    return best;
  }

  dragClipEdgeTo(seconds) {
    const clip = this.clips.find((c) => c.id === this.drag.clip.id);
    if (!clip) return;
    const d = this.duration() || Infinity;
    const t = roundHundredths(Math.min(d, Math.max(0, this.snapTime(seconds, { clipId: clip.id }))));
    if (this.drag.edge === "start") clip.start = Math.min(t, roundHundredths(this.clipEnd(clip) - MIN_CLIP_SECONDS));
    else clip.end = Math.max(t, roundHundredths(clip.start + MIN_CLIP_SECONDS));
    if (clip.id === this.focusEntryId && this.focus) this.focus = { ...this.focus, start: clip.start, end: this.clipEnd(clip) };
    this.dirty = true;
  }

  // The time of the edge being dragged (segment or card)
  draggedEdgeTime() {
    const drag = this.drag;
    if (!drag) return null;
    if (drag.clip) {
      const clip = this.clips.find((c) => c.id === drag.clip.id);
      return clip ? (drag.edge === "start" ? clip.start : this.clipEnd(clip)) : null;
    }
    const segment = this.markers.find((m) => m.id === drag.ref.id);
    return segment ? (drag.edge === "start" ? segment.seconds : segment.endSeconds) : null;
  }

  endDrag() {
    const drag = this.drag;
    this.drag = null;
    this.snapAt = null;
    this.dirty = true;
    if (!drag.clip) {
      this.afterLocalChange();
      return this.commitEdit(drag.ref, drag.before);
    }
    const clip = this.clips.find((c) => c.id === drag.clip.id);
    if (!clip) return;
    const after = { start: clip.start, end: clip.end };
    if (after.start === drag.before.start && after.end === drag.before.end) return;
    this.run(async () => {
      try {
        await this.trimClip(clip.id, after);
        this.record({ kind: "trim", clipId: clip.id, before: drag.before, after });
      } catch (error) {
        Object.assign(clip, drag.before);
        if (clip.id === this.focusEntryId && this.focus) this.focus = { ...this.focus, start: clip.start, end: this.clipEnd(clip) };
        this.dirty = true;
        this.flash(error.message);
      }
    });
  }

  // Reference cards the segment was on (before the change) that no longer cover all of it, and their stretched range
  cardStretchesFor(before, after) {
    if (before.endSeconds == null || after.endSeconds == null) return [];
    const trims = [];
    for (const clip of this.clips) {
      if (!this.draggableClip(clip) || clip.followsMarkers) continue; // a live markers card already follows its markers
      const end = clip.end ?? Infinity;
      const wasOn = before.seconds < end && before.endSeconds > clip.start;
      if (!wasOn || (after.seconds >= clip.start && after.endSeconds <= end)) continue;
      trims.push({
        clipId: clip.id,
        before: { start: clip.start, end: clip.end },
        after: { start: Math.min(clip.start, after.seconds), end: clip.end == null ? null : Math.max(clip.end, after.endSeconds) },
      });
    }
    return trims;
  }

  // Re-cuts a library entry (PUT /trim, as the review dialog's Save new cut does). range.followsMarkers (undoing a hand re-cut
  // of a live markers card) makes it follow its markers again, so the server may move it: its answer is what's shown.
  async trimClip(id, range) {
    const entry = await api("PUT", `/api/entries/${id}/trim`, {
      startTime: formatTrimValue(range.start),
      endTime: range.end == null ? "" : formatTrimValue(range.end),
      followMarkers: Boolean(range.followsMarkers),
    });
    const start = entry.startSeconds ?? range.start;
    const end = entry.endSeconds === undefined ? range.end : entry.endSeconds;
    this.clips = this.clips.map((c) => (c.id === id
      ? { ...c, start, end, referenceOnly: Boolean(entry.isReferenceOnly), followsMarkers: Boolean(entry.followsMarkers) }
      : c));
    if (id === this.focusEntryId && this.focus) this.focus = { ...this.focus, start, end: end ?? this.duration() };
    this.dirty = true;
    this.options.onEntryTrimmed?.(entry);
    this.notifyChanged({ entries: true });
    return entry;
  }

  history() {
    if (!this.vodId) return null;
    let history = markerHistory.get(this.vodId);
    if (!history) {
      history = { undo: [], redo: [] };
      markerHistory.set(this.vodId, history);
    }
    return history;
  }

  record(step) {
    const history = this.history();
    history.undo.push(step);
    if (history.undo.length > HISTORY_LIMIT) history.undo.shift();
    history.redo = [];
    this.renderTools();
  }

  undo() {
    return this.replay("undo", "redo", true);
  }

  redo() {
    return this.replay("redo", "undo", false);
  }

  replay(from, to, reverse) {
    this.flushPendingEdit();
    return this.run(async () => {
      const history = this.history();
      const step = history?.[from].pop();
      if (!step) return;
      try {
        await this.applyStep(step, reverse);
        history[to].push(step);
      } catch (error) {
        // Someone may have changed or deleted it meanwhile: this step can't be replayed, so it's dropped
        this.flash(`Couldn't ${reverse ? "undo" : "redo"} that: ${error.message}`);
      }
      this.renderTools();
    });
  }

  async applyStep(step, reverse) {
    switch (step.kind) {
      case "add": return reverse ? this.removeMarker(step.ref) : this.createMarker(step.ref, step.after);
      case "delete": return reverse ? this.createMarker(step.ref, step.before) : this.removeMarker(step.ref);
      case "update":
        await this.updateMarker(step.ref, reverse ? step.before : step.after);
        for (const trim of step.trims ?? []) await this.trimClip(trim.clipId, reverse ? trim.before : trim.after);
        return;
      case "trim": return this.trimClip(step.clipId, reverse ? step.before : step.after);
      case "split":
        if (reverse) {
          await this.removeMarker(step.extraRef);
          return this.updateMarker(step.ref, step.before);
        }
        await this.updateMarker(step.ref, step.after);
        return this.createMarker(step.extraRef, step.extra);
    }
  }

  // Marker requests one at a time, in order (fast presses, undo right after a change)
  run(task) {
    const next = this.work.then(task);
    this.work = next.catch(() => {});
    return next;
  }

  async createMarker(ref, data) {
    const marker = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/markers`, data);
    if (ref.id != null) markerRefs.delete(ref.id);
    ref.id = marker.id;
    markerRefs.set(marker.id, ref);
    this.markers = [...this.markers, marker].sort((a, b) => a.seconds - b.seconds);
    this.afterServerChange({ liveEntry: marker.liveEntry });
    return marker;
  }

  async updateMarker(ref, data) {
    const marker = await api("PUT", `/api/markers/${ref.id}`, data);
    this.markers = this.markers.map((m) => (m.id === marker.id ? marker : m)).sort((a, b) => a.seconds - b.seconds);
    this.afterServerChange({ liveEntry: marker.liveEntry });
    return marker;
  }

  async removeMarker(ref) {
    await api("DELETE", `/api/markers/${ref.id}`);
    this.markers = this.markers.filter((m) => m.id !== ref.id);
    if (this.selectedRef === ref) this.selectedRef = null;
    this.afterServerChange({ deleted: true });
  }

  afterLocalChange() {
    this.renderSelection();
    this.renderMarkers();
    this.dirty = true;
  }

  afterServerChange(change) {
    this.afterLocalChange();
    this.notifyChanged(change);
  }

  // The library, VOD cards and the review's trim bar show markers too
  notifyChanged(change = {}) {
    this.onMarkersChanged?.();
    if (typeof markersChanged === "function") markersChanged(this.vodId, change);
  }

  renderMarkers() {
    const list = this.el.querySelector(".vp-markers");
    list.replaceChildren();
    // Short-clip review: only what's on the trim bar below
    const w = this.compact ? this.focusWindow : null;
    const shown = w ? this.markers.filter((m) => (isSegment(m) ? m.endSeconds > w.from && m.seconds < w.to : m.seconds >= w.from && m.seconds <= w.to)) : this.markers;
    for (const marker of shown) {
      const chip = document.createElement("span");
      chip.className = "vp-chip";
      const go = document.createElement("button");
      go.type = "button";
      go.className = "vp-chip-go";
      go.title = markerTitle(marker);
      const dot = document.createElement("i");
      dot.style.background = markerColor(marker);
      if (isSegment(marker)) dot.className = "vp-chip-segment";
      const time = document.createElement("strong");
      time.textContent = isSegment(marker) ? `${formatTrimValue(marker.seconds)}–${formatTrimValue(marker.endSeconds)}` : formatTrimValue(marker.seconds);
      go.append(dot, time, marker.label ? ` ${marker.label}` : "");
      if (marker.createdBy) {
        const by = document.createElement("span");
        by.className = "muted";
        by.textContent = ` · ${marker.createdBy}`;
        go.append(by);
      }
      go.addEventListener("click", () => {
        // A segment's chip selects it (zooming to it); a marker's seeks to it
        if (isSegment(marker) && !this.compact) this.select(marker);
        else this.centerOn(marker.seconds);
        this.seek(marker.seconds);
      });
      if (isSegment(marker) && marker.id === this.selectedRef?.id) chip.classList.add("vp-chip-selected");
      chip.append(go);
      if (marker.canDelete) {
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "vp-chip-x";
        remove.textContent = "✕";
        remove.setAttribute("aria-label", isSegment(marker) ? "Delete segment" : "Delete marker");
        remove.addEventListener("click", () => this.deleteMarker(marker));
        chip.append(remove);
      }
      list.append(chip);
    }
  }

  // ---- input ----

  onKeyDown(event) {
    if (!this.backend) return;
    if (event.target.closest("input, select, textarea, .vp-marker-form")) return;
    const id = keybindFor("player", event);
    if (!id) return;
    // Buttons handle Space themselves
    if (id === "playPause" && event.key === " " && event.target.closest("button")) return;
    const actions = {
      playPause: () => this.togglePlay(),
      back10: () => this.seek(this.now() - 10),
      fwd10: () => this.seek(this.now() + 10),
      back5: () => this.seek(this.now() - 5),
      fwd5: () => this.seek(this.now() + 5),
      frameBack: () => this.step(-1),
      frameFwd: () => this.step(1),
      marker: () => this.openMarkerForm(),
      segment: () => this.toggleSegment(),
      fullscreen: () => this.toggleFullscreen(),
      zoomIn: () => this.act("zoomin"),
      zoomOut: () => this.act("zoomout"),
      fit: () => this.fit(),
      toolPointer: () => this.setTool("pointer"),
      toolCut: () => this.setTool("cut"),
      toolZoom: () => this.setTool("zoom"),
      toolHand: () => this.setTool("hand"),
      toggleSnap: () => this.toggleSnap(),
      undo: () => this.undo(),
      redo: () => this.redo(),
      deleteSelected: () => this.deleteSelected(),
      escape: () => {
        if (this.segmentStart != null) this.cancelSegment();
        else if (this.selected()) this.deselect();
        else if (this.tool !== "pointer") this.setTool("pointer");
        else return false;
      },
    };
    const handler = actions[id];
    if (!handler) return;
    if (handler() === false) return; // nothing to do: let the key through (e.g. Escape closing a dialog)
    event.preventDefault();
  }

  xToTime(x) {
    const width = this.canvas.clientWidth || 1;
    return this.view.from + (x / width) * (this.view.to - this.view.from);
  }

  timeToX(seconds, width) {
    const span = this.view.to - this.view.from || 1;
    return ((seconds - this.view.from) / span) * width;
  }

  localX(event, element) {
    return event.clientX - element.getBoundingClientRect().left;
  }

  markerAt(x) {
    const width = this.canvas.clientWidth;
    let best = null;
    for (const marker of this.markers) {
      if (isSegment(marker)) continue;
      const distance = Math.abs(this.timeToX(marker.seconds, width) - x);
      if (distance <= MARKER_HIT_PX && (!best || distance < best.distance)) best = { marker, distance };
    }
    return best?.marker ?? null;
  }

  // The smallest segment under a time (segments can overlap), when the pointer is on the track
  segmentAt(seconds, y) {
    if (y < BAR_TRACK.top - 4 || y > BAR_TRACK.top + BAR_TRACK.height + 4) return null;
    let best = null;
    for (const marker of this.markers) {
      if (!isSegment(marker) || seconds < marker.seconds || seconds > marker.endSeconds) continue;
      if (!best || marker.endSeconds - marker.seconds < best.endSeconds - best.seconds) best = marker;
    }
    return best;
  }

  // "start"/"end" when x is on an edge of the selected segment (only it can be resized, so a stray click can't)
  edgeAt(x, y) {
    const segment = this.selectedEditable();
    if (!segment || y < BAR_TRACK.top - 6 || y > BAR_TRACK.top + BAR_TRACK.height + 6) return null;
    const width = this.canvas.clientWidth;
    const a = this.timeToX(segment.seconds, width);
    const b = this.timeToX(segment.endSeconds, width);
    const da = Math.abs(x - a);
    const db = Math.abs(x - b);
    if (Math.min(da, db) > EDGE_HIT_PX) return null;
    return da < db ? "start" : "end";
  }

  onPointerDown(event) {
    if (!this.video) return;
    this.wrap.focus({ preventScroll: true });
    const x = this.localX(event, this.canvas);
    const y = event.clientY - this.canvas.getBoundingClientRect().top;
    const time = this.xToTime(x);

    if (this.tool === "zoom") {
      if (event.button === 0) this.zoomBy(0.5, time);
      else if (event.button === 2) this.zoomBy(2, time);
      return;
    }
    if (event.button !== 0) return;

    // Hand: drag the view (nothing else changes: no seek, no selection)
    if (this.tool === "hand") {
      this.pan = { x, from: this.view.from, to: this.view.to };
      this.canvas.setPointerCapture(event.pointerId);
      this.canvas.style.cursor = this.toolCursor();
      return;
    }

    if (this.tool === "cut") {
      const segment = this.segmentAt(time, y);
      if (segment) this.splitSegment(segment, time);
      else this.flash("Click on a segment to cut it there.");
      return;
    }

    // Pointer: resize the selected segment by an edge
    const edge = this.edgeAt(x, y);
    if (edge) {
      const segment = this.selected();
      this.flushPendingEdit();
      this.drag = { ref: markerRef(segment.id), edge, before: markerData(segment) };
      this.canvas.setPointerCapture(event.pointerId);
      return;
    }
    // A reference card's start/end (purple lane, or the reviewed entry's edges)
    const clipEdge = this.clipEdgeAt(x, y);
    if (clipEdge) {
      this.flushPendingEdit();
      const { start, end, followsMarkers } = clipEdge.clip;
      this.drag = { clip: { id: clipEdge.clip.id }, edge: clipEdge.edge, before: { start, end, followsMarkers: Boolean(followsMarkers) } };
      this.canvas.setPointerCapture(event.pointerId);
      return;
    }

    const marker = this.markerAt(x);
    if (marker) {
      this.seek(marker.seconds);
      return;
    }
    if (y < BAR_TRACK.top) {
      const clip = this.clipAt(time);
      if (clip) {
        this.seek(clip.start);
        return;
      }
    }
    // A click on a segment selects it (and zooms to it); inside the selected one it seeks; elsewhere it unselects
    const segment = this.segmentAt(time, y);
    if (segment && segment.id !== this.selectedRef?.id) {
      this.select(segment);
      return;
    }
    if (!segment && this.selectedRef) this.deselect();
    this.scrubbing = true;
    this.canvas.setPointerCapture(event.pointerId);
    this.seek(time);
  }

  onPointerMove(event) {
    const x = this.localX(event, this.canvas);
    const y = event.clientY - this.canvas.getBoundingClientRect().top;
    this.hoverX = x;
    if (this.pan) {
      if (this.zoomed) {
        const span = this.pan.to - this.pan.from;
        const delta = ((this.pan.x - x) / (this.canvas.clientWidth || 1)) * span;
        this.autoView = false;
        this.manualPan = true;
        this.view = { from: this.pan.from + delta, to: this.pan.to + delta };
        this.clampView();
      }
    } else if (this.drag) {
      this.dragEdgeTo(this.xToTime(x));
    } else if (this.scrubbing) {
      this.seek(this.xToTime(x));
    }
    this.canvas.style.cursor = this.drag || (this.tool === "pointer" && (this.edgeAt(x, y) || this.clipEdgeAt(x, y))) ? "ew-resize" : this.toolCursor();
    this.renderTip(x, y);
    this.dirty = true;
  }

  onPointerUp(event) {
    if (this.pan) {
      this.pan = null;
      this.canvas.releasePointerCapture?.(event.pointerId);
      this.canvas.style.cursor = this.toolCursor();
      return;
    }
    if (this.drag) {
      this.canvas.releasePointerCapture?.(event.pointerId);
      this.endDrag();
      return;
    }
    if (!this.scrubbing) return;
    this.scrubbing = false;
    this.canvas.releasePointerCapture?.(event.pointerId);
  }

  onWheel(event) {
    if (!this.video) return;
    event.preventDefault();
    const span = this.view.to - this.view.from;
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
      // Pan
      if (!this.zoomed) return;
      const delta = (event.shiftKey ? event.deltaY : event.deltaX) / (this.canvas.clientWidth || 1) * span;
      this.autoView = false;
      this.manualPan = true;
      this.view = { from: this.view.from + delta, to: this.view.to + delta };
      this.clampView();
    } else {
      this.zoomBy(event.deltaY < 0 ? 0.8 : 1.25, this.xToTime(this.localX(event, this.canvas)));
    }
    this.dirty = true;
  }

  overviewTime(event) {
    const d = this.duration();
    if (!d) return null;
    return (this.localX(event, this.overview) / (this.overview.clientWidth || 1)) * d;
  }

  clipAt(seconds) {
    return this.clips.find((clip) => seconds >= clip.start && seconds <= (clip.end ?? this.duration())) ?? null;
  }

  renderTip(x, y) {
    const time = this.xToTime(x);
    if (this.drag) {
      // While dragging: just where the edge is, so the tip doesn't jump in size
      this.tip.textContent = `${this.drag.edge === "start" ? "Start" : "End"} ${formatTrimValue(this.draggedEdgeTime() ?? time)}${this.snapAt != null ? " (snapped)" : ""}`;
      this.tip.hidden = false;
      const tipWidth = this.tip.offsetWidth;
      this.tip.style.left = `${Math.min(Math.max(0, x - tipWidth / 2), Math.max(0, this.canvas.clientWidth - tipWidth))}px`;
      return;
    }
    const lines = [formatTrimValue(Math.max(0, time))];
    const marker = this.markerAt(x);
    if (marker) {
      lines.push(`Marker${marker.label ? `: ${marker.label}` : ""}`);
      if (marker.createdBy) lines.push(`  added by ${marker.createdBy}`);
    }
    for (const segment of this.markers) {
      if (isSegment(segment) && time >= segment.seconds && time <= segment.endSeconds) {
        lines.push(`${segment.label || "Segment"}: ${markerTimeText(segment)}`);
        if (segment.createdBy) lines.push(`  added by ${segment.createdBy}`);
      }
    }
    if (this.tool === "cut" && this.segmentAt(time, y)) lines.push("Click to cut here");
    const clipEdge = this.drag ? null : this.clipEdgeAt(x, y);
    if (clipEdge) lines.push(`Drag to move the card's ${clipEdge.edge}: ${clipEdge.clip.title}`);
    if (this.tool === "hand") lines.push(this.zoomed ? "Drag to scroll the timeline" : "Zoom in to scroll the timeline");
    const range = this.ranges.find((r) => time >= r.start && time < r.end);
    if (range) lines.push(range.clean ? "Muted by Twitch: plays saved clean audio" : "Muted by Twitch");
    if (this.focus && time >= this.focus.start && time <= this.focus.end) lines.push("This clip");
    const clip = y < BAR_TRACK.top + BAR_TRACK.height ? this.clipAt(time) : null;
    if (clip) lines.push(`Clip: ${clip.title}`);
    this.tip.textContent = lines.join("\n");
    this.tip.hidden = false;
    const width = this.canvas.clientWidth;
    const tipWidth = this.tip.offsetWidth;
    this.tip.style.left = `${Math.min(Math.max(0, x - tipWidth / 2), Math.max(0, width - tipWidth))}px`;
  }

  // ---- drawing ----

  renderButtons() {
    const video = this.video;
    if (!video) return;
    const play = this.el.querySelector(".vp-play");
    play.textContent = video.paused ? "▶" : "❚❚";
    play.setAttribute("aria-label", video.paused ? "Play" : "Pause");
    const silent = video.muted || video.volume === 0;
    const mute = this.el.querySelector(".vp-mute");
    mute.textContent = silent ? "🔇" : video.volume < 0.5 ? "🔉" : "🔊";
    mute.setAttribute("aria-label", silent ? "Unmute" : "Mute");
    this.el.querySelector(".vp-volume").value = String(Math.round((video.muted ? 0 : video.volume) * 100));
    this.dirty = true;
  }

  update() {
    const video = this.video;
    if (!video) return;
    const now = video.currentTime;
    const selected = this.selected();
    // Playing a selected segment that was just made longer or shorter: stop at its new end
    if (this.playUntil != null && selected) this.playUntil = selected.endSeconds;
    if (this.playUntil != null && !video.paused && now >= this.playUntil) {
      video.pause();
      this.video.currentTime = this.playUntil;
    }
    // An automatic view keeps the whole selected segment on screen, also after it was made longer (nudge, undo, someone
    // else's change) and when the playhead leaves the view: it zooms out to show both instead of panning past the segment
    const fitted = this.autoView && this.zoomed && !this.drag && !this.scrubbing && !this.pan ? selected : null;
    if (fitted) {
      const outside = !video.paused && (now > this.view.to || now < this.view.from);
      if (outside || fitted.seconds < this.view.from || fitted.endSeconds > this.view.to)
        this.zoomTo(this.segmentView(fitted, outside ? now : null));
    } else if (this.zoomed && !this.scrubbing && !this.pan && !video.paused) {
      // Follow the playhead while zoomed in. After the user scrolled the view away (hand, Shift+wheel) it stays put until the
      // playhead comes back into it or the user seeks.
      const span = this.view.to - this.view.from;
      if (this.manualPan) {
        if (now >= this.view.from && now <= this.view.to - span * 0.02) this.manualPan = false;
      } else if (now > this.view.to - span * 0.02 || now < this.view.from) {
        this.view = { from: now - span * 0.1, to: now + span * 0.9 };
        this.clampView();
        this.dirty = true;
      }
    }
    if (!this.dirty && video.paused) return;
    this.dirty = false;
    this.el.querySelector(".vp-now").textContent = formatClock(now);
    const d = this.duration();
    const span = this.view.to - this.view.from;
    this.el.querySelector(".vp-zoom-label").textContent = !this.zoomed || !d ? "All" : span >= 3600 ? `${(span / 3600).toFixed(1)} h` : span >= 60 ? `${Math.round(span / 60)} min` : `${Math.round(span)} s`;
    this.overview.hidden = !this.zoomed || this.compact;
    if (this.compact) return;
    this.drawTimeline(now);
    if (this.zoomed) this.drawOverview(now);
  }

  colors() {
    const style = getComputedStyle(this.el);
    const get = (name) => style.getPropertyValue(name).trim();
    return {
      track: get("--vp-track"), buffered: get("--vp-buffered"), played: get("--vp-played"), muted: get("--vp-muted"),
      clean: get("--vp-clean"), clip: get("--vp-clip"), playhead: get("--vp-playhead"), text: get("--vp-text"), window: get("--vp-window"),
    };
  }

  setupCanvas(canvas, cssHeight) {
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(cssHeight * ratio)) {
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(cssHeight * ratio);
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, cssHeight);
    return { ctx, width };
  }

  drawTimeline(now) {
    const { ctx, width } = this.setupCanvas(this.canvas, BAR_TRACK.height_total);
    if (!width) return;
    const c = this.colors();
    const { top, height } = BAR_TRACK;
    const x = (t) => this.timeToX(t, width);
    const span = (from, to) => [Math.max(0, x(from)), Math.min(width, x(to))];

    ctx.fillStyle = c.track;
    ctx.fillRect(0, top, width, height);

    // Buffered, then played
    const buffered = this.video.buffered;
    ctx.fillStyle = c.buffered;
    for (let i = 0; i < buffered.length; i++) {
      const [a, b] = span(buffered.start(i), buffered.end(i));
      if (b > a) ctx.fillRect(a, top, b - a, height);
    }
    ctx.fillStyle = c.played;
    const playedTo = Math.min(width, Math.max(0, x(now)));
    ctx.fillRect(0, top + height - 4, playedTo, 4);

    // The entry being reviewed
    if (this.focus) {
      const [a, b] = span(this.focus.start, this.focus.end);
      if (b > 0 && a < width) {
        ctx.save();
        ctx.fillStyle = c.window;
        ctx.globalAlpha = 0.22;
        ctx.fillRect(a, top, Math.max(2, b - a), height);
        ctx.globalAlpha = 1;
        ctx.fillRect(Math.round(a) - 1, top - 3, 3, height + 6);
        ctx.fillRect(Math.round(b) - 1, top - 3, 3, height + 6);
        ctx.restore();
      }
    }

    // Muted parts (bottom two thirds of the track, so the played line stays visible), half see-through
    ctx.globalAlpha = 0.5;
    for (const range of this.ranges) {
      const [a, b] = span(range.start, range.end);
      if (b <= 0 || a >= width) continue;
      ctx.fillStyle = range.clean ? c.clean : c.muted;
      ctx.fillRect(a, top + 4, Math.max(1, b - a), height - 10);
    }
    ctx.globalAlpha = 1;

    // Segments, and the one being marked (from where S was pressed to the playhead)
    const selected = this.selected();
    for (const segment of this.markers) {
      if (!isSegment(segment) || segment === selected) continue;
      const a = x(segment.seconds);
      const b = x(segment.endSeconds);
      if (b < -4 || a > width + 4) continue;
      drawSegmentBox(ctx, a, b, top, height, markerColor(segment));
    }
    // The selected one on top, outlined, with grips on its edges when it can be resized
    let deleteAt = null;
    if (selected) {
      const a = x(selected.seconds);
      const b = x(selected.endSeconds);
      if (b >= -4 && a <= width + 4) {
        ctx.save();
        ctx.strokeStyle = c.text;
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 2]);
        ctx.strokeRect(a - 2.5, top - 2.5, Math.max(2, b - a) + 5, height + 5);
        ctx.restore();
        drawSegmentBox(ctx, a, b, top, height, markerColor(selected));
        if (selected.canEdit) {
          ctx.fillStyle = c.playhead;
          for (const edge of [a, b]) {
            ctx.fillRect(Math.round(edge) - 2, top + height / 2 - 7, 4, 14);
          }
        }
        if (selected.canDelete) deleteAt = b + 34 <= width ? b + 14 : a - 34 >= 0 ? a - 32 : null;
      }
    }
    this.selX.hidden = deleteAt == null;
    if (deleteAt != null) this.selX.style.left = `${deleteAt}px`;
    if (this.segmentStart != null) {
      const a = x(Math.min(this.segmentStart, now));
      const b = x(Math.max(this.segmentStart, now));
      drawSegmentBox(ctx, a, b, top, height, c.text, true);
    }

    // Library clips
    ctx.fillStyle = c.clip;
    for (const clip of this.clips) {
      const [a, b] = span(clip.start, clip.end ?? this.duration());
      if (b <= 0 || a >= width) continue;
      ctx.fillRect(a, BAR_TRACK.clipTop, Math.max(2, b - a), BAR_TRACK.clipHeight);
    }

    // Time ticks
    const visible = this.view.to - this.view.from;
    const step = BAR_TICK_STEPS.find((s) => visible / s <= Math.max(2, width / 90)) ?? BAR_TICK_STEPS[BAR_TICK_STEPS.length - 1];
    ctx.fillStyle = c.text;
    ctx.strokeStyle = c.text;
    ctx.font = "11px system-ui, sans-serif";
    ctx.textBaseline = "top";
    ctx.globalAlpha = 0.75;
    for (let t = Math.ceil(this.view.from / step) * step; t <= this.view.to; t += step) {
      const tx = Math.round(x(t)) + 0.5;
      ctx.beginPath();
      ctx.moveTo(tx, top + height);
      ctx.lineTo(tx, top + height + 3);
      ctx.stroke();
      const label = step < 60 ? formatDuration(t) : formatDuration(t).replace(/:00$/, "");
      const w = ctx.measureText(label).width;
      if (tx + w / 2 < width && tx - w / 2 > 0) ctx.fillText(label, tx - w / 2, BAR_TRACK.labelTop + 1);
    }
    ctx.globalAlpha = 1;

    // Markers
    for (const marker of this.markers) {
      if (isSegment(marker)) continue;
      const mx = Math.round(x(marker.seconds)) + 0.5;
      if (mx < -4 || mx > width + 4) continue;
      const color = markerColor(marker);
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(mx, 0);
      ctx.lineTo(mx, top + height);
      ctx.stroke();
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.moveTo(mx - 5, 0);
      ctx.lineTo(mx + 5, 0);
      ctx.lineTo(mx, 7);
      ctx.closePath();
      ctx.fill();
    }
    ctx.lineWidth = 1;

    // Hover line
    if (this.hoverX != null && !this.scrubbing) {
      ctx.fillStyle = c.text;
      ctx.globalAlpha = 0.5;
      ctx.fillRect(Math.round(this.hoverX), top, 1, height);
      ctx.globalAlpha = 1;
    }

    // Playhead
    const px = x(now);
    if (px >= -2 && px <= width + 2) {
      ctx.fillStyle = c.playhead;
      ctx.fillRect(Math.round(px) - 1, 0, 2, top + height);
      ctx.beginPath();
      ctx.arc(px, top + height / 2, 5, 0, Math.PI * 2);
      ctx.fill();
    }

    // Where a dragged edge is stuck (snapping)
    if (this.drag && this.snapAt != null) {
      const sx = Math.round(x(this.snapAt)) + 0.5;
      ctx.save();
      ctx.strokeStyle = c.text;
      ctx.lineWidth = 1;
      ctx.setLineDash([2, 2]);
      ctx.beginPath();
      ctx.moveTo(sx, 0);
      ctx.lineTo(sx, top + height + 4);
      ctx.stroke();
      ctx.restore();
    }
  }

  drawOverview(now) {
    const { ctx, width } = this.setupCanvas(this.overview, 12);
    const d = this.duration();
    if (!width || !d) return;
    const c = this.colors();
    const x = (t) => (t / d) * width;
    ctx.fillStyle = c.track;
    ctx.fillRect(0, 2, width, 8);
    ctx.globalAlpha = 0.5;
    for (const range of this.ranges) {
      ctx.fillStyle = range.clean ? c.clean : c.muted;
      ctx.fillRect(x(range.start), 2, Math.max(1, x(range.end) - x(range.start)), 8);
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = c.clip;
    for (const clip of this.clips) ctx.fillRect(x(clip.start), 0, Math.max(1, x(clip.end ?? d) - x(clip.start)), 2);
    for (const marker of this.markers) {
      ctx.fillStyle = markerColor(marker);
      if (isSegment(marker)) {
        ctx.globalAlpha = 0.6;
        ctx.fillRect(x(marker.seconds), 3, Math.max(2, x(marker.endSeconds) - x(marker.seconds)), 6);
        ctx.globalAlpha = 1;
      } else {
        ctx.fillRect(Math.round(x(marker.seconds)), 0, 2, 12);
      }
    }
    ctx.strokeStyle = c.window;
    ctx.lineWidth = 2;
    ctx.strokeRect(x(this.view.from) + 1, 1, Math.max(3, x(this.view.to) - x(this.view.from) - 2), 10);
    ctx.lineWidth = 1;
    ctx.fillStyle = c.playhead;
    ctx.fillRect(Math.round(x(now)) - 1, 0, 2, 12);
  }
}
