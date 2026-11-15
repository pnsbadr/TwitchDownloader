// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { $, el, api, me, can, loadMe, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, iconButton, loadSetting, saveSetting, toast } from "./core.js";
import { draggable, dropTarget } from "./drag.js";

// Phosphor icons, bold weight, as <sl-icon library="ph" name="…">
registerIconLibrary("ph", {
  resolver: (name) => `https://cdn.jsdelivr.net/npm/@phosphor-icons/core@2.1.1/assets/bold/${name}-bold.svg`,
});

const DEMO = new URLSearchParams(location.search).has("demo");

// Same names and order as VodMarker.Colors on the server and MARKER_COLORS in player-bar.js
export const MARKER_COLORS = {
  yellow: "#f2c230", blue: "#3d8bfd", pink: "#ff5fa2", orange: "#ff8a3d",
  red: "#ff4d4d", lime: "#9be15d", teal: "#2ec4b6", violet: "#a78bfa", gray: "#a1a1aa",
};

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

// ---------- left panel ----------

const lists = { vods: [], videos: [], clips: [] };
let currentList = "vods";

const EMPTY_TEXT = {
  vods: "Your team's VODs and live streams will be listed here.",
  videos: "Videos you link from segments will be listed here.",
  clips: "Your team's clips will be listed here.",
};

function renderList() {
  const items = lists[currentList];
  $("side-count").textContent = items.length ? `${items.length} ${currentList}` : "";
  if (!items.length) {
    $("side-list").replaceChildren(el("p", { class: "side-empty" }, EMPTY_TEXT[currentList]));
    return;
  }
  $("side-list").replaceChildren(...items.map(card));
}

// A card in the left panel. item = { kind: "vod" | "video" | "clip", title, meta: [..], thumb?, live?, length? }
function card(item) {
  const node = el("div", { class: "card", role: "listitem", tabindex: "0", "aria-label": `${item.kind}: ${item.title}` },
    el("div", { class: "card-thumb", style: item.thumb ? `background-image:url("${item.thumb}")` : null },
      item.live ? el("span", { class: "card-badge live" }, "LIVE") : null,
      item.length ? el("span", { class: "card-badge num" }, item.length) : null),
    el("div", { class: "card-body" },
      el("div", { class: "card-title" }, item.title),
      ...item.meta.map((line) => el("div", { class: "card-meta" }, line)),
      el("div", { class: "card-actions" },
        iconButton({ icon: "download-simple", label: "Export", onClick: () => openExport(item) }))));
  draggable(node, () => ({ kind: item.kind, label: item.title, data: item }));
  node.addEventListener("dblclick", () => load(item));
  node.addEventListener("keydown", (event) => { if (event.key === "Enter" && event.target === node) load(item); });
  return node;
}

function openExport(item) {
  screenTabs.pick("export");
  if (DEMO) toast(`"${item.title}" would be added to the export list (step 8).`);
}

// ---------- players (filled in steps 3 and 4) ----------

const PLAYER_KINDS = { p1: ["vod"], p2: ["video", "clip", "segment"] };

function load(item) {
  const player = PLAYER_KINDS.p1.includes(item.kind) ? "p1" : "p2";
  $(`${player}-info`).textContent = player === "p1" ? item.title : `${item.kind.toUpperCase()} · ${item.length ?? ""} · ${item.title}`;
  timelineTabs.pick(player);
  if (DEMO) toast(`Loaded "${item.title}" in ${player === "p1" ? "Player 1" : "Player 2"} (demo).`);
}

function setUpPlayers() {
  for (const player of ["p1", "p2"]) {
    dropTarget($(`${player}-screen`), {
      accepts: (item) => PLAYER_KINDS[player].includes(item.kind),
      onDrop: (item) => load(item.data),
    });
  }
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

// ---------- demo (?demo): sample cards to try dragging, tabs and the title editor ----------

function fillDemo() {
  lists.vods = [
    { kind: "vod", title: "🔶 qsmp @ARKY X @ELLSYA 🔶 !qsmp !comm !socials", meta: ["arky · Nov 14, 2026"], length: "6:12:40", live: false },
    { kind: "vod", title: "yellow! !comm !socials !discord !oscs", meta: ["arky · Nov 15, 2026"], live: true },
    { kind: "vod", title: "NMS BOXING INSTIGATOR 🔶 !oscs !comm !socials", meta: ["arky · Nov 12, 2026"], length: "4:03:11" },
  ];
  lists.videos = [
    { kind: "video", title: "Boxing highlights", meta: ["VOD Nov 12, 2026", "by badrfly"], length: "3:41" },
  ];
  lists.clips = [
    { kind: "clip", title: "chose to like it", meta: ["VOD Nov 14, 2026", "by badrfly"], length: "0:48" },
    { kind: "clip", title: "the instigator", meta: ["VOD Nov 12, 2026", "by badrfly"], length: "1:12" },
  ];
  renderList();
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
  listTabs = tabs(document.querySelector(".side-tabs"), "data-list", (value) => { currentList = value; renderList(); }, "clipco.editor.list");
  timelineTabs = tabs(document.querySelector(".tl-tabs"), "data-tl", () => {}, null);
  setUpPlayers();
  setUpItemEditor();
  if (DEMO) fillDemo();
}

start();
