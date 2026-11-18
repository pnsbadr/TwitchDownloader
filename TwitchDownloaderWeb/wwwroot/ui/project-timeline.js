// Player 2's timeline: the open project (ui/project.js) as a multi-track editor, like a sequence in Premiere. It starts at 0:00
// and always runs a minute past the last piece, so there's room to scroll and drop more. Rows (tracks) are listed top to
// bottom; where rows overlap the higher one plays. It shares the timeline panel with Player 1's (the Player 1 / Player 2 tabs).
//
// Pointer tool: click a piece to select it (Shift adds), drag it along its row or onto another (Shift keeps its time), drag
// its edges to trim it, drag on empty space to select with a box, click empty space to move the playhead. Cut tool (razor):
// click a piece to split it. Zoom and Hand tools as on Player 1. Wheel scrolls, Ctrl+wheel zooms. Cards and Player 1 items
// dropped on a row land where they're dropped (a ghost shows it); dropped under the last row they make a new row.
// Every change is one undo step of the store, which saves the project shortly after.

import { timelineCursor, rulerCursor, timelineWheel } from "./timeline-input.js";
import { $, el, api, toast, formatDuration, withTooltip, loadSetting, saveSetting } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";
import { MARKER_COLORS } from "./colors.js";
import { openMenu } from "./menu.js";
import { dropTarget } from "./drag.js";
import {
  MAX_TRACKS, MAX_PIECES, MIN_PIECE, MAX_END, PROJECT_KINDS, round, pieceEnd, pieceLength, newId, trackName, piecesOn, fits,
  nearestFit, flatten, piecesFromItem, splitLayout, projectLength, makeTrack,
} from "./project.js";

const ROW_HEIGHT = 40;
const FOCUS_ROW_HEIGHT = 64;    // the row that plays alone (row focus)
const FOLDED_ROW_HEIGHT = 16;   // the other rows meanwhile
const MIN_SPAN = 2;
const TAIL = 60;                // seconds of empty room after the last piece
const EDGE_PX = 7;
const DRAG_PX = 4;
const SNAP_PX = 8;
const SNAP_KEY = "clipLibrary.snap"; // shared with Player 1's timeline and the vertical editor
const TICK_STEPS = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

let clipboard = null;           // copied pieces (Ctrl+C), relative to the first one's time: [{ piece, trackOffset }]
let layoutClipboard = null;     // "Copy layout"
const vodLengths = new Map();   // vodId -> length in seconds (from /api/vods/info), for trims past the original item

export async function vodLength(vodId) {
  const key = String(vodId);
  if (vodLengths.has(key)) return vodLengths.get(key);
  vodLengths.set(key, null);
  try {
    const info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(key)}`);
    // A live VOD keeps growing: no limit until it's over
    vodLengths.set(key, info.isLive ? null : info.lengthSeconds || null);
  } catch {
    vodLengths.delete(key);
  }
  return vodLengths.get(key) ?? null;
}

// A stream ended (core.js clipco:stream): VODs that were live get their final length on the next look
export function forgetLiveVodLengths() {
  for (const [key, length] of vodLengths) if (length == null) vodLengths.delete(key);
}

// The play button on a row head: plays only that row, gaps skipped, with the rest of the editor dimmed (row focus).
// Shared with Player 1's timeline.
export function rowPlayButton(playing, onClick) {
  const label = playing ? "Stop playing this row" : "Play only this row (skips gaps)";
  const button = el("sl-icon-button", { library: "ph", name: playing ? "stop" : "play", label, class: "row-btn row-play" });
  if (playing) button.setAttribute("data-active", "");
  button.addEventListener("pointerdown", (event) => event.stopPropagation());
  button.addEventListener("click", (event) => {
    event.stopPropagation();
    onClick();
  });
  return withTooltip(button, label);
}

const colorOf = (piece) => MARKER_COLORS[piece.color] ?? MARKER_COLORS.violet ?? "#8b7cf6";

export class ProjectTimeline {
  // hooks: getPosition() → project seconds or null, onSeek(t), openItemEditor/closeItemEditor (tools bar title editor),
  // loadItem(item) → loads a dropped item when nothing is loaded, onChange() (selection, tool, history), onShowInPlayer1(piece),
  // onApplyToOriginal(pieces), onPieceDoubleClick?(piece)
  constructor(store, hooks) {
    this.store = store;
    this.hooks = hooks;
    this.ruler = el("canvas", { class: "tl-ruler-canvas", hidden: true });
    this.track = el("canvas", { class: "tl-track-canvas", hidden: true });
    this.heads = el("div", { class: "tl-heads-set", hidden: true });
    this.tip = el("div", { class: "tl-tip", hidden: true });
    this.scroll = el("div", { class: "pt-scroll", hidden: true }, el("div", { class: "pt-scroll-thumb" }));
    $("tl-ruler").append(this.ruler);
    $("tl-track").prepend(this.track);
    $("tl-heads").append(this.heads);
    document.querySelector(".tl-foot-left").after(this.scroll);
    document.body.append(this.tip);
    this.active = false;
    this.tool = "pointer";
    this.snap = loadSetting(SNAP_KEY, true) !== false;
    this.view = { start: 0, end: 60 };
    this.follow = true;
    this.selected = new Set();
    this.trackSel = null;       // the row picked last (row actions, paste)
    this.press = null;
    this.preview = null;        // pieces moved/trimmed while dragging: Map id -> { trackId, at, start, end }
    this.invalid = false;
    this.marquee = null;
    this.snapAt = null;
    this.razorAt = null;
    this.ghost = null;          // a card dragged over the rows: { pieces, trackIndex, newRow, ok }
    this.hover = null;
    this.dirty = true;
    this.bindPointer();
    this.bindFooter();
    this.bindScroll();
    this.bindDrop();
    new ResizeObserver(() => { this.dirty = true; }).observe($("tl-track"));
    new ResizeObserver(() => { this.dirty = true; }).observe($("tl-ruler"));
    const frame = () => {
      this.update();
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  }

  // Existing editor code checks `vodId` to see if a timeline has something on it
  get vodId() {
    return this.store.open ? `project:${this.store.project.id}` : null;
  }

  get content() {
    return this.store.content;
  }

  // ---------- state from the store ----------

  // The project was opened, changed, undone or replaced
  refresh({ reset = false } = {}) {
    if (reset) {
      this.selected.clear();
      this.trackSel = null;
      this.follow = true;
      // Opening a project starts fitted: the whole project from 0:00
      this.view = { start: 0, end: Math.max(this.length() * 1.04, 10) };
      for (const piece of this.content?.pieces ?? []) vodLength(piece.vodId);
    }
    const ids = new Set((this.content?.pieces ?? []).map((p) => p.id));
    for (const id of [...this.selected]) if (!ids.has(id)) this.selected.delete(id);
    if (this.trackSel && !this.content?.tracks.some((t) => t.id === this.trackSel)) this.trackSel = null;
    this.setView(this.view.start, this.view.end, true);
    this.renderHeads();
    this.renderFooter();
    this.dirty = true;
  }

  length() {
    return projectLength(this.content);
  }

  extent() {
    return { start: 0, end: Math.min(MAX_END, Math.max(this.length() + TAIL, 60)) };
  }

  span() {
    return this.view.end - this.view.start;
  }

  setActive(on) {
    this.active = on;
    this.heads.hidden = !on;
    this.ruler.hidden = !on;
    this.track.hidden = !on;
    this.scroll.hidden = !on;
    this.tip.hidden = true;
    this.hover = null;
    $("tl-add-clip").hidden = on;
    $("tl-add-video").label = on ? "Add a row" : "Insert video row";
    $("tl-delete-rows").label = on ? "Delete the selected row" : "Delete toggled rows";
    this.renderHeads();
    this.renderFooter();
    this.dirty = true;
  }

  pieceById(id) {
    return this.content?.pieces.find((p) => p.id === id) ?? null;
  }

  selectedPieces() {
    return (this.content?.pieces ?? []).filter((p) => this.selected.has(p.id));
  }

  trackIndex(trackId) {
    return this.content?.tracks.findIndex((t) => t.id === trackId) ?? -1;
  }

  trackOf(piece) {
    return this.content?.tracks.find((t) => t.id === piece.trackId) ?? null;
  }

  isLocked(trackId) {
    return Boolean(this.content?.tracks.find((t) => t.id === trackId)?.locked);
  }

  changed() {
    this.dirty = true;
    this.hooks.onChange?.();
  }

  select(ids, { add = false } = {}) {
    if (!add) this.selected.clear();
    for (const id of ids) this.selected.add(id);
    const first = this.pieceById(ids[0]);
    if (first) this.trackSel = first.trackId;
    this.renderHeads();
    this.changed();
  }

  // ---------- rows ----------

  renderHeads() {
    if (this.active) {
      $("tl-empty").hidden = true;
    }
    const content = this.content;
    if (!content) {
      this.heads.replaceChildren();
      if (this.active) {
        $("tl-empty").hidden = false;
        $("tl-empty").textContent = "Load a clip, video or segment in Player 2 (double-click its card, or pick it from the menu above Player 2).";
      }
      return;
    }
    const canEdit = this.store.canEdit;
    const geom = this.rowGeom();
    this.heads.replaceChildren(...content.tracks.map((track, index) => {
      const toggle = (key, labelOn, labelOff, iconOn, iconOff) => {
        const on = Boolean(track[key]);
        const button = el("sl-icon-button", { library: "ph", name: on ? iconOn : iconOff, label: on ? labelOn : labelOff, class: "row-btn", disabled: !canEdit });
        if (on) button.setAttribute("data-active", "");
        button.addEventListener("click", (event) => {
          event.stopPropagation();
          this.store.edit((c) => {
            const t = c.tracks.find((x) => x.id === track.id);
            t[key] = !t[key];
          });
        });
        return withTooltip(button, on ? labelOn : labelOff);
      };
      const head = el("div", {
        class: "row-head pt-head" + (index % 2 ? " odd" : "") + (this.trackSel === track.id ? " picked" : "") + (track.hidden ? " is-hidden" : "")
          + (this.focus ? (this.focus === track.id ? " focused" : " folded") : ""),
        style: `height:${geom[index].h}px`,
        "data-track": track.id,
      },
      el("sl-icon", { library: "ph", name: "dots-six-vertical", class: "pt-grip", title: "Drag to move the row" }),
      rowPlayButton(this.focus === track.id, () => this.hooks.onFocusRow?.(track.id)),
      el("div", { class: "row-name" },
        el("span", { class: "row-title", title: canEdit ? "Double-click to rename" : "" }, trackName(content, track))),
      el("div", { class: "row-btns" },
        toggle("hidden", "Show row", "Hide row (not played or rendered)", "eye-slash", "eye"),
        toggle("muted", "Unmute row", "Mute row (plays without sound)", "speaker-slash", "speaker-high"),
        toggle("locked", "Unlock row", "Lock row (can't be changed)", "lock-simple", "lock-simple-open")));
      head.querySelector(".row-title").addEventListener("dblclick", () => this.renameTrack(track.id));
      head.addEventListener("pointerdown", (event) => this.headPointerDown(event, track.id));
      head.addEventListener("contextmenu", (event) => {
        event.preventDefault();
        this.trackSel = track.id;
        this.renderHeads();
        openMenu(event.clientX, event.clientY, this.trackMenu(track.id));
      });
      return head;
    }));
    this.dirty = true;
  }

  trackMenu(trackId) {
    if (!this.store.canEdit) return [];
    const full = this.content.tracks.length >= MAX_TRACKS;
    const items = [];
    if (!full) {
      items.push({ icon: "arrow-line-up", label: "Insert a row above", run: () => this.addTrack(this.trackIndex(trackId)) });
      items.push({ icon: "arrow-line-down", label: "Insert a row below", run: () => this.addTrack(this.trackIndex(trackId) + 1) });
    }
    items.push({ icon: "pencil-simple", label: "Rename the row", run: () => this.renameTrack(trackId) });
    if (clipboard) items.push({ icon: "clipboard-text", label: "Paste at the playhead", run: () => this.paste(trackId) });
    if (this.content.tracks.length > 1) items.push({ icon: "trash", label: "Delete the row", danger: true, run: () => this.deleteTrack(trackId) });
    return items;
  }

  addTrack(index = this.content.tracks.length) {
    if (!this.store.canEdit) return null;
    if (this.content.tracks.length >= MAX_TRACKS) {
      toast(`An edit can have at most ${MAX_TRACKS} rows.`, "error");
      return null;
    }
    const id = newId("t");
    this.store.edit((c) => {
      c.tracks.splice(Math.max(0, Math.min(index, c.tracks.length)), 0, makeTrack(c, id));
    });
    this.trackSel = id;
    this.renderHeads();
    return id;
  }

  async renameTrack(trackId) {
    if (!this.store.canEdit) return;
    const track = this.content.tracks.find((t) => t.id === trackId);
    if (!track) return;
    const name = await askText({ heading: "Rename the row", label: "Name", value: track.name ?? "", maxLength: 60 });
    if (name == null) return;
    this.store.edit((c) => {
      const t = c.tracks.find((x) => x.id === trackId);
      t.name = name.trim() || t.name || trackName(c, t);
    });
  }

  async deleteTrack(trackId = this.trackSel) {
    if (!this.store.canEdit || !trackId) return;
    if (this.content.tracks.length < 2) {
      toast("An edit needs at least one row.");
      return;
    }
    const count = this.content.pieces.filter((p) => p.trackId === trackId).length;
    if (count) {
      const ok = await confirmAction({
        heading: "Delete this row?",
        text: `The ${count} piece${count === 1 ? "" : "s"} on it ${count === 1 ? "goes" : "go"} too (Ctrl+Z brings ${count === 1 ? "it" : "them"} back). The clips, segments and videos they came from don't change.`,
        confirm: "Delete",
      });
      if (!ok) return;
    }
    this.store.edit((c) => {
      c.tracks = c.tracks.filter((t) => t.id !== trackId);
      c.pieces = c.pieces.filter((p) => p.trackId !== trackId);
    });
    this.trackSel = null;
  }

  // Dragging a row's header up or down moves the row
  headPointerDown(event, trackId) {
    if (event.button !== 0 || event.target.closest("sl-icon-button")) return;
    // While a row plays alone, clicking another row's (folded) head plays that one instead
    if (this.focus) {
      if (trackId !== this.focus) this.hooks.onFocusRow?.(trackId);
      return;
    }
    this.trackSel = trackId;
    for (const head of this.heads.children) head.classList.toggle("picked", head.dataset.track === trackId);
    this.renderFooter();
    if (!this.store.canEdit) return;
    const startY = event.clientY;
    const from = this.trackIndex(trackId);
    let to = from;
    let moved = false;
    const head = event.currentTarget;
    const move = (e) => {
      if (!moved && Math.abs(e.clientY - startY) < DRAG_PX) return;
      moved = true;
      to = Math.max(0, Math.min(this.content.tracks.length - 1, from + Math.round((e.clientY - startY) / ROW_HEIGHT)));
      head.style.transform = `translateY(${e.clientY - startY}px)`;
      head.classList.add("dragging");
      this.rowDropAt = to;
      this.dirty = true;
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      head.style.transform = "";
      head.classList.remove("dragging");
      this.rowDropAt = null;
      if (moved && to !== from) {
        this.store.edit((c) => {
          const [track] = c.tracks.splice(from, 1);
          c.tracks.splice(to, 0, track);
        });
      } else {
        this.dirty = true;
      }
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  bindFooter() {
    const on = (id, type, run) => $(id).addEventListener(type, (event) => {
      if (this.active) run(event);
    });
    on("tl-add-video", "click", () => {
      const index = this.trackSel ? this.trackIndex(this.trackSel) : 0;
      this.addTrack(Math.max(0, index));
    });
    on("tl-delete-rows", "click", () => this.deleteTrack());
    on("tl-zoom-in", "click", () => this.zoomBy(0.5));
    on("tl-zoom-out", "click", () => this.zoomBy(2));
    on("tl-fit", "click", () => this.fit());
    on("tl-zoom", "sl-input", (event) => this.zoomToSlider(Number(event.target.value)));
  }

  renderFooter() {
    if (!this.active) return;
    const open = Boolean(this.content);
    const canEdit = open && this.store.canEdit;
    $("tl-add-video").disabled = !canEdit || this.content.tracks.length >= MAX_TRACKS;
    $("tl-delete-rows").disabled = !canEdit || !this.trackSel || this.content.tracks.length < 2;
    for (const id of ["tl-zoom-in", "tl-zoom-out", "tl-fit", "tl-zoom"]) $(id).disabled = !open;
    $("tl-zoom").value = this.sliderValue();
    this.renderScroll();
  }

  // ---------- zoom and scroll ----------

  minSpan() {
    return MIN_SPAN;
  }

  sliderValue() {
    const full = this.extent().end;
    if (full <= MIN_SPAN + 0.01) return 0;
    const ratio = Math.log(full / this.span()) / Math.log(full / MIN_SPAN);
    return Math.round(Math.min(Math.max(ratio, 0), 1) * 100);
  }

  zoomToSlider(value) {
    const full = this.extent().end;
    const span = full * Math.pow(MIN_SPAN / full, value / 100);
    const center = this.hooks.getPosition() ?? (this.view.start + this.span() / 2);
    this.setView(center - span / 2, center + span / 2, false);
  }

  zoomBy(factor, anchor = this.hooks.getPosition() ?? (this.view.start + this.span() / 2)) {
    const span = Math.min(Math.max(this.span() * factor, MIN_SPAN), this.extent().end);
    const ratio = (anchor - this.view.start) / this.span();
    this.setView(anchor - ratio * span, anchor - ratio * span + span);
  }

  // The whole project (and a little room after it)
  fit() {
    this.follow = true;
    const end = Math.max(this.length() * 1.04, 10);
    this.setView(0, end);
  }

  setView(start, end, updateSlider = true) {
    const extent = this.extent();
    const span = Math.min(Math.max(end - start, MIN_SPAN), extent.end);
    start = Math.min(Math.max(start, 0), Math.max(0, extent.end - span));
    this.view = { start, end: start + span };
    if (this.active) {
      if (updateSlider) $("tl-zoom").value = this.sliderValue();
      this.renderScroll();
    }
    this.dirty = true;
  }

  pan(seconds) {
    this.follow = false;
    this.setView(this.view.start + seconds, this.view.end + seconds);
  }

  renderScroll() {
    const thumb = this.scroll.firstChild;
    const full = this.extent().end;
    thumb.style.left = `${(this.view.start / full) * 100}%`;
    thumb.style.width = `${Math.max(2, (this.span() / full) * 100)}%`;
  }

  bindScroll() {
    const thumb = this.scroll.firstChild;
    this.scroll.addEventListener("pointerdown", (event) => {
      if (event.button !== 0 || !this.content) return;
      const rect = this.scroll.getBoundingClientRect();
      const full = this.extent().end;
      if (event.target !== thumb) {
        // Click on the bar: center the view there
        const t = ((event.clientX - rect.left) / rect.width) * full;
        this.follow = false;
        this.setView(t - this.span() / 2, t + this.span() / 2);
      }
      const startX = event.clientX;
      const startView = this.view.start;
      const move = (e) => {
        this.follow = false;
        const start = startView + ((e.clientX - startX) / rect.width) * full;
        this.setView(start, start + this.span());
      };
      const up = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
    });
  }

  // ---------- tools ----------

  setTool(tool) {
    this.tool = tool;
    this.razorAt = null;
    this.updateCursor();
    this.changed();
  }

  toggleSnap() {
    this.snap = !this.snap;
    saveSetting(SNAP_KEY, this.snap);
    this.changed();
  }

  // Cursors come from ui/timeline-input.js, the same as Player 1's timeline
  updateCursor(over = null) {
    const kind = this.press?.kind;
    const pressing = kind === "hand" ? "pan" : kind === "edge" && this.press.moved ? "edge" : kind === "move" && this.press.moved ? "drag" : null;
    this.track.style.cursor = timelineCursor({ tool: this.tool, over, pressing, canCut: over === "piece" });
    this.ruler.style.cursor = rulerCursor(this.tool === "hand" ? "pointer" : this.tool);
  }

  // ---------- geometry ----------

  xToTime(x, width) {
    return this.view.start + (x / width) * this.span();
  }

  timeToX(t, width) {
    return ((t - this.view.start) / this.span()) * width;
  }

  local(event, canvas = this.track) {
    const rect = canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top, width: rect.width, rect };
  }

  rowAt(y) {
    if (this.focus) {
      const geom = this.rowGeom();
      const index = geom.findIndex((g) => y >= g.y && y < g.y + g.h);
      return index < 0 ? geom.length : index;
    }
    return Math.floor(y / ROW_HEIGHT);
  }

  // ---------- row focus: one row plays alone (editor.js focusRow); it's taller, the others fold to thin lines ----------

  setFocus(trackId) {
    this.focus = trackId ?? null;
    this.renderHeads();
    this.dirty = true;
  }

  // Each row's top and height
  rowGeom() {
    let y = 0;
    return (this.content?.tracks ?? []).map((track) => {
      const h = !this.focus ? ROW_HEIGHT : track.id === this.focus ? FOCUS_ROW_HEIGHT : FOLDED_ROW_HEIGHT;
      const g = { y, h };
      y += h;
      return g;
    });
  }

  rowsHeight() {
    return this.rowGeom().reduce((sum, g) => sum + g.h, 0);
  }

  drawFocused(now) {
    const content = this.content;
    const geom = this.rowGeom();
    const height = Math.max(this.rowsHeight(), $("tl-track").clientHeight || 0, 1);
    const { ctx, width } = this.prepare(this.track, height);
    const css = getComputedStyle(document.documentElement);
    const line = css.getPropertyValue("--line");
    const accent = css.getPropertyValue("--accent-strong").trim() || "#a597ff";
    content.tracks.forEach((track, index) => {
      const { y, h } = geom[index];
      ctx.fillStyle = line;
      ctx.fillRect(0, y + h - 1, width, 1);
      const pieces = piecesOn(content, track.id).sort((a, b) => a.at - b.at);
      if (track.id !== this.focus) {
        ctx.save();
        ctx.globalAlpha = 0.8;
        for (const piece of pieces) {
          const x1 = this.timeToX(piece.at, width);
          const x2 = this.timeToX(pieceEnd(piece), width);
          if (x2 < 0 || x1 > width) continue;
          ctx.fillStyle = colorOf(piece);
          roundRect(ctx, x1, y + 5, Math.max(2, x2 - x1 - 1), h - 10, 3);
          ctx.fill();
        }
        ctx.restore();
        return;
      }
      // the gaps it skips: dashed, "skip"
      let from = 0;
      for (const piece of pieces) {
        if (piece.at - from > 0.05) drawSkip(ctx, this.timeToX(from, width), this.timeToX(piece.at, width), y, h, accent);
        from = Math.max(from, pieceEnd(piece));
      }
      for (const piece of pieces) this.drawPiece(ctx, piece, y, width, { selected: this.selected.has(piece.id), h, solo: true });
    });
    this.drawPlayhead(ctx, width, height, now, false);
  }

  // The piece under a point, and whether it's on one of its edges
  hit(x, y, width) {
    const content = this.content;
    if (!content) return null;
    const index = this.rowAt(y);
    const track = content.tracks[index];
    if (!track) return null;
    const t = this.xToTime(x, width);
    const pad = (EDGE_PX / width) * this.span();
    const pieces = piecesOn(content, track.id);
    // edges first (they reach a few pixels outside the piece)
    for (const piece of pieces) {
      const x1 = this.timeToX(piece.at, width);
      const x2 = this.timeToX(pieceEnd(piece), width);
      const wide = x2 - x1 > EDGE_PX * 3;
      if (wide && Math.abs(x - x1) <= EDGE_PX && t >= piece.at - pad) return { piece, track, edge: "start" };
      if (wide && Math.abs(x - x2) <= EDGE_PX && t <= pieceEnd(piece) + pad) return { piece, track, edge: "end" };
    }
    const piece = pieces.find((p) => t >= p.at && t < pieceEnd(p));
    return piece ? { piece, track, edge: null } : { piece: null, track, edge: null, t };
  }

  // Times dragged things stick to: every piece's edges (but the ones moving), the playhead, 0:00, the project's end and keyframes
  snapPoints(except = new Set()) {
    const points = [0];
    const now = this.hooks.getPosition();
    if (now != null) points.push(now);
    for (const piece of this.content?.pieces ?? []) {
      if (except.has(piece.id)) continue;
      points.push(piece.at, pieceEnd(piece));
      for (const key of (this.store.isVertical ? piece.layout?.keys : null) ?? []) {
        const t = piece.at + key.time - piece.start;
        if (t >= piece.at && t <= pieceEnd(piece)) points.push(t);
      }
    }
    return points;
  }

  // The nearest snap point to any of `times` within SNAP_PX: { delta, at } or null
  snapDelta(times, width, except) {
    if (!this.snap) return null;
    const limit = (SNAP_PX / width) * this.span();
    let best = null;
    for (const point of this.snapPoints(except)) {
      for (const t of times) {
        const delta = point - t;
        if (Math.abs(delta) <= limit && (!best || Math.abs(delta) < Math.abs(best.delta))) best = { delta, at: point };
      }
    }
    return best;
  }

  // ---------- pointer ----------

  bindPointer() {
    this.track.addEventListener("pointerdown", (event) => this.pointerDown(event));
    this.track.addEventListener("pointermove", (event) => this.pointerMove(event));
    this.track.addEventListener("pointerup", (event) => this.pointerUp(event));
    this.track.addEventListener("pointercancel", () => this.cancelPress());
    this.track.addEventListener("pointerleave", () => {
      this.hover = null;
      this.razorAt = null;
      this.tip.hidden = true;
      this.dirty = true;
    });
    this.track.addEventListener("dblclick", (event) => {
      const { x, y, width } = this.local(event);
      const hit = this.hit(x, y, width);
      if (hit?.piece) this.editPieceTitle(hit.piece);
    });
    this.track.addEventListener("contextmenu", (event) => this.contextMenu(event));
    this.ruler.addEventListener("pointerdown", (event) => {
      if (event.button !== 0 || !this.content) return;
      this.ruler.setPointerCapture(event.pointerId);
      const seek = (e) => {
        const { x, width } = this.local(e, this.ruler);
        this.hooks.onSeek(Math.max(0, Math.min(this.xToTime(x, width), this.extent().end)));
      };
      seek(event);
      const move = (e) => seek(e);
      const up = () => {
        this.ruler.removeEventListener("pointermove", move);
        this.ruler.removeEventListener("pointerup", up);
      };
      this.ruler.addEventListener("pointermove", move);
      this.ruler.addEventListener("pointerup", up);
    });
    for (const canvas of [this.ruler, this.track]) {
      canvas.addEventListener("wheel", (event) => {
        if (!this.content) return;
        const { x, width } = this.local(event, canvas);
        timelineWheel(event, {
          x, width, span: this.span(), xToTime: (px, w) => this.xToTime(px, w),
          zoomAt: (factor, at) => { this.follow = false; this.zoomBy(factor, at); },
          panBy: (seconds) => this.pan(seconds),
          rows: document.querySelector(".tl-body"),
        });
      }, { passive: false });
    }
  }

  pointerDown(event) {
    if (!this.content) return;
    if (event.button === 2) return; // contextmenu
    if (event.button === 1) {
      event.preventDefault();
      this.startHand(event);
      return;
    }
    if (event.button !== 0) return;
    const p = this.local(event);
    const t = this.xToTime(p.x, p.width);
    if (this.tool === "hand") {
      this.startHand(event);
      return;
    }
    if (this.tool === "zoom") {
      this.follow = false;
      this.zoomBy(0.5, t);
      return;
    }
    const hit = this.hit(p.x, p.y, p.width);
    if (this.tool === "cut") {
      if (hit?.piece) this.splitAt(hit.piece, this.razorAt ?? t);
      return;
    }
    // pointer
    this.track.setPointerCapture(event.pointerId);
    if (hit?.track) this.trackSel = hit.track.id;
    if (hit?.piece) {
      const piece = hit.piece;
      if (event.shiftKey && !hit.edge) {
        if (this.selected.has(piece.id)) this.selected.delete(piece.id);
        else this.selected.add(piece.id);
        this.renderHeads();
        this.changed();
      } else if (!this.selected.has(piece.id) || hit.edge) {
        this.select([piece.id]);
      }
      const locked = this.isLocked(piece.trackId) || !this.store.canEdit;
      this.press = {
        kind: hit.edge ? "edge" : "move", piece, edge: hit.edge, startX: event.clientX, startY: event.clientY, t0: t,
        row0: this.rowAt(p.y), moved: false, locked, clickSeek: t, rect: p.rect,
      };
      this.renderHeads();
      return;
    }
    this.press = { kind: "empty", startX: event.clientX, startY: event.clientY, x0: p.x, y0: p.y, t0: t, moved: false, add: event.shiftKey, rect: p.rect };
    this.renderHeads();
  }

  startHand(event) {
    const startX = event.clientX;
    const startView = this.view.start;
    const width = this.track.getBoundingClientRect().width;
    this.press = { kind: "hand" };
    this.updateCursor();
    const move = (e) => {
      this.follow = false;
      const start = startView - ((e.clientX - startX) / width) * this.span();
      this.setView(start, start + this.span());
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      this.press = null;
      this.updateCursor();
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  pointerMove(event) {
    const p = this.local(event);
    this.hover = { ...p, clientX: event.clientX, clientY: event.clientY };
    const press = this.press;
    if (!press) {
      const hit = this.hit(p.x, p.y, p.width);
      if (this.tool === "cut") {
        if (hit?.piece) {
          const raw = this.xToTime(p.x, p.width);
          const snapped = this.snapDelta([raw], p.width, new Set());
          const at = snapped ? snapped.at : raw;
          const inside = at > hit.piece.at + MIN_PIECE - 0.001 && at < pieceEnd(hit.piece) - MIN_PIECE + 0.001;
          this.razorAt = inside ? at : null;
          this.razorRow = this.rowAt(p.y);
        } else {
          this.razorAt = null;
        }
        this.updateCursor(hit?.piece && this.razorAt != null ? "piece" : null);
        this.dirty = true;
      } else if (this.tool === "pointer") {
        const editable = hit?.piece && this.store.canEdit && !this.isLocked(hit.piece.trackId);
        this.updateCursor(editable ? (hit.edge ? "edge" : "item") : null);
      }
      this.renderTip(hit);
      return;
    }
    if (press.kind === "hand") return;
    if (!press.moved && Math.hypot(event.clientX - press.startX, event.clientY - press.startY) < DRAG_PX) return;
    if (!press.moved) {
      press.moved = true;
      this.updateCursor();
    }
    this.tip.hidden = true;
    if (press.kind === "empty") {
      this.marquee = { x1: press.x0, y1: press.y0, x2: p.x, y2: p.y };
      const t1 = this.xToTime(Math.min(press.x0, p.x), p.width);
      const t2 = this.xToTime(Math.max(press.x0, p.x), p.width);
      const r1 = this.rowAt(Math.min(press.y0, p.y));
      const r2 = this.rowAt(Math.max(press.y0, p.y));
      const ids = this.content.pieces.filter((piece) => {
        const row = this.trackIndex(piece.trackId);
        return row >= r1 && row <= r2 && piece.at < t2 && pieceEnd(piece) > t1;
      }).map((piece) => piece.id);
      this.selected = new Set([...(press.add ? press.before ?? (press.before = [...this.selected]) : []), ...ids]);
      this.changed();
      return;
    }
    if (press.locked) return;
    const t = this.xToTime(p.x, p.width);
    if (press.kind === "edge") this.dragEdge(press, t, p.width);
    else this.dragMove(press, t, p, event.shiftKey);
  }

  dragEdge(press, t, width) {
    const piece = press.piece;
    const others = piecesOn(this.content, piece.trackId).filter((x) => x.id !== piece.id);
    const length = vodLengths.get(String(piece.vodId)) ?? null;
    const snapped = this.snapDelta([t], width, new Set([piece.id]));
    let edgeAt = snapped ? snapped.at : t;
    this.snapAt = snapped ? snapped.at : null;
    if (press.edge === "start") {
      const before = others.filter((x) => pieceEnd(x) <= piece.at + 0.01).reduce((m, x) => Math.max(m, pieceEnd(x)), 0);
      const min = Math.max(before, piece.at - piece.start);
      const max = pieceEnd(piece) - MIN_PIECE;
      edgeAt = round(Math.min(Math.max(edgeAt, min), max));
      const shift = edgeAt - piece.at;
      this.preview = new Map([[piece.id, { trackId: piece.trackId, at: edgeAt, start: round(piece.start + shift), end: piece.end }]]);
      this.edgeTip = { piece, text: `Starts at ${formatDuration(piece.start + shift, { short: false })} in the VOD` };
    } else {
      const after = others.filter((x) => x.at >= pieceEnd(piece) - 0.01).reduce((m, x) => Math.min(m, x.at), MAX_END);
      const maxByVod = length ? piece.at + (length - piece.start) : MAX_END;
      const max = Math.min(after, maxByVod, MAX_END);
      const min = piece.at + MIN_PIECE;
      edgeAt = round(Math.min(Math.max(edgeAt, min), max));
      this.preview = new Map([[piece.id, { trackId: piece.trackId, at: piece.at, start: piece.start, end: round(piece.start + edgeAt - piece.at) }]]);
    }
    this.invalid = false;
    this.dirty = true;
  }

  dragMove(press, t, p, keepTime) {
    const content = this.content;
    const moving = this.selectedPieces().filter((x) => !this.isLocked(x.trackId));
    if (!moving.length) return;
    const ids = new Set(moving.map((x) => x.id));
    let dt = keepTime ? 0 : t - press.t0;
    const minAt = Math.min(...moving.map((x) => x.at));
    dt = Math.max(dt, -minAt);
    // rows: all selected move by the same number of rows, staying inside the project; below the last row = a new row (one piece)
    let dRow = this.rowAt(p.y) - press.row0;
    const rows = moving.map((x) => this.trackIndex(x.trackId));
    dRow = Math.max(dRow, -Math.min(...rows));
    let newRow = false;
    if (Math.max(...rows) + dRow > content.tracks.length - 1) {
      newRow = moving.length === 1 && content.tracks.length < MAX_TRACKS;
      dRow = content.tracks.length - 1 - Math.max(...rows);
    }
    if (!keepTime) {
      const primary = press.piece;
      const snapped = this.snapDelta([primary.at + dt, pieceEnd(primary) + dt], p.width, ids);
      if (snapped) dt = Math.max(snapped.delta + dt, -minAt);
      this.snapAt = snapped ? snapped.at : null;
    } else {
      this.snapAt = null;
    }
    const preview = new Map();
    let ok = true;
    for (const piece of moving) {
      const trackId = newRow ? "__new" : content.tracks[this.trackIndex(piece.trackId) + dRow].id;
      let at = round(piece.at + dt);
      if (!newRow) {
        if (this.isLocked(trackId)) ok = false;
        if (!fits(content, trackId, at, pieceLength(piece), ids)) {
          // One piece slides to the nearest free place; more than one must all fit as they are
          const near = moving.length === 1 ? nearestFit(content, trackId, at, pieceLength(piece), ids) : null;
          if (near != null && Math.abs(near - at) <= Math.max(pieceLength(piece), (40 / p.width) * this.span())) at = round(near);
          else ok = false;
        }
      }
      preview.set(piece.id, { trackId, at, start: piece.start, end: piece.end });
    }
    // more than one: the moved pieces mustn't overlap each other either
    if (ok && moving.length > 1) {
      const byTrack = new Map();
      for (const [, v] of preview) {
        const list = byTrack.get(v.trackId) ?? [];
        list.push(v);
        byTrack.set(v.trackId, list);
      }
      for (const list of byTrack.values()) {
        list.sort((a, b) => a.at - b.at);
        for (let i = 1; i < list.length; i++) if (list[i].at < list[i - 1].at + (list[i - 1].end - list[i - 1].start) - 0.01) ok = false;
      }
    }
    this.preview = preview;
    this.newRowPreview = newRow;
    this.invalid = !ok;
    this.dirty = true;
  }

  pointerUp(event) {
    const press = this.press;
    if (!press || press.kind === "hand") return;
    this.press = null;
    this.updateCursor();
    const preview = this.preview;
    const invalid = this.invalid;
    const newRow = this.newRowPreview;
    this.preview = null;
    this.invalid = false;
    this.newRowPreview = false;
    this.marquee = null;
    this.snapAt = null;
    this.edgeTip = null;
    this.dirty = true;
    if (!press.moved) {
      if (press.kind === "empty") {
        if (!press.add) this.select([]);
        this.hooks.onSeek(Math.max(0, press.t0));
      } else if (press.kind === "move" && !event.shiftKey) {
        // a click on a piece: select just it and move the playhead there
        this.select([press.piece.id]);
        this.hooks.onSeek(press.clickSeek);
      }
      return;
    }
    if (press.kind === "empty") {
      this.renderHeads();
      this.changed();
      return;
    }
    if (!preview || press.locked) return;
    if (invalid) {
      toast("Pieces on one row can't overlap, and locked rows can't change.");
      return;
    }
    let newTrackId = null;
    this.store.edit((c) => {
      if (newRow) {
        newTrackId = newId("t");
        c.tracks.push(makeTrack(c, newTrackId));
      }
      for (const [id, v] of preview) {
        const piece = c.pieces.find((x) => x.id === id);
        if (!piece) continue;
        piece.trackId = v.trackId === "__new" ? newTrackId : v.trackId;
        piece.at = v.at;
        piece.start = v.start;
        piece.end = v.end;
      }
    });
    if (press.kind === "edge") {
      vodLength(press.piece.vodId);
    }
  }

  cancelPress() {
    this.press = null;
    this.preview = null;
    this.marquee = null;
    this.snapAt = null;
    this.invalid = false;
    this.dirty = true;
  }

  // ---------- editing ----------

  // The razor: split the piece at project time t (each half keeps the layout and its motion)
  splitAt(piece, t) {
    if (!this.store.canEdit || this.isLocked(piece.trackId)) return;
    t = round(t);
    if (t < piece.at + MIN_PIECE - 0.001 || t > pieceEnd(piece) - MIN_PIECE + 0.001) {
      toast("Each half needs at least half a second.");
      return;
    }
    if (this.content.pieces.length >= MAX_PIECES) {
      toast(`An edit can have at most ${MAX_PIECES} pieces.`, "error");
      return;
    }
    const vodT = round(piece.start + (t - piece.at));
    const rightId = newId("p");
    this.store.edit((c) => {
      const p = c.pieces.find((x) => x.id === piece.id);
      const [leftLayout, rightLayout] = splitLayout(p.layout, vodT);
      const right = { ...structuredClone(p), id: rightId, at: t, start: vodT, layout: rightLayout };
      p.end = vodT;
      p.layout = leftLayout;
      c.pieces.splice(c.pieces.indexOf(p) + 1, 0, right);
    });
    this.select([rightId]);
  }

  // Splits every unlocked piece under the playhead (for a key; the razor tool splits one)
  splitAtPlayhead() {
    const now = this.hooks.getPosition();
    if (now == null) return;
    const under = this.content.pieces.filter((p) => now > p.at + MIN_PIECE && now < pieceEnd(p) - MIN_PIECE && !this.isLocked(p.trackId));
    const target = under.filter((p) => this.selected.has(p.id));
    for (const piece of (target.length ? target : under.slice(0, 1))) this.splitAt(piece, now);
  }

  // Delete: the selected pieces go and leave a gap. ripple (Shift+Delete): later pieces on their rows slide left to close it.
  deleteSelected(ripple = false) {
    const pieces = this.selectedPieces().filter((p) => !this.isLocked(p.trackId));
    if (!pieces.length || !this.store.canEdit) return false;
    const ids = new Set(pieces.map((p) => p.id));
    this.store.edit((c) => {
      if (ripple) {
        for (const removed of pieces) {
          const length = pieceLength(removed);
          for (const p of c.pieces) {
            if (p.trackId === removed.trackId && !ids.has(p.id) && p.at >= pieceEnd(removed) - 0.01) p.at = round(p.at - length);
          }
        }
      }
      c.pieces = c.pieces.filter((p) => !ids.has(p.id));
    });
    this.selected.clear();
    this.changed();
    return true;
  }

  // The empty time on a row around t: { start, end } (end = the next piece's start; null after the last piece)
  gapAt(trackId, t) {
    const pieces = piecesOn(this.content, trackId);
    const before = pieces.filter((p) => pieceEnd(p) <= t + 0.001).reduce((m, p) => Math.max(m, pieceEnd(p)), 0);
    const next = pieces.find((p) => p.at >= t - 0.001);
    if (!next) return null;
    return next.at - before > 0.01 ? { start: before, end: next.at } : null;
  }

  closeGap(trackId, gap) {
    const length = gap.end - gap.start;
    this.store.edit((c) => {
      for (const p of c.pieces) if (p.trackId === trackId && p.at >= gap.end - 0.01) p.at = round(p.at - length);
    });
  }

  // I / O: the selected piece's start or end at the playhead (trims it; it can reach into the rest of the VOD)
  setEdgeAtPlayhead(edge) {
    const now = this.hooks.getPosition();
    const piece = this.selectedPieces()[0];
    if (now == null || !piece || this.isLocked(piece.trackId) || !this.store.canEdit) return;
    const press = { piece, edge };
    const width = this.track.getBoundingClientRect().width || 1000;
    const snap = this.snap;
    this.snap = false;
    this.dragEdge(press, now, width);
    this.snap = snap;
    const v = this.preview?.get(piece.id);
    this.preview = null;
    this.snapAt = null;
    this.edgeTip = null;
    if (!v) return;
    this.store.edit((c) => {
      const p = c.pieces.find((x) => x.id === piece.id);
      Object.assign(p, { at: v.at, start: v.start, end: v.end });
    });
  }

  copySelected() {
    const pieces = this.selectedPieces().sort((a, b) => a.at - b.at);
    if (!pieces.length) return false;
    const first = pieces[0].at;
    const topRow = Math.min(...pieces.map((p) => this.trackIndex(p.trackId)));
    clipboard = pieces.map((p) => ({ piece: { ...structuredClone(p), at: p.at - first }, trackOffset: this.trackIndex(p.trackId) - topRow }));
    toast(`Copied ${pieces.length} piece${pieces.length === 1 ? "" : "s"}.`);
    return true;
  }

  // Ctrl+V: the copied pieces at the playhead, on the picked row (and the rows under it), in the first place they fit from there
  paste(trackId = this.trackSel ?? this.content?.tracks[0]?.id) {
    if (!clipboard || !this.store.canEdit || !this.content) return false;
    const now = Math.max(0, this.hooks.getPosition() ?? 0);
    const base = this.trackIndex(trackId);
    if (base < 0) return false;
    if (this.content.pieces.length + clipboard.length > MAX_PIECES) {
      toast(`An edit can have at most ${MAX_PIECES} pieces.`, "error");
      return false;
    }
    const placed = [];
    const ok = this.store.edit((c) => {
      // Shift right until the whole group fits
      let offset = 0;
      for (let tries = 0; tries < 400; tries++) {
        const fitsAll = clipboard.every(({ piece, trackOffset }) => {
          const track = c.tracks[base + trackOffset];
          return track && !track.locked && fits(c, track.id, now + offset + piece.at, pieceLength(piece));
        });
        if (fitsAll) break;
        const blockers = c.pieces.filter((p) => clipboard.some(({ trackOffset }) => c.tracks[base + trackOffset]?.id === p.trackId) && pieceEnd(p) > now + offset);
        const next = Math.min(...blockers.map(pieceEnd).filter((e) => e > now + offset + 0.001));
        if (!Number.isFinite(next)) return false;
        offset = next - now;
      }
      for (const { piece, trackOffset } of clipboard) {
        const track = c.tracks[base + trackOffset];
        if (!track) return false;
        const copy = { ...structuredClone(piece), id: newId("p"), trackId: track.id, at: round(now + offset + piece.at) };
        c.pieces.push(copy);
        placed.push(copy.id);
      }
      return true;
    });
    if (!ok) {
      toast("The copied pieces don't fit on the rows there.");
      return false;
    }
    this.select(placed);
    return true;
  }

  editPieceTitle(piece) {
    if (!this.store.canEdit || this.isLocked(piece.trackId)) return;
    this.hooks.openItemEditor({
      title: piece.label ?? "",
      color: piece.color ?? "violet",
      placeholder: "Piece name",
      onColor: (color) => {
        this.colorPreview = { id: piece.id, color };
        this.dirty = true;
      },
      onApply: (title, color) => {
        this.colorPreview = null;
        this.store.edit((c) => {
          const p = c.pieces.find((x) => x.id === piece.id);
          if (!p) return false;
          p.label = title || null;
          p.color = color;
        });
      },
      onCancel: () => {
        this.colorPreview = null;
        this.dirty = true;
      },
    });
  }

  contextMenu(event) {
    event.preventDefault();
    if (!this.content) return;
    const p = this.local(event);
    const t = this.xToTime(p.x, p.width);
    if (this.tool === "zoom") {
      this.follow = false;
      this.zoomBy(2, t);
      return;
    }
    const hit = this.hit(p.x, p.y, p.width);
    if (!hit) {
      if (this.store.canEdit && this.content.tracks.length < MAX_TRACKS) {
        openMenu(event.clientX, event.clientY, [{ icon: "rows-plus-bottom", label: "Add a row", run: () => this.addTrack() }]);
      }
      return;
    }
    const canEdit = this.store.canEdit && !hit.track.locked;
    const items = [];
    if (hit.piece) {
      const piece = hit.piece;
      if (!this.selected.has(piece.id)) this.select([piece.id]);
      const pieces = this.selectedPieces();
      if (canEdit) {
        items.push({ icon: "pencil-simple", label: "Name and color", run: () => this.editPieceTitle(piece) });
        items.push({ icon: "scissors", label: "Split at the playhead", run: () => this.splitAtPlayhead() });
      }
      items.push({ icon: "copy", label: pieces.length > 1 ? `Copy ${pieces.length} pieces` : "Copy", run: () => this.copySelected() });
      // Layouts belong to vertical edits only (Player 2's horizontal edits have none)
      const vertical = this.store.isVertical;
      if (vertical) {
        items.push({ icon: "squares-four", label: "Copy layout", run: () => {
          layoutClipboard = structuredClone(piece.layout ?? null);
          toast(piece.layout ? "Layout copied." : "This piece has no layout yet (it uses the centered box). Copied that.");
        } });
      }
      if (vertical && canEdit && layoutClipboard !== null) {
        items.push({ icon: "clipboard-text", label: pieces.length > 1 ? `Paste layout on ${pieces.length} pieces` : "Paste layout", run: () => this.pasteLayout(pieces) });
      }
      if (vertical && canEdit && this.content.pieces.length > 1) {
        items.push({ icon: "stack", label: "Use this layout on every piece", run: () => this.layoutEverywhere(piece) });
      }
      items.push({ icon: "crosshair", label: "Show in Player 1", run: () => this.hooks.onShowInPlayer1?.(piece) });
      if (piece.source && (piece.source.kind === "clip" || piece.source.kind === "segment") && this.hooks.onApplyToOriginal) {
        items.push({ icon: "arrow-fat-line-up", label: "Apply this trim to the original", run: () => this.hooks.onApplyToOriginal(piece) });
      }
      if (canEdit) {
        items.push({ icon: "trash", label: "Delete (leave a gap)", danger: true, run: () => this.deleteSelected(false) });
        items.push({ icon: "arrows-in-line-horizontal", label: "Delete and close the gap", danger: true, run: () => this.deleteSelected(true) });
      }
    } else {
      this.trackSel = hit.track.id;
      this.renderHeads();
      const gap = this.gapAt(hit.track.id, t);
      if (canEdit && gap) items.push({ icon: "arrows-in-line-horizontal", label: `Close gap (${formatDuration(gap.end - gap.start, { short: false })})`, run: () => this.closeGap(hit.track.id, gap) });
      if (canEdit && clipboard) items.push({ icon: "clipboard-text", label: "Paste at the playhead", run: () => this.paste(hit.track.id) });
      items.push(...this.trackMenu(hit.track.id).filter((x) => !x.label.startsWith("Paste")));
    }
    openMenu(event.clientX, event.clientY, items);
  }

  pasteLayout(pieces) {
    this.store.edit((c) => {
      for (const piece of pieces) {
        const p = c.pieces.find((x) => x.id === piece.id);
        if (!p || c.tracks.find((t) => t.id === p.trackId)?.locked) continue;
        // keyframe times are VOD seconds: moved to this piece's start
        const layout = structuredClone(layoutClipboard);
        if (layout?.keys?.length) {
          const first = layout.keys[0].time;
          layout.keys = layout.keys.map((k) => ({ ...k, time: round(p.start + (k.time - first)) }));
        }
        p.layout = layout;
      }
    });
  }

  layoutEverywhere(piece) {
    const layout = piece.layout ? { ...structuredClone(piece.layout), keys: null } : null;
    this.store.edit((c) => {
      for (const p of c.pieces) if (!c.tracks.find((t) => t.id === p.trackId)?.locked) p.layout = structuredClone(layout);
    });
    toast("Every piece has this layout now (moving boxes dropped). Ctrl+Z undoes it.");
  }

  escape() {
    if (this.press) {
      this.cancelPress();
      return;
    }
    if (this.tool !== "pointer") {
      this.setTool("pointer");
      return;
    }
    this.select([]);
  }

  // ---------- dropping cards and Player 1 items ----------

  bindDrop() {
    dropTarget(this.track, {
      accepts: (item) => this.active && !this.focus && PROJECT_KINDS.includes(item.kind) && (!this.content || this.store.canEdit),
      onOver: (item, info) => this.dragOver(item, info),
      onLeave: () => {
        this.ghost = null;
        this.dirty = true;
      },
      onDrop: (item, info) => this.drop(item, info),
    });
  }

  dragOver(item, info) {
    const pieces = piecesFromItem(item);
    if (!pieces?.length) {
      this.ghost = null;
      return;
    }
    const rect = this.track.getBoundingClientRect();
    const x = info.x - rect.left;
    const y = info.y - rect.top;
    const width = rect.width || 1;
    const content = this.content;
    const rows = content?.tracks.length ?? 0;
    let index = this.rowAt(y);
    const newRow = !content || index >= rows;
    if (newRow) index = rows;
    const length = pieces.reduce((s, p) => s + pieceLength(p), 0);
    let at = Math.max(0, this.xToTime(x, width));
    const snapped = content ? this.snapDelta([at, at + length], width, new Set()) : null;
    if (snapped) at = Math.max(0, at + snapped.delta);
    this.snapAt = snapped ? snapped.at : null;
    at = round(at);
    let ok = true;
    if (!newRow) {
      const track = content.tracks[index];
      if (track.locked) ok = false;
      else if (!fits(content, track.id, at, length)) {
        const near = nearestFit(content, track.id, at, length);
        if (near != null) at = round(near);
        else ok = false;
      }
    }
    if ((content?.pieces.length ?? 0) + pieces.length > MAX_PIECES) ok = false;
    // Everything in one edit comes from one VOD
    const otherVod = Boolean(content) && !this.store.takesVod(pieces[0].vodId);
    if (otherVod) ok = false;
    if (newRow && rows >= MAX_TRACKS) ok = false;
    this.ghost = { pieces, index, newRow, at, length, ok, otherVod, label: item.label };
    this.dirty = true;
  }

  async drop(item, info) {
    this.dragOver(item, info);
    const ghost = this.ghost;
    this.ghost = null;
    this.snapAt = null;
    this.dirty = true;
    if (!ghost) {
      toast("That can't go in Player 2 (it needs a VOD range).");
      return;
    }
    if (!this.content) {
      // Nothing loaded: the item loads on its own
      this.hooks.loadItem?.(item);
      return;
    }
    if (ghost.otherVod) {
      toast("Everything in one edit comes from one VOD. Load it on its own (double-click its card) to work on it.", "error");
      return;
    }
    if (!ghost.ok) {
      toast("It doesn't fit there: pieces on one row can't overlap, and locked rows can't change.");
      return;
    }
    // The playhead goes to what was dropped
    if (this.insertPieces(ghost.pieces, { trackIndex: ghost.newRow ? null : ghost.index, at: ghost.at, newRow: ghost.newRow })) this.hooks.onSeek(ghost.at);
  }

  // Adds pieces back to back from `at` on a row (newRow: a new row at the bottom). Each takes the layout of the piece before it on the row.
  insertPieces(pieces, { trackIndex = null, at = 0, newRow = false } = {}) {
    if (!this.store.canEdit) return false;
    const ids = [];
    const ok = this.store.edit((c) => {
      let trackId;
      if (newRow || trackIndex == null || !c.tracks[trackIndex]) {
        if (c.tracks.length >= MAX_TRACKS) return false;
        trackId = newId("t");
        c.tracks.push(makeTrack(c, trackId));
      } else {
        trackId = c.tracks[trackIndex].id;
      }
      const length = pieces.reduce((s, p) => s + pieceLength(p), 0);
      if (!fits(c, trackId, at, length)) return false;
      const before = piecesOn(c, trackId).filter((p) => pieceEnd(p) <= at + 0.01).pop();
      let layout = before?.layout ? { ...structuredClone(before.layout), keys: null } : null;
      let t = at;
      for (const piece of pieces) {
        const copy = { ...structuredClone(piece), id: newId("p"), trackId, at: round(t) };
        if (!this.store.isVertical) delete copy.layout; // pasted from a vertical edit: horizontal edits keep no boxes
        if (!copy.layout && layout) copy.layout = structuredClone(layout);
        layout = copy.layout ? { ...structuredClone(copy.layout), keys: null } : layout;
        c.pieces.push(copy);
        ids.push(copy.id);
        t += pieceLength(piece);
      }
    });
    if (!ok) return false;
    for (const piece of pieces) vodLength(piece.vodId);
    this.select(ids);
    return true;
  }

  // "Load in Player 2" and new items from Player 1: at the playhead on the picked row (or the end of the top row if that's taken)
  addItemAtPlayhead(pieces) {
    const content = this.content;
    const length = pieces.reduce((s, p) => s + pieceLength(p), 0);
    const trackId = this.trackSel && !this.isLocked(this.trackSel) ? this.trackSel : content.tracks.find((t) => !t.locked)?.id;
    if (!trackId) {
      const ok = this.insertPieces(pieces, { newRow: true, at: 0 });
      if (ok) this.fit();
      return ok;
    }
    const now = Math.max(0, this.hooks.getPosition() ?? 0);
    const index = this.trackIndex(trackId);
    let at = fits(content, trackId, now, length) ? now : Math.max(0, ...piecesOn(content, trackId).map(pieceEnd));
    if (!content.pieces.length) at = 0;
    const ok = this.insertPieces(pieces, { trackIndex: index, at: round(at) });
    // Loading something starts fitted, like opening a project
    if (ok) this.fit();
    return ok;
  }

  // ---------- tip ----------

  renderTip(hit) {
    if (!hit?.piece || !this.hover) {
      this.tip.hidden = true;
      return;
    }
    const piece = hit.piece;
    const kind = { clip: "Clip", segment: "Segment", video: "Video", vod: "VOD", edit: "Edit" }[piece.source?.kind] ?? "Piece";
    const lines = [
      `${formatDuration(piece.at, { short: false })} – ${formatDuration(pieceEnd(piece), { short: false })} · ${formatDuration(pieceLength(piece), { short: false })}`,
      `${kind}: ${piece.label || "untitled"}`,
      `VOD ${piece.vodId}, ${formatDuration(piece.start, { short: false })} – ${formatDuration(piece.end, { short: false })}`,
    ];
    if (this.coveredAt(piece, this.xToTime(this.hover.x, this.hover.width))) lines.push("Covered by a higher row here (not seen or heard)");
    if (this.trackOf(piece)?.muted) lines.push("Row muted: plays without sound");
    this.tip.replaceChildren(...lines.map((line, i) => el("div", { class: i ? "" : "num" }, line)));
    this.tip.hidden = false;
    this.tip.style.left = `${this.hover.clientX + 12}px`;
    this.tip.style.top = `${this.hover.clientY + 16}px`;
  }

  // ---------- drawing ----------

  update() {
    const now = this.hooks.getPosition();
    if (this.content && now != null) {
      if (this.follow && !this.press && (now < this.view.start || now > this.view.end)) {
        const span = this.span();
        this.setView(now - span * 0.1, now + span * 0.9);
      }
      if (now !== this.lastNow) this.dirty = true;
      this.lastNow = now;
    }
    if (!this.dirty || !this.active) return;
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
    if (!this.content) return;
    const css = getComputedStyle(document.documentElement);
    const span = this.span();
    const step = TICK_STEPS.find((s) => (s / span) * width >= 70) ?? TICK_STEPS[TICK_STEPS.length - 1];
    ctx.font = "12px Barlow, system-ui, sans-serif";
    ctx.textBaseline = "top";
    ctx.strokeStyle = css.getPropertyValue("--line-strong");
    for (let t = Math.ceil(this.view.start / step) * step; t <= this.view.end; t += step) {
      const x = Math.round(this.timeToX(t, width)) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, height - 16);
      ctx.lineTo(x, height - 8);
      ctx.stroke();
      ctx.fillStyle = css.getPropertyValue("--text-dim");
      ctx.fillText(step < 1 ? formatDuration(t, { short: false }) : formatDuration(t), x + 4, 3);
      for (let i = 1; i < 5; i++) {
        const mx = Math.round(this.timeToX(t + (step * i) / 5, width)) + 0.5;
        ctx.beginPath();
        ctx.moveTo(mx, height - 12);
        ctx.lineTo(mx, height - 8);
        ctx.stroke();
      }
    }
    // What plays: each stretch in its piece's color, gaps hatched, along the bottom
    for (const s of flatten(this.content)) {
      const x1 = this.timeToX(s.at, width);
      const x2 = this.timeToX(s.at + s.duration, width);
      if (x2 < 0 || x1 > width) continue;
      ctx.fillStyle = s.piece ? colorOf(s.piece) : hatch(ctx, "#ffffff");
      ctx.globalAlpha = s.piece ? 0.8 : 0.6;
      ctx.fillRect(x1, height - 6, Math.max(1, x2 - x1), 6);
      ctx.globalAlpha = 1;
    }
    this.drawEnd(ctx, width, height);
    this.drawPlayhead(ctx, width, height, now, true);
  }

  drawEnd(ctx, width, height) {
    const end = this.length();
    if (end <= 0) return;
    const x = Math.round(this.timeToX(end, width)) + 0.5;
    if (x < 0 || x > width) return;
    ctx.save();
    ctx.strokeStyle = "rgb(255 255 255 / 0.5)";
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
    ctx.restore();
  }

  // Parts of a piece covered by visible pieces on higher rows
  coveredRanges(piece) {
    const index = this.trackIndex(piece.trackId);
    const ranges = [];
    for (const other of this.content.pieces) {
      const track = this.trackOf(other);
      if (!track || track.hidden || this.trackIndex(other.trackId) >= index) continue;
      const start = Math.max(piece.at, other.at);
      const end = Math.min(pieceEnd(piece), pieceEnd(other));
      if (end > start) ranges.push({ start, end });
    }
    return ranges;
  }

  coveredAt(piece, t) {
    return this.coveredRanges(piece).some((r) => t >= r.start && t < r.end);
  }

  drawTrack(now) {
    if (this.focus && this.content) {
      this.drawFocused(now);
      return;
    }
    const content = this.content;
    const rows = content?.tracks.length ?? 0;
    const extraRow = this.ghost?.newRow || this.newRowPreview ? 1 : 0;
    const bodyHeight = $("tl-track").clientHeight;
    const height = Math.max((rows + extraRow) * ROW_HEIGHT, bodyHeight || 0, ROW_HEIGHT);
    const { ctx, width } = this.prepare(this.track, height);
    if (!content) {
      if (this.ghost) this.drawGhost(ctx, width);
      return;
    }
    const css = getComputedStyle(document.documentElement);
    content.tracks.forEach((track, index) => {
      const y = index * ROW_HEIGHT;
      ctx.fillStyle = this.trackSel === track.id ? "rgb(139 124 246 / 0.08)" : index % 2 ? "rgb(255 255 255 / 0.015)" : "transparent";
      ctx.fillRect(0, y, width, ROW_HEIGHT);
      if (track.locked) {
        ctx.fillStyle = hatch(ctx, "#000000");
        ctx.globalAlpha = 0.35;
        ctx.fillRect(0, y, width, ROW_HEIGHT);
        ctx.globalAlpha = 1;
      }
      ctx.fillStyle = css.getPropertyValue("--line");
      ctx.fillRect(0, y + ROW_HEIGHT - 1, width, 1);
    });
    // where a dragged row header lands
    if (this.rowDropAt != null) {
      ctx.fillStyle = css.getPropertyValue("--accent").trim() || "#8b7cf6";
      ctx.fillRect(0, this.rowDropAt * ROW_HEIGHT, width, 2);
    }
    // the project's own time past its end is darker, so "the end" is easy to see
    const endX = this.timeToX(this.length(), width);
    if (endX < width) {
      ctx.fillStyle = "rgb(0 0 0 / 0.22)";
      ctx.fillRect(Math.max(0, endX), 0, width - Math.max(0, endX), rows * ROW_HEIGHT);
    }
    if (!content.pieces.length && !this.ghost) {
      ctx.fillStyle = css.getPropertyValue("--text-dim");
      ctx.font = "13px Barlow, system-ui, sans-serif";
      ctx.textBaseline = "middle";
      ctx.fillText("Drag clips, segments or videos here. Drop under the last row to add a row.", 12, ROW_HEIGHT / 2);
    }
    const moving = this.preview;
    for (const piece of content.pieces) {
      const index = this.trackIndex(piece.trackId);
      const track = content.tracks[index];
      const moved = moving?.get(piece.id);
      if (moved && moved.trackId !== piece.trackId || (moved && moved.at !== piece.at && !this.press?.edge)) {
        // drawn where it was, faint, and again at the new place below
        this.drawPiece(ctx, piece, index * ROW_HEIGHT, width, { faint: true });
        continue;
      }
      const shown = moved ? { ...piece, ...moved } : piece;
      this.drawPiece(ctx, shown, index * ROW_HEIGHT, width, { hidden: track.hidden, selected: this.selected.has(piece.id), invalid: moved && this.invalid });
    }
    if (moving && !this.press?.edge) {
      for (const [id, v] of moving) {
        const piece = this.pieceById(id);
        if (!piece) continue;
        const index = v.trackId === "__new" ? content.tracks.length : this.trackIndex(v.trackId);
        this.drawPiece(ctx, { ...piece, ...v }, index * ROW_HEIGHT, width, { selected: true, invalid: this.invalid });
      }
    }
    if (this.ghost) this.drawGhost(ctx, width);
    if (this.marquee) {
      const { x1, y1, x2, y2 } = this.marquee;
      ctx.save();
      ctx.fillStyle = "rgb(139 124 246 / 0.15)";
      ctx.strokeStyle = css.getPropertyValue("--accent").trim() || "#8b7cf6";
      ctx.fillRect(Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1));
      ctx.strokeRect(Math.min(x1, x2) + 0.5, Math.min(y1, y2) + 0.5, Math.abs(x2 - x1), Math.abs(y2 - y1));
      ctx.restore();
    }
    if (this.snapAt != null) this.drawLine(ctx, this.snapAt, width, height, [3, 3], "#fff");
    if (this.razorAt != null && this.tool === "cut") {
      const x = Math.round(this.timeToX(this.razorAt, width)) + 0.5;
      ctx.save();
      ctx.strokeStyle = "#fff";
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(x, this.razorRow * ROW_HEIGHT);
      ctx.lineTo(x, this.razorRow * ROW_HEIGHT + ROW_HEIGHT);
      ctx.stroke();
      ctx.restore();
    }
    this.drawEnd(ctx, width, rows * ROW_HEIGHT);
    this.drawPlayhead(ctx, width, height, now, false);
  }

  drawLine(ctx, t, width, height, dash, color) {
    const x = Math.round(this.timeToX(t, width)) + 0.5;
    ctx.save();
    ctx.strokeStyle = color;
    ctx.setLineDash(dash);
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
    ctx.restore();
  }

  drawGhost(ctx, width) {
    const g = this.ghost;
    const y = g.index * ROW_HEIGHT;
    let t = g.at;
    ctx.save();
    for (const piece of g.pieces) {
      const x1 = this.timeToX(t, width);
      const x2 = this.timeToX(t + pieceLength(piece), width);
      ctx.globalAlpha = 0.55;
      ctx.fillStyle = g.ok ? colorOf(piece) : "#f05252";
      roundRect(ctx, x1, y + 4, Math.max(2, x2 - x1 - 1), ROW_HEIGHT - 8, 4);
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.strokeStyle = g.ok ? "#fff" : "#f05252";
      ctx.setLineDash([4, 3]);
      ctx.stroke();
      ctx.setLineDash([]);
      t += pieceLength(piece);
    }
    if (g.newRow) {
      ctx.fillStyle = "#fff";
      ctx.font = "12px Barlow, system-ui, sans-serif";
      ctx.textBaseline = "middle";
      ctx.fillText("New row", Math.max(4, this.timeToX(g.at, width)) + 6, y - 0 + ROW_HEIGHT / 2);
    }
    ctx.restore();
  }

  drawPiece(ctx, piece, y, width, { faint = false, hidden = false, selected = false, invalid = false, h = ROW_HEIGHT, solo = false } = {}) {
    const x1 = this.timeToX(piece.at, width);
    const x2 = this.timeToX(pieceEnd(piece), width);
    if (x2 < -2 || x1 > width + 2) return;
    const color = this.colorPreview?.id === piece.id ? (MARKER_COLORS[this.colorPreview.color] ?? colorOf(piece)) : colorOf(piece);
    const w = Math.max(2, x2 - x1 - 1);
    ctx.save();
    ctx.globalAlpha = faint ? 0.25 : hidden ? 0.35 : 1;
    ctx.fillStyle = invalid ? "#f05252" : color;
    roundRect(ctx, x1, y + 4, w, h - 8, 4);
    ctx.fill();
    // covered by a higher row: darker
    if (!faint && !hidden && !solo) {
      ctx.save();
      roundRect(ctx, x1, y + 4, w, h - 8, 4);
      ctx.clip();
      for (const range of this.coveredRanges(piece)) {
        const cx1 = this.timeToX(range.start, width);
        const cx2 = this.timeToX(range.end, width);
        ctx.fillStyle = "rgb(0 0 0 / 0.5)";
        ctx.fillRect(cx1, y, cx2 - cx1, h);
      }
      // a muted row: speaker crossed out drawn as a dark band at the bottom
      if (this.trackOf(piece)?.muted) {
        ctx.fillStyle = "rgb(0 0 0 / 0.35)";
        ctx.fillRect(x1, y + h - 12, w, 8);
      }
      ctx.restore();
    }
    // label
    if (w > 24) {
      ctx.save();
      roundRect(ctx, x1, y + 4, w, h - 8, 4);
      ctx.clip();
      ctx.fillStyle = "#0b0b10";
      ctx.font = "600 12px Barlow, system-ui, sans-serif";
      ctx.textBaseline = "top";
      const text = piece.label || "Untitled";
      ctx.fillText(text, Math.max(x1, 0) + 6, y + 8);
      ctx.font = "11px Barlow, system-ui, sans-serif";
      ctx.globalAlpha *= 0.75;
      ctx.fillText(formatDuration(pieceLength(piece), { short: false }), Math.max(x1, 0) + 6, y + 22);
      ctx.restore();
    }
    // moving-box keyframes: diamonds along the bottom (vertical edits only)
    const keys = (this.store.isVertical ? piece.layout?.keys : null) ?? [];
    if (keys.length && !faint) {
      ctx.fillStyle = "#fff";
      ctx.strokeStyle = "#0b0b10";
      for (const key of keys) {
        const t = piece.at + key.time - piece.start;
        if (t < piece.at - 0.001 || t > pieceEnd(piece) + 0.001) continue;
        const kx = this.timeToX(t, width);
        const ky = y + h - 9;
        ctx.beginPath();
        ctx.moveTo(kx, ky - 4);
        ctx.lineTo(kx + 4, ky);
        ctx.lineTo(kx, ky + 4);
        ctx.lineTo(kx - 4, ky);
        ctx.closePath();
        ctx.fill();
        ctx.stroke();
      }
    }
    if (selected) {
      ctx.globalAlpha = 1;
      ctx.strokeStyle = "#fff";
      ctx.lineWidth = 2;
      roundRect(ctx, x1 + 1, y + 5, Math.max(1, w - 2), h - 10, 4);
      ctx.stroke();
      // grips
      ctx.fillStyle = "#fff";
      ctx.fillRect(x1 + 1, y + 12, 3, h - 24);
      ctx.fillRect(x1 + w - 4, y + 12, 3, h - 24);
    }
    ctx.restore();
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
  if (x2 < 0 || x1 > ctx.canvas.width) return;
  const w = x2 - x1;
  if (w < 3) return;
  ctx.save();
  ctx.strokeStyle = color;
  ctx.setLineDash([4, 3]);
  ctx.lineWidth = 1;
  roundRect(ctx, x1 + 1.5, y + 6.5, w - 3, h - 13, 4);
  ctx.stroke();
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
  const key = color;
  if (hatches.has(key)) return hatches.get(key);
  const tile = document.createElement("canvas");
  tile.width = tile.height = 8;
  const t = tile.getContext("2d");
  t.strokeStyle = color;
  t.globalAlpha = 0.5;
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
  hatches.set(key, pattern);
  return pattern;
}
