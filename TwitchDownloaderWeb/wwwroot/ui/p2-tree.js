// Player 2's picker (badrfly 2026-11-17, "the VOD is the project"): a tree of what the team made on each VOD.
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
};

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
  close: () => {},
};

export function setUpTree(options) {
  hooks = { ...hooks, ...options };
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
  const first = [p1, loaded].filter((id, i, all) => id && all.indexOf(id) === i);
  return [...first, ...ids.filter((id) => !first.includes(id))];
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
      || (state.loadingWork ? "Loading…" : "Nothing yet. Mark segments or clips on a VOD in Player 1, then load them here.")));
  }
  for (const vodId of ids) rows.push(...vodRows(vodId, loaded));
  root.replaceChildren(...rows);
}

function row({ key = null, parent = null, depth, icon, label, detail = null, current = false, expandable = false, onClick, onDouble, actions = [], menu = null, title = null }) {
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
    current: Number(loaded?.vodId) === vodId,
    title: `${label}${work?.streamDateUtc ? ` · ${formatDate(work.streamDateUtc, false)}` : ""}`,
    actions: [iconButton({ icon: "monitor-play", label: "Load in Player 1", onClick: () => { hooks.onLoadVod(vodId, state.titles.get(vodId) ?? label); hooks.close(); } })],
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
  const videosKey = `${key}:videos`;
  rows.push(row({ key: videosKey, parent: key, depth: 1, icon: "film-strip", label: "Videos", detail: String(data.videos.length), expandable: true }));
  if (state.expanded.has(videosKey)) {
    if (!data.videos.length) rows.push(el("p", { class: "tree-empty faint", style: "--depth:2" }, "No videos yet. Link segments on Player 1 to make one."));
    for (const video of data.videos) rows.push(...videoRows(video, editsOf("videoId", video.id), videosKey, loaded));
  }

  const clipsKey = `${key}:clips`;
  rows.push(row({ key: clipsKey, parent: key, depth: 1, icon: "scissors", label: "Clips", detail: String(data.clips.length), expandable: true }));
  if (state.expanded.has(clipsKey)) {
    if (!data.clips.length) rows.push(el("p", { class: "tree-empty faint", style: "--depth:2" }, "No clips yet. Mark one on Player 1 (X)."));
    for (const clip of data.clips) rows.push(...clipRows(clip, editsOf("entryId", clip.id), clipsKey, loaded));
  }

  const other = data.edits.filter((e) => !e.entryId && !e.videoId);
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
    key, parent, depth: 2, icon: "film-strip", label: video.title, expandable: true, current: isLoaded,
    detail: `${streams > 1 ? `${streams} streams · ` : ""}${formatDuration(Math.round(video.lengthSeconds ?? 0))}`,
    title: `${video.title}${video.createdBy ? ` · by ${video.createdBy}` : ""} (double-click to load)`,
    onDouble: load,
    actions: [
      iconButton({ icon: "play", label: "Load in Player 2", onClick: load }),
      video.canEdit ? iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameVideo(video) }) : null,
    ].filter(Boolean),
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: load },
      video.canEdit && hooks.onAddSegments && { label: "Add segments…", icon: "link-simple", run: () => {
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
function segmentRow(segment, video, loaded, home) {
  const vodId = String(segment.vodId ?? video.vodId);
  const canRename = !me?.support && !segment.removed && (can("EditAnyEntries") || (can("AddEntries") && video.userId === me?.id));
  const data = {
    id: segment.markerId, vodId, seconds: segment.startSeconds, endSeconds: segment.endSeconds,
    label: segment.label, color: segment.color, cuts: [...(segment.cuts ?? [])],
  };
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
    key, parent, depth: 2, icon: "scissors", label: clip.title, expandable: true,
    current: Number(loaded?.entryId) === clip.id && !loaded?.editId,
    detail: formatDuration(Math.round(clip.clipLengthSeconds ?? 0)),
    title: `${clip.title}${clip.addedBy ? ` · by ${clip.addedBy}` : ""} (double-click to load)`,
    onDouble: load,
    actions: [
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
  const canMake = can("AddEntries") && !me?.support;
  return row({
    // Vertical (9:16) edits open in the vertical editor, horizontal ones in Player 2 (badrfly 2026-11-17)
    depth, icon: edit.vertical ? "device-mobile" : "monitor", label: edit.title || "Untitled edit", current: Number(loaded?.editId) === edit.id,
    detail: `${edit.vertical ? "9:16 · " : ""}${who} · ${formatDuration(Math.round(edit.lengthSeconds ?? 0))}`,
    title: `${edit.vertical ? "Vertical (9:16) edit" : "Horizontal edit"}: ${edit.title || "Untitled edit"} · by ${who}${others}${edit.canEdit ? "" : edit.canCopy === false ? " (view only)" : " (view only: make your own copy to change it)"}`,
    onClick: open,
    actions: edit.canEdit ? [iconButton({ icon: "pencil-simple", label: "Rename", onClick: () => renameEdit(edit) })] : [],
    menu: () => [
      { label: edit.vertical ? "Open in the vertical editor" : "Load in Player 2", icon: edit.vertical ? "device-mobile" : "film-strip", run: open },
      edit.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameEdit(edit) },
      // A teammate's vertical edit is copied and exported only with the role permissions for that (canCopy / canDownload)
      canMake && edit.canCopy !== false && { label: edit.canEdit ? "Duplicate" : "Make my own copy", icon: "copy", run: () => hooks.onCopyEdit(edit) },
      edit.canDownload !== false && { label: "Add to the export list", icon: "download-simple", run: () => hooks.onExportEdit(edit) },
      edit.canDelete && { label: "Delete this edit", icon: "trash", danger: true, run: () => deleteEdit(edit) },
    ].filter(Boolean),
  });
}

// ---------- renaming ----------

async function ask(heading, value) {
  const title = await askText({ heading, label: "Name", value, confirm: "Rename", maxLength: 200 });
  return title === null || !title.trim() || title.trim() === value ? null : title.trim();
}

async function afterChange(vodId, kind) {
  await loadVod(vodId);
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
