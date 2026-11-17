// The left panel (frontend rework step 2): VODS / VIDEOS / CLIPS lists with simple cards, a filter popover, a right-click
// menu and the export icon. The same panel is used on the Editor and Export screens.
//
// VODs come only from the channels the app Owner assigned to the team (GET /api/channels, then /api/channels/{login}/vods); a VOD
// with work on it (badrfly 2026-11-17: "the VOD is the project") shows what's on it (GET /api/vods/work). Clips are the team's library
// entries (GET /api/entries); videos are segments linked on one row (GET /api/videos). Both filter by VOD and by member: made by, or
// edited by (made an edit of it in Player 2).

import { $, el, api, me, can, formatDate, formatDuration, loadSetting, saveSetting, toast, iconButton, withTooltip, twitchThumb, thumbStyle } from "./core.js";
import { draggable } from "./drag.js";
import { dayRange, todayIn, viewerTimeZone } from "./dates.js";
import { askText, confirmAction } from "./dialogs.js";
import { openMenu } from "./menu.js";

const POLL_MS = 3000;
const NEAR_BOTTOM_PX = 300;

const state = {
  list: "vods",
  channels: [],
  channel: loadSetting("clipco.library.channel", null),
  vodType: loadSetting("clipco.library.vodType", "archive"),
  vods: [],
  vodCursor: null,
  vodsLoading: false,
  vodsError: "",
  vodSearch: "",
  clips: [],
  clipsLoaded: false,
  clipsError: "",
  clipFilter: { search: "", sort: loadSetting("clipco.library.sort", "dateAdded"), order: "desc", last24h: false, day: "" },
  videos: [],
  videosLoaded: false,
  videosError: "",
  // VODs: what the team made on each (vodId → GET /api/vods/work row) and the "only with work" switch
  work: new Map(),
  onlyWorked: false,
  // Clips and videos: one VOD and / or one member (made by, or edited by)
  who: { vodId: "", memberId: "", mode: "made" },
  members: new Map(),   // userId → name, from what's been loaded
  workedVods: [],       // GET /api/vods/work, for the VOD filter
};

let handlers = { onLoad: () => {}, onExport: () => {} };
let pollTimer = null;
let searchTimer = null;

export function setUpLibrary(options) {
  handlers = { ...handlers, ...options };
  $("side-list").addEventListener("scroll", () => {
    const list = $("side-list");
    if (state.list === "vods" && list.scrollTop + list.clientHeight > list.scrollHeight - NEAR_BOTTOM_PX) loadMoreVods();
  });
  $("side-channel").addEventListener("sl-change", (event) => pickChannel(event.target.value));
  setUpFilter();
  if (options.sample) showSample(options.sample);
  else loadChannels();
}

// Demo mode (?demo): sample data instead of the server (filters that call the server do nothing useful there)
function showSample(sample) {
  state.channels = sample.channels;
  state.channel = sample.channels[0].login;
  state.vods = sample.vods;
  state.clips = sample.clips;
  state.clipsLoaded = true;
  state.videos = sample.videos;
  state.videosLoaded = true;
  renderChannelPicker();
  render();
}

export function showList(list) {
  state.list = list;
  renderChannelPicker();
  renderFilter();
  render();
  if (list === "clips" && !state.clipsLoaded) loadClips();
  if (list === "videos" && !state.videosLoaded) loadVideos();
}

// A VOD from the loaded lists (Player 2's tree uses its title)
export function knownVod(vodId) {
  return state.vods.find((v) => Number(v.vodId) === Number(vodId)) ?? null;
}

export function knownVodTitle(vodId) {
  return knownVod(vodId)?.title ?? null;
}

// Clips / videos of one VOD (card menus, Player 2's tree)
export function showVodWork(vodId, list = "clips") {
  state.who.vodId = String(vodId);
  if (handlers.onPickList) handlers.onPickList(list);
  else showList(list);
  reloadWho();
}

// Something was made, renamed or deleted elsewhere: counts and lists follow
export function refreshLibraryFilters() {
  loadWorkFor(state.vods.map((v) => v.vodId));
  if (state.clipsLoaded) loadClips();
  if (state.videosLoaded) loadVideos();
}

// ---------- loading ----------

async function loadChannels() {
  try {
    state.channels = await api("GET", "/api/channels");
  } catch (error) {
    state.vodsError = error.message;
    render();
    return;
  }
  if (!state.channels.some((c) => c.login === state.channel)) state.channel = state.channels[0]?.login ?? null;
  renderChannelPicker();
  await reloadVods();
}

function pickChannel(login) {
  if (!login || login === state.channel) return;
  state.channel = login;
  saveSetting("clipco.library.channel", login);
  reloadVods();
}

async function reloadVods() {
  state.vods = [];
  state.vodCursor = null;
  state.vodsError = "";
  render();
  if (state.channel) await loadMoreVods(true);
}

async function loadMoreVods(first = false) {
  if (state.vodsLoading || (!first && !state.vodCursor) || !state.channel) return;
  state.vodsLoading = true;
  const login = state.channel;
  const type = state.vodType;
  const cursor = first ? "" : `&cursor=${encodeURIComponent(state.vodCursor)}`;
  try {
    const page = await api("GET", `/api/channels/${encodeURIComponent(login)}/vods?type=${type}&limit=20${cursor}`);
    if (login !== state.channel || type !== state.vodType) return; // the user switched while this loaded
    state.vods.push(...page.items);
    state.vodCursor = page.nextCursor;
    if (first) checkLive(login);
    loadWorkFor(page.items.map((v) => v.vodId));
  } catch (error) {
    state.vodsError = error.message;
  } finally {
    state.vodsLoading = false;
  }
  if (state.list === "vods") render();
}

// Twitch's VOD lists don't say whether a stream is still going, so the newest VOD (if it started in the last 2 days) is
// looked up on its own (GET /api/vods/info → isLive)
async function checkLive(login) {
  const newest = state.vods[0];
  if (!newest || Date.now() - new Date(newest.createdAtUtc).getTime() > 2 * 86400_000) return;
  try {
    const info = await api("GET", `/api/vods/info?vod=${newest.vodId}`);
    if (login !== state.channel || !info.isLive) return;
    newest.isLive = true;
    if (state.list === "vods") render();
  } catch {
    // Not knowing is fine: the card just shows no LIVE badge
  }
}

// What the team made on these VODs (badges on their cards)
async function loadWorkFor(vodIds) {
  const ids = [...new Set(vodIds.map(String))].filter(Boolean);
  for (let i = 0; i < ids.length; i += 200) {
    const part = ids.slice(i, i + 200);
    try {
      const rows = await api("GET", `/api/vods/work?vodIds=${part.join(",")}`);
      for (const id of part) state.work.delete(id);
      for (const row of rows) state.work.set(String(row.vodId), row);
    } catch {
      // Cards just show no badge
    }
  }
  if (state.list === "vods") render();
}

function rememberMembers(pairs) {
  for (const [id, name] of pairs) if (id != null && name) state.members.set(String(id), name);
}

// The VOD and member filters on the query of clips (entries) or videos
function whoQuery(query, list) {
  const who = state.who;
  if (who.vodId) query.set(list === "clips" ? "vodId" : "vodIds", who.vodId);
  if (who.memberId) {
    if (who.mode === "edited") query.set("editedBy", who.memberId);
    else query.set(list === "clips" ? "madeBy" : "userId", who.memberId);
  }
}

function reloadWho() {
  renderFilter();
  if (state.list === "videos" || state.videosLoaded) loadVideos();
  if (state.list === "clips" || state.clipsLoaded) loadClips();
}

async function loadClips() {
  const f = state.clipFilter;
  const query = new URLSearchParams({ sort: f.sort, order: f.order, limit: "500" });
  if (f.search) query.set("search", f.search);
  whoQuery(query, "clips");
  const dateField = f.sort;
  if (f.last24h) {
    query.set("from", new Date(Date.now() - 86400_000).toISOString());
    query.set("dateField", dateField);
  } else if (f.day) {
    const [year, month, day] = f.day.split("-").map(Number);
    const range = dayRange({ year, month, day }, viewerTimeZone());
    query.set("from", range.from);
    query.set("to", range.to);
    query.set("dateField", dateField);
  }
  try {
    state.clips = await api("GET", `/api/entries?${query}`);
    rememberMembers(state.clips.map((c) => [c.addedByUserId, c.addedBy]));
    state.clipsError = "";
  } catch (error) {
    state.clipsError = error.message;
  }
  state.clipsLoaded = true;
  if (state.list === "clips") render();
  schedulePoll();
}

function schedulePoll() {
  clearTimeout(pollTimer);
  const busy = state.clips.some((c) => ["Queued", "Downloading"].includes(c.status) || ["Queued", "Generating"].includes(c.generationStatus));
  if (busy) pollTimer = setTimeout(loadClips, POLL_MS);
}

// The team's videos (segments linked on one row), newest first
async function loadVideos() {
  try {
    const query = new URLSearchParams({ limit: "500" });
    whoQuery(query, "videos");
    state.videos = await api("GET", `/api/videos?${query}`);
    rememberMembers(state.videos.map((v) => [v.userId, v.createdBy]));
    state.videosError = "";
  } catch (error) {
    state.videosError = error.message;
  }
  state.videosLoaded = true;
  if (state.list === "videos") render();
}

export function refreshVideos() {
  return loadVideos();
}

export function refreshClips() {
  return loadClips();
}

// ---------- rendering ----------

const EMPTY = {
  vods: () => state.vodsError || (state.channels.length
    ? (state.vodsLoading ? "Loading VODs…" : "No VODs here yet.")
    : "Your team has no channels yet. The app owner assigns them."),
  videos: () => state.videosError || (state.videosLoaded
    ? (filtersOn() ? "No videos match the filter." : "No videos yet. Link segments on a VOD's timeline to make one.")
    : "Loading videos…"),
  clips: () => state.clipsError || (state.clipsLoaded ? (filtersOn() ? "No clips match the filter." : "No clips yet.") : "Loading clips…"),
};

function visibleItems() {
  if (state.list === "vods") {
    const search = state.vodSearch.toLowerCase();
    return state.vods.filter((v) => (!search || v.title.toLowerCase().includes(search)) && (!state.onlyWorked || state.work.has(String(v.vodId))));
  }
  if (state.list === "videos" && state.clipFilter.search) {
    const search = state.clipFilter.search.toLowerCase();
    return state.videos.filter((v) => v.title.toLowerCase().includes(search));
  }
  return state.list === "clips" ? state.clips : state.videos;
}

function render() {
  const items = visibleItems();
  const noun = { vods: "VOD", videos: "video", clips: "clip" }[state.list];
  const more = state.list === "vods" && state.vodCursor ? "+" : "";
  $("side-count").textContent = items.length ? `${items.length}${more} ${noun}${items.length === 1 && !more ? "" : "s"}` : "";
  $("side-filter").toggleAttribute("data-active", filtersOn());
  if (!items.length) {
    $("side-list").replaceChildren(el("p", { class: "side-empty" }, EMPTY[state.list]()));
    return;
  }
  const cards = items.map((item) => (state.list === "vods" ? vodCard(item) : state.list === "clips" ? clipCard(item) : videoCard(item)));
  if (state.list === "vods" && state.vodsLoading) cards.push(el("p", { class: "side-empty" }, "Loading more…"));
  $("side-list").replaceChildren(...cards);
}

function renderChannelPicker() {
  const picker = $("side-channel");
  const show = state.list === "vods" && state.channels.length > 1;
  picker.hidden = !show;
  if (!show) return;
  picker.replaceChildren(...state.channels.map((c) => el("sl-option", { value: c.login }, c.displayName || c.login)));
  picker.value = state.channel ?? "";
}

// A card: thumbnail with badges, title, meta lines, and icon actions. item.kind decides where it can be dropped.
// thumb may be a list (first on top: see thumbStyle).
function card({ kind, title, thumb, badges = [], meta = [], actions = [], dragData, menu }) {
  const node = el("div", { class: "card", role: "listitem", tabindex: "0", "aria-label": title },
    el("div", { class: "card-thumb", style: thumbStyle(thumb) }, ...badges),
    el("div", { class: "card-body" },
      el("div", { class: "card-title", title }, title),
      ...meta.filter(Boolean).map((line) => (line instanceof Node ? line : el("div", { class: "card-meta" }, line))),
      el("div", { class: "card-actions" }, ...actions)));
  const item = { kind, label: title, data: dragData };
  draggable(node, () => item);
  node.addEventListener("dblclick", () => handlers.onLoad(item));
  node.addEventListener("keydown", (event) => {
    if (event.target !== node) return;
    if (event.key === "Enter") handlers.onLoad(item);
    if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
      event.preventDefault();
      const rect = node.getBoundingClientRect();
      openMenu(rect.left + 24, rect.top + 24, menu());
    }
  });
  node.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    openMenu(event.clientX, event.clientY, menu());
  });
  return node;
}

function exportButton(item) {
  return iconButton({ icon: "download-simple", label: "Export", onClick: () => handlers.onExport(item) });
}

// --- VODs: only what Twitch says about the stream ---

const RESCUE_TEXT = {
  Grabbing: "Saving audio while Twitch still has clean copies of muted parts",
  WaitingToPrune: "Full audio saved; only the muted parts are kept after a couple of days",
  Pruned: "Clean audio for muted parts is saved",
  Failed: "Saving the audio failed",
};

function vodCard(vod) {
  const thumb = twitchThumb(vod.thumbnailUrl);
  const badges = [
    vod.isLive ? el("span", { class: "card-badge live" }, "LIVE") : null,
    !vod.isLive && vod.length ? el("span", { class: "card-badge num" }, vod.length) : null,
  ];
  const actions = [];
  // Saving audio: live or ended < 24 h ago and not saving yet, or a failed try (same rule as the old page)
  const canStart = !vod.rescueStatus || (vod.rescuePaused && vod.rescueStatus === "Grabbing");
  if (can("SaveAudio") && ((vod.unmuteWindowOpen || vod.isLive) && canStart || vod.rescueStatus === "Failed")) {
    actions.push(iconButton({
      icon: "microphone",
      label: vod.rescueStatus === "Failed" ? "Retry saving audio" : "Save audio now",
      onClick: () => startRescue(vod),
    }));
  } else if (vod.rescueStatus && RESCUE_TEXT[vod.rescueStatus]) {
    const icon = el("sl-icon", { library: "ph", name: vod.rescueStatus === "Failed" ? "warning" : "waveform", class: "card-status-icon" });
    actions.push(withTooltip(icon, vod.rescuePaused ? "Audio saving paused" : RESCUE_TEXT[vod.rescueStatus]));
  }
  actions.push(exportButton({ kind: "vod", data: vod }));
  const item = { ...vod, kind: "vod", channel: state.channel };
  const work = state.work.get(String(vod.vodId));
  return card({
    kind: "vod",
    title: vod.title,
    thumb,
    badges,
    meta: [[formatDate(vod.createdAtUtc, false), vod.game].filter(Boolean).join(" · "), work ? workLine(work) : null],
    actions,
    dragData: item,
    menu: () => [
      { label: "Load in Player 1", icon: "broadcast", run: () => handlers.onLoad({ kind: "vod", data: item }) },
      { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "vod", data: item }) },
      work?.clips && { label: "Show its clips", icon: "scissors", run: () => showVodWork(vod.vodId, "clips") },
      work?.videos && { label: "Show its videos", icon: "film-strip", run: () => showVodWork(vod.vodId, "videos") },
    ].filter(Boolean),
  });
}

// "3 clips · 1 video · 2 edits" on a VOD card (segments and moments when there's nothing else)
function workLine(work) {
  const n = (count, word) => (count ? `${count} ${word}${count === 1 ? "" : "s"}` : null);
  const made = [n(work.clips, "clip"), n(work.videos, "video"), n(work.edits, "edit")].filter(Boolean);
  const marks = [n(work.segments, "segment"), n(work.moments, "moment")].filter(Boolean);
  const text = (made.length ? made : marks).join(" · ");
  return el("div", { class: "card-meta card-work", title: [...made, ...marks].join(" · ") },
    el("sl-icon", { library: "ph", name: "stack" }), text);
}

async function startRescue(vod) {
  try {
    const rescue = await api("POST", `/api/vods/${vod.vodId}/rescue`);
    vod.rescueStatus = rescue.status;
    vod.rescuePaused = false;
    toast(`Saving the audio of "${vod.title}" in the background.`);
  } catch (error) {
    toast(error.message, "error");
  }
  render();
}

// --- clips: title, length, the VOD's date, who made it ---

function clipStatus(entry) {
  if (entry.status === "Queued") return "Waiting to download";
  if (entry.status === "Downloading") return "Downloading…";
  if (entry.status === "Failed") return el("div", { class: "card-meta error", title: entry.error || "" }, "Download failed");
  if (entry.status === "VodUnavailable") return el("div", { class: "card-meta error" }, entry.kind === "Clip" ? "Clip deleted" : "VOD deleted");
  return null;
}

function clipCard(entry) {
  const badges = [
    el("span", { class: "card-badge num" }, formatDuration(entry.clipLengthSeconds)),
    entry.kind === "Clip" ? el("span", { class: "card-badge tag" }, "Twitch") : null,
    entry.kind !== "Clip" && entry.isReferenceOnly ? el("span", { class: "card-badge tag", title: "Longer than the stored-clip limit: not stored, made on demand" }, "Long") : null,
  ];
  const item = entry; // keeps entry.kind (Vod / Clip): the card's own kind is "clip"
  return card({
    kind: "clip",
    title: entry.title,
    // A frame from the middle of the clip (Twitch clips: Twitch's own thumbnail)
    thumb: [entry.frameUrl, twitchThumb(entry.thumbnailUrl)],
    badges,
    meta: [
      `VOD ${formatDate(entry.streamDateUtc, false)}`,
      entry.addedBy ? `by ${entry.addedBy}` : null,
      clipStatus(entry),
    ],
    actions: [exportButton({ kind: "clip", data: item })],
    dragData: item,
    menu: () => clipMenu(item),
  });
}

function canOnEntry(entry, own, any) {
  return can(any) || (can(own) && entry.addedByUserId === String(me.id));
}

function clipMenu(entry) {
  return [
    { label: "Load in Player 2", icon: "film-strip", run: () => handlers.onLoad({ kind: "clip", data: entry }) },
    { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "clip", data: entry }) },
    entry.vodId && String(entry.vodId) !== state.who.vodId && { label: "Show this VOD's clips", icon: "funnel-simple", run: () => showVodWork(entry.vodId, "clips") },
    canOnEntry(entry, "EditOwnEntries", "EditAnyEntries") && { label: "Rename", icon: "pencil-simple", run: () => renameClip(entry) },
    canOnEntry(entry, "DeleteOwnEntries", "DeleteAnyEntries") && { label: "Delete", icon: "trash", danger: true, run: () => deleteClip(entry) },
  ].filter(Boolean);
}

export async function renameClip(entry) {
  const title = await askText({ heading: "Rename clip", label: "Title", value: entry.title, confirm: "Rename", maxLength: 200 });
  if (title === null || title.trim() === entry.title) return;
  try {
    await api("PATCH", `/api/entries/${entry.id}`, { title: title.trim() });
    await loadClips();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function deleteClip(entry) {
  const ok = await confirmAction({
    heading: "Delete clip?",
    text: `"${entry.title}" and its files are deleted for the whole team. This can't be undone.`,
    confirm: "Delete",
  });
  if (!ok) return;
  try {
    await api("DELETE", `/api/entries/${entry.id}`);
    toast(`Deleted "${entry.title}".`);
    await loadClips();
  } catch (error) {
    toast(error.message, "error");
  }
}

// --- videos: segments linked on one row (length, the VOD's date, who made it) ---

function videoCard(video) {
  const item = video;
  return card({
    kind: "video",
    title: video.title,
    thumb: [video.frameUrl, twitchThumb(video.thumbnailUrl)],
    badges: [el("span", { class: "card-badge num" }, formatDuration(video.lengthSeconds ?? 0))],
    meta: [video.streamDateUtc ? `VOD ${formatDate(video.streamDateUtc, false)}` : null, video.createdBy ? `by ${video.createdBy}` : null],
    actions: [exportButton({ kind: "video", data: item })],
    dragData: item,
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: () => handlers.onLoad({ kind: "video", data: item }) },
      { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "video", data: item }) },
      String(video.vodId) !== state.who.vodId && { label: "Show this VOD's videos", icon: "funnel-simple", run: () => showVodWork(video.vodId, "videos") },
      video.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameVideo(item) },
      video.canDelete && { label: "Delete", icon: "trash", danger: true, run: () => deleteVideo(item) },
    ].filter(Boolean),
  });
}

export async function renameVideo(video) {
  const title = await askText({ heading: "Rename video", label: "Title", value: video.title, confirm: "Rename", maxLength: 200 });
  if (title === null || title.trim() === video.title) return;
  try {
    await api("PUT", `/api/videos/${video.id}`, { title: title.trim() });
    await loadVideos();
  } catch (error) {
    toast(error.message, "error");
  }
}

export async function deleteVideo(video) {
  const ok = await confirmAction({
    heading: "Delete video?",
    text: `"${video.title}" is deleted for the whole team. Its segments stay on the timeline.`,
    confirm: "Delete",
  });
  if (!ok) return;
  try {
    await api("DELETE", `/api/videos/${video.id}`);
    toast(`Deleted "${video.title}".`);
    await loadVideos();
  } catch (error) {
    toast(error.message, "error");
  }
}

// ---------- filter popover ----------

function filtersOn() {
  if (state.list === "vods") return Boolean(state.vodSearch) || state.vodType !== "archive" || state.onlyWorked;
  const who = Boolean(state.who.vodId || state.who.memberId);
  if (state.list === "clips") {
    const f = state.clipFilter;
    return Boolean(f.search || f.last24h || f.day) || who;
  }
  return who || Boolean(state.clipFilter.search);
}

// The filter popover opened: the VODs with work and the members known so far, for the pickers
async function loadFilterOptions() {
  try {
    const [worked, edits] = await Promise.all([api("GET", "/api/vods/work?limit=200"), api("GET", "/api/projects?limit=1000")]);
    state.workedVods = worked;
    rememberMembers(edits.map((e) => [e.userId, e.createdBy]));
  } catch {
    // The pickers keep what they had
  }
  renderFilter();
}

function vodOptionLabel(work) {
  const title = knownVodTitle(work.vodId);
  const date = work.streamDateUtc ? formatDate(work.streamDateUtc, false) : null;
  return title ? `${title}${date ? ` (${date})` : ""}` : [date, work.channelName].filter(Boolean).join(" · ") || `VOD ${work.vodId}`;
}

function setUpFilter() {
  $("filter-search").addEventListener("sl-input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      const value = $("filter-search").value.trim();
      if (state.list === "vods") {
        state.vodSearch = value;
        render();
      } else if (state.list === "videos") {
        state.clipFilter.search = value;
        render();
      } else {
        state.clipFilter.search = value;
        loadClips();
      }
    }, 250);
  });
  $("filter-menu").addEventListener("sl-show", (event) => {
    if (event.target === $("filter-menu") && state.list !== "vods") loadFilterOptions();
  });
  $("filter-work").addEventListener("sl-change", (event) => {
    state.onlyWorked = event.target.checked;
    render();
  });
  $("filter-vod").addEventListener("sl-change", (event) => {
    state.who.vodId = event.target.value || "";
    reloadWho();
  });
  $("filter-member").addEventListener("sl-change", (event) => {
    state.who.memberId = event.target.value || "";
    reloadWho();
  });
  $("filter-member-mode").addEventListener("sl-change", (event) => {
    state.who.mode = event.target.value;
    if (state.who.memberId) reloadWho();
  });
  $("filter-type").addEventListener("sl-change", (event) => {
    state.vodType = event.target.value;
    saveSetting("clipco.library.vodType", state.vodType);
    reloadVods();
  });
  $("filter-sort").addEventListener("sl-change", (event) => {
    state.clipFilter.sort = event.target.value;
    saveSetting("clipco.library.sort", state.clipFilter.sort);
    loadClips();
  });
  $("filter-order").addEventListener("click", () => {
    state.clipFilter.order = state.clipFilter.order === "desc" ? "asc" : "desc";
    renderFilter();
    loadClips();
  });
  $("filter-24h").addEventListener("sl-change", (event) => {
    state.clipFilter.last24h = event.target.checked;
    if (state.clipFilter.last24h) state.clipFilter.day = "";
    renderFilter();
    loadClips();
  });
  $("filter-day").addEventListener("sl-change", (event) => {
    state.clipFilter.day = event.target.value;
    if (state.clipFilter.day) state.clipFilter.last24h = false;
    renderFilter();
    loadClips();
  });
  $("filter-today").addEventListener("click", () => {
    state.clipFilter.day = todayIn(viewerTimeZone());
    state.clipFilter.last24h = false;
    renderFilter();
    loadClips();
  });
  $("filter-clear").addEventListener("click", () => {
    if (state.list === "vods") {
      state.vodSearch = "";
      state.onlyWorked = false;
      if (state.vodType !== "archive") {
        state.vodType = "archive";
        saveSetting("clipco.library.vodType", "archive");
        reloadVods();
      }
    } else {
      Object.assign(state.clipFilter, { search: "", last24h: false, day: "" });
      Object.assign(state.who, { vodId: "", memberId: "" });
      loadClips();
      if (state.videosLoaded) loadVideos();
    }
    renderFilter();
    render();
  });
  renderFilter();
}

function renderFilter() {
  const vods = state.list === "vods";
  const clips = state.list === "clips";
  const videos = state.list === "videos";
  $("side-filter").disabled = false;
  $("filter-search").value = vods ? state.vodSearch : state.clipFilter.search;
  $("filter-search").placeholder = vods ? "Search loaded VODs" : "Search titles";
  $("filter-type-row").hidden = !vods;
  $("filter-work-row").hidden = !vods;
  $("filter-work").checked = state.onlyWorked;
  for (const id of ["filter-vod-row", "filter-member-row"]) $(id).hidden = !(clips || videos);
  // VODs: the ones with work, plus the one picked (it may have none left)
  const vodChoices = [...state.workedVods];
  if (state.who.vodId && !vodChoices.some((w) => String(w.vodId) === state.who.vodId)) vodChoices.unshift({ vodId: state.who.vodId });
  $("filter-vod").replaceChildren(...vodChoices.map((w) => el("sl-option", { value: String(w.vodId) }, vodOptionLabel(w))));
  $("filter-vod").value = state.who.vodId;
  const members = [...state.members].sort((a, b) => a[1].localeCompare(b[1]));
  $("filter-member").replaceChildren(...members.map(([id, name]) => el("sl-option", { value: id }, `${name}${id === String(me?.id) ? " (you)" : ""}`)));
  $("filter-member").value = state.who.memberId;
  $("filter-member-mode").value = state.who.mode;
  $("filter-type").value = state.vodType;
  for (const id of ["filter-sort-row", "filter-date-row"]) $(id).hidden = !clips;
  $("filter-sort").value = state.clipFilter.sort;
  const order = $("filter-order");
  order.name = state.clipFilter.order === "desc" ? "sort-descending" : "sort-ascending";
  order.label = state.clipFilter.order === "desc" ? "Newest first" : "Oldest first";
  order.parentElement.content = order.label;
  $("filter-24h").checked = state.clipFilter.last24h;
  $("filter-day").value = state.clipFilter.day;
}
