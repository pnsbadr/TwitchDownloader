// The timeline under the players (frontend rework steps 3 and 4). There are two, one per player, sharing the same panel
// (the Player 1 / Player 2 tabs show one at a time). Player 1's (mode "vod"): every member's rows on the loaded VOD, from
// GET /api/vods/{id}/timeline. Player 2 has its own multi-track project timeline
// (ProjectTimeline, ui/project-timeline.js); the "item" mode code paths here are left from the old saved-edits timeline. A row belongs to one member and one VOD: video rows hold segments (hatched boxes) and
// moment markers (lines), clip rows hold clips (solid bars). Everyone can see and play every row; only the owner edits
// it (editing comes in step 3b). Hide / solo / lock are page-only, per row.
//
// Drawing: one canvas for the ruler (with Twitch's muted parts in red and saved clean audio in green under the ticks)
// and one for the rows; row headers are DOM next to it so they can hold buttons. Times are seconds into the VOD.

import { timelineWheel } from "./timeline-input.js";
import { $, el, api, me, can, toast, formatDuration, withTooltip, loadSetting, saveSetting } from "./core.js";
import { askText, confirmAction, chooseSegmentDelete } from "./dialogs.js";
import { MARKER_COLORS, clipColor } from "./colors.js";
import { formatClock } from "./media.js";
import { editing, shiftTimes } from "./timeline-edit.js";
import { isDragging } from "./drag.js";
import { rowPlayButton } from "./project-timeline.js";
import { keptParts } from "./cuts.js";
import { drawWave, onWaves } from "./waves.js";

const ROW_HEIGHT = 40;
const WAVES_KEY = "clipco.p1.waves";      // sound waves on synced rows (W on Player 1)
const WAVES_MAX_SPAN = 20 * 60;           // they show once 20 min or less is in view
const FOCUS_ROW_HEIGHT = 64;    // the row that plays alone (row focus, editor.js focusRow)
const FOLDED_ROW_HEIGHT = 16;   // the other rows meanwhile
const MIN_SPAN = 10;
const REFRESH_MS = 5000;
const MUTED_COLOR = "rgb(240 82 82 / 0.5)";
const CLEAN_COLOR = "rgb(60 207 122 / 0.5)";
const TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];

export class Timeline {
  // mode "vod" (Player 1) or "item" (Player 2). hooks: see initEditing in timeline-edit.js, plus for "item" mode
  // onFocusChanged(item) (its range or title changed) and onFocusGone(message) (deleted, or its VOD can't be opened)
  constructor({ mode = "vod", onSeek, getPosition, ...hooks }) {
    this.mode = mode;
    this.onSeek = onSeek;
    this.rowHeight = ROW_HEIGHT;
    this.getPosition = getPosition;
    this.ruler = el("canvas", { class: "tl-ruler-canvas", hidden: true });
    this.track = el("canvas", { class: "tl-track-canvas", hidden: true });
    this.heads = el("div", { class: "tl-heads-set", hidden: true });
    this.tip = el("div", { class: "tl-tip", hidden: true });
    $("tl-ruler").append(this.ruler);
    $("tl-track").prepend(this.track);
    $("tl-heads").append(this.heads);
    document.body.append(this.tip);
    this.active = false;
    this.focus = null;      // "item" mode: { type: "marker" | "clip" | "video", id } of the item Player 2 shows
    this.video = null;      // "item" mode with a video: the video (GET /timeline's videos)
    this.window = null;     // "item" mode: { start, end } the view is locked to (the item, 0:00 to its length)

    this.vodId = null;
    this.vodTitle = "";
    this.knownLength = 0;   // from the VOD card; the player's playlist can say more while live
    this.mediaLength = 0;
    this.rows = [];
    this.markers = [];
    this.clips = [];
    this.videos = [];
    this.audio = [];        // [{ start, end, clean }]
    this.rowState = new Map(); // rowId -> { hidden, solo, locked, toggled }
    this.waves = loadSetting(WAVES_KEY, true) !== false;
    onWaves(() => { this.dirty = true; });
    this.view = { start: 0, end: 60 };
    this.follow = true;     // the view follows the playhead until the user scrolls it away
    this.hover = null;
    this.dirty = true;
    this.refreshTimer = null;
    this.loading = false;
    this.initEditing(hooks);

    this.bindPointer();
    this.bindFooter();
    new ResizeObserver(() => { this.dirty = true; }).observe($("tl-track"));
    new ResizeObserver(() => { this.dirty = true; }).observe($("tl-ruler"));
    const frame = () => {
      this.update();
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  }

  // ---------- loading ----------

  async open(vod) {
    await this.flushTrim();
    this.vodId = String(vod.vodId);
    this.vodTitle = vod.title;
    this.knownLength = vod.lengthSeconds || 0;
    this.mediaLength = 0;
    this.rows = [];
    this.markers = [];
    this.clips = [];
    this.videos = [];
    this.audio = [];
    this.rowState.clear();
    this.resetEditing({ keepLinking: true }); // a video being linked may take segments from this VOD too
    this.follow = true;
    this.view = { start: 0, end: Math.max(MIN_SPAN, this.length()) };
    this.renderHeads();
    this.renderFooter();
    await this.load();
    clearInterval(this.refreshTimer);
    this.refreshTimer = setInterval(() => {
      if (!document.hidden) this.load();
    }, REFRESH_MS);
  }

  // Player 2: one segment ({ type: "marker" }) or clip ({ type: "clip" }) of a VOD
  async openItem({ type, id, vodId, lengthSeconds }) {
    await this.flushTrim();
    this.vodId = String(vodId);
    this.knownLength = lengthSeconds || 0;
    this.mediaLength = 0;
    this.rows = [];
    this.markers = [];
    this.clips = [];
    this.videos = [];
    this.audio = [];
    this.rowState.clear();
    this.focus = { type, id };
    this.video = null;
    this.window = null;
    this.resetEditing();
    this.selected = type === "video" ? null : { ...this.focus };
    this.follow = true;
    this.renderHeads();
    this.renderFooter();
    await this.load();
    clearInterval(this.refreshTimer);
    this.refreshTimer = setInterval(() => {
      if (!document.hidden) this.load();
    }, REFRESH_MS);
  }

  async close() {
    await this.flushTrim();
    clearInterval(this.refreshTimer);
    this.vodId = null;
    this.focus = null;
    this.video = null;
    this.window = null;
    this.rows = [];
    this.markers = [];
    this.clips = [];
    this.resetEditing();
    this.renderHeads();
    this.renderFooter();
    this.dirty = true;
  }

  async load() {
    const vodId = this.vodId;
    if (!vodId) return;
    if (this.loading) {
      // Something changed while a read was on its way: read again after it
      this.loadAgain = true;
      return;
    }
    this.loading = true;
    try {
      const timeline = await api("GET", `/api/vods/${encodeURIComponent(vodId)}/timeline`);
      if (vodId !== this.vodId) return;
      if (timeline.maxStoredClipSeconds) this.maxStoredClipSeconds = timeline.maxStoredClipSeconds;
      if (this.mode === "item") {
        this.applyItem(timeline); // SourceTimeline
      } else {
        const rowsChanged = JSON.stringify(timeline.rows) !== JSON.stringify(this.rows);
        this.rows = timeline.rows ?? [];
        // Items on synced rows (another VOD's rows shown on this one, badrfly 2026-11-20) are kept on this VOD's clock:
        // their own VOD's seconds - the row's shift. `raw` keeps them as the server sent them.
        const shifts = new Map(this.rows.filter((r) => r.shift != null && r.vodId != null && String(r.vodId) !== String(vodId)).map((r) => [r.id, r.shift]));
        const local = (item) => (shifts.has(item.rowId) ? { ...shiftTimes(item, -shifts.get(item.rowId)), raw: item } : item);
        this.markers = (timeline.markers ?? []).map(local);
        this.clips = (timeline.clips ?? []).map(local);
        this.videos = timeline.videos ?? [];
        if (rowsChanged) {
          this.renderHeads();
          this.renderFooter();
        }
      }
    } catch (error) {
      if (vodId === this.vodId) {
        console.warn("Couldn't load the timeline:", error);
        // Player 2 can't show an item of a VOD the team can't open
        if (this.mode === "item" && (error.status === 403 || error.status === 404) && !this.rows.length) this.hooks.onFocusGone?.(error.message);
      }
    } finally {
      this.loading = false;
    }
    this.dirty = true;
    if (this.loadAgain) {
      this.loadAgain = false;
      this.load();
    }
  }

  // Player 2 (SourceTimeline) shows a video
  isVideoLayout() {
    return this.mode === "item" && this.focus?.type === "video";
  }


  // What the view can show: the whole VOD, or Player 2's window
  extent() {
    return this.window ?? { start: 0, end: this.length() };
  }

  // Player 1: select an item (Player 2's "Show in Player 1") and zoom so it shows whole, with some room around it.
  // range = { start, end } to show instead of the item's own (a video's segments)
  reveal({ type, id }, range = null) {
    this.selected = { type, id };
    const item = this.selectedItem();
    this.changed();
    if (!item && !range) return null;
    const start = range?.start ?? item.start;
    const end = range?.end ?? item.end ?? item.start;
    const room = Math.max((end - start) * 0.2, 2);
    // The item is there, so the VOD is at least that long (a live VOD's known length can lag behind): don't clamp the view short of it
    if (this.mode === "vod") this.knownLength = Math.max(this.knownLength, end + room);
    this.follow = true;
    this.setView(start - room, end + room);
    return item;
  }

  // Shows this timeline in the panel (the Player 1 / Player 2 tabs)
  setActive(on) {
    this.active = on;
    this.heads.hidden = !on;
    this.ruler.hidden = !on;
    this.track.hidden = !on;
    this.hover = null;
    this.tip.hidden = true;
    this.renderHeads();
    this.renderFooter();
    this.dirty = true;
  }

  // The playing rendition's playlist (hls.js): muted/clean parts and the length so far
  setDetails(details) {
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
    this.vodAudio = ranges;
    this.audio = ranges;
    const fragments = details?.fragments ?? [];
    const last = fragments[fragments.length - 1];
    const before = this.length();
    this.mediaLength = last ? last.start + last.duration : 0;
    // First playlist: show the whole VOD
    if (this.mode === "vod" && (before <= MIN_SPAN || (this.view.end > before - 0.5 && this.view.start <= 0))) {
      this.view.end = Math.max(this.view.end, Math.min(this.length(), this.view.start + this.span()));
    }
    this.dirty = true;
  }

  // The VOD's length (known so far)
  vodLength() {
    return Math.max(this.knownLength, this.mediaLength, MIN_SPAN);
  }

  // What the timeline spans: the VOD (Player 2: its item)
  length() {
    return this.vodLength();
  }

  span() {
    return this.view.end - this.view.start;
  }

  // ---------- rows: headers and the footer buttons ----------

  stateOf(row) {
    if (!this.rowState.has(row.id)) this.rowState.set(row.id, { hidden: false, solo: false, locked: false, toggled: false });
    return this.rowState.get(row.id);
  }

  canDeleteRow(row) {
    if (me?.support) return false;
    return (row.userId === me?.id && can("AddEntries")) || can("DeleteAnyEntries");
  }

  rowTitle(row) {
    const name = row.name || (row.kind === "Clip" || row.kind === "clip" ? "Clips" : "Segments");
    // Numbered only among the member's unnamed rows of that kind (a synced row carries its channel's name)
    const sameKind = this.rows.filter((r) => r.userId === row.userId && r.kind === row.kind && !r.name && !r.shownOnVodId);
    const number = row.name || sameKind.length < 2 ? "" : ` ${sameKind.indexOf(row) + 1}`;
    return `${name}${number}`;
  }

  renderHeads() {
    const heads = this.heads;
    const item = this.mode === "item";
    if (this.active) {
      $("tl-empty").hidden = Boolean(this.vodId && this.rows.length);
      $("tl-empty").textContent = item
        ? "Load a video, clip or segment in Player 2."
        : this.vodId
          ? "No rows on this VOD yet."
          : "Load a VOD in Player 1 to see the team's rows.";
    }
    heads.replaceChildren(...this.rows.map((row, index) => {
      const state = this.stateOf(row);
      const isClip = /clip/i.test(row.kind);
      const own = row.userId === me?.id;
      const toggle = (key, label, icon, iconOn) => {
        const button = el("sl-icon-button", { library: "ph", name: state[key] ? iconOn : icon, label, class: "row-btn" });
        if (state[key]) button.setAttribute("data-active", "");
        button.addEventListener("click", () => {
          state[key] = !state[key];
          this.renderHeads();
          this.dirty = true;
        });
        return withTooltip(button, label);
      };
      const synced = !item && row.shift != null && row.vodId != null && String(row.vodId) !== String(this.vodId);
      const name = el("div", { class: "row-name", title: synced ? "Synced row: another streamer's VOD, lined up with this one" : "" },
        el("sl-icon", { library: "ph", name: isClip ? "film-strip" : "brackets-square", class: "row-kind" }),
        el("span", { class: "row-title", title: row.canEdit ? "Double-click to rename" : "" }, this.rowTitle(row)),
        el("span", { class: "row-owner" + (own ? " own" : "") }, own ? "you" : row.username));
      if (row.canEdit) name.addEventListener("dblclick", () => this.renameRow(row));
      // Checked rows play together (row play button / footer) and are what "Delete checked rows" deletes (the ones this member may)
      const check = !item && !me?.support
        ? el("sl-checkbox", { size: "small", class: "row-check", checked: state.toggled, "aria-label": "Check row (play together, delete)" })
        : el("span", { class: "row-check-gap" });
      check.addEventListener?.("sl-change", (event) => {
        state.toggled = event.target.checked;
        this.renderFooter();
      });
      const focusClass = this.focus ? (this.focus.includes(row.id) ? " focused" : " folded") : "";
      const head = el("div", { class: "row-head" + (own ? " own" : "") + (index % 2 ? " odd" : "") + focusClass, style: `height:${this.rowGeom()[index].h}px` },
        check,
        item ? null : rowPlayButton(Boolean(this.focus?.includes(row.id)), () => this.hooks.onFocusRow?.(this.playSet(row.id))),

        name,
        el("div", { class: "row-btns" },
          item ? null : toggle("hidden", state.hidden ? "Show row" : "Hide row", "eye", "eye-slash"),
          // a synced row: their stream in Player 1's corner (whose header opens it in Player 1) instead of solo (room for the name)
          synced ? this.pipButton(row) : null,
          item || synced ? null : toggle("solo", "Solo (only this row)", "headphones", "headphones"),
          row.canEdit ? toggle("locked", state.locked ? "Unlock row" : "Lock row", "lock-simple-open", "lock-simple") : null));
      // While a row plays alone, clicking another row's (folded) head plays that one instead
      head.addEventListener("click", (event) => {
        if (event.target.closest("sl-icon-button, sl-checkbox")) return;
        if (this.focus && !this.focus.includes(row.id)) this.hooks.onFocusRow?.([row.id]);
        // the row < / > slide (a synced row) and new items go to
        else if (!this.focus && row.userId === me?.id) this.activeRowId = row.id;
      });
      return head;
    }));
    this.dirty = true;
  }

  // A synced row's head: their stream plays small in Player 1's corner, following Player 1
  pipButton(row) {
    const on = this.hooks.pipShowing?.(row) ?? false;
    const button = el("sl-icon-button", { library: "ph", name: "picture-in-picture", label: on ? "Hide their stream" : "Watch their stream in a corner", class: "row-btn" });
    if (on) button.setAttribute("data-active", "");
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      this.hooks.onPip?.(row);
      this.renderHeads();
    });
    return withTooltip(button, on ? "Hide their stream" : "Watch their stream in Player 1's corner, following it");
  }

  // The rows a row's play button plays: every checked row when it's one of them, else just it
  playSet(rowId) {
    const checked = this.rows.filter((r) => this.stateOf(r).toggled).map((r) => r.id);
    return checked.length > 1 && checked.includes(rowId) ? checked : [rowId];
  }

  async renameRow(row) {
    const name = await askText({ heading: "Rename row", label: "Name", value: row.name ?? "", confirm: "Rename", maxLength: 60 });
    if (name === null) return;
    try {
      await api("PUT", `/api/rows/${row.id}`, { name: name.trim() });
      await this.load();
      this.renderHeads();
    } catch (error) {
      toast(error.message, "error");
    }
  }

  // Both timelines bind the footer; only the one on show acts
  bindFooter() {
    const on = (id, type, run) => $(id).addEventListener(type, (event) => {
      if (this.active) run(event);
    });
    on("tl-add-video", "click", () => this.addRow("video"));
    on("tl-add-clip", "click", () => this.addRow("clip"));
    on("tl-add-synced", "click", () => this.hooks.onAddSyncedRow?.(this.getPosition() ?? 0));
    on("tl-delete-rows", "click", () => this.deleteToggledRows());
    on("tl-play-checked", "click", () => {
      const checked = this.rows.filter((r) => this.stateOf(r).toggled).map((r) => r.id);
      if (checked.length) this.hooks.onFocusRow?.(checked);
    });
    on("tl-zoom-in", "click", () => this.zoomBy(0.5));
    on("tl-zoom-out", "click", () => this.zoomBy(2));
    on("tl-fit", "click", () => this.fit());
    on("tl-zoom", "sl-input", (event) => this.zoomToSlider(Number(event.target.value)));
  }

  renderFooter() {
    if (!this.active) return;
    const rows = this.mode === "vod";
    const canAdd = rows && Boolean(this.vodId) && can("AddEntries") && !me?.support;
    $("tl-add-video").disabled = !canAdd;
    $("tl-add-clip").disabled = !canAdd;
    $("tl-add-synced").disabled = !canAdd || !this.hooks.onAddSyncedRow;
    $("tl-delete-rows").disabled = !rows || !this.rows.some((row) => this.stateOf(row).toggled && this.canDeleteRow(row));
    $("tl-play-checked").disabled = !rows || !this.rows.some((row) => this.stateOf(row).toggled);
    for (const id of ["tl-zoom-in", "tl-zoom-out", "tl-fit", "tl-zoom"]) $(id).disabled = !this.vodId;
    $("tl-zoom").value = this.sliderValue();
  }

  async addRow(kind) {
    try {
      await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/rows`, { kind });
      await this.load();
      this.renderHeads();
      this.renderFooter();
    } catch (error) {
      toast(error.message, "error");
    }
  }

  async deleteToggledRows() {
    const rows = this.rows.filter((row) => this.stateOf(row).toggled && this.canDeleteRow(row));
    if (!rows.length) return;
    const markers = this.markers.filter((m) => rows.some((row) => row.id === m.rowId));
    const items = markers.length;
    const ok = await confirmAction({
      heading: rows.length === 1 ? "Delete this row?" : `Delete ${rows.length} rows?`,
      text: items
        ? `The ${items} segment${items === 1 ? "" : "s"} and marker${items === 1 ? "" : "s"} on ${rows.length === 1 ? "it" : "them"} are deleted too.`
        : "They're empty.",
      confirm: "Delete",
    });
    if (!ok) return;
    // Segments in videos (badrfly 2026-11-20): unlink them from the videos, or keep the videos as they are
    let videos = "unlink";
    const linked = markers.filter((m) => this.videosOf(m.id).length);
    if (linked.length) {
      const titles = [...new Set(linked.flatMap((m) => this.videosOf(m.id).map((v) => v.title)))];
      const choice = await chooseSegmentDelete({ count: linked.length, titles });
      if (!choice) return;
      videos = choice;
    }
    for (const row of rows) {
      try {
        await api("DELETE", `/api/rows/${row.id}${videos === "keep" ? "?videos=keep" : ""}`);
        this.rowState.delete(row.id);
      } catch (error) {
        toast(`${this.rowTitle(row)}: ${error.message}`, "error");
      }
    }
    await this.load();
    this.renderHeads();
    this.renderFooter();
  }

  // ---------- zoom and view ----------

  extentSpan() {
    const extent = this.extent();
    return extent.end - extent.start;
  }

  minSpan() {
    return Math.min(MIN_SPAN, this.extentSpan());
  }

  // Slider 0 = everything (the whole VOD, or Player 2's window), 100 = MIN_SPAN; logarithmic in between
  sliderValue() {
    const full = this.extentSpan();
    if (full <= this.minSpan() + 0.01) return 0;
    const ratio = Math.log(full / this.span()) / Math.log(full / this.minSpan());
    return Math.round(Math.min(Math.max(ratio, 0), 1) * 100);
  }

  zoomToSlider(value) {
    const full = this.extentSpan();
    const span = full * Math.pow(this.minSpan() / full, value / 100);
    const center = this.getPosition() ?? (this.view.start + this.span() / 2);
    this.setView(center - span / 2, center + span / 2, false);
  }

  zoomBy(factor, anchor = this.getPosition() ?? (this.view.start + this.span() / 2)) {
    const span = Math.min(Math.max(this.span() * factor, this.minSpan()), this.extentSpan());
    const ratio = (anchor - this.view.start) / this.span();
    this.setView(anchor - ratio * span, anchor - ratio * span + span);
  }

  fit() {
    this.follow = true;
    const extent = this.extent();
    this.setView(extent.start, extent.end);
  }

  setView(start, end, updateSlider = true) {
    const extent = this.extent();
    const span = Math.min(Math.max(end - start, this.minSpan()), extent.end - extent.start);
    start = Math.min(Math.max(start, extent.start), Math.max(extent.start, extent.end - span));
    this.view = { start, end: start + span };
    if (updateSlider && this.active) $("tl-zoom").value = this.sliderValue();
    this.dirty = true;
  }

  // ---------- pointer ----------

  xToTime(x, width) {
    return this.view.start + (x / width) * this.span();
  }

  timeToX(seconds, width) {
    return ((seconds - this.view.start) / this.span()) * width;
  }

  bindPointer() {
    for (const canvas of [this.ruler, this.track]) {
      canvas.addEventListener("pointerdown", (event) => this.pointerDown(event, canvas));
      canvas.addEventListener("pointermove", (event) => {
        const rect = canvas.getBoundingClientRect();
        this.hover = { canvas, x: event.clientX - rect.left, y: event.clientY - rect.top, width: rect.width, clientX: event.clientX, clientY: event.clientY };
        this.pointerMove(event);
        this.renderTip();
      });
      canvas.addEventListener("pointerup", (event) => this.pointerUp(event));
      canvas.addEventListener("pointercancel", () => {
        this.press = null;
        this.preview = null;
        this.snapAt = null;
        this.edgeTip = null;
        this.dirty = true;
      });
      canvas.addEventListener("pointerleave", () => {
        this.hover = null;
        if (this.razorAt) {
          this.razorAt = null;
          this.snapAt = null;
          this.dirty = true;
        }
        this.renderTip();
      });
      canvas.addEventListener("contextmenu", (event) => this.contextMenu(event, canvas));
      canvas.addEventListener("dblclick", (event) => this.doubleClick(event, canvas));
      canvas.addEventListener("wheel", (event) => {
        if (!this.vodId) return;
        const rect = canvas.getBoundingClientRect();
        timelineWheel(event, {
          x: event.clientX - rect.left, width: rect.width, span: this.span(), xToTime: (px, w) => this.xToTime(px, w),
          zoomAt: (factor, at) => { this.follow = false; this.zoomBy(factor, at); },
          panBy: (seconds) => { this.follow = false; this.setView(this.view.start + seconds, this.view.end + seconds); },
          rows: document.querySelector(".tl-body"),
        });
      }, { passive: false });
    }
  }

  // The item drawn under a point of the rows canvas: { kind, id, start, end?, title, by, row, data }
  itemAt(x, y, width) {
    const row = this.rows[Math.floor(y / ROW_HEIGHT)];
    if (!row || this.stateOf(row).hidden) return null;
    const t = this.xToTime(x, width);
    const pad = (4 / width) * this.span();
    const items = this.itemsOf(row);
    // the selected item first, then moments (thin), then ranges, topmost (last drawn) first
    const selected = items.find((item) => this.isPrimary(item));
    if (selected && (selected.end == null ? Math.abs(selected.start - t) <= pad * 1.5 : t >= selected.start - pad && t <= selected.end + pad)) return selected;
    for (const item of [...items].reverse()) {
      if (item.end == null && Math.abs(item.start - t) <= pad * 1.5) return item;
    }
    for (const item of [...items].reverse()) {
      if (item.end != null && t >= item.start - pad && t <= item.end + pad) return item;
    }
    return null;
  }

  itemsOf(row) {
    const preview = this.preview ?? (this.pendingTrim ? { type: "clip", ...this.pendingTrim } : null);
    const moved = (type, item) => (preview && preview.type === type && preview.id === item.id
      ? { ...item, start: preview.start, end: preview.end, ...(preview.cuts !== undefined ? { cuts: preview.cuts ?? [] } : {}) }
      : item);
    if (/clip/i.test(row.kind)) {
      return this.clips.filter((c) => c.rowId === row.id).map((c) => {
        const start = c.startSeconds ?? c.start;
        const end = c.endSeconds ?? c.end ?? this.length();
        // Only a clip over the stored-clip limit looks like a reference: shorter ones are references too when the team doesn't store
        // clips (badrfly 2026-11-18), but they look like any clip
        const long = Boolean(c.referenceOnly) && end - start > this.maxStoredClipSeconds + 0.01;
        return moved("clip", {
          kind: "clip", id: c.id, start, end, title: c.title, color: c.color ?? "violet", by: c.createdBy, reference: c.referenceOnly, long, cuts: c.cuts ?? [], row, data: c,
        });
      });
    }
    return this.markers.filter((m) => m.rowId === row.id).map((m) => moved("marker", {
      kind: m.endSeconds == null ? "moment" : "segment", id: m.id, start: m.seconds, end: m.endSeconds, title: m.label,
      color: m.color, by: m.createdBy, cuts: m.cuts ?? [], row, data: m, videos: this.videosOf(m.id),
    }));
  }

  // Player 1: the videos a segment is linked into, [{ title, position, count, prev, next }] (prev / next: the parts around it, which
  // may be on another stream: a video can span several VODs)
  videosOf(markerId) {
    if (this.mode !== "vod") return [];
    const out = [];
    for (const video of this.videos) {
      const segments = [...(video.segments ?? [])].sort((a, b) => a.position - b.position);
      const index = segments.findIndex((s) => s.markerId === markerId);
      if (index >= 0) {
        const part = (s, i) => s && { markerId: s.markerId, vodId: String(s.vodId ?? video.vodId), seconds: s.startSeconds, position: i + 1 };
        out.push({ id: video.id, title: video.title, position: index + 1, count: segments.length,
          prev: part(segments[index - 1], index - 1), next: part(segments[index + 1], index + 1) });
      }
    }
    return out;
  }

  // A neighboring part of a video that isn't on this timeline (another stream)
  elsewhere(part) {
    return part && part.vodId !== String(this.vodId) && !this.markers.some((m) => m.id === part.markerId) ? part : null;
  }

  // "→ 3 · 20 Nov" after a segment whose video goes on in another stream, "1 · 19 Nov ←" before one whose video started in another;
  // a click opens that stream at that part (pointer tool)
  drawStreamLinks(ctx, width) {
    this.streamPills = [];
    if (this.mode !== "vod") return;
    ctx.save();
    ctx.font = "600 11px Barlow, system-ui, sans-serif";
    ctx.textBaseline = "middle";
    this.rows.forEach((row, index) => {
      if (this.stateOf(row).hidden) return;
      for (const item of this.itemsOf(row)) {
        if (item.kind !== "segment") continue;
        const video = item.videos?.find((v) => this.elsewhere(v.prev) || this.elsewhere(v.next));
        if (!video) continue;
        const y = index * ROW_HEIGHT + ROW_HEIGHT / 2 - 9;
        const pill = (part, after) => {
          const day = this.hooks.vodDay?.(part.vodId) ?? "other stream";
          const text = after ? `→ ${part.position} · ${day}` : `${part.position} · ${day} ←`;
          const w = ctx.measureText(text).width + 14;
          const x = after ? this.timeToX(item.end, width) + 4 : this.timeToX(item.start, width) - 4 - w;
          if (x > width || x + w < 0) return;
          ctx.fillStyle = "rgb(16 17 20 / 0.92)";
          ctx.strokeStyle = "#8b7cf6";
          ctx.lineWidth = 1;
          ctx.setLineDash([3, 2]);
          ctx.beginPath();
          ctx.roundRect(x + 0.5, y + 0.5, w, 18, 9);
          ctx.fill();
          ctx.stroke();
          ctx.setLineDash([]);
          ctx.fillStyle = "#cfc7ff";
          ctx.fillText(text, x + 7, y + 10);
          this.streamPills.push({ x, y, w, h: 18, vodId: part.vodId, seconds: part.seconds, title: video.title, position: part.position });
        };
        if (this.elsewhere(video.next)) pill(video.next, true);
        if (this.elsewhere(video.prev)) pill(video.prev, false);
      }
    });
    ctx.restore();
  }

  streamPillAt(x, y) {
    return (this.streamPills ?? []).find((p) => x >= p.x && x <= p.x + p.w && y >= p.y && y <= p.y + p.h) ?? null;
  }

  renderTip() {
    const tip = this.tip;
    // While an edge is dragged the tip shows only that edge's time
    if (this.edgeTip) {
      tip.replaceChildren(el("div", { class: "num" }, this.edgeTip.text ?? formatClock(this.edgeTip.time)));
      tip.hidden = false;
      tip.style.left = `${this.edgeTip.clientX + 12}px`;
      tip.style.top = `${this.edgeTip.clientY + 16}px`;
      return;
    }
    // While something is dragged the tip says what dropping it does (showDropTip)
    if (isDragging()) return;
    const hover = this.hover;
    if (!hover || !this.vodId || this.press) {
      tip.hidden = true;
      return;
    }
    const t = this.xToTime(hover.x, hover.width);
    const item = hover.canvas === this.track ? this.itemAt(hover.x, hover.y, hover.width) : null;
    const lines = [formatDuration(Math.max(0, t), { short: false })];
    if (item) {
      const range = item.end != null ? `${formatDuration(item.start, { short: false })} – ${formatDuration(item.end, { short: false })}` : `at ${formatDuration(item.start, { short: false })}`;
      lines.push(`${item.title || { clip: "Clip", segment: "Segment", moment: "Marker" }[item.kind]} · ${range}`);
      const cut = (item.cuts ?? []).find((c) => t >= c.start && t < c.end);
      if (cut) lines.push(`Cut out ${formatDuration(cut.start, { short: false })} – ${formatDuration(cut.end, { short: false })} · ${this.cutNote ?? "left out of files"}`);
      if (item.videoIndex) lines.push(`Part ${item.videoIndex} · VOD ${formatDuration(item.vodStart, { short: false })} – ${formatDuration(item.vodEnd, { short: false })}`);
      for (const video of item.videos ?? []) {
        const on = [this.elsewhere(video.prev), this.elsewhere(video.next)].filter(Boolean).map((p) => this.hooks.vodDay?.(p.vodId) ?? "another stream");
        lines.push(`In video "${video.title}" (${video.position} of ${video.count})${on.length ? ` · goes on in ${[...new Set(on)].join(", ")}` : ""}`);
      }
      if (item.by) lines.push(`by ${item.by}${this.canEditItem(item) ? "" : " · view only"}`);
    }
    tip.replaceChildren(...lines.map((line, i) => el("div", { class: i ? "" : "num" }, line)));
    tip.hidden = false;
    tip.style.left = `${hover.clientX + 12}px`;
    tip.style.top = `${hover.clientY + 16}px`;
  }

  // ---------- drawing ----------

  update() {
    const now = this.getPosition();
    if (this.vodId && now != null) {
      // Keep the playhead in view while following
      if (this.follow && (now < this.view.start || now > this.view.end)) {
        const span = this.span();
        this.setView(now - span * 0.1, now + span * 0.9);
      }
      if (now !== this.lastNow) this.dirty = true;
      this.lastNow = now;
    }
    if (!this.dirty) return;
    this.dirty = false;
    this.drawRuler(now);
    this.drawTrack(now);
  }

  prepare(canvas, cssHeight) {
    const dpr = window.devicePixelRatio || 1;
    const width = canvas.parentElement.clientWidth;
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(cssHeight * dpr)) {
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(cssHeight * dpr);
      canvas.style.width = `${width}px`;
      canvas.style.height = `${cssHeight}px`;
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, cssHeight);
    return { ctx, width };
  }

  drawRuler(now) {
    const height = this.ruler.parentElement.clientHeight || 32;
    const { ctx, width } = this.prepare(this.ruler, height);
    if (!this.vodId) return;
    const css = getComputedStyle(document.documentElement);
    const span = this.span();
    const step = TICK_STEPS.find((s) => (s / span) * width >= 80) ?? TICK_STEPS[TICK_STEPS.length - 1];
    ctx.font = "12px Barlow, system-ui, sans-serif";
    ctx.textBaseline = "top";
    for (let t = Math.ceil(this.view.start / step) * step; t <= this.view.end; t += step) {
      const x = Math.round(this.timeToX(t, width)) + 0.5;
      ctx.strokeStyle = css.getPropertyValue("--line-strong");
      ctx.beginPath();
      ctx.moveTo(x, height - 14);
      ctx.lineTo(x, height - 6);
      ctx.stroke();
      ctx.fillStyle = css.getPropertyValue("--text-dim");
      ctx.fillText(formatDuration(t), x + 4, 4);
      // minor ticks
      for (let i = 1; i < 5; i++) {
        const mx = Math.round(this.timeToX(t + (step * i) / 5, width)) + 0.5;
        ctx.beginPath();
        ctx.moveTo(mx, height - 10);
        ctx.lineTo(mx, height - 6);
        ctx.stroke();
      }
    }
    // Audio: muted (red) and saved clean audio (green) along the bottom
    for (const range of this.audio) {
      const x1 = this.timeToX(range.start, width);
      const x2 = this.timeToX(range.end, width);
      if (x2 < 0 || x1 > width) continue;
      ctx.fillStyle = range.clean ? CLEAN_COLOR : MUTED_COLOR;
      ctx.fillRect(x1, height - 5, Math.max(1, x2 - x1), 5);
    }
    this.drawPlayhead(ctx, width, height, now, true);
  }

  // ---------- row focus: one row plays alone (editor.js focusRow); it's taller, the others fold to thin lines ----------

  // rowIds: the rows that play (one, or the checked ones together), or null
  setFocus(rowIds) {
    const ids = rowIds == null ? [] : Array.isArray(rowIds) ? rowIds : [rowIds];
    this.focus = ids.length ? ids : null;
    this.renderHeads();
    this.dirty = true;
  }

  rowGeom() {
    let y = 0;
    return this.rows.map((row) => {
      const h = !this.focus ? ROW_HEIGHT : this.focus.includes(row.id) ? (this.focus.length > 2 ? ROW_HEIGHT : FOCUS_ROW_HEIGHT) : FOLDED_ROW_HEIGHT;
      const g = { y, h };
      y += h;
      return g;
    });
  }

  rowsHeight() {
    return this.rowGeom().reduce((sum, g) => sum + g.h, 0);
  }

  // What the focused rows play, on this VOD's clock: their segments' and clips' kept parts (moments are skipped), in time order.
  // [{ start, end, vodId, shift }]: a synced row's parts play from its own VOD at clock + shift. Where checked rows overlap the
  // higher row plays (like Player 2's rows).
  focusParts() {
    if (!this.focus) return [];
    const rows = this.rows.filter((r) => this.focus.includes(r.id));
    const perRow = rows.map((row, rank) => {
      const synced = row.shift != null && row.vodId != null && String(row.vodId) !== String(this.vodId);
      const parts = this.itemsOf(row)
        .filter((item) => item.end != null && item.end > item.start)
        .flatMap((item) => keptParts(item.start, item.end, item.cuts ?? []))
        .sort((a, b) => a.start - b.start);
      const joined = [];
      for (const part of parts) {
        const last = joined[joined.length - 1];
        if (last && part.start <= last.end + 0.05) last.end = Math.max(last.end, part.end);
        else joined.push({ start: part.start, end: part.end, rank, vodId: String(synced ? row.vodId : this.vodId), shift: synced ? row.shift : 0 });
      }
      return joined;
    });
    if (perRow.length === 1) return perRow[0];
    const all = perRow.flat();
    const cuts = [...new Set(all.flatMap((p) => [p.start, p.end]))].sort((a, b) => a - b);
    const out = [];
    for (let i = 0; i + 1 < cuts.length; i++) {
      const a = cuts[i];
      const b = cuts[i + 1];
      if (b - a < 0.01) continue;
      const mid = (a + b) / 2;
      let top = null;
      for (const p of all) if (p.start <= mid && mid < p.end && (!top || p.rank < top.rank)) top = p;
      if (!top) continue;
      const last = out[out.length - 1];
      if (last && last.rank === top.rank && Math.abs(last.end - a) < 0.01) last.end = b;
      else out.push({ start: a, end: b, rank: top.rank, vodId: top.vodId, shift: top.shift });
    }
    return out;
  }

  // Sound waves on synced rows on/off (W on Player 1)
  toggleWaves() {
    this.waves = !this.waves;
    saveSetting(WAVES_KEY, this.waves);
    this.dirty = true;
    toast(this.waves ? "Sound waves on synced rows: on (they show once you zoom in to 20 minutes or less)." : "Sound waves on synced rows: off.");
  }

  // A synced row's background: their stream's sound wave above the middle, this VOD's below, so a laugh or a shout lines up by eye
  drawSyncWaves(ctx, row, y, width) {
    if (!this.waves || row.shift == null || row.vodId == null || String(row.vodId) === String(this.vodId)) return;
    const start = Math.max(0, this.xToTime(0, width));
    const end = this.xToTime(width, width);
    if (!(end > start) || end - start > WAVES_MAX_SPAN) return;
    const x1 = this.timeToX(start, width);
    const x2 = this.timeToX(end, width);
    const quarter = ROW_HEIGHT / 4;
    drawWave(ctx, { vodId: row.vodId, start: start + row.shift, end: end + row.shift, x1, x2, midY: y + quarter + 1, halfHeight: quarter - 2, color: "rgb(255 255 255 / 0.16)" });
    drawWave(ctx, { vodId: this.vodId, start, end, x1, x2, midY: y + 3 * quarter - 1, halfHeight: quarter - 2, color: "rgb(139 124 246 / 0.3)" });
  }

  drawFocused(now) {
    const geom = this.rowGeom();
    const height = Math.max(this.rowsHeight(), $("tl-track").clientHeight || 0, 1);
    const { ctx, width } = this.prepare(this.track, height);
    const css = getComputedStyle(document.documentElement);
    const line = css.getPropertyValue("--line");
    const accent = css.getPropertyValue("--accent-strong").trim() || "#a597ff";
    this.rows.forEach((row, index) => {
      const { y, h } = geom[index];
      ctx.fillStyle = line;
      ctx.fillRect(0, y + h - 1, width, 1);
      const items = this.itemsOf(row);
      if (!this.focus.includes(row.id)) {
        ctx.save();
        ctx.globalAlpha = 0.8;
        for (const item of items) {
          const x1 = this.timeToX(item.start, width);
          const x2 = item.end == null ? x1 + 2 : this.timeToX(item.end, width);
          if (x2 < 0 || x1 > width) continue;
          ctx.fillStyle = item.kind === "clip" ? clipColor(item.color) : MARKER_COLORS[item.color] ?? MARKER_COLORS.yellow;
          roundRect(ctx, x1, y + 5, Math.max(2, x2 - x1), h - 10, 3);
          ctx.fill();
        }
        ctx.restore();
        return;
      }
      // the gaps it skips: dashed, "skip"
      let from = null;
      for (const part of this.focusParts()) {
        if (from != null && part.start - from > 0.05) drawSkip(ctx, this.timeToX(from, width), this.timeToX(part.start, width), y, h, accent);
        from = part.end;
      }
      for (const item of items) this.drawItem(ctx, item, y, width, h);
    });
    this.drawPlayhead(ctx, width, height, now, false);
  }

  drawTrack(now) {
    if (this.focus && this.vodId) {
      this.drawFocused(now);
      return;
    }
    const rowCount = this.rows.length;
    const height = Math.max(rowCount * ROW_HEIGHT, $("tl-track").clientHeight);
    const { ctx, width } = this.prepare(this.track, rowCount * ROW_HEIGHT || 1);
    if (!this.vodId) return;
    const css = getComputedStyle(document.documentElement);
    const anySolo = this.rows.some((row) => this.stateOf(row).solo);
    this.rows.forEach((row, index) => {
      const y = index * ROW_HEIGHT;
      const state = this.stateOf(row);
      ctx.fillStyle = row.userId === me?.id ? "rgb(139 124 246 / 0.06)" : index % 2 ? "rgb(255 255 255 / 0.015)" : "transparent";
      ctx.fillRect(0, y, width, ROW_HEIGHT);
      ctx.fillStyle = css.getPropertyValue("--line");
      ctx.fillRect(0, y + ROW_HEIGHT - 1, width, 1);
      if (state.hidden) return;
      this.drawSyncWaves(ctx, row, y, width);
      ctx.globalAlpha = anySolo && !state.solo ? 0.25 : state.locked ? 0.7 : 1;
      const items = this.itemsOf(row);
      // the selected items on top (the one clicked last above the rest)
      const selected = items.filter((item) => this.isSelected(item)).sort((a, b) => this.isPrimary(a) - this.isPrimary(b));
      for (const item of items) if (!selected.includes(item)) this.drawItem(ctx, item, y, width);
      for (const item of selected) {
        this.drawItem(ctx, item, y, width);
        this.drawSelection(ctx, item, y, width);
      }
      ctx.globalAlpha = 1;
    });
    this.drawStreamLinks(ctx, width);
    this.drawOverlay?.(ctx, width);
    this.drawPending(ctx, width, now);
    if (this.marquee) this.drawMarquee(ctx);
    if (this.cutDrag) this.drawCutDrag(ctx, width);
    this.drawRazor?.(ctx, width);
    // The row a dragged item would land on
    const dropIndex = this.dropRowId == null ? -1 : this.rows.findIndex((row) => row.id === this.dropRowId);
    if (dropIndex >= 0) {
      const accent = css.getPropertyValue("--accent").trim() || "#8b7cf6";
      ctx.save();
      ctx.fillStyle = accent;
      ctx.globalAlpha = 0.14;
      ctx.fillRect(0, dropIndex * ROW_HEIGHT, width, ROW_HEIGHT);
      ctx.globalAlpha = 1;
      ctx.strokeStyle = accent;
      ctx.lineWidth = 2;
      ctx.strokeRect(1, dropIndex * ROW_HEIGHT + 1, width - 2, ROW_HEIGHT - 2);
      ctx.restore();
    }
    if (this.snapAt != null) {
      const x = Math.round(this.timeToX(this.snapAt, width)) + 0.5;
      ctx.save();
      ctx.strokeStyle = "#fff";
      ctx.setLineDash([3, 3]);
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, height);
      ctx.stroke();
      ctx.restore();
    }
    this.drawPlayhead(ctx, width, height, now, false);
  }

  // The box being drawn to select items (roadmap 1b): like Player 2's
  drawMarquee(ctx) {
    const { x1, y1, x2, y2 } = this.marquee;
    ctx.save();
    ctx.fillStyle = "rgb(139 124 246 / 0.15)";
    ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue("--accent").trim() || "#8b7cf6";
    ctx.fillRect(x1, y1, x2 - x1, y2 - y1);
    ctx.strokeRect(x1 + 0.5, y1 + 0.5, x2 - x1, y2 - y1);
    ctx.restore();
  }

  // White outline around the selected item, with grips on edges that can be dragged
  drawSelection(ctx, item, y, width) {
    const x1 = this.timeToX(item.start, width);
    ctx.save();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 2;
    if (item.kind === "moment") {
      ctx.strokeRect(Math.round(x1) - 4, y + 2, 8, ROW_HEIGHT - 4);
      ctx.restore();
      return;
    }
    const x2 = this.timeToX(item.end, width);
    roundRect(ctx, x1 - 2, y + 5, Math.max(2, x2 - x1) + 4, ROW_HEIGHT - 10, 4);
    ctx.stroke();
    // grips only when it's the one selected item (edges drag one item at a time)
    if (this.picks.size === 1 && this.canMoveEdges(item)) {
      ctx.fillStyle = "#fff";
      for (const x of [x1, x2]) {
        if (x < -4 || x > width + 4) continue;
        ctx.fillRect(x - 2, y + ROW_HEIGHT / 2 - 7, 4, 14);
      }
    }
    ctx.restore();
  }

  // Player 2's cut tool: the part being dragged across (dark, red dashed outline)
  drawCutDrag(ctx, width) {
    const x1 = this.timeToX(this.cutDrag.start, width);
    const x2 = this.timeToX(this.cutDrag.end, width);
    ctx.save();
    ctx.fillStyle = "rgb(0 0 0 / 0.55)";
    ctx.fillRect(x1, 4, Math.max(1, x2 - x1), ROW_HEIGHT - 8);
    ctx.strokeStyle = "#ff6b6b";
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 4]);
    ctx.strokeRect(x1, 4, Math.max(1, x2 - x1), ROW_HEIGHT - 8);
    ctx.restore();
  }

  // A segment being marked (S pressed once: start to playhead), a clip being dragged with the clip tool, and the item the title
  // editor is naming
  drawPending(ctx, width, now) {
    let pending = null;
    if (this.draft) pending = this.draft;
    else if (this.clipDrag) pending = this.clipDrag;
    else if (this.marking && now != null) {
      const end = now;
      pending = { kind: this.marking.kind, start: Math.min(this.marking.start, end), end: Math.max(this.marking.start, end), color: this.lastColor };
    }
    if (!pending) return;
    const row = this.targetRow(pending.kind === "clip" ? "clip" : "video");
    const index = row ? this.rows.indexOf(row) : -1;
    if (index < 0 || this.stateOf(row).hidden) return; // no own row yet: the server makes one when it's saved
    const y = index * ROW_HEIGHT;
    const color = pending.kind === "clip" ? clipColor(pending.color) : MARKER_COLORS[pending.color] ?? MARKER_COLORS.yellow;
    const x1 = this.timeToX(pending.start, width);
    ctx.save();
    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 4]);
    if (pending.kind === "moment") {
      ctx.beginPath();
      ctx.moveTo(x1, y + 3);
      ctx.lineTo(x1, y + ROW_HEIGHT - 3);
      ctx.stroke();
    } else {
      const x2 = this.timeToX(pending.end, width);
      ctx.globalAlpha = 0.25;
      ctx.fillRect(x1, y + 6, Math.max(2, x2 - x1), ROW_HEIGHT - 12);
      ctx.globalAlpha = 1;
      ctx.strokeRect(x1, y + 6, Math.max(2, x2 - x1), ROW_HEIGHT - 12);
    }
    ctx.restore();
  }

  drawItem(ctx, item, y, width, rowH = ROW_HEIGHT) {
    const x1 = this.timeToX(item.start, width);
    if (item.kind === "moment") {
      if (x1 < -8 || x1 > width + 8) return;
      const color = MARKER_COLORS[item.color] ?? MARKER_COLORS.yellow;
      ctx.fillStyle = color;
      ctx.fillRect(Math.round(x1) - 1, y + 4, 2, rowH - 8);
      ctx.beginPath();
      ctx.moveTo(x1 - 6, y + 4);
      ctx.lineTo(x1 + 6, y + 4);
      ctx.lineTo(x1, y + 11);
      ctx.closePath();
      ctx.fill();
      return;
    }
    const x2 = this.timeToX(item.end, width);
    if (x2 < 0 || x1 > width) return;
    const left = Math.max(x1, -2);
    const right = Math.min(x2, width + 2);
    const top = y + 7;
    const h = rowH - 14;
    if (item.kind === "clip") {
      const color = clipColor(item.color);
      ctx.fillStyle = color;
      roundRect(ctx, left, top, Math.max(2, right - left), h, 3);
      ctx.globalAlpha = item.long ? 0.35 : 0.92;
      ctx.fill();
      ctx.globalAlpha = 1;
      if (item.long) {
        ctx.strokeStyle = color;
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    } else {
      // segments: soft diagonal bands with a solid bar at each end (the "brackets")
      const color = MARKER_COLORS[item.color] ?? MARKER_COLORS.yellow;
      ctx.save();
      roundRect(ctx, left, top, Math.max(2, right - left), h, 3);
      ctx.clip();
      ctx.fillStyle = hatch(ctx, color);
      ctx.fillRect(left, top, Math.max(2, right - left), h);
      ctx.fillStyle = color;
      if (x1 >= -4) ctx.fillRect(x1, top, Math.min(3, x2 - x1), h);
      if (x2 <= width + 4) ctx.fillRect(x2 - Math.min(3, x2 - x1), top, Math.min(3, x2 - x1), h);
      ctx.restore();
    }
    // cut-out parts: darker (they're left out of files)
    for (const cut of item.cuts ?? []) {
      const c1 = Math.max(this.timeToX(cut.start, width), left);
      const c2 = Math.min(this.timeToX(cut.end, width), right);
      if (c2 <= c1) continue;
      ctx.fillStyle = "rgb(16 17 20 / 0.7)";
      ctx.fillRect(c1, top, c2 - c1, h);
    }
    // A number in a pill before the title: the part's place in the video (Player 2), its place in a video (Player 1), or the
    // order the link tool picked it in (white, like the link tray's chips)
    const linkIndex = this.linking ? this.linking.ids.indexOf(item.id) : -1;
    const number = linkIndex >= 0 ? linkIndex + 1 : item.videoIndex ?? item.videos?.[0]?.position ?? null;
    let textLeft = Math.max(left, 0) + 7;
    if (number != null && right - left > 22) {
      const strong = linkIndex >= 0;
      const text = String(number);
      ctx.save();
      ctx.font = "700 11px Barlow, system-ui, sans-serif";
      const w = Math.max(16, ctx.measureText(text).width + 8);
      const bh = Math.min(16, h - 4);
      const bx = textLeft - 1;
      const by = top + (h - bh) / 2;
      roundRect(ctx, bx, by, w, bh, bh / 2);
      ctx.fillStyle = strong ? "#fff" : "rgb(255 255 255 / 0.82)";
      ctx.shadowColor = "rgb(0 0 0 / 0.5)";
      ctx.shadowBlur = 2;
      ctx.fill();
      ctx.shadowBlur = 0;
      if (strong) {
        ctx.lineWidth = 2;
        ctx.strokeStyle = "#8b7cf6";
        ctx.stroke();
      }
      ctx.fillStyle = "#141518";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(text, bx + w / 2, by + bh / 2 + 0.5);
      ctx.restore();
      textLeft = bx + w + 5;
    }
    // title inside when there's room
    const room = right - textLeft - 5;
    if (item.title && room > 24) {
      ctx.font = "600 12px Barlow, system-ui, sans-serif";
      ctx.textBaseline = "middle";
      ctx.fillStyle = "#fff";
      ctx.save();
      ctx.beginPath();
      ctx.rect(textLeft, top, room, h);
      ctx.clip();
      ctx.shadowColor = "rgb(0 0 0 / 0.85)";
      ctx.shadowBlur = 3;
      ctx.fillText(item.title, textLeft, top + h / 2 + 0.5);
      ctx.restore();
    }
  }

  drawPlayhead(ctx, width, height, now, withHandle) {
    if (now == null) return;
    const x = Math.round(this.timeToX(now, width)) + 0.5;
    if (x < 0 || x > width) return;
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
    if (withHandle) {
      ctx.fillStyle = "#fff";
      ctx.beginPath();
      ctx.moveTo(x - 6, 0);
      ctx.lineTo(x + 6, 0);
      ctx.lineTo(x, 8);
      ctx.closePath();
      ctx.fill();
    }
  }
}

// A gap the focused row skips
function drawSkip(ctx, x1, x2, y, h, color) {
  const w = x2 - x1;
  if (w < 3) return;
  ctx.save();
  ctx.strokeStyle = color;
  ctx.setLineDash([4, 3]);
  ctx.lineWidth = 1;
  ctx.strokeRect(x1 + 1.5, y + 6.5, w - 3, h - 13);
  if (w > 40) {
    ctx.fillStyle = color;
    ctx.font = "11px Barlow, system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText("skip", x1 + w / 2, y + h / 2);
  }
  ctx.restore();
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.roundRect ? ctx.roundRect(x, y, w, h, r) : ctx.rect(x, y, w, h);
}

const hatches = new Map();
function hatch(ctx, color) {
  if (hatches.has(color)) return hatches.get(color);
  // calm diagonal bands (light, then lighter) so the title on top stays readable
  const tile = document.createElement("canvas");
  tile.width = tile.height = 12;
  const t = tile.getContext("2d");
  t.fillStyle = color;
  t.globalAlpha = 0.12;
  t.fillRect(0, 0, 12, 12);
  t.globalAlpha = 0.24;
  t.strokeStyle = color;
  t.lineWidth = 4.2;
  t.beginPath();
  for (const k of [-12, 0, 12]) {
    t.moveTo(k - 2, 14);
    t.lineTo(k + 14, -2);
  }
  t.stroke();
  const pattern = ctx.createPattern(tile, "repeat");
  hatches.set(color, pattern);
  return pattern;
}

Object.assign(Timeline.prototype, editing);
