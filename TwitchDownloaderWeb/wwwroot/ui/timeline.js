// The timeline under the players (frontend rework steps 3 and 4). There are two, one per player, sharing the same panel
// (the Player 1 / Player 2 tabs show one at a time). Player 1's (mode "vod"): every member's rows on the loaded VOD, from
// GET /api/vods/{id}/timeline. Player 2's (mode "item"): one segment or clip on its own row, the view locked to a window
// around it; trimming it there changes the item itself, so Player 1's timeline moves too. A video in Player 2 (step 6) is
// shown in "video time": its segments end to end in play order, whatever their places in the VOD (videoItems()). A row belongs to one member and one VOD: video rows hold segments (hatched boxes) and
// moment markers (lines), clip rows hold clips (solid bars). Everyone can see and play every row; only the owner edits
// it (editing comes in step 3b). Hide / solo / lock are page-only, per row.
//
// Drawing: one canvas for the ruler (with Twitch's muted parts in red and saved clean audio in green under the ticks)
// and one for the rows; row headers are DOM next to it so they can hold buttons. Times are seconds into the VOD.

import { $, el, api, me, can, toast, formatDuration, withTooltip } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";
import { MARKER_COLORS } from "./colors.js";
import { formatClock } from "./media.js";
import { editing } from "./timeline-edit.js";
import { isDragging } from "./drag.js";

const ROW_HEIGHT = 36;
const MIN_SPAN = 10;
const REFRESH_MS = 5000;
const CLIP_COLOR = "#8b7cf6";
const MUTED_COLOR = "rgb(240 82 82 / 0.5)";
const CLEAN_COLOR = "rgb(60 207 122 / 0.5)";
const TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];

// Player 2's window around its item: a quarter of its length on each side, 5 s to 60 s
function windowMargin(length) {
  return Math.min(Math.max(length * 0.25, 5), 60);
}

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
    this.window = null;     // "item" mode: { start, end } the view is locked to

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
    this.resetEditing();
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
        this.applyItem(timeline);
      } else {
        const rowsChanged = JSON.stringify(timeline.rows) !== JSON.stringify(this.rows);
        this.rows = timeline.rows ?? [];
        this.markers = timeline.markers ?? [];
        this.clips = timeline.clips ?? [];
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

  // "item" mode: keep only Player 2's item and its row
  applyItem(timeline) {
    const focus = this.focus;
    if (!focus) return;
    if (focus.type === "video") {
      this.applyVideo(timeline);
      return;
    }
    const found = focus.type === "clip"
      ? (timeline.clips ?? []).find((c) => c.id === focus.id)
      : (timeline.markers ?? []).find((m) => m.id === focus.id);
    if (!found) {
      this.markers = [];
      this.clips = [];
      this.hooks.onFocusGone?.(null);
      return;
    }
    const row = (timeline.rows ?? []).find((r) => r.id === found.rowId) ?? {
      id: 0,
      userId: focus.type === "clip" ? Number(found.addedByUserId) : found.createdByUserId,
      username: found.createdBy,
      kind: focus.type === "clip" ? "Clip" : "Video",
      position: 0,
      name: null,
      canEdit: false,
    };
    const rowsChanged = JSON.stringify([row]) !== JSON.stringify(this.rows);
    this.rows = [row];
    this.markers = focus.type === "marker" ? [found] : [];
    this.clips = focus.type === "clip" ? [found] : [];
    this.videos = [];
    if (rowsChanged) {
      this.renderHeads();
      this.renderFooter();
    }
    this.updateWindow();
    this.hooks.onFocusChanged?.(this.focusItem());
  }

  // A video: its row, and its segments' markers (for who may edit them)
  applyVideo(timeline) {
    const video = (timeline.videos ?? []).find((v) => v.id === this.focus.id);
    if (!video || !video.segments?.length) {
      this.markers = [];
      this.video = null;
      this.hooks.onFocusGone?.(null);
      return;
    }
    const ids = new Set(video.segments.map((s) => s.markerId));
    const row = (timeline.rows ?? []).find((r) => r.id === video.rowId) ?? {
      id: video.rowId, userId: video.userId, username: video.createdBy, kind: "Video", position: 0, name: null, canEdit: false,
    };
    const rowsChanged = JSON.stringify([row]) !== JSON.stringify(this.rows);
    this.rows = [row];
    this.video = video;
    this.markers = (timeline.markers ?? []).filter((m) => ids.has(m.id));
    this.clips = [];
    this.videos = [video];
    if (rowsChanged) {
      this.renderHeads();
      this.renderFooter();
    }
    this.updateWindow();
    this.hooks.onFocusChanged?.(this.focusInfo());
  }

  isVideoLayout() {
    return this.mode === "item" && this.focus?.type === "video";
  }

  // A video's segments end to end: start/end are video time; offset = where it starts in the video, vodStart/vodEnd its
  // place in the VOD. toVod() turns a video time back into VOD seconds.
  videoItems(row) {
    const video = this.video;
    if (!video) return [];
    const preview = this.preview;
    let offset = 0;
    return [...video.segments].sort((a, b) => a.position - b.position).map((segment, index) => {
      const marker = this.markers.find((m) => m.id === segment.markerId);
      const vodStart = marker?.seconds ?? segment.startSeconds;
      const vodEnd = marker?.endSeconds ?? segment.endSeconds;
      const length = vodEnd - vodStart;
      const shift = (cut) => ({ start: offset + cut.start - vodStart, end: offset + cut.end - vodStart });
      const ownCutsVod = marker?.cuts ?? segment.cuts ?? [];
      const videoCutsVod = segment.videoCuts ?? [];
      let item = {
        kind: "segment", id: segment.markerId, start: offset, end: offset + length, offset, vodStart, vodEnd,
        title: marker?.label ?? segment.label, color: marker?.color ?? segment.color, by: marker?.createdBy ?? video.createdBy,
        cuts: [...ownCutsVod, ...videoCutsVod].map(shift), ownCutsVod, videoCutsVod, row, videoIndex: index + 1,
        data: marker ?? { id: segment.markerId, seconds: vodStart, endSeconds: vodEnd, label: segment.label, color: segment.color, canEdit: false, canDelete: false },
      };
      if (preview && preview.type === "marker" && preview.id === item.id) item = { ...item, start: preview.start, end: preview.end };
      offset += length;
      return item;
    });
  }

  // The video's parts in play order, in VOD seconds (Player 2 plays them one after the other)
  videoParts() {
    return this.isVideoLayout() ? this.videoItems(this.rows[0]).map((item) => ({
      id: item.id, offset: item.offset, vodStart: item.vodStart, vodEnd: item.vodEnd, cuts: [...item.ownCutsVod, ...item.videoCutsVod],
    })) : [];
  }

  // Player 2's item as drawn (with an edge being dragged or a trim waiting to be saved)
  focusItem() {
    if (this.mode !== "item" || !this.rows.length || this.isVideoLayout()) return null;
    return this.itemsOf(this.rows[0])[0] ?? null;
  }

  // Player 2's header: { title, length } (length = what's left after cut-out parts)
  focusInfo() {
    if (this.isVideoLayout()) return this.video ? { title: this.video.title, length: this.video.lengthSeconds } : null;
    const item = this.focusItem();
    if (!item) return null;
    let length = null;
    if (item.end != null) {
      const cut = (item.cuts ?? []).reduce((sum, c) => sum + Math.max(0, Math.min(c.end, item.end) - Math.max(c.start, item.start)), 0);
      length = item.end - item.start - cut;
    }
    return { title: item.title, length, start: item.start, end: item.end };
  }

  // The window around Player 2's item. It only moves when the item comes close to its edges or gets much shorter, so
  // trimming doesn't make the view jump.
  updateWindow() {
    if (this.isVideoLayout()) {
      // A video shows whole: from its start to its end
      const old = this.window;
      const fitted = !old || (Math.abs(this.view.start - old.start) < 0.01 && Math.abs(this.view.end - old.end) < 0.01);
      this.window = { start: 0, end: Math.max(this.length(), 0.5) };
      if (fitted || this.view.end > this.window.end) this.setView(0, this.window.end);
      return;
    }
    const item = this.focusItem();
    if (!item) return;
    const end = item.end ?? item.start;
    const margin = windowMargin(end - item.start);
    const want = { start: Math.max(0, item.start - margin), end: Math.min(this.length(), end + margin) };
    const old = this.window;
    const tooTight = old && ((item.start < old.start + 1 && old.start > 0) || (end > old.end - 1 && old.end < this.length()));
    const tooWide = old && old.end - old.start > 3 * (want.end - want.start);
    if (old && !tooTight && !tooWide) return;
    const fitted = !old || (Math.abs(this.view.start - old.start) < 0.01 && Math.abs(this.view.end - old.end) < 0.01);
    this.window = want;
    if (fitted || this.view.start < want.start || this.view.end > want.end) this.setView(want.start, want.end);
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
    this.audio = this.isVideoLayout() ? this.videoAudio(ranges) : ranges;
    const fragments = details?.fragments ?? [];
    const last = fragments[fragments.length - 1];
    const before = this.length();
    this.mediaLength = last ? last.start + last.duration : 0;
    // First playlist: show the whole VOD
    if (this.mode === "item") this.updateWindow();
    else if (before <= MIN_SPAN || (this.view.end > before - 0.5 && this.view.start <= 0)) this.view.end = Math.max(this.view.end, Math.min(this.length(), this.view.start + this.span()));
    this.dirty = true;
  }

  // Muted / saved-audio ranges of the VOD, moved into a video's time
  videoAudio(ranges) {
    const out = [];
    for (const item of this.videoItems(this.rows[0])) {
      for (const range of ranges) {
        const start = Math.max(range.start, item.vodStart);
        const end = Math.min(range.end, item.vodEnd);
        if (end > start) out.push({ start: item.offset + start - item.vodStart, end: item.offset + end - item.vodStart, clean: range.clean });
      }
    }
    return out;
  }

  // The VOD's length (known so far)
  vodLength() {
    return Math.max(this.knownLength, this.mediaLength, MIN_SPAN);
  }

  // What the timeline spans: the VOD, or a video's segments end to end
  length() {
    if (this.isVideoLayout()) {
      const items = this.videoItems(this.rows[0]);
      return items.length ? items[items.length - 1].offset + (items[items.length - 1].vodEnd - items[items.length - 1].vodStart) : 0;
    }
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
    const name = row.name || (row.kind === "Clip" || row.kind === "clip" ? "Clips" : "Video");
    const sameKind = this.rows.filter((r) => r.userId === row.userId && r.kind === row.kind);
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
    const layoutHead = this.hasLayoutRow()
      ? el("div", { class: "row-head layout-head", style: `height:${ROW_HEIGHT}px` },
        el("span", { class: "row-check-gap" }),
        el("div", { class: "row-name" },
          el("sl-icon", { library: "ph", name: "device-mobile", class: "row-kind" }),
          el("span", { class: "row-title" }, "Layout"),
          el("span", { class: "row-owner" }, "vertical")))
      : null;
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
      const name = el("div", { class: "row-name" },
        el("sl-icon", { library: "ph", name: isClip ? "film-strip" : "brackets-square", class: "row-kind" }),
        el("span", { class: "row-title", title: row.canEdit ? "Double-click to rename" : "" }, this.rowTitle(row)),
        el("span", { class: "row-owner" + (own ? " own" : "") }, own ? "you" : row.username));
      if (row.canEdit) name.addEventListener("dblclick", () => this.renameRow(row));
      const check = !item && this.canDeleteRow(row)
        ? el("sl-checkbox", { size: "small", class: "row-check", checked: state.toggled, "aria-label": "Select row to delete" })
        : el("span", { class: "row-check-gap" });
      check.addEventListener?.("sl-change", (event) => {
        state.toggled = event.target.checked;
        this.renderFooter();
      });
      return el("div", { class: "row-head" + (own ? " own" : "") + (index % 2 ? " odd" : ""), style: `height:${ROW_HEIGHT}px` },
        check,
        name,
        el("div", { class: "row-btns" },
          item ? null : toggle("hidden", state.hidden ? "Show row" : "Hide row", "eye", "eye-slash"),
          item ? null : toggle("solo", "Solo (only this row)", "headphones", "headphones"),
          row.canEdit ? toggle("locked", state.locked ? "Unlock row" : "Lock row", "lock-simple-open", "lock-simple") : null));
    }));
    if (layoutHead) heads.append(layoutHead);
    this.dirty = true;
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
    on("tl-delete-rows", "click", () => this.deleteToggledRows());
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
    $("tl-delete-rows").disabled = !rows || !this.rows.some((row) => this.stateOf(row).toggled);
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
    const rows = this.rows.filter((row) => this.stateOf(row).toggled);
    if (!rows.length) return;
    const items = rows.reduce((n, row) => n + this.markers.filter((m) => m.rowId === row.id).length, 0);
    const ok = await confirmAction({
      heading: rows.length === 1 ? "Delete this row?" : `Delete ${rows.length} rows?`,
      text: items
        ? `The ${items} segment${items === 1 ? "" : "s"} and marker${items === 1 ? "" : "s"} on ${rows.length === 1 ? "it" : "them"} are deleted too.`
        : "They're empty.",
      confirm: "Delete",
    });
    if (!ok) return;
    for (const row of rows) {
      try {
        await api("DELETE", `/api/rows/${row.id}`);
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
        this.layoutDrag = null;
        this.snapAt = null;
        this.edgeTip = null;
        this.dirty = true;
      });
      canvas.addEventListener("pointerleave", () => {
        this.hover = null;
        this.renderTip();
      });
      canvas.addEventListener("contextmenu", (event) => this.contextMenu(event, canvas));
      canvas.addEventListener("dblclick", (event) => this.doubleClick(event, canvas));
      canvas.addEventListener("wheel", (event) => {
        if (!this.vodId) return;
        event.preventDefault();
        const rect = canvas.getBoundingClientRect();
        if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
          const delta = (event.shiftKey ? event.deltaY : event.deltaX) / rect.width * this.span();
          this.follow = false;
          this.setView(this.view.start + delta, this.view.end + delta);
        } else {
          this.follow = false;
          this.zoomBy(event.deltaY > 0 ? 1.25 : 0.8, this.xToTime(event.clientX - rect.left, rect.width));
        }
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
    const selected = items.find((item) => this.isSelected(item));
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
    if (this.isVideoLayout()) return this.videoItems(row);
    const preview = this.preview ?? (this.pendingTrim ? { type: "clip", ...this.pendingTrim } : null);
    const moved = (type, item) => (preview && preview.type === type && preview.id === item.id
      ? { ...item, start: preview.start, end: preview.end, ...(preview.cuts !== undefined ? { cuts: preview.cuts ?? [] } : {}) }
      : item);
    if (/clip/i.test(row.kind)) {
      return this.clips.filter((c) => c.rowId === row.id).map((c) => moved("clip", {
        kind: "clip", id: c.id, start: c.startSeconds ?? c.start, end: c.endSeconds ?? c.end ?? this.length(), title: c.title,
        by: c.createdBy, reference: c.referenceOnly, cuts: c.cuts ?? [], row, data: c,
      }));
    }
    return this.markers.filter((m) => m.rowId === row.id).map((m) => moved("marker", {
      kind: m.endSeconds == null ? "moment" : "segment", id: m.id, start: m.seconds, end: m.endSeconds, title: m.label,
      color: m.color, by: m.createdBy, cuts: m.cuts ?? [], row, data: m, videos: this.videosOf(m.id),
    }));
  }

  // Player 1: the videos a segment is linked into, [{ title, position, count }]
  videosOf(markerId) {
    if (this.mode !== "vod") return [];
    const out = [];
    for (const video of this.videos) {
      const segments = [...(video.segments ?? [])].sort((a, b) => a.position - b.position);
      const index = segments.findIndex((s) => s.markerId === markerId);
      if (index >= 0) out.push({ id: video.id, title: video.title, position: index + 1, count: segments.length });
    }
    return out;
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
    const part = hover.canvas === this.track ? this.layoutPartAt(hover.x, hover.y, hover.width) : null;
    if (part) {
      lines.push(`Part ${part.number} · ${part.removed ? "left out of the render" : part.label}`);
      if (part.keys?.length) lines.push(`${part.keys.length} keyframe${part.keys.length === 1 ? "" : "s"} (moving boxes) · click a diamond to go to it · F adds or removes one`);
      lines.push("Drag a split line to move it · drag a part to slide it");
      lines.push("S splits at the playhead · Delete leaves a part out · J joins");
    }
    if (item) {
      const range = item.end != null ? `${formatDuration(item.start, { short: false })} – ${formatDuration(item.end, { short: false })}` : `at ${formatDuration(item.start, { short: false })}`;
      lines.push(`${item.title || { clip: "Clip", segment: "Segment", moment: "Marker" }[item.kind]} · ${range}`);
      const cut = (item.cuts ?? []).find((c) => t >= c.start && t < c.end);
      if (cut) lines.push(`Cut out ${formatDuration(cut.start, { short: false })} – ${formatDuration(cut.end, { short: false })} · left out of files`);
      if (item.videoIndex) lines.push(`Part ${item.videoIndex} · VOD ${formatDuration(item.vodStart, { short: false })} – ${formatDuration(item.vodEnd, { short: false })}`);
      for (const video of item.videos ?? []) lines.push(`In video "${video.title}" (${video.position} of ${video.count})`);
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

  drawTrack(now) {
    const rowCount = this.rows.length + (this.hasLayoutRow() ? 1 : 0);
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
      ctx.globalAlpha = anySolo && !state.solo ? 0.25 : state.locked ? 0.7 : 1;
      const items = this.itemsOf(row);
      // the selected item on top
      const selected = items.find((item) => this.isSelected(item));
      for (const item of items) if (item !== selected) this.drawItem(ctx, item, y, width);
      if (selected) {
        this.drawItem(ctx, selected, y, width);
        this.drawSelection(ctx, selected, y, width);
      }
      ctx.globalAlpha = 1;
    });
    if (this.hasLayoutRow()) {
      const y = this.rows.length * ROW_HEIGHT;
      ctx.fillStyle = css.getPropertyValue("--line");
      ctx.fillRect(0, y + ROW_HEIGHT - 1, width, 1);
      this.drawVerticalParts(ctx, width, y);
    }
    this.drawPending(ctx, width, now);
    if (this.cutDrag) this.drawCutDrag(ctx, width);
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
    const focus = this.focusItem();
    if (focus) {
      // Player 2: outside the item is darker (playback stays inside it)
      ctx.fillStyle = "rgb(0 0 0 / 0.35)";
      const x1 = this.timeToX(focus.start, width);
      const x2 = this.timeToX(focus.end ?? focus.start, width);
      if (x1 > 0) ctx.fillRect(0, 0, x1, height);
      if (x2 < width) ctx.fillRect(x2, 0, width - x2, height);
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

  // The vertical editor's parts (VOD seconds), drawn on the clip in Player 2's row: a white line at each split, a label per part
  // ("2 · Two boxes"), the part under the playhead brighter with a white bar, parts left out dark and hatched, and outside
  // the render's trim darker
  setVerticalParts(info) {
    const had = this.hasLayoutRow();
    this.verticalParts = info;
    if (had !== this.hasLayoutRow()) this.renderHeads();
    this.dirty = true;
  }

  // Player 2 with the vertical editor open: a "Layout" row under the clip with the editor's parts
  hasLayoutRow() {
    return Boolean(this.verticalParts && this.mode === "item" && this.rows.length);
  }

  // The vertical part under x in the layout row, or null
  layoutPartAt(x, y, width) {
    if (!this.hasLayoutRow() || Math.floor(y / ROW_HEIGHT) !== this.rows.length) return null;
    const t = this.xToTime(x, width);
    const index = this.verticalParts.parts.findIndex((part) => t >= part.start && t < part.end);
    return index < 0 ? null : { ...this.verticalParts.parts[index], number: index + 1 };
  }

  drawVerticalParts(ctx, width, y) {
    const { trim } = this.verticalParts;
    // While a split line is dragged, the parts are drawn where they'd end up
    const edges = this.layoutDrag?.edges;
    const parts = edges
      ? this.verticalParts.parts.map((part, i) => ({ ...part, start: edges[i], end: edges[i + 1] }))
      : this.verticalParts.parts;
    ctx.fillStyle = "rgb(139 124 246 / 0.06)";
    ctx.fillRect(0, y, width, ROW_HEIGHT - 1);
    const top = y + 6;
    const h = ROW_HEIGHT - 12;
    const x = (t) => this.timeToX(t, width);
    const now = this.getPosition();
    ctx.save();
    parts.forEach((part, i) => {
      const x1 = x(part.start);
      const x2 = x(part.end);
      if (x2 < 0 || x1 > width) return;
      const w = Math.max(1, x2 - x1);
      if (part.removed) {
        ctx.fillStyle = "rgb(16 17 20 / 0.78)";
        ctx.fillRect(x1, top, w, h);
        ctx.fillStyle = hatch(ctx, "#6b6f78");
        ctx.fillRect(x1, top, w, h);
      } else {
        ctx.fillStyle = i % 2 ? "rgb(96 165 250 / 0.55)" : "rgb(56 130 230 / 0.55)";
        ctx.fillRect(x1, top, w, h);
      }
      if (part.current) {
        ctx.fillStyle = "rgb(255 255 255 / 0.14)";
        ctx.fillRect(x1, top, w, h);
        ctx.fillStyle = "#fff";
        ctx.fillRect(x1, top + h - 3, w, 3);
      }
      if (i > 0) {
        // the split: a white line over the whole row with a small notch on top
        ctx.fillStyle = "#fff";
        ctx.fillRect(Math.round(x1) - 1, y + 1, 2, ROW_HEIGHT - 2);
        ctx.beginPath();
        ctx.moveTo(x1 - 5, y);
        ctx.lineTo(x1 + 5, y);
        ctx.lineTo(x1, y + 6);
        ctx.closePath();
        ctx.fill();
      }
      // keyframes of moving boxes: small diamonds along the bottom, the one at the playhead filled
      for (const key of part.keys ?? []) {
        const kx = x(key);
        if (kx < -6 || kx > width + 6) continue;
        const on = now != null && Math.abs(now - key) < 0.06;
        ctx.beginPath();
        ctx.moveTo(kx, top + h - 11);
        ctx.lineTo(kx + 5, top + h - 6);
        ctx.lineTo(kx, top + h - 1);
        ctx.lineTo(kx - 5, top + h - 6);
        ctx.closePath();
        ctx.fillStyle = on ? "#fff" : "#1b1d22";
        ctx.fill();
        ctx.strokeStyle = "#fff";
        ctx.lineWidth = 1.5;
        ctx.stroke();
      }
      const text = `${i + 1} · ${part.removed ? "Left out" : part.label}`;
      ctx.font = "600 11px Barlow, system-ui, sans-serif";
      const room = w - 10;
      if (room < 14) return;
      const shown = ctx.measureText(text).width <= room ? text : String(i + 1);
      ctx.save();
      ctx.beginPath();
      ctx.rect(x1 + 4, top, room, h);
      ctx.clip();
      ctx.fillStyle = part.removed ? "#b8bcc6" : "#fff";
      ctx.textBaseline = "middle";
      ctx.shadowColor = "rgb(0 0 0 / 0.9)";
      ctx.shadowBlur = 3;
      ctx.fillText(shown, Math.max(x1, 0) + 6, top + h / 2 - 1);
      ctx.restore();
    });
    if (trim && parts.length) {
      ctx.fillStyle = "rgb(0 0 0 / 0.55)";
      const start = x(parts[0].start);
      const end = x(parts[parts.length - 1].end);
      const t1 = x(trim.start);
      const t2 = x(trim.end);
      if (t1 > start + 0.5) ctx.fillRect(start, top, t1 - start, h);
      if (t2 < end - 0.5) ctx.fillRect(t2, top, end - t2, h);
    }
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
    ctx.strokeRect(x1, y + 4, Math.max(2, x2 - x1), ROW_HEIGHT - 8);
    if (this.canMoveEdges(item)) {
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

  // A segment or clip being marked (S / X pressed once: start to playhead) and the item the title editor is naming
  drawPending(ctx, width, now) {
    let pending = null;
    if (this.draft) pending = this.draft;
    else if (this.marking && now != null) {
      let end = now;
      if (this.marking.kind === "clip") end = Math.min(Math.max(end, this.marking.start - this.maxStoredClipSeconds), this.marking.start + this.maxStoredClipSeconds);
      pending = { kind: this.marking.kind, start: Math.min(this.marking.start, end), end: Math.max(this.marking.start, end), color: this.lastColor };
    }
    if (!pending) return;
    const row = this.targetRow(pending.kind === "clip" ? "clip" : "video");
    const index = row ? this.rows.indexOf(row) : -1;
    if (index < 0 || this.stateOf(row).hidden) return; // no own row yet: the server makes one when it's saved
    const y = index * ROW_HEIGHT;
    const color = pending.kind === "clip" ? CLIP_COLOR : MARKER_COLORS[pending.color] ?? MARKER_COLORS.yellow;
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

  drawItem(ctx, item, y, width) {
    const x1 = this.timeToX(item.start, width);
    if (item.kind === "moment") {
      if (x1 < -8 || x1 > width + 8) return;
      const color = MARKER_COLORS[item.color] ?? MARKER_COLORS.yellow;
      ctx.fillStyle = color;
      ctx.fillRect(Math.round(x1) - 1, y + 4, 2, ROW_HEIGHT - 8);
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
    const top = y + 6;
    const h = ROW_HEIGHT - 12;
    if (item.kind === "clip") {
      ctx.fillStyle = item.reference ? "rgb(139 124 246 / 0.35)" : CLIP_COLOR;
      roundRect(ctx, left, top, Math.max(2, right - left), h, 4);
      ctx.fill();
      if (item.reference) {
        ctx.strokeStyle = CLIP_COLOR;
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    } else {
      const color = MARKER_COLORS[item.color] ?? MARKER_COLORS.yellow;
      ctx.fillStyle = hatch(ctx, color);
      ctx.fillRect(left, top, Math.max(2, right - left), h);
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      // brackets at both ends
      for (const [x, dir] of [[x1, 1], [x2, -1]]) {
        if (x < -4 || x > width + 4) continue;
        ctx.beginPath();
        ctx.moveTo(x + 5 * dir, top);
        ctx.lineTo(x, top);
        ctx.lineTo(x, top + h);
        ctx.lineTo(x + 5 * dir, top + h);
        ctx.stroke();
      }
    }
    // cut-out parts: darker (they're left out of files)
    for (const cut of item.cuts ?? []) {
      const c1 = Math.max(this.timeToX(cut.start, width), left);
      const c2 = Math.min(this.timeToX(cut.end, width), right);
      if (c2 <= c1) continue;
      ctx.fillStyle = "rgb(16 17 20 / 0.7)";
      ctx.fillRect(c1, top, c2 - c1, h);
    }
    // title inside when there's room
    const room = right - left - 12;
    if (item.title && room > 30) {
      ctx.font = "600 12px Barlow, system-ui, sans-serif";
      ctx.textBaseline = "middle";
      ctx.fillStyle = "#fff";
      ctx.save();
      ctx.beginPath();
      ctx.rect(left + 6, top, room, h);
      ctx.clip();
      ctx.shadowColor = "rgb(0 0 0 / 0.8)";
      ctx.shadowBlur = 3;
      ctx.fillText(item.title, Math.max(left, 0) + 6, top + h / 2);
      ctx.restore();
    }
    // A number in a circle: the part's place in the video (Player 2), its place in a video (Player 1), or the order
    // the link tool picked it in
    const linkIndex = this.linking ? this.linking.ids.indexOf(item.id) : -1;
    const number = linkIndex >= 0 ? linkIndex + 1 : item.videoIndex ?? item.videos?.[0]?.position ?? null;
    if (number != null && x2 - x1 > 14) {
      const strong = linkIndex >= 0;
      const cx = Math.max(x1, -2) + 9;
      const cy = top;
      ctx.save();
      ctx.beginPath();
      ctx.arc(cx, cy, strong ? 8 : 6.5, 0, Math.PI * 2);
      ctx.fillStyle = strong ? "#8b7cf6" : "rgb(16 17 20 / 0.9)";
      ctx.fill();
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = strong ? "#fff" : "#8b7cf6";
      ctx.stroke();
      ctx.fillStyle = "#fff";
      ctx.font = `700 ${strong ? 11 : 9}px Barlow, system-ui, sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(String(number), cx, cy + 0.5);
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

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.roundRect ? ctx.roundRect(x, y, w, h, r) : ctx.rect(x, y, w, h);
}

const hatches = new Map();
function hatch(ctx, color) {
  if (hatches.has(color)) return hatches.get(color);
  const tile = document.createElement("canvas");
  tile.width = tile.height = 8;
  const t = tile.getContext("2d");
  t.fillStyle = color;
  t.globalAlpha = 0.22;
  t.fillRect(0, 0, 8, 8);
  t.globalAlpha = 0.75;
  t.strokeStyle = color;
  t.lineWidth = 1.5;
  t.beginPath();
  t.moveTo(-2, 10);
  t.lineTo(10, -2);
  t.moveTo(-2, 2);
  t.lineTo(2, -2);
  t.moveTo(6, 10);
  t.lineTo(10, 6);
  t.stroke();
  const pattern = ctx.createPattern(tile, "repeat");
  hatches.set(color, pattern);
  return pattern;
}

Object.assign(Timeline.prototype, editing);
