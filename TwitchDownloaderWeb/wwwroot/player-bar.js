"use strict";

// ---------- the app player's own control bar ----------
//
// Replaces the browser's video controls for the app player (hls.js) so the timeline can show what matters to editors:
//  - parts Twitch muted (red) and parts that play with saved clean audio instead (green), read from the playlist the
//    player loaded (a muted segment's URL ends in "-muted.ts"; the server points ones with saved audio at ".../clean/")
//  - the team's library entries cut from this VOD (purple lane above the track)
//  - the team's markers (GET/POST /api/vods/{id}/timeline, /markers), shared with everyone in the team
// The timeline zooms (wheel, +/−, Fit) so a 24-hour VOD can still be scrubbed to the second; a strip above it shows the
// whole VOD and the zoomed window. The Twitch embed fallback keeps Twitch's own controls; the bar is hidden then.
//
// Keys while the player has focus: Space/K play/pause, J/L ∓10 s, ←/→ ∓5 s, ,/. one frame back/forward, M add a marker,
// +/− zoom, 0 fit, F fullscreen.
//
// Uses the page's helpers from app.js ($, api, can, me, formatTrimValue, formatDuration), which load after this file but
// before anything here runs.

const BAR_MIN_SPAN_SECONDS = 10;
const BAR_TRACK = { clipTop: 0, clipHeight: 7, top: 10, height: 22, labelTop: 34, height_total: 48 };
const BAR_TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600];
const MARKER_COLORS = { yellow: "#f2c230", blue: "#3d8bfd", pink: "#ff5fa2", orange: "#ff8a3d" };
const MARKER_HIT_PX = 6;

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
    this.pendingMarker = null; // seconds of the marker being added
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
        <button type="button" class="vp-btn" data-act="fullscreen" aria-label="Fullscreen" title="Fullscreen (F)">⛶</button>
      </div>
      <div class="vp-marker-form" hidden>
        <span>Marker at <strong class="vp-marker-time"></strong></span>
        <input class="vp-marker-label" maxlength="200" placeholder="What happens here? (optional)" autocomplete="off">
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
      </div>`;

    this.overview = this.el.querySelector(".vp-overview");
    this.canvas = this.el.querySelector(".vp-timeline");
    this.tip = this.el.querySelector(".vp-tip");
    this.form = this.el.querySelector(".vp-marker-form");

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
    this.closeMarkerForm();
    this.el.hidden = false;
    this.wrap.classList.add("has-bar");
    this.el.querySelector(".vp-add-marker").hidden = !can("AddEntries") || Boolean(me?.support);

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

  fitIfNeeded() {
    const d = this.duration();
    if (!d) return;
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
      case "fullscreen": return this.toggleFullscreen();
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

  openMarkerForm() {
    if (!this.video || this.el.querySelector(".vp-add-marker").hidden) return;
    this.video.pause();
    this.pendingMarker = this.now();
    this.form.querySelector(".vp-marker-time").textContent = formatTrimValue(this.pendingMarker);
    this.form.querySelector(".vp-marker-message").textContent = "";
    this.form.hidden = false;
    this.form.querySelector(".vp-marker-label").value = "";
    this.form.querySelector(".vp-marker-label").focus();
  }

  closeMarkerForm() {
    this.pendingMarker = null;
    this.form.hidden = true;
  }

  async saveMarker() {
    if (this.pendingMarker == null) return;
    const label = this.form.querySelector(".vp-marker-label").value.trim();
    const color = this.form.querySelector("input[name=vp-color]:checked")?.value ?? "yellow";
    const message = this.form.querySelector(".vp-marker-message");
    message.textContent = "Saving…";
    try {
      const marker = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/markers`, { seconds: this.pendingMarker, label, color });
      this.markers = [...this.markers, marker].sort((a, b) => a.seconds - b.seconds);
      this.closeMarkerForm();
      this.renderMarkers();
      this.dirty = true;
      this.wrap.focus({ preventScroll: true });
    } catch (error) {
      message.textContent = error.message;
    }
  }

  async deleteMarker(marker) {
    if (!confirm(`Delete the marker at ${formatTrimValue(marker.seconds)}${marker.label ? ` ("${marker.label}")` : ""}?`)) return;
    try {
      await api("DELETE", `/api/markers/${marker.id}`);
      this.markers = this.markers.filter((x) => x.id !== marker.id);
      this.renderMarkers();
      this.dirty = true;
    } catch (error) {
      alert(error.message);
    }
  }

  renderMarkers() {
    const list = this.el.querySelector(".vp-markers");
    list.replaceChildren();
    for (const marker of this.markers) {
      const chip = document.createElement("span");
      chip.className = "vp-chip";
      const go = document.createElement("button");
      go.type = "button";
      go.className = "vp-chip-go";
      go.title = `Go to this marker${marker.createdBy ? ` (added by ${marker.createdBy})` : ""}`;
      const dot = document.createElement("i");
      dot.style.background = MARKER_COLORS[marker.color] ?? MARKER_COLORS.yellow;
      const time = document.createElement("strong");
      time.textContent = formatTrimValue(marker.seconds);
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
        remove.setAttribute("aria-label", "Delete marker");
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
    const keys = {
      " ": () => this.togglePlay(), k: () => this.togglePlay(), K: () => this.togglePlay(),
      j: () => this.seek(this.now() - 10), J: () => this.seek(this.now() - 10),
      l: () => this.seek(this.now() + 10), L: () => this.seek(this.now() + 10),
      ArrowLeft: () => this.seek(this.now() - 5), ArrowRight: () => this.seek(this.now() + 5),
      ",": () => this.step(-1), ".": () => this.step(1),
      m: () => this.openMarkerForm(), M: () => this.openMarkerForm(),
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
    const range = this.ranges.find((r) => time >= r.start && time < r.end);
    if (range) lines.push(range.clean ? "Muted by Twitch: plays saved clean audio" : "Muted by Twitch");
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
    this.overview.hidden = !this.zoomed;
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

    // Muted parts (bottom two thirds of the track, so the played line stays visible)
    for (const range of this.ranges) {
      const [a, b] = span(range.start, range.end);
      if (b <= 0 || a >= width) continue;
      ctx.fillStyle = range.clean ? c.clean : c.muted;
      ctx.fillRect(a, top + 4, Math.max(1, b - a), height - 10);
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
      const mx = Math.round(x(marker.seconds)) + 0.5;
      if (mx < -4 || mx > width + 4) continue;
      const color = MARKER_COLORS[marker.color] ?? MARKER_COLORS.yellow;
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
    for (const range of this.ranges) {
      ctx.fillStyle = range.clean ? c.clean : c.muted;
      ctx.fillRect(x(range.start), 2, Math.max(1, x(range.end) - x(range.start)), 8);
    }
    ctx.fillStyle = c.clip;
    for (const clip of this.clips) ctx.fillRect(x(clip.start), 0, Math.max(1, x(clip.end ?? d) - x(clip.start)), 2);
    for (const marker of this.markers) {
      ctx.fillStyle = MARKER_COLORS[marker.color] ?? MARKER_COLORS.yellow;
      ctx.fillRect(Math.round(x(marker.seconds)), 0, 2, 12);
    }
    ctx.strokeStyle = c.window;
    ctx.lineWidth = 2;
    ctx.strokeRect(x(this.view.from) + 1, 1, Math.max(3, x(this.view.to) - x(this.view.from) - 2), 10);
    ctx.lineWidth = 1;
    ctx.fillStyle = c.playhead;
    ctx.fillRect(Math.round(x(now)) - 1, 0, 2, 12);
  }
}
