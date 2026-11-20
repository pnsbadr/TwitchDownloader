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
// the cards view is one click away and remembered per member (clipco.library.view.{userId}).

import { $, el, api, me, can, liveChannels, formatDate, formatDuration, loadSetting, saveSetting, toast, iconButton, withTooltip, twitchThumb, thumbStyle } from "./core.js";
import { draggable, shadePath } from "./drag.js";
import { dayRange, todayIn, viewerTimeZone, zonedParts } from "./dates.js";
import { clipColor, CLIP_COLOR, STREAM_COLORS } from "./colors.js";
import { askText, confirmAction } from "./dialogs.js";
import { openMenu } from "./menu.js";

const POLL_MS = 3000;
const NEAR_BOTTOM_PX = 300;
const PAGE_SIZE = 100;
const ROW_H = 30;        // one row in the rows view (the list is drawn from this, keep editor.css .lrow in step)
const SEP_H = 9;         // the thin line between days (editor.css .lrow.lsep)
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
  clipFilter: { search: "", sort: loadSetting("clipco.library.sort", "dateAdded"), order: "desc", last24h: false, day: "" },
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
    litScrub();
    loadMoreIfNear();
  });
  // Moving from a row to its card at the bottom keeps that card (its buttons can be clicked)
  $("side-list").addEventListener("mouseleave", (event) => { if (!$("side-peek").contains(event.relatedTarget)) setHovered(""); });
  $("side-peek").addEventListener("mouseleave", (event) => { if (!$("side-list").contains(event.relatedTarget)) setHovered(""); });
  $("side-view").addEventListener("click", () => setView(state.view === "rows" ? "cards" : "rows"));
  setUpScrub();
  new ResizeObserver(() => { if (state.view === "rows") scheduleWindow(); }).observe($("side-list"));
  renderViewButton();
  setUpFilter();
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
    return mergedVods().filter((v) => (!search || v.title.toLowerCase().includes(search)) && (!state.onlyWorked || state.work.has(String(v.vodId))));
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
    list.replaceChildren(el("p", { class: "side-empty" }, EMPTY[state.list]()));
    renderScrub(null);
    renderPeek();
    return;
  }
  const lines = linesFor(items);
  if (state.view === "rows") {
    renderRows(lines);
  } else {
    rowsModel = null;
    const nodes = lines.map((line, index) => (line.head
      ? el("div", { class: `side-sep${index ? "" : " first"}`, "data-line": String(index), title: sepTitle(line) }, el("i"))
      : card(specFor(line.item))));
    if (listLoadingMore()) nodes.push(el("p", { class: "side-empty" }, "Loading more…"));
    list.replaceChildren(...nodes);
  }
  renderScrub(lines);
  renderPeek();
  // A short first page that doesn't fill the panel can't be scrolled to load the next one
  requestAnimationFrame(loadMoreIfNear);
}

// ---------- rows view: day headings, only the rows in view drawn ----------

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

// The list as lines: a thin line before each day (its date in the tooltip and on the month bar), then that day's items ({ key, item }; their card or row is made when drawn)
function linesFor(items) {
  const zone = viewerTimeZone();
  const lines = [];
  let dayKey = null;
  let head = null;
  for (const item of items) {
    const at = itemDate(item);
    const ms = at ? new Date(at).getTime() : NaN;
    const p = Number.isNaN(ms) ? null : zonedParts(ms, zone);
    const bucket = !p ? { key: "none", label: "No date" } : { key: `${p.year}-${p.month}-${p.day}`, label: dayLabel(ms, zone) };
    if (bucket.key !== dayKey) {
      dayKey = bucket.key;
      // The same day can come back in a list that isn't sorted on this date: each heading still needs its own key
      head = { head: bucket.label, key: `day:${bucket.key}:${lines.length}`, count: 0, at: p };
      lines.push(head);
    }
    head.count++;
    lines.push({ key: itemKey(item), item, at: p });
  }
  return lines;
}

// ---------- the month bar on the list's right edge (badrfly 2026-11-20) ----------
// At rest: one mark per month of this year that the loaded list reaches, then one per earlier year ('25); the mark of what's at the top of
// the list is lit, with that day's number under it. Under the mouse a month opens into its days (a year into its months) and the other
// marks slide down; a day shows its date and count, and a click or drag jumps there. Months not loaded yet aren't shown: "…" loads further.

const scrubFormatters = new Map();
let scrubMarks = [];  // [{ key, label, line, children: [{ label, line, count, tip }] }] in list order
let scrubOpen = -1;   // the mark opened under the mouse

function scrubFormatter(options) {
  const key = JSON.stringify(options);
  let formatter = scrubFormatters.get(key);
  if (!formatter) scrubFormatters.set(key, formatter = new Intl.DateTimeFormat(undefined, { ...options, timeZone: "UTC" }));
  return formatter;
}

function scrubMarksFor(lines) {
  const thisYear = zonedParts(Date.now(), viewerTimeZone()).year;
  const short = scrubFormatter({ month: "short" });
  const long = scrubFormatter({ month: "long", year: "numeric" });
  const marks = [];
  lines.forEach((line, index) => {
    if (!line.head || !line.at) return;
    const { year, month, day } = line.at;
    const recent = year === thisYear;
    const key = recent ? `${year}-${month}` : `${year}`;
    let mark = marks[marks.length - 1];
    if (mark?.key !== key) {
      mark = { key, label: recent ? short.format(new Date(Date.UTC(year, month - 1, 15))) : `'${String(year).slice(-2)}`, line: index, children: [] };
      marks.push(mark);
    }
    if (recent) {
      mark.children.push({ label: String(day), line: index, count: line.count, tip: sepTitle(line) });
      return;
    }
    // An earlier year opens into its months
    const at = new Date(Date.UTC(year, month - 1, 15));
    let child = mark.children[mark.children.length - 1];
    if (child?.month !== month) mark.children.push(child = { month, label: short.format(at), line: index, count: 0 });
    child.count += line.count;
    child.tip = `${long.format(at)} · ${countLabel(child.count)}`;
  });
  return marks;
}

function renderScrub(lines) {
  const scrub = $("side-scrub");
  scrubMarks = lines ? scrubMarksFor(lines) : [];
  const days = scrubMarks.reduce((n, mark) => n + mark.children.length, 0);
  const show = scrubMarks.length > 0 && (days > 1 || listHasMore());
  scrub.hidden = !show;
  $("side-list").classList.toggle("with-scrub", show);
  if (scrubOpen >= scrubMarks.length) scrubOpen = -1;
  if (show) drawScrub();
}

function drawScrub() {
  const scrub = $("side-scrub");
  const nodes = scrubMarks.map((mark, i) => {
    const block = el("div", { class: `scrub-month${i === scrubOpen ? " open" : ""}`, "data-index": String(i) },
      el("span", { class: "scrub-mark" }, mark.label));
    if (i === scrubOpen) {
      block.append(el("div", { class: "scrub-days" }, ...mark.children.map((child) =>
        el("span", { class: "scrub-day", "data-line": String(child.line), "data-tip": child.tip }, child.label, el("i")))));
    } else block.append(el("span", { class: "scrub-day at", hidden: "" }));
    return block;
  });
  if (listHasMore()) nodes.push(el("div", { class: "scrub-month more", title: "Load older" }, el("span", { class: "scrub-mark" }, "…")));
  nodes.push(el("span", { class: "scrub-tip", hidden: "" }));
  scrub.replaceChildren(...nodes);
  litScrub();
}

function listHasMore() {
  return state.list === "vods" ? moreVods() : state.list === "clips" ? Boolean(state.clipsCursor) : Boolean(state.videosCursor);
}

// The line at the top of the list (rows: by position; cards: the last day line above the top)
function topLine() {
  const list = $("side-list");
  if (state.view === "rows") return lineAt(list.scrollTop + 1);
  let index = 0;
  for (const sep of list.querySelectorAll(":scope > .side-sep[data-line]")) {
    if (sep.offsetTop - list.offsetTop > list.scrollTop + 4) break;
    index = Number(sep.dataset.line);
  }
  return index;
}

function litScrub() {
  const scrub = $("side-scrub");
  if (scrub.hidden || !scrubMarks.length) return;
  const top = topLine();
  let lit = 0;
  scrubMarks.forEach((mark, i) => { if (mark.line <= top) lit = i; });
  let child = null;
  for (const c of scrubMarks[lit].children) if (c.line <= top) child = c;
  for (const block of scrub.querySelectorAll(".scrub-month[data-index]")) {
    const on = block.dataset.index === String(lit);
    block.querySelector(".scrub-mark").classList.toggle("on", on);
    const at = block.querySelector(".scrub-day.at");
    if (at) {
      at.hidden = !on || !child;
      if (on && child) at.replaceChildren(child.label, el("i"));
    }
    for (const day of block.querySelectorAll(".scrub-days .scrub-day")) day.classList.toggle("on", Boolean(on && child) && day.dataset.line === String(child.line));
  }
}

function jumpToLine(line) {
  const list = $("side-list");
  if (state.view === "rows") {
    if (rowsModel?.ys) list.scrollTop = rowsModel.ys[line] ?? 0;
  } else {
    const sep = list.querySelector(`:scope > .side-sep[data-line="${line}"]`);
    if (sep) list.scrollTop = sep.offsetTop - list.offsetTop;
  }
  litScrub();
}

function setUpScrub() {
  const scrub = $("side-scrub");
  // What's under the pointer: the mark whose block holds it, and in an open one the nearest day
  const hit = (event) => {
    const y = event.clientY;
    const blocks = [...scrub.querySelectorAll(".scrub-month")];
    if (!blocks.length) return null;
    let block = blocks.find((b) => { const r = b.getBoundingClientRect(); return y >= r.top && y < r.bottom; });
    if (!block) block = y < blocks[0].getBoundingClientRect().top ? blocks[0] : blocks[blocks.length - 1];
    let day = null;
    let best = Infinity;
    for (const node of block.querySelectorAll(".scrub-days .scrub-day")) {
      const r = node.getBoundingClientRect();
      const d = Math.abs(y - (r.top + r.height / 2));
      if (d < best) { best = d; day = node; }
    }
    // Over the month's own name, the month itself
    const name = block.querySelector(".scrub-mark").getBoundingClientRect();
    if (y < name.bottom + 2) day = null;
    return { block, day };
  };
  const openAt = (event) => {
    const found = hit(event);
    const index = found?.block.dataset.index == null ? -1 : Number(found.block.dataset.index);
    if (index >= 0 && index !== scrubOpen) {
      scrubOpen = index;
      drawScrub();
    }
    return hit(event);
  };
  const showTip = (day) => {
    const tip = scrub.querySelector(".scrub-tip");
    if (!tip) return;
    for (const node of scrub.querySelectorAll(".scrub-day.hover")) node.classList.remove("hover");
    tip.hidden = !day;
    if (!day) return;
    day.classList.add("hover");
    const r = day.getBoundingClientRect();
    tip.textContent = day.dataset.tip;
    tip.style.top = `${r.top - scrub.getBoundingClientRect().top + r.height / 2}px`;
  };
  const go = (found) => {
    if (!found) return;
    if (found.block.classList.contains("more")) {
      const list = $("side-list");
      list.scrollTop = list.scrollHeight;
      loadMoreIfNear();
    } else if (found.day) jumpToLine(Number(found.day.dataset.line));
    else jumpToLine(scrubMarks[Number(found.block.dataset.index)]?.line ?? 0);
  };
  const close = () => {
    if (scrubOpen < 0) return;
    scrubOpen = -1;
    drawScrub();
  };
  scrub.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    scrub.setPointerCapture(event.pointerId);
    scrub.classList.add("dragging");
    const found = openAt(event);
    showTip(found?.day);
    go(found);
  });
  scrub.addEventListener("pointermove", (event) => {
    const found = openAt(event);
    showTip(found?.day);
    if (scrub.hasPointerCapture(event.pointerId)) go(found);
  });
  scrub.addEventListener("pointerup", (event) => {
    scrub.classList.remove("dragging");
    const r = scrub.getBoundingClientRect();
    if (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom) close();
  });
  scrub.addEventListener("pointercancel", () => { scrub.classList.remove("dragging"); close(); });
  scrub.addEventListener("pointerleave", () => { if (!scrub.classList.contains("dragging")) close(); });
}

function itemKey(item) {
  return state.list === "vods" ? `vod:${item.vodId}` : state.list === "clips" ? `clip:${item.id}` : `video:${item.id}`;
}

function specFor(item) {
  return state.list === "vods" ? vodSpec(item) : state.list === "clips" ? clipSpec(item) : videoSpec(item);
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
    if (kept && kept.item === line.item && kept.count === line.count && (line.head || kept.sig === rowSig(line.item))) rowsModel.nodes.set(line.key, kept);
  }
  // Where each line starts: rows are ROW_H, the lines between days SEP_H
  const ys = new Array(lines.length + 1);
  ys[0] = 0;
  lines.forEach((line, i) => { ys[i + 1] = ys[i] + (line.head ? SEP_H : ROW_H); });
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
      entry = { item: line.item, count: line.count, sig: line.head ? "" : rowSig(line.item), node: line.head ? dayRow(line) : listRow(line) };
      nodes.set(line.key, entry);
    }
    entry.node.style.top = `${rowsModel.ys[i]}px`;
    entry.node.classList.toggle("sel", !line.head && state.selected[state.list] === line.key);
    wanted.push(entry.node);
  }
  if (listLoadingMore()) {
    const loading = el("div", { class: "lrow lrow-note faint" }, "Loading more…");
    loading.style.top = `${rowsModel.ys[lines.length]}px`;
    wanted.push(loading);
  }
  // Keep the focused row in the page even when it scrolled out of view (keyboard focus isn't lost)
  const focused = document.activeElement?.closest?.(".lrow");
  if (focused && inner.contains(focused) && !wanted.includes(focused)) wanted.push(focused);
  inner.replaceChildren(...wanted);
}

// The line before a day's rows (badrfly 2026-11-20: a thin line instead of a heading; the date is on the month bar and in the tooltip)
function dayRow(line) {
  return el("div", { class: `lrow lsep${line === rowsModel?.lines[0] ? " first" : ""}`, role: "presentation", title: sepTitle(line) }, el("i"));
}

function sepTitle(line) {
  return `${line.head} · ${countLabel(line.count)}`;
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
function rowSig(item) {
  if (state.list === "vods") {
    const work = state.work.get(String(item.vodId));
    return `${item.isLive}|${work ? workCount(work) : ""}|${shownChannels().length > 1}|${state.cards.get(item.channel)?.profileImageUrl ?? ""}`;
  }
  return `${item.title}|${item.status}|${item.generationStatus}|${item.color}|${item.clipLengthSeconds ?? item.lengthSeconds}`;
}

function listRow(line) {
  const spec = specFor(line.item);
  const r = spec.row;
  const node = el("div", { class: "lrow item", role: "listitem", tabindex: "0", "aria-label": spec.title, "data-key": line.key },
    r.lead ?? el("span", { class: "ldot", style: `background:${r.color ?? CLIP_COLOR}` }),
    el("span", { class: "ltitle", title: spec.title }, spec.title),
    ...(r.tags ?? []).filter(Boolean),
    el("span", { class: "llen" }, r.length ?? ""),
    r.who ?? el("span", { class: "lwho empty" }));
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
  });
  return node;
}

function itemLines() {
  return rowsModel ? rowsModel.lines.filter((line) => !line.head) : [];
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
  do index += step; while (index >= 0 && index < lines.length && lines[index].head);
  if (index < 0 || index >= lines.length) return;
  const list = $("side-list");
  const y = rowsModel.ys[index];
  if (y < list.scrollTop + SEP_H) list.scrollTop = y - SEP_H;
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
  const lines = state.view === "rows" ? itemLines() : [];
  const line = lines.find((l) => l.key === state.hovered) ?? lines.find((l) => l.key === state.selected[state.list]);
  peek.hidden = !line;
  if (!line) {
    peekKey = null;
    peek.replaceChildren();
    return;
  }
  if (peekKey === line.key && peek.firstChild?._item === line.item && peek.firstChild?._sig === rowSig(line.item)) return;
  peekKey = line.key;
  const node = card(specFor(line.item));
  node._sig = rowSig(line.item);
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
      el("div", { class: "card-actions" }, ...actions)));
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
      work?.clips && { label: "Show its clips", icon: "scissors", run: () => showVodWork(vod.vodId, "clips") },
      work?.videos && { label: "Show its videos", icon: "film-strip", run: () => showVodWork(vod.vodId, "videos") },
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
    ],
    actions: [exportButton({ kind: "clip", data: item })],
    dragData: item,
    channel: channelByName(entry.channelName),
    row: {
      color: clipColor(entry.color),
      length: formatDuration(entry.clipLengthSeconds),
      who: whoBadge(entry.addedByUserId, entry.addedBy),
      tags: [
        entry.kind === "Clip" ? el("span", { class: "ltag" }, "Twitch") : null,
        entry.kind !== "Clip" && isLongClip(entry) ? el("span", { class: "ltag", title: "Longer than the stored-clip limit: not stored, made on demand" }, "Long") : null,
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
    ],
    actions: [exportButton({ kind: "video", data: item })],
    dragData: item,
    channel: channelByName(video.channelName),
    row: {
      color: CLIP_COLOR,
      length: formatDuration(video.lengthSeconds ?? 0),
      who: whoBadge(video.userId, video.createdBy),
      tags: [streams > 1 ? el("span", { class: "ltag", title: `Plays from ${streams} streams` }, `${streams} streams`) : null],
    },
    menu: () => [
      { label: "Load in Player 2", icon: "film-strip", run: () => handlers.onLoad({ kind: "video", data: item }) },
      { label: "Export", icon: "download-simple", run: () => handlers.onExport({ kind: "video", data: item }) },
      String(video.vodId) !== state.who.vodId && { label: "Show this VOD's videos", icon: "funnel-simple", run: () => showVodWork(video.vodId, "videos") },
      video.canEdit && handlers.onAddSegments && { label: "Add segments…", icon: "link", run: () => handlers.onAddSegments(item) },
      video.canEdit && { label: "Rename", icon: "pencil-simple", run: () => renameVideo(item) },
      video.canDelete && { label: "Delete", icon: "trash", danger: true, run: () => deleteVideo(item) },
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
        loadVideos();
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
