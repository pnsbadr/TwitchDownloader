// Projects (multi-track Player 2): the data, the rules the server checks, what a project plays, and saving.
//
// A project is one JSON document (the server's ProjectContent): tracks top to bottom ({ id, name, hidden, muted, locked }) and
// pieces ({ id, trackId, at, vodId, start, end, source, label, color, layout }). `at` is seconds on the project's clock from
// 0:00; start/end are VOD seconds. Pieces are copies: nothing follows the clip, segment or video they came from. Where tracks
// overlap the higher one plays (flatten); gaps are black and silent.
//
// ProjectStore keeps the open project, its undo history (snapshots of the content, 100 steps) and saves the whole content
// about 0.6 s after the last change (PUT /api/projects/{id} with the revision it last saw; one request at a time).

import { api, toast } from "./core.js";
import { keptParts } from "./cuts.js";

export const MAX_TRACKS = 20;
export const MAX_PIECES = 200;
export const MIN_PIECE = 0.5;
export const MAX_END = 3 * 3600;
export const MIN_STRETCH = 0.1;
const OVERLAP_TOLERANCE = 0.01;
const SAVE_DELAY_MS = 600;
const HISTORY_LIMIT = 100;

export const round = (seconds) => Math.round(seconds * 1000) / 1000;
export const pieceEnd = (piece) => piece.at + (piece.end - piece.start);
export const pieceLength = (piece) => piece.end - piece.start;

let idCounter = 0;
export function newId(prefix) {
  idCounter = (idCounter + 1) % 1296;
  return `${prefix}${Date.now().toString(36)}${idCounter.toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`;
}

export function emptyContent() {
  return { tracks: [{ id: "t1", name: null, hidden: false, muted: false, locked: false }], pieces: [] };
}

export function projectLength(content) {
  return Math.max(0, ...(content?.pieces ?? []).map(pieceEnd));
}

// A row's name; rows without one are "V1", "V2"… counted from the bottom like an editor's video tracks
export function trackName(content, track) {
  if (track.name) return track.name;
  const index = content.tracks.indexOf(track);
  return `V${content.tracks.length - index}`;
}

// The name a new row gets, so the names of the rows already there don't change: the next "V" number
export function newTrackName(content) {
  const used = content.tracks.map((t) => /^V(\d+)$/.exec(trackName(content, t))?.[1]).filter(Boolean).map(Number);
  return `V${Math.max(0, ...used) + 1}`;
}

// A new row (fills in the names of unnamed rows first, so adding one doesn't renumber them)
export function makeTrack(content, id) {
  for (const t of content.tracks) if (!t.name) t.name = trackName(content, t);
  return { id, name: newTrackName(content), hidden: false, muted: false, locked: false };
}

export function piecesOn(content, trackId) {
  return content.pieces.filter((p) => p.trackId === trackId).sort((a, b) => a.at - b.at);
}

// A place for a piece on a track that overlaps nothing there (ignoring the pieces in `except`)
export function fits(content, trackId, at, length, except = new Set()) {
  if (at < -0.0005 || at + length > MAX_END + 0.0005) return false;
  return !content.pieces.some((p) => p.trackId === trackId && !except.has(p.id)
    && at < pieceEnd(p) - OVERLAP_TOLERANCE && p.at < at + length - OVERLAP_TOLERANCE);
}

// The nearest start to `at` where `length` fits on the track: `at` itself, else right after or before a piece in the way
export function nearestFit(content, trackId, at, length, except = new Set()) {
  if (fits(content, trackId, at, length, except)) return at;
  const others = content.pieces.filter((p) => p.trackId === trackId && !except.has(p.id));
  const candidates = [0, ...others.map(pieceEnd), ...others.map((p) => p.at - length)]
    .filter((x) => x >= 0 && fits(content, trackId, x, length, except))
    .sort((a, b) => Math.abs(a - at) - Math.abs(b - at));
  return candidates.length ? candidates[0] : null;
}

// What the project plays from 0:00 to its end, in order (the server's ProjectTimeline.Flatten): the piece on the highest visible
// track at every moment, else a gap. [{ at, duration, piece, vodStart, vodEnd, muted }]
export function flatten(content) {
  const tracks = content?.tracks ?? [];
  const order = new Map(tracks.map((t, i) => [t.id, i]));
  const hidden = new Set(tracks.filter((t) => t.hidden).map((t) => t.id));
  const muted = new Set(tracks.filter((t) => t.muted).map((t) => t.id));
  const pieces = (content?.pieces ?? []).filter((p) => !hidden.has(p.trackId) && order.has(p.trackId));
  if (!pieces.length) return [];
  const cuts = [...new Set([0, ...pieces.flatMap((p) => [p.at, pieceEnd(p)])])].sort((a, b) => a - b);
  const raw = [];
  for (let i = 0; i + 1 < cuts.length; i++) {
    const from = cuts[i];
    const to = cuts[i + 1];
    if (to - from < 1e-6) continue;
    const middle = (from + to) / 2;
    let top = null;
    for (const p of pieces) {
      if (p.at <= middle && pieceEnd(p) > middle && (!top || order.get(p.trackId) < order.get(top.trackId))) top = p;
    }
    raw.push(top
      ? { at: from, duration: to - from, piece: top, vodStart: top.start + (from - top.at), vodEnd: top.start + (to - top.at), muted: muted.has(top.trackId) }
      : { at: from, duration: to - from, piece: null, vodStart: 0, vodEnd: 0, muted: false });
  }
  const joined = [];
  for (const s of raw) {
    const last = joined[joined.length - 1];
    if (last && last.piece === s.piece && (!last.piece || Math.abs(last.vodEnd - s.vodStart) < 1e-6)) {
      last.duration += s.duration;
      last.vodEnd = s.vodEnd;
    } else {
      joined.push({ ...s });
    }
  }
  const extend = (s, seconds) => {
    s.duration += seconds;
    if (s.piece) s.vodEnd += seconds;
  };
  const result = [];
  for (const s of joined) {
    if (s.duration >= MIN_STRETCH || (!result.length && joined.length === 1)) result.push(s);
    else if (result.length) extend(result[result.length - 1], s.duration);
    else result.push(s);
  }
  if (result.length > 1 && result[0].duration < MIN_STRETCH) {
    const first = result.shift();
    result[0].at = first.at;
    result[0].duration += first.duration;
    if (result[0].piece) result[0].vodStart -= first.duration;
  }
  return result;
}

// The stretch playing at project time t (null past the end)
export function stretchAt(stretches, t) {
  for (const s of stretches) if (t >= s.at - 1e-6 && t < s.at + s.duration - 1e-6) return s;
  return null;
}

// ---------- items from the library and Player 1 → pieces ----------

const clipColor = "violet";

// A dragged card or timeline item ({ kind, data }) as pieces back to back from 0 (one per kept part; a video: its segments in
// play order). null = it can't go in a project (moments, Twitch clips without a VOD, VODs).
export function piecesFromItem({ kind, data }) {
  const vodId = Number(data?.vodId ?? 0);
  if (!vodId) return null;
  const parts = [];
  if (kind === "clip") {
    const start = data.startSeconds ?? data.start ?? 0;
    const end = data.endSeconds ?? data.end ?? (data.vodLengthSeconds || start + (data.clipLengthSeconds || 0));
    if (!(end > start)) return null;
    const title = data.title || "Clip";
    for (const part of keptParts(start, end, data.cuts ?? [])) {
      parts.push({ vodId, start: part.start, end: part.end, source: { kind: "clip", id: data.id }, label: title, color: clipColor });
    }
  } else if (kind === "segment") {
    const start = data.seconds ?? data.startSeconds ?? data.start;
    const end = data.endSeconds ?? data.end;
    if (start == null || end == null || !(end > start)) return null;
    for (const part of keptParts(start, end, data.cuts ?? [])) {
      parts.push({ vodId, start: part.start, end: part.end, source: { kind: "segment", id: data.id }, label: data.label || "Segment", color: data.color || "yellow" });
    }
  } else if (kind === "video") {
    const segments = [...(data.segments ?? [])].sort((a, b) => a.position - b.position);
    for (const segment of segments) {
      const cuts = [...(segment.cuts ?? []), ...(segment.videoCuts ?? [])];
      for (const part of keptParts(segment.startSeconds, segment.endSeconds, cuts)) {
        parts.push({
          vodId, start: part.start, end: part.end, source: { kind: "video", id: data.id },
          label: segment.label ? `${data.title || "Video"} · ${segment.label}` : (data.title || "Video"), color: segment.color || "yellow",
        });
      }
    }
  } else {
    return null;
  }
  let at = 0;
  return parts.filter((p) => p.end - p.start >= MIN_PIECE).map((p) => {
    const piece = { id: newId("p"), trackId: null, at, ...p, start: round(p.start), end: round(p.end), layout: null };
    at = round(at + piece.end - piece.start);
    return piece;
  });
}

export const PROJECT_KINDS = ["clip", "segment", "video"];

// ---------- layouts on pieces (vertical) ----------

// Splitting a piece at VOD second t: each side keeps its own keyframes plus the nearest one past the split, so motion doesn't change
export function splitLayout(layout, t) {
  if (!layout) return [null, null];
  const keys = layout.keys ?? [];
  if (!keys.length) return [structuredClone(layout), structuredClone(layout)];
  const before = keys.filter((k) => k.time < t);
  const after = keys.filter((k) => k.time >= t);
  const left = [...before, ...(after.length ? [after[0]] : [])];
  const right = [...(before.length ? [before[before.length - 1]] : []), ...after];
  return [{ ...structuredClone(layout), keys: structuredClone(left) }, { ...structuredClone(layout), keys: structuredClone(right) }];
}

// ---------- the open project ----------

export class ProjectStore {
  // hooks: onChange() (content, title, save state or history changed), onReplaced() (the server's newer version came in)
  constructor(hooks = {}) {
    this.hooks = hooks;
    this.project = null;   // the DTO without content
    this.content = null;
    this.history = [];
    this.future = [];
    this.saving = false;
    this.dirty = false;
    this.failed = false;
    this.timer = null;
  }

  get open() {
    return Boolean(this.project);
  }

  get canEdit() {
    return Boolean(this.project?.canEdit);
  }

  set(project) {
    const { content, ...rest } = project;
    this.project = rest;
    this.content = content && content.tracks?.length ? content : emptyContent();
    this.content.pieces ??= [];
  }

  async openProject(id) {
    await this.saveNow();
    const project = await api("GET", `/api/projects/${id}`);
    this.set(project);
    this.history = [];
    this.future = [];
    this.failed = false;
    this.hooks.onChange?.();
    return project;
  }

  async create({ title = null, content = null } = {}) {
    await this.saveNow();
    const project = await api("POST", "/api/projects", { title, content: content ?? emptyContent() });
    this.set(project);
    this.history = [];
    this.future = [];
    this.failed = false;
    this.hooks.onChange?.();
    return project;
  }

  async close() {
    await this.saveNow();
    this.project = null;
    this.content = null;
    this.history = [];
    this.future = [];
    this.hooks.onChange?.();
  }

  // One undo step: fn(content) changes a copy of the content; returning false drops it (nothing changes)
  edit(fn) {
    if (!this.content || !this.canEdit) return false;
    const before = JSON.stringify(this.content);
    const draft = JSON.parse(before);
    if (fn(draft) === false) return false;
    const after = JSON.stringify(draft);
    if (after === before) return false;
    this.history.push(before);
    if (this.history.length > HISTORY_LIMIT) this.history.shift();
    this.future = [];
    this.content = draft;
    this.changed();
    return true;
  }

  canUndo() {
    return this.canEdit && this.history.length > 0;
  }

  canRedo() {
    return this.canEdit && this.future.length > 0;
  }

  undo() {
    if (!this.canUndo()) return;
    this.future.push(JSON.stringify(this.content));
    this.content = JSON.parse(this.history.pop());
    this.changed();
  }

  redo() {
    if (!this.canRedo()) return;
    this.history.push(JSON.stringify(this.content));
    this.content = JSON.parse(this.future.pop());
    this.changed();
  }

  changed() {
    this.dirty = true;
    this.scheduleSave();
    this.hooks.onChange?.();
  }

  scheduleSave() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.save(), SAVE_DELAY_MS);
  }

  async saveNow() {
    clearTimeout(this.timer);
    while (this.saving) await new Promise((resolve) => setTimeout(resolve, 50));
    if (this.dirty) await this.save();
  }

  async save() {
    if (!this.project || !this.dirty) return;
    if (this.saving) {
      this.scheduleSave();
      return;
    }
    this.saving = true;
    this.dirty = false;
    const id = this.project.id;
    const sent = JSON.stringify(this.content);
    this.hooks.onChange?.();
    try {
      const saved = await api("PUT", `/api/projects/${id}`, { content: JSON.parse(sent), revision: this.project.revision });
      if (this.project?.id === id) {
        const { content, ...rest } = saved;
        this.project = rest;
      }
      this.failed = false;
    } catch (error) {
      if (this.project?.id !== id) return;
      if (error.code === "project-changed" && error.problem?.project) {
        // Someone (or another tab) saved a newer version: take it, so nothing is saved over it
        this.set(error.problem.project);
        this.history = [];
        this.future = [];
        this.failed = false;
        toast("This project was changed somewhere else, so the newer version is shown. Your last change wasn't saved.", "error");
        this.hooks.onReplaced?.();
      } else {
        this.failed = true;
        this.dirty = true;
        toast(`Couldn't save the project: ${error.message}`, "error");
      }
    } finally {
      this.saving = false;
      this.hooks.onChange?.();
    }
  }

  // The page is closing: send what isn't saved yet without waiting
  flushOnExit() {
    if (!this.project || !this.dirty) return;
    clearTimeout(this.timer);
    fetch(`/api/projects/${this.project.id}`, {
      method: "PUT",
      keepalive: true,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ content: this.content, revision: this.project.revision }),
    }).catch(() => {});
  }

  async rename(title) {
    if (!this.project) return;
    await this.saveNow();
    const saved = await api("PUT", `/api/projects/${this.project.id}`, { title, revision: this.project.revision });
    const { content, ...rest } = saved;
    this.project = rest;
    this.hooks.onChange?.();
  }

  stateText() {
    if (this.saving || (this.dirty && !this.failed)) return "saving…";
    if (this.failed) return "not saved";
    return this.canEdit ? "" : "view only";
  }
}
