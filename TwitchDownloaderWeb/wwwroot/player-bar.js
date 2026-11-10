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
// Keys while the player has focus: Space/K play/pause, J/L ∓10 s, ←/→ ∓5 s, ,/. one frame back/forward, M add a marker,
// S start a segment here / end it here (Esc cancels), +/− zoom, 0 fit, F fullscreen.
//
// Uses the page's helpers from app.js ($, api, can, me, formatTrimValue, formatDuration), which load after this file but
// before anything here runs.

const BAR_MIN_SPAN_SECONDS = 10;
const BAR_TRACK = { clipTop: 0, clipHeight: 7, top: 10, height: 22, labelTop: 34, height_total: 48 };
const BAR_TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600];
const MARKER_COLORS = { yellow: "#f2c230", blue: "#3d8bfd", pink: "#ff5fa2", orange: "#ff8a3d" };
const MARKER_HIT_PX = 6;
const MIN_SEGMENT_SECONDS = 0.5;

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
        <button type="button" class="vp-btn" data-act="back10" title="Back 10 s (J)">−10s</button>
        <button type="button" class="vp-btn" data-act="frameback" title="One frame back (,)">‹</button>
        <button type="button" class="vp-btn" data-act="framefwd" title="One frame forward (.)">›</button>
        <button type="button" class="vp-btn" data-act="fwd10" title="Forward 10 s (L)">+10s</button>
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
    this.canvas.addEventListener("pointercancel", () => (this.scrubbing = false));
    this.canvas.addEventListener("pointerleave", () => {
      this.hoverX = null;
      this.tip.hidden = true;
      this.dirty = true;
    });
    this.canvas.addEventListener("wheel", (event) => this.onWheel(event), { passive: false });
    this.overview.addEventListener("pointerdown", (event) => {
      const time = this.overviewTime(event);
      if (time == null) return;
      this.centerOn(time);
      this.seek(time);
    });

    this.onKey = (event) => this.onKeyDown(event);
    this.wrap.addEventListener("keydown", this.onKey);
    this.onResize = () => (this.dirty = true);
    window.addEventListener("resize", this.onResize);
    this.wrap.addEventListener("fullscreenchange", () => (this.dirty = true));
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
    this.compact = Boolean(this.options.compact);
    this.focus = this.options.focus ?? null;
    this.focusWindow = this.options.focusWindow ?? null;
    this.pendingZoom = this.options.zoom ?? null;
    this.applyLayout();
    this.closeMarkerForm();
    this.cancelSegment();
    this.el.hidden = false;
    this.wrap.classList.add("has-bar");
    const canAdd = can("AddEntries") && !me?.support;
    this.el.querySelector(".vp-add-marker").hidden = !canAdd;
    this.el.querySelector(".vp-add-segment").hidden = !canAdd;

    backend.onDetails = (details) => this.readPlaylist(details);
    if (backend.details) this.readPlaylist(backend.details);

    const video = this.video;
    this.videoListeners = {
      play: () => this.renderButtons(),
      pause: () => this.renderButtons(),
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
    this.wrap.classList.remove("has-bar");
    this.tip.hidden = true;
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
    } catch (error) {
      console.warn("Couldn't load the timeline's clips and markers:", error);
    }
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
    const windowChanged = this.focusWindow?.from !== focusWindow?.from || this.focusWindow?.to !== focusWindow?.to;
    this.focusWindow = focusWindow;
    if (windowChanged && this.compact) this.renderMarkers();
    this.dirty = true;
  }

  zoomTo(range) {
    if (!range) return;
    const d = this.duration();
    if (!d) {
      this.pendingZoom = range;
      return;
    }
    const span = Math.max(BAR_MIN_SPAN_SECONDS, range.to - range.from);
    if (span >= d) return this.fit();
    this.view = { from: range.from, to: range.from + span };
    this.zoomed = true;
    this.clampView();
    this.dirty = true;
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
      case "back10": return this.seek(this.now() - 10);
      case "fwd10": return this.seek(this.now() + 10);
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
    const d = this.duration();
    this.video.currentTime = Math.max(0, d ? Math.min(seconds, d - 0.05) : seconds);
    this.dirty = true;
  }

  togglePlay() {
    if (!this.video) return;
    if (this.video.paused) this.video.play().catch(() => {});
    else this.video.pause();
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
    this.fitIfNeeded();
  }

  zoomBy(factor, anchor) {
    const d = this.duration();
    if (!d) return;
    const span = this.view.to - this.view.from;
    const next = Math.min(d, Math.max(BAR_MIN_SPAN_SECONDS, span * factor));
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
    try {
      const marker = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/markers`,
        { seconds: times.start, endSeconds: times.end, label, color });
      this.markers = [...this.markers, marker].sort((a, b) => a.seconds - b.seconds);
      this.closeMarkerForm();
      this.renderMarkers();
      this.dirty = true;
      this.wrap.focus({ preventScroll: true });
      this.notifyChanged({ liveEntry: marker.liveEntry });
    } catch (error) {
      message.textContent = error.message;
    }
  }

  async deleteMarker(marker) {
    const what = isSegment(marker) ? "segment" : "marker";
    if (!confirm(`Delete the ${what} ${isSegment(marker) ? "from" : "at"} ${isSegment(marker)
      ? `${formatTrimValue(marker.seconds)} to ${formatTrimValue(marker.endSeconds)}` : formatTrimValue(marker.seconds)}${marker.label ? ` ("${marker.label}")` : ""}?`)) return;
    try {
      await api("DELETE", `/api/markers/${marker.id}`);
      this.markers = this.markers.filter((x) => x.id !== marker.id);
      this.renderMarkers();
      this.dirty = true;
      this.notifyChanged({ deleted: true });
    } catch (error) {
      alert(error.message);
    }
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
        this.centerOn(marker.seconds);
        this.seek(marker.seconds);
      });
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
    if (!this.backend || event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.target.closest("input, select, textarea, .vp-marker-form")) return;
    if (event.key === "Escape" && this.segmentStart != null) {
      event.preventDefault();
      this.cancelSegment();
      return;
    }
    const keys = {
      " ": () => this.togglePlay(), k: () => this.togglePlay(), K: () => this.togglePlay(),
      j: () => this.seek(this.now() - 10), J: () => this.seek(this.now() - 10),
      l: () => this.seek(this.now() + 10), L: () => this.seek(this.now() + 10),
      ArrowLeft: () => this.seek(this.now() - 5), ArrowRight: () => this.seek(this.now() + 5),
      ",": () => this.step(-1), ".": () => this.step(1),
      m: () => this.openMarkerForm(), M: () => this.openMarkerForm(),
      s: () => this.toggleSegment(), S: () => this.toggleSegment(),
      f: () => this.toggleFullscreen(), F: () => this.toggleFullscreen(),
      "+": () => this.act("zoomin"), "=": () => this.act("zoomin"), "-": () => this.act("zoomout"), "0": () => this.fit(),
    };
    const handler = keys[event.key];
    if (!handler) return;
    // Buttons handle Space themselves
    if (event.key === " " && event.target.closest("button")) return;
    event.preventDefault();
    handler();
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

  onPointerDown(event) {
    if (!this.video || event.button !== 0) return;
    this.wrap.focus({ preventScroll: true });
    const x = this.localX(event, this.canvas);
    const marker = this.markerAt(x);
    if (marker) {
      this.seek(marker.seconds);
      return;
    }
    const y = event.clientY - this.canvas.getBoundingClientRect().top;
    if (y < BAR_TRACK.top) {
      const clip = this.clipAt(this.xToTime(x));
      if (clip) {
        this.seek(clip.start);
        return;
      }
    }
    this.scrubbing = true;
    this.canvas.setPointerCapture(event.pointerId);
    this.seek(this.xToTime(x));
  }

  onPointerMove(event) {
    const x = this.localX(event, this.canvas);
    this.hoverX = x;
    if (this.scrubbing) this.seek(this.xToTime(x));
    this.renderTip(x, event.clientY - this.canvas.getBoundingClientRect().top);
    this.dirty = true;
  }

  onPointerUp(event) {
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
    const lines = [formatTrimValue(Math.max(0, time))];
    const marker = this.markerAt(x);
    if (marker) lines.push(`Marker${marker.label ? `: ${marker.label}` : ""}${marker.createdBy ? ` (${marker.createdBy})` : ""}`);
    for (const segment of this.markers) {
      if (isSegment(segment) && time >= segment.seconds && time <= segment.endSeconds) {
        lines.push(`${segment.label || "Segment"}: ${markerTimeText(segment)}${segment.createdBy ? ` (${segment.createdBy})` : ""}`);
      }
    }
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
    // Follow the playhead while zoomed in
    if (this.zoomed && !this.scrubbing && !video.paused) {
      const span = this.view.to - this.view.from;
      if (now > this.view.to - span * 0.02 || now < this.view.from) {
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
    for (const segment of this.markers) {
      if (!isSegment(segment)) continue;
      const a = x(segment.seconds);
      const b = x(segment.endSeconds);
      if (b < -4 || a > width + 4) continue;
      drawSegmentBox(ctx, a, b, top, height, markerColor(segment));
    }
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
