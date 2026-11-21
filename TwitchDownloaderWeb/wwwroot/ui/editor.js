// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { setUpStart, openStart, startIsOpen } from "./start.js";
import { $, el, api, me, can, loadMe, startLiveEvents, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, loadSetting, saveSetting, toast, formatDuration, formatDate } from "./core.js";
import { dropTarget, watchDrags, shadePath } from "./drag.js";
import { setUpLibrary, refreshEdits, player2Changed, revealLoaded, noteOpened, myMarks, toggleStar, showList, refreshClips, refreshVideos, refreshLibraryFilters, knownVod, knownVodTitle, pickChannel, teamChannels, adjacentVod, focusLibrarySearch, libraryHidden, setLibraryHidden, statusItemsFor } from "./library.js";
import { setUpLinkTray, renderLinkTray, savedLinking, addSegmentsTo, vodDay, vodInfo } from "./link-tray.js";
import { setUpTree, forgetTree, segmentData } from "./p2-tree.js";
import { MARKER_COLORS } from "./colors.js";
import { MediaPlayer, formatClock, isChannelRefusal, channelRefusalText, checkVodAccess } from "./media.js";
import { Timeline } from "./timeline.js";
import { trimValue } from "./timeline-edit.js";
import { skipCut, keptSeconds } from "./cuts.js";
import { askText, confirmAction, chooseEditTarget } from "./dialogs.js";
import { ProjectStore, piecesFromItem, flatten, stretchAt, emptyContent, withoutLayouts, trackName } from "./project.js";
import { ProjectTimeline, forgetLiveVodLengths } from "./project-timeline.js";
import { ProjectPlayback } from "./project-playback.js";
import { setUpExport, addToExport, exportShown, pausePreview } from "./export.js";
import { showShortcuts } from "./shortcuts.js";
import { makeVideoFrom } from "./bulk.js";
import { openSyncView } from "./sync-view.js";
import { SyncedPip } from "./p1-pip.js";
import { setUpProjectVertical, openProjectVertical, closeProjectVertical, projectVerticalOpen, refreshProjectVertical, handleProjectVerticalKey, pauseProjectVertical, verticalFrameCanvas } from "./project-vertical.js";
import { saveScreenshot } from "./snapshot.js";

// Phosphor icons, bold weight, as <sl-icon library="ph" name="…">
registerIconLibrary("ph", {
  // Bold everywhere; "star-fill" and the like are the filled set (a starred item's star)
  resolver: (name) => (name.endsWith("-fill")
    ? `https://cdn.jsdelivr.net/npm/@phosphor-icons/core@2.1.1/assets/fill/${name}.svg`
    : `https://cdn.jsdelivr.net/npm/@phosphor-icons/core@2.1.1/assets/bold/${name}-bold.svg`),
});

const DEMO = new URLSearchParams(location.search).has("demo");


// ---------- gate (no team, no role, support ended) ----------

function showGate(text, heading = "Waiting for access", extra = null) {
  $("gate-heading").textContent = heading;
  $("gate-text").textContent = text;
  $("gate-extra").replaceChildren(...(extra ? [extra] : []));
  $("gate").hidden = false;
  $("workspace").hidden = true;
}

sessionHandlers.gate = (text, heading) => {
  const back = heading === "Support view ended"
    ? el("sl-button", { onclick: exitSupport }, "Back to the Admin page")
    : null;
  showGate(text, heading, back);
};

async function exitSupport() {
  await fetch("/api/admin/support", { method: "DELETE" });
  location.href = "/admin.html";
}

// ---------- top bar ----------

function renderTopbar() {
  const name = me.support ? me.support.staffUsername : me.username;
  $("avatar").textContent = (name || "?").slice(0, 1).toUpperCase();
  $("account-name").textContent = me.support ? `Staff: ${name}` : name;
  // The editor's menu keeps the Team page and shortcuts; the start screen's own menu is the short one (badrfly 2026-11-19)
  $("menu-team").hidden = !TEAM_PAGE_PERMISSIONS.some(can);

  if (me.support) {
    $("support-team").textContent = me.support.teamName;
    $("support-banner").hidden = false;
    $("menu-logout").hidden = true;
    $("account-items").querySelector('[value="account"]').hidden = true;
    $("team-pill-slot").replaceChildren(el("span", { class: "team-pill" }, me.support.teamName));
    return;
  }

  // The pill brings back the start screen, where the member switches teams (badrfly 2026-11-19)
  const current = me.teams.find((team) => team.id === me.currentTeamId);
  $("team-pill-slot").replaceChildren(el("button", { type: "button", class: "team-pill", "aria-label": "Your teams", title: "Your teams",
    onclick: () => openStart({ entered: true }) }, current?.name ?? "Pick a team", el("sl-icon", { library: "ph", name: "caret-down" })));
}

// The profile menu: who's logged in, Account settings, Report a problem, Log out (badrfly 2026-11-19); the start screen has the same
async function accountAction(value) {
  switch (value) {
    case "account": location.href = "/account.html"; break;
    case "team": location.href = "/team.html"; break;
    case "shortcuts": showShortcuts(); break;
    case "report": window.clipcoReports?.openBugDialog(); break;
    case "logout":
      await fetch("/api/auth/logout", { method: "POST" });
      location.href = "/";
      break;
  }
}
$("account-items").addEventListener("sl-select", (event) => accountAction(event.detail.item.value));
$("support-exit").addEventListener("click", exitSupport);
$("shortcuts-button").addEventListener("click", showShortcuts);

// ---------- tabs ----------

// A row of role="tab" buttons: click or arrow keys pick one; onPick(value) shows it
function tabs(container, attribute, onPick, storageKey) {
  const buttons = [...container.querySelectorAll(`[${attribute}]`)];
  const pick = (value, save = true) => {
    for (const button of buttons) button.setAttribute("aria-selected", String(button.getAttribute(attribute) === value));
    if (save && storageKey) saveSetting(storageKey, value);
    onPick(value);
  };
  for (const button of buttons) {
    button.tabIndex = button.getAttribute("aria-selected") === "true" ? 0 : -1;
    button.addEventListener("click", () => pick(button.getAttribute(attribute)));
    button.addEventListener("keydown", (event) => {
      if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
      const index = buttons.indexOf(button) + (event.key === "ArrowRight" ? 1 : -1);
      const next = buttons[(index + buttons.length) % buttons.length];
      next.focus();
      pick(next.getAttribute(attribute));
    });
  }
  const saved = storageKey ? loadSetting(storageKey, null) : null;
  const first = buttons.find((b) => b.getAttribute(attribute) === saved) ?? buttons.find((b) => b.getAttribute("aria-selected") === "true");
  pick(first.getAttribute(attribute), false);
  return { pick };
}

let screenTabs = null;
let listTabs = null;
let timelineTabs = null;

// What error and bug reports say the editor had open (reports.js)
window.clipcoReports?.setContext(() => {
  const project = store?.project;
  const p2 = project ? {
    editId: project.draft ? null : project.id ?? null,
    of: project.entryId ? `clip ${project.entryId}` : project.videoId ? `video ${project.videoId}` : null,
    vertical: Boolean(project.vertical),
    vodId: store.vodId || null,
    pieces: store.content?.pieces?.length ?? 0,
  } : null;
  return {
    view: $("view-export")?.hidden === false ? "export" : "editor",
    activePlayer,
    timeline: activePlayer,
    rowFocus: rowFocus ? `${rowFocus.which} row ${rowFocus.rowIds.join(",")}` : null,
    vodId: p1Vod?.id ?? p2?.vodId ?? null,
    entryId: project?.entryId ?? null,
    player1: p1Vod ? { vodId: p1Vod.id, title: p1Vod.title, playing: Boolean(player1?.ready && player1.playing()), time: Math.round(player1?.position?.() ?? 0) } : null,
    player2: p2,
    player: player1 ? `${player1.kind ?? "?"}${player1.segmentMode ? ` (${player1.segmentMode})` : ""}` : null,
  };
});

function showScreen(view) {
  window.clipcoReports?.note(`screen ${view}`);
  $("view-editor").hidden = view !== "editor";
  $("view-export").hidden = view !== "export";
  // Only one player plays at a time: the screen left behind is paused
  if (view === "export") {
    endRowFocus();
    for (const player of [player1, player2]) if (player?.ready && player.playing()) player.pause();
    playback?.pause();
    pauseProjectVertical();
    exportShown();
  } else {
    pausePreview();
  }
}

// ---------- layout: resizable borders (Split.js), sizes kept per browser ----------

function split(elements, key, options) {
  return Split(elements, {
    gutterSize: 6,
    snapOffset: 0,
    ...options,
    sizes: loadSetting(key, options.sizes),
    onDragEnd: (sizes) => saveSetting(key, sizes),
  });
}

function setUpLayout() {
  split([$("side"), document.querySelector(".views")], "clipco.layout.side", { sizes: [19, 81], minSize: [240, 760] });
  split([$("players"), $("lower")], "clipco.layout.players", { sizes: [58, 42], minSize: [220, 220], direction: "vertical" });
  split([$("player1"), $("player2")], "clipco.layout.p1p2", { sizes: [50, 50], minSize: [320, 320] });
  // Export screen: two columns, each split top/bottom (badrfly 2026-11-21: "resizable like on editor").
  split([$("export-col-left"), $("export-col-right")], "clipco.layout.export.cols", { sizes: [60, 40], minSize: [360, 300] });
  split([$("export-player"), $("export-list-panel")], "clipco.layout.export.left", { sizes: [50, 50], minSize: [200, 160], direction: "vertical" });
  split([$("export-settings"), $("export-history")], "clipco.layout.export.right", { sizes: [50, 50], minSize: [160, 120], direction: "vertical" });
}

// ---------- left panel (ui/library.js) ----------

// A card's download icon or "Export": the item goes on the export list (a VOD opens the range pop-up)
function openExport(item) {
  screenTabs.pick("export");
  addToExport(item);
}

// Player 1's selection menu (roadmap 1c / 1d, badrfly 2026-11-21): a video of the selection in time order (clips copied as segments
// first, see ui/bulk.js) and the export list
function p1BulkItems(items) {
  const out = [];
  const usable = items.filter((it) => it.kind === "segment" || (it.kind === "clip" && it.data.kind === "Vod"));
  if (usable.length && can("AddEntries") && !me?.support) {
    out.push({ icon: "film-reel", label: `Make a video of ${usable.length} part${usable.length === 1 ? "" : "s"}…`, run: async () => {
      const video = await makeVideoFrom(items.map((it) => ({
        kind: it.kind, id: it.id, vodId: timeline.rowVod(it.row?.id), order: it.start, own: timeline.isOwn(it), title: it.title,
        color: it.kind === "clip" ? it.data.color ?? "violet" : it.color, twitch: it.kind === "clip" && it.data.kind !== "Vod",
        copyRowId: it.kind === "segment" && !timeline.isOwn(it) ? timeline.targetRow("video", timeline.rowVod(it.row?.id))?.id ?? null : null,
      })), { shownOnVodId: timeline.vodId });
      if (!video) return;
      await timeline.load();
      timeline.edited?.();
      refreshVideos();
      refreshClips();
    } });
  }
  // Segments and clips of this VOD (a synced row's items are another VOD's: added from their own VOD)
  const exportable = usable.filter((it) => !timeline.syncedRow(it.row?.id));
  if (exportable.length && can("GenerateFiles")) {
    out.push({ icon: "download-simple", label: `Add ${exportable.length} to the export list`, run: () => {
      for (const item of exportable) exportFromTimeline(item, { quiet: true });
      toast(`Added ${exportable.length} item${exportable.length === 1 ? "" : "s"} to the export list.`);
    } });
  }
  return out;
}

// The library's selection menu (roadmap 1c / 1d): a video of the selected clips and segments in stream time order, copies of
// teammates' clips and videos, and the export list
function libraryBulkItems(lines) {
  const out = [];
  // Stream time orders the parts, across VODs too: a VOD not in the loaded list is looked up first
  const vodStart = async (vodId, date) => {
    const at = Date.parse(date ?? knownVod(vodId)?.createdAtUtc ?? "");
    if (at) return at / 1000;
    const info = await vodInfo(vodId);
    return Date.parse(info?.streamDateUtc ?? info?.createdAt ?? "") / 1000 || 0;
  };
  const clips = lines.filter((l) => l.type === "clip").map((l) => l.item);
  const videos = lines.filter((l) => l.type === "video").map((l) => l.item);
  const segments = lines.filter((l) => l.type === "segment" && !l.item.removed && l.item.endSeconds != null);
  const parts = [
    ...clips.filter((c) => c.kind !== "Clip").map((c) => ({
      kind: "clip", id: c.id, vodId: String(c.vodId), own: String(c.addedByUserId) === String(me?.id), title: c.title, color: c.color ?? "violet",
      at: [String(c.vodId), c.streamDateUtc, c.startSeconds ?? 0], twitch: false,
    })),
    ...segments.map((l) => {
      const vodId = String(l.item.vodId ?? l.video.vodId);
      return { kind: "segment", id: l.item.markerId, vodId, own: l.video.userId === me?.id, title: l.item.label, color: l.item.color,
        at: [vodId, null, l.item.startSeconds] };
    }),
  ];
  if (parts.length && can("AddEntries") && !me?.support) {
    const left = clips.length - parts.filter((p) => p.kind === "clip").length;
    out.push({ icon: "film-reel", label: `Make a video of ${parts.length} part${parts.length === 1 ? "" : "s"}…`, run: async () => {
      for (const part of parts) part.order = (await vodStart(part.at[0], part.at[1])) + part.at[2];
      const all = [...parts, ...Array.from({ length: left }, () => ({ kind: "clip", twitch: true, order: 0 }))];
      const video = await makeVideoFrom(all, { shownOnVodId: null });
      if (!video) return;
      refreshVideos();
      refreshClips();
      if (timeline?.vodId) timeline.load();
    } });
  }
  const theirClips = clips.filter((c) => c.kind !== "Clip" && String(c.addedByUserId) !== String(me?.id));
  const theirVideos = videos.filter((v) => v.userId !== me?.id);
  const copies = theirClips.length + theirVideos.length;
  if (copies && can("AddEntries") && !me?.support) {
    out.push({ icon: "copy", label: `Copy ${copies === 1 ? "1 teammate's item" : `${copies} teammates' items`} to my rows`, run: async () => {
      let failed = 0;
      let firstError = "";
      for (const [url, id] of [...theirClips.map((c) => ["/api/entries", c.id]), ...theirVideos.map((v) => ["/api/videos", v.id])]) {
        try {
          await api("POST", `${url}/${id}/copy`, {});
        } catch (error) {
          failed++;
          firstError ||= error.message;
        }
      }
      toast(failed ? `Copied ${copies - failed} of ${copies}: ${firstError}` : `Copied ${copies} item${copies === 1 ? "" : "s"} to your rows.`, failed ? "error" : "");
      if (theirClips.length) refreshClips();
      if (theirVideos.length) refreshVideos();
      if (timeline?.vodId) timeline.load();
    } });
  }
  const exportable = clips.filter((c) => c.kind !== "Clip").length + videos.length + segments.length;
  if (exportable && can("GenerateFiles")) {
    out.push({ icon: "download-simple", label: `Add ${exportable} to the export list`, run: () => {
      for (const c of clips.filter((c) => c.kind !== "Clip")) addToExport({ kind: "clip", data: c });
      for (const v of videos) addToExport({ kind: "video", data: v });
      for (const l of segments) {
        const vod = knownVod(l.item.vodId ?? l.video.vodId);
        addToExport({ kind: "segment", data: { ...segmentData(l.item, l.video), vodTitle: vod?.title, vodDate: vod?.createdAtUtc, thumbnailUrl: vod?.thumbnailUrl } });
      }
      toast(`Added ${exportable} item${exportable === 1 ? "" : "s"} to the export list.`);
    } });
  }
  return out;
}

// Player 1's right-click "Add to the export list" on a segment or clip (stays on the Editor screen)
function exportFromTimeline(item, { quiet = false } = {}) {
  const vod = p1Vod ?? {};
  if (item.kind === "segment") {
    addToExport({
      kind: "segment",
      data: { ...item.data, vodId: timeline.vodId, vodTitle: vod.title, vodDate: vod.createdAtUtc, thumbnailUrl: vod.thumbnailUrl },
    }, { quiet });
    return;
  }
  const start = item.start;
  const end = item.end;
  addToExport({
    kind: "clip",
    data: {
      id: item.id, kind: item.data.kind, vodId: timeline.vodId, title: item.title, startSeconds: start, endSeconds: end, cuts: item.cuts ?? [],
      clipLengthSeconds: keptSeconds(start, end, item.cuts ?? []), streamDateUtc: vod.createdAtUtc, thumbnailUrl: vod.thumbnailUrl,
      isReferenceOnly: Boolean(item.reference),
    },
  }, { quiet });
}

// ---------- players ----------
//
// Player 1 plays VODs with every member's rows on its timeline: the overview where things are marked. Player 2 is the editor:
// it plays the open PROJECT (ui/project.js), a multi-track timeline from 0:00 made of pieces of VODs (copies of clips,
// segments and videos dropped on it). Player 2's position is project time (ui/project-playback.js maps it to a VOD and a
// second). 9:16 (E) is a view of the same project (ui/project-vertical.js). The keys and the tools bar work on the player whose
// timeline tab is open; clicking a player that has something loaded opens its tab.

const PLAYER_KINDS = { p1: ["vod"], p2: ["video", "clip", "segment", "project"] };
const FRAME_SECONDS = 1 / 30;
const LAST_PROJECT_KEY = "clipco.p2.project";

let player1 = null;
let player2 = null;
let timeline = null;   // Player 1's
let timeline2 = null;  // Player 2's (ProjectTimeline)
let store = null;      // the project open in Player 2
let playback = null;
let p1Vod = null;
let activePlayer = "p1";
let p2Opened = 0;      // which open the project picker is on (ignores late answers)

function pair(which = activePlayer) {
  return which === "p2" ? { player: player2, timeline: timeline2 } : { player: player1, timeline };
}

function activeTimeline() {
  return pair().timeline;
}

// item = { kind, data } from a card, a timeline item or a drop
function load(item) {
  if (PLAYER_KINDS.p1.includes(item.kind)) {
    openVod(item.data);
    timelineTabs.pick("p1");
    return;
  }
  if (item.kind === "project") {
    openProject(item.data.id);
    return;
  }
  loadInPlayer2(item);
}

// back = { vod, shift } when a synced row's VOD was opened from another one: the header's Back button returns there
async function openVod(vod, { start = 0, paused = false, back = null } = {}) {
  if (DEMO) {
    toast("Playback isn't available in the demo.");
    return;
  }
  p1Vod = vod;
  p1Back = back;
  p1Away = null;
  noteOpened("vod", vod);
  if (pip) {
    pip.close();
    pip = null;
  }
  $("p1-synced-back").hidden = !back;
  if (back) $("p1-synced-back").label = `Back to ${back.vod.title}`;
  $("p1-info").textContent = vod.title;
  $("p1-info").title = vod.title;
  $("p1-hint").hidden = true;
  $("p1-media").hidden = false;
  showNote("p1", "Loading…");
  // A live VOD's length from Twitch can lag behind (or be 0): the timeline must reach the moment we open at, or its view is
  // clamped to the start and shows nothing (badrfly 2026-11-20, Show in Player 1 on a live VOD)
  const loaded = timeline.open({ ...vod, lengthSeconds: Math.max(Number(vod.lengthSeconds) || 0, start || 0) });
  try {
    await player1.open(vod.vodId, start);
    if (paused) player1.pause();
    if (p1Vod !== vod) return;
    showNote("p1", playerNotes(player1), 8000);
  } catch (error) {
    if (p1Vod !== vod) return;
    if (isChannelRefusal(error)) {
      await loaded.catch(() => {});
      if (p1Vod === vod) unloadPlayer1(channelRefusalText(error));
      return;
    }
    showNote("p1", `Couldn't play this VOD: ${error.message}`);
  }
  await loaded;
  if (p1Vod !== vod) return;
  // A video being linked keeps its picks across VODs; one saved in this tab before a reload comes back with the first VOD
  if (!linkingResumed) {
    linkingResumed = true;
    if (!timeline.linking) timeline.resumeLinking(savedLinking());
  }
  renderLinkTray(timeline.linking);
  renderStreamArrows();
}

let linkingResumed = false;

// Player 1 on a VOD at a moment: seeks when it's already open, else opens it (paused there)
async function openVodAt(vodId, seconds = 0) {
  const id = String(vodId);
  timelineTabs.pick("p1");
  if (p1Vod && String(p1Vod.vodId) === id) {
    player1.pause();
    player1.seek(seconds);
    return;
  }
  const known = knownVod(id);
  if (known) {
    await openVod(known, { start: seconds, paused: true });
    return;
  }
  const info = await vodInfo(id);
  if (!info) {
    toast("Couldn't open that VOD.", "error");
    return;
  }
  await openVod({ vodId: String(info.vodId), title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive, createdAtUtc: info.streamDateUtc },
    { start: seconds, paused: true });
}

// Player 1's arrows: the channel's previous / next broadcast (from the library's VOD lists), e.g. to go on linking a video in the
// next day's stream
async function renderStreamArrows() {
  const vod = p1Vod;
  const show = (id, target) => {
    $(id).hidden = !target;
    $(id).dataset.vodId = target ? String(target.vodId) : "";
    if (target) $(id).label = `${id === "p1-prev-stream" ? "Previous" : "Next"} stream: ${target.title}`;
  };
  if (!vod || p1Back) {
    show("p1-prev-stream", null);
    show("p1-next-stream", null);
    return;
  }
  const [older, newer] = await Promise.all([adjacentVod(vod.vodId, "older"), adjacentVod(vod.vodId, "newer")]);
  if (p1Vod !== vod) return;
  show("p1-prev-stream", older);
  show("p1-next-stream", newer);
}

function stepStream(id) {
  const target = knownVod($(id).dataset.vodId);
  if (target) openVod(target);
}

// Player 1 back to empty (its drop hint), with a note why: a VOD the team may no longer open (badrfly 2026-11-20)
function unloadPlayer1(text) {
  p1Away = null; // (so ending row focus doesn't open a VOD again)
  if (rowFocus) endRowFocus();
  p1Vod = null;
  p1Back = null;
  renderStreamArrows();
  if (pip) {
    pip.close();
    pip = null;
  }
  player1.close();
  timeline.close();
  $("p1-synced-back").hidden = true;
  $("p1-info").textContent = "";
  $("p1-info").title = "";
  $("p1-media").hidden = true;
  $("p1-hint").hidden = false;
  showNote("p1", text);
  refusalShown.p1 = Boolean(text);
  renderTools();
}

// A player left empty with a note why its VOD was refused: the note goes once the team's channels change again (assigned again)
const refusalShown = { p1: false, p2: false };

function playerNotes(player) {
  const notes = [];
  if (player.fallbackReason) notes.push(`Twitch player: our player couldn't play this VOD (${player.fallbackReason}).`);
  if (player.cleanSegments) notes.push("Saved audio plays over muted parts.");
  return notes.join(" ");
}

const noteTimers = {};
function showNote(which, text, hideAfter = 0) {
  clearTimeout(noteTimers[which]);
  const note = $(`${which}-note`);
  note.textContent = text;
  note.hidden = !text;
  if (text && hideAfter) noteTimers[which] = setTimeout(() => { note.hidden = true; }, hideAfter);
}

// ---------- Player 2: what's loaded (a clip, video or segment, or one of their edits) ----------
//
// badrfly 2026-11-17, "the VOD is the project": what Player 2 changes is saved as an EDIT of the clip or video it was loaded from (the
// server's projects, never called that on the page). A clip, video or segment loads as itself (store.isDraft); the first change makes
// it a new edit ("clip · Edit 2"). Everything in one edit comes from one VOD.

// Opens a saved edit
async function openProject(id, { show = true, quiet = false } = {}) {
  if (DEMO) {
    toast("Edits aren't available in the demo.");
    return false;
  }
  const opened = ++p2Opened;
  try {
    await store.openProject(id);
  } catch (error) {
    if (!quiet) toast(`Couldn't open the edit: ${error.message}`, "error");
    if (error.status === 404) saveSetting(LAST_PROJECT_KEY, null);
    return false;
  }
  if (opened !== p2Opened) return false;
  projectOpened();
  if (show) timelineTabs.pick("p2");
  return true;
}

// The title of an item as Player 2 shows it
function itemTitle({ kind, data }) {
  if (kind === "segment") return data.label || "Segment";
  return data.title || (kind === "video" ? "Video" : "Clip");
}

// A clip, video or segment on its own in Player 2 (replaces what was loaded). Nothing is saved until it's changed.
async function loadInPlayer2(item, { show = true, quiet = false, original = false } = {}) {
  if (DEMO) {
    toast("Player 2 isn't available in the demo.");
    return false;
  }
  const pieces = piecesFromItem(item);
  if (!pieces?.length) {
    if (!quiet) toast(item.kind === "clip" ? "Twitch didn't say which VOD this clip is from, so it can't be loaded in Player 2." : "That can't be loaded in Player 2.", "error");
    return false;
  }
  const opened = ++p2Opened;
  const data = item.data;
  const vodId = Number(data.vodId);
  // An item left on the page from before the app Owner revoked its channel: refused, Player 2 shows why (badrfly 2026-11-20)
  try {
    await checkVodAccess(vodId);
  } catch (error) {
    if (opened === p2Opened) await refusePlayer2(error);
    return false;
  }
  if (opened !== p2Opened) return false;
  if (item.kind === "clip" || item.kind === "video") noteOpened(item.kind, data);
  const title = itemTitle(item);
  const parent = item.kind === "clip" ? { entryId: data.id } : item.kind === "video" ? { videoId: data.id } : {};
  // The new edit's name: "clip · Edit n", counting the item's edits so far
  let count = 0;
  try {
    const query = parent.entryId ? `entryId=${parent.entryId}` : parent.videoId ? `videoId=${parent.videoId}` : `vodId=${vodId}`;
    const edits = await api("GET", `/api/projects?${query}&limit=1000`);
    count = parent.entryId || parent.videoId ? edits.length : edits.filter((e) => !e.entryId && !e.videoId && e.title.startsWith(`${title} · `)).length;
    // badrfly 2026-11-17: something you already edited opens as your newest edit of it, so your work never looks lost behind the
    // original (Player 2's menu still loads the original: original = true)
    const mine = original ? null : await newestOwnEdit(item, edits);
    if (mine && opened === p2Opened) {
      if (!quiet) toast(`Opened your latest edit "${mine.title}". The original is in Player 2's menu.`);
      return openProject(mine.id, { show, quiet });
    }
  } catch {
    // The name is only a starting point
  }
  if (opened !== p2Opened) return false;
  const content = emptyContent();
  content.pieces = pieces.map((piece) => ({ ...piece, trackId: content.tracks[0].id }));
  await store.openDraft({
    title,
    editTitle: `${title} · Edit ${count + 1}`,
    vodId,
    ...parent,
    source: { kind: item.kind, id: data.id },
    sourceItem: { kind: item.kind, data },
    canEdit: can("AddEntries") && !me?.support,
    content,
  });
  if (opened !== p2Opened) return false;
  projectOpened();
  if (show) timelineTabs.pick("p2");
  return true;
}

// The member's newest horizontal edit of an item, from its edits list (a segment's edits sit on the VOD: their pieces tell)
async function newestOwnEdit(item, edits) {
  if (!me?.id) return null;
  const own = edits.filter((e) => e.userId === me.id && !e.vertical)
    .sort((a, b) => Date.parse(b.updatedAtUtc) - Date.parse(a.updatedAtUtc));
  if (item.kind !== "segment") return own[0] ?? null;
  for (const edit of own.filter((e) => !e.entryId && !e.videoId).slice(0, 10)) {
    const full = await api("GET", `/api/projects/${edit.id}`);
    const pieces = full.content?.pieces ?? [];
    if (pieces.length && pieces.every((p) => p.source?.kind === "segment" && Number(p.source.id) === Number(item.data.id))) return edit;
  }
  return null;
}

// Where Player 2 is in the VOD tree (ui/p2-tree.js)
function p2Loaded() {
  if (!store?.open) return null;
  const project = store.project;
  return {
    vodId: store.vodId,
    editId: store.isDraft ? null : project.id,
    entryId: project.entryId ?? null,
    videoId: project.videoId ?? null,
    segmentId: store.isDraft && project.source?.kind === "segment" ? project.source.id : null,
  };
}

function projectOpened() {
  const project = store.project;
  // (a new vertical edit isn't remembered until it's saved: reloading then opens what it was made from)
  if (!store.isDraft) saveSetting(LAST_PROJECT_KEY, { edit: project.id });
  else if (!store.isVertical) saveSetting(LAST_PROJECT_KEY, { kind: project.source.kind, id: project.source.id, vodId: project.vodId });
  playback.stop();
  playback.seek(0, { quiet: true });
  timeline2.refresh({ reset: true });
  $("p2-hint").hidden = true;
  $("p2-media").hidden = false;
  syncVerticalView();
  if (projectVerticalOpen()) refreshProjectVertical();
  renderPlayer2();
  renderTools();
}

// What was in Player 2 last time: an edit, or a clip / video / segment (read again)
async function restoreLastLoaded() {
  const last = loadSetting(LAST_PROJECT_KEY, null);
  if (!last || DEMO) return;
  if (typeof last === "number") return openProject(last, { show: false, quiet: true });
  if (last.edit) return openProject(last.edit, { show: false, quiet: true });
  // A loaded clip / video / segment changed just before the page closed was saved as a new edit as it closed: open that edit
  const pending = ProjectStore.pendingDraft();
  if (pending && pending.kind === last.kind && Number(pending.id) === Number(last.id) && me?.id) {
    try {
      const query = last.kind === "clip" ? `entryId=${last.id}` : last.kind === "video" ? `videoId=${last.id}` : `vodId=${last.vodId}`;
      const edits = await api("GET", `/api/projects?${query}&userId=${me.id}&limit=20`);
      const made = edits.filter((e) => !e.vertical && Date.parse(e.createdAtUtc) >= pending.at - 60_000)
        .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc))[0];
      if (made) {
        ProjectStore.claimDraft(made.id);
        return openProject(made.id, { show: false, quiet: true });
      }
    } catch {
      // Falls back to the original
    }
  }
  try {
    let data = null;
    if (last.kind === "clip") data = await api("GET", `/api/entries/${last.id}`);
    else if (last.kind === "video") data = await api("GET", `/api/videos/${last.id}`);
    else if (last.kind === "segment") data = (await api("GET", `/api/markers?vodIds=${last.vodId}`)).find((m) => m.id === last.id) ?? null;
    if (data) await loadInPlayer2({ kind: last.kind, data }, { show: false, quiet: true });
    else saveSetting(LAST_PROJECT_KEY, null);
  } catch {
    // Gone or not open to the team any more
  }
}

// Player 2 can't play what it was given (the team may no longer open its VOD): back to empty with its drop hint and a note why. A
// saved edit isn't touched (the server hides it anyway); only what's loaded closes.
async function refusePlayer2(error) {
  if (store?.open) await closeProject();
  else {
    $("p2-media").hidden = true;
    $("p2-hint").hidden = false;
  }
  showNote("p2", channelRefusalText(error));
  refusalShown.p2 = true;
}

async function closeProject() {
  verticalReturn = null;
  hideVerticalView();
  p2Opened++;
  playback.stop();
  player2.close();
  await store.close();
  saveSetting(LAST_PROJECT_KEY, null);
  timeline2.refresh({ reset: true });
  // Closing the store re-reads the (now empty) content, which seeks and shows the gap's black: stop again so the hint shows
  playback.stop();
  $("p2-media").hidden = true;
  $("p2-hint").hidden = false;
  showNote("p2", "");
  renderPlayer2();
  renderTools();
}

// ---------- "Sync with" (badrfly 2026-11-20): the same moment from another streamer's point of view ----------
//
// Right-click a piece in Player 2 > Sync with…, then click a channel's picture in the library's channel bar. The server turns the
// piece's VOD range into wall-clock time (the VOD's stream start + seconds) and finds that channel's past broadcast recording then;
// what matched goes on a new row (named after the channel) right under the piece's row, at the same place on the edit's clock. Being
// lower, it's covered and silent until the member cuts the row above away where the other view is better. Where the other stream
// wasn't recording, that part is left out (a gap). Twitch's start times can be a few seconds apart between streams: "Slide the
// footage" (< / >) lines it up.

const SLIDE_STEP = 0.1;

function syncWith(pieces) {
  if (!store.open || !store.canEdit || !pieces.length) return;
  if (teamChannels().length < 2) {
    toast("Your team has only one channel, so there's no one else to sync with. The app Owner can give it more.", "error");
    return;
  }
  if (rowFocus) endRowFocus();
  const ids = pieces.map((p) => p.id);
  toast("Sync with: click a streamer's picture above the list. Esc cancels.");
  pickChannel({ onPick: (login) => runSync(ids, login) });
}

async function runSync(ids, login) {
  const content = store.content;
  const pieces = (content?.pieces ?? []).filter((p) => ids.includes(p.id)).sort((a, b) => a.at - b.at);
  if (!pieces.length) return;
  const name = teamChannels().find((c) => c.login === login)?.displayName ?? login;
  const added = [];
  let asked = 0;
  let covered = 0;
  let empty = null;
  const notes = new Set();
  let firstMatch = null;
  for (const piece of pieces) {
    let answer;
    try {
      const query = new URLSearchParams({ channel: login, start: String(piece.start), end: String(piece.end) });
      answer = await api("GET", `/api/vods/${piece.vodId}/sync?${query}`);
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    asked += piece.end - piece.start;
    covered += answer.coveredSeconds;
    if (!answer.matches.length) empty ??= answer;
    for (const match of answer.matches) {
      firstMatch ??= { piece, match };
      if (match.restricted) notes.add(`${name}'s VOD is for subscribers only, so it may only play in Twitch's player.`);
      if (match.isLive) notes.add(`${name} is still live: the row ends where the stream is now.`);
      added.push({
        trackId: null, at: piece.at + match.offset, vodId: match.vodId, start: match.start, end: match.end,
        source: { kind: "vod", id: match.vodId }, label: `${name} · ${piece.label || "Synced"}`.slice(0, 200), color: piece.color,
      });
    }
  }
  // The pieces might have been moved or trimmed while Twitch was asked
  const now = (store.content?.pieces ?? []).filter((p) => ids.includes(p.id)).sort((a, b) => a.at - b.at);
  if (JSON.stringify(now) !== JSON.stringify(pieces)) {
    toast("The pieces changed while looking for the other stream. Try Sync with again.", "error");
    return;
  }
  if (!added.length) {
    toast(noSyncText(name, empty), "error");
    return;
  }
  // Start times are a few seconds off between streamers: unless this pair was lined up before, listen to both and line them up by sound
  const first = firstMatch;
  let lined = first.match.lined !== "start" ? "saved" : null;
  if (!lined) {
    const listening = toast(`Listening to both streams to line ${name}'s up…`);
    const len = first.match.end - first.match.start;
    const into = Math.max(0, (len - AUDIO_MATCH_SECONDS) / 2);
    try {
      const result = await api("POST", `/api/vods/${first.piece.vodId}/sync/${first.match.vodId}/audio`, {
        at: first.piece.start + first.match.offset + into, otherAt: first.match.start + into, seconds: Math.min(AUDIO_MATCH_SECONDS, Math.max(10, len)),
      });
      if (result.trusted) {
        lined = "audio";
        for (const piece of added) {
          if (piece.vodId !== first.match.vodId) continue;
          piece.start += result.shift;
          piece.end += result.shift;
          if (piece.start < 0) {
            piece.at -= piece.start;
            piece.start = 0;
          }
        }
        notes.add(`Lined up by sound (${Math.round(result.confidence * 100)}% sure; moved ${result.shift > 0 ? "+" : ""}${result.shift.toFixed(2)} s from the start times).`);
      } else {
        notes.add(`Couldn't line up by sound: ${result.reason} It uses the stream start times; < / > or Alt+drag slide it.`);
      }
    } catch (error) {
      notes.add(`Couldn't line up by sound (${error.message}). It uses the stream start times.`);
    }
    listening.remove();
  } else {
    notes.add(`Lined up like the last time these two were synced (${first.match.lined === "audio" ? "by sound" : "by hand"}).`);
  }
  const usable = added.filter((p) => p.end - p.start >= 0.5);
  const still = (store.content?.pieces ?? []).filter((p) => ids.includes(p.id));
  if (!usable.length || still.length !== pieces.length) return;
  if (!timeline2.addSyncedRows(pieces, usable, name)) return;
  const missing = asked - covered;
  const parts = [missing > 0.5
    ? `Added ${name}'s view of ${formatDuration(covered)} (${name} wasn't streaming for ${formatDuration(missing)} of it, left as a gap).`
    : `Added ${name}'s view on a row under it.`];
  parts.push("It plays once you cut away the row above.");
  toast([...parts, ...notes].join(" "));
}

// A slide (< / >, Alt+drag, Slide the footage) lines a piece up with the piece over it on another VOD: saved for the pair, so the next
// "Sync with" between those two VODs lands lined up (debounced per pair)
const AUDIO_MATCH_SECONDS = 45;
const pairSaves = new Map();

// The piece another one is lined up against: the closest row above (else below) with another VOD's piece playing at the same time
function partnerOf(piece, content = store.content) {
  if (!content || !piece) return null;
  const rowOf = (p) => content.tracks.findIndex((t) => t.id === p.trackId);
  const end = piece.at + (piece.end - piece.start);
  const partners = content.pieces
    .filter((p) => p.vodId !== piece.vodId && p.trackId !== piece.trackId && p.at < end - 0.5 && p.at + (p.end - p.start) > piece.at + 0.5)
    .sort((a, b) => {
      const da = rowOf(piece) - rowOf(a);
      const db = rowOf(piece) - rowOf(b);
      return (da > 0 ? da : 100 - da) - (db > 0 ? db : 100 - db);
    });
  return partners[0] ?? null;
}

function rememberSlide(ids) {
  const content = store.content;
  if (!content || !store.canEdit) return;
  for (const id of ids) {
    const piece = content.pieces.find((p) => p.id === id);
    const partner = partnerOf(piece, content);
    if (!partner) continue;
    const end = piece.at + (piece.end - piece.start);
    const t = (Math.max(piece.at, partner.at) + Math.min(end, partner.at + (partner.end - partner.start))) / 2;
    const at = partner.start + (t - partner.at);
    const otherAt = piece.start + (t - piece.at);
    const key = `${partner.vodId}:${piece.vodId}`;
    clearTimeout(pairSaves.get(key));
    pairSaves.set(key, setTimeout(() => {
      pairSaves.delete(key);
      api("PUT", `/api/vods/${partner.vodId}/sync/${piece.vodId}`, { at, otherAt }).catch(() => {});
    }, 800));
  }
}

// "Line up side by side…" (piece menu): both streams playing next to each other, nudged until they match (ui/sync-view.js)
async function sideBySide(piece) {
  const top = partnerOf(piece);
  if (!top || !store.canEdit) return;
  if (rowFocus) endRowFocus();
  playback?.pause();
  if (player1?.playing()) player1.pause();
  const rowName = (p) => store.content.tracks.find((t) => t.id === p.trackId)?.name || p.label || "Stream";
  const id = piece.id;
  await openSyncView({
    top: structuredClone(top), below: structuredClone(piece), now: playback?.time(),
    names: { top: rowName(top), below: rowName(piece) },
    onSave: (delta) => {
      const now = store.content?.pieces.find((p) => p.id === id);
      if (!now) {
        toast("That piece is gone, so nothing was moved.", "error");
        return;
      }
      if (timeline2.slideSelected(delta, [now])) toast(`Moved ${delta > 0 ? "+" : ""}${delta.toFixed(2)} s. These two VODs stay lined up for the next Sync with.`);
    },
  });
}

function noSyncText(name, answer) {
  const when = (iso) => formatDate(iso);
  const near = [
    answer?.before && `their stream before ended ${when(answer.before.endedAtUtc)}`,
    answer?.after && `the next started ${when(answer.after.startedAtUtc)}`,
  ].filter(Boolean);
  return `${name} wasn't streaming then (no past broadcast covers it)${near.length ? `: ${near.join(", ")}` : ""}.`;
}

// A new segment or clip from Player 1 goes into the open edit (not a clip loaded on its own, and only from the edit's VOD)
function addToProject(item) {
  if (!store.open || store.isDraft || !store.canEdit) return;
  const pieces = piecesFromItem(item);
  if (!pieces?.length || !store.takesVod(pieces[0].vodId)) return;
  timeline2.addItemAtPlayhead(pieces);
}

// Edits were made, renamed or deleted: the library's lines of them are read again
function editsChanged(vodId) {
  forgetTree(vodId);
  refreshEdits();
}

// An edit was made from what was loaded (its first change): the library follows
function onEditCreated(edit) {
  saveSetting(LAST_PROJECT_KEY, { edit: edit.id });
  editsChanged(edit.vodId);
  refreshLibraryFilters();
  toast(`Saved as a new edit: "${edit.title}". Rename it from the menu above Player 2.`);
}

// Store changes: the timeline, the preview and the buttons follow
function onProjectChanged() {
  timeline2?.refresh();
  playback?.refresh();
  if (projectVerticalOpen()) refreshProjectVertical();
  renderPlayer2();
  if (activePlayer === "p2") renderTools();
}

function p2Position() {
  return store?.open ? playback.time() : null;
}

function p2Seek(seconds) {
  if (!store.open) return;
  playback.seek(seconds);
  timeline2.dirty = true;
}

// Play / pause / volume / fullscreen buttons of player n (1 or 2)
function renderPlayerButtons(n, player, { ready = player.ready, playing = player.playing() } = {}) {
  for (const id of ["play", "back", "fwd", "full", "shot"]) $(`p${n}-${id}`).disabled = !ready;
  const app = player.ready && player.kind === "app";
  $(`p${n}-mute`).disabled = !app;
  $(`p${n}-volume`).disabled = !app;
  $(`p${n}-play`).name = playing ? "pause" : "play";
  $(`p${n}-play`).label = playing ? "Pause" : "Play";
  const volume = player.volume;
  if (volume) {
    $(`p${n}-volume`).value = Math.round(volume.volume * 100);
    $(`p${n}-mute`).name = volume.muted || volume.volume === 0 ? "speaker-slash" : volume.volume < 0.5 ? "speaker-low" : "speaker-high";
    $(`p${n}-mute`).label = volume.muted ? "Unmute" : "Mute";
  }
}

function renderPlayer1() {
  renderPlayerButtons(1, player1);
  $("p1-quality").disabled = !player1.ready;
  renderClock(player1.position());
}

function renderPlayer2() {
  if (!store) return;
  const open = store.open;
  const hasPieces = open && store.content.pieces.length > 0;
  renderPlayerButtons(2, player2, { ready: hasPieces, playing: playback.playing() });
  $("p2-vertical").hidden = false;
  $("p2-vertical").disabled = !open || DEMO;
  $("p2-vertical").label = open && store.isVertical ? "Back to Player 2 (horizontal)" : "New vertical edit (9:16) of what's loaded";
  $("p2-show").disabled = !hasPieces;
  $("p2-apply").disabled = !hasPieces || DEMO || !can("AddEntries");
  $("p2-rename").disabled = !open || !store.canEdit;
  $("p2-delete").disabled = !open;
  $("p2-export").disabled = !hasPieces || DEMO;
  $("p2-quality").disabled = !player2.ready;
  renderProjectPicker();
  renderP2Info();
}

// Top right of Player 2: the playhead, the project's length and how many pieces
function renderP2Info() {
  const info = $("p2-info");
  if (!store?.open) {
    info.replaceChildren();
    return;
  }
  const length = store.content.pieces.length ? Math.max(...store.content.pieces.map((p) => p.at + p.end - p.start)) : 0;
  const now = playback.time();
  info.replaceChildren(
    el("span", { class: "p2-length num" }, `${shortClock(now)} / ${shortClock(length)}`),
    el("span", { class: "p2-kind" }, `${store.content.pieces.length} piece${store.content.pieces.length === 1 ? "" : "s"} · ${store.content.tracks.length} row${store.content.tracks.length === 1 ? "" : "s"}`));
}

// "1:05.20" (hours only when there are any)
function shortClock(seconds) {
  const hundredths = Math.max(0, Math.floor(seconds * 100));
  return `${formatDuration(Math.floor(hundredths / 100))}.${String(hundredths % 100).padStart(2, "0")}`;
}

// ---------- Player 2's header: what's loaded and what can be done with it (badrfly 2026-11-21: picking moved into the library) ----------

function renderProjectPicker() {
  const open = store.open;
  const project = store.project;
  // A new vertical edit shows the name it will get (it's saved with its first change)
  const newVertical = open && store.isDraft && store.isVertical;
  $("p2-project-name").textContent = open ? (newVertical ? project.editTitle : project.title) || "Untitled edit" : "Nothing loaded";
  const state = open ? store.stateText() : "";
  $("p2-project-state").textContent = state;
  $("p2-project-state").classList.toggle("bad", state === "not saved");
  $("p2-project-pick").title = open
    ? newVertical ? `${project.editTitle} (a new vertical edit: your first change saves it)`
    : `${project.title}${store.isDraft ? (store.canEdit ? " (the original: your first change saves a new edit of it)" : "") : ""}${project.createdBy && !store.canEdit ? ` by ${project.createdBy} (${project.canCopy === false ? "view only" : "view only: make your own copy to change it"})` : ""}`
    : "Pick a clip, video or segment, or one of their edits";
  if ($("p2-project-menu").open) renderProjectMenu();
  player2Changed();
}

function renderProjectMenu() {
  const current = store.project;
  const item = (value, label, { icon = null, disabled = false } = {}) => el("sl-menu-item", { value, disabled },
    icon ? el("sl-icon", { slot: "prefix", library: "ph", name: icon }) : null, label);
  const entries = [];
  const canMake = can("AddEntries") && !me?.support && !DEMO;
  if (current) {
    // A teammate's vertical edit is exported and copied only with the role permissions for that (canDownload / canCopy)
    if (!DEMO) entries.push(item("export", store.isDraft ? "Add to the export list" : "Add this edit to the export list", { icon: "download-simple", disabled: !store.content.pieces.length || current.canDownload === false }));
    if (store.canEdit) entries.push(item("rename", store.isDraft ? "Save as a new edit…" : "Rename this edit", { icon: "pencil-simple" }));
    if (!store.isDraft) entries.push(item("copy", store.canEdit ? "Duplicate this edit" : "Make my own copy", { icon: "copy", disabled: !canMake || current.canCopy === false }));
    if (!store.isDraft && current.canDelete) entries.push(item("delete", "Delete this edit", { icon: "trash" }));
    entries.push(item("reveal", "Show in the library", { icon: "list-magnifying-glass" }));
    entries.push(item("close", "Unload (nothing is deleted)", { icon: "eject" }));
  } else {
    entries.push(item("none", "Double-click a clip, video, segment or edit in the library to load it", { icon: "info", disabled: true }));
  }
  $("p2-project-items").hidden = !entries.length;
  $("p2-project-items").replaceChildren(...entries);
}

async function renameProject() {
  if (!store.open || !store.canEdit) return;
  const draft = store.isDraft;
  const title = await askText({
    heading: draft ? "Save as a new edit" : "Rename this edit",
    label: "Name",
    value: draft ? store.project.editTitle : store.project.title,
    confirm: draft ? "Save" : "Rename",
  });
  if (title == null || !title.trim()) return;
  try {
    await store.rename(title.trim());
    editsChanged(store.vodId);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function copyEdit(id) {
  if (store.project?.id === id) await store.saveNow();
  try {
    const copy = await api("POST", `/api/projects/${id}/copy`, {});
    editsChanged(copy.vodId);
    await openProject(copy.id);
    toast(`Made "${copy.title}".`);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function deleteEdit(edit) {
  if (store.project?.id === edit.id) {
    await store.saveNow();
    await api("DELETE", `/api/projects/${edit.id}`);
    await closeProject();
  } else {
    await api("DELETE", `/api/projects/${edit.id}`);
  }
  editsChanged(edit.vodId);
  refreshLibraryFilters();
}

async function deleteProject() {
  if (!store.open || store.isDraft) return;
  const project = store.project;
  const ok = await confirmAction({
    heading: "Delete this edit?",
    text: `"${project.title || "Untitled edit"}" is deleted for the whole team. The clip, video and segments it was made from stay as they are.`,
    confirm: "Delete",
  });
  if (!ok) return;
  try {
    await deleteEdit(project);
  } catch (error) {
    toast(error.message, "error");
  }
}

// The loaded clip / video / segment as itself, or the edit
async function exportProject() {
  if (!store.open) return;
  if (store.isDraft && store.isVertical) {
    // A new vertical edit is exported as itself: save it first
    await store.ensureSaved();
    if (store.isDraft) return;
  }
  if (store.isDraft) {
    addToExport(store.project.sourceItem, { quiet: false });
    return;
  }
  if (store.project.canDownload === false) {
    toast("Your role doesn't let you export teammates' vertical edits.", "error");
    return;
  }
  addToExport({ kind: "project", data: { ...store.project, content: store.content } }, { quiet: false });
}

async function exportEdit(edit) {
  try {
    const full = await api("GET", `/api/projects/${edit.id}`);
    addToExport({ kind: "project", data: full }, { quiet: false });
  } catch (error) {
    toast(error.message, "error");
  }
}

function setUpProjectPicker() {
  setUpTree({
    root: null,
    loaded: p2Loaded,
    player1VodId: () => timeline?.vodId ?? 0,
    knownVodTitle: (vodId) => (Number(p1Vod?.vodId) === Number(vodId) ? p1Vod.title : knownVodTitle(vodId)),
    onLoadItem: (item) => loadInPlayer2(item, { original: true }),
    onOpenEdit: (id) => openProject(id),
    onLoadVod: (vodId, title) => {
      const known = knownVod(vodId);
      openVod(known ?? { vodId: String(vodId), title });
      timelineTabs.pick("p1");
    },
    onRenameEdit: (_edit, title) => store.rename(title),
    onDeleteEdit: deleteEdit,
    onCopyEdit: (edit) => copyEdit(edit.id),
    onExportEdit: exportEdit,
    onAddSegments: (video) => addSegmentsTo(video),
    vodDay: (vodId) => vodDay(vodId, () => refreshEdits()),
    marks: myMarks,
    toggleStar,
    onChanged: (kind) => {
      if (kind === "edit") refreshEdits();
      if (kind === "clip") refreshClips();
      if (kind === "video" || kind === "segment") refreshVideos();
      refreshLibraryFilters();
    },
    close: () => $("p2-project-menu").hide(),
  });
  $("p2-project-menu").addEventListener("sl-show", (event) => {
    if (event.target !== $("p2-project-menu")) return;
    renderProjectMenu();
  });
  $("p2-project-items").addEventListener("sl-select", (event) => {
    const value = event.detail.item.value;
    if (value === "export") exportProject();
    else if (value === "rename") renameProject();
    else if (value === "copy") copyEdit(store.project.id);
    else if (value === "delete") deleteProject();
    else if (value === "close") closeProject();
    else if (value === "reveal") revealLoaded();
  });
}

function renderClock(position) {
  const ready = player1.ready && position != null;
  $("p1-clock").hidden = !ready || player1.live;
  if (ready && !player1.live) $("p1-clock").textContent = `${formatClock(position)} / ${formatClock(player1.duration() || p1Vod?.lengthSeconds || 0)}`;
  const live = $("p1-live");
  live.hidden = !ready || !player1.live;
  if (!live.hidden) live.classList.toggle("behind", player1.livePosition() - position > 8);
}

// Quality menu: Auto + the renditions (app player), and the switch between our player and Twitch's
function renderQualityMenu(player, itemsId) {
  const items = [];
  if (player.kind === "app") {
    const levels = player.levels();
    const current = player.level;
    items.push(el("sl-menu-item", { type: "checkbox", value: "level:-1", checked: current === -1 }, "Auto"));
    for (const level of levels) {
      items.push(el("sl-menu-item", { type: "checkbox", value: `level:${level.index}`, checked: current === level.index }, level.name));
    }
    items.push(el("sl-divider"));
  }
  items.push(el("sl-menu-item", { value: "switch" },
    el("sl-icon", { slot: "prefix", library: "ph", name: "swap" }),
    player.kind === "app" ? "Use the Twitch player" : "Use the app player"));
  $(itemsId).replaceChildren(...items);
}

// Only one player plays at a time: starting one pauses the other
function playAlone(player, other) {
  const playing = player.playing();
  if (playing && !player.wasPlaying && other.playing()) {
    other.pause();
    if (other === player2) playback.pause();
  }
  player.wasPlaying = playing;
}

function wirePlayerButtons(n, which) {
  const player = () => pair(which).player;
  $(`p${n}-play`).addEventListener("click", () => playPause(which));
  $(`p${n}-back`).addEventListener("click", () => skip(which, -10));
  $(`p${n}-fwd`).addEventListener("click", () => skip(which, 10));
  $(`p${n}-full`).addEventListener("click", () => player().fullscreen());
  $(`p${n}-shot`).addEventListener("click", () => screenshot(which));
  $(`p${n}-mute`).addEventListener("click", () => {
    const volume = player().volume;
    if (!volume) return;
    if (volume.volume === 0) player().setVolume(0.5, false);
    else player().setVolume(null, !volume.muted);
  });
  $(`p${n}-volume`).addEventListener("sl-input", (event) => player().setVolume(Number(event.target.value) / 100, false));
}

function setUpPlayer1() {
  player1 = new MediaPlayer($("p1-media"), {
    onTick: (position) => {
      renderClock(p1Position() ?? position);
      pip?.follow();
      if (rowFocus?.which === "p1") focusTickP1(p1Position());
      else p1Tick(position);
    },
    onState: () => {
      playAlone(player1, player2);
      renderPlayer1();
    },
    // (not while checked rows play another VOD in it: the timeline stays on this one)
    onDetails: (details) => {
      if (String(player1.vodId) === String(p1Vod?.vodId)) timeline.setDetails(details);
    },
  });
  timeline = new Timeline({
    // While checked rows play, Player 1 may be on a synced row's VOD: the timeline's clock is still this VOD's
    onSeek: (seconds) => player1.seek(p1Away ? seconds + p1Away.shift : seconds),
    getPosition: () => p1Position(),
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    onChange: () => {
      renderTools();
      // A stored clip's re-trim waits 3 s (it downloads again)
      $("p1-info").toggleAttribute("data-saving", Boolean(timeline?.pendingTrim));
    },
    onClipsChanged: refreshClips,
    // Several selected clips (roadmap 1b): the team's statuses, set on all of them
    bulkStatusItems: (entries) => statusItemsFor("clip", entries),
    // ... a video of them, and the export list (roadmap 1c / 1d)
    bulkMenuItems: (items) => p1BulkItems(items),
    onVideosChanged: refreshVideos,
    onExport: exportFromTimeline,
    // A new segment or clip goes into the edit open in Player 2 (at its playhead) when it's of the same VOD, without switching the tab
    onCreated: (item) => {
      // A segment marked on a synced row is the other VOD's
      const synced = timeline.syncedRow(item.data.rowId);
      addToProject(synced
        ? { kind: item.kind, data: { ...(item.data.raw ?? item.data), vodId: String(synced.vodId) } }
        : { kind: item.kind, data: { ...item.data, vodId: timeline.vodId, vodLengthSeconds: timeline.length() } });
    },
    onEdited: () => {},
    onFocusRow: (rowId) => focusRow("p1", rowId),
    onLinkingChanged: (linking) => renderLinkTray(linking),
    vodDay: (vodId) => vodDay(vodId, () => { timeline.dirty = true; }),
    onOpenVodAt: openVodAt,
    onAddSyncedRow: addSyncedRow,
    onPip: togglePip,
    pipShowing: (row) => Boolean(pip && String(pip.vodId) === String(row.vodId)),
  });
  $("p1-synced-back").addEventListener("click", backFromSynced);
  $("p1-prev-stream").addEventListener("click", () => stepStream("p1-prev-stream"));
  $("p1-next-stream").addEventListener("click", () => stepStream("p1-next-stream"));
  setUpLinkTray({
    timeline,
    currentVodId: () => p1Vod?.vodId ?? null,
    openVodAt,
    seek: (seconds) => {
      timelineTabs.pick("p1");
      player1.pause();
      player1.seek(seconds);
    },
    titleValue: () => $("item-title").value ?? "",
  });
  wirePlayerButtons(1, "p1");
  $("p1-live").addEventListener("click", () => {
    player1.seek(player1.livePosition());
    player1.play();
  });
  $("p1-quality-menu").addEventListener("sl-show", () => renderQualityMenu(player1, "p1-quality-items"));
  $("p1-quality-items").addEventListener("sl-select", async (event) => {
    const value = event.detail.item.value;
    if (value === "switch") {
      showNote("p1", "Switching player…");
      try {
        await player1.switchPlayer();
        showNote("p1", "");
      } catch (error) {
        showNote("p1", `Couldn't play this VOD: ${error.message}`);
      }
    } else if (value.startsWith("level:")) {
      player1.level = Number(value.slice(6));
    }
  });
  renderPlayer1();
}

// Player 1 skips the cut-out parts of the selected segment or clip while it plays just that
function p1Tick(position) {
  if (!player1.playing() || player1.stopAt == null) return;
  const item = timeline.selectedItem();
  if (item?.cuts?.length) {
    const to = skipCut(item.cuts, position, player1.stopAt);
    if (to === "end") {
      player1.stopAt = null;
      player1.pause();
    } else if (to != null) {
      player1.seek(to);
    }
  }
}

function setUpPlayer2() {
  player2 = new MediaPlayer($("p2-media"), {
    onTick: () => renderP2Info(),
    onState: () => {
      playAlone(player2, player1);
      playback?.playerPaused();
      renderPlayer2();
    },
  });
  store = new ProjectStore({
    onChange: onProjectChanged,
    onCreated: onEditCreated,
    onReplaced: () => timeline2.refresh(),
  });
  // A clip or segment changed on Player 1 (a trim, cut-out parts, an undo there): edits made from it follow on the server, and the open
  // one follows here, undo steps included, so a Player 2 undo never brings back what Player 1 took out
  window.addEventListener("clipco:source-sync", (event) => {
    store.followSources(event.detail);
  });
  playback = new ProjectPlayback(player2, {
    getContent: () => store.content,
    black: $("p2-black"),
    onState: () => {
      renderPlayer2();
      timeline2.dirty = true;
    },
  });
  playback.onError = (error) => {
    if (isChannelRefusal(error)) refusePlayer2(error);
    else showNote("p2", `Couldn't play this VOD: ${error.message}`, 8000);
  };
  // Same as Player 1: say when our player couldn't play the VOD and Twitch's took over (and why)
  playback.onOpened = () => showNote("p2", playerNotes(player2), player2.fallbackReason ? 15000 : 8000);
  $("p2-quality-menu").addEventListener("sl-show", () => renderQualityMenu(player2, "p2-quality-items"));
  $("p2-quality-items").addEventListener("sl-select", async (event) => {
    const value = event.detail.item.value;
    if (value === "switch") {
      showNote("p2", "Switching player…");
      try {
        await playback.switchPlayer();
        showNote("p2", playerNotes(player2), 8000);
      } catch (error) {
        showNote("p2", `Couldn't play this VOD: ${error.message}`);
      }
    } else if (value.startsWith("level:")) {
      player2.level = Number(value.slice(6));
    }
  });
  timeline2 = new ProjectTimeline(store, {
    vodDay: (vodId) => vodDay(vodId, () => { timeline2.dirty = true; }),
    getPosition: p2Position,
    onSeek: p2Seek,
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    loadItem: (item) => loadInPlayer2(item, { show: false }),
    onChange: () => {
      renderTools();
      if (projectVerticalOpen()) refreshProjectVertical();
    },
    onShowInPlayer1: showInPlayer1,
    onSyncWith: syncWith,
    onSlid: rememberSlide,
    partnerOf: (piece) => partnerOf(piece),
    onSideBySide: sideBySide,
    canClip: () => p2CanCreate(),
    maxClipSeconds: () => timeline.maxStoredClipSeconds,
    clipColor: () => timeline.lastClipColor,
    onClipRange: p2SaveClip,
    onFocusRow: (trackId) => focusRow("p2", trackId),
  });
  wirePlayerButtons(2, "p2");
  setUpProjectVertical($("player2"), {
    store,
    playback,
    player: player2,
    timeline: () => timeline2,
    onExit: () => toggleVertical(false),
    onUndoChanged: () => renderTools(),
    onScreenshot: () => screenshot("p2"),
  });
  $("p2-vertical").addEventListener("click", () => toggleVertical());
  $("p2-show").addEventListener("click", () => {
    // the piece playing at the playhead (the top one), else the selected one
    const now = playback.time();
    const s = stretchAt(flatten(store.content), now);
    const piece = s?.piece ?? timeline2.selectedPieces()[0];
    if (piece) showInPlayer1(piece);
    else toast("Move Player 2's playhead onto a piece first.");
  });
  $("p2-rename").addEventListener("click", renameProject);
  $("p2-delete").addEventListener("click", closeProject);
  $("p2-export").addEventListener("click", exportProject);
  $("p2-apply").addEventListener("click", () => applyEditToOriginals());
  setUpProjectPicker();
  renderPlayer2();
  // What was loaded last time
  restoreLastLoaded();
}

// Player 2's crosshair: Player 1 opens the piece's VOD at the moment under Player 2's playhead (the piece's start when the playhead
// isn't on it) and zooms out to the whole clip or segment it was copied from (selected when it's still there), else the piece
async function showInPlayer1(piece) {
  const vodId = String(piece.vodId);
  const now = playback.time();
  const on = now >= piece.at && now < piece.at + (piece.end - piece.start);
  const seconds = on ? piece.start + (now - piece.at) : piece.start;
  playback.pause();
  if (!p1Vod || String(p1Vod.vodId) !== vodId) {
    let info;
    try {
      info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(vodId)}`);
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    await openVod({ vodId: info.vodId, title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive }, { start: seconds, paused: true });
  } else {
    player1.pause();
    player1.seek(seconds);
  }
  timelineTabs.pick("p1");
  const focus = piece.source?.kind === "clip" ? { type: "clip", id: piece.source.id } : piece.source?.kind === "segment" ? { type: "marker", id: piece.source.id } : { type: null, id: null };
  const item = timeline.reveal(focus, { start: piece.start, end: piece.end });
  // the whole clip or segment, and the piece too if it reaches past it (a trim made in Player 2)
  if (item?.end != null) timeline.reveal(focus, { start: Math.min(item.start, piece.start), end: Math.max(item.end, piece.end) });
}

// ---------- synced rows on Player 1 (badrfly 2026-11-20) ----------
//
// "Add a synced row" (timeline footer): pick a streamer's picture, and a row of their VOD recording at the same time shows on this
// VOD's timeline, on this VOD's clock (lined up by the pair's saved sync, else by sound, else by the start times). What's marked on
// it is theirs: segments of their VOD, which a video can link with this VOD's. Player 1 keeps playing this VOD; the row's monitor
// button opens theirs at the same moment, and Back returns.

let p1Back = null;
const SYNC_ROW_WINDOW = 90 * 60;

function addSyncedRow(now) {
  if (!p1Vod || DEMO || me?.support || !can("AddEntries")) return;
  if (teamChannels().length < 2) {
    toast("Your team has only one channel, so there's no one else to sync with. The app Owner can give it more.", "error");
    return;
  }
  if (rowFocus) endRowFocus();
  toast("Add a synced row: click a streamer's picture above the list. Esc cancels.");
  pickChannel({ onPick: (login) => runSyncedRow(login, now) });
}

async function runSyncedRow(login, now) {
  const vod = p1Vod;
  if (!vod) return;
  const name = teamChannels().find((c) => c.login === login)?.displayName ?? login;
  const length = timeline.length() || vod.lengthSeconds || now + SYNC_ROW_WINDOW;
  const start = Math.max(0, Math.min(now, length) - SYNC_ROW_WINDOW);
  const end = Math.max(start + 1, Math.min(length, now + SYNC_ROW_WINDOW));
  let answer;
  try {
    const query = new URLSearchParams({ channel: login, start: String(Math.floor(start)), end: String(Math.ceil(end)) });
    answer = await api("GET", `/api/vods/${encodeURIComponent(vod.vodId)}/sync?${query}`);
  } catch (error) {
    toast(error.message, "error");
    return;
  }
  if (!answer.matches.length) {
    toast(noSyncText(name, answer), "error");
    return;
  }
  // The broadcast recording at the playhead, else the one nearest to it
  const into = now - start;
  const distance = (m) => (into < m.offset ? m.offset - into : into > m.offset + (m.end - m.start) ? into - m.offset - (m.end - m.start) : 0);
  const match = [...answer.matches].sort((a, b) => distance(a) - distance(b))[0];
  if (timeline.rows.some((r) => r.userId === me?.id && String(r.vodId) === String(match.vodId) && r.shift != null)) {
    toast(`You already have a synced row of ${name}'s VOD here.`);
    return;
  }
  // A video row (segments) and a clip row of their VOD, like the first rows of any VOD
  let row;
  try {
    row = await api("POST", `/api/vods/${encodeURIComponent(vod.vodId)}/rows`, { kind: "video", syncedVodId: Number(match.vodId) });
    await api("POST", `/api/vods/${encodeURIComponent(vod.vodId)}/rows`, { kind: "clip", syncedVodId: Number(match.vodId) });
  } catch (error) {
    toast(error.message, "error");
    if (!row) return;
  }
  const notes = [];
  if (match.restricted) notes.push(`${name}'s VOD is for subscribers only.`);
  if (match.isLive) notes.push(`${name} is still live.`);
  // Start times are a few seconds off between streamers: unless the pair was lined up before, listen to both
  if (match.lined === "start") {
    const listening = toast(`Listening to both streams to line ${name}'s up…`);
    const len = match.end - match.start;
    const at = Math.min(Math.max(now, start + match.offset), start + match.offset + Math.max(0, len - AUDIO_MATCH_SECONDS));
    const otherAt = match.start + (at - start - match.offset);
    try {
      const result = await api("POST", `/api/vods/${encodeURIComponent(vod.vodId)}/sync/${match.vodId}/audio`, {
        at, otherAt, seconds: Math.min(AUDIO_MATCH_SECONDS, Math.max(10, len)),
      });
      notes.push(result.trusted
        ? `Lined up by sound (${Math.round(result.confidence * 100)}% sure).`
        : `Couldn't line up by sound: ${result.reason} It uses the stream start times.`);
    } catch (error) {
      notes.push(`Couldn't line up by sound (${error.message}). It uses the stream start times.`);
    }
    listening.remove();
  }
  if (p1Vod !== vod) return;
  timeline.activeRowId = row.id;
  await timeline.load();
  timeline.renderHeads();
  timeline.renderFooter();
  toast([`Added rows of ${name}'s VOD (segments and clips), lined up with this one. What you mark on them is theirs.`, ...notes].join(" "));
}

// A synced row's monitor button: Player 1 opens that row's VOD at the same moment
async function openSynced(row, at) {
  const from = p1Vod;
  if (!from) return;
  let info;
  try {
    info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(row.vodId)}`);
  } catch (error) {
    toast(error.message, "error");
    return;
  }
  const paused = !player1.playing();
  await openVod({ vodId: info.vodId, title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive },
    { start: Math.max(0, at), paused, back: { vod: from, shift: row.shift } });
}

// ---------- their stream in Player 1's corner (badrfly 2026-11-20) ----------

let pip = null;

function togglePip(row) {
  if (pip && String(pip.vodId) === String(row.vodId)) {
    pip.close();
    pip = null;
  } else {
    pip?.close();
    pip = new SyncedPip($("p1-screen"), {
      vodId: row.vodId,
      title: timeline.rowTitle(row),
      // the row's shift now (a < / > slide changes it), and this VOD's moment
      shift: () => timeline.rows.find((r) => String(r.vodId) === String(row.vodId) && r.shift != null)?.shift ?? row.shift,
      clock: () => (p1Away ? null : p1Position()),
      playing: () => player1.playing(),
      onClose: () => {
        pip = null;
        timeline.renderHeads();
      },
      onSwap: () => openSynced(row, (p1Position() ?? 0) + row.shift),
    });
  }
  timeline.renderHeads();
}

async function backFromSynced() {
  const back = p1Back;
  if (!back) return;
  const at = player1.ready ? player1.position() - back.shift : 0;
  await openVod(back.vod, { start: Math.max(0, at), paused: true });
}

// ---------- moments and clips made from Player 2 ----------
//
// M and the clip tool on Player 2 work like on Player 1, on the VOD under the playhead (M) or the piece dragged across (the clip
// tool): the moment or clip goes on the member's own rows of that VOD (Player 1's timeline shows it). In a gap there's no VOD, so
// nothing is made. A clip starts and ends on the same VOD.

function p2CanCreate() {
  return Boolean(store?.open) && !DEMO && !me?.support && can("AddEntries");
}

// ---------- the screenshot button (badrfly 2026-11-21): the frame on screen, saved as a PNG (ui/snapshot.js) ----------

function screenshot(which) {
  if (which === "p2") {
    if (!store?.open) return;
    const point = p2VodPoint();
    if (!point) {
      toast("Nothing plays here (a gap is black in the video).", "error");
      return;
    }
    const vodTitle = knownVodTitle(point.vodId) || (Number(p1Vod?.vodId) === Number(point.vodId) ? p1Vod.title : "");
    if (projectVerticalOpen()) {
      saveScreenshot({ player: player2, vodId: point.vodId, seconds: point.seconds, title: `${store.project?.title || vodTitle || "Vertical"} (vertical)`,
        compose: verticalFrameCanvas });
      return;
    }
    saveScreenshot({ player: player2, vodId: point.vodId, seconds: player2.vodId === point.vodId ? player2.position() ?? point.seconds : point.seconds, title: vodTitle || store.project?.title });
    return;
  }
  if (!player1.ready) return;
  const vodId = player1.vodId;
  const title = Number(p1Vod?.vodId) === Number(vodId) ? p1Vod.title : knownVodTitle(vodId);
  saveScreenshot({ player: player1, vodId, seconds: player1.position(), title });
}

// The VOD and VOD second under Player 2's playhead, or null in a gap
function p2VodPoint() {
  const t = p2Position();
  if (t == null) return null;
  const s = stretchAt(flatten(store.content), t);
  if (!s?.piece) return null;
  return { vodId: String(s.piece.vodId), seconds: Math.round((s.vodStart + (t - s.at)) * 100) / 100 };
}

// The row Player 1 would use when it shows that VOD (it knows which rows are locked); otherwise the server picks the first one
function p1RowFor(vodId, kind) {
  if (String(timeline?.vodId) !== vodId) return undefined;
  const row = timeline.targetRow(kind);
  return row === false ? false : row ? row.id : undefined;
}

function p2NoVod() {
  toast("The playhead is on a gap: move it onto a piece to mark its VOD.");
}

function p2AddMoment() {
  if (!p2CanCreate()) return;
  const point = p2VodPoint();
  if (!point) return p2NoVod();
  const rowId = p1RowFor(point.vodId, "video");
  if (rowId === false) return toast("Your video rows are locked.");
  playback.pause();
  openItemEditor({
    title: "",
    color: timeline.lastColor,
    colors: true,
    placeholder: "Marker title",
    onApply: async (title, color) => {
      timeline.lastColor = color;
      try {
        const marker = await api("POST", `/api/vods/${point.vodId}/markers`, { seconds: point.seconds, endSeconds: null, label: title, color, rowId });
        if (marker.liveEntry) refreshClips();
        p1Reload(point.vodId);
        toast("Moment added to the VOD.");
      } catch (error) {
        toast(error.message, "error");
      }
    },
  });
}

// The clip tool dragged across a piece (ProjectTimeline): name it and give it a color, then it's saved on the VOD
function p2SaveClip({ vodId, start, end }) {
  if (!p2CanCreate()) {
    timeline2.clearClipDraft();
    return;
  }
  if (p1RowFor(vodId, "clip") === false) {
    timeline2.clearClipDraft();
    toast("Your clip rows are locked.");
    return;
  }
  playback.pause();
  openItemEditor({
    title: "",
    color: timeline.lastClipColor,
    placeholder: "Clip title (blank = the VOD's title)",
    onColor: (color) => {
      if (timeline2.clipDraft) timeline2.clipDraft.color = color;
      timeline2.dirty = true;
    },
    onCancel: () => timeline2.clearClipDraft(),
    onApply: async (title, color) => {
      timeline2.clearClipDraft();
      timeline.lastClipColor = color;
      try {
        await api("POST", "/api/entries", {
          vodId,
          title: title || null,
          color: color === "violet" ? null : color,
          trimMode: "Exact",
          startTime: trimValue(start),
          endTime: trimValue(end),
          rowId: p1RowFor(vodId, "clip") || undefined,
        });
        refreshClips();
        p1Reload(vodId);
        toast("Clip added to the VOD.");
      } catch (error) {
        toast(error.message, "error");
      }
    },
  });
}

function p1Reload(vodId) {
  if (String(timeline?.vodId) === vodId) timeline.load().catch(() => {});
}

// ---------- Apply to the originals (badrfly 2026-11-20: one button in Player 2's controls, for the whole timeline) ----------
//
// Every clip and segment the edit's pieces were copied from gets the range of all its pieces here (on its VOD), and what's between
// them as cut-out parts, like "Apply this trim to the original" did for one piece. Footage that plays from a VOD range (Sync with,
// a VOD range loaded) becomes segments on your rows: of this VOD on your own row, of another streamer's VOD on your synced row of it
// on this VOD's Player 1, made when there's none. Teammates' clips and segments ask Edit originals / Make copies once (with "Edit
// anyone's"), else copies go on your rows. Pieces from videos aren't applied.

const sameTime = (a, b) => Math.abs(a - b) < 0.05;

// The VOD-time parts a set of ranges covers, merged where they touch
function mergeRanges(ranges) {
  const merged = [];
  for (const r of [...ranges].sort((a, b) => a.start - b.start)) {
    const last = merged[merged.length - 1];
    if (last && r.start <= last.end + 0.01) last.end = Math.max(last.end, r.end);
    else merged.push({ start: r.start, end: r.end });
  }
  return merged;
}

async function applyEditToOriginals() {
  if (!store?.open || !store.content.pieces.length) return;
  if (!can("AddEntries")) return toast("Your role can't change clips or segments.", "error");
  const mainVod = String(store.vodId);
  const sources = new Map(); // "clip:12" -> { kind, id, vodId, pieces }
  const footage = new Map(); // vodId -> [{ start, end, label }] (VOD ranges that play)
  let skippedVideos = 0;
  for (const p of store.content.pieces) {
    const kind = p.source?.kind;
    if (kind === "clip" || kind === "segment") {
      const key = `${kind}:${p.source.id}:${p.vodId}`;
      if (!sources.has(key)) sources.set(key, { kind, id: p.source.id, vodId: String(p.vodId), pieces: [] });
      sources.get(key).pieces.push(p);
    } else if (kind !== "vod") {
      skippedVideos++;
    }
  }
  // VOD ranges: only the parts that play (a synced row is mostly covered by the row above it)
  for (const s of flatten(store.content)) {
    if (s.piece?.source?.kind !== "vod" || s.vodEnd - s.vodStart < 0.5) continue;
    const vodId = String(s.piece.vodId);
    if (!footage.has(vodId)) footage.set(vodId, []);
    footage.get(vodId).push({ start: s.vodStart, end: s.vodEnd, label: s.piece.label || store.project?.title || "" });
  }
  if (!sources.size && !footage.size) {
    return toast(skippedVideos ? "Pieces from videos can't be applied to their originals yet." : "Nothing here came from a clip, segment or VOD range.");
  }

  // Read each VOD's timeline once: who owns each original, which synced rows and segments already exist
  const timelines = new Map();
  const read = async (vodId) => {
    if (!timelines.has(vodId)) timelines.set(vodId, await api("GET", `/api/vods/${encodeURIComponent(vodId)}/timeline`));
    return timelines.get(vodId);
  };
  const jobs = [];
  const notes = [];
  const teammates = [];
  try {
    for (const src of sources.values()) {
      const data = await read(src.vodId);
      const original = src.kind === "clip" ? data.clips?.find((c) => c.id === src.id) : data.markers?.find((m) => m.id === src.id);
      const what = src.kind === "clip" ? "clip" : "segment";
      if (!original) {
        notes.push(`A ${what} some pieces came from isn't there any more.`);
        continue;
      }
      const parts = mergeRanges(src.pieces.map((p) => ({ start: p.start, end: p.end })));
      const start = parts[0].start;
      const end = parts[parts.length - 1].end;
      const cuts = parts.slice(1).map((p, i) => ({ start: parts[i].end, end: p.start }));
      const ownerId = src.kind === "clip" ? original.addedByUserId : original.createdByUserId;
      const own = ownerId != null && String(ownerId) === String(me?.id);
      // Already like this (applied before): nothing to change
      const was = src.kind === "clip" ? { start: original.start, end: original.end } : { start: original.seconds, end: original.endSeconds };
      const wasCuts = original.cuts ?? [];
      if (sameTime(was.start, start) && was.end != null && sameTime(was.end, end) && wasCuts.length === cuts.length
        && wasCuts.every((c, i) => sameTime(c.start, cuts[i].start) && sameTime(c.end, cuts[i].end))) continue;
      const job = { type: "source", src, original, what, start, end, cuts, own, copy: false };
      if (!own && original.canEdit) teammates.push(job);
      else if (!own) {
        if (src.kind === "clip" && original.kind !== "Vod") {
          notes.push(`"${original.title || "A Twitch clip"}" can't be copied.`);
          continue;
        }
        job.copy = true;
      }
      jobs.push(job);
    }
    for (const [vodId, ranges] of footage) {
      const parts = [];
      for (const r of mergeRanges(ranges)) parts.push({ ...r, label: ranges.find((x) => x.start <= r.start + 0.01 && x.end >= r.start)?.label ?? "" });
      if (vodId === mainVod) {
        const data = await read(mainVod);
        const mine = (data.markers ?? []).filter((m) => m.endSeconds != null && String(m.vodId ?? mainVod) === mainVod
          && data.rows?.some((r) => r.id === m.rowId && String(r.userId) === String(me?.id) && r.shift == null));
        const fresh = parts.filter((r) => !mine.some((m) => sameTime(m.seconds, r.start) && sameTime(m.endSeconds, r.end)));
        if (fresh.length) jobs.push({ type: "footage", vodId, parts: fresh, row: null });
      } else {
        const data = await read(mainVod);
        const row = data.rows?.find((r) => r.shift != null && String(r.vodId) === vodId && String(r.userId) === String(me?.id)
          && String(r.kind).toLowerCase() === "video");
        const there = row ? (data.markers ?? []).filter((m) => m.rowId === row.id) : [];
        const fresh = parts.filter((r) => !there.some((m) => sameTime(m.seconds, r.start) && sameTime(m.endSeconds, r.end)));
        if (fresh.length) jobs.push({ type: "footage", vodId, parts: fresh, row, synced: true, name: parts[0].label });
      }
    }
  } catch (error) {
    return toast(`Couldn't read the originals: ${error.message}`, "error");
  }
  if (!jobs.length) return toast(["Everything here is already on Player 1.", ...notes].join(" "));

  if (teammates.length) {
    const owners = [...new Set(teammates.map((j) => j.original.createdBy).filter(Boolean))];
    const choice = await chooseEditTarget({ what: teammates.length === 1 ? teammates[0].what : "clips and segments", owner: owners.join(" and ") || null });
    if (!choice) return;
    for (const j of teammates) j.copy = choice === "copy";
  }
  const count = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;
  const changed = jobs.filter((j) => j.type === "source" && !j.copy);
  const copied = jobs.filter((j) => j.type === "source" && j.copy);
  const lines = [];
  if (changed.length) {
    const clips = changed.filter((j) => j.what === "clip").length;
    const segs = changed.length - clips;
    lines.push(`${[clips && count(clips, "clip"), segs && count(segs, "segment")].filter(Boolean).join(" and ")} will match the pieces made from ${changed.length === 1 ? "it" : "them"} here (what's between them is cut out).`);
  }
  if (copied.length) lines.push(`Copies of ${count(copied.length, "teammate's item")} go on your rows, with these trims.`);
  for (const j of jobs.filter((x) => x.type === "footage")) {
    const go = j.parts.length === 1 ? "goes" : "go";
    lines.push(j.synced
      ? `${count(j.parts.length, "segment")} of ${j.name ? `"${j.name}"` : "the other stream"} that ${j.parts.length === 1 ? "plays" : "play"} here ${go} on ${j.row ? "your synced row of it" : "a new synced row of it on this VOD"}.`
      : `${count(j.parts.length, "segment")} for the VOD ranges that play here ${go} on your row.`);
  }
  if (skippedVideos) lines.push("Pieces from videos aren't applied.");
  const ok = await confirmAction({ heading: "Apply this edit to the originals on Player 1?", text: [...lines, ...notes].join(" "), confirm: "Apply", danger: false });
  if (!ok) return;

  const failed = [];
  const rewrites = new Map(); // piece id -> new source (copies)
  for (const job of jobs) {
    try {
      if (job.type === "source") {
        const { src, original } = job;
        let id = src.id;
        let made = null;
        if (job.copy) {
          made = await api("POST", src.kind === "clip" ? `/api/entries/${id}/copy` : `/api/markers/${id}/copy`, {});
          id = made.id;
          for (const p of src.pieces) rewrites.set(p.id, { ...p.source, id });
        }
        if (src.kind === "clip") {
          await api("PUT", `/api/entries/${id}/trim`, { startTime: String(job.start), endTime: String(job.end), cuts: job.cuts });
        } else {
          const marker = made ?? original;
          await api("PUT", `/api/markers/${id}`, { seconds: job.start, endSeconds: job.end, label: marker.label, color: marker.color, rowId: marker.rowId, cuts: job.cuts });
        }
      } else {
        let rowId = job.row?.id ?? null;
        if (job.synced && !rowId) {
          const row = await api("POST", `/api/vods/${encodeURIComponent(mainVod)}/rows`, { kind: "video", syncedVodId: Number(job.vodId) });
          await api("POST", `/api/vods/${encodeURIComponent(mainVod)}/rows`, { kind: "clip", syncedVodId: Number(job.vodId) }).catch(() => {});
          rowId = row.id;
        }
        for (const part of job.parts) {
          await api("POST", `/api/vods/${encodeURIComponent(job.vodId)}/markers`, {
            seconds: part.start, endSeconds: part.end, label: part.label.slice(0, 200), ...(rowId ? { rowId } : {}),
          });
        }
      }
    } catch (error) {
      failed.push(error.message);
    }
  }
  if (rewrites.size && store.canEdit) {
    store.edit((c) => {
      for (const p of c.pieces) if (rewrites.has(p.id)) p.source = rewrites.get(p.id);
    });
  }
  refreshClips();
  if (timeline?.vodId) timeline.load().catch(() => {});
  toast(failed.length ? `Applied, but ${count(failed.length, "change")} failed: ${failed[0]}` : "Applied to the originals on Player 1.", failed.length ? "error" : undefined);
}

// ---------- the vertical editor (ui/project-vertical.js) ----------
//
// badrfly 2026-11-17 ("Separate the vertical editor from Player 2"): a vertical (9:16) edit is its own edit with its own timeline. Player 2
// takes the whole width and shows the landscape source of the piece under the playhead with its crop boxes, the live 9:16 result and
// the layout tools; the timeline below is the vertical edit's. Horizontal edits (Player 2) have no boxes or keyframes. The phone button
// makes a NEW vertical edit, copied from what Player 2 has loaded (its pieces and cuts, no boxes); after that the two are separate.
// A vertical edit loaded from anywhere opens straight in the vertical editor.

let verticalReturn = null; // what Player 2 had loaded before the phone button: { edit: id } or { item: { kind, data } }

function showVerticalView() {
  if (projectVerticalOpen()) return;
  player1.pause();
  $("view-editor").classList.add("vertical-mode");
  timelineTabs.pick("p2");
  setPlayer2Name(true);
  openProjectVertical();
  renderTools();
}

// "Player 2" reads "Vertical" while the vertical editor is open (above the player and on the timeline's tab)
function setPlayer2Name(vertical) {
  const name = vertical ? "Vertical" : "Player 2";
  document.querySelector("#player2 .player-name").textContent = name;
  document.querySelector('.tl-tab[data-tl="p2"]').textContent = name;
}

function hideVerticalView() {
  if (!projectVerticalOpen()) return;
  closeProjectVertical();
  $("view-editor").classList.remove("vertical-mode");
  setPlayer2Name(false);
  renderTools();
}

// What's loaded decides the view: a vertical edit opens in the vertical editor, anything else in Player 2
function syncVerticalView() {
  if (store.open && store.isVertical) showVerticalView();
  else {
    verticalReturn = null;
    hideVerticalView();
  }
}

// The phone button (E): from Player 2, a new vertical edit of what's loaded; from the vertical editor, back to Player 2
async function toggleVertical(on = !projectVerticalOpen()) {
  endRowFocus();
  if (!on) return leaveVertical();
  if (!store.open) {
    toast("Load a clip, video or segment in Player 2 first.");
    return;
  }
  if (store.isVertical) {
    showVerticalView();
    return;
  }
  if (!can("AddEntries") || me?.support) {
    toast("Your role doesn't let you make vertical edits.", "error");
    return;
  }
  await newVerticalEdit();
}

// A new vertical edit starting from what Player 2 has now: same pieces and cuts, no boxes. Nothing is saved until its first change.
async function newVerticalEdit() {
  await store.saveNow();
  const from = store.project;
  const content = withoutLayouts(store.content);
  if (!content.pieces.length) {
    toast("Put something on Player 2's timeline first.");
    return;
  }
  const opened = ++p2Opened;
  const back = store.isDraft ? { item: from.sourceItem } : { edit: from.id };
  // Named after the clip or video it belongs to: "clip · Vertical n"
  let name = store.isDraft ? from.title : (from.title || "").split(" · ")[0] || "Untitled";
  let count = 0;
  try {
    if (!store.isDraft && from.entryId) name = (await api("GET", `/api/entries/${from.entryId}`)).title || name;
    else if (!store.isDraft && from.videoId) name = (await api("GET", `/api/videos/${from.videoId}`)).title || name;
    const query = from.entryId ? `entryId=${from.entryId}` : from.videoId ? `videoId=${from.videoId}` : `vodId=${store.vodId}`;
    const edits = await api("GET", `/api/projects?${query}&vertical=true&limit=1000`);
    count = from.entryId || from.videoId ? edits.length : edits.filter((e) => !e.entryId && !e.videoId).length;
  } catch {
    // The name is only a starting point
  }
  if (opened !== p2Opened) return;
  await store.openDraft({
    title: name,
    editTitle: `${name} · Vertical ${count + 1}`,
    vodId: store.vodId,
    entryId: from.entryId ?? null,
    videoId: from.videoId ?? null,
    vertical: true,
    source: store.isDraft ? from.source : { kind: "edit", id: from.id },
    sourceItem: from.sourceItem ?? null,
    canEdit: true,
    content,
  });
  if (opened !== p2Opened) return;
  projectOpened();
  verticalReturn = back;
  toast("New vertical edit: it's saved with your first change. The phone button goes back to Player 2.");
}

// Leaving the vertical editor: back to what Player 2 had before the phone button, else the clip or video this is an edit of
async function leaveVertical() {
  if (!projectVerticalOpen()) return;
  const back = verticalReturn;
  verticalReturn = null;
  if (!store.open || !store.isVertical) {
    hideVerticalView();
    return;
  }
  await store.saveNow();
  const project = store.project;
  let ok = false;
  if (back?.edit) ok = await openProject(back.edit, { quiet: true });
  else if (back?.item) ok = await loadInPlayer2(back.item, { quiet: true, original: true });
  else {
    try {
      if (project.entryId) ok = await loadInPlayer2({ kind: "clip", data: await api("GET", `/api/entries/${project.entryId}`) }, { quiet: true });
      else if (project.videoId) ok = await loadInPlayer2({ kind: "video", data: await api("GET", `/api/videos/${project.videoId}`) }, { quiet: true });
    } catch {
      // The clip or video is gone or not open to the team any more
    }
  }
  if (!ok) await closeProject();
}

// ---------- tools bar (works on the timeline whose tab is open) ----------

function renderTools() {
  if (DEMO || !timeline || !timeline2) return;
  const t = activeTimeline();
  const on = Boolean(t.vodId);
  const p2 = activePlayer === "p2";
  const canCreate = !p2 && on && t.canCreate();
  // Player 2: moments and clips on the VOD under the playhead; S splits there and linking is Player 1's
  const p2Create = p2 && p2CanCreate();
  for (const button of document.querySelectorAll("#tools [data-tool]")) {
    // the clip tool makes clips: Player 1 on its VOD, Player 2 on the VOD of the piece dragged across
    const enabled = on && (button.dataset.tool !== "clip" || (p2 ? p2Create : canCreate));
    button.disabled = !enabled;
    button.toggleAttribute("data-active", enabled && t.tool === button.dataset.tool);
  }
  const snap = document.querySelector('#tools [data-keybind="toggleSnap"]');
  snap.disabled = !on;
  snap.toggleAttribute("data-active", on && t.snap);
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    const kind = button.dataset.create;
    if (p2) {
      button.disabled = !(p2Create && kind === "marker");
      button.removeAttribute("data-active");
      continue;
    }
    button.disabled = !canCreate;
    button.toggleAttribute("data-active", canCreate && (kind === "video" ? Boolean(t.linking) : t.marking?.kind === kind));
  }
  const undo = screenUndoState();
  document.querySelector('#tools [data-keybind="undo"]').disabled = !undo.canUndo;
  document.querySelector('#tools [data-keybind="redo"]').disabled = !undo.canRedo;
  // While a row plays alone: only the pointer, zoom and hand tools and the moment marker
  if (rowFocus) {
    for (const button of document.querySelectorAll("#tools sl-icon-button")) {
      const kept = ["pointer", "zoom", "hand"].includes(button.dataset.tool) || button.dataset.create === "marker";
      if (!kept) button.disabled = true;
    }
  }
}

// Undo / redo: Player 1's steps, or Player 2's project (the 9:16 view's box changes are project changes too)
function screenUndo(redo) {
  if (activePlayer === "p2" || projectVerticalOpen()) {
    redo ? store.redo() : store.undo();
  } else if (timeline.vodId) {
    redo ? timeline.redo() : timeline.undo();
  }
  renderTools();
}

function screenUndoState() {
  if (activePlayer === "p2" || projectVerticalOpen()) return { canUndo: store.canUndo(), canRedo: store.canRedo() };
  return { canUndo: timeline.canUndo(), canRedo: timeline.canRedo() };
}

function setUpTools() {
  if (DEMO) return;
  for (const button of document.querySelectorAll("#tools [data-tool]")) {
    button.addEventListener("click", () => activeTimeline().setTool(button.dataset.tool));
  }
  document.querySelector('#tools [data-keybind="toggleSnap"]').addEventListener("click", () => activeTimeline().toggleSnap());
  document.querySelector('#tools [data-keybind="undo"]').addEventListener("click", () => screenUndo(false));
  document.querySelector('#tools [data-keybind="redo"]').addEventListener("click", () => screenUndo(true));
  const create = {
    marker: () => (activePlayer === "p2" ? p2AddMoment() : timeline.addMoment()),
    segment: () => timeline.mark("segment"),
    video: () => timeline.startLinking(),
  };
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    button.addEventListener("click", () => create[button.dataset.create]?.());
  }
  renderTools();
}

// Play / pause. Player 1: with a segment or clip selected and the player paused, play just it. Player 2: the project.
function playPause(which = activePlayer) {
  if (which === "p2") {
    if (store.open) playback.toggle();
    return;
  }
  const { player, timeline: t } = pair(which);
  if (!player.ready) return;
  if (rowFocus?.which === "p1") {
    if (player1.playing()) player1.pause();
    else playFocusedP1();
    return;
  }
  const range = t.selectedRange();
  if (range && !player.playing()) {
    const now = player.position();
    const from = now != null && now >= range.start && now < range.end - 0.05 ? now : range.start;
    player.playRange(from, range.end);
    return;
  }
  player.togglePlay();
}

// Shift+Space: the last 3 s of the selected item (Player 2: of the selected piece, then on)
function playLastSeconds(which = activePlayer) {
  if (which === "p2") {
    const piece = timeline2.selectedPieces()[0];
    if (!piece) return;
    const end = piece.at + piece.end - piece.start;
    playback.seek(Math.max(piece.at, end - 3));
    playback.play();
    return;
  }
  const { player, timeline: t } = pair(which);
  if (!player.ready) return;
  const range = t.selectedRange();
  if (!range) return;
  player.playRange(Math.max(range.start, range.end - 3), range.end);
}

function skip(which, delta) {
  if (which === "p2") {
    if (!store.open) return;
    playback.seek(playback.time() + delta);
    return;
  }
  const { player, timeline: t } = pair(which);
  const now = t.getPosition();
  if (!player.ready || now == null) return;
  const extent = t.extent();
  t.onSeek(Math.min(Math.max(now + delta, extent.start), extent.end));
}

function setActivePlayer(which) {
  if (rowFocus && rowFocus.which !== which) endRowFocus();
  activePlayer = which;
  timeline.setActive(which === "p1");
  timeline2.setActive(which === "p2");
  renderTools();
}

function setUpPlayers() {
  setUpPlayer1();
  setUpPlayer2();
  setUpTools();
  setUpRowFocus();
  for (const player of ["p1", "p2"]) {
    dropTarget($(`${player}-screen`), {
      accepts: (item) => PLAYER_KINDS[player].includes(item.kind),
      onDrop: (item) => load(item),
    });
  }
  peekPlayer2WhileDragging();
  // Clicking a player that has something loaded opens its timeline tab, so the keys work on it
  $("player1").addEventListener("pointerdown", () => {
    if (activePlayer !== "p1" && p1Vod) timelineTabs.pick("p1");
  });
  $("player2").addEventListener("pointerdown", () => {
    if (activePlayer !== "p2" && store.open) timelineTabs.pick("p2");
  });
  // A stored clip's trim waits 3 s before it's saved, and the project saves shortly after a change; don't lose either
  window.addEventListener("pagehide", () => {
    timeline.flushTrimOnExit();
    store.flushOnExit();
  });
}

// Dragging a clip, segment or video over Player 2's tab or screen shows Player 2's timeline, so it can be dropped there; moving out of
// the timeline and Player 2's screen again (or dropping anywhere else) goes back to Player 1's. When Player 2's timeline was already
// showing, nothing changes (badrfly 2026-11-18)
function peekPlayer2WhileDragging() {
  let peeking = false;
  let last = null;
  const inside = (element, { x, y }) => {
    const r = element?.getBoundingClientRect();
    return Boolean(r) && r.width > 0 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
  };
  const tab = () => document.querySelector('.tl-tab[data-tl="p2"]');
  watchDrags({
    start: () => { peeking = false; },
    move: (item, at) => {
      last = at;
      if (!PLAYER_KINDS.p2.includes(item.kind) || item.kind === "project") return;
      if (!peeking) {
        if (activePlayer === "p2") return;
        if (inside(tab(), at) || inside($("p2-screen"), at)) {
          peeking = true;
          timelineTabs.pick("p2");
        }
      } else if (!inside($("timeline"), at) && !inside($("p2-screen"), at)) {
        peeking = false;
        timelineTabs.pick("p1");
      }
    },
    end: (_item, _droppedOn, cancelled) => {
      if (!peeking) return;
      peeking = false;
      // Let go over the timeline or Player 2's screen (dropped or not): Player 2 stays; anywhere else, or Esc, back to Player 1
      const over = last && (inside($("timeline"), last) || inside($("p2-screen"), last));
      if (cancelled || !over) timelineTabs.pick("p1");
    },
  });
}

// ---------- keyboard (every key is listed in keybinds.js) ----------

// Typing in a field, or a control that uses these keys itself (tabs, sliders, menus, checkboxes, buttons)
const CONTROL_KEYS = new Set(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "Enter", " "]);
function isTyping(event) {
  if (event.defaultPrevented) return true;
  return event.composedPath().some((node) => {
    if (!(node instanceof HTMLElement)) return false;
    if (node.isContentEditable || /^(INPUT|TEXTAREA|SELECT|SL-INPUT|SL-TEXTAREA|SL-SELECT)$/.test(node.tagName)) return true;
    const control = /^(SL-RANGE|SL-CHECKBOX|SL-SWITCH|SL-RADIO-GROUP|SL-MENU)$/.test(node.tagName)
      || /^(tab|menuitem|radio)$/.test(node.getAttribute("role") ?? "");
    return control && CONTROL_KEYS.has(event.key) && !(event.key === " " && node.getAttribute("role") === "tab");
  });
}

function setUpKeys() {
  document.addEventListener("keydown", (event) => {
    if (isTyping(event) || document.querySelector("sl-dialog[open]")) return;
    // ? works on both screens
    if (keybindFor("player", event) === "shortcutsHelp") {
      event.preventDefault();
      showShortcuts();
      return;
    }
    // Ctrl+B: hide / show the library (both screens)
    if (keybindFor("player", event) === "toggleLibrary") {
      if (rowFocus) return;
      event.preventDefault();
      setLibraryHidden(!libraryHidden());
      return;
    }
    // Ctrl+K or / : the library's search box (both screens have the left panel)
    if (keybindFor("player", event) === "librarySearch") {
      if (rowFocus) return;
      event.preventDefault();
      focusLibrarySearch();
      return;
    }
    if ($("view-editor").hidden) return;
    // The 9:16 view has its own keys (boxes, keyframes); the rest of the player's still work there
    if (handleProjectVerticalKey(event)) return;
    const id = keybindFor("player", event);
    if (!id) return;
    // While a row plays alone only playback, zoom and Esc work (the rows can't be changed then)
    if (rowFocus && !FOCUS_KEYS.has(id)) {
      event.preventDefault();
      return;
    }
    const p2 = activePlayer === "p2";
    const { player, timeline: t } = pair();
    const ready = p2 ? store.open : player.ready;
    const on = Boolean(t.vodId);
    const frame = (delta) => {
      if (!ready) return;
      if (p2) playback.pause();
      else player.pause();
      skip(activePlayer, delta);
    };
    const run = {
      playPause: () => playPause(),
      playEnd: () => playLastSeconds(),
      back10: () => skip(activePlayer, -10),
      fwd10: () => skip(activePlayer, 10),
      back5: () => skip(activePlayer, -5),
      fwd5: () => skip(activePlayer, 5),
      frameBack: () => frame(-FRAME_SECONDS),
      frameFwd: () => frame(FRAME_SECONDS),
      fullscreen: () => (p2 ? store.open && player2.fullscreen() : ready && player.fullscreen()),
      screenshot: () => ready && screenshot(activePlayer),
      zoomIn: () => on && t.zoomBy(0.5),
      zoomOut: () => on && t.zoomBy(2),
      fit: () => on && t.fit(),
      toolPointer: () => on && t.setTool("pointer"),
      toolCut: () => on && t.setTool("cut"),
      toolZoom: () => on && t.setTool("zoom"),
      toolHand: () => on && t.setTool("hand"),
      toggleSnap: () => on && t.toggleSnap(),
      marker: () => (p2 ? p2AddMoment() : on && t.addMoment()),
      segment: () => (p2 ? on && t.splitAtPlayhead() : on && t.mark("segment")),
      clip: () => on && t.setTool(t.tool === "clip" ? "pointer" : "clip"),
      setStart: () => on && t.setEdgeAtPlayhead("start"),
      setEnd: () => on && t.setEdgeAtPlayhead("end"),
      undo: () => screenUndo(false),
      redo: () => screenUndo(true),
      deleteSelected: () => on && (p2 ? t.deleteSelected(false) : t.deleteSelected()),
      rippleDelete: () => p2 && on && t.deleteSelected(true),
      copyPieces: () => p2 && on && t.copySelected(),
      selectAll: () => on && t.selectAll?.(),
      pastePieces: () => p2 && on && t.paste(),
      addRow: () => p2 && on && t.addTrack(),
      slideBack: () => on && (p2 ? t.slideSelected(-SLIDE_STEP) : t.slideSynced(-SLIDE_STEP)),
      slideFwd: () => on && (p2 ? t.slideSelected(SLIDE_STEP) : t.slideSynced(SLIDE_STEP)),
      toggleWaves: () => on && t.toggleWaves?.(),
      escape: () => {
        if (rowFocus) {
          endRowFocus();
          return;
        }
        if (on) t.escape();
      },
      verticalToggle: () => toggleVertical(),
    }[id];
    if (!run) return;
    // Space on a focused button would also click it
    event.preventDefault();
    run();
  });
}

// ---------- row focus (badrfly 2026-11-18): a row's play button plays only that row ----------
//
// The row head's play button (both timelines) plays that row alone: Player 2 only its pieces, Player 1 only its segments' and
// clips' kept parts (moments skipped), gaps jumped over. The rest of the editor goes dark with the same shade as dragging, with
// the player and the row left lit; the left panel and the other player fold away, the other rows fold to thin lines and the
// timeline shrinks to its rows, so the player gets the room. Stop (the same button), Esc, a click in the dark, another tab or
// screen, or the row going away ends it; the playhead stays where it stopped.

let rowFocus = null;        // { which: "p1" | "p2", rowId }
const FOCUS_KEYS = new Set(["playPause", "back10", "fwd10", "back5", "fwd5", "frameBack", "frameFwd", "fullscreen", "screenshot", "zoomIn", "zoomOut", "fit", "escape", "toggleWaves",
  "toolPointer", "toolZoom", "toolHand", "marker"]);
let focusShade = null;
let focusFrame = 0;

function focusTimeline() {
  return rowFocus?.which === "p1" ? timeline : timeline2;
}

// rowIds: one row, or (Player 1) the checked rows, which play together, each from its own VOD
function focusRow(which, rowIds) {
  const ids = Array.isArray(rowIds) ? rowIds : [rowIds];
  const rowId = ids[0];
  if (rowFocus?.which === which && rowFocus.rowIds.length === ids.length && ids.every((id) => rowFocus.rowIds.includes(id))) {
    endRowFocus();
    return;
  }
  if (which === "p2" && (!store.open || projectVerticalOpen())) return;
  if (which === "p1" && !player1.ready) return;
  endRowFocus();
  closeItemEditor(false);
  if (activePlayer !== which) timelineTabs.pick(which);
  rowFocus = { which, rowId, rowIds: ids };
  const t = focusTimeline();
  if (!["pointer", "zoom", "hand"].includes(t.tool)) t.setTool("pointer");
  t.setFocus(which === "p1" ? ids : rowId);
  document.body.classList.add("row-focus", `row-focus-${which}`);
  const p1Row = which === "p1" ? timeline.rows.find((r) => r.id === rowId) : null;
  const name = ids.length > 1
    ? `${ids.length} rows`
    : p1Row
    ? `${timeline.rowTitle(p1Row)} (${p1Row.userId === me?.id ? "you" : p1Row.username})`
    : trackName(store.content, store.content.tracks.find((tr) => tr.id === rowId));
  const badge = $(`${which}-focus`);
  badge.textContent = `Playing ${name} only · Esc`;
  badge.hidden = false;
  focusShade = el("div", { class: "focus-shade", title: "Click to leave (Esc)" });
  focusShade.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    endRowFocus();
  });
  document.body.append(focusShade);
  focusFrame = requestAnimationFrame(focusLoop);
  if (which === "p2") {
    playback.setSolo(rowId);
    if (!flattenSolo()) {
      toast("Nothing to play on this row.");
      endRowFocus();
      return;
    }
    playback.play();
  } else {
    if (!timeline.focusParts().length) {
      toast(ids.length > 1 ? "Nothing to play on these rows (moments aren't played)." : "Nothing to play on this row (moments aren't played).");
      endRowFocus();
      return;
    }
    playFocusedP1();
  }
  renderTools();
}

function flattenSolo() {
  return playback.flat().some((s) => s.piece);
}

function endRowFocus() {
  if (!rowFocus) return;
  const { which } = rowFocus;
  rowFocus = null;
  cancelAnimationFrame(focusFrame);
  focusShade?.remove();
  focusShade = null;
  document.body.classList.remove("row-focus", "row-focus-p1", "row-focus-p2");
  document.body.style.removeProperty("--focus-lower-h");
  $(`${which}-focus`).hidden = true;
  if (which === "p2") {
    if (store.open) playback.pause();
    playback.setSolo(null);
    timeline2.setFocus(null);
  } else {
    player1.stopAt = null;
    if (player1.ready) player1.pause();
    timeline.setFocus(null);
    comeBackToP1Vod();
  }
  renderTools();
}

// ---------- Player 1 playing checked rows of several VODs (badrfly 2026-11-20) ----------
//
// While checked rows play together, a synced row's parts play from its own VOD in Player 1 (opened without changing the timeline,
// which stays on this VOD's clock: position - shift). p1Away = { vodId, shift } while Player 1 shows another VOD.

let p1Away = null;

function p1Position() {
  if (!player1?.ready) return null;
  const position = player1.position();
  if (position == null) return null;
  return p1Away ? position - p1Away.shift : position;
}

function p1PlayingVod() {
  return p1Away ? String(p1Away.vodId) : String(p1Vod?.vodId ?? "");
}

// Plays a part from where the clock is (its own VOD, switching VODs when needed)
async function playPartP1(part, clock) {
  rowFocus.part = part;
  rowFocus.seekAt = performance.now();
  const at = Math.max(0, clock + part.shift);
  if (String(part.vodId) === p1PlayingVod()) {
    player1.seek(at);
    if (!player1.playing()) player1.play();
    return;
  }
  const focus = rowFocus;
  focus.switching = true;
  try {
    await player1.open(part.vodId, at);
    if (rowFocus !== focus) return;
    p1Away = String(part.vodId) === String(p1Vod?.vodId) ? null : { vodId: String(part.vodId), shift: part.shift };
    player1.play();
  } catch (error) {
    toast(`Couldn't play that stream: ${error.message}`, "error");
    endRowFocus();
  } finally {
    focus.switching = false;
    rowFocus && (rowFocus.seekAt = performance.now());
  }
}

// Back on this VOD (at the same moment) once the rows stop playing
function comeBackToP1Vod() {
  if (!p1Away || !p1Vod) {
    p1Away = null;
    return;
  }
  const clock = p1Position() ?? 0;
  p1Away = null;
  player1.open(p1Vod.vodId, Math.max(0, clock)).then(() => player1.pause()).catch(() => {});
}

// Player 1: from the playhead when it's in one of the rows' parts, else the next part (or the first one)
function playFocusedP1() {
  const parts = timeline.focusParts();
  if (!parts.length || !player1.ready) return;
  const now = p1Position() ?? 0;
  const inside = parts.find((p) => now >= p.start && now < p.end - 0.05);
  const part = inside ?? parts.find((p) => p.start >= now) ?? parts[0];
  playPartP1(part, inside ? now : part.start);
}

// Player 1 while a row plays alone: past a part's end (or in a gap), on to the next part
// (the part it jumped to counts as playing even when the player reports a little before its start, or a stale position for a
// moment after the seek, so it never seeks back and forth)
function focusTickP1(position) {
  if (rowFocus.switching || position == null || !player1.playing()) return;
  const parts = timeline.focusParts();
  const part = rowFocus.part && parts.find((p) => Math.abs(p.start - rowFocus.part.start) < 0.01 && p.vodId === rowFocus.part.vodId);
  if (part && part.vodId === p1PlayingVod() && position < part.end - 0.04
    && (position >= part.start - 1.5 || performance.now() - rowFocus.seekAt < 1500)) return;
  const inside = parts.find((p) => position >= p.start - 0.05 && position < p.end - 0.04);
  if (inside) {
    if (inside.vodId === p1PlayingVod()) rowFocus.part = inside;
    else playPartP1(inside, position);
    return;
  }
  const next = parts.find((p) => p.start > position - 0.05);
  if (!next) {
    player1.pause();
    return;
  }
  playPartP1(next, next.start);
}

// Every frame while focused: the shade's holes follow the player and the row, the timeline is as tall as its rows, and the
// focus ends if the row went away (deleted, another VOD or edit loaded)
function focusLoop() {
  if (!rowFocus) return;
  const t = focusTimeline();
  const head = t.heads.querySelector(".row-head.focused");
  if (!head || t.heads.hidden) {
    endRowFocus();
    return;
  }
  // The timeline as tall as its rows: measured only when the rows or the room change (measuring every frame made it jump)
  const key = `${t.rowsHeight()}:${$("view-editor").clientHeight}`;
  if (rowFocus.layoutKey !== key) {
    rowFocus.layoutKey = key;
    const lower = $("lower");
    const body = document.querySelector(".tl-body");
    const chrome = lower.offsetHeight - body.clientHeight;
    const wanted = Math.round(Math.min(chrome + t.rowsHeight() + 2, $("view-editor").clientHeight * 0.6));
    document.body.style.setProperty("--focus-lower-h", `${wanted}px`);
    t.dirty = true;
  }
  // Lit: the player, the tools bar and the whole timeline (its playhead can be moved as usual)
  const holes = [$(rowFocus.which === "p1" ? "player1" : "player2"), $("toolsbar"), $("timeline")].map((e) => e.getBoundingClientRect());
  const path = shadePath(holes);
  if (path !== rowFocus.path) {
    rowFocus.path = path;
    focusShade.style.clipPath = path;
  }
  focusFrame = requestAnimationFrame(focusLoop);
}

function setUpRowFocus() {
  // While focused the rows can't be edited: a click on the timeline only moves the playhead (to the next part when it's in a gap)
  const track = $("tl-track");
  // The hand and zoom tools and the middle button work as usual; with the pointer a click on an item or piece goes to its start,
  // anywhere else to that exact time (and the ruler, outside this, as usual)
  const passes = (event) => event.button === 1 || ["hand", "zoom"].includes(focusTimeline().tool);
  track.addEventListener("pointerdown", (event) => {
    if (!rowFocus || passes(event)) return;
    event.stopPropagation();
    event.preventDefault();
    if (event.button !== 0) return;
    const t = focusTimeline();
    const rect = t.track.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const seconds = t.xToTime(x, rect.width);
    const geom = t.rowGeom();
    const index = geom.findIndex((g) => y >= g.y && y < g.y + g.h);
    if (rowFocus.which === "p2") {
      const track2 = store.content?.tracks[index];
      const piece = track2 && store.content.pieces.find((p) => p.trackId === track2.id && seconds >= p.at && seconds < p.at + (p.end - p.start));
      p2Seek(piece ? piece.at : seconds);
      return;
    }
    const row = timeline.rows[index];
    const pad = (4 / rect.width) * timeline.span();
    const item = row && timeline.itemsOf(row).find((i) => (i.end == null ? Math.abs(i.start - seconds) <= pad * 1.5 : seconds >= i.start - pad && seconds <= i.end + pad));
    rowFocus.part = null;
    player1.seek(item ? item.start : seconds);
  }, true);
  for (const type of ["dblclick", "contextmenu", "pointermove"]) {
    track.addEventListener(type, (event) => {
      if (!rowFocus) return;
      const t = focusTimeline();
      if (type === "pointermove" && t.press) return;
      if (type === "contextmenu" && t.tool === "zoom") return;
      event.stopPropagation();
      if (type === "contextmenu") event.preventDefault();
    }, true);
  }
}

// ---------- title and color editor in the tools bar ----------
//
// openItemEditor({ title, color, colors, needsApply, onColor, onApply(title, color), onCancel }): the tools slide left and the editor
// shows on the right, with the ✓ button beside the title (badrfly 2026-11-20). Segments, markers and clips also apply on Enter or a
// click anywhere else; linking a video needs the ✓ button (needsApply). Esc cancels.

let itemEditor = null;

export function openItemEditor(options) {
  closeItemEditor(false);
  itemEditor = { ...options, color: options.color ?? "yellow" };
  $("item-title").value = options.title ?? "";
  $("item-title").placeholder = options.placeholder ?? "Title";
  $("item-apply").hidden = false;
  $("item-colors").hidden = options.colors === false;
  renderSwatches();
  $("toolsbar").classList.add("editing");
  $("item-editor").hidden = false;
  setTimeout(() => $("item-title").focus(), 0);
}

export function closeItemEditor(apply) {
  if (!itemEditor) return;
  const editor = itemEditor;
  itemEditor = null;
  $("item-editor").hidden = true;
  $("toolsbar").classList.remove("editing");
  if (apply) editor.onApply?.($("item-title").value.trim(), editor.color);
  else editor.onCancel?.();
}

function renderSwatches() {
  $("item-colors").replaceChildren(...Object.entries(MARKER_COLORS).map(([name, hex]) => {
    const swatch = el("button", {
      type: "button",
      class: "swatch",
      role: "radio",
      "aria-checked": String(itemEditor.color === name),
      "aria-label": name,
      style: `background:${hex}`,
    });
    // Enter on a swatch applies, like in the title box (a click on it only picks the color)
    swatch.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" || itemEditor?.needsApply) return;
      event.preventDefault();
      closeItemEditor(true);
    });
    swatch.addEventListener("click", () => {
      itemEditor.color = name;
      itemEditor.onColor?.(name);
      renderSwatches();
    });
    return el("sl-tooltip", { content: name, hoist: true }, swatch);
  }));
}

function setUpItemEditor() {
  $("item-apply").addEventListener("click", () => closeItemEditor(true));
  $("item-title").addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !itemEditor?.needsApply) closeItemEditor(true);
    if (event.key === "Escape") closeItemEditor(false);
  });
  // A click anywhere outside the editor applies it (not for linking, which waits for the apply button)
  document.addEventListener("pointerdown", (event) => {
    if (!itemEditor || itemEditor.needsApply) return;
    if (event.composedPath().includes($("item-editor"))) return;
    closeItemEditor(true);
  }, true);
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && itemEditor) closeItemEditor(false);
  });
}

// ---------- demo (?demo): sample cards to try dragging, menus and the title editor, nothing sent ----------

function demoSample() {
  const now = Date.now();
  const iso = (hoursAgo) => new Date(now - hoursAgo * 3600_000).toISOString();
  return ({
    channels: [{ login: "arky", displayName: "Arky" }, { login: "ellsya", displayName: "Ellsya" }],
    vods: [
      { vodId: 3, title: "yellow! !comm !socials !discord !oscs", createdAtUtc: iso(3), length: "3:02:11", lengthSeconds: 10931, game: "Minecraft", isLive: true, unmuteWindowOpen: true },
      { vodId: 2, title: "🔶 qsmp @ARKY X @ELLSYA 🔶 !qsmp !comm !socials", createdAtUtc: iso(30), length: "6:12:40", lengthSeconds: 22360, game: "Minecraft", rescueStatus: "Pruned" },
      { vodId: 1, title: "🔶QSMP？🔶 NMS BOXING INSTIGATOR 🔶 !oscs !comm !socials", createdAtUtc: iso(80), length: "4:03:11", lengthSeconds: 14591, game: "No Man's Sky" },
    ],
    clips: [
      { id: 11, kind: "Vod", title: "chose to like it", clipLengthSeconds: 48, streamDateUtc: iso(30), addedBy: "badrfly", addedByUserId: "1", status: "Ready" },
      { id: 12, kind: "Vod", title: "the instigator", clipLengthSeconds: 72, streamDateUtc: iso(80), addedBy: "editor2", addedByUserId: "2", status: "Downloading" },
      { id: 13, kind: "Clip", title: "old twitch clip", clipLengthSeconds: 30, streamDateUtc: iso(200), addedBy: "badrfly", addedByUserId: "1", status: "ReferenceOnly", isReferenceOnly: true },
    ],
    videos: [{ id: 21, title: "Boxing highlights", lengthSeconds: 221, streamDateUtc: iso(80), createdBy: "badrfly" }],
  });
}

function wireDemoTools() {
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    button.disabled = false;
    button.addEventListener("click", () => openItemEditor({
      title: "",
      needsApply: button.dataset.create === "video",
      onApply: (title, color) => toast(`Would save ${button.dataset.create} "${title || "untitled"}" in ${color} (demo).`),
    }));
  }
}

// ---------- start ----------

// The app opens on the start screen over the empty editor (badrfly 2026-11-19: nothing loaded behind it): log in, a link's form, or
// the member's teams. Picking a team runs enterTeam. Staff in a support view go straight in; so does a reload right after switching teams.
async function start() {
  $("workspace").hidden = false;
  wireIconButtons();
  setUpLayout();
  setUpStart({ onEnter: enterTeam, onAccount: accountAction });
  // The session ended while working (logged out elsewhere, password changed, account disabled): start over on the login form
  sessionHandlers.loggedOut = () => { if (me && !startIsOpen()) location.href = "/"; };
  try {
    await loadMe();
  } catch {
    // Logged out: the start screen shows the login form
  }
  let enterNow = null;
  try {
    enterNow = sessionStorage.getItem("clipco.start.enter");
    sessionStorage.removeItem("clipco.start.enter");
  } catch {
    // Storage blocked: show the start screen
  }
  if (me && (me.support || DEMO || (enterNow && Number(enterNow) === me.currentTeamId && !location.hash.startsWith("#join=")))) {
    await enterTeam();
    return;
  }
  openStart();
}

async function enterTeam() {
  renderTopbar();
  if (!me.currentTeamId && !me.support) {
    showGate("You aren't in a team yet. Ask a team owner for an invite link.", "No team yet");
    return;
  }
  const teamName = me.support?.teamName ?? me.teams.find((team) => team.id === me.currentTeamId)?.name;
  if (me.permissions.length === 0) {
    showGate(`You're in ${teamName}, but you don't have a role there yet. The team owner gives you one; reload this page after that.`);
    return;
  }
  if (!can("ViewLibrary")) {
    showGate(`Your role in ${teamName} doesn't include the library. Open the Team page from the account menu.`, "Team tools only");
    return;
  }

  $("workspace").hidden = false;
  setUpExport({ demo: DEMO, otherPlayers: () => [player1, player2], onPausePlayers: () => playback?.pause() });
  screenTabs = tabs(document.querySelector(".main-tabs"), "data-view", showScreen, "clipco.editor.screen");
  setUpLibrary({ onLoad: load, onExport: openExport, bulkMenuItems: libraryBulkItems,
    // Edits and segments under clips and videos (the old Player 2 picker)
    loaded: p2Loaded,
    onOpenEdit: (id) => openProject(id),
    onLoadSegment: (segment, video) => loadInPlayer2({ kind: "segment", data: segmentData(segment, video) }, { original: true }),
    vodDay: (vodId) => vodDay(vodId, () => refreshEdits()), onPickList: (list) => listTabs.pick(list), onAddSegments: (video) => addSegmentsTo(video), sample: DEMO ? demoSample() : null });
  if (DEMO) wireDemoTools();
  listTabs = tabs(document.querySelector(".side-tabs"), "data-list", showList, "clipco.editor.list");
  setUpPlayers();
  timelineTabs = tabs(document.querySelector(".tl-tabs"), "data-tl", setActivePlayer, null);
  setUpKeys();
  setUpItemEditor();
  if (!DEMO) setUpLiveUpdates();
}

// A stream ended (core.js startLiveEvents: the server hears it from Twitch): a VOD open in Player 1 that was live gets its final length
// (the player itself sees the playlist end); Player 2 reads live VODs' lengths again
function setUpLiveUpdates() {
  window.addEventListener("clipco:stream", (event) => {
    if (event.detail.live) return;
    forgetLiveVodLengths();
    const vod = p1Vod;
    if (!vod?.isLive) return;
    // Twitch settles the VOD's length a little after the stream ends
    setTimeout(async () => {
      try {
        const info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(vod.vodId)}`);
        vod.isLive = info.isLive;
        vod.lengthSeconds = info.lengthSeconds;
        if (p1Vod === vod && timeline.vodId === String(vod.vodId)) timeline.knownLength = Math.max(timeline.knownLength, info.lengthSeconds || 0);
      } catch {
        // The player's own playlist still has the length
      }
    }, 15_000);
  });
  // The app Owner changed the team's channels (a revoke): whatever is loaded from a channel the team lost closes now, with a note why,
  // instead of waiting for a refresh (badrfly 2026-11-20). The library re-reads its own lists.
  window.addEventListener("clipco:channels", () => dropRefusedVods());
  startLiveEvents();
}

async function dropRefusedVods() {
  for (const which of ["p1", "p2"]) {
    const empty = which === "p1" ? !p1Vod : !store?.open;
    if (refusalShown[which] && empty) showNote(which, "");
    refusalShown[which] = false;
  }
  const p1 = p1Vod;
  if (p1) {
    try {
      await checkVodAccess(p1.vodId);
    } catch (error) {
      if (p1Vod === p1) unloadPlayer1(channelRefusalText(error));
    }
  }
  if (store?.open) {
    const project = store.project;
    const vodIds = new Set([store.vodId, ...(store.content?.pieces ?? []).map((p) => p.vodId)].filter(Boolean).map(String));
    for (const vodId of vodIds) {
      try {
        await checkVodAccess(vodId);
      } catch (error) {
        if (store.open && store.project === project) await refusePlayer2(error);
        break;
      }
    }
  }
}

start();
