// Editing on the timelines (frontend rework steps 3b and 4), mixed into Timeline (ui/timeline.js). Player 2's timeline
// ("item" mode) has the same tools for its one item, except creating and cutting; stored clips are trimmed only there.
//
// Tools (V/C/Z/H): pointer (click selects an item and seeks to it; drag the selected item's edges), cut (click a segment to
// split it in two), zoom (click in, right click out) and hand (drag to scroll the view). S / X mark a segment / clip
// (start, then end), M a moment; the title and color editor in the tools bar names it, and it goes on the member's own row
// (the last own row clicked, else their first of that kind). Others' items: view only; with "edit anyone's entries" each
// change asks "Edit original" or "Make a copy" (Enter = copy); anyone who may add things can copy them from the right-click
// menu. Undo/redo: the last 10 marker changes and clip re-trims per VOD, in this tab only, shared by both timelines.
// Dragging an item's body (not its edges) drags it to other places, e.g. onto Player 2 (ui/drag.js), or onto one of the
// member's own rows on Player 1 (step 5): own items move there, others' are copied there, Alt copies own ones too, and
// Alt + a clip on a video row makes a segment at exactly its times. Segments and markers only go on video rows, clips on
// clip rows.
//
// Videos (step 6): the link tool (tools bar) takes a title and the segments clicked on one of the member's rows, in order.
// In Player 2 a video is laid out in video time (Timeline.videoItems): dragging a part's edge trims that segment (in VOD
// seconds, via toVod), the right-click menu reorders parts or takes one out of the video. Someone else's video asks
// "Edit original / Make a copy" first (copying the whole video).
//
// Every time a new kind of item gets drawn here, add its times to snapPoints().

import { api, me, can, toast } from "./core.js";
import { confirmAction, chooseEditTarget } from "./dialogs.js";
import { openMenu } from "./menu.js";
import { formatClock } from "./media.js";
import { startDrag, dropTarget } from "./drag.js";

const EDGE_PX = 7;
const SNAP_PX = 8;
const DRAG_THRESHOLD_PX = 3;
const MIN_LENGTH = 0.5;
const HISTORY_LIMIT = 10;
const SNAP_KEY = "clipLibrary.snap"; // shared with the old page's player bar
const STORED_SAVE_DELAY_MS = 3000;     // a stored clip downloads again on every re-trim: wait for the last change

// Undo/redo steps per VOD, shared by the Player 1 and Player 2 timelines
const HISTORIES = new Map();

// "0:01:02.5" for PUT /trim and POST /api/entries (hundredths)
export function trimValue(seconds) {
  const hundredths = Math.round(Math.max(0, seconds) * 100);
  const whole = Math.floor(hundredths / 100);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  const fraction = hundredths % 100;
  return `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}${fraction ? `.${String(fraction).padStart(2, "0")}` : ""}`;
}

// "Name 1" / "Name 2", or "Segment A1" / "A2" when the last word is one character (same as the old player bar)
function splitLabel(label, part) {
  if (!label) return "";
  const lastWord = label.split(/\s+/).pop();
  return lastWord.length === 1 ? `${label}${part}` : `${label} ${part}`;
}

// What PUT /api/markers/{id} needs (the whole marker)
function markerBody(marker) {
  return {
    seconds: marker.seconds,
    endSeconds: marker.endSeconds ?? null,
    label: marker.label ?? "",
    color: marker.color,
    cuts: marker.cuts ?? null,
  };
}

// Times are kept to hundredths, like the time boxes
function round(seconds) {
  return Math.round(seconds * 100) / 100;
}

function isClipKind(kind) {
  return /clip/i.test(kind ?? "");
}

function loadSnap() {
  try {
    return localStorage.getItem(SNAP_KEY) !== "off";
  } catch {
    return true;
  }
}

export const editing = {
  initEditing(hooks) {
    // hooks: openItemEditor(options), closeItemEditor(), onChange() (tools, selection, undo state), onClipsChanged(),
    // onCreated({ kind, data }) for new segments and clips (Player 2 opens them), onEdited() after anything was saved
    // (the other timeline reads the VOD again)
    this.hooks = hooks;
    this.tool = "pointer";
    this.snap = loadSnap();
    this.snapAt = null;
    this.selected = null;     // { type: "marker" | "clip", id }
    this.marking = null;      // { kind: "segment" | "clip", start } between the two presses of S / X
    this.draft = null;        // { kind, start, end, color, rowId } while the title editor names a new item
    this.preview = null;      // { type, id, start, end } while an edge is dragged
    this.press = null;
    this.activeRowId = null;  // the member's own row clicked last: new items go there
    this.maxStoredClipSeconds = 180;
    this.histories = HISTORIES;
    this.pendingTrim = null;  // { id, start, end, before, timer }: a stored clip's new range, saved after a pause
    this.dropRowId = null;    // the row a dragged item would land on (drawn highlighted)
    this.linking = null;      // link tool: { rowId, ids } (segments picked in order) while the title editor is open
    if (this.mode === "vod") {
      dropTarget(this.track, {
        accepts: (item, info) => Boolean(this.dropPlan(item, info)),
        onOver: (item, info) => {
          const plan = this.dropPlan(item, info);
          const rowId = plan?.row.id ?? null;
          if (rowId !== this.dropRowId) {
            this.dropRowId = rowId;
            this.dirty = true;
          }
          this.showDropTip(plan, info);
        },
        onLeave: () => {
          this.dropRowId = null;
          this.dirty = true;
          this.showDropTip(null);
        },
        onDrop: (item, info) => {
          this.dropRowId = null;
          this.showDropTip(null);
          const plan = this.dropPlan(item, info);
          if (plan) this.runDrop(plan);
        },
      });
    }
    this.lastColor = "yellow";
  },

  resetEditing() {
    this.selected = null;
    this.marking = null;
    this.draft = null;
    this.linking = null;
    this.preview = null;
    this.press = null;
    this.activeRowId = null;
    this.tool = "pointer";
    if (this.active) this.hooks.closeItemEditor?.();
    this.changed();
  },

  changed() {
    this.dirty = true;
    this.hooks.onChange?.();
  },

  edited() {
    this.hooks.onEdited?.();
  },

  // ---------- who may do what ----------

  canAdd() {
    return Boolean(this.vodId) && !me?.support && can("AddEntries");
  },

  // New markers, segments and clips are made on Player 1's timeline only
  canCreate() {
    return this.mode === "vod" && this.canAdd();
  },

  isOwn(item) {
    if (item.kind === "clip") return item.data.addedByUserId != null && String(item.data.addedByUserId) === String(me?.id);
    return item.data.createdByUserId === me?.id;
  },

  rowLocked(item) {
    return Boolean(item.row && this.stateOf(item.row).locked);
  },

  canEditItem(item) {
    if (!item || me?.support || this.rowLocked(item)) return false;
    return Boolean(item.data.canEdit);
  },

  // Edges can be dragged on segments and on reference-only VOD clips; stored clips only in Player 2 (they download again)
  canMoveEdges(item) {
    if (!this.canEditItem(item)) return false;
    if (item.kind === "segment") return true;
    return item.kind === "clip" && item.data.kind === "Vod" && (item.reference || this.mode === "item");
  },

  // A stored clip's longest range: the stored-clip limit counts what's left after cut-out parts
  clipLimit(item) {
    if (item.kind !== "clip" || item.reference) return Infinity;
    const cut = (item.cuts ?? []).reduce((sum, c) => sum + Math.max(0, c.end - c.start), 0);
    return this.maxStoredClipSeconds + cut;
  },

  canDeleteItem(item) {
    if (!item || me?.support || this.rowLocked(item)) return false;
    if (item.kind === "clip") return this.isOwn(item) ? can("DeleteOwnEntries") || can("DeleteAnyEntries") : can("DeleteAnyEntries");
    return Boolean(item.data.canDelete);
  },

  canCopyItem(item) {
    return this.canAdd() && !this.isOwn(item) && (item.kind !== "clip" || item.data.kind === "Vod");
  },

  whatIs(item) {
    return { clip: "clip", segment: "segment", moment: "marker" }[item.kind];
  },

  // Someone else's item that this member may edit: "Edit original" or "Make a copy" (Enter). Returns { id, copied } or null.
  async editTarget(item) {
    if (this.isVideoLayout()) return (await this.videoEditTarget()) ? { id: item.id, copied: false } : null;
    if (this.isOwn(item) || !this.canCopyItem(item)) return { id: item.id, copied: false };
    const choice = await chooseEditTarget({ what: this.whatIs(item), owner: item.by });
    if (!choice) return null;
    if (choice === "original") return { id: item.id, copied: false };
    const copy = await this.copyItem(item, { quiet: true });
    if (!copy) return null;
    // Player 2 goes on with the copy
    if (this.mode === "item") this.focusOn({ type: item.kind === "clip" ? "clip" : "marker", id: copy.id });
    return { id: copy.id, copied: true };
  },

  // The member's own row for a new item: the one clicked last, else their first unlocked one. undefined = none yet
  // (the server makes one), false = all locked.
  targetRow(kind) {
    const own = this.rows.filter((row) => row.userId === me?.id && isClipKind(row.kind) === (kind === "clip"));
    const active = own.find((row) => row.id === this.activeRowId);
    if (active && !this.stateOf(active).locked) return active;
    const open = own.find((row) => !this.stateOf(row).locked);
    if (open) return open;
    return own.length ? false : undefined;
  },

  // ---------- tools ----------

  setTool(tool) {
    if (!["pointer", "cut", "zoom", "hand"].includes(tool)) return;
    if (tool === "cut" && this.mode === "item") return; // Player 2's cut tool removes parts (step 7)
    this.tool = tool;
    this.updateCursor();
    this.changed();
  },

  toggleSnap() {
    this.snap = !this.snap;
    try {
      localStorage.setItem(SNAP_KEY, this.snap ? "on" : "off");
    } catch {
      // not saved; still on/off for this visit
    }
    toast(this.snap ? "Snapping on" : "Snapping off");
    this.changed();
  },

  updateCursor(edge = false) {
    const cursor = this.press?.kind === "pan" ? "grabbing"
      : this.press?.kind === "edge" || edge ? "ew-resize"
        : { pointer: "pointer", cut: "crosshair", zoom: "zoom-in", hand: "grab" }[this.tool];
    this.track.style.cursor = cursor;
    this.ruler.style.cursor = this.tool === "hand" ? cursor : this.tool === "zoom" ? "zoom-in" : "pointer";
  },

  // ---------- selection ----------

  isSelected(item) {
    return Boolean(this.selected && this.selected.id === item.id && (this.selected.type === "clip") === (item.kind === "clip"));
  },

  select(item) {
    // Player 2's item stays selected (a video's parts can be selected one by one)
    if (this.mode === "item" && !this.isVideoLayout()) this.selected = this.focus ? { ...this.focus } : null;
    else this.selected = item ? { type: item.kind === "clip" ? "clip" : "marker", id: item.id } : null;
    this.changed();
  },

  // Player 2 switches to another item of the same VOD (a copy made by the first change)
  focusOn(focus) {
    this.focus = focus;
    this.selected = focus.type === "video" ? null : { ...focus };
    if (focus.type === "video") this.video = null;
    this.load();
    this.hooks.onFocusMoved?.(focus);
  },

  selectedItem() {
    if (!this.selected) return null;
    for (const row of this.rows) {
      const found = this.itemsOf(row).find((item) => this.isSelected(item));
      if (found) return found;
    }
    return null;
  },

  // The selected segment's or clip's range, for Play / Last 3 s
  selectedRange() {
    const item = this.selectedItem();
    if (!item || item.end == null) return null;
    return { start: item.start, end: item.end };
  },

  // The same in VOD seconds (a video's part is shown in video time)
  selectedVodRange() {
    const item = this.selectedItem();
    if (!item || item.end == null) return null;
    return { start: this.toVod(item, item.start), end: this.toVod(item, item.end) };
  },

  // A time on this timeline as VOD seconds, for one item (only a video's parts differ)
  toVod(item, t) {
    return item.offset != null ? round(item.vodStart + (t - item.offset)) : t;
  },

  // How far an item's edges can go, in this timeline's time: the start and end of the VOD
  edgeBounds(item) {
    if (item.offset != null) return { min: item.offset - item.vodStart, max: item.offset + (this.vodLength() - item.vodStart) };
    return { min: 0, max: this.vodLength() };
  },

  // ---------- pointer ----------

  edgeAt(item, x, y, width) {
    const index = this.rows.indexOf(item.row);
    if (index < 0 || Math.floor(y / this.rowHeight) !== index || item.end == null) return null;
    const dStart = Math.abs(x - this.timeToX(item.start, width));
    const dEnd = Math.abs(x - this.timeToX(item.end, width));
    if (Math.min(dStart, dEnd) > EDGE_PX) return null;
    return dStart < dEnd ? "start" : "end";
  },

  pointerDown(event, canvas) {
    if (!this.vodId || event.button !== 0) return;
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const extent = this.extent();
    const t = Math.max(extent.start, Math.min(this.xToTime(x, rect.width), extent.end));

    if (this.tool === "hand") {
      this.press = { kind: "pan", lastX: event.clientX, width: rect.width };
      canvas.setPointerCapture(event.pointerId);
      this.updateCursor();
      return;
    }
    if (this.tool === "zoom") {
      this.follow = false;
      this.zoomBy(0.5, t);
      return;
    }
    if (canvas === this.ruler) {
      this.press = { kind: "scrub", canvas, rect };
      canvas.setPointerCapture(event.pointerId);
      this.seekTo(t);
      return;
    }

    const row = this.rows[Math.floor(y / this.rowHeight)];
    if (row && row.userId === me?.id) this.activeRowId = row.id;
    const item = this.itemAt(x, y, rect.width);

    if (this.linking && this.tool === "pointer") {
      if (item) this.toggleLink(item);
      return;
    }

    if (this.tool === "cut") {
      if (item?.kind === "segment") this.split(item, t);
      else toast("Click a segment to cut it in two.");
      return;
    }

    const selected = this.selectedItem();
    const edge = selected && this.canMoveEdges(selected) ? this.edgeAt(selected, x, y, rect.width) : null;
    if (edge) {
      this.press = { kind: "edge", item: selected, edge, startX: event.clientX, moved: false, rect };
      canvas.setPointerCapture(event.pointerId);
      this.updateCursor();
      return;
    }
    this.select(item);
    // Player 1: a click on an item goes to its start; Player 2 (one item) goes where it's clicked
    this.seekTo(item && this.mode === "vod" ? item.start : t);
    // Dragging the item's body takes it somewhere else (onto Player 2, ...)
    if (item && this.mode === "vod") {
      this.press = { kind: "item", item, startX: event.clientX, startY: event.clientY, canvas, pointerId: event.pointerId };
      canvas.setPointerCapture(event.pointerId);
    }
  },

  // Where an item is drawn on the page (the drag shade leaves it lit)
  itemRect(item, canvas) {
    const rect = canvas.getBoundingClientRect();
    const index = this.rows.indexOf(item.row);
    const x1 = rect.left + this.timeToX(item.start, rect.width);
    const x2 = item.end == null ? x1 : rect.left + this.timeToX(item.end, rect.width);
    const left = Math.max(rect.left, Math.min(x1, x2) - 6);
    const right = Math.min(rect.right, Math.max(x1, x2) + 6);
    return new DOMRect(left, rect.top + index * this.rowHeight, Math.max(0, right - left), this.rowHeight);
  },

  // What a dragged timeline item carries (ui/drag.js): kind segment / moment / clip, data with its VOD
  dragItem(item) {
    const label = item.title || { clip: "Clip", segment: "Segment", moment: "Marker" }[item.kind];
    return { kind: item.kind, label, data: { ...item.data, vodId: this.vodId, vodLengthSeconds: this.length() } };
  },

  pointerMove(event) {
    const press = this.press;
    if (!press) {
      // Edge cursor over the selected item's edges
      if (this.tool === "pointer" && this.hover?.canvas === this.track) {
        const selected = this.selectedItem();
        const edge = selected && this.canMoveEdges(selected) && this.edgeAt(selected, this.hover.x, this.hover.y, this.hover.width);
        this.updateCursor(Boolean(edge));
      }
      return;
    }
    if (press.kind === "pan") {
      const delta = ((press.lastX - event.clientX) / press.width) * this.span();
      press.lastX = event.clientX;
      this.follow = false;
      this.setView(this.view.start + delta, this.view.end + delta);
      return;
    }
    if (press.kind === "scrub") {
      const t = this.xToTime(event.clientX - press.rect.left, press.rect.width);
      const extent = this.extent();
      this.seekTo(Math.max(extent.start, Math.min(t, extent.end)));
      return;
    }
    if (press.kind === "item") {
      if (Math.hypot(event.clientX - press.startX, event.clientY - press.startY) < 5) return;
      this.press = null;
      if (press.canvas.hasPointerCapture(press.pointerId)) press.canvas.releasePointerCapture(press.pointerId);
      this.hover = null;
      this.renderTip();
      startDrag(event, this.dragItem(press.item), { sourceRect: () => this.itemRect(press.item, press.canvas) });
      return;
    }
    if (press.kind === "edge") {
      if (!press.moved && Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD_PX) return;
      press.moved = true;
      const { item, edge, rect } = press;
      const raw = this.xToTime(event.clientX - rect.left, rect.width);
      const t = round(this.snapTime(raw, rect.width, { type: item.kind === "clip" ? "clip" : "marker", id: item.id, edge }));
      let start = item.start;
      let end = item.end;
      const limit = this.clipLimit(item);
      const bounds = this.edgeBounds(item);
      if (edge === "start") start = Math.max(bounds.min, end - limit, Math.min(t, end - MIN_LENGTH));
      else end = Math.min(bounds.max, start + limit, Math.max(t, start + MIN_LENGTH));
      this.preview = { type: item.kind === "clip" ? "clip" : "marker", id: item.id, start, end };
      this.edgeTip = { time: edge === "start" ? start : end, clientX: event.clientX, clientY: event.clientY };
      this.dirty = true;
      this.renderTip();
    }
  },

  pointerUp() {
    const press = this.press;
    this.press = null;
    this.snapAt = null;
    this.edgeTip = null;
    this.updateCursor();
    if (press?.kind === "edge" && press.moved && this.preview) {
      const { start, end } = this.preview;
      this.commitRange(press.item, start, end);
    }
    this.dirty = true;
  },

  contextMenu(event, canvas) {
    event.preventDefault();
    if (!this.vodId) return;
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    if (this.tool === "zoom") {
      this.follow = false;
      this.zoomBy(2, this.xToTime(x, rect.width));
      return;
    }
    if (canvas !== this.track) return;
    const item = this.itemAt(x, event.clientY - rect.top, rect.width);
    if (!item) return;
    this.select(item);
    if (this.isVideoLayout()) {
      openMenu(event.clientX, event.clientY, this.videoPartMenu(item));
      return;
    }
    const entries = [];
    if (this.canEditItem(item)) {
      entries.push({ icon: "pencil-simple", label: item.kind === "clip" ? "Rename" : "Edit title and color", run: () => this.editTitle(item) });
    }
    if (this.canCopyItem(item)) entries.push({ icon: "copy", label: "Make a copy on my row", run: () => this.copyItem(item) });
    if (this.canDeleteItem(item)) entries.push({ icon: "trash", label: "Delete", danger: true, run: () => this.deleteItem(item) });
    if (!entries.length) entries.push({ icon: "eye", label: "View only", run: () => {} });
    openMenu(event.clientX, event.clientY, entries);
  },

  doubleClick(event, canvas) {
    if (!this.vodId || canvas !== this.track || this.tool !== "pointer") return;
    const rect = canvas.getBoundingClientRect();
    const item = this.itemAt(event.clientX - rect.left, event.clientY - rect.top, rect.width);
    if (item && this.canEditItem(item)) this.editTitle(item);
  },

  seekTo(seconds) {
    this.follow = true;
    this.onSeek(seconds);
  },

  // ---------- dropping items on rows (step 5) ----------

  // What dropping item at the pointer would do: { action, row, source } or null (not allowed there)
  dropPlan(item, info) {
    if (this.mode !== "vod" || !this.vodId || !this.active || me?.support || this.track.hidden) return null;
    const data = item?.data;
    if (!data || String(data.vodId) !== this.vodId) return null;
    const rect = this.track.getBoundingClientRect();
    const row = this.rows[Math.floor((info.y - rect.top) / this.rowHeight)];
    if (!row || row.userId !== me?.id || this.stateOf(row).locked || this.stateOf(row).hidden) return null;
    const videoRow = !isClipKind(row.kind);
    if (item.kind === "segment" || item.kind === "moment") {
      if (!videoRow) return null;
      const marker = this.markers.find((m) => m.id === data.id);
      if (!marker) return null;
      const own = marker.createdByUserId === me?.id;
      if (own && !info.altKey) {
        if (marker.rowId === row.id || !marker.canEdit) return null;
        return { action: "moveMarker", row, source: marker };
      }
      return this.canAdd() ? { action: "copyMarker", row, source: marker } : null;
    }
    if (item.kind === "clip") {
      if (data.kind !== "Vod") return null;
      if (videoRow) return info.altKey && this.canAdd() ? { action: "clipToSegment", row, source: data } : null;
      const own = data.addedByUserId != null && String(data.addedByUserId) === String(me?.id);
      if (own && !info.altKey) {
        if (data.rowId === row.id || !(can("EditOwnEntries") || can("EditAnyEntries"))) return null;
        return { action: "moveClip", row, source: data };
      }
      return this.canAdd() ? { action: "copyClip", row, source: data } : null;
    }
    return null;
  },

  // Next to the pointer while dragging over a row: what letting go does
  showDropTip(plan, info) {
    if (!plan) {
      this.tip.hidden = true;
      return;
    }
    const text = {
      moveMarker: "Move to this row",
      copyMarker: "Copy to this row",
      clipToSegment: "Make a segment at the clip's times",
      moveClip: "Move to this row",
      copyClip: "Copy to this row",
    }[plan.action];
    this.tip.textContent = text;
    this.tip.hidden = false;
    this.tip.style.left = `${info.x + 14}px`;
    this.tip.style.top = `${info.y + 40}px`;
  },

  async runDrop({ action, row, source }) {
    try {
      switch (action) {
        case "moveMarker": {
          const before = { ...markerBody(source), rowId: source.rowId ?? undefined };
          const after = { ...markerBody(source), rowId: row.id };
          await this.putMarker(source.id, after);
          this.remember({ type: "change", ref: { id: source.id }, before, after });
          this.selected = { type: "marker", id: source.id };
          break;
        }
        case "copyMarker":
        case "clipToSegment": {
          const url = action === "copyMarker" ? `/api/markers/${source.id}/copy` : `/api/entries/${source.id}/copy-as-segment`;
          const copy = await api("POST", url, { rowId: row.id });
          this.remember({ type: "add", ref: { id: copy.id }, data: { ...markerBody(copy), rowId: row.id } });
          this.selected = { type: "marker", id: copy.id };
          toast(action === "copyMarker" ? "Copied to your row." : "Made a segment from the clip.");
          break;
        }
        case "moveClip":
          await api("PUT", `/api/entries/${source.id}/row`, { rowId: row.id });
          this.remember({ type: "clipRow", ref: { id: source.id }, before: source.rowId, after: row.id });
          this.selected = { type: "clip", id: source.id };
          break;
        case "copyClip": {
          const copy = await api("POST", `/api/entries/${source.id}/copy`, { rowId: row.id });
          this.selected = { type: "clip", id: copy.id };
          this.hooks.onClipsChanged?.();
          toast("Copied to your row.");
          break;
        }
      }
    } catch (error) {
      toast(error.message, "error");
    }
    this.activeRowId = row.id;
    this.changed();
    await this.load();
    this.edited();
  },

  // ---------- snapping ----------

  // Every time on the timeline a dragged edge can stick to. New kinds of timeline items must add theirs here.
  snapPoints(skip) {
    const points = [];
    if (this.isVideoLayout()) {
      // A video's parts, in video time
      for (const item of this.videoItems(this.rows[0])) {
        if (!(skip?.id === item.id && skip.edge === "start")) points.push(item.start);
        if (!(skip?.id === item.id && skip.edge === "end")) points.push(item.end);
      }
      const now = this.getPosition();
      if (now != null) points.push(now);
      return points;
    }
    for (const marker of this.markers) {
      if (!(skip?.type === "marker" && skip.id === marker.id && skip.edge === "start")) points.push(marker.seconds);
      if (marker.endSeconds != null && !(skip?.type === "marker" && skip.id === marker.id && skip.edge === "end")) points.push(marker.endSeconds);
    }
    for (const clip of this.clips) {
      if (!(skip?.type === "clip" && skip.id === clip.id && skip.edge === "start")) points.push(clip.start);
      if (clip.end != null && !(skip?.type === "clip" && skip.id === clip.id && skip.edge === "end")) points.push(clip.end);
    }
    const now = this.getPosition();
    if (now != null) points.push(now);
    if (this.marking) points.push(this.marking.start);
    if (this.draft) points.push(this.draft.start, ...(this.draft.end != null ? [this.draft.end] : []));
    return points;
  },

  snapTime(seconds, width, skip) {
    this.snapAt = null;
    if (!this.snap) return seconds;
    const reach = (SNAP_PX / width) * this.span();
    let best = null;
    for (const point of this.snapPoints(skip)) {
      const distance = Math.abs(point - seconds);
      if (distance <= reach && (best == null || distance < Math.abs(best - seconds))) best = point;
    }
    if (best == null) return seconds;
    this.snapAt = best;
    return best;
  },

  // ---------- creating ----------

  // S (segment) / X (clip): first press marks the start at the playhead, the second the end; then the title editor opens
  mark(kind) {
    if (!this.canCreate()) return;
    const position = this.getPosition();
    if (position == null) return;
    const now = round(position);
    if (this.marking?.kind !== kind) {
      if (this.targetRow(kind) === false) {
        toast(`Your ${kind === "clip" ? "clip" : "video"} rows are locked.`);
        return;
      }
      this.marking = { kind, start: now };
      this.changed();
      return;
    }
    let start = Math.min(this.marking.start, now);
    let end = Math.max(this.marking.start, now);
    this.marking = null;
    this.changed();
    if (end - start < MIN_LENGTH) {
      toast("Too short: move the playhead at least half a second away from the start.");
      return;
    }
    if (kind === "clip" && end - start > this.maxStoredClipSeconds) {
      if (now < start + 0.001) start = end - this.maxStoredClipSeconds;
      else end = start + this.maxStoredClipSeconds;
      toast(`Clips are at most ${formatClock(this.maxStoredClipSeconds).replace(/^0:0?|\.00$/g, "")}; the clip was shortened.`);
    }
    this.openDraft({ kind, start, end });
  },

  // M: a moment at the playhead
  addMoment() {
    if (!this.canCreate()) return;
    const now = this.getPosition();
    if (now == null) return;
    if (this.targetRow("video") === false) {
      toast("Your video rows are locked.");
      return;
    }
    this.openDraft({ kind: "moment", start: round(now), end: null });
  },

  openDraft(draft) {
    const row = this.targetRow(draft.kind === "clip" ? "clip" : "video");
    this.draft = { ...draft, color: this.lastColor, rowId: row ? row.id : null, vodId: this.vodId };
    this.selected = null;
    this.changed();
    this.hooks.openItemEditor({
      title: "",
      color: this.lastColor,
      colors: draft.kind !== "clip",
      placeholder: draft.kind === "clip" ? "Clip title (blank = the VOD's title)" : draft.kind === "moment" ? "Marker title" : "Segment title",
      onColor: (color) => {
        if (this.draft) this.draft.color = color;
        this.dirty = true;
      },
      onApply: (title, color) => this.saveDraft(title, color),
      onCancel: () => {
        this.draft = null;
        this.changed();
      },
    });
  },

  async saveDraft(title, color) {
    const draft = this.draft;
    this.draft = null;
    if (!draft || draft.vodId !== this.vodId) return;
    const vodId = this.vodId;
    try {
      if (draft.kind === "clip") {
        const entry = await api("POST", "/api/entries", {
          vodId: vodId,
          title: title || null,
          trimMode: "Exact",
          startTime: trimValue(draft.start),
          endTime: trimValue(draft.end),
          rowId: draft.rowId ?? undefined,
        });
        await this.load();
        this.selected = { type: "clip", id: entry.id };
        this.hooks.onClipsChanged?.();
        this.hooks.onCreated?.({ kind: "clip", data: entry });
      } else {
        this.lastColor = color;
        const body = {
          seconds: draft.start,
          endSeconds: draft.kind === "segment" ? draft.end : null,
          label: title,
          color,
          rowId: draft.rowId ?? undefined,
        };
        const marker = await this.postMarker(body);
        this.remember({ type: "add", ref: { id: marker.id }, data: body });
        this.selected = { type: "marker", id: marker.id };
        if (draft.kind === "segment") this.hooks.onCreated?.({ kind: "segment", data: marker });
      }
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
  },

  // ---------- changing ----------

  // A new start/end for a segment or a reference clip (edge drag, I / O)
  async commitRange(item, start, end) {
    const target = await this.editTarget(item);
    if (!target) {
      this.preview = null;
      this.dirty = true;
      return;
    }
    if (item.kind === "clip" && !item.reference) {
      this.scheduleTrim(target.id, { start: item.data.start, end: item.data.end ?? null }, { start, end });
      return;
    }
    try {
      if (item.kind === "clip") {
        const before = { start: item.data.start, end: item.data.end ?? null };
        const after = { start, end };
        await this.trimClip(target.id, after);
        this.remember({ type: "trim", ref: { id: target.id }, before: target.copied ? after : before, after });
        this.selected = { type: "clip", id: target.id };
      } else {
        const before = markerBody(item.data);
        const after = { ...before, seconds: this.toVod(item, start), endSeconds: this.toVod(item, end), cuts: null };
        await this.putMarker(target.id, after);
        this.remember({ type: "change", ref: { id: target.id }, before, after });
        this.selected = { type: "marker", id: target.id };
      }
    } catch (error) {
      toast(error.message, "error");
    }
    this.preview = null;
    this.changed();
    this.load();
  },

  // A stored clip downloads again on every re-trim: keep the new range on screen and save it 3 s after the last change
  // (or when Player 2 loads something else, or the page is closed)
  scheduleTrim(id, before, { start, end }) {
    const pending = this.pendingTrim;
    clearTimeout(pending?.timer);
    this.pendingTrim = {
      id,
      start,
      end,
      before: pending?.id === id ? pending.before : before,
      timer: setTimeout(() => this.flushTrim(), STORED_SAVE_DELAY_MS),
    };
    this.preview = null;
    this.changed();
    if (this.mode === "item") this.hooks.onFocusChanged?.(this.focusItem());
  },

  async flushTrim() {
    const pending = this.pendingTrim;
    if (!pending) return;
    clearTimeout(pending.timer);
    const after = { start: pending.start, end: pending.end };
    try {
      await this.trimClip(pending.id, after);
      this.remember({ type: "trim", ref: { id: pending.id }, before: pending.before, after });
    } catch (error) {
      toast(error.message, "error");
    }
    if (this.pendingTrim === pending) this.pendingTrim = null;
    this.changed();
    if (this.mode === "item") this.hooks.onFocusChanged?.(this.focusItem());
  },

  // The page is closing: send a waiting stored-clip trim without waiting for the answer
  flushTrimOnExit() {
    const pending = this.pendingTrim;
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pendingTrim = null;
    fetch(`/api/entries/${pending.id}/trim`, {
      method: "PUT",
      keepalive: true,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ startTime: trimValue(pending.start), endTime: pending.end == null ? "" : trimValue(pending.end) }),
    }).catch(() => {});
  },

  // I / O: the selected item's start or end at the playhead
  setEdgeAtPlayhead(edge) {
    const item = this.selectedItem();
    const position = this.getPosition();
    if (!item || position == null) return;
    const now = round(position);
    if (!this.canMoveEdges(item)) {
      toast(item.kind === "clip" && !item.reference ? "Stored clips are trimmed in Player 2." : `You can't change this ${this.whatIs(item)}.`);
      return;
    }
    const start = edge === "start" ? now : item.start;
    const end = edge === "end" ? now : item.end;
    if (end - start < MIN_LENGTH) {
      toast(edge === "start" ? "The start has to be before the end." : "The end has to be after the start.");
      return;
    }
    if (end - start > this.clipLimit(item)) {
      toast(`Stored clips are at most ${formatClock(this.maxStoredClipSeconds).replace(/^0:0?|\.00$/g, "")}.`);
      return;
    }
    this.commitRange(item, start, end);
  },

  editTitle(item) {
    if (!this.canEditItem(item)) return;
    this.select(item);
    this.hooks.openItemEditor({
      title: item.title ?? "",
      color: item.color ?? "yellow",
      colors: item.kind !== "clip",
      placeholder: item.kind === "clip" ? "Clip title" : "Title",
      onApply: (title, color) => this.saveTitle(item, title, color),
    });
  },

  async saveTitle(item, title, color) {
    if (item.kind === "clip") {
      if (!title || title === item.title) return;
    } else if (title === (item.title ?? "") && color === item.color) {
      return;
    }
    const target = await this.editTarget(item);
    if (!target) return;
    try {
      if (item.kind === "clip") {
        await api("PATCH", `/api/entries/${target.id}`, { title });
        this.hooks.onClipsChanged?.();
        this.edited();
      } else {
        const before = markerBody(item.data);
        const after = { ...before, label: title, color };
        await this.putMarker(target.id, after);
        this.remember({ type: "change", ref: { id: target.id }, before, after });
      }
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
    this.load();
  },

  // Cut tool: one segment becomes two at t
  async split(item, at) {
    if (this.mode !== "vod") return;
    const t = round(at);
    if (!this.canEditItem(item)) {
      toast(this.canCopyItem(item) ? "This segment is view only. Right-click it to make a copy on your row." : "You can't change this segment.");
      return;
    }
    if (t - item.start < MIN_LENGTH || item.end - t < MIN_LENGTH) {
      toast("Each part has to be at least half a second long.");
      return;
    }
    const target = await this.editTarget(item);
    if (!target) return;
    const before = markerBody(item.data);
    const cuts = before.cuts ?? [];
    const after = {
      ...before,
      endSeconds: t,
      label: splitLabel(before.label, 1),
      cuts: cuts.filter((c) => c.start < t).map((c) => ({ start: c.start, end: Math.min(c.end, t) })),
    };
    const extra = {
      ...before,
      seconds: t,
      label: splitLabel(before.label, 2),
      cuts: cuts.filter((c) => c.end > t).map((c) => ({ start: Math.max(c.start, t), end: c.end })),
      // The second part goes on the same row; on someone else's original (or its copy) it goes on the caller's row
      rowId: this.isOwn(item) ? item.data.rowId ?? undefined : target.copied ? this.targetRow("video")?.id : undefined,
    };
    try {
      await this.putMarker(target.id, after);
      let created;
      try {
        created = await this.postMarker(extra);
      } catch (error) {
        await this.putMarker(target.id, before).catch(() => {});
        throw error;
      }
      this.remember({ type: "split", ref: { id: target.id }, before, after, newRef: { id: created.id }, newData: extra });
      this.selected = { type: "marker", id: created.id };
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
    this.load();
  },

  async copyItem(item, { quiet = false } = {}) {
    const row = this.targetRow(item.kind === "clip" ? "clip" : "video");
    try {
      const url = item.kind === "clip" ? `/api/entries/${item.id}/copy` : `/api/markers/${item.id}/copy`;
      const copy = await api("POST", url, row ? { rowId: row.id } : {});
      if (item.kind === "clip") {
        this.hooks.onClipsChanged?.();
      } else {
        this.remember({ type: "add", ref: { id: copy.id }, data: { ...markerBody(copy), rowId: copy.rowId ?? undefined } });
      }
      this.edited();
      await this.load();
      this.selected = { type: item.kind === "clip" ? "clip" : "marker", id: copy.id };
      if (!quiet) toast(`Copied to your row.`);
      this.changed();
      return copy;
    } catch (error) {
      toast(error.message, "error");
      return null;
    }
  },

  async deleteItem(item) {
    if (!item || !this.canDeleteItem(item)) return;
    if (item.kind === "clip") {
      const ok = await confirmAction({
        heading: "Delete this clip?",
        text: `"${item.title}" and its files are deleted for the whole team. This can't be undone.`,
        confirm: "Delete",
      });
      if (!ok) return;
      try {
        if (this.pendingTrim?.id === item.id) {
          clearTimeout(this.pendingTrim.timer);
          this.pendingTrim = null;
        }
        await api("DELETE", `/api/entries/${item.id}`);
        this.select(null);
        await this.load();
        this.hooks.onClipsChanged?.();
        this.edited();
      } catch (error) {
        toast(error.message, "error");
      }
      return;
    }
    if (!this.isOwn(item)) {
      const ok = await confirmAction({
        heading: `Delete ${item.by ? `${item.by}'s` : "this"} ${this.whatIs(item)}?`,
        text: "It's deleted for the whole team. Undo (Ctrl+Z) brings it back on your own row.",
        confirm: "Delete",
      });
      if (!ok) return;
    }
    try {
      await this.deleteMarker(item.id);
      this.remember({ type: "delete", ref: { id: item.id }, data: { ...markerBody(item.data), rowId: this.isOwn(item) ? item.data.rowId ?? undefined : undefined } });
      this.select(null);
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
    this.load();
  },

  deleteSelected() {
    const item = this.selectedItem();
    if (!item) return;
    if (this.isVideoLayout()) {
      this.removeFromVideo(item);
      return;
    }
    if (!this.canDeleteItem(item)) {
      toast(`You can't delete this ${this.whatIs(item)}.`);
      return;
    }
    this.deleteItem(item);
  },

  // Esc: stop marking, close the title editor, or unselect
  escape() {
    if (this.mode === "item") return false;
    if (this.linking) {
      this.hooks.closeItemEditor?.();
      this.linking = null;
      this.changed();
      return true;
    }
    if (this.marking) {
      this.marking = null;
      this.changed();
      return true;
    }
    if (this.selected) {
      this.select(null);
      return true;
    }
    return false;
  },

  // ---------- videos (step 6) ----------

  // Link tool: the title editor opens with an apply button; segments clicked on one of the member's rows are linked in
  // the order they're clicked (click one again to take it out)
  startLinking() {
    if (!this.canCreate()) return;
    if (this.linking) {
      this.escape();
      return;
    }
    this.marking = null;
    this.linking = { rowId: null, ids: [] };
    this.select(null);
    this.openLinkEditor("");
  },

  openLinkEditor(title) {
    this.changed();
    this.hooks.openItemEditor({
      title,
      colors: false,
      needsApply: true,
      placeholder: "Video title, then click its segments in order",
      onApply: (text) => this.saveLink(text),
      onCancel: () => {
        this.linking = null;
        this.changed();
      },
    });
  },

  toggleLink(item) {
    const link = this.linking;
    if (item.kind !== "segment") {
      toast(item.kind === "clip" ? "Only segments can be linked into a video." : "Only segments can be linked, not moments.");
      return;
    }
    if (item.row.userId !== me?.id) {
      toast("Link segments on your own rows.");
      return;
    }
    if (link.ids.length && link.rowId !== item.row.id) {
      toast("A video links segments from one row.");
      return;
    }
    const at = link.ids.indexOf(item.id);
    if (at >= 0) link.ids.splice(at, 1);
    else link.ids.push(item.id);
    link.rowId = link.ids.length ? item.row.id : null;
    this.changed();
  },

  async saveLink(title) {
    const link = this.linking;
    if (!link) return;
    if (!link.ids.length || !title) {
      // The editor closed: open it again with what was typed
      toast(!title ? "Give the video a title." : "Click the segments to link, in order.");
      this.openLinkEditor(title);
      return;
    }
    this.linking = null;
    this.changed();
    try {
      const video = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/videos`, { title, markerIds: link.ids });
      this.hooks.onVideosChanged?.();
      await this.load();
      this.edited();
      toast(`Made the video "${video.title}".`);
      this.hooks.onCreated?.({ kind: "video", data: video });
    } catch (error) {
      toast(error.message, "error");
    }
  },

  // Someone else's video: view only, or with "edit anyone's entries" Edit original / Make a copy (the copy opens instead)
  async videoEditTarget() {
    const video = this.video;
    if (!video || me?.support) return false;
    if (!video.canEdit) {
      toast(this.canAdd() ? "This video is view only. Right-click a part to make a copy on your row." : "This video is view only.");
      return false;
    }
    if (video.userId === me?.id || !this.canAdd()) return true;
    const choice = await chooseEditTarget({ what: "video", owner: video.createdBy });
    if (!choice) return false;
    if (choice === "original") return true;
    await this.copyVideo();
    return false;
  },

  async copyVideo() {
    try {
      const copy = await api("POST", `/api/videos/${this.video.id}/copy`, {});
      this.hooks.onVideosChanged?.();
      this.focusOn({ type: "video", id: copy.id });
      toast("Copied the video to your row. Make the change on the copy.");
      return copy;
    } catch (error) {
      toast(error.message, "error");
      return null;
    }
  },

  videoMarkerIds() {
    return [...this.video.segments].sort((a, b) => a.position - b.position).map((s) => s.markerId);
  },

  async saveVideo(body) {
    try {
      await api("PUT", `/api/videos/${this.video.id}`, body);
      this.hooks.onVideosChanged?.();
    } catch (error) {
      toast(error.message, "error");
    }
    await this.load();
    this.edited();
  },

  videoPartMenu(item) {
    const ids = this.videoMarkerIds();
    const index = ids.indexOf(item.id);
    const entries = [];
    const editable = Boolean(this.video?.canEdit) && !me?.support;
    if (this.canEditItem(item)) entries.push({ icon: "pencil-simple", label: "Edit title and color", run: () => this.editTitle(item) });
    if (editable && index > 0) entries.push({ icon: "arrow-left", label: "Move earlier", run: () => this.moveVideoPart(item, -1) });
    if (editable && index < ids.length - 1) entries.push({ icon: "arrow-right", label: "Move later", run: () => this.moveVideoPart(item, 1) });
    if (editable && ids.length > 1) entries.push({ icon: "link-break", label: "Take out of the video", danger: true, run: () => this.removeFromVideo(item) });
    if (!editable && this.canAdd() && this.video?.userId !== me?.id) entries.push({ icon: "copy", label: "Make a copy on my row", run: () => this.copyVideo() });
    if (!entries.length) entries.push({ icon: "eye", label: "View only", run: () => {} });
    return entries;
  },

  async moveVideoPart(item, step) {
    if (!(await this.videoEditTarget())) return;
    const ids = this.videoMarkerIds();
    const index = ids.indexOf(item.id);
    const to = index + step;
    if (index < 0 || to < 0 || to >= ids.length) return;
    [ids[index], ids[to]] = [ids[to], ids[index]];
    await this.saveVideo({ markerIds: ids });
  },

  async removeFromVideo(item) {
    const ids = this.videoMarkerIds();
    if (ids.length < 2) {
      toast("This is the video's only segment. Delete the video instead.");
      return;
    }
    if (!(await this.videoEditTarget())) return;
    await this.saveVideo({ markerIds: ids.filter((id) => id !== item.id) });
  },

  // Player 2's rename and delete for a video
  editVideoTitle() {
    if (!this.video) return;
    this.hooks.openItemEditor({
      title: this.video.title,
      colors: false,
      placeholder: "Video title",
      onApply: async (title) => {
        if (!title || title === this.video?.title) return;
        if (!(await this.videoEditTarget())) return;
        await this.saveVideo({ title });
      },
    });
  },

  async deleteVideo() {
    const video = this.video;
    if (!video?.canDelete || me?.support) return;
    const ok = await confirmAction({
      heading: "Delete this video?",
      text: `"${video.title}" is deleted for the whole team. Its segments stay on the row.`,
      confirm: "Delete",
    });
    if (!ok) return;
    try {
      await api("DELETE", `/api/videos/${video.id}`);
      this.hooks.onVideosChanged?.();
    } catch (error) {
      toast(error.message, "error");
    }
    await this.load();
    this.edited();
  },

  // ---------- server calls ----------

  async postMarker(body) {
    const marker = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/markers`, body);
    this.markers = [...this.markers.filter((m) => m.id !== marker.id), marker];
    if (marker.liveEntry) {
      this.hooks.onClipsChanged?.();
      this.load();
    }
    this.dirty = true;
    this.edited();
    return marker;
  },

  async putMarker(id, body) {
    const marker = await api("PUT", `/api/markers/${id}`, body);
    this.markers = this.markers.map((m) => (m.id === id ? marker : m));
    if (marker.liveEntry) this.hooks.onClipsChanged?.();
    this.dirty = true;
    this.edited();
    return marker;
  },

  async deleteMarker(id) {
    await api("DELETE", `/api/markers/${id}`);
    this.markers = this.markers.filter((m) => m.id !== id);
    this.dirty = true;
    this.edited();
  },

  async trimClip(id, { start, end }) {
    await api("PUT", `/api/entries/${id}/trim`, { startTime: trimValue(start), endTime: end == null ? "" : trimValue(end) });
    await this.load();
    this.hooks.onClipsChanged?.();
    this.edited();
  },

  // ---------- undo / redo (last 10 per VOD, this tab only) ----------

  history() {
    if (!this.histories.has(this.vodId)) this.histories.set(this.vodId, { undo: [], redo: [] });
    return this.histories.get(this.vodId);
  },

  remember(step) {
    const history = this.history();
    history.undo.push(step);
    if (history.undo.length > HISTORY_LIMIT) history.undo.shift();
    history.redo = [];
    this.changed();
  },

  canUndo() {
    return Boolean(this.vodId && this.history().undo.length && !me?.support);
  },

  canRedo() {
    return Boolean(this.vodId && this.history().redo.length && !me?.support);
  },

  async undo() {
    await this.replay("undo", "redo", true);
  },

  async redo() {
    await this.replay("redo", "undo", false);
  },

  async replay(from, to, reverse) {
    if (this.replaying || !this.vodId) return;
    const history = this.history();
    const step = history[from].pop();
    if (!step) return;
    this.replaying = true;
    try {
      await this.applyStep(step, reverse);
      history[to].push(step);
    } catch (error) {
      toast(`Couldn't ${reverse ? "undo" : "redo"} that: ${error.message}`, "error");
    } finally {
      this.replaying = false;
    }
    this.changed();
    this.load();
  },

  async applyStep(step, reverse) {
    switch (step.type) {
      case "add":
        if (reverse) await this.deleteMarker(step.ref.id);
        else step.ref.id = (await this.postMarker(this.ownRowOnly(step.data))).id;
        break;
      case "delete":
        if (reverse) step.ref.id = (await this.postMarker(this.ownRowOnly(step.data))).id;
        else await this.deleteMarker(step.ref.id);
        break;
      case "change":
        await this.putMarker(step.ref.id, reverse ? step.before : step.after);
        break;
      case "split":
        if (reverse) {
          await this.deleteMarker(step.newRef.id);
          await this.putMarker(step.ref.id, step.before);
        } else {
          await this.putMarker(step.ref.id, step.after);
          step.newRef.id = (await this.postMarker(this.ownRowOnly(step.newData))).id;
        }
        break;
      case "trim":
        await this.trimClip(step.ref.id, reverse ? step.before : step.after);
        break;
      case "clipRow":
        await api("PUT", `/api/entries/${step.ref.id}/row`, { rowId: reverse ? step.before : step.after });
        this.edited();
        break;
    }
  },

  // A re-added marker may only name one of the caller's rows that still exists
  ownRowOnly(data) {
    const row = this.rows.find((r) => r.id === data.rowId);
    return row && row.userId === me?.id ? data : { ...data, rowId: undefined };
  },
};
