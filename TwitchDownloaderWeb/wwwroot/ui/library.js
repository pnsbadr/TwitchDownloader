// The left panel (frontend rework step 2): VODS / VIDEOS / CLIPS lists with simple cards, a filter popover, a right-click
// menu and the export icon. The same panel is used on the Editor and Export screens.
//
// VODs come only from the channels the app Owner assigned to the team (GET /api/channels, then /api/channels/{login}/vods); a VOD
// with work on it (badrfly 2026-11-17: "the VOD is the project") shows what's on it (GET /api/vods/work). Clips are the team's library
// entries (GET /api/entries); videos are segments linked on one row (GET /api/videos). Both filter by VOD and by member: made by, or
// edited by (made an edit of it in Player 2).
//
// The channel bar (badrfly 2026-11-19, option C of the "Channel bar mockups"): each of the team's channels is its Twitch picture
// (GET /api/channels/cards), with a blinking red ring while it's live. Clicking pictures turns channels on and off; the ones off go dark
// (a live one keeps a faint ring), and none on = every channel. It starts on the channel the team is known by (isPrimary). It filters all
// three lists: VODs of several channels are merged newest first, clips and videos are asked for with ?channels=.
//
// Large teams (badrfly 2026-11-20, "Redesign the library for large teams"): clips and videos come a page at a time (?paged=true, a
// cursor per list) as the list scrolls, so nothing is cut off at 500. Every list is shown as slim rows under day headings by default
// (only the rows in view are drawn), with the card of the row under the mouse, else the selected one, at the bottom of the panel;
// the cards view is one click away and remembered per member (clipco.library.view.{userId}). The search box over the lists turns typed
// shortcuts (by:, vod:, @channel, a day, >1m) into chips under it (see "the search box" below).

import { $, el, api, me, can, liveChannels, formatDate, formatDuration, loadSetting, saveSetting, toast, iconButton, withTooltip, twitchThumb, thumbStyle } from "./core.js";
import { draggable, shadePath } from "./drag.js";
import { dayRange, todayIn, viewerTimeZone, zonedParts } from "./dates.js";
import { clipColor, CLIP_COLOR, MARKER_COLORS, STREAM_COLORS } from "./colors.js";
import { askText, confirmAction } from "./dialogs.js";
import { openMenu } from "./menu.js";
import { editMenu, segmentMenu } from "./p2-tree.js";

const POLL_MS = 3000;
const NEAR_BOTTOM_PX = 300;
const PAGE_SIZE = 100;
const ROW_H = 30;        // one row in the rows view (the list is drawn from this, keep editor.css .lrow in step)
const SEP_H = 9;         // the line between days (editor.css .lrow.lsep); months are MONTH_H
const ROW_OVERSCAN = 12; // rows drawn above and below the visible part

const state = {
  list: "vods",
  channels: [],
  cards: new Map(),     // login → { profileImageUrl, live } (GET /api/channels/cards)
  picked: new Set(),    // logins turned on in the channel bar; none = every channel
  demo: false,
  vodType: loadSetting("clipco.library.vodType", "archive"),
  // VODs per channel for the current type: login → { login, items, cursor, first, loading, error }
  sources: new Map(),
  vodsError: "",
  vodSearch: "",
  clips: [],
  clipsLoaded: false,
  clipsError: "",
  clipsCursor: null,    // the next page's cursor (null = all loaded)
  clipsTotal: 0,
  clipsMore: false,     // a next page is loading
  // The search box's words (clips, videos; VODs use vodSearch + vodMatches) and the day / length chips. A day applies to every list.
  clipFilter: { search: "", sort: loadSetting("clipco.library.sort", "dateAdded"), order: "desc", last24h: false, day: "", minSeconds: null, maxSeconds: null },
  vodMatches: null,     // VOD IDs whose work matches the words searched (GET /api/vods/matches), or null
  videos: [],
  videosLoaded: false,
  videosError: "",
  videosCursor: null,
  videosTotal: 0,
  videosMore: false,
  // "rows" (slim lines under day headings) or "cards"
  view: "rows",
  selected: { vods: "", clips: "", videos: "" }, // key of the selected row per list (rows view)
  hovered: "",
  // VODs: what the team made on each (vodId → GET /api/vods/work row) and the "only with work" switch
  work: new Map(),
  onlyWorked: false,
  // Clips and videos: one VOD and / or one member (made by, or edited by)
  who: { vodId: "", memberId: "", mode: "made" },
  members: new Map(),   // userId → name, from what's been loaded
  workedVods: [],       // GET /api/vods/work, for the VOD filter
  // VODs opened to show their videos and clips under them (step 3 of Large libraries): vodId → { clips, videos, clipsTotal, videosTotal, loading, error }
  open: new Set(),
  kids: new Map(),
  subOpen: new Set(),  // "clip:5" / "video:3" / "other:{vodId}": rows opened to their edits and segments
  subs: new Map(),     // "clip:5" / "video:3" → { edits, loading, error }
  segEdits: new Map(), // segment (marker) ID → its edits (edits made only from that segment: segmentId on /api/projects)
  // The team's statuses and tags (GET /api/team/statuses; badrfly 2026-11-20, step 4) and the status / tag picked as a filter
  statuses: [],
  tags: [],
  canSetStatus: false,
  statusFilter: "",     // a status ID, "none", or "" for any
  tagFilter: "",        // a tag name
  statusVods: null,     // VOD IDs with clips or videos of that status / tag (GET /api/vods/matches), or null
  // The member's own stars and recently opened items (GET /api/my/marks; step 5) and the ★ / Recent chip picked ("starred", "recent" or "")
  marks: { starred: { clips: [], videos: [], vods: [] }, recent: { clips: [], videos: [], vods: [] } },
  mark: "",
  // "Sync with" (Player 2's piece menu) waiting for a channel to be picked: { onPick, onCancel, shade }
  picking: null,
};

let handlers = { onLoad: () => {}, onExport: () => {} };
let pollTimer = null;
let searchTimer = null;

export function setUpLibrary(options) {
  handlers = { ...handlers, ...options };
  state.view = loadSetting(viewKey(), "rows") === "cards" ? "cards" : "rows";
  $("side-list").addEventListener("scroll", () => {
    if (state.view === "rows") scheduleWindow();
    scheduleStamp();
    loadMoreIfNear();
  });
  // Moving from a row to its card at the bottom keeps that card (its buttons can be clicked)
  $("side-list").addEventListener("mouseleave", (event) => { if (!$("side-peek").contains(event.relatedTarget)) setHovered(""); });
  $("side-peek").addEventListener("mouseleave", (event) => { if (!$("side-list").contains(event.relatedTarget)) setHovered(""); });
  $("side-view").addEventListener("click", () => setView(state.view === "rows" ? "cards" : "rows"));
  $("side-stamp").addEventListener("click", (event) => openJumpMenu(event.currentTarget));
  new ResizeObserver(() => { if (state.view === "rows") scheduleWindow(); }).observe($("side-list"));
  renderViewButton();
  setUpFilter();
  setUpSearch();
  loadStatuses();
  loadMarks();
  if (options.sample) showSample(options.sample);
  else {
    loadChannels();
    window.addEventListener("clipco:stream", (event) => onStreamChange(event.detail));
    window.addEventListener("clipco:live-hello", () => onLiveHello());
    window.addEventListener("clipco:stream", () => renderChannelBar());
    window.addEventListener("clipco:channels", () => onChannelsChanged());
  }
}

// The app Owner changed the team's channels (core.js clipco:channels, badrfly 2026-11-20): a channel taken away leaves the bar and the
// VOD list, and the clips and videos lists are read again (the server hides a revoked channel's work); a new one shows up
async function onChannelsChanged() {
  let channels;
  try {
    channels = await api("GET", "/api/channels");
  } catch {
    return;
  }
  state.channels = channels;
  const logins = new Set(channels.map((c) => c.login));
  for (const login of [...state.sources.keys()]) if (!logins.has(login)) state.sources.delete(login);
  state.picked = new Set([...state.picked].filter((login) => logins.has(login)));
  if (!state.picked.size) {
    const primary = channels.find((c) => c.isPrimary) ?? channels[0];
    if (primary) state.picked.add(primary.login);
  }
  renderChannelBar();
  loadChannelCards();
  refreshLibraryFilters();
  await showChannelVods();
}

// Demo mode (?demo): sample data instead of the server (filters that call the server do nothing useful there)
function showSample(sample) {
  state.demo = true;
  state.channels = sample.channels;
  state.picked = new Set([sample.channels[0].login]);
  for (const channel of sample.channels) {
    const items = channel === sample.channels[0] ? sample.vods.map((vod) => ({ ...vod, channel: channel.login })) : [];
    state.sources.set(channel.login, { login: channel.login, items, cursor: null, first: false, loading: false, error: "" });
  }
  state.clips = sample.clips;
  state.clipsLoaded = true;
  state.videos = sample.videos;
  state.videosLoaded = true;
  renderChannelBar();
  render();
}

export function showList(list) {
  state.list = list;
  renderFilter();
  renderStatusBar();
  renderChips();
  render();
  if (list === "clips" && !state.clipsLoaded) loadClips();
  if (list === "videos" && !state.videosLoaded) loadVideos();
}

// A VOD from the loaded lists (Player 2's tree uses its title)
export function knownVod(vodId) {
  return loadedVods().find((v) => Number(v.vodId) === Number(vodId)) ?? null;
}

// The same channel's VOD before ("older") or after ("newer") one in the loaded lists, or null (Player 1's stream arrows). The older
// one may need the channel's next page.
export async function adjacentVod(vodId, direction) {
  const find = () => {
    for (const source of state.sources.values()) {
      const i = source.items.findIndex((v) => Number(v.vodId) === Number(vodId));
      if (i >= 0) return { source, i };
    }
    return null;
  };
  let found = find();
  if (!found) return null;
  if (direction === "newer") return found.source.items[found.i - 1] ?? null;
  if (found.i === found.source.items.length - 1 && found.source.cursor && !state.demo) {
    await loadChannelPage(found.source.login);
    found = find();
    if (!found) return null;
  }
  return found.source.items[found.i + 1] ?? null;
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
  loadWorkFor(loadedVods().map((v) => v.vodId));
  scheduleStatuses();
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
  // Starts on the team's own channel (badrfly 2026-11-19)
  const primary = state.channels.find((c) => c.isPrimary) ?? state.channels[0];
  state.picked = new Set(primary ? [primary.login] : []);
  renderChannelBar();
  loadChannelCards();
  await showChannelVods();
}

async function loadChannelCards() {
  try {
    for (const card of await api("GET", "/api/channels/cards")) state.cards.set(card.login, card);
  } catch {
    // The bar shows the channels' initials and no live ring
  }
  renderChannelBar();
}

// ---------- the channel bar ----------

function several() {
  return state.channels.length > 1;
}

// The channels whose VODs, videos and clips show: the ones turned on, or all of them
function shownChannels() {
  return several() && state.picked.size ? state.channels.filter((c) => state.picked.has(c.login)) : state.channels;
}

function channelByLogin(login) {
  return state.channels.find((c) => c.login === login) ?? null;
}

// Clips and videos keep the name Twitch gave the VOD's owner: the display name, sometimes the login
function channelByName(name) {
  const lower = String(name ?? "").toLowerCase();
  return lower ? state.channels.find((c) => c.login === lower || c.displayName?.toLowerCase() === lower) ?? null : null;
}

// The live watcher knows the moment a channel starts or stops; else the cards' answer (cached a few minutes) or a live VOD card
function channelIsLive(login) {
  const known = liveChannels.get(login);
  if (known?.watched && known.live != null) return known.live;
  return Boolean(state.cards.get(login)?.live) || Boolean(state.sources.get(login)?.items.some((vod) => vod.isLive));
}

function channelPicture(channel, className) {
  const picture = state.cards.get(channel.login)?.profileImageUrl;
  return picture
    ? el("img", { class: className, src: picture, alt: "", loading: "lazy", referrerpolicy: "no-referrer" })
    : el("span", { class: `${className} initial`, "aria-hidden": "true" }, (channel.displayName || channel.login).slice(0, 1).toUpperCase());
}

function renderChannelBar() {
  const bar = $("side-channels");
  bar.hidden = state.channels.length === 0;
  bar.classList.toggle("single", !several() && !state.picking);
  bar.classList.toggle("picking", Boolean(state.picking));
  bar.replaceChildren(...state.channels.map((channel) => {
    const picking = Boolean(state.picking);
    const on = !picking && state.picked.has(channel.login);
    const lit = picking || !several() || !state.picked.size || on;
    const live = channelIsLive(channel.login);
    const name = channel.displayName || channel.login;
    const tip = picking
      ? `Sync with ${name}`
      : `${name}${live ? " · live" : ""}${several() ? (on ? " · click to hide" : " · click to show") : ""}`;
    const button = el("button", {
      type: "button",
      class: `chan${live ? " live" : ""}${lit ? "" : " dim"}${several() && on ? " on" : ""}${picking ? " pick" : ""}`,
      "aria-label": picking ? `Sync with ${name}` : `${name}${live ? " (live)" : ""}`,
      "aria-pressed": several() && !picking ? String(on) : null,
      onclick: () => channelClicked(channel.login),
    }, channelPicture(channel, "chan-picture"));
    return withTooltip(button, tip, null, "bottom");
  }));
}

// ---------- picking a channel for "Sync with" ----------
//
// Player 2's piece menu asks for a channel: the rest of the editor goes dark (the drag shade) with only the channel bar lit, every
// picture lit, and a click on one hands its login to onPick instead of filtering the lists. Esc, a click in the dark or another
// "Sync with" cancels it.

export function teamChannels() {
  return state.channels.map((c) => ({ login: c.login, displayName: c.displayName || c.login }));
}

export function pickChannel({ onPick, onCancel }) {
  cancelChannelPick();
  const shade = el("div", { class: "focus-shade pick-shade", title: "Click to cancel (Esc)" });
  shade.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    cancelChannelPick();
  });
  document.body.append(shade);
  const cut = () => {
    const bar = $("side-channels");
    const r = bar.getBoundingClientRect();
    shade.style.clipPath = shadePath([{ left: r.left - 6, top: r.top - 6, right: r.right + 6, bottom: r.bottom + 6 }]);
  };
  const onKey = (event) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopImmediatePropagation();
    cancelChannelPick();
  };
  document.addEventListener("keydown", onKey, true);
  window.addEventListener("resize", cut);
  state.picking = {
    onPick, onCancel,
    stop: () => {
      shade.remove();
      document.removeEventListener("keydown", onKey, true);
      window.removeEventListener("resize", cut);
    },
  };
  renderChannelBar();
  cut();
  $("side-channels").querySelector(".chan")?.focus();
}

export function cancelChannelPick() {
  const picking = state.picking;
  if (!picking) return;
  state.picking = null;
  picking.stop();
  renderChannelBar();
  picking.onCancel?.();
}

function channelClicked(login) {
  const picking = state.picking;
  if (!picking) return toggleChannel(login);
  state.picking = null;
  picking.stop();
  renderChannelBar();
  picking.onPick(login);
}

function toggleChannel(login) {
  if (!several()) return;
  if (state.picked.has(login)) state.picked.delete(login);
  else state.picked.add(login);
  renderChannelBar();
  showChannelVods();
  if (state.demo) return render();
  if (state.clipsLoaded || state.list === "clips") loadClips();
  if (state.videosLoaded || state.list === "videos") loadVideos();
}

// clips and videos of the channels turned on (none on: everything, VODs of channels no longer assigned too)
function channelQuery(query) {
  if (several() && state.picked.size) query.set("channels", [...state.picked].join(","));
}

// A small picture of the item's channel on its card, when cards of several channels are in one list
function cardChannel(channel) {
  if (!channel || shownChannels().length < 2) return null;
  const picture = channelPicture(channel, "card-chan");
  picture.title = channel.displayName || channel.login;
  return picture;
}

// ---------- VODs of several channels ----------

const vodTime = (vod) => new Date(vod.createdAtUtc).getTime();

function loadedVods() {
  return [...state.sources.values()].flatMap((source) => source.items);
}

function shownSources() {
  return shownChannels().map((c) => state.sources.get(c.login)).filter(Boolean);
}

// The shown channels' VODs, newest first. A channel with more pages holds the list at its oldest loaded VOD, so an older VOD of
// another channel never shows before this one's next page is in.
function mergedVods() {
  const sources = shownSources();
  let cutoff = -Infinity;
  for (const source of sources) if (source.cursor && source.items.length) cutoff = Math.max(cutoff, vodTime(source.items.at(-1)));
  return sources.flatMap((source) => source.items).filter((vod) => vod.isLive || vodTime(vod) >= cutoff).sort((a, b) => vodTime(b) - vodTime(a));
}

function moreVods() {
  return shownSources().some((source) => source.cursor);
}

function vodsLoading() {
  return shownSources().some((source) => source.loading);
}

// Starts the first page of each shown channel not loaded yet
function showChannelVods() {
  render();
  if (state.demo) return Promise.resolve();
  return Promise.all(shownChannels().filter((c) => !state.sources.has(c.login)).map((c) => loadChannelPage(c.login)));
}

async function reloadVods() {
  state.sources = new Map();
  state.vodsError = "";
  await showChannelVods();
}

// Scrolled near the end: the channel holding the list back (its oldest loaded VOD is the newest) loads its next page
function loadMoreVods() {
  const waiting = shownSources().filter((source) => source.cursor && !source.loading);
  if (!waiting.length) return;
  const next = waiting.reduce((a, b) => (vodTime(a.items.at(-1)) >= vodTime(b.items.at(-1)) ? a : b));
  loadChannelPage(next.login);
}

async function loadChannelPage(login) {
  let source = state.sources.get(login);
  if (!source) state.sources.set(login, source = { login, items: [], cursor: null, first: true, loading: false, error: "" });
  if (source.loading || (!source.first && !source.cursor)) return;
  source.loading = true;
  const type = state.vodType;
  const cursor = source.first ? "" : `&cursor=${encodeURIComponent(source.cursor)}`;
  if (state.list === "vods") render();
  try {
    const page = await api("GET", `/api/channels/${encodeURIComponent(login)}/vods?type=${type}&limit=20${cursor}`);
    if (state.sources.get(login) !== source) return; // the type changed while this loaded
    const first = source.first;
    for (const vod of page.items) vod.channel = login;
    source.items.push(...page.items);
    source.cursor = page.nextCursor;
    source.first = false;
    if (first) checkLive(login);
    loadWorkFor(page.items.map((v) => v.vodId));
  } catch (error) {
    source.error = error.message;
    source.first = false;
  } finally {
    source.loading = false;
  }
  if (state.list === "vods") render();
}

// Twitch's VOD lists don't say whether a stream is still going, so the newest VOD (if it started in the last 2 days) is
// looked up on its own (GET /api/vods/info → isLive)
async function checkLive(login) {
  const newest = state.sources.get(login)?.items[0];
  if (!newest || Date.now() - vodTime(newest) > 2 * 86400_000) return;
  // The server hears from Twitch when this channel streams: no need to ask when it says it's offline
  const known = liveChannels.get(login);
  if (known?.watched && known.live === false) return;
  try {
    const info = await api("GET", `/api/vods/info?vod=${newest.vodId}`);
    if (!info.isLive) return;
    newest.isLive = true;
    renderChannelBar();
    if (state.list === "vods") render();
  } catch {
    // Not knowing is fine: the card just shows no LIVE badge
  }
}

// ---------- live stream updates (core.js startLiveEvents) ----------
// A channel started or stopped streaming: its VODs re-read their first page (a new VOD appears, the LIVE badge comes and goes, the
// final length shows). Twitch needs a moment to list a new VOD and to settle the length, so it looks again a couple of times after.
const liveRetries = new Map(); // login → timers

function onStreamChange({ login, live }) {
  const source = state.sources.get(login);
  if (!source) return;
  for (const timer of liveRetries.get(login) ?? []) clearTimeout(timer);
  if (!live) {
    for (const vod of source.items) vod.isLive = false;
    if (state.list === "vods") render();
  }
  refreshNewestVods(login);
  liveRetries.set(login, [20_000, 90_000].map((ms) => setTimeout(() => refreshNewestVods(login), ms)));
}

// After (re)connecting: the cards may have missed a change while the page was hidden or the server restarted
function onLiveHello() {
  renderChannelBar();
  for (const source of state.sources.values()) {
    const known = liveChannels.get(source.login);
    if (!known?.watched || known.live == null || !source.items.length) continue;
    const showsLive = source.items.some((vod) => vod.isLive);
    if (showsLive !== known.live) onStreamChange({ login: source.login, live: known.live });
  }
}

async function refreshNewestVods(login) {
  const source = state.sources.get(login);
  if (!source || source.loading) return;
  const type = state.vodType;
  let page;
  try {
    page = await api("GET", `/api/channels/${encodeURIComponent(login)}/vods?type=${type}&limit=20`);
  } catch {
    return; // the next change or retry tries again
  }
  if (state.sources.get(login) !== source) return;
  const byId = new Map(source.items.map((vod) => [String(vod.vodId), vod]));
  const added = [];
  for (const item of page.items) {
    item.channel = login;
    const known = byId.get(String(item.vodId));
    if (known) Object.assign(known, item, { isLive: known.isLive && liveChannels.get(login)?.live !== false });
    else added.push(item);
  }
  if (added.length) source.items.unshift(...added);
  renderChannelBar();
  if (state.list === "vods") render();
  if (added.length) loadWorkFor(added.map((v) => v.vodId));
  if (liveChannels.get(login)?.live !== false) checkLive(login);
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
  statusQuery(query);
  if (state.mark) query.set("mark", state.mark);
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

// The clips query for the current filters (without paging)
function clipsQuery() {
  const f = state.clipFilter;
  const query = new URLSearchParams({ sort: f.sort, order: f.order, paged: "true", limit: String(PAGE_SIZE) });
  if (f.search) query.set("search", f.search);
  whoQuery(query, "clips");
  channelQuery(query);
  const bounds = dayBounds();
  if (bounds) {
    query.set("from", bounds.from);
    if (bounds.to) query.set("to", bounds.to);
    query.set("dateField", f.sort);
  }
  if (f.minSeconds != null) query.set("minSeconds", String(f.minSeconds));
  if (f.maxSeconds != null) query.set("maxSeconds", String(f.maxSeconds));
  return query;
}

// The first page of clips for the current filters (a newer call wins over one still loading)
let clipsSeq = 0;
async function loadClips() {
  const seq = ++clipsSeq;
  try {
    const page = await api("GET", `/api/entries?${clipsQuery()}`);
    if (seq !== clipsSeq) return;
    state.clips = page.items;
    state.clipsCursor = page.nextCursor;
    state.clipsTotal = page.total;
    rememberMembers(state.clips.map((c) => [c.addedByUserId, c.addedBy]));
    state.clipsError = "";
  } catch (error) {
    if (seq !== clipsSeq) return;
    state.clipsError = error.message;
  }
  state.clipsMore = false;
  state.clipsLoaded = true;
  if (state.list === "clips") render();
  schedulePoll();
  reloadKids();
}

async function loadMoreClips() {
  if (!state.clipsCursor || state.clipsMore || state.demo) return;
  const seq = clipsSeq;
  state.clipsMore = true;
  const query = clipsQuery();
  query.set("cursor", state.clipsCursor);
  try {
    const page = await api("GET", `/api/entries?${query}`);
    if (seq !== clipsSeq) return;
    const known = new Set(state.clips.map((c) => c.id));
    state.clips.push(...page.items.filter((c) => !known.has(c.id)));
    state.clipsCursor = page.nextCursor;
    state.clipsTotal = page.total;
    rememberMembers(page.items.map((c) => [c.addedByUserId, c.addedBy]));
  } catch (error) {
    if (seq === clipsSeq) toast(error.message, "error");
  }
  if (seq !== clipsSeq) return;
  state.clipsMore = false;
  if (state.list === "clips") render();
  schedulePoll();
}

const isBusy = (c) => ["Queued", "Downloading"].includes(c.status) || ["Queued", "Generating"].includes(c.generationStatus);

// Clips still downloading are read again one by one (the list may hold thousands; only these change by themselves)
function schedulePoll() {
  clearTimeout(pollTimer);
  if (state.clips.some(isBusy)) pollTimer = setTimeout(pollBusyClips, POLL_MS);
}

async function pollBusyClips() {
  const busy = state.clips.filter(isBusy).slice(0, 20);
  await Promise.all(busy.map(async (clip) => {
    try {
      const fresh = await api("GET", `/api/entries/${clip.id}`);
      const index = state.clips.indexOf(clip);
      if (index >= 0) state.clips[index] = { ...fresh, addedBy: fresh.addedBy ?? clip.addedBy };
    } catch (error) {
      if (error.status === 404) state.clips = state.clips.filter((c) => c !== clip);
    }
  }));
  if (state.list === "clips") render();
  schedulePoll();
}

function videosQuery() {
  const query = new URLSearchParams({ paged: "true", limit: String(PAGE_SIZE) });
  whoQuery(query, "videos");
  channelQuery(query);
  if (state.clipFilter.search) query.set("search", state.clipFilter.search);
  const bounds = dayBounds();
  if (bounds) {
    query.set("from", bounds.from);
    if (bounds.to) query.set("to", bounds.to);
  }
  return query;
}

// The team's videos (segments linked into one), newest first, a page at a time
let videosSeq = 0;
async function loadVideos() {
  const seq = ++videosSeq;
  try {
    const page = await api("GET", `/api/videos?${videosQuery()}`);
    if (seq !== videosSeq) return;
    state.videos = page.items;
    state.videosCursor = page.nextCursor;
    state.videosTotal = page.total;
    rememberMembers(state.videos.map((v) => [v.userId, v.createdBy]));
    state.videosError = "";
  } catch (error) {
    if (seq !== videosSeq) return;
    state.videosError = error.message;
  }
  state.videosMore = false;
  state.videosLoaded = true;
  if (state.list === "videos") render();
  reloadKids();
}

async function loadMoreVideos() {
  if (!state.videosCursor || state.videosMore || state.demo) return;
  const seq = videosSeq;
  state.videosMore = true;
  const query = videosQuery();
  query.set("cursor", state.videosCursor);
  try {
    const page = await api("GET", `/api/videos?${query}`);
    if (seq !== videosSeq) return;
    const known = new Set(state.videos.map((v) => v.id));
    state.videos.push(...page.items.filter((v) => !known.has(v.id)));
    state.videosCursor = page.nextCursor;
    state.videosTotal = page.total;
    rememberMembers(page.items.map((v) => [v.userId, v.createdBy]));
  } catch (error) {
    if (seq === videosSeq) toast(error.message, "error");
  }
  if (seq !== videosSeq) return;
  state.videosMore = false;
  if (state.list === "videos") render();
}

// Near the end of the list (or the list doesn't fill the panel yet): the next page of the list shown
function loadMoreIfNear() {
  const list = $("side-list");
  if (list.scrollTop + list.clientHeight <= list.scrollHeight - NEAR_BOTTOM_PX) return;
  if (state.list === "vods") loadMoreVods();
  else if (state.list === "clips") loadMoreClips();
  else loadMoreVideos();
}

export function refreshVideos() {
  return loadVideos();
}

export function refreshClips() {
  return loadClips();
}

// ---------- rendering ----------

const EMPTY = {
  vods: () => state.vodsError || shownSources().find((source) => source.error)?.error || (state.channels.length
    ? (vodsLoading() || shownSources().length < shownChannels().length ? "Loading VODs…" : "No VODs here yet.")
    : "Your team has no channels yet. The app owner assigns them."),
  videos: () => state.videosError || (state.videosLoaded
    ? (filtersOn() ? "No videos match the filter." : "No videos yet. Link segments on a VOD's timeline to make one.")
    : "Loading videos…"),
  clips: () => state.clipsError || (state.clipsLoaded ? (filtersOn() ? "No clips match the filter." : "No clips yet.") : "Loading clips…"),
};

function visibleItems() {
  if (state.list === "vods") {
    const search = state.vodSearch.toLowerCase();
    const bounds = dayBounds();
    const from = bounds ? Date.parse(bounds.from) : -Infinity;
    const to = bounds?.to ? Date.parse(bounds.to) : Infinity;
    return mergedVods().filter((v) => (!search || v.title.toLowerCase().includes(search) || state.vodMatches?.has(String(v.vodId)))
      && (!state.onlyWorked || state.work.has(String(v.vodId)))
      && (!bounds || (vodTime(v) >= from && vodTime(v) < to))
      && (!state.statusVods || state.statusVods.has(String(v.vodId)))
      && (!state.mark || marked(state.mark, "vod", v)));
  }
  return state.list === "clips" ? state.clips : state.videos;
}

function countText(items) {
  const noun = { vods: "VOD", videos: "video", clips: "clip" }[state.list];
  const total = state.list === "clips" ? state.clipsTotal : state.list === "videos" ? state.videosTotal : items.length;
  const more = state.list === "vods" && moreVods() ? "+" : "";
  if (!items.length && !total) return "";
  return `${total.toLocaleString()}${more} ${noun}${total === 1 && !more ? "" : "s"}`;
}

function listLoadingMore() {
  return state.list === "vods" ? vodsLoading() : state.list === "clips" ? state.clipsMore : state.videosMore;
}

function render() {
  const items = visibleItems();
  $("side-count").textContent = countText(items);
  $("side-filter").toggleAttribute("data-active", filtersOn());
  const list = $("side-list");
  list.classList.toggle("rows", state.view === "rows");
  if (!items.length) {
    rowsModel = null;
    state.cardLines = null;
    list.replaceChildren(el("p", { class: "side-empty" }, EMPTY[state.list]()));
    renderRail(null);
    renderPeek();
    return;
  }
  const lines = linesFor(items);
  if (state.view === "rows") {
    renderRows(lines);
    state.cardLines = null;
  } else {
    rowsModel = null;
    state.cardLines = lines;
    const nodes = [];
    lines.forEach((line, index) => {
      // A thin line between two items of one day (not under an opened VOD)
      if (!isMark(line) && !line.note && !line.kid && !line.sub && !line.first) nodes.push(el("div", { class: "side-sep thin" }, el("i")));
      const node = line.month ? monthRow(line, index)
        : line.head ? el("div", { class: "side-sep day" }, el("i"))
        : line.note ? kidNote(line)
        : line.sub ? subRow(line)
        : line.kid ? listRow(line)
        : card(specFor(line.item, line.type));
      if (line.first && !line.kid) node.append(dayNumber(line));
      node.dataset.line = String(index);
      nodes.push(node);
    });
    if (listLoadingMore()) nodes.push(el("p", { class: "side-empty" }, "Loading more…"));
    list.replaceChildren(...nodes);
  }
  renderRail(lines);
  renderPeek();
  // A short first page that doesn't fill the panel can't be scrolled to load the next one
  requestAnimationFrame(loadMoreIfNear);
}

// ---------- rows view: months, days, only the rows in view drawn ----------

const dayFormatters = new Map();

// What a list is grouped by: the date the list is sorted on (clips: added or stream date; videos: made; VODs: streamed)
function itemDate(item) {
  if (state.list === "clips") return state.clipFilter.sort === "streamDate" ? item.streamDateUtc : item.addedAtUtc;
  return item.createdAtUtc;
}

// "Today", "Yesterday", "Thu 14 Nov", "Thu 14 Nov 2025" in the viewer's time zone
function dayLabel(ms, zone) {
  const day = zonedParts(ms, zone);
  const today = zonedParts(Date.now(), zone);
  const key = (p) => Date.UTC(p.year, p.month - 1, p.day);
  const diff = Math.round((key(today) - key(day)) / 86400_000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Yesterday";
  const withYear = day.year !== today.year;
  let formatter = dayFormatters.get(`${zone}|${withYear}`);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(undefined, { timeZone: zone, weekday: "short", day: "numeric", month: "short", ...(withYear ? { year: "numeric" } : {}) });
    dayFormatters.set(`${zone}|${withYear}`, formatter);
  }
  return formatter.format(new Date(ms));
}

// The list as lines (badrfly 2026-11-21, his sketch): a month line across the whole width with the month in the date column, a slightly
// thicker line between days, a thin line between items, and each day's number in the date column in front of its first item.
// Lines: { month, at, count } / { head: true } (between days) / { key, item, type, at, day, first } (day = { label, number, count })
function linesFor(items) {
  const zone = viewerTimeZone();
  const thisYear = zonedParts(Date.now(), zone).year;
  const type = listType();
  const lines = [];
  let monthKey = null;
  let dayKey = null;
  let month = null;
  let day = null;
  for (const item of items) {
    const at = itemDate(item);
    const ms = at ? new Date(at).getTime() : NaN;
    const p = Number.isNaN(ms) ? null : zonedParts(ms, zone);
    const mKey = p ? `${p.year}-${p.month}` : "none";
    const dKey = p ? `${mKey}-${p.day}` : "none";
    let first = false;
    // The same month or day can come back in a list that isn't sorted on this date: each line still needs its own key
    if (mKey !== monthKey) {
      monthKey = mKey;
      dayKey = null;
      month = { month: monthName(p, thisYear), key: `month:${mKey}:${lines.length}`, at: p, count: 0 };
      lines.push(month);
    }
    if (dKey !== dayKey) {
      if (dayKey !== null) lines.push({ head: true, key: `day:${dKey}:${lines.length}` });
      dayKey = dKey;
      day = { label: p ? dayLabel(ms, zone) : "No date", number: p ? String(p.day) : "–", count: 0 };
      first = true;
    }
    day.count++;
    month.count++;
    const line = { key: itemKey(item, type), item, type, at: p, day, first };
    lines.push(line);
    if (type === "vod" && state.open.has(String(item.vodId))) lines.push(...kidLines(item, p));
    if (type !== "vod" && isSubOpen(type, item)) lines.push(...subLines(line));
  }
  return lines;
}

// A line that isn't an item or a note: a month or the line between days
function isMark(line) {
  return Boolean(line.month || line.head);
}

const monthFormatters = new Map();

function monthFormatter(options) {
  const key = JSON.stringify(options);
  let formatter = monthFormatters.get(key);
  if (!formatter) monthFormatters.set(key, formatter = new Intl.DateTimeFormat(undefined, { ...options, timeZone: "UTC" }));
  return formatter;
}

// "Oct", or "Oct '25" for an earlier year
function monthName(p, thisYear) {
  if (!p) return "—";
  const name = monthFormatter({ month: "short" }).format(new Date(Date.UTC(p.year, p.month - 1, 15)));
  return p.year === thisYear ? name : `${name} '${String(p.year).slice(-2)}`;
}

function monthTitle(line) {
  if (!line.at) return `No date · ${countLabel(line.count)}`;
  return `${monthFormatter({ month: "long", year: "numeric" }).format(new Date(Date.UTC(line.at.year, line.at.month - 1, 15)))} · ${countLabel(line.count)}`;
}

function dayTitle(day) {
  return `${day.label} · ${countLabel(day.count)}`;
}

// What the list shows: "vod", "clip" or "video" (a VOD's opened lines are clips and videos on the VODs list)
function listType() {
  return state.list === "vods" ? "vod" : state.list === "clips" ? "clip" : "video";
}

// ---------- VODs open to their videos and clips (badrfly 2026-11-20, step 3 of Large libraries) ----------
// A VOD with clips or videos on it gets an arrow; opened, its videos then clips (in VOD order) are slim rows right under it.
// Up to KIDS_MAX of each are read (GET /api/videos?vodIds= / /api/entries?vodId=); a longer list ends in "All n clips", which filters CLIPS.

const KIDS_MAX = 50;

function hasKids(vod) {
  const work = state.work.get(String(vod.vodId));
  return Boolean(work && (work.clips || work.videos || work.segments || work.edits));
}

function kidLines(vod, at) {
  const id = String(vod.vodId);
  const kids = state.kids.get(id);
  const note = (key, text, run) => ({ key: `kid:${id}:${key}`, note: text, run, vodId: id, kid: true, at });
  if (!kids || (kids.loading && !kids.clips)) return [note("loading", "Loading…")];
  if (kids.error) return [note("error", kids.error)];
  const lines = [];
  const push = (line) => {
    lines.push(line);
    if (isSubOpen(line.type, line.item)) lines.push(...subLines(line));
  };
  for (const video of kids.videos) push({ key: `kid:${id}:video:${video.id}`, item: video, type: "video", kid: true, vodId: id, at });
  if (kids.videosTotal > kids.videos.length) lines.push(note("videos", `All ${kids.videosTotal} videos`, () => showVodWork(id, "videos")));
  for (const clip of kids.clips) push({ key: `kid:${id}:clip:${clip.id}`, item: clip, type: "clip", kid: true, vodId: id, at });
  if (kids.clipsTotal > kids.clips.length) lines.push(note("clips", `All ${kids.clipsTotal} clips`, () => showVodWork(id, "clips")));
  lines.push(...otherLines(id, at, kids));
  if (!lines.length) return [note("none", "Nothing here any more.")];
  return lines;
}

function toggleVod(vod, open = !state.open.has(String(vod.vodId))) {
  const id = String(vod.vodId);
  if (open === state.open.has(id)) return;
  if (open) {
    state.open.add(id);
    if (!state.kids.has(id)) loadKids(id);
  } else {
    state.open.delete(id);
    // A selected row inside it goes back to the VOD
    if (state.selected.vods.startsWith(`kid:${id}:`)) state.selected.vods = `vod:${id}`;
  }
  if (state.list === "vods") render();
}

// Where a part of the VOD starts: clips by their start, videos by their first segment on this VOD
function kidStart(item, id) {
  if (item.segments) return Math.min(...item.segments.filter((s) => String(s.vodId ?? item.vodId) === id).map((s) => s.startSeconds), Infinity);
  return item.startSeconds ?? item.startTime ?? 0;
}

async function loadKids(id) {
  const kids = state.kids.get(id) ?? { clips: null, videos: null, clipsTotal: 0, videosTotal: 0 };
  kids.loading = true;
  state.kids.set(id, kids);
  const byStart = (a, b) => kidStart(a, id) - kidStart(b, id);
  try {
    if (state.demo) {
      Object.assign(kids, { clips: state.clips.filter((c) => String(c.vodId) === id), videos: state.videos.filter((v) => videoStreamIds(v).has(id)), segments: [], other: [] });
      kids.clipsTotal = kids.clips.length;
      kids.videosTotal = kids.videos.length;
    } else {
      const limit = String(KIDS_MAX);
      const [clips, videos, edits] = await Promise.all([
        api("GET", `/api/entries?${statusQuery(new URLSearchParams({ paged: "true", limit, vodId: id }))}`),
        api("GET", `/api/videos?${statusQuery(new URLSearchParams({ paged: "true", limit, vodIds: id }))}`),
        api("GET", `/api/projects?vodId=${id}&limit=1000`),
      ]);
      // The edits of its clips and videos are known now (their rows open without asking again); the rest are "Other edits"
      const oldest = [...edits].reverse();
      for (const clip of clips.items) state.subs.set(`clip:${clip.id}`, { edits: oldest.filter((e) => e.entryId === clip.id) });
      for (const video of videos.items) {
        if (String(video.vodId) === id) state.subs.set(`video:${video.id}`, { edits: oldest.filter((e) => e.videoId === video.id) });
      }
      const loose = oldest.filter((e) => !e.entryId && !e.videoId && Number(e.vodId) === Number(id));
      rememberSegmentEdits(loose);
      // Every segment on the VOD gets a line under it (badrfly 2026-11-21: a vertical edit shouldn't be found only through a video)
      const markers = await api("GET", `/api/markers?vodIds=${id}`);
      const segments = markers.filter((m) => m.endSeconds != null && String(m.vodId) === id).sort((a, b) => a.seconds - b.seconds);
      const shown = new Set(segments.map((m) => m.id));
      Object.assign(kids, { clips: clips.items, videos: videos.items, clipsTotal: clips.total, videosTotal: videos.total, segments,
        // Edits of a VOD range, or whose clip, video or segment is gone: right under the VOD
        other: loose.filter((e) => !e.segmentId || !shown.has(e.segmentId)) });
      rememberMembers([...clips.items.map((c) => [c.addedByUserId, c.addedBy]), ...videos.items.map((v) => [v.userId, v.createdBy])]);
    }
    kids.clips.sort(byStart);
    kids.videos.sort(byStart);
    kids.error = "";
  } catch (error) {
    kids.error = error.message;
  }
  kids.loading = false;
  if (state.list === "vods" && state.open.has(id)) render();
}

// Clips or videos changed: opened VODs read theirs again, closed ones forget them
let kidsTimer = 0;
function reloadKids() {
  clearTimeout(kidsTimer);
  kidsTimer = setTimeout(reloadKidsNow, 300);
}

function reloadKidsNow() {
  // The VOD rows' counts and arrows change with the clips and videos too (a VOD's first clip gives it an arrow)
  if (!state.demo) loadWorkFor(loadedVods().map((v) => v.vodId));
  for (const id of [...state.kids.keys()]) {
    if (state.open.has(id)) loadKids(id);
    else state.kids.delete(id);
  }
}

function videoStreamIds(video) {
  return new Set((video.segments ?? []).map((s) => String(s.vodId ?? video.vodId)).concat(video.segments?.length ? [] : [String(video.vodId)]));
}

// The arrow at the start of a VOD's row (an empty space of the same width on VODs with nothing to open)
function vodCaret(vod) {
  if (!hasKids(vod)) return el("span", { class: "lcaret empty" });
  const open = state.open.has(String(vod.vodId));
  const caret = el("button", { class: `lcaret${open ? " open" : ""}`, type: "button", tabindex: "-1", "aria-label": open ? "Close" : "Show its videos and clips", title: open ? "Close" : "Show its videos and clips (→)" },
    el("sl-icon", { library: "ph", name: "caret-right" }));
  caret.addEventListener("pointerdown", (event) => event.stopPropagation());
  caret.addEventListener("click", (event) => {
    event.stopPropagation();
    toggleVod(vod);
  });
  caret.addEventListener("dblclick", (event) => event.stopPropagation());
  return caret;
}

// A line under an opened VOD that isn't an item: loading, "All 120 clips", a problem
function kidNote(line) {
  const node = el(line.run ? "button" : "div", { class: `lrow${line.kid ? " kid" : ""}${line.sub ? " sub" : ""} lrow-kidnote${line.run ? " link" : ""}`, ...(line.run ? { type: "button" } : {}) }, line.note);
  if (line.run) node.addEventListener("click", line.run);
  return node;
}

// ---------- edits and segments under clips and videos (badrfly 2026-11-21: Player 2's picker moved into the library) ----------
// A clip or video row (on its own tab or under an opened VOD) has an arrow: opened, a video shows its segments, then the edits of it; a clip
// its edits. An opened VOD ends with "Other edits" (edits of a lone segment or VOD range, or whose clip/video was deleted) when it has any.
// Double-click / Enter loads a segment in Player 2 and opens an edit (a vertical one in the vertical editor); right-click has the same menu
// the picker had (ui/p2-tree.js editMenu / segmentMenu). The line of what Player 2 has loaded is marked.

function subKeyOf(type, item) {
  return `${type}:${item.id}`;
}

function isSubOpen(type, item) {
  return state.subOpen.has(subKeyOf(type, item));
}

function toggleSub(type, item, open = !isSubOpen(type, item)) {
  const key = subKeyOf(type, item);
  if (open === state.subOpen.has(key)) return;
  if (open) {
    state.subOpen.add(key);
    if ((type === "clip" || type === "video") && !state.subs.has(key)) loadSub(type, item);
  } else {
    state.subOpen.delete(key);
    // A selected line inside it goes back to its row
    const selected = state.selected[state.list] ?? "";
    const cut = selected.indexOf(`>${key}>`);
    if (cut >= 0) state.selected[state.list] = selected.slice(0, cut);
  }
  render();
}

async function loadSub(type, item) {
  const key = subKeyOf(type, item);
  const sub = state.subs.get(key) ?? { edits: null };
  sub.loading = true;
  state.subs.set(key, sub);
  try {
    if (state.demo) sub.edits = [];
    else {
      const edits = await api("GET", `/api/projects?${type === "clip" ? "entryId" : "videoId"}=${item.id}&limit=200`);
      sub.edits = [...edits].reverse();
      // A video's segments: the edits made from each of them (edits belong to their segment's VOD)
      if (type === "video" && item.segments?.length) {
        const vods = [...new Set(item.segments.map((x) => String(x.vodId ?? item.vodId)))];
        const onVods = await api("GET", `/api/projects?vodIds=${vods.join(",")}&limit=1000`);
        rememberSegmentEdits([...onVods].reverse().filter((e) => !e.entryId && !e.videoId));
      }
    }
    sub.error = "";
  } catch (error) {
    sub.error = error.message;
  }
  sub.loading = false;
  if (state.subOpen.has(key)) render();
}

// Edits changed (made, renamed, deleted): the opened lists are read again, the others forgotten
export function refreshEdits() {
  state.segEdits.clear();
  for (const key of [...state.subs.keys()]) if (!state.subOpen.has(key)) state.subs.delete(key);
  for (const key of state.subOpen) {
    const [type, id] = key.split(":");
    if (type === "clip" || type === "video") loadSub(type, { id: Number(id) });
  }
  for (const id of state.open) loadKids(id);
}

// What Player 2 has loaded changed: its line is marked
let p2LoadedSig = "";
export function player2Changed() {
  const loaded = handlers.loaded?.() ?? null;
  const sig = JSON.stringify(loaded);
  if (sig === p2LoadedSig) return;
  p2LoadedSig = sig;
  if (rowsModel || state.cardLines) render();
}

function p2Loaded() {
  return handlers.loaded?.() ?? null;
}

// The lines under an opened clip or video row
function subLines(parent) {
  const { item, type } = parent;
  const base = `${parent.key}>${subKeyOf(type, item)}>`;
  const common = { sub: true, kid: parent.kid, vodId: parent.vodId, at: parent.at, parent: { item, type, key: parent.key } };
  const lines = [];
  if (type === "video") {
    const home = String(parent.vodId ?? item.vodId);
    for (const segment of [...(item.segments ?? [])].sort((a, b) => a.position - b.position)) {
      const line = { ...common, key: `${base}seg:${segment.markerId}`, type: "segment", item: segment, video: item, home };
      lines.push(line, ...segmentEditLines(line));
    }
  }
  const sub = state.subs.get(subKeyOf(type, item));
  const note = (k, text) => ({ ...common, key: `${base}${k}`, note: text });
  if (!sub?.edits) lines.push(note("loading", sub?.error || "Loading…"));
  else {
    for (const edit of sub.edits) lines.push({ ...common, key: `${base}edit:${edit.id}`, type: "edit", item: edit });
    if (!sub.edits.length && type === "clip") lines.push(note("none", "No edits yet. Load the clip and change something to start one."));
  }
  return lines;
}

function rememberSegmentEdits(edits) {
  const bySegment = new Map();
  for (const edit of edits) {
    if (!edit.segmentId) continue;
    if (!bySegment.has(edit.segmentId)) bySegment.set(edit.segmentId, []);
    bySegment.get(edit.segmentId).push(edit);
  }
  for (const [id, list] of bySegment) state.segEdits.set(id, list);
}

function segmentEdits(markerId) {
  return state.segEdits.get(markerId) ?? [];
}

// A segment's edits, when its line is opened (one step further in)
function segmentEditLines(segmentLine) {
  const id = segmentLine.item.markerId;
  const segKey = segmentLine.segKey ?? "seg";
  if (!segmentEdits(id).length || !isSubOpen(segKey, { id })) return [];
  return segmentEdits(id).map((edit) => ({ ...segmentLine, key: `${segmentLine.key}>${segKey}:${id}>edit:${edit.id}`, type: "edit", item: edit, deep: !segmentLine.lone, lone: false,
    parent: { item: { id }, type: segKey, key: segmentLine.key, segment: true } }));
}

// The end of an opened VOD's lines: its segments (each opening to its edits), then edits of a VOD range or of something deleted
function otherLines(vodId, at, kids) {
  const lines = [];
  const common = { sub: true, kid: true, vodId, at, parent: null };
  for (const marker of kids.segments ?? []) {
    const segment = { markerId: marker.id, position: 0, label: marker.label, color: marker.color, startSeconds: marker.seconds, endSeconds: marker.endSeconds,
      keptSeconds: marker.keptSeconds, vodId: marker.vodId, cuts: marker.cuts };
    // Its "video" for loading and renaming: the VOD and the segment's maker
    const line = { ...common, key: `kid:${vodId}:seg:${marker.id}`, type: "segment", item: segment, video: { vodId: marker.vodId, userId: marker.createdByUserId }, home: String(vodId), lone: true, segKey: "lseg" };
    lines.push(line, ...segmentEditLines(line));
  }
  for (const edit of kids.other ?? []) lines.push({ ...common, key: `kid:${vodId}:edit:${edit.id}`, type: "edit", item: edit, lone: true });
  return lines;
}

// The arrow on a segment line that has edits
function segmentCaret(markerId, segKey) {
  const open = isSubOpen(segKey, { id: markerId });
  const caret = el("button", { class: `lcaret${open ? " open" : ""}`, type: "button", tabindex: "-1", "aria-label": open ? "Close" : "Show its edits", title: open ? "Close" : "Show its edits (→)" },
    el("sl-icon", { library: "ph", name: "caret-right" }));
  caret.addEventListener("pointerdown", (event) => event.stopPropagation());
  caret.addEventListener("click", (event) => {
    event.stopPropagation();
    toggleSub(segKey, { id: markerId });
  });
  caret.addEventListener("dblclick", (event) => event.stopPropagation());
  return caret;
}

// The arrow on a clip or video row
function itemCaret(line) {
  const open = isSubOpen(line.type, line.item);
  const what = line.type === "video" ? "its segments and edits" : "its edits";
  const caret = el("button", { class: `lcaret${open ? " open" : ""}`, type: "button", tabindex: "-1", "aria-label": open ? "Close" : `Show ${what}`, title: open ? "Close" : `Show ${what} (→)` },
    el("sl-icon", { library: "ph", name: "caret-right" }));
  caret.addEventListener("pointerdown", (event) => event.stopPropagation());
  caret.addEventListener("click", (event) => {
    event.stopPropagation();
    toggleSub(line.type, line.item);
  });
  caret.addEventListener("dblclick", (event) => event.stopPropagation());
  return caret;
}

function subOpenLine(line) {
  if (line.type === "edit") handlers.onOpenEdit?.(line.item.id);
  else handlers.onLoadSegment?.(line.item, line.video);
}

// A segment or edit under its clip or video
function subRow(line) {
  const loaded = p2Loaded();
  const thing = line.item;
  let icon;
  let title;
  let detail;
  let tip;
  let current;
  if (line.type === "edit") {
    const who = thing.userId === me?.id ? "you" : thing.createdBy ?? "teammate";
    icon = thing.vertical ? "device-mobile" : "monitor";
    title = thing.title || "Untitled edit";
    detail = `${thing.vertical ? "9:16 · " : ""}${who} · ${formatDuration(Math.round(thing.lengthSeconds ?? 0))}`;
    const others = (thing.vodIds ?? []).length > 1 ? ` · uses ${thing.vodIds.length} VODs` : "";
    tip = `${thing.vertical ? "Vertical (9:16) edit" : "Horizontal edit"}: ${title} · by ${who}${others}${thing.canEdit ? "" : thing.canCopy === false ? " (view only)" : " (view only: make your own copy to change it)"} · double-click to open`;
    current = Number(loaded?.editId) === thing.id;
  } else {
    const vodId = String(thing.vodId ?? line.video.vodId);
    const other = vodId !== line.home ? (handlers.vodDay?.(vodId) ?? "other stream") : null;
    icon = "brackets-square";
    title = `${thing.label || `Segment ${thing.position + 1}`}${other ? ` · ${other}` : ""}`;
    detail = formatDuration(Math.round(thing.keptSeconds ?? thing.endSeconds - thing.startSeconds));
    tip = `${title}${thing.removed ? " (deleted from its timeline, still in this video)" : ""} · double-click to load`;
    current = Number(loaded?.segmentId) === thing.markerId && !loaded?.editId;
  }
  const segEdits = line.type === "segment" ? segmentEdits(thing.markerId) : [];
  const node = el("div", { class: `lrow item sub${line.kid ? " kid" : ""}${line.deep ? " deep" : ""}${line.lone ? " lone" : ""}${current ? " current" : ""}`, role: "listitem", tabindex: "0", "aria-label": title, "data-key": line.key, title: current ? `Loaded in Player 2 · ${tip}` : tip },
    segEdits.length ? segmentCaret(thing.markerId, line.segKey ?? "seg") : null,
    el("sl-icon", { library: "ph", name: icon, class: "lkind" }),
    el("span", { class: "ltitle" }, title),
    el("span", { class: "llen" }, detail));
  const menu = () => (line.type === "edit" ? editMenu(thing) : segmentMenu(thing, line.video));
  node.addEventListener("click", () => select(line.key, { focus: false }));
  node.addEventListener("dblclick", () => subOpenLine(line));
  node.addEventListener("mouseenter", () => setHovered(line.key));
  node.addEventListener("focus", () => select(line.key, { focus: false }));
  node.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    openMenu(event.clientX, event.clientY, menu());
  });
  node.addEventListener("keydown", (event) => {
    if (event.target !== node) return;
    if (event.key === "Enter") subOpenLine(line);
    else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      moveSelection(event.key === "ArrowDown" ? 1 : -1);
    } else if (event.key === "ArrowRight" && segEdits.length) {
      event.preventDefault();
      toggleSub(line.segKey ?? "seg", { id: thing.markerId }, true);
      focusRow(line.key);
    } else if (event.key === "ArrowLeft" && segEdits.length && isSubOpen(line.segKey ?? "seg", { id: thing.markerId })) {
      event.preventDefault();
      toggleSub(line.segKey ?? "seg", { id: thing.markerId }, false);
      focusRow(line.key);
    } else if (event.key === "ArrowLeft") {
      // Back to its row, closed
      event.preventDefault();
      if (line.parent) {
        toggleSub(line.parent.type, line.parent.item, false);
        select(line.parent.key, { focus: false });
        focusRow(line.parent.key);
      }
    } else if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
      event.preventDefault();
      const rect = node.getBoundingClientRect();
      openMenu(rect.left + 24, rect.top + 24, menu());
    }
  });
  return node;
}

// Show what Player 2 has loaded in the library: its VOD opened to it (else its clip or video on its own tab), the line selected
export async function revealLoaded() {
  const loaded = p2Loaded();
  if (!loaded?.vodId) return;
  const vodId = String(loaded.vodId);
  const vod = mergedVods().find((v) => String(v.vodId) === vodId);
  let list = "vods";
  let parent = null;
  if (vod) {
    if (!state.open.has(vodId)) {
      state.open.add(vodId);
      await loadKids(vodId);
    }
    const kids = state.kids.get(vodId);
    if (loaded.entryId) parent = kids?.clips?.find((c) => c.id === Number(loaded.entryId)) && { type: "clip", item: kids.clips.find((c) => c.id === Number(loaded.entryId)) };
    else if (loaded.videoId) parent = kids?.videos?.find((v) => v.id === Number(loaded.videoId)) && { type: "video", item: kids.videos.find((v) => v.id === Number(loaded.videoId)) };
    else if (loaded.editId) {
      // An edit of a segment: the segment's line under the VOD, opened
      const editId = Number(loaded.editId);
      const segmentId = [...state.segEdits].find(([, edits]) => edits.some((e) => e.id === editId))?.[0] ?? null;
      if (segmentId) state.subOpen.add(subKeyOf("lseg", { id: segmentId }));
    }
  } else if (loaded.entryId && state.clips.some((c) => c.id === Number(loaded.entryId))) {
    list = "clips";
    parent = { type: "clip", item: state.clips.find((c) => c.id === Number(loaded.entryId)) };
  } else if (loaded.videoId && state.videos.some((v) => v.id === Number(loaded.videoId))) {
    list = "videos";
    parent = { type: "video", item: state.videos.find((v) => v.id === Number(loaded.videoId)) };
  } else {
    toast("Its VOD isn't in the list yet: scroll the VODs list down to load older ones.");
    return;
  }
  if (state.list !== list) handlers.onPickList?.(list);
  if (parent && (loaded.editId || loaded.segmentId)) {
    const key = subKeyOf(parent.type, parent.item);
    state.subOpen.add(key);
    if (!state.subs.has(key)) await loadSub(parent.type, parent.item);
  }
  render();
  // The loaded line: an edit or segment line, else the clip / video / VOD row
  const lines = rowsModel?.lines ?? state.cardLines ?? [];
  const index = lines.findIndex((line) => (loaded.editId ? line.type === "edit" && line.item.id === Number(loaded.editId)
    : loaded.segmentId ? line.type === "segment" && line.item.markerId === Number(loaded.segmentId)
    : loaded.entryId ? line.type === "clip" && line.item.id === Number(loaded.entryId)
    : loaded.videoId ? line.type === "video" && line.item.id === Number(loaded.videoId)
    : line.type === "vod" && String(line.item.vodId) === vodId));
  if (index < 0) return;
  const list$ = $("side-list");
  list$.scrollTop = Math.max(0, lineY(index) - list$.clientHeight / 3);
  if (state.view === "rows") drawWindow();
  select(lines[index].key);
}

// ---------- the date column on the list's right edge (badrfly 2026-11-21, replaces the month bar) ----------
// Each day's number sits in front of its first item and each month's name under its line, scrolling with the list. While a day's first
// item is above the top, a stamp at the top of the column keeps its month and day (pushed up by the next day). A click on a month, a day
// or the stamp opens the list of months to jump to.

const MONTH_H = 22;  // a month's line (editor.css .lmonth)
const STAMP_H = 28;

function railLines() {
  return rowsModel?.lines ?? state.cardLines ?? null;
}

function listHasMore() {
  return state.list === "vods" ? moreVods() : state.list === "clips" ? Boolean(state.clipsCursor) : Boolean(state.videosCursor);
}

// Where a line starts in the list (rows: from the model; cards: its node)
function lineY(index) {
  if (state.view === "rows") return rowsModel?.ys?.[index] ?? 0;
  const node = $("side-list").querySelector(`:scope > [data-line="${index}"]`);
  return node ? node.offsetTop - $("side-list").offsetTop : 0;
}

// The line at a y position (rows: binary search; cards: the last node starting above it)
function lineAtY(y) {
  if (state.view === "rows") return lineAt(y);
  const list = $("side-list");
  let index = 0;
  for (const node of list.querySelectorAll(":scope > [data-line]")) {
    if (node.offsetTop - list.offsetTop > y) break;
    index = Number(node.dataset.line);
  }
  return index;
}

// A month's line: across the whole width, its name in the date column
function monthRow(line, index) {
  const label = el("button", { class: "lmonth-label", type: "button", title: `${monthTitle(line)} · jump to a month` }, line.month);
  label.addEventListener("pointerdown", (event) => event.stopPropagation());
  label.addEventListener("click", (event) => openJumpMenu(event.currentTarget));
  return el("div", { class: `lmonth${index === 0 ? " first" : ""}`, role: "presentation", "data-line": String(index) }, label);
}

// The day's number in front of its first item
function dayNumber(line) {
  const node = el("button", { class: "lday", type: "button", tabindex: "-1", title: `${dayTitle(line.day)} · jump to a month` }, line.day.number);
  node.addEventListener("pointerdown", (event) => event.stopPropagation());
  node.addEventListener("click", (event) => {
    event.stopPropagation();
    openJumpMenu(event.currentTarget);
  });
  node.addEventListener("dblclick", (event) => event.stopPropagation());
  return node;
}

function renderRail(lines) {
  const list = $("side-list");
  const show = Boolean(lines?.length);
  list.classList.toggle("with-rail", show);
  const stamp = $("side-stamp");
  if (!show) stamp.hidden = true;
  else updateStamp();
}

let stampFrame = 0;
function scheduleStamp() {
  if (stampFrame) return;
  stampFrame = requestAnimationFrame(() => {
    stampFrame = 0;
    updateStamp();
  });
}

function updateStamp() {
  const stamp = $("side-stamp");
  const list = $("side-list");
  const lines = railLines();
  const hide = () => { stamp.hidden = true; };
  if (!lines?.length) return hide();
  const top = list.scrollTop;
  const at = lineAtY(top + 1);
  if (!lines[at] || isMark(lines[at])) return hide();
  // The day of the line at the top: its first item
  let first = at;
  while (first > 0 && !(lines[first].first && !lines[first].kid)) first--;
  if (!lines[first]?.first || lineY(first) >= top - 1) return hide();
  let month = first;
  while (month > 0 && !lines[month].month) month--;
  // The next day or month pushes it up
  let next = at + 1;
  while (next < lines.length && !isMark(lines[next])) next++;
  const offset = next < lines.length ? Math.min(0, lineY(next) - top - STAMP_H) : 0;
  stamp.hidden = false;
  stamp.style.transform = `translateY(${offset}px)`;
  stamp.style.right = `${list.offsetWidth - list.clientWidth}px`;
  stamp.title = `${dayTitle(lines[first].day)} · jump to a month`;
  stamp.replaceChildren(el("span", { class: "stamp-month" }, lines[month]?.month ?? ""), el("span", { class: "stamp-day" }, lines[first].day.number));
}

// Every loaded month (the one at the top checked), then "Load older" while the list has more
function openJumpMenu(anchor) {
  const lines = railLines();
  if (!lines) return;
  const current = (() => {
    let index = lineAtY($("side-list").scrollTop + 1);
    while (index > 0 && !lines[index].month) index--;
    return index;
  })();
  const items = [];
  lines.forEach((line, index) => {
    if (line.month) items.push({ icon: "calendar-blank", label: monthTitle(line), checked: index === current, run: () => jumpToLine(index) });
  });
  if (listHasMore()) {
    items.push({ divider: true }, { icon: "clock-counter-clockwise", label: "Load older", run: () => {
      const list = $("side-list");
      list.scrollTop = list.scrollHeight;
      loadMoreIfNear();
    } });
  }
  const r = anchor.getBoundingClientRect();
  openMenu(r.left, r.bottom + 2, items);
}

function jumpToLine(index) {
  $("side-list").scrollTop = lineY(index);
  updateStamp();
}

function itemKey(item, type = listType()) {
  return type === "vod" ? `vod:${item.vodId}` : `${type}:${item.id}`;
}

function specFor(item, type = listType()) {
  return type === "vod" ? vodSpec(item) : type === "clip" ? clipSpec(item) : videoSpec(item);
}

let rowsModel = null;  // { lines, nodes: Map key → { item, node } } of the rows view on screen
let windowFrame = 0;

function renderRows(lines) {
  const list = $("side-list");
  const old = rowsModel?.nodes ?? new Map();
  rowsModel = { lines, nodes: new Map() };
  // Rows of items that didn't change keep their node (focus and hover stay put)
  for (const line of lines) {
    const kept = old.get(line.key);
    if (kept && kept.item === line.item && kept.sig === lineSig(line, line === lines[0])) rowsModel.nodes.set(line.key, kept);
  }
  // Where each line starts: rows are ROW_H, the lines between days SEP_H, months MONTH_H
  const ys = new Array(lines.length + 1);
  ys[0] = 0;
  lines.forEach((line, i) => { ys[i + 1] = ys[i] + (line.month ? MONTH_H : line.head ? SEP_H : ROW_H); });
  rowsModel.ys = ys;
  let inner = list.querySelector(":scope > .vlist");
  if (!inner) {
    inner = el("div", { class: "vlist" });
    list.replaceChildren(inner);
  }
  const extra = listLoadingMore() ? ROW_H : 0;
  inner.style.height = `${ys[lines.length] + extra + 8}px`;
  drawWindow();
}

function scheduleWindow() {
  if (windowFrame) return;
  windowFrame = requestAnimationFrame(() => {
    windowFrame = 0;
    drawWindow();
  });
}

function drawWindow() {
  if (!rowsModel) return;
  const list = $("side-list");
  const inner = list.querySelector(":scope > .vlist");
  if (!inner) return;
  const { lines, nodes } = rowsModel;
  const top = list.scrollTop;
  const first = Math.max(0, lineAt(top) - ROW_OVERSCAN);
  const last = Math.min(lines.length, lineAt(top + list.clientHeight) + 1 + ROW_OVERSCAN);
  const wanted = [];
  for (let i = first; i < last; i++) {
    const line = lines[i];
    let entry = nodes.get(line.key);
    if (!entry) {
      entry = { item: line.item, sig: lineSig(line, i === 0),
        node: line.month ? monthRow(line, i) : line.head ? dayRow() : line.note ? kidNote(line) : line.sub ? subRow(line) : listRow(line) };
      nodes.set(line.key, entry);
    }
    entry.node.style.top = `${rowsModel.ys[i]}px`;
    entry.node.classList.toggle("sel", !isMark(line) && !line.note && state.selected[state.list] === line.key);
    wanted.push(entry.node);
  }
  if (listLoadingMore()) {
    const loading = el("div", { class: "lrow lrow-note faint" }, "Loading more…");
    loading.style.top = `${rowsModel.ys[lines.length]}px`;
    wanted.push(loading);
  }
  // Keep the focused row in the page even when it scrolled out of view (keyboard focus isn't lost)
  // A row drawn anew (its item changed) takes the focus over from its old node
  const focused = document.activeElement?.closest?.(".lrow");
  const replacement = focused && !wanted.includes(focused) && focused.dataset.key ? wanted.find((n) => n.dataset.key === focused.dataset.key) : null;
  if (focused && inner.contains(focused) && !wanted.includes(focused) && !replacement) wanted.push(focused);
  // Only rows that come or go are moved (rows are placed by top, so order doesn't matter): taking a focused row out and back loses its focus
  const keep = new Set(wanted);
  for (const child of [...inner.children]) if (!keep.has(child)) child.remove();
  for (const node of wanted) if (node.parentNode !== inner) inner.append(node);
  replacement?.focus({ preventScroll: true });
}

// The slightly thicker line between days (the day's number is in the date column)
function dayRow() {
  return el("div", { class: "lrow lsep", role: "presentation" }, el("i"));
}

// What a line shows that can change: a row drawn with an older one is made again
function lineSig(line, top) {
  if (line.month) return `${line.month}|${line.count}|${top}`;
  if (line.head || line.note) return line.note ?? "";
  if (line.sub) {
    const loaded = p2Loaded();
    const t = line.item;
    return `${t.title ?? t.label}|${t.lengthSeconds ?? t.keptSeconds}|${loaded?.editId}|${loaded?.segmentId}|${line.type === "segment" ? `${segmentEdits(t.markerId).length}|${isSubOpen(line.segKey ?? "seg", { id: t.markerId })}|${handlers.vodDay?.(String(t.vodId ?? line.video.vodId))}` : ""}`;
  }
  return `${rowSig(line.item, line.type)}|${line.type === "vod" ? "" : isSubOpen(line.type, line.item)}|${line.first}|${line.first ? `${line.day.number}|${line.day.count}|${line.day.label}` : ""}`;
}

function countLabel(n) {
  const noun = state.list === "vods" ? "VOD" : state.list === "clips" ? "clip" : "video";
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}

// The line at a y position in the rows view (binary search over the lines' starts)
function lineAt(y) {
  const ys = rowsModel?.ys;
  if (!ys || ys.length < 2) return 0;
  let lo = 0;
  let hi = ys.length - 2;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ys[mid] <= y) lo = mid;
    else hi = mid - 1;
  }
  return lo;
}

// Who made it: their initial on a color picked from their user ID (names are in the tooltip and the card)
function whoBadge(userId, name) {
  if (!name) return el("span", { class: "lwho empty" });
  const color = STREAM_COLORS[Math.abs(Number(userId) || hashText(name)) % STREAM_COLORS.length];
  return el("span", { class: "lwho", style: `background:${color}`, title: name }, name.trim().charAt(0).toUpperCase());
}

function hashText(text) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return h;
}

// What a row shows that can change on the same item (VOD work loads later, clip downloads finish)
function rowSig(item, type = listType()) {
  if (type === "vod") {
    const work = state.work.get(String(item.vodId));
    return `${marked("starred", type, item)}|${item.isLive}|${work ? workCount(work) : ""}|${hasKids(item)}|${state.open.has(String(item.vodId))}|${shownChannels().length > 1}|${state.cards.get(item.channel)?.profileImageUrl ?? ""}`;
  }
  return `${marked("starred", type, item)}|${item.title}|${item.status}|${item.generationStatus}|${item.color}|${item.clipLengthSeconds ?? item.lengthSeconds}|${item.teamStatusId}|${(item.tags ?? []).join()}`;
}

function listRow(line) {
  const spec = specFor(line.item, line.type);
  const r = spec.row;
  const node = el("div", { class: `lrow item${line.kid ? " kid" : ""}${line.first ? " first" : ""}`, role: "listitem", tabindex: "0", "aria-label": spec.title, "data-key": line.key },
    line.type === "vod" ? vodCaret(line.item) : itemCaret(line),
    // Under an opened VOD: the kind's icon in the item's color instead of the dot
    line.kid ? el("sl-icon", { library: "ph", name: line.type === "video" ? "film-strip" : "scissors", class: "lkind", style: `color:${r.color ?? CLIP_COLOR}` })
      : r.lead ?? el("span", { class: "ldot", style: `background:${r.color ?? CLIP_COLOR}` }),
    el("span", { class: "ltitle", title: spec.title }, spec.title),
    ...(r.tags ?? []).filter(Boolean),
    starButton(line.type, line.item),
    el("span", { class: "llen" }, r.length ?? ""),
    r.who ?? el("span", { class: "lwho empty" }),
    line.first && !line.kid && state.view === "rows" ? dayNumber(line) : null);
  wireItem(node, spec);
  node.addEventListener("click", () => select(line.key, { focus: false }));
  node.addEventListener("mouseenter", () => setHovered(line.key));
  node.addEventListener("focus", () => select(line.key, { focus: false }));
  node.addEventListener("keydown", (event) => {
    if (event.target !== node) return;
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      moveSelection(event.key === "ArrowDown" ? 1 : -1);
    }
    // * stars or unstars the row's item (keybinds.js rowStar)
    if (keybindFor("library", event) === "rowStar") {
      event.preventDefault();
      event.stopPropagation();
      toggleStar(line.type, line.item);
      return;
    }
    // 1–9 give the row's clip or video the team's status of that number, 0 takes it off (keybinds.js rowStatus)
    if (keybindFor("library", event) === "rowStatus" && (line.type === "clip" || line.type === "video") && state.canSetStatus) {
      const status = event.key === "0" ? null : state.statuses[Number(event.key) - 1];
      if (event.key === "0" || status) {
        event.preventDefault();
        setItemStatus(line.type, line.item, status?.id ?? null);
      }
      return;
    }
    // → opens a clip or video to its edits (and segments), ← closes it
    if ((line.type === "clip" || line.type === "video") && (event.key === "ArrowRight" || (event.key === "ArrowLeft" && isSubOpen(line.type, line.item)))) {
      event.preventDefault();
      toggleSub(line.type, line.item, event.key === "ArrowRight");
      focusRow(line.key);
      return;
    }
    // → opens a VOD, ← closes it (from one of its rows: back to the VOD)
    if ((event.key === "ArrowRight" || event.key === "ArrowLeft") && (line.type === "vod" || line.kid) && state.list === "vods") {
      event.preventDefault();
      const open = event.key === "ArrowRight";
      // The VOD's row is drawn again (its arrow turned): keep the keyboard on it
      if (line.type === "vod" && hasKids(line.item)) {
        toggleVod(line.item, open);
        focusRow(line.key);
      } else if (line.kid && !open) {
        const vod = mergedVods().find((v) => String(v.vodId) === line.vodId);
        if (vod) {
          toggleVod(vod, false);
          select(`vod:${line.vodId}`, { focus: false });
          focusRow(`vod:${line.vodId}`);
        }
      }
    }
  });
  return node;
}

function itemLines() {
  return (rowsModel?.lines ?? state.cardLines ?? []).filter((line) => !isMark(line) && !line.note);
}

function select(key, { focus = true } = {}) {
  if (state.selected[state.list] === key) return;
  state.selected[state.list] = key;
  for (const node of $("side-list").querySelectorAll(".lrow.item")) node.classList.toggle("sel", node.dataset.key === key);
  renderPeek();
  if (focus) focusRow(key);
}

function moveSelection(step) {
  const lines = rowsModel?.lines ?? [];
  let index = lines.findIndex((line) => line.key === state.selected[state.list]);
  do index += step; while (index >= 0 && index < lines.length && (isMark(lines[index]) || lines[index].note));
  if (index < 0 || index >= lines.length) return;
  const list = $("side-list");
  const y = rowsModel.ys[index];
  if (y < list.scrollTop + SEP_H) list.scrollTop = y - (lines[index - 1]?.month ? MONTH_H : SEP_H);
  else if (y + ROW_H > list.scrollTop + list.clientHeight) list.scrollTop = y + ROW_H - list.clientHeight;
  drawWindow();
  select(lines[index].key);
}

function focusRow(key) {
  const node = rowsModel?.nodes.get(key)?.node;
  node?.focus({ preventScroll: true });
}

function setHovered(key) {
  if (state.hovered === key) return;
  state.hovered = key;
  renderPeek();
}

// The rows view's card at the bottom: the row under the mouse, else the selected one
let peekKey = null;
function renderPeek() {
  const peek = $("side-peek");
  // Rows view: the row under the mouse, else the selected one. Cards view: only a row of an opened VOD under the mouse
  const lines = itemLines().filter((l) => state.view === "rows" || l.kid || l.sub);
  const found = lines.find((l) => l.key === state.hovered) ?? (state.view === "rows" ? lines.find((l) => l.key === state.selected[state.list]) : null);
  // A segment or edit line shows the card of its clip or video
  const line = found?.sub ? found.parent : found;
  peek.hidden = !line;
  if (!line) {
    peekKey = null;
    peek.replaceChildren();
    return;
  }
  if (peekKey === line.key && peek.firstChild?._item === line.item && peek.firstChild?._sig === rowSig(line.item, line.type)) return;
  peekKey = line.key;
  const node = card(specFor(line.item, line.type));
  node._sig = rowSig(line.item, line.type);
  node._item = line.item;
  peek.replaceChildren(node);
}

function viewKey() {
  return `clipco.library.view.${me?.id ?? "anon"}`;
}

function setView(view) {
  state.view = view;
  saveSetting(viewKey(), view);
  renderViewButton();
  $("side-list").scrollTop = 0;
  render();
}

function renderViewButton() {
  const button = $("side-view");
  const rows = state.view === "rows";
  button.name = rows ? "cards" : "rows";
  button.label = rows ? "Show as cards" : "Show as rows";
  if (button.parentElement?.tagName === "SL-TOOLTIP") button.parentElement.content = button.label;
}

// A card: thumbnail with badges, title, meta lines, and icon actions. item.kind decides where it can be dropped.
// thumb may be a list (first on top: see thumbStyle).
function card(spec) {
  const { title, thumb, badges = [], meta = [], actions = [], channel = null, stacked = false } = spec;
  const node = el("div", { class: "card", role: "listitem", tabindex: "0", "aria-label": title },
    el("div", { class: `card-thumb${stacked ? " stacked" : ""}`, style: thumbStyle(thumb) }, ...badges, cardChannel(channel)),
    el("div", { class: "card-body" },
      el("div", { class: "card-title", title }, title),
      ...meta.filter(Boolean).map((line) => (line instanceof Node ? line : el("div", { class: "card-meta" }, line))),
      el("div", { class: "card-actions" }, spec.star ? starButton(...spec.star) : null, ...actions)));
  wireItem(node, spec);
  return node;
}

// Drag, double-click / Enter to load, right-click / Menu key for the menu: the same on cards and rows
function wireItem(node, { kind, title, dragData, menu }) {
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

function vodSpec(vod) {
  const thumb = twitchThumb(vod.thumbnailUrl);
  const badges = [
    vod.isLive ? el("span", { class: "card-badge live" }, "LIVE") : null,
    !vod.isLive && vod.length ? el("span", { class: "card-badge num" }, vod.length) : null,
  ];
  const actions = [];
  // Saving audio: live or ended < 24 h ago and not saving yet, or a failed try (same rule as the old page)
  const canStart = !vod.rescueStatus || (vod.rescuePaused && vod.rescueStatus === "Grabbing");
  // The app Owner allows Save audio now per channel (badrfly 2026-11-18)
  const audioAllowed = channelByLogin(vod.channel)?.saveAudio !== false;
  if (can("SaveAudio") && audioAllowed && ((vod.unmuteWindowOpen || vod.isLive) && canStart || vod.rescueStatus === "Failed")) {
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
  if (hasKids(vod)) {
    const open = state.open.has(String(vod.vodId));
    actions.push(iconButton({ icon: open ? "caret-up" : "caret-down", label: open ? "Hide its videos and clips" : "Show its videos and clips", onClick: () => toggleVod(vod) }));
  }
  const item = { ...vod, kind: "vod" };
  const work = state.work.get(String(vod.vodId));
  const channel = channelByLogin(vod.channel);
  return {
    kind: "vod",
    title: vod.title,
    thumb,
    badges,
    meta: [[formatDate(vod.createdAtUtc, false), vod.game].filter(Boolean).join(" · "), work ? workLine(work) : null],
    actions,
    star: ["vod", vod],
    dragData: item,
    channel,
    row: {
      lead: shownChannels().length > 1 && channel ? channelPicture(channel, "lchan") : el("span", { class: "ldot vod" }),
      length: vod.isLive ? "" : vod.length ?? "",
      tags: [
        vod.isLive ? el("span", { class: "ltag live" }, "LIVE") : null,
        work ? el("span", { class: "ltag work", title: workText(work) }, el("sl-icon", { library: "ph", name: "stack" }), String(workCount(work))) : null,
      ],
    },
    menu: () => [
      { label: "Load in Player 1", icon: "broadcast", run: () => handlers.onLoad({ kind: "vod", data: item }) },
      { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "vod", data: item }) },
      hasKids(vod) && { label: state.open.has(String(vod.vodId)) ? "Hide its videos and clips" : "Show its videos and clips here", icon: "caret-down", run: () => toggleVod(vod) },
      work?.clips && { label: "Show its clips", icon: "scissors", run: () => showVodWork(vod.vodId, "clips") },
      work?.videos && { label: "Show its videos", icon: "film-strip", run: () => showVodWork(vod.vodId, "videos") },
      starMenuItem("vod", vod),
    ].filter(Boolean),
  };
}

const workCount = (work) => (work.clips || 0) + (work.videos || 0) + (work.edits || 0) || (work.segments || 0) + (work.moments || 0);
const workText = (work) => [["clip", work.clips], ["video", work.videos], ["edit", work.edits], ["segment", work.segments], ["moment", work.moments]]
  .filter(([, n]) => n).map(([word, n]) => `${n} ${word}${n === 1 ? "" : "s"}`).join(" · ");

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

// A reference clip over the stored-clip limit. Shorter references (teams that don't store clips, badrfly 2026-11-18) look like any clip
function isLongClip(entry) {
  if (!entry.isReferenceOnly) return false;
  const start = entry.startSeconds ?? 0;
  const end = entry.endSeconds ?? entry.vodLengthSeconds ?? 0;
  return end - start > (me?.maxStoredClipSeconds || 180) + 0.01;
}

function clipSpec(entry) {
  const badges = [
    el("span", { class: "card-badge num" }, formatDuration(entry.clipLengthSeconds)),
    entry.kind === "Clip" ? el("span", { class: "card-badge tag" }, "Twitch") : null,
    entry.kind !== "Clip" && isLongClip(entry) ? el("span", { class: "card-badge tag", title: "Longer than the stored-clip limit: not stored, made on demand" }, "Long") : null,
  ];
  const item = entry; // keeps entry.kind (Vod / Clip): the card's own kind is "clip"
  const problem = ["Failed", "VodUnavailable"].includes(entry.status);
  return {
    kind: "clip",
    title: entry.title,
    // A frame from the middle of the clip (Twitch clips: Twitch's own thumbnail)
    thumb: [entry.frameUrl, twitchThumb(entry.thumbnailUrl)],
    badges,
    meta: [
      `VOD ${formatDate(entry.streamDateUtc, false)}`,
      entry.addedBy ? `by ${entry.addedBy}` : null,
      clipStatus(entry),
      statusLine(entry),
    ],
    actions: [exportButton({ kind: "clip", data: item })],
    star: ["clip", entry],
    dragData: item,
    channel: channelByName(entry.channelName),
    row: {
      color: clipColor(entry.color),
      length: formatDuration(entry.clipLengthSeconds),
      who: whoBadge(entry.addedByUserId, entry.addedBy),
      tags: [
        entry.kind === "Clip" ? el("span", { class: "ltag" }, "Twitch") : null,
        entry.kind !== "Clip" && isLongClip(entry) ? el("span", { class: "ltag", title: "Longer than the stored-clip limit: not stored, made on demand" }, "Long") : null,
        statusPill(entry.teamStatusId),
        isBusy(entry) ? el("sl-icon", { library: "ph", name: "hourglass-medium", class: "lstate", title: "Downloading" }) : null,
        problem ? el("sl-icon", { library: "ph", name: "warning", class: "lstate error", title: entry.error || (entry.status === "Failed" ? "Download failed" : entry.kind === "Clip" ? "Clip deleted" : "VOD deleted") }) : null,
      ],
    },
    menu: () => clipMenu(item),
  };
}

function canOnEntry(entry, own, any) {
  return can(any) || (can(own) && entry.addedByUserId === String(me.id));
}

function clipMenu(entry) {
  return [
    { label: "Load in Player 2", icon: "film-strip", run: () => handlers.onLoad({ kind: "clip", data: entry }) },
    { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "clip", data: entry }) },
    starMenuItem("clip", entry),
    entry.vodId && String(entry.vodId) !== state.who.vodId && { label: "Show this VOD's clips", icon: "funnel-simple", run: () => showVodWork(entry.vodId, "clips") },
    canOnEntry(entry, "EditOwnEntries", "EditAnyEntries") && { label: "Rename", icon: "pencil-simple", run: () => renameClip(entry) },
    canOnEntry(entry, "DeleteOwnEntries", "DeleteAnyEntries") && { label: "Delete", icon: "trash", danger: true, run: () => deleteClip(entry) },
    ...statusMenu("clip", entry),
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

// --- videos: linked segments (length, the VOD's date, who made it); one video may span several streams ---


export function videoStreams(video) {
  return new Set((video.segments ?? []).map((s) => String(s.vodId ?? video.vodId))).size || 1;
}

function videoSpec(video) {
  const item = video;
  const streams = videoStreams(video);
  return {
    kind: "video",
    title: video.title,
    thumb: [video.frameUrl, twitchThumb(video.thumbnailUrl)],
    stacked: streams > 1,
    badges: [el("span", { class: "card-badge num" }, formatDuration(video.lengthSeconds ?? 0))],
    meta: [
      video.streamDateUtc ? `VOD ${formatDate(video.streamDateUtc, false)}${streams > 1 ? ` · ${streams} streams` : ""}` : streams > 1 ? `${streams} streams` : null,
      video.createdBy ? `by ${video.createdBy}` : null,
      statusLine(video),
    ],
    actions: [exportButton({ kind: "video", data: item })],
    star: ["video", video],
    dragData: item,
    channel: channelByName(video.channelName),
    row: {
      color: CLIP_COLOR,
      length: formatDuration(video.lengthSeconds ?? 0),
      who: whoBadge(video.userId, video.createdBy),
      tags: [streams > 1 ? el("span", { class: "ltag", title: `Plays from ${streams} streams` }, `${streams} streams`) : null, statusPill(video.teamStatusId)],
    },
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: () => handlers.onLoad({ kind: "video", data: item }) },
      { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "video", data: item }) },
      starMenuItem("video", item),
      String(video.vodId) !== state.who.vodId && { label: "Show this VOD's videos", icon: "funnel-simple", run: () => showVodWork(video.vodId, "videos") },
      video.canEdit && handlers.onAddSegments && { label: "Add segments…", icon: "link", run: () => handlers.onAddSegments(item) },
      video.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameVideo(item) },
      video.canDelete && { label: "Delete", icon: "trash", danger: true, run: () => deleteVideo(item) },
      ...statusMenu("video", item),
    ].filter(Boolean),
  };
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
  if (state.statusFilter || state.tagFilter || state.mark) return true;
  const day = Boolean(state.clipFilter.day || state.clipFilter.last24h);
  if (state.list === "vods") return Boolean(state.vodSearch) || state.vodType !== "archive" || state.onlyWorked || day;
  const who = Boolean(state.who.vodId || state.who.memberId);
  if (state.list === "clips") {
    const f = state.clipFilter;
    return Boolean(f.search || day || f.minSeconds != null || f.maxSeconds != null) || who;
  }
  return who || day || Boolean(state.clipFilter.search);
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
  $("filter-menu").addEventListener("sl-show", (event) => {
    if (event.target === $("filter-menu") && state.list !== "vods") loadFilterOptions();
  });
  $("filter-work").addEventListener("sl-change", (event) => {
    state.onlyWorked = event.target.checked;
    renderChips();
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
    if (event.target.checked !== state.clipFilter.last24h) setDay(event.target.checked ? "24h" : "");
  });
  $("filter-day").addEventListener("sl-change", (event) => {
    if (event.target.value !== state.clipFilter.day) setDay(event.target.value);
  });
  $("filter-today").addEventListener("click", () => setDay(todayIn(viewerTimeZone())));
  $("filter-clear").addEventListener("click", () => {
    // Everything the box and its chips hold, on every list
    searchInput().value = "";
    state.vodSearch = "";
    state.vodMatches = null;
    state.onlyWorked = false;
    if (state.vodType !== "archive") {
      state.vodType = "archive";
      saveSetting("clipco.library.vodType", "archive");
      reloadVods();
    }
    Object.assign(state.clipFilter, { search: "", last24h: false, day: "", minSeconds: null, maxSeconds: null });
    Object.assign(state.who, { vodId: "", memberId: "" });
    state.statusFilter = "";
    state.tagFilter = "";
    state.statusVods = null;
    state.mark = "";
    renderStatusBar();
    if (state.clipsLoaded || state.list === "clips") loadClips();
    if (state.videosLoaded || state.list === "videos") loadVideos();
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
  // The day applies to every list now (the search box's chip)
  $("filter-date-row").hidden = false;
  renderChips();
}

// ---------- the search box (badrfly 2026-11-20, "Redesign the library for large teams" step 2) ----------
// One box over the three lists. Plain words search on the server: clip titles, video titles and their segments' labels, and on the VODs
// tab the loaded titles plus every VOD whose clips, segments, moments or videos match (GET /api/vods/matches). Typed shortcuts become
// chips under the box, so what filters the list is always in sight: by:sam (made by), vod:… (one stream), @arky (the channel bar),
// nov 14 / today / yesterday / 24h (a day), >1m / <30s (clip length). The suggestions offer them as you type.

const MONTHS = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
const PREFIXED = /^(@|#|by:|vod:|[<>])/i;
const search = { items: [], active: -1, matches: null, matchesFor: "", timer: 0, optionsAt: 0 };

function searchInput() {
  return $("search-input");
}

export function focusLibrarySearch() {
  const input = searchInput();
  input.focus();
  input.select();
}

// "2026-11-14" for "nov 14", "14 nov", "november 14 2025", "2026-11-14", "today", "yesterday"; "24h" for the last 24 hours; else null
function parseDay(text) {
  const t = text.trim().toLowerCase().replace(/,/g, " ").replace(/\s+/g, " ");
  if (!t) return null;
  const zone = viewerTimeZone();
  const today = todayIn(zone);
  const iso = (y, m, d) => `${y}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;
  if (t === "today") return today;
  if (t === "yesterday") {
    const [y, m, d] = today.split("-").map(Number);
    const at = new Date(Date.UTC(y, m - 1, d - 1));
    return iso(at.getUTCFullYear(), at.getUTCMonth() + 1, at.getUTCDate());
  }
  if (t === "24h" || t === "last 24h") return "24h";
  let m = t.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/);
  let year;
  let month;
  let day;
  if (m) [year, month, day] = [Number(m[1]), Number(m[2]), Number(m[3])];
  else {
    const monthOf = (word) => (word.length >= 3 ? MONTHS.findIndex((name) => word.startsWith(name)) + 1 : 0);
    m = t.match(/^([a-z]+)\.? (\d{1,2})(?: (\d{4}))?$/);
    if (m && monthOf(m[1])) [month, day, year] = [monthOf(m[1]), Number(m[2]), m[3] ? Number(m[3]) : null];
    else {
      m = t.match(/^(\d{1,2}) ([a-z]+)\.?(?: (\d{4}))?$/);
      if (!m || !monthOf(m[2])) return null;
      [day, month, year] = [Number(m[1]), monthOf(m[2]), m[3] ? Number(m[3]) : null];
    }
    // No year: the last time that date came round
    if (!year) {
      year = Number(today.slice(0, 4));
      if (iso(year, month, day) > today) year--;
    }
  }
  if (month < 1 || month > 12 || day < 1 || day > new Date(Date.UTC(year, month, 0)).getUTCDate()) return null;
  return iso(year, month, day);
}

function dayChipLabel(day) {
  if (day === "24h") return "Last 24 hours";
  const today = todayIn(viewerTimeZone());
  if (day === today) return "Today";
  if (day === parseDay("yesterday")) return "Yesterday";
  const [y, m, d] = day.split("-").map(Number);
  const withYear = y !== Number(today.slice(0, 4));
  return new Intl.DateTimeFormat(undefined, { timeZone: "UTC", weekday: "short", month: "short", day: "numeric", ...(withYear ? { year: "numeric" } : {}) })
    .format(new Date(Date.UTC(y, m - 1, d)));
}

// ">1m" → { minSeconds: 60 }, "<30s" → { maxSeconds: 30 } (no unit = seconds)
function parseLength(token) {
  const m = token.trim().toLowerCase().match(/^([<>])=?(\d+(?:\.\d+)?)(s|sec|m|min|h)?$/);
  if (!m) return null;
  const seconds = Number(m[2]) * ({ m: 60, min: 60, h: 3600 }[m[3]] ?? 1);
  return m[1] === ">" ? { minSeconds: seconds } : { maxSeconds: seconds };
}

function lengthText(seconds) {
  return seconds >= 60 && seconds % 60 === 0 ? `${seconds / 60} min` : seconds >= 60 ? formatDuration(seconds) : `${seconds} s`;
}

// The words typed that aren't a shortcut: what's searched
function searchText() {
  return searchInput().value.split(/\s+/).filter((word) => word && !PREFIXED.test(word)).join(" ").trim();
}

function sameText(a, b) {
  return a.toLowerCase().includes(b.toLowerCase());
}

function memberChoices() {
  const members = [...state.members].map(([id, name]) => ({ id, name }));
  if (me && !members.some((m) => m.id === String(me.id))) members.push({ id: String(me.id), name: me.username });
  return members.sort((a, b) => a.name.localeCompare(b.name));
}

// What the box offers for the words before the caret (the last word, or the last two for a date like "nov 14")
function buildSuggestions() {
  const input = searchInput();
  const before = input.value.slice(0, input.selectionStart ?? input.value.length);
  const words = before.split(/\s+/).filter(Boolean);
  const token = before.endsWith(" ") ? "" : (words[words.length - 1] ?? "");
  const lower = token.toLowerCase();
  const items = [];
  const add = (item) => { if (items.length < 8) items.push(item); };
  if (lower.startsWith("@")) {
    const rest = lower.slice(1);
    for (const c of state.channels) if (c.login.startsWith(rest) || (c.displayName ?? "").toLowerCase().startsWith(rest))
      add({ kind: "channel", login: c.login, label: c.displayName || c.login, hint: "Channel", consume: 1 });
  } else if (lower.startsWith("by:")) {
    const rest = lower.slice(3);
    for (const m of memberChoices()) if (!rest || m.name.toLowerCase().startsWith(rest) || (rest === "me" && m.id === String(me?.id))) {
      add({ kind: "member", id: m.id, mode: "made", label: `Made by ${m.name}`, hint: m.id === String(me?.id) ? "You" : "Member", consume: 1 });
      add({ kind: "member", id: m.id, mode: "edited", label: `Edited by ${m.name}`, hint: m.id === String(me?.id) ? "You" : "Member", consume: 1 });
    }
  } else if (lower.startsWith("vod:")) {
    const rest = token.slice(4);
    for (const w of state.workedVods) if (!rest || sameText(vodOptionLabel(w), rest)) add({ kind: "vod", vodId: String(w.vodId), label: vodOptionLabel(w), hint: "Stream", consume: 1 });
  } else if (lower.startsWith("#")) {
    const rest = lower.slice(1);
    for (const tag of state.tags) if (tag.name.startsWith(rest)) add({ kind: "tag", name: tag.name, label: `#${tag.name}`, hint: `Tag · ${tag.clips + tag.videos}`, consume: 1 });
  } else if (lower.startsWith(">") || lower.startsWith("<")) {
    const length = parseLength(lower);
    if (length) add({ kind: "length", ...length, label: length.minSeconds != null ? `Longer than ${lengthText(length.minSeconds)}` : `Up to ${lengthText(length.maxSeconds)}`, hint: "Clips", consume: 1 });
  } else if (token) {
    // A date of one or two words ("today", "nov 14", "14 nov")
    for (const n of [3, 2, 1]) {
      if (words.length < n) continue;
      const day = parseDay(words.slice(-n).join(" "));
      if (day) {
        add({ kind: "day", day, label: dayChipLabel(day), hint: "Day", consume: n });
        break;
      }
    }
    if (lower.length >= 2) {
      // A step of the team's work ("ready")
      for (const n of [2, 1]) {
        if (words.length < n) continue;
        const said = words.slice(-n).join(" ").toLowerCase();
        const status = state.statuses.find((x) => x.name.toLowerCase().startsWith(said));
        if (status) {
          add({ kind: "status", id: String(status.id), label: status.name, hint: "Status", consume: n });
          break;
        }
      }
      for (const c of state.channels) if (several() && (c.login.startsWith(lower) || (c.displayName ?? "").toLowerCase().startsWith(lower)))
        add({ kind: "channel", login: c.login, label: c.displayName || c.login, hint: "Channel", consume: 1 });
      for (const m of memberChoices()) if (m.name.toLowerCase().startsWith(lower)) add({ kind: "member", id: m.id, mode: "made", label: `Made by ${m.name}`, hint: m.id === String(me?.id) ? "You" : "Member", consume: 1 });
      let streams = 0;
      for (const w of state.workedVods) if (streams < 3 && sameText(vodOptionLabel(w), token)) {
        streams++;
        add({ kind: "vod", vodId: String(w.vodId), label: vodOptionLabel(w), hint: "Stream", consume: 1 });
      }
    }
  }
  const text = searchText();
  if (text) items.push({ kind: "text", label: `Search “${text}”`, hint: matchesHint(text) });
  return items;
}

// "Also in 12 segment labels · 3 video titles" for the words searched (GET /api/vods/matches)
function matchesHint(text) {
  const m = search.matchesFor === text ? search.matches : null;
  if (!m) return "";
  const parts = [[m.clips, "clip title"], [m.videos, "video title"], [m.segments, "segment label"], [m.moments, "moment label"]]
    .filter(([n]) => n).map(([n, what]) => `${n} ${what}${n === 1 ? "" : "s"}`);
  return parts.length ? `in ${parts.join(" · ")}` : "nothing in titles or labels";
}

async function loadMatches(text) {
  if (!text || state.demo) {
    search.matches = null;
    search.matchesFor = text;
    state.vodMatches = null;
    return;
  }
  try {
    const found = await api("GET", `/api/vods/matches?q=${encodeURIComponent(text)}`);
    if (searchText() !== text) return;
    search.matches = found;
    search.matchesFor = text;
    state.vodMatches = new Set(found.vodIds.map(String));
  } catch {
    return;
  }
  if (state.list === "vods") render();
  if (!$("search-suggest").hidden) updateSuggestions();
}

function renderSuggestions() {
  const box = $("search-suggest");
  if (!search.items.length || document.activeElement !== searchInput()) {
    box.hidden = true;
    searchInput().setAttribute("aria-expanded", "false");
    return;
  }
  box.replaceChildren(...search.items.map((item, i) => {
    const node = el("div", { class: `sugg${i === search.active ? " on" : ""}${item.kind === "text" ? " text" : ""}`, role: "option", "aria-selected": String(i === search.active), id: `sugg-${i}` },
      el("span", { class: "sugg-label" }, item.label), el("span", { class: "sugg-hint" }, item.hint ?? ""));
    node.addEventListener("pointerdown", (event) => {
      event.preventDefault(); // keep the box focused
      pickSuggestion(item);
    });
    return node;
  }));
  box.hidden = false;
  searchInput().setAttribute("aria-expanded", "true");
  searchInput().setAttribute("aria-activedescendant", search.active >= 0 ? `sugg-${search.active}` : "");
}

function updateSuggestions() {
  search.items = buildSuggestions();
  // A shortcut being typed: Enter takes the first suggestion; plain words: Enter just searches
  const input = searchInput();
  const before = input.value.slice(0, input.selectionStart ?? input.value.length);
  const token = before.split(/\s+/).pop() ?? "";
  search.active = PREFIXED.test(token) && search.items.length && search.items[0].kind !== "text" ? 0 : -1;
  renderSuggestions();
}

// Takes the words a suggestion stood for out of the box (the ones before the caret)
function consumeWords(n) {
  const input = searchInput();
  const caret = input.selectionStart ?? input.value.length;
  let before = input.value.slice(0, caret).replace(/\s+$/, "");
  for (let i = 0; i < n; i++) before = before.replace(/\S+\s*$/, "");
  const after = input.value.slice(caret).replace(/^\s+/, "");
  input.value = (before.trimEnd() + (before.trim() && after ? " " : "") + after).replace(/^\s+/, "");
  const at = before.trimEnd().length + (before.trim() ? 1 : 0);
  input.setSelectionRange(Math.min(at, input.value.length), Math.min(at, input.value.length));
  if (input.value && !input.value.endsWith(" ") && at >= input.value.length) input.value += " ";
}

function pickSuggestion(item) {
  if (item.kind !== "text") consumeWords(item.consume ?? 1);
  switch (item.kind) {
    case "channel":
      pickOnlyChannel(item.login);
      break;
    case "member":
      Object.assign(state.who, { memberId: item.id, mode: item.mode });
      reloadWho();
      break;
    case "vod":
      state.who.vodId = item.vodId;
      reloadWho();
      break;
    case "day":
      setDay(item.day);
      break;
    case "length":
      Object.assign(state.clipFilter, { minSeconds: item.minSeconds ?? null, maxSeconds: item.maxSeconds ?? null });
      loadClipsIfShown();
      break;
    case "status":
      setStatusFilter(item.id);
      break;
    case "tag":
      setTagFilter(item.name);
      break;
    default:
      break;
  }
  applySearchText();
  // Picked: the suggestions close so the chips under the box show (typing opens them again)
  search.items = [];
  renderSuggestions();
  renderChips();
}

// @channel: only that channel on in the channel bar
function pickOnlyChannel(login) {
  if (!several()) return;
  state.picked = new Set([login]);
  renderChannelBar();
  showChannelVods();
  if (state.demo) return render();
  if (state.clipsLoaded || state.list === "clips") loadClips();
  if (state.videosLoaded || state.list === "videos") loadVideos();
}

function setDay(day) {
  state.clipFilter.last24h = day === "24h";
  state.clipFilter.day = day === "24h" ? "" : day ?? "";
  renderFilter();
  reloadDated();
}

// A day applies to clips (on their sort date), videos (made) and VODs (streamed)
function reloadDated() {
  loadClipsIfShown();
  if (state.list === "videos" || state.videosLoaded) loadVideos();
  if (state.list === "vods") render();
}

function loadClipsIfShown() {
  if (state.list === "clips" || state.clipsLoaded) loadClips();
}

// The from / to of the day chip, or null
function dayBounds() {
  const f = state.clipFilter;
  if (f.last24h) return { from: new Date(Date.now() - 86400_000).toISOString(), to: null };
  if (!f.day) return null;
  const [year, month, day] = f.day.split("-").map(Number);
  return dayRange({ year, month, day }, viewerTimeZone());
}

// The words typed (not shortcuts) search every list: clips and videos on the server, VODs by title and by their work's matches
function applySearchText() {
  const text = searchText();
  if (text === state.clipFilter.search) return;
  state.clipFilter.search = text;
  state.vodSearch = text;
  state.vodMatches = null;
  loadMatches(text);
  if (state.list === "vods") render();
  loadClipsIfShown();
  if (state.list === "videos" || state.videosLoaded) loadVideos();
}

// Enter with no suggestion picked: every shortcut that names exactly one thing becomes its chip; the rest stays in the box
function applyTypedShortcuts() {
  for (const word of searchInput().value.split(/\s+/).filter((w) => PREFIXED.test(w))) {
    const lower = word.toLowerCase();
    let item = null;
    if (lower.startsWith("@")) {
      const c = state.channels.find((x) => x.login === lower.slice(1) || (x.displayName ?? "").toLowerCase() === lower.slice(1));
      if (c) item = { kind: "channel", login: c.login };
    } else if (lower.startsWith("by:")) {
      const name = lower.slice(3);
      const found = name === "me" ? memberChoices().filter((m) => m.id === String(me?.id)) : memberChoices().filter((m) => m.name.toLowerCase() === name);
      if (found.length === 1) item = { kind: "member", id: found[0].id, mode: "made" };
    } else if (lower.startsWith("vod:")) {
      const found = state.workedVods.filter((w) => sameText(vodOptionLabel(w), word.slice(4)));
      if (found.length === 1) item = { kind: "vod", vodId: String(found[0].vodId) };
    } else if (lower.startsWith("#")) {
      const tag = state.tags.find((t) => t.name === lower.slice(1));
      if (tag) item = { kind: "tag", name: tag.name };
    } else {
      const length = parseLength(lower);
      if (length) item = { kind: "length", ...length };
    }
    if (!item) continue;
    const input = searchInput();
    input.value = input.value.split(/\s+/).filter((w) => w !== word).join(" ");
    pickSuggestion({ ...item, consume: 0 });
  }
}

function renderChips() {
  const chips = [];
  const lists = state.list;
  const chip = (label, onRemove, appliesHere, where) => {
    const remove = el("button", { type: "button", class: "schip-x", "aria-label": `Remove ${label}` }, "×");
    remove.addEventListener("click", () => {
      onRemove();
      renderChips();
      searchInput().focus();
    });
    chips.push(el("span", { class: `schip${appliesHere ? "" : " off"}`, title: appliesHere ? "" : `Only on ${where}` }, label, remove));
  };
  const clipsOrVideos = lists !== "vods";
  if (state.who.vodId) {
    const work = state.workedVods.find((w) => String(w.vodId) === state.who.vodId) ?? { vodId: state.who.vodId };
    chip(vodOptionLabel(work), () => { state.who.vodId = ""; reloadWho(); }, clipsOrVideos, "Clips and Videos");
  }
  if (state.who.memberId) {
    const name = state.members.get(state.who.memberId) ?? (state.who.memberId === String(me?.id) ? me.username : "someone");
    chip(`${state.who.mode === "edited" ? "Edited by" : "By"} ${name}`, () => { state.who.memberId = ""; reloadWho(); }, clipsOrVideos, "Clips and Videos");
  }
  const f = state.clipFilter;
  if (f.day || f.last24h) chip(dayChipLabel(f.last24h ? "24h" : f.day), () => setDay(""), true, "");
  if (f.minSeconds != null || f.maxSeconds != null) {
    chip(f.minSeconds != null ? `Longer than ${lengthText(f.minSeconds)}` : `Up to ${lengthText(f.maxSeconds)}`, () => {
      Object.assign(state.clipFilter, { minSeconds: null, maxSeconds: null });
      loadClipsIfShown();
    }, lists === "clips", "Clips");
  }
  if (state.tagFilter) chip(`#${state.tagFilter}`, () => setTagFilter(""), true, "");
  if (state.statusFilter) {
    const status = state.statusFilter === "none" ? { name: "No status" } : statusById(state.statusFilter);
    chip(status?.name ?? "Status", () => setStatusFilter(""), true, "");
  }
  if (state.onlyWorked) chip("With work", () => { state.onlyWorked = false; renderFilter(); render(); }, lists === "vods", "VODs");
  if (state.vodType !== "archive") chip(state.vodType === "highlight" ? "Highlights" : "Uploads", () => {
    state.vodType = "archive";
    saveSetting("clipco.library.vodType", "archive");
    renderFilter();
    reloadVods();
  }, lists === "vods", "VODs");
  const box = $("search-chips");
  box.replaceChildren(...chips);
  box.hidden = !chips.length;
}

function setUpSearch() {
  const input = searchInput();
  input.addEventListener("input", () => {
    updateSuggestions();
    clearTimeout(search.timer);
    search.timer = setTimeout(applySearchText, 250);
  });
  input.addEventListener("focus", () => {
    // Members and streams to suggest (once a minute at most)
    if (Date.now() - search.optionsAt > 60_000 && !state.demo) {
      search.optionsAt = Date.now();
      loadFilterOptions().then(() => { if (document.activeElement === input) updateSuggestions(); });
    }
    updateSuggestions();
  });
  input.addEventListener("blur", () => {
    search.items = [];
    renderSuggestions();
  });
  input.addEventListener("click", () => updateSuggestions());
  input.addEventListener("keydown", (event) => {
    const id = keybindFor("search", event);
    const open = !$("search-suggest").hidden && search.items.length;
    if (id === "searchMove" && open) {
      event.preventDefault();
      const step = event.key === "ArrowDown" ? 1 : -1;
      search.active = (search.active + step + search.items.length + 1) % (search.items.length + 1);
      if (search.active === search.items.length) search.active = -1;
      renderSuggestions();
    } else if (id === "searchPick") {
      if (event.key === "Tab" && !(open && search.active >= 0)) return;
      event.preventDefault();
      clearTimeout(search.timer);
      if (open && search.active >= 0) pickSuggestion(search.items[search.active]);
      else {
        applyTypedShortcuts();
        applySearchText();
        search.items = [];
        renderSuggestions();
      }
    } else if (id === "searchClose") {
      event.preventDefault();
      if (open) {
        search.items = [];
        renderSuggestions();
      } else if (input.value) {
        input.value = "";
        applySearchText();
      } else input.blur();
    } else if (id === "searchChipBack" && !input.value && input.selectionStart === 0) {
      // Backspace in an empty box takes off the last chip
      const last = [...$("search-chips").querySelectorAll(".schip-x")].pop();
      if (last) {
        event.preventDefault();
        last.click();
      }
    }
  });
  renderChips();
}

// ---------- the team's statuses and tags (badrfly 2026-11-20, "Redesign the library for large teams" step 4) ----------
// The team owner (or a role with Manage statuses) makes the steps on the Team page; members set one on a clip or video from its menu or with
// 1–9 on its row, and tags with "Tags…". The status row under the search box filters every list by one (counts: clips on CLIPS, videos on
// VIDEOS, both on VODs, which keeps the streams that have some). "#tag" in the search box filters by a tag.

let statusesTimer = 0;
async function loadStatuses() {
  if (state.demo) return;
  try {
    const data = await api("GET", "/api/team/statuses");
    state.statuses = data.statuses;
    state.tags = data.tags;
    state.noStatus = { clips: data.noStatusClips, videos: data.noStatusVideos };
    state.canSetStatus = data.canSet;
  } catch {
    // The library works without them
  }
  renderStatusBar();
  render();
}

function scheduleStatuses() {
  clearTimeout(statusesTimer);
  statusesTimer = setTimeout(loadStatuses, 400);
}

function statusById(id) {
  return state.statuses.find((s) => String(s.id) === String(id)) ?? null;
}

function statusColor(status) {
  return MARKER_COLORS[status?.color] ?? MARKER_COLORS.gray;
}

// The small status label on a row
function statusPill(statusId) {
  const status = statusId ? statusById(statusId) : null;
  if (!status) return null;
  return el("span", { class: "lpill", title: `Status: ${status.name}` }, el("i", { style: `background:${statusColor(status)}` }), status.name);
}

// "● Editing · #boxing #collab" on a card
function statusLine(item) {
  const status = item.teamStatusId ? statusById(item.teamStatusId) : null;
  const tags = item.tags ?? [];
  if (!status && !tags.length) return null;
  return el("div", { class: "card-meta card-status" },
    status ? el("span", { class: "lpill" }, el("i", { style: `background:${statusColor(status)}` }), status.name) : null,
    ...tags.map((tag) => el("span", { class: "card-tag" }, `#${tag}`)));
}

function statusCount(status) {
  if (state.list === "clips") return status.clips;
  if (state.list === "videos") return status.videos;
  return status.clips + status.videos;
}

function renderStatusBar() {
  const bar = $("status-bar");
  if (!bar) return;
  bar.hidden = state.demo;
  if (bar.hidden) return;
  const chip = (value, label, color, count, title) => {
    const on = state.statusFilter === value;
    const node = el("button", { type: "button", class: `sbar-chip${on ? " on" : ""}`, "aria-pressed": String(on), title },
      color ? el("i", { style: `background:${color}` }) : null, label, el("span", { class: "sbar-n" }, count.toLocaleString()));
    node.addEventListener("click", () => setStatusFilter(on ? "" : value));
    return node;
  };
  const none = state.noStatus ? (state.list === "clips" ? state.noStatus.clips : state.list === "videos" ? state.noStatus.videos : state.noStatus.clips + state.noStatus.videos) : 0;
  bar.replaceChildren(
    ...quickChips(),
    state.statuses.length ? el("span", { class: "sbar-gap", "aria-hidden": "true" }) : "",
    ...state.statuses.map((s) => chip(String(s.id), s.name, statusColor(s), statusCount(s), s.hideFromLists ? "Hidden from lists unless picked" : "")),
    none ? chip("none", "No status", null, none, "") : "");
  // Fades out on the right while more chips are hidden there
  const fade = () => bar.classList.toggle("more", bar.scrollWidth - bar.scrollLeft > bar.clientWidth + 2);
  requestAnimationFrame(fade);
  bar.onscroll = fade;
  // Its scrollbar is hidden, so a mouse wheel scrolls it sideways
  bar.onwheel = (event) => {
    if (bar.scrollWidth <= bar.clientWidth || Math.abs(event.deltaX) > Math.abs(event.deltaY)) return;
    event.preventDefault();
    bar.scrollLeft += event.deltaY;
  };
}

function setStatusFilter(value) {
  state.statusFilter = value ? String(value) : "";
  applyStatusFilters();
}

function setTagFilter(name) {
  state.tagFilter = name ?? "";
  applyStatusFilters();
}

// The status / tag filter changed: every list follows (VODs: the streams that have such clips or videos)
function applyStatusFilters() {
  renderStatusBar();
  renderChips();
  renderFilter();
  if (state.clipsLoaded || state.list === "clips") loadClips();
  if (state.videosLoaded || state.list === "videos") loadVideos();
  for (const id of [...state.kids.keys()]) if (!state.open.has(id)) state.kids.delete(id);
  for (const id of state.open) loadKids(id);
  loadStatusVods();
}

let statusVodsSeq = 0;
async function loadStatusVods() {
  const seq = ++statusVodsSeq;
  if ((!state.statusFilter && !state.tagFilter) || state.demo) {
    state.statusVods = null;
  } else {
    const query = statusQuery(new URLSearchParams());
    try {
      const found = await api("GET", `/api/vods/matches?${query}`);
      if (seq !== statusVodsSeq) return;
      state.statusVods = new Set(found.vodIds.map(String));
    } catch {
      if (seq !== statusVodsSeq) return;
      state.statusVods = null;
    }
  }
  if (state.list === "vods") render();
}

function statusQuery(query) {
  if (state.statusFilter) query.set("status", state.statusFilter);
  if (state.tagFilter) query.set("tag", state.tagFilter);
  return query;
}

// The statuses and Tags… at the end of a clip's or video's menu
function statusMenu(kind, item) {
  if (!state.canSetStatus || !state.statuses.length) return [];
  return [
    { divider: true },
    ...state.statuses.map((s, i) => ({
      label: s.name, swatch: statusColor(s), checked: item.teamStatusId === s.id, hint: i < 9 ? String(i + 1) : "",
      run: () => setItemStatus(kind, item, s.id),
    })),
    item.teamStatusId && { label: "No status", icon: "x", hint: "0", run: () => setItemStatus(kind, item, null) },
    { label: "Tags…", icon: "tag", run: () => editTags(kind, item) },
  ].filter(Boolean);
}

export async function setItemStatus(kind, item, statusId) {
  if ((item.teamStatusId ?? null) === statusId) return;
  try {
    await api("PUT", `/api/${kind === "clip" ? "entries" : "videos"}/${item.id}/status`, { statusId });
    item.teamStatusId = statusId;
    const status = statusId ? statusById(statusId) : null;
    toast(status ? `"${item.title}" is now ${status.name}.` : `Took the status off "${item.title}".`);
    render();
    renderPeekAgain();
    scheduleStatuses();
  } catch (error) {
    toast(error.message, "error");
  }
}

async function editTags(kind, item) {
  const text = await askText({
    heading: "Tags", label: "Tags, separated by spaces (e.g. #boxing #collab)", value: (item.tags ?? []).map((t) => `#${t}`).join(" "),
    confirm: "Save", maxLength: 400,
  });
  if (text === null) return;
  const tags = text.split(/[\s,]+/).map((t) => t.replace(/^#+/, "")).filter(Boolean);
  try {
    const result = await api("PUT", `/api/${kind === "clip" ? "entries" : "videos"}/${item.id}/tags`, { tags });
    item.tags = result.tags;
    render();
    renderPeekAgain();
    scheduleStatuses();
  } catch (error) {
    toast(error.message, "error");
  }
}

// The bottom card shows the same item changed in place: draw it again
function renderPeekAgain() {
  peekKey = null;
  renderPeek();
}

// ---------- the member's own stars, Recent and Mine (badrfly 2026-11-20, "Redesign the library for large teams" step 5) ----------
// ★ on a row (shown on hover, filled when on), on a card and in the menus (or * on a row) stars a VOD, clip or video for this member only
// (PUT / DELETE /api/my/stars/{kind}/{id}). Loading one in a player puts it in their Recent (POST /api/my/opened, from editor.js). The
// chips at the front of the status row filter the open list by them; Mine = clips / videos they made (the member filter's "made by").

const MARK_LISTS = { vod: "vods", clip: "clips", video: "videos" };
const markId = (type, item) => String(type === "vod" ? item.vodId : item.id);

async function loadMarks() {
  if (state.demo) return;
  try {
    const data = await api("GET", "/api/my/marks");
    const sets = (group) => ({ clips: group.clips.map(String), videos: group.videos.map(String), vods: group.vods.map(String) });
    state.marks = { starred: sets(data.starred), recent: sets(data.recent) };
  } catch {
    return;
  }
  renderStatusBar();
  render();
}

// Is the item starred / recently opened by this member (type: vod, clip or video)
function marked(mark, type, item) {
  const list = MARK_LISTS[type];
  return Boolean(list && item && state.marks[mark]?.[list]?.includes(markId(type, item)));
}

// Stars and Recent are the member's own: a support visit has no member (and is read-only)
function starButton(type, item) {
  if (!MARK_LISTS[type] || state.demo || me?.support) return null;
  const on = marked("starred", type, item);
  const button = el("button", {
    type: "button", class: `lstar${on ? " on" : ""}`, tabindex: "-1", "aria-pressed": String(on),
    "aria-label": on ? "Unstar" : "Star", title: `${on ? "Unstar" : "Star"} (${keybindLabel("rowStar")})`,
  }, el("sl-icon", { library: "ph", name: on ? "star-fill" : "star" }));
  button.addEventListener("pointerdown", (event) => event.stopPropagation());
  button.addEventListener("dblclick", (event) => event.stopPropagation());
  button.addEventListener("click", (event) => {
    event.stopPropagation();
    toggleStar(type, item);
  });
  return button;
}

function starMenuItem(type, item) {
  if (state.demo || me?.support) return null;
  const on = marked("starred", type, item);
  return { label: on ? "Unstar" : "Star", icon: on ? "star-fill" : "star", hint: keybindLabel("rowStar"), run: () => toggleStar(type, item) };
}

export async function toggleStar(type, item) {
  const list = MARK_LISTS[type];
  if (!list || state.demo || me?.support) return;
  const id = markId(type, item);
  const stars = state.marks.starred;
  const on = !stars[list].includes(id);
  stars[list] = on ? [id, ...stars[list]] : stars[list].filter((x) => x !== id);
  render();
  renderPeekAgain();
  renderStatusBar();
  try {
    await api(on ? "PUT" : "DELETE", `/api/my/stars/${type}/${id}`);
  } catch (error) {
    stars[list] = on ? stars[list].filter((x) => x !== id) : [id, ...stars[list]];
    render();
    renderPeekAgain();
    renderStatusBar();
    toast(error.message, "error");
    return;
  }
  // Unstarred while only stars are shown: it leaves the list
  if (state.mark === "starred" && !on) {
    if (type === "clip") loadClips();
    if (type === "video") loadVideos();
  }
}

// Player 2's picker reads them too
export function myMarks() {
  return state.marks;
}

// editor.js: a VOD, clip or video was loaded in a player
export async function noteOpened(type, item) {
  const list = MARK_LISTS[type];
  if (!list || state.demo || !item || me?.support) return;
  const id = markId(type, item);
  if (!id || id === "undefined" || id === "0") return;
  const recent = state.marks.recent;
  recent[list] = [id, ...recent[list].filter((x) => x !== id)].slice(0, 50);
  renderStatusBar();
  try {
    await api("POST", "/api/my/opened", { kind: type, id: Number(id) });
  } catch {
    // Recent is a convenience
  }
}

// ★ / Recent / Mine at the front of the status row
function quickChips() {
  const list = state.list;
  const count = (mark) => state.marks[mark][list]?.length ?? 0;
  const chip = (on, icon, label, n, title, run) => {
    const node = el("button", { type: "button", class: `sbar-chip quick${on ? " on" : ""}`, "aria-pressed": String(on), title },
      icon ? el("sl-icon", { library: "ph", name: icon }) : null, label, n == null ? null : el("span", { class: "sbar-n" }, n.toLocaleString()));
    node.addEventListener("click", run);
    return node;
  };
  const mine = state.who.memberId === String(me.id) && state.who.mode !== "edited";
  return [
    chip(state.mark === "starred", state.mark === "starred" ? "star-fill" : "star", "", count("starred"), "Only what you starred", () => setMark("starred")),
    chip(state.mark === "recent", "clock-counter-clockwise", "Recent", null, "Only what you opened lately", () => setMark("recent")),
    list === "vods" ? null : chip(mine, null, "Mine", null, `Only ${list} you made`, () => toggleMine()),
  ].filter(Boolean);
}

function setMark(mark) {
  state.mark = state.mark === mark ? "" : mark;
  renderStatusBar();
  renderFilter();
  if (state.clipsLoaded || state.list === "clips") loadClips();
  if (state.videosLoaded || state.list === "videos") loadVideos();
  render();
}

function toggleMine() {
  const mine = state.who.memberId === String(me.id) && state.who.mode !== "edited";
  if (mine) Object.assign(state.who, { memberId: "" });
  else {
    rememberMembers([[me.id, me.username]]);
    Object.assign(state.who, { memberId: String(me.id), mode: "made" });
  }
  renderStatusBar();
  renderChips();
  reloadWho();
}
