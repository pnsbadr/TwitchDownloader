// Player 2's timeline: saved edits (badrfly 2026-11-16; design and API in rework/player2-edits-spec.md).
//
// The item loaded in Player 2 (a clip, segment or video) is the SOURCE, like a file on a drive in a video editor. Its timeline
// runs from 0:00 to the item's length (a video: its segments end to end, in play order); nothing outside the item is shown
// and nothing can be dragged past it (to get more, extend the item on Player 1). What's done here is an EDIT of the source:
// its ends trimmed inward (the white outline's edges) and parts cut out (cut tool). Edits are saved on the server as you
// work (/api/edits, ~0.6 s after the last change) and never change Player 1, until one is applied to it ("Apply to Player 1":
// what the edit leaves out becomes the item's cut-out parts, grayed out there; the server keeps a history, so "Undo last
// apply" and "Revert to original" always work).
//
// "Original" is the item as it is on Player 1 (view only: the first change makes a new edit from it). Someone else's edit
// is view only too: the first change makes your own copy of it. Each edit keeps what it leaves out per part in VOD seconds,
// like the item's own cut-out parts, so it stays valid when the item moves on Player 1 (and is cut down to fit when the
// item gets shorter). A video's segments keep their own cut-out parts (made on the segment): those stay out of every edit.
//
// Times on this timeline are seconds into the source ("source time"); sourceParts() maps them to the VOD for the player.

import { api, me, can, toast } from "./core.js";
import { Timeline } from "./timeline.js";
import { normalizeCuts, MIN_KEPT_SECONDS } from "./cuts.js";
import { confirmAction, askText } from "./dialogs.js";
import { openMenu } from "./menu.js";

const SAVE_DELAY_MS = 600;
const EDGE_PX = 7;
const DRAG_THRESHOLD_PX = 3;
const HISTORY_LIMIT = 50;
const LAST_EDIT_KEY = "clipco.p2.lastEdit"; // { "clip:12": editId | "original" }: the edit last open per item

const round = (seconds) => Math.round(seconds * 100) / 100;
const clone = (partCuts) => Object.fromEntries(Object.entries(partCuts ?? {}).map(([id, cuts]) => [id, cuts.map((c) => ({ ...c }))]));

// Sorted, merged ranges (touching ones joined)
export function mergeRanges(ranges) {
  const sorted = ranges.filter((r) => r.end - r.start > 0.001).map((r) => ({ ...r })).sort((a, b) => a.start - b.start);
  const out = [];
  for (const range of sorted) {
    const last = out[out.length - 1];
    if (last && range.start <= last.end + 0.001) last.end = Math.max(last.end, range.end);
    else out.push(range);
  }
  return out;
}

// ranges minus [from, to)
function subtract(ranges, from, to) {
  if (to <= from) return ranges;
  const out = [];
  for (const r of ranges) {
    if (r.end <= from || r.start >= to) out.push(r);
    else {
      if (r.start < from) out.push({ start: r.start, end: from });
      if (r.end > to) out.push({ start: to, end: r.end });
    }
  }
  return out;
}

function within(ranges, from, to) {
  return ranges.map((r) => ({ start: Math.max(r.start, from), end: Math.min(r.end, to) })).filter((r) => r.end > r.start);
}

function sameCuts(a, b) {
  return JSON.stringify(a ?? []) === JSON.stringify(b ?? []);
}

function loadLastEdits() {
  try {
    return JSON.parse(localStorage.getItem(LAST_EDIT_KEY) ?? "{}") ?? {};
  } catch {
    return {};
  }
}

// "5 min ago"
export function ago(iso) {
  const seconds = Math.max(0, (Date.now() - Date.parse(iso)) / 1000);
  if (!Number.isFinite(seconds)) return "";
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86400)} days ago`;
}

export class SourceTimeline extends Timeline {
  // hooks as Timeline's "item" mode, plus onEditsChanged() (the edit picker), onApplied() (Player 1 reads the VOD again)
  constructor(options) {
    super({ ...options, mode: "item" });
    this.source = null;     // { kind: clip|segment|video, title, length, parts: [{ id, offset, vodStart, vodEnd, locked, applied, … }], canEdit, … }
    this.edits = [];        // the item's edits: the server's plus ones not saved yet (id null)
    this.applies = [];      // GET /api/edits' apply history, newest first
    this.serverCanApply = null;
    this.editsLoaded = false;
    this.edit = null;       // the one shown: an edit, or { original: true } (the item as on Player 1)
    this.previewCuts = null; // partCuts while an edge is dragged
    this.saveTimer = null;
    this.saveChain = Promise.resolve();
    this.editHistory = { undo: [], redo: [] };
    this.historyEdit = null;
    this.cutNote = "left out of this edit";
  }

  // ---------- loading ----------

  // Player 2: one segment ({ type: "marker" }), clip ({ type: "clip" }) or video ({ type: "video" }). editId = the edit to
  // show (else the one last open for this item, else Original)
  async openItem({ type, id, vodId, lengthSeconds, editId }) {
    await this.saveNow();
    this.source = null;
    this.resetEdits();
    this.wantEdit = editId;
    await super.openItem({ type, id, vodId, lengthSeconds });
    await this.loadEdits({ pick: true });
  }

  async close() {
    await this.saveNow();
    this.source = null;
    this.resetEdits();
    await super.close();
  }

  resetEdits() {
    clearTimeout(this.saveTimer);
    this.saveTimer = null;
    this.edits = [];
    this.applies = [];
    this.serverCanApply = null;
    this.serverCanRevert = null;
    this.applyMakesCopy = false;
    this.editsLoaded = false;
    this.edit = null;
    this.previewCuts = null;
    this.editHistory = { undo: [], redo: [] };
    this.historyEdit = null;
    this.hooks.onEditsChanged?.();
  }

  // A copy of the item was made (Edit original / Make a copy, or an apply on someone else's item): go on with it
  focusOn(focus, { editId } = {}) {
    this.resetEdits();
    this.wantEdit = editId;
    this.source = null;
    super.focusOn(focus);
    this.loadEdits({ pick: true });
  }

  // GET /api/vods/{id}/timeline: build the source from the item
  applyItem(timeline) {
    const focus = this.focus;
    if (!focus) return;
    const source = focus.type === "video" ? this.videoSource(timeline) : this.itemSource(timeline);
    if (!source || source.gone) {
      this.markers = [];
      this.clips = [];
      this.video = null;
      this.source = null;
      this.hooks.onFocusGone?.(source?.gone ?? null);
      return;
    }
    const row = {
      id: source.rowId ?? 0, userId: source.userId, username: source.by, kind: source.kind === "clip" ? "Clip" : "Video", position: 0,
      name: source.title || `Untitled ${source.kind}`, canEdit: false,
    };
    const rowsChanged = JSON.stringify([row]) !== JSON.stringify(this.rows);
    this.rows = [row];
    this.source = source;
    if (!this.edit || this.edit.original) this.edit = this.originalEdit();
    if (rowsChanged) {
      this.renderHeads();
      this.renderFooter();
    }
    this.updateWindow();
    this.hooks.onFocusChanged?.(this.focusInfo());
    this.hooks.onEditsChanged?.();
  }

  itemSource(timeline) {
    const focus = this.focus;
    const isClip = focus.type === "clip";
    const found = isClip ? (timeline.clips ?? []).find((c) => c.id === focus.id) : (timeline.markers ?? []).find((m) => m.id === focus.id);
    if (!found) return null;
    if (!isClip && found.endSeconds == null) return { gone: "The segment in Player 2 is now a marker, so it was unloaded." };
    const vodStart = isClip ? (found.startSeconds ?? found.start) : found.seconds;
    const vodEnd = isClip ? (found.endSeconds ?? found.end ?? this.vodLength()) : found.endSeconds;
    const title = isClip ? found.title : found.label;
    this.markers = isClip ? [] : [found];
    this.clips = isClip ? [found] : [];
    this.videos = [];
    this.video = null;
    return {
      kind: isClip ? "clip" : "segment",
      title,
      length: vodEnd - vodStart,
      parts: [{
        id: found.id, kind: isClip ? "clip" : "segment", offset: 0, vodStart, vodEnd, locked: [],
        applied: normalizeCuts(found.cuts ?? [], vodStart, vodEnd), title, color: found.color, by: found.createdBy,
        reference: Boolean(found.referenceOnly), data: found,
      }],
      data: found,
      canEdit: Boolean(found.canEdit),
      rowId: found.rowId,
      userId: isClip ? Number(found.addedByUserId) : found.createdByUserId,
      by: found.createdBy,
      twitchClip: isClip && found.kind != null && found.kind !== "Vod",
    };
  }

  videoSource(timeline) {
    const video = (timeline.videos ?? []).find((v) => v.id === this.focus.id);
    if (!video || !video.segments?.length) return null;
    const markers = timeline.markers ?? [];
    let offset = 0;
    const parts = [...video.segments].sort((a, b) => a.position - b.position).map((segment, index) => {
      const marker = markers.find((m) => m.id === segment.markerId);
      const vodStart = marker?.seconds ?? segment.startSeconds;
      const vodEnd = marker?.endSeconds ?? segment.endSeconds;
      const part = {
        id: segment.markerId, kind: "segment", offset, vodStart, vodEnd,
        locked: normalizeCuts(marker?.cuts ?? segment.cuts ?? [], vodStart, vodEnd),
        applied: normalizeCuts(segment.videoCuts ?? [], vodStart, vodEnd),
        title: marker?.label ?? segment.label, color: marker?.color ?? segment.color, by: marker?.createdBy ?? video.createdBy, index: index + 1,
        data: marker ?? { id: segment.markerId, seconds: vodStart, endSeconds: vodEnd, label: segment.label, color: segment.color, canEdit: false, canDelete: false },
      };
      offset += vodEnd - vodStart;
      return part;
    });
    const ids = new Set(parts.map((p) => p.id));
    this.markers = markers.filter((m) => ids.has(m.id));
    this.clips = [];
    this.video = video;
    this.videos = [video];
    return { kind: "video", title: video.title, length: offset, parts, data: video, canEdit: Boolean(video.canEdit), rowId: video.rowId, userId: video.userId, by: video.createdBy };
  }

  // GET /api/edits?entryId= | markerId= | videoId=: the team's edits of the item and its apply history. pick = choose the
  // edit to show (just opened)
  async loadEdits({ pick = false } = {}) {
    const focus = this.focus;
    if (!focus) return;
    let result;
    try {
      result = await api("GET", `/api/edits?${this.itemQuery(focus)}`);
    } catch (error) {
      console.warn("Couldn't load the item's edits:", error);
      result = { edits: [], applies: [] };
    }
    if (this.focus !== focus) return;
    const list = Array.isArray(result) ? result : result?.edits ?? [];
    const kind = { clip: "clip", marker: "segment", video: "video" }[focus.type];
    // Edits with changes on their way keep what's on screen
    const busy = this.edits.filter((e) => e.id != null && (e.dirty || e.saving));
    const server = list.map((data) => busy.find((e) => e.id === data.id) ?? this.fromServer(data, kind, focus));
    this.edits = [...server, ...this.edits.filter((e) => e.id == null && !e.deleted)];
    this.applies = Array.isArray(result) ? [] : result?.applies ?? [];
    this.serverCanApply = Array.isArray(result) ? null : result?.canApply ?? null;
    this.serverCanRevert = Array.isArray(result) ? null : result?.canRevert ?? null;
    this.applyMakesCopy = !Array.isArray(result) && Boolean(result?.applyMakesCopy);
    this.editsLoaded = true;
    if (pick) {
      const want = this.wantEdit ?? loadLastEdits()[this.itemKey(focus)];
      this.wantEdit = undefined;
      const found = want != null && want !== "original" ? this.edits.find((e) => e.id === want) : null;
      this.edit = found ?? this.originalEdit();
      this.editHistory = { undo: [], redo: [] };
      this.historyEdit = null;
    } else if (this.edit && !this.edit.original) {
      const same = this.edits.find((e) => e === this.edit || (e.id != null && e.id === this.edit.id));
      if (same) {
        this.edit = same;
      } else {
        toast(`"${this.editName(this.edit)}" was deleted.`);
        this.edit = this.originalEdit();
      }
    }
    this.changed();
    this.hooks.onFocusChanged?.(this.focusInfo());
    this.hooks.onEditsChanged?.();
  }

  fromServer(data, kind, focus) {
    const own = data.createdByUserId != null && String(data.createdByUserId) === String(me?.id);
    const partCuts = kind === "video"
      ? Object.fromEntries((data.parts ?? []).map((p) => [String(p.markerId), p.cuts ?? []]))
      : { [String(focus.id)]: data.cuts ?? [] };
    return {
      id: data.id, title: data.title || null, kind, itemId: focus.id, focus: { ...focus },
      createdByUserId: data.createdByUserId, createdBy: data.createdBy, own,
      canEdit: data.canEdit ?? own, canDelete: data.canDelete ?? own,
      createdAtUtc: data.createdAtUtc, updatedAtUtc: data.updatedAtUtc, layout: data.layout ?? null, partCuts,
    };
  }

  itemKey(focus = this.focus) {
    return `${focus.type}:${focus.id}`;
  }

  itemQuery(focus) {
    return { clip: "entryId", marker: "markerId", video: "videoId" }[focus.type] + "=" + encodeURIComponent(focus.id);
  }

  itemBody(focus) {
    return { [{ clip: "entryId", marker: "markerId", video: "videoId" }[focus.type]]: focus.id };
  }

  // ---------- the source in source time ----------

  // [{ id, offset, vodStart, vodEnd }]: where each part of the source is in the VOD
  sourceParts() {
    return (this.source?.parts ?? []).map((p) => ({ id: p.id, offset: p.offset, vodStart: p.vodStart, vodEnd: p.vodEnd }));
  }

  length() {
    return this.source?.length ?? 0;
  }

  // What Original shows: the item's own cut-out parts (a video: its cuts in this video)
  appliedCuts() {
    return Object.fromEntries((this.source?.parts ?? []).map((p) => [String(p.id), p.applied]));
  }

  originalEdit() {
    return { original: true, id: null, title: "Original", own: false, canEdit: false, canDelete: false, partCuts: this.appliedCuts() };
  }

  // An edit's cut parts per part, cut down to fit the parts as they are now
  fitted(partCuts) {
    const out = {};
    for (const part of this.source?.parts ?? []) out[String(part.id)] = normalizeCuts(partCuts?.[String(part.id)] ?? [], part.vodStart, part.vodEnd);
    return out;
  }

  // An edit's cut parts in source time (not the segments' own: see lockedRanges)
  editRanges(partCuts) {
    const out = [];
    for (const part of this.source?.parts ?? []) {
      for (const cut of normalizeCuts(partCuts?.[String(part.id)] ?? [], part.vodStart, part.vodEnd)) {
        out.push({ start: part.offset + cut.start - part.vodStart, end: part.offset + cut.end - part.vodStart });
      }
    }
    return mergeRanges(out);
  }

  // A video's segments' own cut-out parts: out of every edit
  lockedRanges() {
    const out = [];
    for (const part of this.source?.parts ?? []) {
      for (const cut of part.locked) out.push({ start: part.offset + cut.start - part.vodStart, end: part.offset + cut.end - part.vodStart });
    }
    return mergeRanges(out);
  }

  // Everything an edit leaves out, in source time
  removedRanges(partCuts = this.shownCuts()) {
    return mergeRanges([...this.lockedRanges(), ...this.editRanges(partCuts)]);
  }

  // Source-time ranges back to per-part VOD cuts
  toPartCuts(ranges) {
    const out = {};
    for (const part of this.source?.parts ?? []) {
      const length = part.vodEnd - part.vodStart;
      const cuts = within(ranges, part.offset, part.offset + length)
        .map((r) => ({ start: round(part.vodStart + r.start - part.offset), end: round(part.vodStart + r.end - part.offset) }));
      out[String(part.id)] = normalizeCuts(cuts, part.vodStart, part.vodEnd);
    }
    return out;
  }

  shownCuts() {
    return this.previewCuts ?? this.edit?.partCuts ?? {};
  }

  // What's kept, in source time
  keptRanges(partCuts = this.shownCuts()) {
    const removed = this.removedRanges(partCuts);
    const kept = [];
    let at = 0;
    for (const r of removed) {
      if (r.start > at) kept.push({ start: at, end: r.start });
      at = Math.max(at, r.end);
    }
    if (this.length() > at) kept.push({ start: at, end: this.length() });
    return kept.filter((r) => r.end - r.start > 0.001);
  }

  keptRange(partCuts) {
    const kept = this.keptRanges(partCuts);
    return kept.length ? { start: kept[0].start, end: kept[kept.length - 1].end } : { start: 0, end: 0 };
  }

  keptTotal(partCuts) {
    return this.keptRanges(partCuts).reduce((sum, r) => sum + r.end - r.start, 0);
  }

  // The muted / saved-audio ranges of the VOD, moved into source time
  setDetails(details) {
    super.setDetails(details);
    const out = [];
    for (const part of this.source?.parts ?? []) {
      for (const range of this.vodAudio ?? []) {
        const start = Math.max(range.start, part.vodStart);
        const end = Math.min(range.end, part.vodEnd);
        if (end > start) out.push({ start: part.offset + start - part.vodStart, end: part.offset + end - part.vodStart, clean: range.clean });
      }
    }
    this.audio = out;
    this.dirty = true;
  }

  // The whole source is in view to begin with; zooming stays inside it
  updateWindow() {
    const old = this.window;
    const fitted = !old || (Math.abs(this.view.start - old.start) < 0.01 && Math.abs(this.view.end - old.end) < 0.01);
    this.window = { start: 0, end: Math.max(this.length(), 0.5) };
    if (fitted || this.view.end > this.window.end) this.setView(0, this.window.end);
  }

  // ---------- drawing ----------

  rowTitle(row) {
    return row.name;
  }

  itemsOf(row) {
    const source = this.source;
    if (!source || row !== this.rows[0]) return [];
    const removed = this.removedRanges();
    return source.parts.map((part) => {
      const start = part.offset;
      const end = part.offset + (part.vodEnd - part.vodStart);
      return {
        kind: part.kind, id: part.id, start, end, offset: part.offset, vodStart: part.vodStart, vodEnd: part.vodEnd,
        title: source.kind === "video" ? part.title : source.title, color: part.color, by: part.by, reference: part.reference,
        cuts: within(removed, start, end), row, data: part.data, videoIndex: source.kind === "video" ? part.index : undefined,
        ownCutsVod: part.locked, videoCutsVod: part.applied,
      };
    });
  }

  // The item's first part (a clip or segment: the item itself)
  focusItem() {
    if (!this.source || this.source.kind === "video") return null;
    return this.itemsOf(this.rows[0])[0] ?? null;
  }

  // Player 2's header: { title, length } (length = what the edit keeps)
  focusInfo() {
    if (!this.source) return null;
    const range = this.keptRange();
    return { title: this.source.title, length: this.keptTotal(), start: range.start, end: range.end };
  }

  // Items aren't selected one by one here: the white outline is what the edit keeps
  isSelected() {
    return false;
  }

  select() {
    this.selected = null;
    this.changed();
  }

  canMoveEdges() {
    return false;
  }

  // Over everything else: what the edit keeps (white outline, grips on its ends when it can be changed)
  drawOverlay(ctx, width) {
    if (!this.source || !this.rows.length) return;
    const range = this.keptRange();
    if (range.end <= range.start) return;
    const h = this.rowHeight;
    const x1 = this.timeToX(range.start, width);
    const x2 = this.timeToX(range.end, width);
    ctx.save();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 2;
    ctx.strokeRect(x1, 3, Math.max(2, x2 - x1), h - 6);
    if (this.canMakeEdits()) {
      ctx.fillStyle = "#fff";
      for (const x of [x1, x2]) {
        if (x < -4 || x > width + 4) continue;
        ctx.fillRect(x - 2.5, h / 2 - 8, 5, 16);
      }
    }
    ctx.restore();
  }

  // ---------- who may do what ----------

  // Edits: anyone who may add things, on any clip (not old Twitch clips), segment or video they can see
  canMakeEdits() {
    return Boolean(this.source) && !me?.support && can("AddEntries") && !this.source.twitchClip;
  }

  viewOnlyReason() {
    if (!this.source) return "";
    if (this.source.twitchClip) return "Twitch clips can't be edited here.";
    return "View only.";
  }

  // Apply to Player 1: own item with "add", someone else's with "edit anyone's" (else onto a copy); the server says
  canApply() {
    if (!this.canMakeEdits() || !this.edit || this.edit.original) return false;
    return this.serverCanApply ?? true;
  }

  // Undo last apply / Revert to original: whoever may change the item
  canRevert() {
    if (me?.support || !this.applies.length) return false;
    return this.serverCanRevert ?? Boolean(this.source?.canEdit);
  }

  editName(edit = this.edit) {
    if (!edit) return "";
    if (edit.original) return "Original";
    if (edit.title) return edit.title;
    const ordered = [...this.edits].sort((a, b) => String(a.createdAtUtc ?? "").localeCompare(String(b.createdAtUtc ?? "")));
    const index = ordered.indexOf(edit);
    return `Edit ${index >= 0 ? index + 1 : this.edits.length + 1}`;
  }

  // The edit's cut parts are the ones on Player 1 right now
  isApplied(edit) {
    if (!edit || edit.original || !this.source) return false;
    const mine = this.fitted(edit.partCuts);
    return this.source.parts.every((p) => sameCuts(mine[String(p.id)], p.applied));
  }

  // ---------- changing the edit ----------

  // Original or someone else's edit: the first change makes your own edit (a copy of what's shown)
  ensureOwnEdit() {
    const shown = this.edit;
    if (shown && !shown.original && shown.own && shown.canEdit !== false) return shown;
    const edit = this.newEdit({
      partCuts: clone(this.fitted(shown?.partCuts)),
      title: shown && !shown.original ? `Copy of ${this.editName(shown)}`.slice(0, 200) : null,
      layout: shown?.layout ?? null,
    });
    this.edit = edit;
    this.rememberPick();
    toast(shown?.original
      ? `Made a new edit, "${this.editName(edit)}". Player 1 doesn't change unless you apply it.`
      : `Made your own copy, "${this.editName(edit)}", to change.`);
    return edit;
  }

  newEdit({ partCuts = {}, title = null, layout = null } = {}) {
    const focus = { ...this.focus };
    const edit = {
      id: null, title, kind: this.source.kind, itemId: focus.id, focus, own: true, canEdit: true, canDelete: true,
      createdByUserId: me?.id, createdBy: me?.username, createdAtUtc: new Date().toISOString(), layout, partCuts, dirty: true,
    };
    this.edits.push(edit);
    this.scheduleSave();
    this.hooks.onEditsChanged?.();
    return edit;
  }

  // A new version of what the edit leaves out (per-part VOD cuts). false when nothing changed or it can't.
  changeEdit(partCuts) {
    if (!this.canMakeEdits()) {
      toast(this.viewOnlyReason());
      return false;
    }
    const fitted = this.fitted(partCuts);
    if (this.keptTotal(fitted) < MIN_KEPT_SECONDS - 0.001) {
      toast("That would cut out everything.");
      return false;
    }
    if (JSON.stringify(fitted) === JSON.stringify(this.fitted(this.edit?.partCuts))) return false;
    const before = clone(this.fitted(this.edit?.partCuts));
    const edit = this.ensureOwnEdit();
    this.remember({ edit, before, after: clone(fitted) });
    edit.partCuts = fitted;
    edit.dirty = true;
    this.scheduleSave();
    this.changed();
    this.hooks.onFocusChanged?.(this.focusInfo());
    this.hooks.onEditsChanged?.();
    return true;
  }

  // The edit's ranges (not the segments' own), source time
  editableRanges(partCuts = this.edit?.partCuts) {
    return this.editRanges(partCuts);
  }

  // A trimmed end at t: everything before (start) or after (end) t is left out; dragging an end outward brings back what
  // the old trim left out
  trimmed(edge, t, partCuts = this.edit?.partCuts) {
    const ranges = this.editableRanges(partCuts);
    const old = this.keptRange(partCuts);
    const length = this.length();
    if (edge === "start") return this.toPartCuts([...within(subtract(ranges, t, old.start), t, length), { start: 0, end: t }]);
    return this.toPartCuts([...within(subtract(ranges, old.end, t), 0, t), { start: t, end: length }]);
  }

  cutTarget() {
    if (!this.source) return null;
    if (!this.canMakeEdits()) {
      toast(this.viewOnlyReason());
      return null;
    }
    return { start: 0, end: this.length() };
  }

  cutOut(start, end) {
    this.changeEdit(this.toPartCuts([...this.editableRanges(), { start, end }]));
  }

  putBack(range) {
    this.changeEdit(this.toPartCuts(subtract(this.editableRanges(), range.start, range.end)));
  }

  clickCutOut(t) {
    const cut = this.editableRanges().find((r) => t >= r.start && t < r.end);
    if (cut) {
      this.putBack(cut);
      return;
    }
    if (this.lockedRanges().some((r) => t >= r.start && t < r.end)) {
      toast("This part is cut out of the segment itself. Load the segment in Player 2 to change that.");
      return;
    }
    toast("Drag across the part you want to cut out.");
  }

  // I / O: the edit's start or end at the playhead
  setEdgeAtPlayhead(edge) {
    const position = this.getPosition();
    if (position == null || !this.source) return;
    const t = round(Math.max(0, Math.min(position, this.length())));
    const range = this.keptRange();
    if (edge === "start" ? t > range.end - MIN_KEPT_SECONDS : t < range.start + MIN_KEPT_SECONDS) {
      toast(edge === "start" ? "The start has to be before the end." : "The end has to be after the start.");
      return;
    }
    this.changeEdit(this.trimmed(edge, t));
  }

  // ---------- pointer: the edit's ends ----------

  keptEdgeAt(x, y, width) {
    if (!this.canMakeEdits() || Math.floor(y / this.rowHeight) !== 0) return null;
    const range = this.keptRange();
    const dStart = Math.abs(x - this.timeToX(range.start, width));
    const dEnd = Math.abs(x - this.timeToX(range.end, width));
    if (Math.min(dStart, dEnd) > EDGE_PX) return null;
    return dStart < dEnd ? "start" : "end";
  }

  pointerDown(event, canvas) {
    if (this.vodId && event.button === 0 && this.tool === "pointer" && canvas === this.track && !this.hasLayoutPress(event, canvas)) {
      const rect = canvas.getBoundingClientRect();
      const edge = this.keptEdgeAt(event.clientX - rect.left, event.clientY - rect.top, rect.width);
      if (edge) {
        this.press = { kind: "keptEdge", edge, startX: event.clientX, moved: false, rect };
        canvas.setPointerCapture(event.pointerId);
        this.updateCursor(true);
        return;
      }
    }
    super.pointerDown(event, canvas);
  }

  // A press on the vertical editor's Layout row belongs to it
  hasLayoutPress(event, canvas) {
    if (!this.hasLayoutRow()) return false;
    const rect = canvas.getBoundingClientRect();
    return Math.floor((event.clientY - rect.top) / this.rowHeight) === this.rows.length;
  }

  pointerMove(event) {
    const press = this.press;
    if (press?.kind === "keptEdge") {
      if (!press.moved && Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD_PX) return;
      press.moved = true;
      const raw = this.xToTime(event.clientX - press.rect.left, press.rect.width);
      const range = this.keptRange(this.edit?.partCuts);
      let t = round(this.snapTime(raw, press.rect.width, { keptEdge: press.edge }));
      t = press.edge === "start"
        ? Math.max(0, Math.min(t, range.end - MIN_KEPT_SECONDS))
        : Math.min(this.length(), Math.max(t, range.start + MIN_KEPT_SECONDS));
      this.previewCuts = this.trimmed(press.edge, t);
      this.edgeTip = { time: t, clientX: event.clientX, clientY: event.clientY };
      this.dirty = true;
      this.renderTip();
      return;
    }
    super.pointerMove(event);
    if (!this.press && this.tool === "pointer" && this.hover?.canvas === this.track && this.keptEdgeAt(this.hover.x, this.hover.y, this.hover.width)) {
      this.updateCursor(true);
    }
  }

  pointerUp(event) {
    const press = this.press;
    if (press?.kind !== "keptEdge") {
      super.pointerUp(event);
      return;
    }
    this.press = null;
    this.snapAt = null;
    this.edgeTip = null;
    this.renderTip();
    this.updateCursor();
    const preview = this.previewCuts;
    this.previewCuts = null;
    if (press.moved && preview) this.changeEdit(preview);
    this.dirty = true;
  }

  // Dragged ends stick to the source's ends, its parts, cut parts and the playhead
  snapPoints(skip) {
    const points = [0, this.length()];
    for (const part of this.source?.parts ?? []) points.push(part.offset, part.offset + part.vodEnd - part.vodStart);
    const range = this.keptRange(this.edit?.partCuts);
    const dragged = skip?.keptEdge ? range[skip.keptEdge] : null;
    for (const r of this.removedRanges(this.edit?.partCuts)) {
      for (const t of [r.start, r.end]) if (dragged == null || Math.abs(t - dragged) > 0.001) points.push(t);
    }
    if (this.hasLayoutRow()) {
      this.verticalParts.parts.forEach((part, k) => {
        if (k > 0 && !skip?.layout?.includes(k)) points.push(part.start);
      });
    }
    const now = this.getPosition();
    if (now != null) points.push(now);
    return points;
  }

  // ---------- right-click ----------

  contextMenu(event, canvas) {
    event.preventDefault();
    if (!this.vodId || !this.source) return;
    if (this.tool === "zoom" || canvas !== this.track) {
      super.contextMenu(event, canvas);
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const item = this.itemAt(x, event.clientY - rect.top, rect.width);
    if (!item) return;
    const t = this.xToTime(x, rect.width);
    const entries = [];
    if (this.canMakeEdits()) {
      const editable = this.editableRanges();
      const cut = editable.find((r) => t >= r.start && t < r.end);
      if (cut) entries.push({ icon: "arrow-counter-clockwise", label: "Put this part back", run: () => this.putBack(cut) });
      if (this.source.kind === "video" && !cut) {
        entries.push({ icon: "scissors", label: "Leave this part out of the edit", run: () => this.cutOut(item.start, item.end) });
      }
      if (editable.length) entries.push({ icon: "arrows-out-line-horizontal", label: "Put everything back (the whole item)", run: () => this.changeEdit({}) });
    }
    if (this.source.kind === "video") {
      // The video itself (changes it for everyone, like Player 1)
      entries.push(...this.videoPartMenu(item).filter((entry) => entry.label !== "View only"));
    } else if (this.canEditItem(item)) {
      entries.push({ icon: "pencil-simple", label: item.kind === "clip" ? "Rename the clip" : "Edit the segment's title and color", run: () => this.editTitle(item) });
    }
    entries.push({ icon: "eject", label: "Unload from Player 2", run: () => this.hooks.onUnload?.() });
    openMenu(event.clientX, event.clientY, entries);
  }

  // ---------- saving ----------

  scheduleSave() {
    clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => this.saveNow(), SAVE_DELAY_MS);
  }

  // Sends every edit with changes (one request at a time); resolves when they're saved
  saveNow() {
    clearTimeout(this.saveTimer);
    this.saveTimer = null;
    for (const edit of this.edits.filter((e) => e.dirty && !e.queued)) {
      edit.queued = true;
      this.saveChain = this.saveChain.then(() => this.saveEdit(edit));
    }
    return this.saveChain;
  }

  editBody(edit) {
    const body = edit.kind === "video"
      ? { parts: Object.entries(edit.partCuts ?? {}).map(([markerId, cuts]) => ({ markerId: Number(markerId), cuts })) }
      : { cuts: edit.partCuts?.[String(edit.itemId)] ?? [] };
    return { title: edit.title ?? null, ...body, ...(edit.layout != null ? { layout: edit.layout } : {}) };
  }

  async saveEdit(edit) {
    edit.queued = false;
    if (!edit.dirty || edit.deleted) return;
    edit.dirty = false;
    edit.saving = true;
    this.hooks.onEditsChanged?.();
    try {
      const saved = edit.id == null
        ? await api("POST", "/api/edits", { ...this.itemBody(edit.focus), ...this.editBody(edit) })
        : await api("PUT", `/api/edits/${edit.id}`, this.editBody(edit));
      edit.id = saved?.id ?? edit.id;
      if (saved) {
        edit.createdAtUtc = saved.createdAtUtc ?? edit.createdAtUtc;
        edit.updatedAtUtc = saved.updatedAtUtc ?? edit.updatedAtUtc;
        edit.canDelete = saved.canDelete ?? edit.canDelete;
      }
      edit.failed = false;
      if (this.edit === edit) this.rememberPick();
    } catch (error) {
      // Kept on screen; the next change tries again
      if (!edit.failed) toast(`Couldn't save "${this.editName(edit)}": ${error.message}`, "error");
      edit.failed = true;
      edit.dirty = true;
    }
    edit.saving = false;
    this.hooks.onEditsChanged?.();
  }

  // The page is closing: send what's waiting without waiting for the answer
  flushTrimOnExit() {
    clearTimeout(this.saveTimer);
    for (const edit of this.edits.filter((e) => e.dirty && !e.deleted)) {
      edit.dirty = false;
      const isNew = edit.id == null;
      fetch(isNew ? "/api/edits" : `/api/edits/${edit.id}`, {
        method: isNew ? "POST" : "PUT",
        keepalive: true,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(isNew ? { ...this.itemBody(edit.focus), ...this.editBody(edit) } : this.editBody(edit)),
      }).catch(() => {});
    }
  }

  rememberPick() {
    if (!this.focus) return;
    const all = loadLastEdits();
    all[this.itemKey()] = this.edit?.original ? "original" : this.edit?.id ?? "original";
    // keep the newest 200 items
    const entries = Object.entries(all).slice(-200);
    try {
      localStorage.setItem(LAST_EDIT_KEY, JSON.stringify(Object.fromEntries(entries)));
    } catch {
      // not remembered
    }
  }

  // ---------- the edit picker (ui/editor.js) ----------

  async pickEdit(edit) {
    if (edit === this.edit) return;
    await this.saveNow();
    this.edit = edit ?? this.originalEdit();
    this.editHistory = { undo: [], redo: [] };
    this.historyEdit = null;
    this.rememberPick();
    this.changed();
    this.hooks.onFocusChanged?.(this.focusInfo());
    this.hooks.onEditsChanged?.();
  }

  // New edit: the whole item, nothing left out
  async startNewEdit() {
    if (!this.canMakeEdits()) {
      toast(this.viewOnlyReason());
      return;
    }
    await this.saveNow();
    this.edit = this.newEdit({ partCuts: {} });
    this.editHistory = { undo: [], redo: [] };
    this.historyEdit = null;
    this.changed();
    this.hooks.onFocusChanged?.(this.focusInfo());
    toast(`Made "${this.editName(this.edit)}": the whole ${this.source.kind}. Trim it or cut parts out.`);
  }

  async renameEdit() {
    const edit = this.edit;
    if (!edit || edit.original || !edit.own) return;
    const title = await askText({ heading: "Rename edit", label: "Name", value: edit.title ?? "", confirm: "Rename", maxLength: 200 });
    if (title === null) return;
    edit.title = title.trim() || null;
    edit.dirty = true;
    await this.saveNow();
    this.hooks.onEditsChanged?.();
  }

  async deleteEdit() {
    const edit = this.edit;
    if (!edit || edit.original || !(edit.canDelete ?? edit.own) || me?.support) return;
    const ok = await confirmAction({
      heading: "Delete this edit?",
      text: `"${this.editName(edit)}" is deleted for the whole team. The ${this.source?.kind ?? "item"} itself stays as it is, and so does Player 1.`,
      confirm: "Delete",
    });
    if (!ok) return;
    clearTimeout(this.saveTimer);
    await this.saveChain;
    try {
      if (edit.id != null) await api("DELETE", `/api/edits/${edit.id}`);
      edit.deleted = true;
      edit.dirty = false;
      this.edits = this.edits.filter((e) => e !== edit);
      await this.pickEdit(null);
      toast("Edit deleted.");
    } catch (error) {
      toast(error.message, "error");
    }
    await this.loadEdits();
  }

  // Someone else's edit: your own copy of it
  async copyEdit() {
    const edit = this.edit;
    if (!edit || edit.original || edit.own || !this.canMakeEdits()) return;
    try {
      const copy = edit.id != null ? await api("POST", `/api/edits/${edit.id}/copy`, {}) : null;
      if (copy) {
        const made = this.fromServer(copy, edit.kind, this.focus);
        this.edits.push(made);
        await this.pickEdit(made);
      } else {
        this.ensureOwnEdit();
      }
      toast(`Made your own copy, "${this.editName(this.edit)}".`);
    } catch (error) {
      toast(error.message, "error");
    }
    this.hooks.onEditsChanged?.();
  }

  // Apply to Player 1: the item keeps its range there; what the edit leaves out becomes its cut-out parts (grayed out)
  async applyEdit() {
    const edit = this.edit;
    if (!edit || edit.original) return;
    if (!this.canApply()) {
      toast(this.canMakeEdits() ? "You can't apply edits to this item." : this.viewOnlyReason());
      return;
    }
    await this.saveNow();
    if (edit.id == null || edit.dirty) {
      toast("The edit isn't saved yet, so it can't be applied. Try again in a moment.", "error");
      return;
    }
    let result;
    try {
      result = await api("POST", `/api/edits/${edit.id}/apply`, {});
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    this.hooks.onApplied?.();
    if (result?.copied && result.item?.id != null) {
      toast(`Applied to a copy on your row: the ${this.source.kind} is someone else's. Player 2 now shows the copy.`);
      this.focusOn({ type: this.focus.type, id: result.item.id }, { editId: result.edit?.id });
      return;
    }
    toast(`Applied "${this.editName(edit)}" to Player 1. Undo last apply puts it back.`);
    await this.load();
    await this.loadEdits();
  }

  // to = "previous" (undo the last apply) or "original" (before the first one)
  async revert(to) {
    if (!this.canRevert()) return;
    await this.saveNow();
    const path = { clip: "entries", marker: "markers", video: "videos" }[this.focus.type];
    try {
      await api("POST", `/api/${path}/${this.focus.id}/revert`, { to });
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    this.hooks.onApplied?.();
    toast(to === "original" ? "Player 1 has the original again." : "Undid the last apply.");
    await this.load();
    await this.loadEdits();
  }

  // ---------- undo / redo (the edit on show, this visit only) ----------

  remember(step) {
    if (step.edit !== this.historyEdit) {
      this.editHistory = { undo: [], redo: [] };
      this.historyEdit = step.edit;
    }
    this.editHistory.undo.push(step);
    if (this.editHistory.undo.length > HISTORY_LIMIT) this.editHistory.undo.shift();
    this.editHistory.redo = [];
  }

  canUndo() {
    return this.historyEdit === this.edit && this.editHistory.undo.length > 0 && !me?.support;
  }

  canRedo() {
    return this.historyEdit === this.edit && this.editHistory.redo.length > 0 && !me?.support;
  }

  async undo() {
    this.replayEdit("undo", "redo", "before");
  }

  async redo() {
    this.replayEdit("redo", "undo", "after");
  }

  replayEdit(from, to, side) {
    if (!this.canUndo() && from === "undo") return;
    if (!this.canRedo() && from === "redo") return;
    const step = this.editHistory[from].pop();
    const edit = step.edit;
    edit.partCuts = clone(step[side]);
    edit.dirty = true;
    this.editHistory[to].push(step);
    this.scheduleSave();
    this.changed();
    this.hooks.onFocusChanged?.(this.focusInfo());
    this.hooks.onEditsChanged?.();
  }
}
