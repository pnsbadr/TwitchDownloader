// The Export screen (frontend rework step 8). The left panel is the same as on the Editor screen; cards (and segments or clips
// from Player 1's right-click menu) go on the export list, which is kept in this browser (localStorage) across reloads.
//
//   Preview (view only)    | Settings of the selected item: file name, quality, format
//   Export list (drop zone) + Export | Your exports (progress, downloads)
//
// Export sends the whole list as one POST /api/exports and clears it. With one item on the list its files download by
// themselves once ready; with several they're queued and downloaded from "Your exports". A VOD dropped on the list opens a
// pop-up (player, title, start/end) for a one-off range: it never becomes a library entry. Videos are a .zip of their
// segments or, when the team has "render as one" on, one file. Clips are horizontal and/or vertical; the vertical render is
// picked from the team's (DownloadTeamVerticals) or is the member's own newest.

import { $, el, api, me, can, toast, formatDate, formatDuration, loadSetting, saveSetting, iconButton, twitchThumb, thumbStyle } from "./core.js";
import { dropTarget } from "./drag.js";
import { MediaPlayer, formatClock } from "./media.js";
import { timeBox } from "./timebox.js";
import { keptParts, keptSeconds, normalizeCuts } from "./cuts.js";
import { confirmAction } from "./dialogs.js";

const LIST_KEY = "clipco.export.list";
const POLL_MS = 2000;
const MIN_RANGE = 0.5;
const KINDS = {
  vod: { icon: "broadcast", label: "VOD range" },
  clip: { icon: "film-strip", label: "Clip" },
  video: { icon: "film-slate", label: "Video" },
  project: { icon: "stack", label: "Project" },
  segment: { icon: "brackets-square", label: "Segment" },
};
const kindLabel = (item) => (item.editId ? `${KINDS[item.kind].label} edit` : KINDS[item.kind].label);
const ITEM_KIND_ICON = { VodRange: "broadcast", Clip: "film-strip", ClipVertical: "device-mobile", Video: "film-slate", Segment: "brackets-square", Project: "stack", ProjectVertical: "device-mobile" };

let list = [];
let selectedKey = null;
let batches = [];
let pollTimer = null;
let demo = false;
let otherPlayers = () => [];
const autoDownload = new Set();   // exports of a one-item list: download their files once ready
const vodInfos = new Map();       // vodId -> promise of /api/vods/info
const renderLists = new Map();    // entryId -> renders of the clip

let xp = null;                    // the preview MediaPlayer
let preview = null;               // { key, vodId, parts: [{ start, end }], index, ended, settleUntil }

// ---------- set up ----------

export function setUpExport(options = {}) {
  demo = Boolean(options.demo);
  otherPlayers = options.otherPlayers ?? (() => []);
  list = (loadSetting(LIST_KEY, []) ?? []).filter((item) => item && KINDS[item.kind]);
  for (const item of list) delete item.error;

  dropTarget($("export-list-panel"), {
    accepts: (item) => !$("view-export").hidden && Boolean(KINDS[item.kind]),
    onDrop: (item) => addToExport(item),
  });
  Sortable.create($("export-list"), {
    handle: ".xl-handle",
    draggable: ".xl-row",
    animation: 120,
    onEnd: ({ oldIndex, newIndex }) => {
      if (oldIndex === newIndex || oldIndex == null || newIndex == null) return;
      const [moved] = list.splice(oldIndex, 1);
      list.splice(newIndex, 0, moved);
      save();
      renderList();
    },
  });
  $("export-button").addEventListener("click", runExport);
  $("export-clear").addEventListener("click", clearList);
  setUpPreview();
  renderList();
  renderSettings();
}

// The Export screen was opened: read the member's exports
export function exportShown() {
  if (!demo) refreshBatches();
}

export function pausePreview() {
  if (xp?.ready && xp.playing()) xp.pause();
}

function save() {
  saveSetting(LIST_KEY, list.map(({ error, ...item }) => item));
}

function selected() {
  return list.find((item) => item.key === selectedKey) ?? null;
}

// ---------- adding ----------

// item = what was dropped or picked: { kind: "vod" | "clip" | "video" | "segment", data }
export function addToExport(item, { quiet = true } = {}) {
  const data = item?.data ?? {};
  if (item.kind === "vod") {
    openVodDialog({ vodId: String(data.vodId), title: data.title, lengthSeconds: data.lengthSeconds, date: data.createdAtUtc, thumb: thumbOf(data.thumbnailUrl) });
    return;
  }
  if (item.kind === "clip" && data.kind === "Clip") {
    toast("Twitch clips can't be exported here.", "error");
    return;
  }
  if (item.kind === "project" && data.canDownload === false) {
    toast("Your role doesn't let you export teammates' vertical edits.", "error");
    return;
  }
  const made = item.kind === "edit" ? fromEdit(data) : fromDrop(item.kind, data);
  if (!made) return;
  const existing = list.find((x) => x.key === made.key);
  if (existing) {
    toast(`"${existing.title}" is already on the export list.`);
    select(existing.key);
    return;
  }
  list.push(made);
  save();
  renderList();
  select(made.key);
  if (!quiet) toast(`Added "${made.title}" to the export list.`);
}

function fromDrop(kind, data) {
  const base = {
    kind, id: data.id, vodId: String(data.vodId ?? ""), fileName: "", quality: "", horizontal: true, vertical: false, renderId: "",
    joined: false, thumb: thumbOf(data.thumbnailUrl), frame: data.frameUrl ?? null,
  };
  if (kind === "clip") {
    const start = data.startSeconds ?? 0;
    const end = data.endSeconds ?? data.vodLengthSeconds ?? start;
    const cuts = normalizeCuts(data.cuts ?? [], start, end);
    return {
      ...base, key: `clip:${data.id}`, title: data.title, vodDate: data.streamDateUtc, start, end, cuts,
      length: data.clipLengthSeconds ?? keptSeconds(start, end, cuts), reference: Boolean(data.isReferenceOnly),
    };
  }
  if (kind === "video") {
    return { ...base, key: `video:${data.id}`, ...videoFields(data) };
  }
  if (kind === "project") {
    return { ...base, key: `project:${data.id}`, vodId: "", ...projectFields(data) };
  }
  if (kind === "segment") {
    if (data.endSeconds == null) {
      toast("Markers can't be exported, only segments.", "error");
      return null;
    }
    const cuts = normalizeCuts(data.cuts ?? [], data.seconds, data.endSeconds);
    return {
      ...base, key: `segment:${data.id}`, title: data.label || "Segment", vodTitle: data.vodTitle, vodDate: data.vodDate,
      start: data.seconds, end: data.endSeconds, cuts, length: keptSeconds(data.seconds, data.endSeconds, cuts),
    };
  }
  return null;
}

// A saved edit from Player 2: data = { edit (as GET /api/edits/{id} returns it), itemTitle, editName, thumbnailUrl?, frameUrl?, vodDate? }.
// The list item keeps the item's kind (for its settings) plus editId; the export sends { kind: "edit", editId }.
function fromEdit(data) {
  const edit = data.edit;
  if (!edit?.id) return null;
  return {
    kind: edit.kind, id: edit.entryId ?? edit.markerId ?? edit.videoId, vodId: String(edit.vodId ?? ""), editId: edit.id,
    key: `edit:${edit.id}`, itemTitle: data.itemTitle ?? "", editName: data.editName ?? edit.title ?? "Edit",
    fileName: "", quality: "", horizontal: true, vertical: false, renderId: "", joined: false,
    thumb: thumbOf(data.thumbnailUrl), frame: data.frameUrl ?? null, vodDate: data.vodDate ?? null, reference: Boolean(data.reference),
    ...editFields(edit, data.itemTitle, data.editName),
  };
}

// What an edit plays: a clip/segment's whole range less the edit's cut parts; a video's segments with their own cut-out parts
// plus the edit's (a segment the edit cuts out whole is left out)
function editFields(edit, itemTitle, editName) {
  const name = edit.title || editName || "Edit";
  const title = `${itemTitle || edit.itemTitle || "Untitled"} · ${name}`;
  if (edit.kind === "video") {
    const own = new Map((edit.parts ?? []).map((p) => [p.markerId, p.cuts ?? []]));
    const parts = (edit.sourceParts ?? []).map((s) => ({
      start: s.start, end: s.end, label: s.label, cuts: normalizeCuts([...(s.cuts ?? []), ...(own.get(s.markerId) ?? [])], s.start, s.end),
    })).filter((p) => keptSeconds(p.start, p.end, p.cuts) > 0);
    return {
      title, editName: name, parts, length: edit.keptSeconds ?? parts.reduce((sum, p) => sum + keptSeconds(p.start, p.end, p.cuts), 0),
      start: parts.length ? Math.min(...parts.map((p) => p.start)) : 0, end: parts.length ? Math.max(...parts.map((p) => p.end)) : 0,
    };
  }
  const start = edit.sourceStart ?? 0;
  const end = edit.sourceEnd ?? start;
  const cuts = normalizeCuts(edit.cuts ?? [], start, end);
  return { title, editName: name, start, end, cuts, length: edit.keptSeconds ?? keptSeconds(start, end, cuts) };
}

function videoFields(video) {
  const parts = [...(video.segments ?? [])].sort((a, b) => a.position - b.position).map((s) => ({
    start: s.startSeconds, end: s.endSeconds, label: s.label, cuts: normalizeCuts([...(s.cuts ?? []), ...(s.videoCuts ?? [])], s.startSeconds, s.endSeconds),
  }));
  return {
    title: video.title, vodDate: video.streamDateUtc, parts, frame: video.frameUrl ?? null, length: video.lengthSeconds ?? parts.reduce((sum, p) => sum + keptSeconds(p.start, p.end, p.cuts), 0),
    start: parts.length ? Math.min(...parts.map((p) => p.start)) : 0, end: parts.length ? Math.max(...parts.map((p) => p.end)) : 0,
  };
}

// A project (Player 2): its length and pieces; files are made from what its timeline plays (gaps black)
function projectFields(project) {
  const length = project.lengthSeconds ?? 0;
  return { title: project.title, length, pieceCount: project.pieceCount ?? project.content?.pieces?.length ?? 0, start: 0, end: length };
}

const thumbOf = twitchThumb;

function remove(key) {
  list = list.filter((item) => item.key !== key);
  if (selectedKey === key) {
    selectedKey = null;
    closePreview();
  }
  save();
  renderList();
  renderSettings();
}

async function clearList() {
  if (!list.length) return;
  const ok = await confirmAction({ heading: "Clear the export list?", text: "Everything on it is taken off. Nothing is deleted.", confirm: "Clear" });
  if (!ok) return;
  list = [];
  selectedKey = null;
  closePreview();
  save();
  renderList();
  renderSettings();
}

// ---------- the list ----------

const twoFormats = (item) => item.kind === "clip" || item.kind === "project";

function filesOf(item) {
  return twoFormats(item) ? Number(item.horizontal) + Number(item.vertical) : 1;
}

// Like the server: verticals don't count toward the time limit
function secondsOf(item) {
  return twoFormats(item) && !item.horizontal ? 0 : item.length ?? 0;
}

function formatOf(item) {
  if (twoFormats(item)) return [item.horizontal && "horizontal", item.vertical && "vertical"].filter(Boolean).join(" + ") || "nothing picked";
  if (item.kind === "video") return item.joined ? "one file" : `.zip of ${item.parts?.length ?? 0} file${item.parts?.length === 1 ? "" : "s"}`;
  return null;
}

function renderList() {
  const box = $("export-list");
  box.replaceChildren(...(list.length ? list.map(row) : [emptyHint()]));
  const files = list.reduce((sum, item) => sum + filesOf(item), 0);
  const seconds = list.reduce((sum, item) => sum + secondsOf(item), 0);
  $("export-count").textContent = list.length ? `${list.length} item${list.length === 1 ? "" : "s"}` : "";
  $("export-summary").textContent = list.length ? `${files} file${files === 1 ? "" : "s"} · ${formatDuration(seconds, { short: false })}` : "";
  $("export-clear").disabled = !list.length;
  const allowed = can("GenerateFiles") && !me?.support;
  const button = $("export-button");
  button.disabled = !list.length || !allowed || list.some((item) => twoFormats(item) && !item.horizontal && !item.vertical);
  button.title = allowed ? "" : "Your role can't make files.";
}

function emptyHint() {
  return el("div", { class: "drop-hint export-drop-hint" },
    el("sl-icon", { library: "ph", name: "download-simple" }),
    el("p", {}, "Drag VODs, videos and clips here"),
    el("p", { class: "faint" }, "or use the download icon on a card. Segments: right-click them on Player 1."));
}

function row(item) {
  const kind = KINDS[item.kind];
  const range = item.kind === "video"
    ? `${item.parts?.length ?? 0} segment${item.parts?.length === 1 ? "" : "s"}`
    : item.kind === "project" ? `${item.pieceCount ?? 0} piece${item.pieceCount === 1 ? "" : "s"}`
    : `${formatDuration(item.start, { short: false })} – ${formatDuration(item.end, { short: false })}`;
  const quality = item.quality || (item.kind === "clip" ? "its own quality" : "best quality");
  const node = el("div", {
    class: `xl-row${item.key === selectedKey ? " selected" : ""}${item.error || item.gone ? " has-error" : ""}`,
    role: "listitem",
    tabindex: "0",
    "aria-label": item.title,
  },
  el("span", { class: "xl-handle", title: "Drag to change the order" }, el("sl-icon", { library: "ph", name: "dots-six-vertical" })),
  el("div", { class: "xl-thumb", style: thumbStyle(item.frame, item.thumb) },
    el("span", { class: "xl-kind" }, el("sl-icon", { library: "ph", name: kind.icon }))),
  el("div", { class: "xl-main" },
    el("div", { class: "xl-title" }, el("span", { class: "xl-kind-label" }, kindLabel(item)), el("span", { class: "xl-name", title: item.title }, item.title)),
    el("div", { class: "xl-meta num" }, [
      item.vodDate ? `VOD ${formatDate(item.vodDate, false)}` : null,
      range,
      formatDuration(item.length ?? 0, { short: false }),
    ].filter(Boolean).join(" · ")),
    el("div", { class: "xl-meta" }, [`${item.fileName || item.title}`, quality, formatOf(item)].filter(Boolean).join(" · ")),
    item.gone ? el("div", { class: "xl-error" }, "Gone: it was deleted. Take it off the list.") : null,
    item.error ? el("div", { class: "xl-error" }, item.error) : null),
  iconButton({ icon: "x", label: "Take off the list", onClick: (event) => { event.stopPropagation(); remove(item.key); } }));
  node.addEventListener("click", () => select(item.key));
  node.addEventListener("keydown", (event) => {
    if (event.target !== node) return;
    if (event.key === "Enter") select(item.key);
    if (event.key === "Delete") remove(item.key);
  });
  return node;
}

function select(key) {
  const item = list.find((x) => x.key === key);
  if (!item) return;
  const changed = selectedKey !== key;
  selectedKey = key;
  renderList();
  renderSettings();
  if (changed) showPreview(item);
  if (!demo) refreshItem(item);
}

function changed(item, { list: relist = true, settings = false, preview: replay = false } = {}) {
  delete item.error;
  save();
  if (relist) renderList();
  if (settings) renderSettings();
  if (replay && item.key === selectedKey) showPreview(item);
}

// Reads the item again (it may have been trimmed, cut or deleted since it was put on the list)
async function refreshItem(item) {
  try {
    if (item.editId) {
      const edit = await api("GET", `/api/edits/${item.editId}`);
      Object.assign(item, editFields(edit, item.itemTitle, edit.title || item.editName), { gone: false });
      if (!item.thumb || !item.vodDate) {
        try {
          const info = await vodInfo(item.vodId);
          item.thumb ??= thumbOf(info.thumbnailUrl);
          item.vodDate ??= info.streamDateUtc;
        } catch {
          // the VOD's picture stays out
        }
      }
      if (item.kind === "clip") loadRenders(item);
    } else if (item.kind === "clip") {
      const entry = await api("GET", `/api/entries/${item.id}`);
      const start = entry.startSeconds ?? item.start;
      const end = entry.endSeconds ?? entry.vodLengthSeconds ?? item.end;
      Object.assign(item, {
        title: entry.title, start, end, cuts: normalizeCuts(entry.cuts ?? [], start, end), length: entry.clipLengthSeconds,
        reference: entry.isReferenceOnly, vodDate: entry.streamDateUtc, thumb: thumbOf(entry.thumbnailUrl) ?? item.thumb, frame: entry.frameUrl ?? null, gone: false,
      });
      loadRenders(item);
    } else if (item.kind === "video") {
      Object.assign(item, videoFields(await api("GET", `/api/videos/${item.id}`)), { gone: false });
    } else if (item.kind === "project") {
      Object.assign(item, projectFields(await api("GET", `/api/projects/${item.id}`)), { gone: false });
    } else if (item.kind === "segment") {
      const markers = await api("GET", `/api/markers?vodIds=${encodeURIComponent(item.vodId)}`);
      const marker = markers.find((m) => m.id === item.id);
      if (!marker || marker.endSeconds == null) throw Object.assign(new Error("gone"), { status: 404 });
      const cuts = normalizeCuts(marker.cuts ?? [], marker.seconds, marker.endSeconds);
      Object.assign(item, { title: marker.label || "Segment", start: marker.seconds, end: marker.endSeconds, cuts, length: keptSeconds(marker.seconds, marker.endSeconds, cuts), gone: false });
    }
  } catch (error) {
    if (error.status === 404) item.gone = true;
    else return;
  }
  save();
  renderList();
  if (item.key === selectedKey) {
    renderSettings();
    if (preview?.key === item.key) refreshPreviewParts(item);
  }
}

function vodInfo(vodId) {
  if (!vodInfos.has(vodId)) {
    const promise = api("GET", `/api/vods/info?vod=${encodeURIComponent(vodId)}`).catch((error) => {
      vodInfos.delete(vodId);
      throw error;
    });
    vodInfos.set(vodId, promise);
  }
  return vodInfos.get(vodId);
}

async function loadRenders(item) {
  try {
    const state = await api("GET", `/api/entries/${item.id}/shortform`);
    renderLists.set(item.id, state.renders ?? []);
  } catch {
    renderLists.set(item.id, []);
  }
  // Vertical can't be kept without a render to use
  const usable = usableRenders(item);
  if (item.vertical && !usable.length) item.vertical = false;
  if (item.renderId && !usable.some((r) => r.id === item.renderId)) item.renderId = "";
  save();
  renderList();
  if (item.key === selectedKey) renderSettings();
}

// The renders this member may export: anyone's with "Download team vertical entries", else only their own newest
function usableRenders(item) {
  const renders = rendersOf(item);
  if (can("DownloadTeamVerticals")) return renders;
  const own = renders.filter((r) => r.createdByUserId === me?.id).sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
  return own.slice(0, 1);
}

// ---------- settings of the selected item ----------

function renderSettings() {
  const body = $("export-settings-body");
  const item = selected();
  if (!item) {
    body.replaceChildren(el("p", { class: "faint export-empty-note" }, "Select an item on the list to set its file name, quality and format."));
    return;
  }
  const fields = [
    el("div", { class: "xs-title" }, el("span", { class: "xl-kind-label" }, kindLabel(item)), el("span", { class: "xs-name" }, item.title)),
    fileNameField(item),
    qualityField(item),
  ];
  if (item.kind === "vod") fields.push(rangeFields(item));
  if (item.kind === "clip") fields.push(clipFormat(item));
  if (item.kind === "video") fields.push(videoFormat(item));
  if (item.kind === "project") fields.push(projectFormat(item));
  body.replaceChildren(...fields);
}

function fileNameField(item) {
  const input = el("sl-input", { label: "File name", size: "small", value: item.fileName, placeholder: item.title, maxlength: "200", clearable: true });
  input.addEventListener("sl-input", () => {
    item.fileName = input.value.trim();
    changed(item);
  });
  return input;
}

function qualityField(item) {
  const own = item.kind === "clip";
  const select = el("sl-select", { label: "Quality", size: "small", hoist: true, value: item.quality || "default" },
    el("sl-option", { value: "default" }, own ? (item.reference ? "Its own quality" : "Its own quality (the stored file is copied)") : "Best"));
  if (item.kind === "clip" && !item.horizontal) select.disabled = true;
  if (item.quality) select.append(el("sl-option", { value: item.quality }, item.quality));
  select.addEventListener("sl-change", () => {
    item.quality = select.value === "default" ? "" : select.value;
    changed(item);
  });
  if (!demo && item.vodId) {
    vodInfo(item.vodId).then((info) => {
      if (!select.isConnected) return;
      const names = (info.qualities ?? []).filter((q) => q !== item.quality);
      select.append(...names.map((q) => el("sl-option", { value: q }, q)));
    }).catch(() => {});
  }
  return select;
}

function rangeFields(item) {
  const max = item.vodLength ?? Infinity;
  const start = timeBox({ seconds: item.start, fraction: true, label: "Start", max, onChange: (s) => setRange(item, s, item.end) });
  const end = timeBox({ seconds: item.end, fraction: true, label: "End", max, onChange: (s) => setRange(item, item.start, s) });
  return el("div", { class: "xs-range" },
    el("label", {}, el("span", { class: "xs-label" }, "Start"), start),
    el("label", {}, el("span", { class: "xs-label" }, "End"), end),
    el("sl-button", { size: "small", onclick: () => openVodDialog({ vodId: item.vodId, title: item.vodTitle, lengthSeconds: item.vodLength, date: item.vodDate, thumb: item.thumb }, item) },
      el("sl-icon", { slot: "prefix", library: "ph", name: "broadcast" }), "Pick on the VOD"));
}

function setRange(item, start, end) {
  if (end - start < MIN_RANGE) {
    toast("The end has to be at least half a second after the start.", "error");
    renderSettings();
    return;
  }
  Object.assign(item, { start, end, length: end - start });
  changed(item, { preview: true });
}

// A project: one landscape file and/or one 9:16 file with each piece's boxes (Player 2's 9:16 view)
function projectFormat(item) {
  const horizontal = el("sl-checkbox", { checked: item.horizontal }, "Horizontal");
  const vertical = el("sl-checkbox", { checked: item.vertical }, "Vertical (9:16)");
  horizontal.addEventListener("sl-change", () => {
    item.horizontal = horizontal.checked;
    changed(item, { settings: true });
  });
  vertical.addEventListener("sl-change", () => {
    item.vertical = vertical.checked;
    changed(item, { settings: true });
  });
  return el("div", { class: "xs-format" },
    el("div", { class: "xs-label" }, "Format"), el("div", { class: "xs-checks" }, horizontal, vertical),
    el("p", { class: "faint xs-note" }, "Made from what the project's timeline plays, top row first; gaps are black. Vertical uses each piece's boxes (Player 2's 9:16 view; pieces without boxes get a centered crop)."));
}

function clipFormat(item) {
  const renders = renderLists.has(item.id) ? rendersOf(item) : undefined;
  const usable = usableRenders(item);
  const horizontal = el("sl-checkbox", { checked: item.horizontal }, "Horizontal");
  const vertical = el("sl-checkbox", { checked: item.vertical, disabled: !usable.length }, "Vertical (9:16)");
  horizontal.addEventListener("sl-change", () => {
    item.horizontal = horizontal.checked;
    changed(item, { settings: true });
  });
  vertical.addEventListener("sl-change", () => {
    item.vertical = vertical.checked;
    changed(item, { settings: true });
  });
  const parts = [el("div", { class: "xs-label" }, "Format"), el("div", { class: "xs-checks" }, horizontal, vertical)];
  if (renders === undefined) {
    parts.push(el("p", { class: "faint xs-note" }, "Looking for vertical versions…"));
  } else if (!usable.length) {
    parts.push(el("p", { class: "faint xs-note" }, renders.length
      ? "Only the team's vertical versions exist, and your role can only export your own. Make one in the vertical editor."
      : item.editId
        ? "No vertical version of this edit yet. Load the edit in Player 2 and make one in the vertical editor."
        : "No vertical version yet. Make one in the vertical editor first."));
  } else if (can("DownloadTeamVerticals")) {
    const choice = el("sl-select", { label: "Vertical version", size: "small", hoist: true, value: item.renderId || "own", disabled: !item.vertical },
      el("sl-option", { value: "own" }, "My newest"),
      ...usable.map((r) => el("sl-option", { value: r.id }, `${r.createdBy ?? "Someone"} · ${formatDate(r.createdAtUtc)}`)));
    choice.addEventListener("sl-change", () => {
      item.renderId = choice.value === "own" ? "" : choice.value;
      changed(item, { settings: true });
    });
    parts.push(choice);
    parts.push(renderPreview(item, item.renderId ? usable.find((r) => r.id === item.renderId) : newestOwn(item)));
  } else {
    parts.push(el("p", { class: "faint xs-note" }, `Uses your newest vertical version (${formatDate(usable[0].createdAtUtc)}).`));
    parts.push(renderPreview(item, usable[0]));
  }
  return el("div", { class: "xs-format" }, ...parts);
}

// A clip's own renders, or an edit's: the server only exports a render made for the same one
function rendersOf(item) {
  return (renderLists.get(item.id) ?? []).filter((r) => (r.editId ?? null) === (item.editId ?? null));
}

function newestOwn(item) {
  return rendersOf(item).filter((r) => r.createdByUserId === me?.id).sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc))[0] ?? null;
}

// The vertical render's thumbnail; clicking it plays the render here
function renderPreview(item, render) {
  if (!render) return el("p", { class: "faint xs-note" }, "You have no vertical version of your own: pick one of the team's.");
  const box = el("button", { type: "button", class: "xs-render", title: "Play this vertical version" },
    render.thumbnailUrl ? el("img", { src: render.thumbnailUrl, alt: "" }) : null,
    el("sl-icon", { library: "ph", name: "play-circle" }));
  box.addEventListener("click", () => {
    pausePreview();
    const video = el("video", { src: `${render.fileUrl}${render.fileUrl.includes("?") ? "&" : "?"}inline=true`, controls: true, autoplay: true, playsinline: true });
    box.replaceWith(el("div", { class: "xs-render playing" }, video));
  });
  return box;
}

function videoFormat(item) {
  const allowed = Boolean(me?.combineSegmentDownloads);
  const group = el("sl-radio-group", { label: "Format", size: "small", value: item.joined && allowed ? "joined" : "zip" },
    el("sl-radio", { value: "zip" }, `Separate files (.zip, one per segment)`),
    el("sl-radio", { value: "joined", disabled: !allowed }, "One file"));
  group.addEventListener("sl-change", () => {
    item.joined = group.value === "joined";
    changed(item);
  });
  return el("div", { class: "xs-format" }, group,
    allowed ? null : el("p", { class: "faint xs-note" }, "Rendering a video as one file isn't turned on for your team."));
}

// ---------- preview (view only) ----------

function setUpPreview() {
  xp = new MediaPlayer($("xp-media"), {
    onTick: previewTick,
    onState: () => {
      if (xp.playing()) for (const other of otherPlayers()) if (other?.playing()) other.pause();
      renderPreviewButtons();
    },
  });
  $("xp-play").addEventListener("click", previewPlayPause);
  $("xp-restart").addEventListener("click", () => previewSeek(0));
  $("xp-full").addEventListener("click", () => xp.fullscreen());
  $("xp-mute").addEventListener("click", () => {
    const volume = xp.volume;
    if (!volume) return;
    if (volume.volume === 0) xp.setVolume(0.5, false);
    else xp.setVolume(null, !volume.muted);
  });
  $("xp-volume").addEventListener("sl-input", (event) => xp.setVolume(Number(event.target.value) / 100, false));
  $("xp-progress").addEventListener("pointerdown", (event) => {
    if (!preview) return;
    const rect = event.currentTarget.getBoundingClientRect();
    previewSeek(((event.clientX - rect.left) / rect.width) * totalOf(preview.parts));
  });
}

function previewPartsOf(item) {
  if (item.kind === "project") return [];
  if (item.kind === "video") return (item.parts ?? []).flatMap((p) => keptParts(p.start, p.end, p.cuts ?? []));
  return keptParts(item.start, item.end, item.cuts ?? []);
}

const totalOf = (parts) => parts.reduce((sum, p) => sum + p.end - p.start, 0);

async function showPreview(item) {
  if (demo) return;
  const parts = previewPartsOf(item);
  $("xp-info").textContent = item.title;
  $("xp-info").title = item.title;
  if (!parts.length || !item.vodId) {
    closePreview();
    return;
  }
  preview = { key: item.key, vodId: item.vodId, parts, index: 0, ended: false, settleUntil: 0 };
  $("xp-hint").hidden = true;
  $("xp-media").hidden = false;
  note("Loading…");
  try {
    await xp.open(item.vodId, parts[0].start);
    xp.pause();
    if (preview?.key !== item.key) return;
    note(xp.fallbackReason ? `Twitch player: our player couldn't play this VOD (${xp.fallbackReason}).` : "");
  } catch (error) {
    if (preview?.key === item.key) note(`Couldn't play this VOD: ${error.message}`);
  }
  renderPreviewButtons();
}

function refreshPreviewParts(item) {
  if (!preview || preview.vodId !== item.vodId) {
    showPreview(item);
    return;
  }
  preview.parts = previewPartsOf(item);
  preview.index = Math.min(preview.index, preview.parts.length - 1);
}

function closePreview() {
  preview = null;
  xp?.close();
  $("xp-media").hidden = true;
  $("xp-hint").hidden = false;
  $("xp-info").textContent = "";
  note("");
  renderPreviewButtons();
}

function note(text) {
  $("xp-note").textContent = text;
  $("xp-note").hidden = !text;
}

// Plays the kept parts one after the other (cut-out parts and the gaps between a video's segments are skipped)
function previewTick(position) {
  const p = preview;
  if (!p || !p.parts.length) return;
  if (performance.now() > p.settleUntil) {
    const part = p.parts[p.index];
    if (!part || position < part.start - 0.3 || position > part.end + 0.3) {
      const found = p.parts.findIndex((x) => position >= x.start - 0.05 && position < x.end);
      if (found >= 0) p.index = found;
      else if (xp.playing()) {
        // Outside every part (seeked by the player itself): go to the next one, or stop
        const next = p.parts.findIndex((x) => x.start > position);
        if (next >= 0) jumpTo(next);
        else finish();
      }
    } else if (xp.playing() && position >= part.end - 0.04) {
      if (p.index + 1 < p.parts.length) jumpTo(p.index + 1);
      else finish();
    }
  }
  renderPreviewProgress(position);
}

function jumpTo(index) {
  preview.index = index;
  preview.settleUntil = performance.now() + 400;
  xp.seek(preview.parts[index].start);
}

function finish() {
  xp.pause();
  preview.ended = true;
}

function elapsedAt(position) {
  const p = preview;
  const part = p.parts[p.index];
  if (!part) return 0;
  return totalOf(p.parts.slice(0, p.index)) + Math.min(Math.max(position - part.start, 0), part.end - part.start);
}

function previewSeek(elapsed) {
  const p = preview;
  if (!p || !xp.ready) return;
  let left = Math.max(0, elapsed);
  for (let i = 0; i < p.parts.length; i++) {
    const length = p.parts[i].end - p.parts[i].start;
    if (left < length || i === p.parts.length - 1) {
      p.ended = false;
      p.index = i;
      p.settleUntil = performance.now() + 400;
      xp.seek(p.parts[i].start + Math.min(left, length));
      return;
    }
    left -= length;
  }
}

function previewPlayPause() {
  if (!preview || !xp.ready) return;
  if (xp.playing()) {
    xp.pause();
    return;
  }
  const position = xp.position();
  if (preview.ended || position == null || !preview.parts.some((x) => position >= x.start - 0.05 && position < x.end - 0.05)) previewSeek(0);
  preview.ended = false;
  xp.play();
}

function renderPreviewProgress(position) {
  if (!preview) return;
  const total = totalOf(preview.parts);
  const elapsed = elapsedAt(position);
  $("xp-progress-fill").style.width = `${total ? Math.min(100, (elapsed / total) * 100) : 0}%`;
  $("xp-clock").hidden = false;
  $("xp-clock").textContent = `${formatDuration(elapsed)} / ${formatDuration(total)}`;
}

function renderPreviewButtons() {
  const ready = Boolean(preview) && xp.ready;
  for (const id of ["xp-play", "xp-restart", "xp-full"]) $(id).disabled = !ready;
  const app = ready && xp.kind === "app";
  $("xp-mute").disabled = !app;
  $("xp-volume").disabled = !app;
  const playing = ready && xp.playing();
  $("xp-play").name = playing ? "pause" : "play";
  $("xp-play").label = playing ? "Pause" : "Play";
  const volume = xp.volume;
  if (volume) {
    $("xp-volume").value = Math.round(volume.volume * 100);
    $("xp-mute").name = volume.muted || volume.volume === 0 ? "speaker-slash" : volume.volume < 0.5 ? "speaker-low" : "speaker-high";
  }
  if (!preview) {
    $("xp-clock").hidden = true;
    $("xp-progress-fill").style.width = "0";
  }
}

// ---------- a VOD range (pop-up) ----------

// vod = { vodId, title, lengthSeconds, date, thumb }; existing = the list item being changed
function openVodDialog(vod, existing = null) {
  if (demo) {
    toast("Playback isn't available in the demo.");
    return;
  }
  pausePreview();
  for (const other of otherPlayers()) if (other?.playing()) other.pause();
  const length = vod.lengthSeconds ?? Infinity;
  const media = el("div", { class: "player-media" });
  const status = el("p", { class: "player-note" }, "Loading…");
  const clock = el("span", { class: "num vd-clock" }, "");
  let player = null;
  const title = el("sl-input", { label: "Title (file name)", size: "small", value: existing?.fileName || vod.title || "", maxlength: "200" });
  const startBox = timeBox({ seconds: existing?.start ?? 0, fraction: true, label: "Start", max: length });
  const endBox = timeBox({ seconds: existing?.end ?? Math.min(60, Number.isFinite(length) ? length : 60), fraction: true, label: "End", max: length });
  const here = (box, label) => iconButton({
    icon: "hand-pointing",
    label,
    onClick: () => {
      const position = player?.ready ? player.position() : null;
      if (position != null) box.seconds = Math.round(position * 100) / 100;
    },
  });
  const playButton = iconButton({ icon: "play", label: "Play / pause", onClick: () => player?.ready && player.togglePlay() });
  const rangeButton = iconButton({
    icon: "play-circle",
    label: "Play the range",
    onClick: () => {
      const start = startBox.seconds;
      const end = endBox.seconds;
      if (player?.ready && start != null && end != null && end > start) player.playRange(start, end);
    },
  });
  const back = iconButton({ icon: "arrow-counter-clockwise", label: "Back 10 s", onClick: () => player?.ready && player.skip(-10) });
  const fwd = iconButton({ icon: "arrow-clockwise", label: "Forward 10 s", onClick: () => player?.ready && player.skip(10) });
  const add = el("sl-button", { slot: "footer", variant: "primary" }, existing ? "Save" : "Add to the list");
  const cancel = el("sl-button", { slot: "footer" }, "Cancel");
  const box = el("sl-dialog", { label: `${existing ? "Change" : "Download part of"} "${vod.title ?? "VOD"}"`, class: "vod-dialog" },
    el("div", { class: "player-screen vd-screen" }, media, status),
    el("div", { class: "player-controls vd-controls" }, playButton, back, fwd, rangeButton, el("span", { class: "controls-gap" }), clock),
    el("div", { class: "vd-fields" },
      title,
      el("label", { class: "vd-time" }, el("span", { class: "xs-label" }, "Start"), startBox, here(startBox, "Start at the playhead")),
      el("label", { class: "vd-time" }, el("span", { class: "xs-label" }, "End"), endBox, here(endBox, "End at the playhead"))),
    el("p", { class: "faint xs-note" }, "A one-off download: it isn't saved in the library."),
    cancel, add);

  add.addEventListener("click", () => {
    const start = startBox.seconds;
    const end = endBox.seconds;
    if (start == null || end == null || end - start < MIN_RANGE) {
      toast("The end has to be at least half a second after the start.", "error");
      return;
    }
    if (end > length + 1) {
      toast("That goes past the end of the VOD.", "error");
      return;
    }
    const fileName = title.value.trim();
    if (existing) {
      Object.assign(existing, { start, end, length: end - start, fileName: fileName === vod.title ? "" : fileName });
      changed(existing, { settings: true, preview: true });
    } else {
      const item = {
        key: `vod:${vod.vodId}:${Date.now().toString(36)}`, kind: "vod", vodId: vod.vodId, title: vod.title ?? `VOD ${vod.vodId}`,
        vodTitle: vod.title, vodDate: vod.date, vodLength: Number.isFinite(length) ? length : null, thumb: vod.thumb,
        start, end, cuts: [], length: end - start, fileName: fileName === vod.title ? "" : fileName, quality: "",
      };
      list.push(item);
      save();
      renderList();
      select(item.key);
    }
    box.hide();
  });
  cancel.addEventListener("click", () => box.hide());
  box.addEventListener("sl-after-hide", (event) => {
    if (event.target !== box) return;
    player?.close();
    box.remove();
  });
  document.body.append(box);
  customElements.whenDefined("sl-dialog").then(() => box.show());
  // The player starts at once: the dialog's media box is in the page already, and waiting for the open animation isn't needed
  player = new MediaPlayer(media, {
    onTick: (position) => { clock.textContent = formatClock(position); },
    onState: () => {
      const icon = playButton.querySelector("sl-icon-button");
      if (icon) icon.name = player?.playing() ? "pause" : "play";
    },
  });
  player.open(vod.vodId, existing?.start ?? 0)
    .then(() => {
      if (!box.isConnected) return;
      player.pause();
      status.hidden = true;
    })
    .catch((error) => { status.textContent = `Couldn't play this VOD: ${error.message}`; });
}

// ---------- exporting ----------

function requestOf(item) {
  const common = { kind: item.kind, fileName: item.fileName || null, quality: item.quality || null };
  if (item.editId) {
    return {
      ...common, kind: "edit", editId: item.editId,
      ...(item.kind === "clip" ? { horizontal: item.horizontal, vertical: item.vertical, renderId: item.vertical ? item.renderId || null : null } : {}),
      ...(item.kind === "video" ? { joined: Boolean(item.joined && me?.combineSegmentDownloads) } : {}),
    };
  }
  if (item.kind === "vod") return { ...common, vodId: Number(item.vodId), start: item.start, end: item.end };
  if (item.kind === "project") return { ...common, projectId: item.id, horizontal: item.horizontal, vertical: item.vertical };
  if (item.kind === "clip") return { ...common, entryId: item.id, horizontal: item.horizontal, vertical: item.vertical, renderId: item.vertical ? item.renderId || null : null };
  if (item.kind === "video") return { ...common, videoId: item.id, joined: Boolean(item.joined && me?.combineSegmentDownloads) };
  return { ...common, markerId: item.id };
}

async function runExport() {
  if (!list.length) return;
  if (demo) {
    toast("Exports aren't made in the demo.");
    return;
  }
  const button = $("export-button");
  button.loading = true;
  const single = list.length === 1;
  try {
    const batch = await api("POST", "/api/exports", { items: list.map(requestOf) });
    if (single) autoDownload.add(batch.id);
    toast(single ? "Exporting… the file downloads when it's ready." : `Queued ${batch.itemsTotal} file${batch.itemsTotal === 1 ? "" : "s"}. They show up under Your exports.`);
    list = [];
    selectedKey = null;
    closePreview();
    save();
    renderList();
    renderSettings();
    batches = [batch, ...batches.filter((b) => b.id !== batch.id)];
    renderBatches();
    schedulePoll();
  } catch (error) {
    markErrors(error);
    toast(error.message, "error");
  } finally {
    button.loading = false;
  }
}

// A refused export names the item: "items[2]" -> the third row
function markErrors(error) {
  const errors = error.problem?.errors;
  if (!errors) return;
  for (const [key, messages] of Object.entries(errors)) {
    const match = /^items\[(\d+)\]$/.exec(key);
    if (match && list[Number(match[1])]) list[Number(match[1])].error = [].concat(messages).join(" ");
  }
  renderList();
}

// ---------- your exports ----------

async function refreshBatches() {
  try {
    batches = await api("GET", "/api/exports");
  } catch {
    return;
  }
  renderBatches();
  for (const batch of batches) {
    if (!autoDownload.has(batch.id) || batch.status === "Queued" || batch.status === "Running") continue;
    autoDownload.delete(batch.id);
    const ready = batch.items.filter((i) => i.fileUrl);
    for (const item of ready) download(item.fileUrl);
    if (ready.length < batch.items.length) toast(`Some files couldn't be made: ${batch.items.filter((i) => !i.fileUrl).map((i) => i.error || i.fileName).join("; ")}`, "error");
  }
  schedulePoll();
}

function schedulePoll() {
  clearTimeout(pollTimer);
  if (batches.some((b) => b.status === "Queued" || b.status === "Running")) pollTimer = setTimeout(refreshBatches, POLL_MS);
}

function download(url) {
  const link = el("a", { href: url, download: "" });
  document.body.append(link);
  link.click();
  link.remove();
}

function renderBatches() {
  const box = $("export-batches");
  if (!batches.length) {
    box.replaceChildren(el("p", { class: "faint export-empty-note" }, "Exports you start show up here with their progress. Files are kept for a few days."));
    return;
  }
  box.replaceChildren(...batches.map(batchNode));
}

const STATUS_TEXT = { Queued: "Waiting", Running: "Making files", Ready: "Ready", Failed: "Failed" };

function batchNode(batch) {
  const done = batch.items.filter((i) => i.status === "Ready").length;
  return el("div", { class: `xb xb-${batch.status.toLowerCase()}` },
    el("div", { class: "xb-head" },
      el("span", { class: "xb-status" }, STATUS_TEXT[batch.status] ?? batch.status),
      el("span", { class: "faint" }, `${formatDate(batch.createdAtUtc)} · ${done} of ${batch.itemsTotal} ready`),
      el("span", { class: "controls-gap" }),
      batch.canDelete ? iconButton({ icon: "trash", label: "Delete these files", onClick: () => deleteBatch(batch) }) : null),
    batch.error ? el("div", { class: "xl-error" }, batch.error) : null,
    ...batch.items.map(batchItemNode),
    batch.expiresAtUtc ? el("div", { class: "faint xb-expires" }, `Files are deleted ${formatDate(batch.expiresAtUtc)}.`) : null);
}

function batchItemNode(item) {
  const name = item.kind === "Video" && !item.joined ? `${item.fileName}.zip` : item.kind === "ClipVertical" || item.kind === "ProjectVertical" ? `${item.fileName} (vertical).mp4` : `${item.fileName}.mp4`;
  const progress = item.status === "Running" && item.partsTotal > 1
    ? el("sl-progress-bar", { value: String(Math.round((item.partsDone / item.partsTotal) * 100)) })
    : item.status === "Running" ? el("sl-progress-bar", { indeterminate: true }) : null;
  return el("div", { class: "xb-item" },
    el("sl-icon", { library: "ph", name: ITEM_KIND_ICON[item.kind] ?? "file" }),
    el("div", { class: "xb-main" },
      el("div", { class: "xb-name", title: name }, name),
      el("div", { class: "faint xb-meta num" }, [
        formatDuration(item.lengthSeconds ?? 0, { short: false }),
        item.quality,
        item.status === "Ready" && item.fileSizeBytes ? formatSize(item.fileSizeBytes) : STATUS_TEXT[item.status],
      ].filter(Boolean).join(" · ")),
      progress,
      item.error ? el("div", { class: "xl-error" }, item.error) : null),
    item.fileUrl ? iconButton({ icon: "download-simple", label: "Download", onClick: () => download(item.fileUrl) }) : null);
}

function formatSize(bytes) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`;
}

async function deleteBatch(batch) {
  const ok = await confirmAction({ heading: "Delete these files?", text: "The export's files are deleted from the server. The clips, videos and segments stay.", confirm: "Delete" });
  if (!ok) return;
  try {
    await api("DELETE", `/api/exports/${batch.id}`);
    batches = batches.filter((b) => b.id !== batch.id);
    renderBatches();
  } catch (error) {
    toast(error.message, "error");
  }
}
