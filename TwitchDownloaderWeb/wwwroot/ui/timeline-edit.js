// Editing on the timelines (frontend rework steps 3b and 4), mixed into Timeline (ui/timeline.js). Player 1 only: Player 2's
// project timeline is ProjectTimeline (ui/project-timeline.js).
//
// Tools (V/C/Z/H): pointer (click selects an item and seeks to it; drag the selected item's edges), cut (click a segment to
// split it in two), zoom (click in, right click out) and hand (drag to scroll the view). S / X mark a segment / clip
// (start, then end), M a moment; the title and color editor in the tools bar names it, and it goes on the member's own row
// (the last own row clicked, else their first of that kind). Others' items: view only; with "edit anyone's entries" each
// change asks "Edit original" or "Make a copy" (Enter = copy); anyone who may add things can copy them from the right-click
// menu. Undo/redo: the last 10 marker changes and clip re-trims per VOD, in this tab only.
// Dragging an item's body (not its edges) drags it to other places, e.g. onto Player 2 (ui/drag.js), or onto one of the
// member's own rows on Player 1 (step 5): own items move there, others' are copied there, Alt copies own ones too, and
// Alt + a clip on a video row makes a segment at exactly its times. Segments and markers only go on video rows, clips on
// clip rows. Stored clips' edges are dragged here too (saved edits, 2026-11-16: Player 2 no longer changes the item),
// capped at the stored-clip limit over the clip's whole range, and saved 3 s after the last change (each re-trim downloads
// the clip again).
//
// Videos (step 6): the link tool (tools bar) takes a title and the segments clicked on one of the member's rows, in order.
// In Player 2 the right-click menu reorders a video's parts or takes one out of the video (that changes the video itself).
// Someone else's video asks "Edit original / Make a copy" first (copying the whole video).
//
// Cut-out parts (step 7): made in Player 2 as part of an edit; applying the edit to Player 1 makes them the item's own
// (drawn darker here). Files skip them and lengths count what's left (ui/cuts.js); playback skips them too (ui/editor.js).
//
// Every time a new kind of item gets drawn here, add its times to snapPoints().

import { api, me, can, toast } from "./core.js";
import { confirmAction, chooseEditTarget } from "./dialogs.js";
import { openMenu } from "./menu.js";

import { timelineCursor, rulerCursor } from "./timeline-input.js";
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

// A copy of a marker / entry / body with its times moved by `by` seconds (synced rows: this VOD's clock <-> the row's VOD)
export function shiftTimes(item, by) {
  const out = { ...item };
  for (const key of ["seconds", "endSeconds", "start", "end", "startSeconds"]) {
    if (typeof out[key] === "number") out[key] = Math.round((out[key] + by) * 1000) / 1000;
  }
  const ms = (t) => Math.round((t + by) * 1000) / 1000;
  if (Array.isArray(out.cuts)) out.cuts = out.cuts.map((c) => ({ start: ms(c.start), end: ms(c.end) }));
  delete out.raw;
  return out;
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
    this.marking = null;      // { kind: "segment", start } between the two presses of S
    this.clipDrag = null;     // clip tool: { kind: "clip", start, end, color } while a clip is dragged across the timeline
    this.draft = null;        // { kind, start, end, color, rowId } while the title editor names a new item
    this.preview = null;      // { type, id, start, end } while an edge is dragged
    this.press = null;
    this.activeRowId = null;  // the member's own row clicked last: new items go there
    this.maxStoredClipSeconds = 180;
    this.histories = HISTORIES;
    this.pendingTrim = null;  // { id, start, end, before, timer }: a stored clip's new range, saved after a pause
    this.dropRowId = null;    // the row a dragged item would land on (drawn highlighted)
    this.linking = null;      // link tool: { rowId, ids } (segments picked in order) while the title editor is open
    this.cutDrag = null;      // Player 2's cut tool: { start, end } being dragged
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
    this.lastClipColor = "violet";
  },

  resetEditing() {
    this.selected = null;
    this.marking = null;
    this.draft = null;
    this.linking = null;
    this.cutDrag = null;
    this.clipDrag = null;
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

  // Edges can be dragged on segments and VOD clips (stored ones wait 3 s before they're saved: they download again)
  canMoveEdges(item) {
    if (!this.canEditItem(item)) return false;
    if (item.kind === "segment") return true;
    return item.kind === "clip" && item.data.kind === "Vod";
  },

  // A clip's longest range: the stored-clip limit, counting the whole range (a stored file keeps the whole clip; cut-out parts
  // are left out only when files are made from it). Clips the team doesn't store are references but keep the same limit; only
  // one that is already longer (a "Long" reference) has none
  clipLimit(item) {
    if (item.kind !== "clip" || item.long) return Infinity;
    return this.maxStoredClipSeconds;
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
  // vodId = only rows of that VOD (a copy of an item on a synced row); without it the row clicked last may be a synced row,
  // else one of this VOD's own rows
  targetRow(kind, vodId = null) {
    const own = this.rows.filter((row) => row.userId === me?.id && isClipKind(row.kind) === (kind === "clip")
      && (vodId == null || String(this.rowVod(row.id)) === String(vodId)));
    const active = own.find((row) => row.id === this.activeRowId);
    if (active && !this.stateOf(active).locked) return active;
    const open = own.find((row) => !this.stateOf(row).locked && (vodId != null || !this.syncedRow(row.id)));
    if (open) return open;
    const candidates = vodId != null ? own : own.filter((row) => !this.syncedRow(row.id));
    return candidates.length ? false : undefined;
  },

  // ---------- synced rows (badrfly 2026-11-20) ----------
  //
  // A synced row is a row of another streamer's VOD shown on this VOD's clock (TimelineRow.ShownOnVodId). Its items are real
  // items of that VOD; the timeline keeps them on this VOD's clock (their VOD's seconds - shift, see toLocal in timeline.js)
  // and turns them back at every server call.

  syncedRow(rowId) {
    if (rowId == null) return null;
    const row = this.rows.find((r) => r.id === rowId);
    return row && row.shift != null && row.vodId != null && String(row.vodId) !== String(this.vodId) ? row : null;
  },

  rowVod(rowId) {
    return this.syncedRow(rowId)?.vodId ?? this.vodId;
  },

  // < / > on Player 1: slide a synced row's VOD 0.1 s against this one (the selected item's row, else the row clicked last, else the
  // only synced VOD). step > 0 = their footage later on this VOD's clock. Saved for the VOD pair (PUT /sync, debounced), so every
  // synced row of that VOD and Player 2's Sync with use it too.
  slideSynced(step) {
    const vods = [...new Set(this.rows.filter((r) => this.syncedRow(r.id)).map((r) => String(r.vodId)))];
    const row = this.syncedRow(this.selectedItem()?.row?.id) ?? this.syncedRow(this.activeRowId)
      ?? (vods.length === 1 ? this.rows.find((r) => this.syncedRow(r.id)) : null);
    if (!row) {
      toast(vods.length ? "Click a synced row's name first, then < / > slide it." : "< / > slide a synced row (another streamer's VOD) on Player 1.");
      return;
    }
    const shift = Math.round((row.shift - step) * 1000) / 1000;
    for (const r of this.rows) if (this.syncedRow(r.id) && String(r.vodId) === String(row.vodId)) r.shift = shift;
    const reshift = (item) => {
      const r = this.syncedRow(item.rowId);
      return r && item.raw ? { ...shiftTimes(item.raw, -r.shift), raw: item.raw } : item;
    };
    this.markers = this.markers.map(reshift);
    this.clips = this.clips.map(reshift);
    this.dirty = true;
    const moved = this.slideMoved = Math.round(((this.slideMoved ?? 0) + step) * 10) / 10;
    toast(`${row.name || "Their row"}: ${moved > 0 ? "+" : ""}${moved.toFixed(1)} s`);
    clearTimeout(this.slideTimer);
    const vodId = this.vodId;
    this.slideTimer = setTimeout(async () => {
      this.slideMoved = 0;
      if (vodId !== this.vodId) return;
      const at = Math.max(0, this.getPosition() ?? this.xToTime(this.track.clientWidth / 2, this.track.clientWidth));
      try {
        await api("PUT", `/api/vods/${encodeURIComponent(vodId)}/sync/${row.vodId}`, { at, otherAt: at + shift });
      } catch (error) {
        toast(`Couldn't save the line-up: ${error.message}`, "error");
      }
      this.load();
    }, 600);
  },

  // A marker or entry body on this VOD's clock -> its own VOD's seconds
  toServer(body, rowId) {
    const row = this.syncedRow(rowId);
    return row ? shiftTimes(body, row.shift) : body;
  },

  // A marker or entry from the server -> this VOD's clock (when it sits on a synced row)
  fromServer(item) {
    const row = this.syncedRow(item?.rowId);
    return row ? { ...shiftTimes(item, -row.shift), raw: item } : item;
  },

  // ---------- tools ----------

  setTool(tool) {
    if (!["pointer", "cut", "clip", "zoom", "hand"].includes(tool)) return;
    // The clip tool (badrfly 2026-11-20): drag across the timeline to mark a clip, named when the mouse is let go
    if (tool === "clip" && this.mode === "vod") {
      if (!this.canCreate()) return;
      if (this.targetRow("clip") === false) {
        toast("Your clip rows are locked.");
        return;
      }
      this.marking = null;
    }
    if (tool === "clip" && this.mode !== "vod") return;
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

  // Cursors come from ui/timeline-input.js, shared with Player 2's timeline. over: true / "edge" = an edge that trims, "item" = an item
  // that can be dragged
  updateCursor(over = null) {
    const kind = this.press?.kind;
    const pressing = kind === "pan" ? "pan" : kind === "edge" ? "edge" : kind === "item" && this.press.moved ? "drag" : null;
    this.track.style.cursor = timelineCursor({
      tool: this.tool,
      over: over === true ? "edge" : over,
      pressing,
      canCut: this.razorAt != null,
    });
    this.ruler.style.cursor = rulerCursor(this.tool, kind === "pan" ? "pan" : null);
  },

  // Cut tool hover: where a click would cut (snapped like the click), drawn as a line through the row; null = nothing to cut there
  updateRazor() {
    const hover = this.hover;
    let at = null;
    if (hover?.canvas === this.track && this.tool === "cut" && !this.linking) {
      const rowIndex = Math.floor(hover.y / this.rowHeight);
      const t = this.xToTime(hover.x, hover.width);
      if (this.mode === "item") {
        if (rowIndex === 0 && this.source && this.canMakeEdits?.() && t > 0 && t < this.length()) {
          at = { time: round(Math.max(0, Math.min(this.snapTime(t, hover.width), this.length()))), row: 0 };
        }
      } else {
        const item = this.itemAt(hover.x, hover.y, hover.width);
        if (item?.kind === "segment" && this.canEditItem?.(item) !== false) at = { time: t, row: rowIndex };
      }
    }
    if (!at) this.snapAt = null;
    const changed = JSON.stringify(at) !== JSON.stringify(this.razorAt ?? null);
    this.razorAt = at;
    this.updateCursor();
    if (changed) this.dirty = true;
  },

  drawRazor(ctx, width) {
    const at = this.razorAt;
    if (!at || this.tool !== "cut" || this.press) return;
    const x = Math.round(this.timeToX(at.time, width)) + 0.5;
    ctx.save();
    // A white line with a dark edge, so it shows on any item color
    for (const [color, lineWidth] of [["rgba(0,0,0,0.7)", 3.5], ["#fff", 1.5]]) {
      ctx.strokeStyle = color;
      ctx.lineWidth = lineWidth;
      ctx.beginPath();
      ctx.moveTo(x, at.row * this.rowHeight + 1);
      ctx.lineTo(x, (at.row + 1) * this.rowHeight - 1);
      ctx.stroke();
    }
    ctx.restore();
  },

  // ---------- selection ----------

  isSelected(item) {
    return Boolean(this.selected && this.selected.id === item.id && (this.selected.type === "clip") === (item.kind === "clip"));
  },

  select(item) {
    this.selected = item ? { type: item.kind === "clip" ? "clip" : "marker", id: item.id } : null;
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

  // How far an item's edges can go: the start and end of the VOD
  edgeBounds() {
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
    if (!this.vodId) return;
    // Middle button: scroll the view like the hand tool (as on Player 2)
    if (event.button === 1) {
      event.preventDefault();
      const rect = canvas.getBoundingClientRect();
      this.press = { kind: "pan", lastX: event.clientX, width: rect.width };
      canvas.setPointerCapture(event.pointerId);
      this.updateCursor();
      return;
    }
    if (event.button !== 0) return;
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

    if (this.tool === "clip") {
      if (!this.canCreate() || this.draft) return;
      const from = round(Math.max(0, Math.min(this.snapTime(t, rect.width), this.vodLength())));
      this.snapAt = null;
      this.press = { kind: "clip", from, startX: event.clientX, moved: false, rect };
      canvas.setPointerCapture(event.pointerId);
      return;
    }
    const item = this.itemAt(x, y, rect.width);

    if (this.linking && this.tool === "pointer") {
      if (item) this.toggleLink(item);
      return;
    }

    if (this.tool === "cut" && this.mode === "item") {
      this.startCutOut(event, canvas, rect, x, y, t);
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
    // An item on a synced row is an item of the other VOD: it goes out with that VOD's own times
    const synced = this.syncedRow(item.row?.id);
    if (synced) return { kind: item.kind, label, data: { ...(item.data.raw ?? item.data), vodId: String(synced.vodId) } };
    return { kind: item.kind, label, data: { ...item.data, vodId: this.vodId, vodLengthSeconds: this.length() } };
  },

  pointerMove(event) {
    const press = this.press;
    if (!press && this.tool === "cut") this.updateRazor();
    if (!press) {
      // Edge cursor over the selected item's edges
      if (this.tool === "pointer" && this.hover?.canvas === this.track) {
        const selected = this.selectedItem();
        const edge = selected && this.canMoveEdges(selected) && this.edgeAt(selected, this.hover.x, this.hover.y, this.hover.width);
        // an item's body can be dragged (to Player 2, another row, the export list)
        const item = !edge && this.mode === "vod" ? this.itemAt(this.hover.x, this.hover.y, this.hover.width) : null;
        this.updateCursor(edge ? "edge" : item ? "item" : null);
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
    if (press.kind === "cutout") {
      if (!press.moved && Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD_PX) return;
      press.moved = true;
      const raw = this.xToTime(event.clientX - press.rect.left, press.rect.width);
      const t = round(Math.max(press.min, Math.min(this.snapTime(raw, press.rect.width), press.max)));
      this.cutDrag = { start: Math.min(press.from, t), end: Math.max(press.from, t) };
      this.edgeTip = { text: `Cut out ${formatClock(this.cutDrag.start)} – ${formatClock(this.cutDrag.end)}`, clientX: event.clientX, clientY: event.clientY };
      this.dirty = true;
      this.renderTip();
      return;
    }
    if (press.kind === "clip") {
      if (!press.moved && Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD_PX) return;
      press.moved = true;
      const raw = this.xToTime(event.clientX - press.rect.left, press.rect.width);
      const max = this.maxStoredClipSeconds;
      const t = round(Math.max(0, press.from - max, Math.min(this.snapTime(raw, press.rect.width), press.from + max, this.vodLength())));
      this.clipDrag = { kind: "clip", start: Math.min(press.from, t), end: Math.max(press.from, t), color: this.lastClipColor };
      this.edgeTip = { text: `Clip ${formatClock(this.clipDrag.start)} – ${formatClock(this.clipDrag.end)} (${formatClock(this.clipDrag.end - this.clipDrag.start)})`, clientX: event.clientX, clientY: event.clientY };
      this.dirty = true;
      this.renderTip();
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
    const hadTip = Boolean(this.edgeTip);
    this.edgeTip = null;
    if (hadTip) this.renderTip();
    this.updateCursor();
    if (press?.kind === "edge" && press.moved && this.preview) {
      const { start, end } = this.preview;
      this.commitRange(press.item, start, end);
    }
    if (press?.kind === "clip") {
      const drag = this.clipDrag;
      this.clipDrag = null;
      if (!press.moved) toast("Drag across the timeline to mark a clip.");
      else if (drag && drag.end - drag.start < MIN_LENGTH) toast("Too short: drag at least half a second.");
      else if (drag) this.openDraft({ kind: "clip", start: drag.start, end: drag.end });
    }
    if (press?.kind === "cutout") {
      const drag = this.cutDrag;
      this.cutDrag = null;
      if (press.moved && drag && drag.end - drag.start >= 0.05) this.cutOut(drag.start, drag.end);
      else if (!press.moved) this.clickCutOut(press.from);
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
    const entries = [];
    if (this.canEditItem(item)) {
      entries.push({ icon: "pencil-simple", label: "Edit title and color", run: () => this.editTitle(item) });
    }
    if (this.canCopyItem(item)) entries.push({ icon: "copy", label: "Make a copy on my row", run: () => this.copyItem(item) });
    if (this.mode === "vod" && (item.kind === "segment" || (item.kind === "clip" && item.data.kind === "Vod"))) {
      entries.push({ icon: "download-simple", label: "Add to the export list", run: () => this.hooks.onExport?.(item) });
    }
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
    if (!data) return null;
    const rect = this.track.getBoundingClientRect();
    const row = this.rows[Math.floor((info.y - rect.top) / this.rowHeight)];
    if (!row || row.userId !== me?.id || this.stateOf(row).locked || this.stateOf(row).hidden) return null;
    // Items only go on rows of their own VOD (a synced row is the other VOD's)
    if (String(data.vodId) !== String(this.rowVod(row.id))) return null;
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
          this.remember({ type: "change", ref: this.ref("marker", source.id), before, after });
          this.selected = { type: "marker", id: source.id };
          break;
        }
        case "copyMarker":
        case "clipToSegment": {
          const url = action === "copyMarker" ? `/api/markers/${source.id}/copy` : `/api/entries/${source.id}/copy-as-segment`;
          const copy = this.fromServer(await api("POST", url, { rowId: row.id }));
          this.remember({ type: "add", ref: this.ref("marker", copy.id), data: { ...markerBody(copy), rowId: row.id } });
          this.selected = { type: "marker", id: copy.id };
          toast(action === "copyMarker" ? "Copied to your row." : "Made a segment from the clip.");
          break;
        }
        case "moveClip":
          await api("PUT", `/api/entries/${source.id}/row`, { rowId: row.id });
          this.remember({ type: "clipRow", ref: this.ref("clip", source.id), before: source.rowId, after: row.id });
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
    for (const marker of this.markers) {
      if (!(skip?.type === "marker" && skip.id === marker.id && skip.edge === "start")) points.push(marker.seconds);
      if (marker.endSeconds != null && !(skip?.type === "marker" && skip.id === marker.id && skip.edge === "end")) points.push(marker.endSeconds);
    }
    for (const clip of this.clips) {
      if (!(skip?.type === "clip" && skip.id === clip.id && skip.edge === "start")) points.push(clip.start);
      if (clip.end != null && !(skip?.type === "clip" && skip.id === clip.id && skip.edge === "end")) points.push(clip.end);
    }
    // cut-out parts' edges
    for (const item of [...this.markers, ...this.clips]) for (const cut of item.cuts ?? []) points.push(cut.start, cut.end);
    const now = this.getPosition();
    if (now != null) points.push(now);
    if (this.marking) points.push(this.marking.start);
    if (this.press?.kind === "clip") points.push(this.press.from);
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

  // S (segment): first press marks the start at the playhead, the second the end; then the title editor opens. Clips are
  // made with the clip tool (X) instead.
  mark(kind = "segment") {
    if (!this.canCreate() || kind !== "segment") return;
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
    const color = draft.kind === "clip" ? this.lastClipColor : this.lastColor;
    this.draft = { ...draft, color, rowId: row ? row.id : null, vodId: this.vodId };
    this.selected = null;
    this.changed();
    this.hooks.openItemEditor({
      title: "",
      color,
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
        this.lastClipColor = color;
        // on a synced clip row the clip is the other VOD's, in its seconds
        const times = this.toServer({ start: draft.start, end: draft.end }, draft.rowId);
        const entry = await api("POST", "/api/entries", {
          vodId: this.rowVod(draft.rowId),
          title: title || null,
          color: color === "violet" ? null : color,
          trimMode: "Exact",
          startTime: trimValue(times.start),
          endTime: trimValue(times.end),
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
        this.remember({ type: "add", ref: this.ref("marker", marker.id), data: body });
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
      this.scheduleTrim(target.id, { start: item.data.start, end: item.data.end ?? null, cuts: item.data.cuts ?? [] }, { start, end });
      return;
    }
    try {
      if (item.kind === "clip") {
        const before = { start: item.data.start, end: item.data.end ?? null, cuts: item.data.cuts ?? [] };
        const after = { start, end };
        await this.trimClip(target.id, after);
        this.remember({ type: "trim", ref: this.ref("clip", target.id), before: target.copied ? after : before, after });
        this.selected = { type: "clip", id: target.id };
      } else {
        const before = markerBody(item.data);
        const after = { ...before, seconds: start, endSeconds: end, cuts: null };
        await this.putMarker(target.id, after);
        this.remember({ type: "change", ref: this.ref("marker", target.id), before, after });
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
  scheduleTrim(id, before, { start, end, cuts }) {
    const pending = this.pendingTrim;
    clearTimeout(pending?.timer);
    this.pendingTrim = {
      id,
      start,
      end,
      // undefined = keep the cut-out parts it has (a waiting change of them stays)
      cuts: cuts !== undefined ? cuts : pending?.id === id ? pending.cuts : undefined,
      before: pending?.id === id ? pending.before : before,
      timer: setTimeout(() => this.flushTrim(), STORED_SAVE_DELAY_MS),
    };
    this.preview = null;
    this.changed();
  },

  async flushTrim() {
    const pending = this.pendingTrim;
    if (!pending) return;
    clearTimeout(pending.timer);
    const after = { start: pending.start, end: pending.end, cuts: pending.cuts };
    try {
      await this.trimClip(pending.id, after);
      this.remember({ type: "trim", ref: this.ref("clip", pending.id), before: pending.before, after });
    } catch (error) {
      toast(error.message, "error");
    }
    if (this.pendingTrim === pending) this.pendingTrim = null;
    this.changed();
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
      body: JSON.stringify({
        startTime: trimValue(pending.start),
        endTime: pending.end == null ? "" : trimValue(pending.end),
        ...(pending.cuts !== undefined ? { cuts: pending.cuts } : {}),
      }),
    }).catch(() => {});
  },

  // I / O: the selected item's start or end at the playhead
  setEdgeAtPlayhead(edge) {
    const item = this.selectedItem();
    const position = this.getPosition();
    if (!item || position == null) return;
    const now = round(position);
    if (!this.canMoveEdges(item)) {
      toast(item.kind === "clip" && item.data.kind !== "Vod" ? "Twitch clips can't be trimmed." : `You can't change this ${this.whatIs(item)}.`);
      return;
    }
    const start = edge === "start" ? now : item.start;
    const end = edge === "end" ? now : item.end;
    if (end - start < MIN_LENGTH) {
      toast(edge === "start" ? "The start has to be before the end." : "The end has to be after the start.");
      return;
    }
    if (end - start > this.clipLimit(item)) {
      toast(`Clips are at most ${formatClock(this.maxStoredClipSeconds).replace(/^0:0?|\.00$/g, "")}.`);
      return;
    }
    this.commitRange(item, start, end);
  },

  editTitle(item) {
    if (!this.canEditItem(item)) return;
    this.select(item);
    this.hooks.openItemEditor({
      title: item.title ?? "",
      color: item.color ?? (item.kind === "clip" ? "violet" : "yellow"),
      placeholder: item.kind === "clip" ? "Clip title" : "Title",
      onApply: (title, color) => this.saveTitle(item, title, color),
    });
  },

  async saveTitle(item, title, color) {
    if (item.kind === "clip") {
      if ((!title || title === item.title) && color === item.color) return;
    } else if (title === (item.title ?? "") && color === item.color) {
      return;
    }
    const target = await this.editTarget(item);
    if (!target) return;
    try {
      if (item.kind === "clip") {
        await api("PATCH", `/api/entries/${target.id}`, { title: title && title !== item.title ? title : null, color });
        this.hooks.onClipsChanged?.();
        this.edited();
      } else {
        const before = markerBody(item.data);
        const after = { ...before, label: title, color };
        await this.putMarker(target.id, after);
        this.remember({ type: "change", ref: this.ref("marker", target.id), before, after });
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
      rowId: this.isOwn(item) ? item.data.rowId ?? undefined : target.copied ? this.targetRow("video", this.rowVod(item.row?.id))?.id : undefined,
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
      this.remember({ type: "split", ref: this.ref("marker", target.id), before, after, newRef: this.ref("marker", created.id), newData: extra });
      this.selected = { type: "marker", id: created.id };
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
    this.load();
  },

  async copyItem(item, { quiet = false } = {}) {
    const row = this.targetRow(item.kind === "clip" ? "clip" : "video", this.rowVod(item.row?.id));
    try {
      const url = item.kind === "clip" ? `/api/entries/${item.id}/copy` : `/api/markers/${item.id}/copy`;
      const copy = this.fromServer(await api("POST", url, row ? { rowId: row.id } : {}));
      if (item.kind === "clip") {
        this.hooks.onClipsChanged?.();
      } else {
        this.remember({ type: "add", ref: this.ref("marker", copy.id), data: { ...markerBody(copy), rowId: copy.rowId ?? undefined } });
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
        text: "It's deleted for the whole team. Undo (Ctrl+Z) brings it back where it was.",
        confirm: "Delete",
      });
      if (!ok) return;
    }
    try {
      await this.deleteMarker(item.id);
      const ref = this.ref("marker", item.id);
      this.forgetRef(ref);
      // Undo puts it back on the row it was on, a teammate's too (as theirs)
      this.remember({ type: "delete", ref, data: { ...markerBody(item.data), rowId: item.data.rowId ?? undefined } });
      this.select(null);
    } catch (error) {
      toast(error.message, "error");
    }
    this.changed();
    this.load();
  },

  deleteSelected() {
    // Player 2: Delete only unloads it (badrfly 2026-11-16: it used to delete the clip or segment for good). Deleting for real is on
    // Player 1's timeline and the library cards.
    // A piece picked with the pointer is left out of the edit (or put back) instead
    if (this.mode === "item") {
      if (this.deletePiece?.()) return;
      this.hooks.onUnload?.();
      return;
    }
    const item = this.selectedItem();
    if (!item) return;
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
    if (this.tool === "clip") {
      this.setTool("pointer");
      return true;
    }
    if (this.selected) {
      this.select(null);
      return true;
    }
    return false;
  },

  // ---------- cut-out parts (Player 2's cut tool; SourceTimeline says what it cuts: cutTarget, cutOut, clickCutOut) ----------

  // Pressing with the cut tool: dragging marks the part to cut out, a click splits the item there
  startCutOut(event, canvas, rect, x, y, t) {
    const target = this.cutTarget();
    if (!target) return;
    const from = round(Math.max(target.start, Math.min(this.snapTime(t, rect.width), target.end)));
    this.snapAt = null;
    this.press = { kind: "cutout", from, at: t, min: target.start, max: target.end, startX: event.clientX, moved: false, rect };
    canvas.setPointerCapture(event.pointerId);
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
    this.linking = { ids: [] };
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
    // Since 2026-11-20 (badrfly) a video links segments from any of the member's rows, synced rows (other VODs) included
    const at = link.ids.indexOf(item.id);
    if (at >= 0) link.ids.splice(at, 1);
    else link.ids.push(item.id);
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
    const vodId = this.rowVod(body.rowId);
    const marker = this.fromServer(await api("POST", `/api/vods/${encodeURIComponent(vodId)}/markers`, this.toServer(body, body.rowId)));
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
    const rowId = body.rowId ?? this.markers.find((m) => m.id === id)?.rowId;
    const marker = this.fromServer(await api("PUT", `/api/markers/${id}`, this.toServer(body, rowId)));
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

  // cuts: undefined keeps the clip's cut-out parts, [] removes them
  async trimClip(id, times) {
    const { start, end, cuts } = this.toServer(times, this.clips.find((c) => c.id === id)?.rowId);
    await api("PUT", `/api/entries/${id}/trim`, {
      startTime: trimValue(start),
      endTime: end == null ? "" : trimValue(end),
      ...(cuts !== undefined ? { cuts } : {}),
    });
    await this.load();
    this.hooks.onClipsChanged?.();
    this.edited();
  },

  // ---------- undo / redo (last 10 per VOD, this tab only) ----------
  //
  // Steps name markers and clips through refs ({ type, id }), one per item per VOD, shared by every step that names it.
  // Undoing a delete (or redoing an add or split) makes the marker again with a NEW id; the ref follows it, so older steps
  // change the marker as it is now. (They used to keep their own copy of the old id: after undoing a few deletes, undoing
  // the splits before them changed or deleted the wrong segments, or failed, and the history fell apart.)

  history() {
    if (!this.histories.has(this.vodId)) this.histories.set(this.vodId, { undo: [], redo: [], refs: new Map() });
    return this.histories.get(this.vodId);
  },

  ref(type, id) {
    const refs = this.history().refs;
    const key = `${type}:${id}`;
    if (!refs.has(key)) refs.set(key, { type, id });
    return refs.get(key);
  },

  // The marker behind ref is gone: a new one that happens to get its id isn't the same item
  forgetRef(ref) {
    const refs = this.history().refs;
    const key = `${ref.type}:${ref.id}`;
    if (refs.get(key) === ref) refs.delete(key);
  },

  // The marker behind ref was made again with a new id
  moveRef(ref, id) {
    this.forgetRef(ref);
    ref.id = id;
    this.history().refs.set(`${ref.type}:${id}`, ref);
  },

  remember(step) {
    const history = this.history();
    // A change that didn't touch the cut-out parts keeps whatever they are when it's undone or redone (null = keep): undoing
    // a rename used to bring back cut-out parts an Apply in Player 2 had changed since
    if (step.type === "change" && JSON.stringify(step.before.cuts ?? null) === JSON.stringify(step.after.cuts ?? null)) {
      step.before = { ...step.before, cuts: null };
      step.after = { ...step.after, cuts: null };
    }
    history.undo.push(step);
    if (history.undo.length > HISTORY_LIMIT) history.undo.shift();
    history.redo = [];
    this.changed();
  },

  // A stored clip's re-trim waits 3 s before it's sent (it downloads again): it can be undone before that too
  hasPendingTrim() {
    return Boolean(this.pendingTrim && this.mode === "vod");
  },

  canUndo() {
    return Boolean(this.vodId && (this.history().undo.length || this.hasPendingTrim()) && !me?.support);
  },

  canRedo() {
    return Boolean(this.vodId && this.history().redo.length && !me?.support);
  },

  undo() {
    return this.queueReplay("undo", "redo", true);
  },

  redo() {
    return this.queueReplay("redo", "undo", false);
  },

  // Presses while a step is still being sent wait their turn (they used to be dropped, so pressing Ctrl+Z three times
  // quickly undid one or two steps)
  queueReplay(from, to, reverse) {
    const vodId = this.vodId;
    this.replayChain = (this.replayChain ?? Promise.resolve()).then(() => (this.vodId === vodId ? this.replay(from, to, reverse) : null))
      .catch((error) => console.warn("Undo/redo failed:", error));
    return this.replayChain;
  },

  async replay(from, to, reverse) {
    if (!this.vodId) return;
    const history = this.history();
    // Undo with a stored clip's re-trim still waiting: it's dropped (never sent, nothing downloads); redo sends it
    if (reverse && this.hasPendingTrim()) {
      const pending = this.pendingTrim;
      clearTimeout(pending.timer);
      this.pendingTrim = null;
      history.redo.push({ type: "trim", ref: this.ref("clip", pending.id), before: pending.before, after: { start: pending.start, end: pending.end, cuts: pending.cuts } });
      this.changed();
      await this.load();
      return;
    }
    const step = history[from].pop();
    if (!step) return;
    this.changed();
    try {
      await this.applyStep(step, reverse);
      history[to].push(step);
    } catch (error) {
      toast(`Couldn't ${reverse ? "undo" : "redo"} that: ${error.message}`, "error");
    }
    this.changed();
    await this.load();
  },

  async applyStep(step, reverse) {
    switch (step.type) {
      case "add":
        if (reverse) {
          await this.deleteMarker(step.ref.id);
          this.forgetRef(step.ref);
        } else {
          this.moveRef(step.ref, (await this.postMarker(this.ownRowOnly(step.data))).id);
        }
        break;
      case "delete":
        if (reverse) {
          this.moveRef(step.ref, (await this.restoreMarker(step.data)).id);
        } else {
          await this.deleteMarker(step.ref.id);
          this.forgetRef(step.ref);
        }
        break;
      case "change":
        await this.putMarker(step.ref.id, reverse ? step.before : step.after);
        break;
      case "split":
        if (reverse) {
          await this.deleteMarker(step.newRef.id);
          this.forgetRef(step.newRef);
          await this.putMarker(step.ref.id, step.before);
        } else {
          await this.putMarker(step.ref.id, step.after);
          this.moveRef(step.newRef, (await this.postMarker(this.ownRowOnly(step.newData))).id);
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

  // Undoing a delete: back on the row it was on, as its creator's (the server allows a teammate's row for members who may delete
  // anyone's markers, which deleting it needed). A row that's gone since (or a refusal) puts it on the caller's own row.
  async restoreMarker(data) {
    const row = this.rows.find((r) => r.id === data.rowId);
    if (row && row.userId !== me?.id) {
      try {
        return await this.postMarker({ ...data, restore: true });
      } catch (error) {
        if (error.status !== 400 && error.status !== 403) throw error;
      }
    }
    return this.postMarker(this.ownRowOnly(data));
  },

  // A re-added marker may only name one of the caller's rows that still exists
  ownRowOnly(data) {
    const row = this.rows.find((r) => r.id === data.rowId);
    return row && row.userId === me?.id ? data : { ...data, rowId: undefined };
  },
};
