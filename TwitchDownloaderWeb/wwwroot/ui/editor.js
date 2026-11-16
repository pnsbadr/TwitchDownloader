// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { $, el, api, me, can, loadMe, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, loadSetting, saveSetting, toast, formatDuration } from "./core.js";
import { dropTarget } from "./drag.js";
import { setUpLibrary, showList, refreshClips, refreshVideos, refreshProjectCards } from "./library.js";
import { MARKER_COLORS } from "./colors.js";
import { MediaPlayer, formatClock } from "./media.js";
import { Timeline } from "./timeline.js";
import { trimValue } from "./timeline-edit.js";
import { skipCut, keptSeconds } from "./cuts.js";
import { askText, confirmAction } from "./dialogs.js";
import { ProjectStore, piecesFromItem, flatten, stretchAt } from "./project.js";
import { ProjectTimeline } from "./project-timeline.js";
import { ProjectPlayback } from "./project-playback.js";
import { setUpExport, addToExport, exportShown, pausePreview } from "./export.js";
import { showShortcuts } from "./shortcuts.js";
import { setUpProjectVertical, openProjectVertical, closeProjectVertical, projectVerticalOpen, refreshProjectVertical, handleProjectVerticalKey, pauseProjectVertical } from "./project-vertical.js";

// Phosphor icons, bold weight, as <sl-icon library="ph" name="…">
registerIconLibrary("ph", {
  resolver: (name) => `https://cdn.jsdelivr.net/npm/@phosphor-icons/core@2.1.1/assets/bold/${name}-bold.svg`,
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
  $("menu-team").hidden = !TEAM_PAGE_PERMISSIONS.some(can);

  if (me.support) {
    $("support-team").textContent = me.support.teamName;
    $("support-banner").hidden = false;
    $("menu-logout").hidden = true;
    $("account-items").querySelector('[value="account"]').hidden = true;
    $("team-pill-slot").replaceChildren(el("span", { class: "team-pill" }, me.support.teamName));
    return;
  }

  const current = me.teams.find((team) => team.id === me.currentTeamId);
  if (me.teams.length < 2) {
    $("team-pill-slot").replaceChildren(el("span", { class: "team-pill" }, current?.name ?? "No team"));
    return;
  }
  // Two or more teams: the pill is the team switcher
  const menu = el("sl-menu", {}, ...me.teams.map((team) =>
    el("sl-menu-item", { type: "checkbox", value: String(team.id), checked: team.id === me.currentTeamId }, team.name)));
  menu.addEventListener("sl-select", async (event) => {
    const teamId = Number(event.detail.item.value);
    if (teamId === me.currentTeamId) return;
    await api("POST", "/api/auth/team", { teamId });
    location.reload();
  });
  const trigger = el("button", { slot: "trigger", type: "button", class: "team-pill", "aria-label": "Switch team" },
    current?.name ?? "Pick a team", el("sl-icon", { library: "ph", name: "caret-down" }));
  $("team-pill-slot").replaceChildren(el("sl-dropdown", { placement: "bottom-end", distance: "6", hoist: true }, trigger, menu));
}

$("account-items").addEventListener("sl-select", async (event) => {
  switch (event.detail.item.value) {
    case "account": location.href = "/account.html"; break;
    case "team": location.href = "/team.html"; break;
    case "shortcuts": showShortcuts(); break;
    case "logout":
      await fetch("/api/auth/logout", { method: "POST" });
      location.href = "/login.html";
      break;
  }
});
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

function showScreen(view) {
  $("view-editor").hidden = view !== "editor";
  $("view-export").hidden = view !== "export";
  // Only one player plays at a time: the screen left behind is paused
  if (view === "export") {
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
}

// ---------- left panel (ui/library.js) ----------

// A card's download icon or "Export": the item goes on the export list (a VOD opens the range pop-up)
function openExport(item) {
  screenTabs.pick("export");
  addToExport(item);
}

// Player 1's right-click "Add to the export list" on a segment or clip (stays on the Editor screen)
function exportFromTimeline(item) {
  const vod = p1Vod ?? {};
  if (item.kind === "segment") {
    addToExport({
      kind: "segment",
      data: { ...item.data, vodId: timeline.vodId, vodTitle: vod.title, vodDate: vod.createdAtUtc, thumbnailUrl: vod.thumbnailUrl },
    }, { quiet: false });
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
  }, { quiet: false });
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
  addToProject(item);
}

async function openVod(vod, { start = 0, paused = false } = {}) {
  if (DEMO) {
    toast("Playback isn't available in the demo.");
    return;
  }
  p1Vod = vod;
  $("p1-info").textContent = vod.title;
  $("p1-info").title = vod.title;
  $("p1-hint").hidden = true;
  $("p1-media").hidden = false;
  showNote("p1", "Loading…");
  const loaded = timeline.open(vod);
  try {
    await player1.open(vod.vodId, start);
    if (paused) player1.pause();
    if (p1Vod !== vod) return;
    showNote("p1", playerNotes(player1), 8000);
  } catch (error) {
    if (p1Vod !== vod) return;
    showNote("p1", `Couldn't play this VOD: ${error.message}`);
  }
  await loaded;
}

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

// ---------- Player 2: projects ----------

async function openProject(id, { show = true, quiet = false } = {}) {
  if (DEMO) {
    toast("Projects aren't available in the demo.");
    return false;
  }
  const opened = ++p2Opened;
  try {
    await store.openProject(id);
  } catch (error) {
    if (!quiet) toast(`Couldn't open the project: ${error.message}`, "error");
    if (error.status === 404) saveSetting(LAST_PROJECT_KEY, null);
    return false;
  }
  if (opened !== p2Opened) return false;
  projectOpened();
  if (show) timelineTabs.pick("p2");
  return true;
}

async function newProject({ title = null, show = true } = {}) {
  if (DEMO || me?.support || !can("AddEntries")) {
    toast(me?.support ? "Support view is read-only." : "Your role can't make projects.", "error");
    return false;
  }
  try {
    await store.create({ title });
  } catch (error) {
    toast(`Couldn't make a project: ${error.message}`, "error");
    return false;
  }
  p2Opened++;
  projectOpened();
  refreshProjects();
  if (show) timelineTabs.pick("p2");
  return true;
}

function projectOpened() {
  saveSetting(LAST_PROJECT_KEY, store.project.id);
  playback.stop();
  playback.seek(0, { quiet: true });
  timeline2.refresh({ reset: true });
  $("p2-hint").hidden = true;
  $("p2-media").hidden = false;
  if (projectVerticalOpen()) refreshProjectVertical();
  renderPlayer2();
  renderTools();
}

async function closeProject() {
  toggleVertical(false);
  playback.stop();
  player2.close();
  await store.close();
  saveSetting(LAST_PROJECT_KEY, null);
  timeline2.refresh({ reset: true });
  $("p2-media").hidden = true;
  $("p2-hint").hidden = false;
  showNote("p2", "");
  renderPlayer2();
  renderTools();
}

// A card, Player 1 item or new item goes into the open project (a new one when none is open, or it can't be changed)
async function addToProject(item, { show = true } = {}) {
  const pieces = piecesFromItem(item);
  if (!pieces?.length) {
    toast(item.kind === "clip" ? "Twitch didn't say which VOD this clip is from, so it can't go in a project." : "That can't go in a project.", "error");
    return;
  }
  if (!store.open || !store.canEdit) {
    if (!(await newProject({ show }))) return;
  }
  if (!timeline2.addItemAtPlayhead(pieces)) {
    toast("It doesn't fit there. Pick an empty row or make one (+ Row).", "error");
    return;
  }
  if (show) timelineTabs.pick("p2");
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
  for (const id of ["play", "back", "fwd", "full"]) $(`p${n}-${id}`).disabled = !ready;
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
  $("p2-show").disabled = !hasPieces;
  $("p2-rename").disabled = !open || !store.canEdit;
  $("p2-delete").disabled = !open;
  $("p2-export").disabled = !hasPieces || DEMO;
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

// ---------- Player 2's project picker ----------

let projectList = [];

async function loadProjectList() {
  try {
    projectList = await api("GET", "/api/projects?limit=100");
  } catch {
    projectList = [];
  }
  if ($("p2-project-menu").open) renderProjectMenu();
}

function renderProjectPicker() {
  const open = store.open;
  $("p2-project-name").textContent = open ? store.project.title : "No project";
  const state = open ? store.stateText() : "";
  $("p2-project-state").textContent = state;
  $("p2-project-state").classList.toggle("bad", state === "not saved");
  $("p2-project-pick").title = open
    ? `${store.project.title}${store.project.createdBy && !store.canEdit ? ` by ${store.project.createdBy} (view only: make your own copy to change it)` : ""}`
    : "Open or start a project";
  if ($("p2-project-menu").open) renderProjectMenu();
}

function renderProjectMenu() {
  const current = store.project;
  const item = (value, label, { icon = null, checked = null, disabled = false, detail = null } = {}) => el("sl-menu-item", {
    value, disabled, ...(checked != null ? { type: "checkbox", checked } : {}),
  }, icon ? el("sl-icon", { slot: "prefix", library: "ph", name: icon }) : null, label, detail ? el("span", { slot: "suffix", class: "edit-detail" }, detail) : null);
  const entries = [];
  const canMake = can("AddEntries") && !me?.support && !DEMO;
  entries.push(item("new", "New project", { icon: "plus", disabled: !canMake }));
  const own = projectList.filter((p) => p.userId === me?.id).slice(0, 10);
  const others = projectList.filter((p) => p.userId !== me?.id).slice(0, 10);
  const line = (p) => item(`open:${p.id}`, p.title, { checked: p.id === current?.id, detail: formatDuration(Math.round(p.lengthSeconds)) });
  if (own.length) entries.push(el("sl-divider"), el("sl-menu-label", {}, "Your projects"), ...own.map(line));
  if (others.length) entries.push(el("sl-divider"), el("sl-menu-label", {}, "The team's projects"), ...others.map((p) => item(`open:${p.id}`, p.title, {
    checked: p.id === current?.id, detail: `${p.createdBy ?? "teammate"} · ${formatDuration(Math.round(p.lengthSeconds))}`,
  })));
  entries.push(item("all", "All projects…", { icon: "list" }));
  if (current) {
    entries.push(el("sl-divider"));
    if (!DEMO) entries.push(item("export", "Add to the export list", { icon: "download-simple", disabled: !store.content.pieces.length }));
    if (store.canEdit) entries.push(item("rename", "Rename this project", { icon: "pencil-simple" }));
    entries.push(item("copy", store.canEdit ? "Duplicate" : "Make my own copy", { icon: "copy", disabled: !canMake }));
    if (current.canDelete) entries.push(item("delete", "Delete this project", { icon: "trash" }));
    entries.push(item("close", "Close (nothing is deleted)", { icon: "eject" }));
  }
  $("p2-project-items").replaceChildren(...entries);
}

async function renameProject() {
  if (!store.open || !store.canEdit) return;
  const title = await askText({ heading: "Rename the project", label: "Title", value: store.project.title });
  if (title == null || !title.trim()) return;
  try {
    await store.rename(title.trim());
    refreshProjects();
  } catch (error) {
    toast(error.message, "error");
  }
}

async function copyProject() {
  if (!store.open) return;
  await store.saveNow();
  try {
    const copy = await api("POST", `/api/projects/${store.project.id}/copy`, {});
    await openProject(copy.id);
    refreshProjects();
    toast(`Made "${copy.title}".`);
  } catch (error) {
    toast(error.message, "error");
  }
}

async function deleteProject() {
  if (!store.open) return;
  const project = store.project;
  const ok = await confirmAction({
    heading: "Delete this project?",
    text: `"${project.title}" is deleted for the whole team. The clips, segments and videos in it stay as they are.`,
    confirm: "Delete",
  });
  if (!ok) return;
  try {
    await store.saveNow();
    await api("DELETE", `/api/projects/${project.id}`);
    await closeProject();
    refreshProjects();
  } catch (error) {
    toast(error.message, "error");
  }
}

function exportProject() {
  if (!store.open) return;
  addToExport({ kind: "project", data: { ...store.project, content: store.content } }, { quiet: false });
}

function setUpProjectPicker() {
  $("p2-project-menu").addEventListener("sl-show", (event) => {
    if (event.target !== $("p2-project-menu")) return;
    renderProjectMenu();
    loadProjectList();
  });
  $("p2-project-items").addEventListener("sl-select", (event) => {
    const value = event.detail.item.value;
    if (value === "new") newProject();
    else if (value.startsWith("open:")) openProject(Number(value.slice(5)));
    else if (value === "all") listTabs.pick("projects");
    else if (value === "export") exportProject();
    else if (value === "rename") renameProject();
    else if (value === "copy") copyProject();
    else if (value === "delete") deleteProject();
    else if (value === "close") closeProject();
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
function renderQualityMenu() {
  const items = [];
  if (player1.kind === "app") {
    const levels = player1.levels();
    const current = player1.level;
    items.push(el("sl-menu-item", { type: "checkbox", value: "level:-1", checked: current === -1 }, "Auto"));
    for (const level of levels) {
      items.push(el("sl-menu-item", { type: "checkbox", value: `level:${level.index}`, checked: current === level.index }, level.name));
    }
    items.push(el("sl-divider"));
  }
  items.push(el("sl-menu-item", { value: "switch" },
    el("sl-icon", { slot: "prefix", library: "ph", name: "swap" }),
    player1.kind === "app" ? "Use the Twitch player" : "Use the app player"));
  $("p1-quality-items").replaceChildren(...items);
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
      renderClock(position);
      p1Tick(position);
    },
    onState: () => {
      playAlone(player1, player2);
      renderPlayer1();
    },
    onDetails: (details) => timeline.setDetails(details),
  });
  timeline = new Timeline({
    onSeek: (seconds) => player1.seek(seconds),
    getPosition: () => (player1.ready ? player1.position() : null),
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    onChange: () => {
      renderTools();
      // A stored clip's re-trim waits 3 s (it downloads again)
      $("p1-info").toggleAttribute("data-saving", Boolean(timeline?.pendingTrim));
    },
    onClipsChanged: refreshClips,
    onVideosChanged: refreshVideos,
    onExport: exportFromTimeline,
    // A new segment or clip goes into the project open in Player 2 (at its playhead), without switching the tab
    onCreated: (item) => {
      if (store.open && store.canEdit) addToProject({ kind: item.kind, data: { ...item.data, vodId: timeline.vodId, vodLengthSeconds: timeline.length() } }, { show: false });
    },
    onEdited: () => {},
  });
  wirePlayerButtons(1, "p1");
  $("p1-live").addEventListener("click", () => {
    player1.seek(player1.livePosition());
    player1.play();
  });
  $("p1-quality-menu").addEventListener("sl-show", renderQualityMenu);
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
    onReplaced: () => timeline2.refresh(),
  });
  playback = new ProjectPlayback(player2, {
    getContent: () => store.content,
    black: $("p2-black"),
    onState: () => {
      renderPlayer2();
      timeline2.dirty = true;
    },
  });
  playback.onError = (error) => showNote("p2", `Couldn't play this VOD: ${error.message}`, 8000);
  timeline2 = new ProjectTimeline(store, {
    getPosition: p2Position,
    onSeek: p2Seek,
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    ensureProject: () => newProject({ show: false }),
    onChange: () => {
      renderTools();
      if (projectVerticalOpen()) refreshProjectVertical();
    },
    onShowInPlayer1: showInPlayer1,
    onApplyToOriginal: applyToOriginal,
  });
  wirePlayerButtons(2, "p2");
  setUpProjectVertical($("player2"), {
    store,
    playback,
    player: player2,
    timeline: () => timeline2,
    onExit: () => toggleVertical(false),
    onUndoChanged: () => renderTools(),
  });
  $("p2-vertical").addEventListener("click", () => toggleVertical());
  $("p2-show").addEventListener("click", () => {
    const now = playback.time();
    const piece = timeline2.selectedPieces()[0] ?? store.content?.pieces.find((p) => now >= p.at && now < p.at + p.end - p.start);
    if (piece) showInPlayer1(piece);
  });
  $("p2-rename").addEventListener("click", renameProject);
  $("p2-delete").addEventListener("click", closeProject);
  $("p2-export").addEventListener("click", exportProject);
  setUpProjectPicker();
  renderPlayer2();
  // The project open last time
  const last = loadSetting(LAST_PROJECT_KEY, null);
  if (last && !DEMO) openProject(last, { show: false, quiet: true });
}

// A project card was renamed or deleted: the open one follows
async function onProjectCardChanged(id, { deleted = false } = {}) {
  loadProjectList();
  if (store?.project?.id !== id) return;
  if (deleted) await closeProject();
  else await openProject(id, { show: false, quiet: true });
}

function refreshProjects() {
  loadProjectList();
  refreshProjectCards();
}

// Player 2's crosshair (or a piece's right-click): Player 1 opens the piece's VOD, goes to it and shows its range; the clip or
// segment it was copied from is selected when it's still there
async function showInPlayer1(piece) {
  const vodId = String(piece.vodId);
  playback.pause();
  if (!p1Vod || String(p1Vod.vodId) !== vodId) {
    let info;
    try {
      info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(vodId)}`);
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    await openVod({ vodId: info.vodId, title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive }, { start: piece.start, paused: true });
  } else {
    player1.pause();
    player1.seek(piece.start);
  }
  timelineTabs.pick("p1");
  const focus = piece.source?.kind === "clip" ? { type: "clip", id: piece.source.id } : piece.source?.kind === "segment" ? { type: "marker", id: piece.source.id } : { type: null, id: null };
  timeline.reveal(focus, { start: piece.start, end: piece.end });
}

// ---------- moments and clips made from Player 2 ----------
//
// M / X on Player 2 work like on Player 1, on the VOD under the playhead: the moment or clip goes on the member's own rows of that
// VOD (Player 1's timeline shows it). In a gap there's no VOD, so nothing is made. A clip starts and ends on the same VOD.

let p2Marking = null; // { vodId, start } while a clip waits for its end

function p2CanCreate() {
  return Boolean(store?.open) && !DEMO && !me?.support && can("AddEntries");
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

function p2MarkClip() {
  if (!p2CanCreate()) return;
  const point = p2VodPoint();
  if (!point) return p2NoVod();
  if (!p2Marking) {
    if (p1RowFor(point.vodId, "clip") === false) return toast("Your clip rows are locked.");
    p2Marking = { vodId: point.vodId, start: point.seconds };
    toast("Clip started: press X again where it ends.");
    renderTools();
    return;
  }
  const marking = p2Marking;
  p2Marking = null;
  renderTools();
  if (marking.vodId !== point.vodId) return toast("A clip starts and ends on the same VOD: the playhead is on another VOD now.", "error");
  let start = Math.min(marking.start, point.seconds);
  let end = Math.max(marking.start, point.seconds);
  if (end - start < 0.5) return toast("Too short: move the playhead at least half a second away from the start.");
  const max = timeline.maxStoredClipSeconds;
  if (end - start > max) {
    if (point.seconds < marking.start) start = end - max;
    else end = start + max;
    toast(`Clips are at most ${formatClock(max).replace(/^0:0?|\.00$/g, "")}; the clip was shortened.`);
  }
  playback.pause();
  openItemEditor({
    title: "",
    color: timeline.lastColor,
    colors: false,
    placeholder: "Clip title (blank = the VOD's title)",
    onApply: async (title) => {
      try {
        await api("POST", "/api/entries", {
          vodId: marking.vodId,
          title: title || null,
          trimMode: "Exact",
          startTime: trimValue(start),
          endTime: trimValue(end),
          rowId: p1RowFor(marking.vodId, "clip") || undefined,
        });
        refreshClips();
        p1Reload(marking.vodId);
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

// "Apply this trim to the original": the clip or segment a piece was copied from gets the range of every piece made from it in
// this project, and what's between them as its cut-out parts (can be undone with Ctrl+Z on Player 1 only for segments)
async function applyToOriginal(piece) {
  const source = piece.source;
  const from = store.content.pieces.filter((p) => p.source?.kind === source.kind && p.source.id === source.id && String(p.vodId) === String(piece.vodId))
    .sort((a, b) => a.start - b.start);
  const start = Math.min(...from.map((p) => p.start));
  const end = Math.max(...from.map((p) => p.end));
  const cuts = [];
  let at = start;
  for (const p of from) {
    if (p.start > at + 0.01) cuts.push({ start: at, end: p.start });
    at = Math.max(at, p.end);
  }
  const what = source.kind === "clip" ? "clip" : "segment";
  const ok = await confirmAction({
    heading: `Change the original ${what}?`,
    text: `It will run ${formatDuration(start, { short: false })} – ${formatDuration(end, { short: false })} in the VOD${cuts.length ? `, with ${cuts.length} part${cuts.length === 1 ? "" : "s"} cut out` : ""}, like the piece${from.length === 1 ? "" : "s"} made from it here. The project doesn't change.`,
    confirm: "Change it",
    danger: false,
  });
  if (!ok) return;
  try {
    if (source.kind === "clip") {
      await api("PUT", `/api/entries/${source.id}/trim`, { startTime: String(start), endTime: String(end), cuts });
      refreshClips();
    } else {
      const data = await api("GET", `/api/vods/${encodeURIComponent(piece.vodId)}/timeline`);
      const marker = data.markers?.find((m) => m.id === source.id);
      if (!marker) throw new Error("The segment isn't there any more.");
      await api("PUT", `/api/markers/${source.id}`, { seconds: start, endSeconds: end, label: marker.label, color: marker.color, rowId: marker.rowId, cuts });
    }
    if (timeline.vodId === String(piece.vodId)) timeline.load();
    toast(`The original ${what} was changed.`);
  } catch (error) {
    toast(`Couldn't change the original ${what}: ${error.message}`, "error");
  }
}

// ---------- 9:16 view of the project (ui/project-vertical.js) ----------
//
// Player 2 takes the whole width and shows the landscape source of the piece under the playhead with its crop boxes, the live
// 9:16 result and the layout tools; the project timeline stays below. Boxes are saved on the pieces (one undo history).

function toggleVertical(on = !projectVerticalOpen()) {
  if (!on) {
    if (!projectVerticalOpen()) return;
    closeProjectVertical();
    $("view-editor").classList.remove("vertical-mode");
    renderTools();
    return;
  }
  if (!store.open) {
    toast("Open a project in Player 2 first: drop clips, segments or videos on it.");
    return;
  }
  player1.pause();
  $("view-editor").classList.add("vertical-mode");
  timelineTabs.pick("p2");
  openProjectVertical();
  renderTools();
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
    button.disabled = !on;
    button.toggleAttribute("data-active", on && t.tool === button.dataset.tool);
  }
  const snap = document.querySelector('#tools [data-keybind="toggleSnap"]');
  snap.disabled = !on;
  snap.toggleAttribute("data-active", on && t.snap);
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    const kind = button.dataset.create;
    if (p2) {
      const enabled = p2Create && (kind === "marker" || kind === "clip");
      button.disabled = !enabled;
      button.toggleAttribute("data-active", enabled && kind === "clip" && Boolean(p2Marking));
      continue;
    }
    button.disabled = !canCreate;
    button.toggleAttribute("data-active", canCreate && (kind === "video" ? Boolean(t.linking) : t.marking?.kind === kind));
  }
  const undo = screenUndoState();
  document.querySelector('#tools [data-keybind="undo"]').disabled = !undo.canUndo;
  document.querySelector('#tools [data-keybind="redo"]').disabled = !undo.canRedo;
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
    clip: () => (activePlayer === "p2" ? p2MarkClip() : timeline.mark("clip")),
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
  activePlayer = which;
  timeline.setActive(which === "p1");
  timeline2.setActive(which === "p2");
  renderTools();
}

function setUpPlayers() {
  setUpPlayer1();
  setUpPlayer2();
  setUpTools();
  for (const player of ["p1", "p2"]) {
    dropTarget($(`${player}-screen`), {
      accepts: (item) => PLAYER_KINDS[player].includes(item.kind),
      onDrop: (item) => load(item),
    });
  }
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
    if ($("view-editor").hidden) return;
    // The 9:16 view has its own keys (boxes, keyframes); the rest of the player's still work there
    if (handleProjectVerticalKey(event)) return;
    const id = keybindFor("player", event);
    if (!id) return;
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
      clip: () => (p2 ? p2MarkClip() : on && t.mark("clip")),
      setStart: () => on && t.setEdgeAtPlayhead("start"),
      setEnd: () => on && t.setEdgeAtPlayhead("end"),
      undo: () => screenUndo(false),
      redo: () => screenUndo(true),
      deleteSelected: () => on && (p2 ? t.deleteSelected(false) : t.deleteSelected()),
      rippleDelete: () => p2 && on && t.deleteSelected(true),
      copyPieces: () => p2 && on && t.copySelected(),
      pastePieces: () => p2 && on && t.paste(),
      addRow: () => p2 && on && t.addTrack(),
      escape: () => {
        if (p2 && p2Marking) {
          p2Marking = null;
          renderTools();
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

// ---------- title and color editor in the tools bar ----------
//
// openItemEditor({ title, color, needsApply, onApply(title, color), onCancel }): the tools slide left and the editor
// shows on the right. Segments, markers and clips apply as soon as the user clicks anywhere else (or presses Enter);
// linking a video needs the apply button (needsApply). Esc cancels.

let itemEditor = null;

export function openItemEditor(options) {
  closeItemEditor(false);
  itemEditor = { ...options, color: options.color ?? "yellow" };
  $("item-title").value = options.title ?? "";
  $("item-title").placeholder = options.placeholder ?? "Title";
  $("item-apply").hidden = !options.needsApply;
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

async function start() {
  try {
    await loadMe();
  } catch {
    return; // 401 already went to the login page
  }
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
  wireIconButtons();
  setUpLayout();
  setUpExport({ demo: DEMO, otherPlayers: () => [player1, player2], onPausePlayers: () => playback?.pause() });
  screenTabs = tabs(document.querySelector(".main-tabs"), "data-view", showScreen, "clipco.editor.screen");
  setUpLibrary({ onLoad: load, onExport: openExport, onProjectChanged: onProjectCardChanged, sample: DEMO ? demoSample() : null });
  if (DEMO) wireDemoTools();
  listTabs = tabs(document.querySelector(".side-tabs"), "data-list", showList, "clipco.editor.list");
  setUpPlayers();
  timelineTabs = tabs(document.querySelector(".tl-tabs"), "data-tl", setActivePlayer, null);
  setUpKeys();
  setUpItemEditor();
}

start();
