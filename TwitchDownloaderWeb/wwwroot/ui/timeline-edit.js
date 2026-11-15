// Editing on the Player 1 timeline (frontend rework step 3b), mixed into Timeline (ui/timeline.js).
//
// Tools (V/C/Z/H): pointer (click selects an item and seeks to it; drag the selected item's edges), cut (click a segment to
// split it in two), zoom (click in, right click out) and hand (drag to scroll the view). S / X mark a segment / clip
// (start, then end), M a moment; the title and color editor in the tools bar names it, and it goes on the member's own row
// (the last own row clicked, else their first of that kind). Others' items: view only; with "edit anyone's entries" each
// change asks "Edit original" or "Make a copy" (Enter = copy); anyone who may add things can copy them from the right-click
// menu. Undo/redo: the last 10 marker changes and clip re-trims per VOD, in this tab only.
//
// Every time a new kind of item gets drawn here, add its times to snapPoints().

import { api, me, can, toast } from "./core.js";
import { confirmAction, chooseEditTarget } from "./dialogs.js";
import { openMenu } from "./menu.js";
import { formatClock } from "./media.js";

const EDGE_PX = 7;
const SNAP_PX = 8;
const DRAG_THRESHOLD_PX = 3;
const MIN_LENGTH = 0.5;
const HISTORY_LIMIT = 10;
const SNAP_KEY = "clipLibrary.snap"; // shared with the old page's player bar

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
    // onCreated({ kind, data }) for new segments and clips (Player 2 picks them up in step 4)
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
    this.histories = new Map();
    this.lastColor = "yellow";
  },

  resetEditing() {
    this.selected = null;
    this.marking = null;
    this.draft = null;
    this.preview = null;
    this.press = null;
    this.activeRowId = null;
    this.tool = "pointer";
    this.hooks.closeItemEditor?.();
    this.changed();
  },

  changed() {
    this.dirty = true;
    this.hooks.onChange?.();
  },

  // ---------- who may do what ----------

  canAdd() {
    return Boolean(this.vodId) && !me?.support && can("AddEntries");
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

  // Edges can be dragged on segments and on reference-only VOD clips (stored clips are trimmed in Player 2)
  canMoveEdges(item) {
    if (!this.canEditItem(item)) return false;
    if (item.kind === "segment") return true;
    return item.kind === "clip" && item.reference && item.data.kind === "Vod";
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
    if (this.isOwn(item) || !this.canCopyItem(item)) return { id: item.id, copied: false };
    const choice = await chooseEditTarget({ what: this.whatIs(item), owner: item.by });
    if (!choice) return null;
    if (choice === "original") return { id: item.id, copied: false };
    const copy = await this.copyItem(item, { quiet: true });
    return copy ? { id: copy.id, copied: true } : null;
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
    this.selected = item ? { type: item.kind === "clip" ? "clip" : "marker", id: item.id } : null;
    this.changed();
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
    if (!this.vodId || this.tab === "p2" || event.button !== 0) return;
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const t = Math.max(0, Math.min(this.xToTime(x, rect.width), this.length()));

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
    this.seekTo(item ? item.start : t);
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
      this.seekTo(Math.max(0, Math.min(t, this.length())));
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
      if (edge === "start") start = Math.max(0, Math.min(t, end - MIN_LENGTH));
      else end = Math.min(this.length(), Math.max(t, start + MIN_LENGTH));
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
    if (!this.vodId || this.tab === "p2") return;
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
    if (!this.canAdd()) return;
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
    if (!this.canAdd()) return;
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
    try {
      if (item.kind === "clip") {
        const before = { start: item.data.start, end: item.data.end ?? null };
        const after = { start, end };
        await this.trimClip(target.id, after);
        this.remember({ type: "trim", ref: { id: target.id }, before: target.copied ? after : before, after });
        this.selected = { type: "clip", id: target.id };
      } else {
        const before = markerBody(item.data);
        const after = { ...before, seconds: start, endSeconds: end, cuts: null };
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
        await api("DELETE", `/api/entries/${item.id}`);
        this.select(null);
        await this.load();
        this.hooks.onClipsChanged?.();
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
    if (!this.canDeleteItem(item)) {
      toast(`You can't delete this ${this.whatIs(item)}.`);
      return;
    }
    this.deleteItem(item);
  },

  // Esc: stop marking, close the title editor, or unselect
  escape() {
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

  // ---------- server calls ----------

  async postMarker(body) {
    const marker = await api("POST", `/api/vods/${encodeURIComponent(this.vodId)}/markers`, body);
    this.markers = [...this.markers.filter((m) => m.id !== marker.id), marker];
    if (marker.liveEntry) {
      this.hooks.onClipsChanged?.();
      this.load();
    }
    this.dirty = true;
    return marker;
  },

  async putMarker(id, body) {
    const marker = await api("PUT", `/api/markers/${id}`, body);
    this.markers = this.markers.map((m) => (m.id === id ? marker : m));
    if (marker.liveEntry) this.hooks.onClipsChanged?.();
    this.dirty = true;
    return marker;
  },

  async deleteMarker(id) {
    await api("DELETE", `/api/markers/${id}`);
    this.markers = this.markers.filter((m) => m.id !== id);
    this.dirty = true;
  },

  async trimClip(id, { start, end }) {
    await api("PUT", `/api/entries/${id}/trim`, { startTime: trimValue(start), endTime: end == null ? "" : trimValue(end) });
    await this.load();
    this.hooks.onClipsChanged?.();
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
    }
  },

  // A re-added marker may only name one of the caller's rows that still exists
  ownRowOnly(data) {
    const row = this.rows.find((r) => r.id === data.rowId);
    return row && row.userId === me?.id ? data : { ...data, rowId: undefined };
  },
};
