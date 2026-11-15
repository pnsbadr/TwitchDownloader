// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { $, el, api, me, can, loadMe, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, loadSetting, saveSetting, toast, formatDuration } from "./core.js";
import { dropTarget } from "./drag.js";
import { setUpLibrary, showList } from "./library.js";
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

const PLAYER_KINDS = { p1: ["vod"], p2: ["video", "clip", "segment"] };
const FRAME_SECONDS = 1 / 30;

let player1 = null;
let timeline = null;
let p1Vod = null;

// item = { kind, data } from a card or a drop. Player 2 comes in step 4.
function load(item) {
  const data = item.data;
  const player = PLAYER_KINDS.p1.includes(item.kind) ? "p1" : "p2";
  if (player === "p1") {
    openVod(data);
  } else {
    const length = data.clipLengthSeconds ?? data.lengthSeconds;
    $("p2-info").textContent = [item.kind.toUpperCase(), length !== undefined ? formatDuration(length) : null, data.title].filter(Boolean).join(" · ");
  }
  timelineTabs.pick(player);
}

async function openVod(vod) {
  if (DEMO) {
    toast("Playback isn't available in the demo.");
    return;
  }
  p1Vod = vod;
  $("p1-info").textContent = vod.title;
  $("p1-info").title = vod.title;
  $("p1-hint").hidden = true;
  $("p1-media").hidden = false;
  showNote("Loading…");
  timeline.open(vod);
  try {
    await player1.open(vod.vodId, 0);
    if (p1Vod !== vod) return;
    const notes = [];
    if (player1.fallbackReason) notes.push(`Twitch player: our player couldn't play this VOD (${player1.fallbackReason}).`);
    if (player1.cleanSegments) notes.push("Saved audio plays over muted parts.");
    showNote(notes.join(" "), notes.length ? 8000 : 0);
  } catch (error) {
    if (p1Vod !== vod) return;
    showNote(`Couldn't play this VOD: ${error.message}`);
  }
}

let noteTimer = null;
function showNote(text, hideAfter = 0) {
  clearTimeout(noteTimer);
  $("p1-note").textContent = text;
  $("p1-note").hidden = !text;
  if (text && hideAfter) noteTimer = setTimeout(() => { $("p1-note").hidden = true; }, hideAfter);
}

function renderPlayer1() {
  const ready = player1.ready;
  for (const id of ["p1-play", "p1-back", "p1-fwd", "p1-full", "p1-quality"]) $(id).disabled = !ready;
  const app = ready && player1.kind === "app";
  $("p1-mute").disabled = !app;
  $("p1-volume").disabled = !app;
  const playing = player1.playing();
  $("p1-play").name = playing ? "pause" : "play";
  $("p1-play").label = playing ? "Pause" : "Play";
  const volume = player1.volume;
  if (volume) {
    $("p1-volume").value = Math.round(volume.volume * 100);
    $("p1-mute").name = volume.muted || volume.volume === 0 ? "speaker-slash" : volume.volume < 0.5 ? "speaker-low" : "speaker-high";
    $("p1-mute").label = volume.muted ? "Unmute" : "Mute";
  }
  renderClock(player1.position());
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

function setUpPlayer1() {
  player1 = new MediaPlayer($("p1-media"), {
    onTick: (position) => renderClock(position),
    onState: () => renderPlayer1(),
    onDetails: (details) => timeline.setDetails(details),
  });
  timeline = new Timeline({
    onSeek: (seconds) => player1.seek(seconds),
    getPosition: () => (player1.ready ? player1.position() : null),
  });
  $("p1-play").addEventListener("click", () => player1.togglePlay());
  $("p1-back").addEventListener("click", () => player1.skip(-10));
  $("p1-fwd").addEventListener("click", () => player1.skip(10));
  $("p1-full").addEventListener("click", () => player1.fullscreen());
  $("p1-live").addEventListener("click", () => {
    player1.seek(player1.livePosition());
    player1.play();
  });
  $("p1-mute").addEventListener("click", () => {
    const volume = player1.volume;
    if (!volume) return;
    if (volume.volume === 0) player1.setVolume(0.5, false);
    else player1.setVolume(null, !volume.muted);
  });
  $("p1-volume").addEventListener("sl-input", (event) => player1.setVolume(Number(event.target.value) / 100, false));
  $("p1-quality-menu").addEventListener("sl-show", renderQualityMenu);
  $("p1-quality-items").addEventListener("sl-select", async (event) => {
    const value = event.detail.item.value;
    if (value === "switch") {
      showNote("Switching player…");
      try {
        await player1.switchPlayer();
        showNote("");
      } catch (error) {
        showNote(`Couldn't play this VOD: ${error.message}`);
      }
    } else if (value.startsWith("level:")) {
      player1.level = Number(value.slice(6));
    }
  });
  renderPlayer1();
}

function setUpPlayers() {
  setUpPlayer1();
  for (const player of ["p1", "p2"]) {
    dropTarget($(`${player}-screen`), {
      accepts: (item) => PLAYER_KINDS[player].includes(item.kind),
      onDrop: (item) => load(item),
    });
  }
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
    const ready = player1.ready;
    const run = {
      playPause: () => ready && player1.togglePlay(),
      back10: () => ready && player1.skip(-10),
      fwd10: () => ready && player1.skip(10),
      back5: () => ready && player1.skip(-5),
      fwd5: () => ready && player1.skip(5),
      frameBack: () => { if (ready) { player1.pause(); player1.skip(-FRAME_SECONDS); } },
      frameFwd: () => { if (ready) { player1.pause(); player1.skip(FRAME_SECONDS); } },
      fullscreen: () => ready && player1.fullscreen(),
      zoomIn: () => timeline.vodId && timeline.zoomBy(0.5),
      zoomOut: () => timeline.vodId && timeline.zoomBy(2),
      fit: () => timeline.vodId && timeline.fit(),
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
  $("item-apply").hidden = !options.needsApply;
  renderSwatches();
  $("toolsbar").classList.add("editing");
  $("item-editor").hidden = false;
  setTimeout(() => $("item-title").focus(), 0);
}

function closeItemEditor(apply) {
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
    swatch.addEventListener("click", () => { itemEditor.color = name; renderSwatches(); });
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
  timelineTabs = tabs(document.querySelector(".tl-tabs"), "data-tl", (tab) => timeline.showTab(tab), null);
  setUpKeys();
  setUpItemEditor();
}

start();
