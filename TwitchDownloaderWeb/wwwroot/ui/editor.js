// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { $, el, api, me, can, loadMe, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, loadSetting, saveSetting, toast, formatDuration } from "./core.js";
import { dropTarget } from "./drag.js";
import { setUpLibrary, showList, refreshClips } from "./library.js";
import { MARKER_COLORS } from "./colors.js";
import { MediaPlayer, formatClock } from "./media.js";
import { Timeline } from "./timeline.js";

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
    case "classic": location.href = "/"; break;
    case "logout":
      await fetch("/api/auth/logout", { method: "POST" });
      location.href = "/login.html";
      break;
  }
});
$("support-exit").addEventListener("click", exitSupport);

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

function openExport(item) {
  screenTabs.pick("export");
  if (DEMO) toast(`"${item.data?.title ?? item.label}" would be added to the export list (step 8).`);
}

// ---------- players ----------
//
// Player 1 plays VODs with every member's rows on its timeline. Player 2 plays one segment or clip, locked to it: its
// timeline (the Player 2 tab) shows only that item, and playback stays inside it. Both edit the same items, so a change
// in one is read again by the other. The keys and the tools bar work on the player whose timeline tab is open; clicking
// a player that has something loaded opens its tab.

const PLAYER_KINDS = { p1: ["vod"], p2: ["video", "clip", "segment"] };
const FRAME_SECONDS = 1 / 30;

let player1 = null;
let player2 = null;
let timeline = null;   // Player 1's
let timeline2 = null;  // Player 2's
let p1Vod = null;
let p2Item = null;     // { type: "marker" | "clip", id, vodId, kind: "segment" | "clip", title }
let activePlayer = "p1";

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
  openInPlayer2(item);
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

// Player 2: a segment or clip. show = open the Player 2 timeline tab (not for new items made on Player 1's timeline,
// so marking can go on there)
async function openInPlayer2({ kind, data }, { show = true } = {}) {
  if (DEMO) {
    toast("Playback isn't available in the demo.");
    return;
  }
  if (kind === "video") {
    toast("Videos open in Player 2 once linking segments into videos is built (step 6).");
    return;
  }
  const vodId = data.vodId != null ? String(data.vodId) : "";
  if (!vodId || vodId === "0") {
    toast("Twitch didn't say which VOD this clip is from, so it can't play here.", "error");
    return;
  }
  const type = kind === "clip" ? "clip" : "marker";
  const start = kind === "clip" ? (data.startSeconds ?? data.start ?? 0) : data.seconds;
  const target = { type, id: data.id, vodId, kind, title: data.title ?? data.label ?? "" };
  p2Item = target;
  $("p2-hint").hidden = true;
  $("p2-media").hidden = false;
  renderP2Info();
  if (show) timelineTabs.pick("p2");
  showNote("p2", "Loading…");
  const lengthSeconds = data.vodLengthSeconds || (p1Vod && String(p1Vod.vodId) === vodId ? p1Vod.lengthSeconds : 0);
  const loaded = timeline2.openItem({ type, id: data.id, vodId, lengthSeconds });
  try {
    if (player2.ready && player2.vodId === vodId) {
      player2.pause();
      player2.seek(start);
    } else {
      await player2.open(vodId, start);
      player2.pause();
    }
    if (p2Item !== target) return;
    showNote("p2", playerNotes(player2), 8000);
  } catch (error) {
    if (p2Item !== target) return;
    showNote("p2", `Couldn't play this ${kind}: ${error.message}`);
  }
  await loaded;
  renderPlayer2();
}

function closePlayer2(message) {
  const kind = p2Item?.kind;
  p2Item = null;
  player2.close();
  timeline2.close();
  $("p2-media").hidden = true;
  $("p2-hint").hidden = false;
  showNote("p2", "");
  renderP2Info();
  renderPlayer2();
  if (activePlayer === "p2") renderTools();
  if (message !== undefined) toast(message ?? `The ${kind ?? "item"} in Player 2 was deleted.`);
}

const noteTimers = {};
function showNote(which, text, hideAfter = 0) {
  clearTimeout(noteTimers[which]);
  const note = $(`${which}-note`);
  note.textContent = text;
  note.hidden = !text;
  if (text && hideAfter) noteTimers[which] = setTimeout(() => { note.hidden = true; }, hideAfter);
}

// Play / pause / volume / fullscreen buttons of player n (1 or 2)
function renderPlayerButtons(n, player) {
  const ready = player.ready;
  for (const id of ["play", "back", "fwd", "full"]) $(`p${n}-${id}`).disabled = !ready;
  const app = ready && player.kind === "app";
  $(`p${n}-mute`).disabled = !app;
  $(`p${n}-volume`).disabled = !app;
  const playing = player.playing();
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
  renderPlayerButtons(2, player2);
  const item = p2Item ? timeline2.focusItem() : null;
  $("p2-show").disabled = !item;
  $("p2-rename").disabled = !item || !timeline2.canEditItem(item);
  $("p2-delete").disabled = !item || !timeline2.canDeleteItem(item);
}

// Top right of Player 2: type, length (what's left after cut-out parts) and title
function renderP2Info() {
  const info = $("p2-info");
  if (!p2Item) {
    info.replaceChildren();
    return;
  }
  const item = timeline2.focusItem();
  const kind = p2Item.kind === "clip" ? "Clip" : "Segment";
  const title = (item ? item.title : p2Item.title) || (kind === "Clip" ? "Untitled clip" : "Untitled segment");
  let length = null;
  if (item && item.end != null) {
    const cut = (item.cuts ?? []).reduce((sum, c) => sum + Math.max(0, Math.min(c.end, item.end) - Math.max(c.start, item.start)), 0);
    length = item.end - item.start - cut;
  }
  info.replaceChildren(...[
    el("span", { class: "p2-kind" }, kind),
    length != null ? el("span", { class: "p2-length num" }, formatDuration(Math.round(length))) : null,
    el("span", { class: "p2-title", title }, title),
    timeline2.pendingTrim ? el("span", { class: "p2-saving" }, "saving…") : null,
  ].filter(Boolean));
}

// The range Player 2 plays changed (trimmed here or on Player 1): a running preview stops at the new end
function onP2FocusChanged(item) {
  if (!p2Item || !item) return;
  p2Item.title = item.title;
  if (player2.stopAt != null && item.end != null) player2.stopAt = item.end;
  renderP2Info();
  renderPlayer2();
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
  if (playing && !player.wasPlaying && other.playing()) other.pause();
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
    onTick: (position) => renderClock(position),
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
    onChange: renderTools,
    onClipsChanged: refreshClips,
    onCreated: (item) => openInPlayer2({ kind: item.kind, data: { ...item.data, vodId: timeline.vodId, vodLengthSeconds: timeline.length() } }, { show: false }),
    onEdited: () => {
      if (timeline2.vodId && timeline2.vodId === timeline.vodId) timeline2.load();
    },
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

function setUpPlayer2() {
  player2 = new MediaPlayer($("p2-media"), {
    onState: () => {
      playAlone(player2, player1);
      renderPlayer2();
    },
    onDetails: (details) => timeline2.setDetails(details),
  });
  timeline2 = new Timeline({
    mode: "item",
    onSeek: (seconds) => player2.seek(seconds),
    getPosition: () => (player2.ready ? player2.position() : null),
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    onChange: () => {
      renderTools();
      renderP2Info();
      renderPlayer2();
    },
    onClipsChanged: refreshClips,
    onEdited: () => {
      if (timeline.vodId && timeline.vodId === timeline2.vodId) timeline.load();
    },
    onFocusChanged: onP2FocusChanged,
    onFocusGone: (message) => {
      if (p2Item) closePlayer2(message);
    },
  });
  wirePlayerButtons(2, "p2");
  $("p2-show").addEventListener("click", showInPlayer1);
  $("p2-rename").addEventListener("click", () => {
    const item = timeline2.focusItem();
    if (item) timeline2.editTitle(item);
  });
  $("p2-delete").addEventListener("click", () => {
    const item = timeline2.focusItem();
    if (item) timeline2.deleteItem(item);
  });
  renderPlayer2();
}

// Player 2's crosshair: Player 1 opens the item's VOD (if it isn't open already), goes to the item and selects it
async function showInPlayer1() {
  const item = timeline2.focusItem();
  if (!p2Item || !item) return;
  const focus = { ...timeline2.focus };
  const vodId = p2Item.vodId;
  player2.pause();
  if (!p1Vod || String(p1Vod.vodId) !== vodId) {
    let info;
    try {
      info = await api("GET", `/api/vods/info?vod=${encodeURIComponent(vodId)}`);
    } catch (error) {
      toast(error.message, "error");
      return;
    }
    await openVod({ vodId: info.vodId, title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive }, { start: item.start, paused: true });
  } else {
    player1.pause();
    player1.seek(item.start);
  }
  timelineTabs.pick("p1");
  timeline.reveal(focus);
}

// ---------- tools bar (works on the timeline whose tab is open) ----------

function renderTools() {
  if (DEMO || !timeline || !timeline2) return;
  const t = activeTimeline();
  const on = Boolean(t.vodId);
  const canCreate = on && t.canCreate();
  for (const button of document.querySelectorAll("#tools [data-tool]")) {
    button.disabled = !on || (button.dataset.tool === "cut" && t.mode === "item"); // Player 2's cut tool: step 7
    button.toggleAttribute("data-active", on && t.tool === button.dataset.tool);
  }
  const snap = document.querySelector('#tools [data-keybind="toggleSnap"]');
  snap.disabled = !on;
  snap.toggleAttribute("data-active", on && t.snap);
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    const kind = button.dataset.create;
    button.disabled = !canCreate || kind === "video"; // linking videos comes in step 6
    button.toggleAttribute("data-active", canCreate && t.marking?.kind === kind);
  }
  document.querySelector('#tools [data-keybind="undo"]').disabled = !t.canUndo();
  document.querySelector('#tools [data-keybind="redo"]').disabled = !t.canRedo();
}

function setUpTools() {
  if (DEMO) return;
  for (const button of document.querySelectorAll("#tools [data-tool]")) {
    button.addEventListener("click", () => activeTimeline().setTool(button.dataset.tool));
  }
  document.querySelector('#tools [data-keybind="toggleSnap"]').addEventListener("click", () => activeTimeline().toggleSnap());
  document.querySelector('#tools [data-keybind="undo"]').addEventListener("click", () => activeTimeline().undo());
  document.querySelector('#tools [data-keybind="redo"]').addEventListener("click", () => activeTimeline().redo());
  const create = {
    marker: () => activeTimeline().addMoment(),
    segment: () => activeTimeline().mark("segment"),
    clip: () => activeTimeline().mark("clip"),
  };
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    button.addEventListener("click", () => create[button.dataset.create]?.());
  }
  renderTools();
}

// Play / pause: with a segment or clip selected (Player 2: always its item) and the player paused, play just it
function playPause(which = activePlayer) {
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

function playLastSeconds(which = activePlayer) {
  const { player, timeline: t } = pair(which);
  const range = t.selectedRange();
  if (!player.ready || !range) return;
  player.playRange(Math.max(range.start, range.end - 3), range.end);
}

// Seeking stays inside what the timeline can show (Player 2: the window around its item)
function skip(which, delta) {
  const { player, timeline: t } = pair(which);
  const now = player.position();
  if (!player.ready || now == null) return;
  const extent = t.extent();
  player.seek(Math.min(Math.max(now + delta, extent.start), extent.end));
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
    if (activePlayer !== "p2" && p2Item) timelineTabs.pick("p2");
  });
  // A stored clip's trim waits 3 s before it's saved; don't lose it when the page closes
  window.addEventListener("pagehide", () => {
    timeline.flushTrimOnExit();
    timeline2.flushTrimOnExit();
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
    if ($("view-editor").hidden || isTyping(event) || document.querySelector("sl-dialog[open]")) return;
    const id = keybindFor("player", event);
    if (!id) return;
    const { player, timeline: t } = pair();
    const ready = player.ready;
    const on = Boolean(t.vodId);
    const frame = (delta) => {
      if (!ready) return;
      player.pause();
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
      fullscreen: () => ready && player.fullscreen(),
      zoomIn: () => on && t.zoomBy(0.5),
      zoomOut: () => on && t.zoomBy(2),
      fit: () => on && t.fit(),
      toolPointer: () => on && t.setTool("pointer"),
      toolCut: () => on && t.setTool("cut"),
      toolZoom: () => on && t.setTool("zoom"),
      toolHand: () => on && t.setTool("hand"),
      toggleSnap: () => on && t.toggleSnap(),
      marker: () => on && t.addMoment(),
      segment: () => on && t.mark("segment"),
      clip: () => on && t.mark("clip"),
      setStart: () => on && t.setEdgeAtPlayhead("start"),
      setEnd: () => on && t.setEdgeAtPlayhead("end"),
      undo: () => on && t.undo(),
      redo: () => on && t.redo(),
      deleteSelected: () => on && t.deleteSelected(),
      escape: () => on && t.escape(),
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
  screenTabs = tabs(document.querySelector(".main-tabs"), "data-view", showScreen, "clipco.editor.screen");
  setUpLibrary({ onLoad: load, onExport: openExport, sample: DEMO ? demoSample() : null });
  if (DEMO) wireDemoTools();
  listTabs = tabs(document.querySelector(".side-tabs"), "data-list", showList, "clipco.editor.list");
  setUpPlayers();
  timelineTabs = tabs(document.querySelector(".tl-tabs"), "data-tl", setActivePlayer, null);
  setUpKeys();
  setUpItemEditor();
}

start();
