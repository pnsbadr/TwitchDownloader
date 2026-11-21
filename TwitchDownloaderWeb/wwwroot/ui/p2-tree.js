// Player 2's picker (badrfly 2026-11-17, "the VOD is the project"): a tree of what the team made on each VOD.
// Since badrfly 2026-11-21 the tree isn't drawn any more: the library's rows open to the same lines (ui/library.js "edits and segments
// under clips and videos"), and Player 2's header menu only has what can be done with what's loaded. The library uses the menus and
// renames here (editMenu, segmentMenu, segmentData); the drawing code stays for now.
//
//   VOD
//     Videos › video › its segments (and the video's edits)
//     Clips  › clip  › its edits
//     Other edits (edits of a lone segment or a VOD range, and edits whose clip/video was deleted; only when there are any)
//
// Only VODs with something on their timeline are listed (GET /api/vods/work), Player 1's VOD first. Opening a line closes its
// siblings. When the menu opens with something loaded in Player 2, only the path to it is open. Click a clip or video line to open
// it and double-click (or its load icon) to load it; segments and edits load with one click. Names are the team's own: whoever made
// something (or a role with EditAnyEntries) renames it with the pencil or the right-click menu. The word "project" is never shown.
//
// Large libraries (badrfly 2026-11-20, step 5): a search box over the tree (titles of clips and videos, segment labels; the VODs whose work
// matches, from GET /api/vods/matches, opened to what matched) and a ★ button that keeps only what the member starred. Their starred VODs
// come right after Player 1's, then the ones they opened lately; starred lines carry a star, which the star icon on each line toggles.

import { el, api, me, can, formatDate, formatDuration, toast, iconButton } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";
import { openMenu } from "./menu.js";

const MAX_TITLE_LOOKUPS = 30;

const state = {
  work: [],
  workError: "",
  loadingWork: false,
  vods: new Map(),     // vodId → { clips, videos, edits, loading, error }
  titles: new Map(),   // vodId → title (from the library's VOD list or /api/vods/info)
  expanded: new Set(),
  query: "",           // the search box (lower case)
  only: "",            // "starred": only what the member starred
  matches: null,       // VOD IDs the search / ★ keeps (GET /api/vods/matches), or null
};
let matchSeq = 0;
let matchTimer = 0;

let hooks = {
  root: null,              // the element the tree is drawn in
  loaded: () => null,      // { vodId, editId, entryId, videoId, segmentId } of what Player 2 has
  player1VodId: () => 0,
  knownVodTitle: () => null,
  onLoadItem: () => {},    // ({ kind: clip | video | segment, data })
  onOpenEdit: () => {},    // (editId)
  onLoadVod: () => {},     // (vodId, title): Player 1
  onRenameEdit: null,      // (edit, title) → Promise; the open edit renames through its store
  onDeleteEdit: () => {},  // (edit)
  onCopyEdit: () => {},    // (edit)
  onExportEdit: () => {},  // (edit)
  onChanged: () => {},     // (kind) a clip / video / segment / edit was renamed
  onAddSegments: null,     // (video): the link tool with the video's segments (videos across streams)
  vodDay: () => null,      // (vodId) "19 Nov" once known
  marks: () => null,       // the member's { starred, recent } IDs (library.js myMarks)
  toggleStar: null,        // (type, item) → Promise: star / unstar (library.js)
  close: () => {},
};

export function setUpTree(options) {
  hooks = { ...hooks, ...options };
  if (!hooks.root || hooks.root.previousElementSibling?.classList.contains("tree-search")) return;
  const input = el("input", { type: "search", class: "tree-search-input", placeholder: "Search clips, videos, segments", "aria-label": "Search the VODs' work", spellcheck: "false" });
  const starOnly = iconButton({ icon: "star", label: "Only what you starred", className: "tree-star-only", onClick: (event) => {
    state.only = state.only ? "" : "starred";
    event.currentTarget.name = state.only ? "star-fill" : "star";
    event.currentTarget.classList.toggle("on", Boolean(state.only));
    findMatches();
  } });
  input.addEventListener("input", () => {
    clearTimeout(matchTimer);
    matchTimer = setTimeout(() => {
      state.query = input.value.trim().toLowerCase();
      findMatches();
    }, 250);
  });
  input.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      hooks.root.querySelector(".tree-row")?.focus();
    } else if (event.key === "Escape" && input.value) {
      // Clears the box first; the next Esc closes the menu
      event.stopPropagation();
      input.value = "";
      state.query = "";
      findMatches();
    }
  });
  // Typing in the box mustn't reach the editor's keys (or Shoelace's menu type-to-select)
  input.addEventListener("keydown", (event) => event.stopPropagation());
  hooks.root.before(el("div", { class: "tree-search" }, el("sl-icon", { library: "ph", name: "magnifying-glass" }), input, starOnly));
}

// The search box or ★ changed: which VODs to keep, then open the first few to what matched
async function findMatches() {
  const seq = ++matchSeq;
  if (!state.query && !state.only) {
    state.matches = null;
    render();
    return;
  }
  const query = new URLSearchParams();
  if (state.query) query.set("q", state.query);
  if (state.only) query.set("mark", state.only);
  try {
    const found = await api("GET", `/api/vods/matches?${query}`);
    if (seq !== matchSeq) return;
    state.matches = new Set(found.vodIds.map(Number));
  } catch {
    if (seq !== matchSeq) return;
    state.matches = new Set();
  }
  for (const vodId of vodIds().slice(0, 3)) {
    state.expanded.add(`v:${vodId}`);
    state.expanded.add(`v:${vodId}:videos`);
    state.expanded.add(`v:${vodId}:clips`);
    if (!state.vods.get(vodId)) loadVod(vodId);
  }
  render();
  lookUpTitles();
}

const marks = () => hooks.marks?.() ?? null;
function starred(type, id) {
  const list = { vod: "vods", clip: "clips", video: "videos" }[type];
  return Boolean(marks()?.starred?.[list]?.includes(String(id)));
}

function searching() {
  return Boolean(state.query || state.only);
}

// Under a search: does a clip / video match (a video by its title or a segment's label); with ★ only, is it starred
function keepClip(clip, vodStarred) {
  if (state.only && !vodStarred && !starred("clip", clip.id)) return false;
  return !state.query || clip.title.toLowerCase().includes(state.query);
}

function keepVideo(video, vodStarred) {
  if (state.only && !vodStarred && !starred("video", video.id)) return false;
  return !state.query || video.title.toLowerCase().includes(state.query)
    || video.segments.some((s) => (s.label ?? "").toLowerCase().includes(state.query));
}

function starAction(type, item, id) {
  if (!hooks.toggleStar || me?.support) return null;
  const on = starred(type, id);
  return iconButton({ icon: on ? "star-fill" : "star", label: on ? "Unstar" : "Star", className: `tree-star${on ? " on" : ""}`, onClick: async () => {
    await hooks.toggleStar(type, item);
    render();
  } });
}

// The menu opened: read the VODs again and open only the path to what's loaded
export async function openTree() {
  state.expanded = pathOf(hooks.loaded());
  render();
  await loadWork();
  const loaded = hooks.loaded();
  if (loaded?.vodId) await loadVod(loaded.vodId);
}

// What's loaded changed while the menu is closed: forget cached lists so they're read again next time
export function forgetTree(vodId = null) {
  if (vodId) state.vods.delete(Number(vodId));
  else state.vods.clear();
}

function pathOf(loaded) {
  const open = new Set();
  if (!loaded?.vodId) return open;
  const v = `v:${loaded.vodId}`;
  open.add(v);
  if (loaded.entryId) {
    open.add(`${v}:clips`);
    open.add(`clip:${loaded.entryId}`);
  } else if (loaded.videoId) {
    open.add(`${v}:videos`);
    open.add(`vid:${loaded.videoId}`);
  } else if (loaded.segmentId) {
    open.add(`${v}:videos`);
    open.add(`seg-of:${loaded.segmentId}`); // resolved to its video once the VOD's lists are in (see render)
  } else if (loaded.editId) {
    open.add(`${v}:other`);
  }
  return open;
}

async function loadWork() {
  state.loadingWork = true;
  try {
    state.work = await api("GET", "/api/vods/work?limit=100");
    state.workError = "";
  } catch (error) {
    state.workError = error.message;
  }
  state.loadingWork = false;
  render();
  lookUpTitles();
}

async function loadVod(vodId) {
  vodId = Number(vodId);
  const entry = state.vods.get(vodId) ?? { clips: [], videos: [], edits: [], loading: true, error: "" };
  entry.loading = true;
  state.vods.set(vodId, entry);
  render();
  try {
    const [clips, videos, edits] = await Promise.all([
      api("GET", `/api/entries?vodId=${vodId}&sort=dateAdded&order=asc&limit=1000`),
      api("GET", `/api/videos?vodIds=${vodId}&limit=1000`),
      api("GET", `/api/projects?vodId=${vodId}&limit=1000`),
    ]);
    Object.assign(entry, { clips, videos: [...videos].reverse(), edits: [...edits].reverse(), error: "" });
  } catch (error) {
    entry.error = error.message;
  }
  entry.loading = false;
  render();
}

// VOD titles: the library's list has the ones loaded there; others are asked for (a few at a time is fine: 30 at most)
async function lookUpTitles() {
  const missing = vodIds().filter((id) => !state.titles.has(id)).slice(0, MAX_TITLE_LOOKUPS);
  for (const id of missing) {
    const known = hooks.knownVodTitle(id);
    if (known) state.titles.set(id, known);
  }
  await Promise.all(missing.filter((id) => !state.titles.has(id)).map(async (id) => {
    state.titles.set(id, null);
    try {
      const info = await api("GET", `/api/vods/info?vod=${id}`);
      state.titles.set(id, info.title);
    } catch {
      // Deleted or not open to the team any more: the date and channel name stand in
    }
  }));
  render();
}

function vodIds() {
  const ids = state.work.map((w) => Number(w.vodId));
  const loaded = Number(hooks.loaded()?.vodId || 0);
  const p1 = Number(hooks.player1VodId() || 0);
  // Player 1's and what's loaded, then the member's starred VODs, then the ones they opened lately, then the rest by newest work
  const mine = marks();
  const order = [p1, loaded, ...(mine?.starred?.vods ?? []).map(Number), ...(mine?.recent?.vods ?? []).map(Number), ...ids];
  const all = order.filter((id, i) => id && order.indexOf(id) === i);
  if (!state.matches) return all.filter((id) => ids.includes(id) || id === p1 || id === loaded || starred("vod", id));
  // Searching: the VODs whose work matches (the server's order) and loaded titles that match
  const titled = all.filter((id) => state.query && (state.titles.get(id) ?? "").toLowerCase().includes(state.query) && (!state.only || starred("vod", id)));
  const found = [...state.matches];
  return [...all.filter((id) => found.includes(id) || titled.includes(id)), ...found.filter((id) => !all.includes(id))];
}

// ---------- drawing ----------

function toggle(key, parent) {
  if (state.expanded.has(key)) {
    state.expanded.delete(key);
  } else {
    // Opening one closes the others under the same parent
    for (const other of [...state.expanded]) if (siblingOf(other, key, parent)) state.expanded.delete(other);
    state.expanded.add(key);
    const vod = /^v:(\d+)$/.exec(key);
    if (vod && !state.vods.get(Number(vod[1]))) loadVod(vod[1]);
  }
  render();
}

// Keys under one parent: VODs (v:1), a VOD's groups (v:1:clips), a group's items (clip:5 / vid:3, parent given)
function siblingOf(other, key, parent) {
  if (other === key) return false;
  if (/^v:\d+$/.test(key)) return /^v:\d+$/.test(other);
  if (/^v:\d+:/.test(key)) return other.startsWith(`${parent}:`);
  if (/^(clip|vid):/.test(key)) return parent.endsWith(":clips") ? other.startsWith("clip:") : other.startsWith("vid:");
  return false;
}

export function render() {
  const root = hooks.root;
  if (!root) return;
  const loaded = hooks.loaded();
  const rows = [];
  const ids = vodIds();
  if (!ids.length) {
    rows.push(el("p", { class: "tree-empty faint" }, state.workError
      || (searching() ? (state.only && !state.query ? "Nothing starred yet. Star clips, videos or VODs with the ☆ on their line." : "Nothing matches.")
        : state.loadingWork ? "Loading…" : "Nothing yet. Mark segments or clips on a VOD in Player 1, then load them here.")));
  }
  for (const vodId of ids) rows.push(...vodRows(vodId, loaded));
  root.replaceChildren(...rows);
}

function row({ key = null, parent = null, depth, icon, label, detail = null, current = false, expandable = false, onClick, onDouble, actions = [], menu = null, title = null, star = false }) {
  const open = key && state.expanded.has(key);
  const node = el("div", {
    class: `tree-row${current ? " current" : ""}`,
    role: "treeitem",
    tabindex: "0",
    style: `--depth:${depth}`,
    title: title ?? label,
    ...(expandable ? { "aria-expanded": String(Boolean(open)) } : {}),
    ...(current ? { "aria-current": "true" } : {}),
  },
  el("sl-icon", { library: "ph", name: expandable ? (open ? "caret-down" : "caret-right") : "dot", class: "tree-caret" }),
  el("sl-icon", { library: "ph", name: icon, class: "tree-icon" }),
  el("span", { class: "tree-label" }, label),
  star ? el("sl-icon", { library: "ph", name: "star-fill", class: "tree-starred", "aria-label": "Starred" }) : null,
  detail ? el("span", { class: "tree-detail num" }, detail) : null,
  el("span", { class: "tree-actions" }, ...actions));
  const activate = () => (expandable ? toggle(key, parent) : onClick?.());
  node.addEventListener("click", (event) => {
    if (event.target.closest(".tree-actions")) return;
    activate();
    if (expandable && onClick) onClick();
  });
  if (onDouble) node.addEventListener("dblclick", (event) => {
    if (event.target.closest(".tree-actions")) return;
    onDouble();
  });
  node.addEventListener("keydown", (event) => {
    if (event.target !== node) return;
    if (event.key === "Enter") {
      event.preventDefault();
      if (onDouble && !event.shiftKey) onDouble();
      else activate();
    } else if (event.key === " ") {
      event.preventDefault();
      activate();
    } else if (event.key === "ArrowRight" && expandable && !open) {
      toggle(key, parent);
    } else if (event.key === "ArrowLeft" && expandable && open) {
      toggle(key, parent);
    } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const all = [...hooks.root.querySelectorAll(".tree-row")];
      all[all.indexOf(node) + (event.key === "ArrowDown" ? 1 : -1)]?.focus();
    }
  });
  if (menu) node.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    event.stopPropagation();
    openMenu(event.clientX, event.clientY, menu());
  });
  return node;
}

function vodLabel(vodId) {
  const title = state.titles.get(vodId);
  if (title) return title;
  const work = state.work.find((w) => Number(w.vodId) === vodId);
  const parts = [work?.streamDateUtc ? formatDate(work.streamDateUtc, false) : null, work?.channelName].filter(Boolean);
  return parts.length ? `VOD ${parts.join(" · ")}` : `VOD ${vodId}`;
}

function vodRows(vodId, loaded) {
  const key = `v:${vodId}`;
  const work = state.work.find((w) => Number(w.vodId) === vodId);
  const label = vodLabel(vodId);
  const counts = work ? [work.videos && `${work.videos} video${work.videos === 1 ? "" : "s"}`, work.clips && `${work.clips} clip${work.clips === 1 ? "" : "s"}`].filter(Boolean).join(" · ") : "";
  const rows = [row({
    key, depth: 0, icon: "broadcast", label, detail: counts || null, expandable: true,
    current: Number(loaded?.vodId) === vodId, star: starred("vod", vodId),
    title: `${label}${work?.streamDateUtc ? ` · ${formatDate(work.streamDateUtc, false)}` : ""}`,
    actions: [
      starAction("vod", { vodId }, vodId),
      iconButton({ icon: "monitor-play", label: "Load in Player 1", onClick: () => { hooks.onLoadVod(vodId, state.titles.get(vodId) ?? label); hooks.close(); } }),
    ].filter(Boolean),
    menu: () => [{ label: "Load in Player 1", icon: "broadcast", run: () => hooks.onLoadVod(vodId, state.titles.get(vodId) ?? label) }],
  })];
  if (!state.expanded.has(key)) return rows;

  const data = state.vods.get(vodId);
  if (!data || (data.loading && !data.clips.length && !data.videos.length && !data.edits.length)) {
    rows.push(el("p", { class: "tree-empty faint", style: "--depth:1" }, "Loading…"));
    return rows;
  }
  if (data.error) {
    rows.push(el("p", { class: "tree-empty faint", style: "--depth:1" }, data.error));
    return rows;
  }

  // A loaded segment opens the video it's in (the first one)
  const segOf = [...state.expanded].find((k) => k.startsWith("seg-of:"));
  if (segOf) {
    const segmentId = Number(segOf.slice(7));
    const video = data.videos.find((v) => v.segments.some((s) => s.markerId === segmentId));
    state.expanded.delete(segOf);
    if (video) state.expanded.add(`vid:${video.id}`);
  }

  const editsOf = (field, id) => data.edits.filter((e) => e[field] === id);
  // Searching: only what matches (a starred VOD keeps all its work under ★); groups with nothing left are left out
  const vodStarred = starred("vod", vodId);
  const videos = searching() ? data.videos.filter((v) => keepVideo(v, vodStarred)) : data.videos;
  const clips = searching() ? data.clips.filter((c) => keepClip(c, vodStarred)) : data.clips;
  const videosKey = `${key}:videos`;
  if (!searching() || videos.length) {
    rows.push(row({ key: videosKey, parent: key, depth: 1, icon: "film-strip", label: "Videos", detail: String(videos.length), expandable: true }));
    if (state.expanded.has(videosKey)) {
      if (!videos.length) rows.push(el("p", { class: "tree-empty faint", style: "--depth:2" }, "No videos yet. Link segments on Player 1 to make one."));
      for (const video of videos) rows.push(...videoRows(video, editsOf("videoId", video.id), videosKey, loaded));
    }
  }

  const clipsKey = `${key}:clips`;
  if (!searching() || clips.length) {
    rows.push(row({ key: clipsKey, parent: key, depth: 1, icon: "scissors", label: "Clips", detail: String(clips.length), expandable: true }));
    if (state.expanded.has(clipsKey)) {
      if (!clips.length) rows.push(el("p", { class: "tree-empty faint", style: "--depth:2" }, "No clips yet. Mark one on Player 1 (X)."));
      for (const clip of clips) rows.push(...clipRows(clip, editsOf("entryId", clip.id), clipsKey, loaded));
    }
  }
  if (searching() && !videos.length && !clips.length) {
    rows.push(el("p", { class: "tree-empty faint", style: "--depth:1" }, state.query ? "Matches a segment or moment on its timeline." : "Starred."));
  }

  const other = searching() ? [] : data.edits.filter((e) => !e.entryId && !e.videoId);
  if (other.length) {
    const otherKey = `${key}:other`;
    rows.push(row({ key: otherKey, parent: key, depth: 1, icon: "stack", label: "Other edits", detail: String(other.length), expandable: true,
      title: "Edits of a single segment, of a VOD range, or of a clip or video that was deleted" }));
    if (state.expanded.has(otherKey)) for (const edit of other) rows.push(editRow(edit, 2, loaded));
  }
  return rows;
}

function videoRows(video, edits, parent, loaded) {
  const key = `vid:${video.id}`;
  const isLoaded = Number(loaded?.videoId) === video.id && !loaded?.editId;
  const load = () => {
    hooks.onLoadItem({ kind: "video", data: video });
    hooks.close();
  };
  // A video may span several streams: it's listed under each VOD it plays from
  const streams = new Set(video.segments.map((s) => String(s.vodId ?? video.vodId))).size;
  const rows = [row({
    key, parent, depth: 2, icon: "film-strip", label: video.title, expandable: true, current: isLoaded, star: starred("video", video.id),
    detail: `${streams > 1 ? `${streams} streams · ` : ""}${formatDuration(Math.round(video.lengthSeconds ?? 0))}`,
    title: `${video.title}${video.createdBy ? ` · by ${video.createdBy}` : ""} (double-click to load)`,
    onDouble: load,
    actions: [
      starAction("video", video, video.id),
      iconButton({ icon: "play", label: "Load in Player 2", onClick: load }),
      video.canEdit ? iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameVideo(video) }) : null,
    ].filter(Boolean),
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: load },
      video.canEdit && hooks.onAddSegments && { label: "Add segments…", icon: "link", run: () => {
        hooks.close();
        hooks.onAddSegments(video);
      } },
      video.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameVideo(video) },
    ].filter(Boolean),
  })];
  if (!state.expanded.has(key)) return rows;
  const segments = [...video.segments].sort((a, b) => a.position - b.position);
  const home = String(parentVod(parent) ?? video.vodId);
  for (const segment of segments) rows.push(segmentRow(segment, video, loaded, home));
  for (const edit of edits) rows.push(editRow(edit, 3, loaded));
  return rows;
}

// "v:123:videos" → "123"
function parentVod(parent) {
  return parent?.startsWith("v:") ? parent.split(":")[1] : null;
}

// home = the VOD this line is under: a segment of another stream says which
// What Player 2 loads for a video's segment
export function segmentData(segment, video) {
  return {
    id: segment.markerId, vodId: String(segment.vodId ?? video.vodId), seconds: segment.startSeconds, endSeconds: segment.endSeconds,
    label: segment.label, color: segment.color, cuts: [...(segment.cuts ?? [])],
  };
}

function canRenameSegment(segment, video) {
  return !me?.support && !segment.removed && (can("EditAnyEntries") || (can("AddEntries") && video.userId === me?.id));
}

// The right-click menu of a video's segment (the library's line under the video)
export function segmentMenu(segment, video) {
  const load = () => hooks.onLoadItem({ kind: "segment", data: segmentData(segment, video) });
  return [
    { label: "Load in Player 2", icon: "film-strip", run: load },
    canRenameSegment(segment, video) && { label: "Rename", icon: "pencil-simple", run: () => renameSegment(segment, video) },
  ].filter(Boolean);
}

function segmentRow(segment, video, loaded, home) {
  const vodId = String(segment.vodId ?? video.vodId);
  const canRename = canRenameSegment(segment, video);
  const data = segmentData(segment, video);
  const other = vodId !== home ? (hooks.vodDay(vodId) ?? "other stream") : null;
  const load = () => {
    hooks.onLoadItem({ kind: "segment", data });
    hooks.close();
  };
  const label = `${segment.label || `Segment ${segment.position + 1}`}${other ? ` · ${other}` : ""}`;
  return row({
    depth: 3, icon: "brackets-square", label, current: Number(loaded?.segmentId) === segment.markerId && !loaded?.editId,
    title: segment.removed ? "Deleted from its timeline, still in this video" : undefined,
    detail: formatDuration(Math.round(segment.keptSeconds ?? segment.endSeconds - segment.startSeconds)),
    onClick: load,
    actions: canRename ? [iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameSegment(segment, video) })] : [],
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: load },
      canRename && { label: "Rename", icon: "pencil-simple", run: () => renameSegment(segment, video) },
    ].filter(Boolean),
  });
}

function canOnEntry(entry, own, any) {
  return !me?.support && (can(any) || (can(own) && entry.addedByUserId === String(me?.id)));
}

function clipRows(clip, edits, parent, loaded) {
  const key = `clip:${clip.id}`;
  const canRename = canOnEntry(clip, "EditOwnEntries", "EditAnyEntries");
  const load = () => {
    hooks.onLoadItem({ kind: "clip", data: clip });
    hooks.close();
  };
  const rows = [row({
    key, parent, depth: 2, icon: "scissors", label: clip.title, expandable: true, star: starred("clip", clip.id),
    current: Number(loaded?.entryId) === clip.id && !loaded?.editId,
    detail: formatDuration(Math.round(clip.clipLengthSeconds ?? 0)),
    title: `${clip.title}${clip.addedBy ? ` · by ${clip.addedBy}` : ""} (double-click to load)`,
    onDouble: load,
    actions: [
      starAction("clip", clip, clip.id),
      iconButton({ icon: "play", label: "Load in Player 2", onClick: load }),
      canRename ? iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameClip(clip) }) : null,
    ].filter(Boolean),
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: load },
      canRename && { label: "Rename", icon: "pencil-simple", run: () => renameClip(clip) },
    ].filter(Boolean),
  })];
  if (!state.expanded.has(key)) return rows;
  if (!edits.length) rows.push(el("p", { class: "tree-empty faint", style: "--depth:3" }, "No edits yet. Load the clip and change it to start one."));
  for (const edit of edits) rows.push(editRow(edit, 3, loaded));
  return rows;
}

function editRow(edit, depth, loaded) {
  const open = () => {
    hooks.onOpenEdit(edit.id);
    hooks.close();
  };
  const others = (edit.vodIds ?? []).length > 1 ? ` · uses ${edit.vodIds.length} VODs` : "";
  const who = edit.userId === me?.id ? "you" : edit.createdBy ?? "teammate";
  return row({
    // Vertical (9:16) edits open in the vertical editor, horizontal ones in Player 2 (badrfly 2026-11-17)
    depth, icon: edit.vertical ? "device-mobile" : "monitor", label: edit.title || "Untitled edit", current: Number(loaded?.editId) === edit.id,
    detail: `${edit.vertical ? "9:16 · " : ""}${who} · ${formatDuration(Math.round(edit.lengthSeconds ?? 0))}`,
    title: `${edit.vertical ? "Vertical (9:16) edit" : "Horizontal edit"}: ${edit.title || "Untitled edit"} · by ${who}${others}${edit.canEdit ? "" : edit.canCopy === false ? " (view only)" : " (view only: make your own copy to change it)"}`,
    onClick: open,
    actions: edit.canEdit ? [iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameEdit(edit) })] : [],
    menu: () => editMenu(edit),
  });
}

// The right-click menu of an edit (the tree's line, the library's line under its clip or video)
export function editMenu(edit) {
  const canMake = can("AddEntries") && !me?.support;
  return [
    { label: edit.vertical ? "Open in the vertical editor" : "Load in Player 2", icon: edit.vertical ? "device-mobile" : "film-strip", run: () => hooks.onOpenEdit(edit.id) },
    edit.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameEdit(edit) },
    // A teammate's vertical edit is copied and exported only with the role permissions for that (canCopy / canDownload)
    canMake && edit.canCopy !== false && { label: edit.canEdit ? "Duplicate" : "Make my own copy", icon: "copy", run: () => hooks.onCopyEdit(edit) },
    edit.canDownload !== false && { label: "Add to the export list", icon: "download-simple", run: () => hooks.onExportEdit(edit) },
    edit.canDelete && { label: "Delete this edit", icon: "trash", danger: true, run: () => deleteEdit(edit) },
  ].filter(Boolean);
}

// ---------- renaming ----------

async function ask(heading, value) {
  const title = await askText({ heading, label: "Name", value, confirm: "Rename", maxLength: 200 });
  return title === null || !title.trim() || title.trim() === value ? null : title.trim();
}

async function afterChange(vodId, kind) {
  if (hooks.root) await loadVod(vodId);
  hooks.onChanged(kind);
}

async function renameClip(clip) {
  const title = await ask("Rename clip", clip.title);
  if (!title) return;
  try {
    await api("PATCH", `/api/entries/${clip.id}`, { title });
    await afterChange(clip.vodId, "clip");
  } catch (error) {
    toast(error.message, "error");
  }
}

async function renameVideo(video) {
  const title = await ask("Rename video", video.title);
  if (!title) return;
  try {
    await api("PUT", `/api/videos/${video.id}`, { title });
    await afterChange(video.vodId, "video");
  } catch (error) {
    toast(error.message, "error");
  }
}

async function renameSegment(segment, video) {
  const label = await ask("Rename segment", segment.label || "");
  if (!label) return;
  try {
    // The whole marker, as it is: only the label changes (cuts: null keeps its cut-out parts)
    await api("PUT", `/api/markers/${segment.markerId}`, {
      seconds: segment.startSeconds, endSeconds: segment.endSeconds, label, color: segment.color, cuts: null,
    });
    await afterChange(video.vodId, "segment");
  } catch (error) {
    toast(error.message, "error");
  }
}

async function renameEdit(edit) {
  const title = await ask("Rename edit", edit.title);
  if (!title) return;
  try {
    if (hooks.onRenameEdit && Number(hooks.loaded()?.editId) === edit.id) await hooks.onRenameEdit(edit, title);
    else await api("PUT", `/api/projects/${edit.id}`, { title });
    await afterChange(edit.vodId, "edit");
  } catch (error) {
    toast(error.message, "error");
  }
}

async function deleteEdit(edit) {
  const ok = await confirmAction({
    heading: "Delete this edit?",
    text: `"${edit.title || "Untitled edit"}" is deleted for the whole team. The clip, video and segments it was made from stay as they are.`,
    confirm: "Delete",
  });
  if (!ok) return;
  try {
    await hooks.onDeleteEdit(edit);
    await afterChange(edit.vodId, "edit");
  } catch (error) {
    toast(error.message, "error");
  }
}
