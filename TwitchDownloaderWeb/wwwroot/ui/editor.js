// The new editor page (frontend rework). Step 1: app shell, account menu, layout, tooltips, drag and drop, title/color
// editor. Lists, players and the timeline are filled in by the next steps. Open with ?demo to try the shell with sample
// cards (nothing is sent to the server).

import { registerIconLibrary } from "https://cdn.jsdelivr.net/npm/@shoelace-style/shoelace@2.20.1/cdn/utilities/icon-library.js";
import { $, el, api, me, can, loadMe, sessionHandlers, TEAM_PAGE_PERMISSIONS, wireIconButtons, loadSetting, saveSetting, toast, formatDuration } from "./core.js";
import { dropTarget } from "./drag.js";
import { setUpLibrary, showList, refreshClips, refreshVideos } from "./library.js";
import { MARKER_COLORS } from "./colors.js";
import { MediaPlayer, formatClock } from "./media.js";
import { Timeline } from "./timeline.js";
import { SourceTimeline, ago } from "./source-timeline.js";
import { skipCut, keptSeconds, normalizeCuts } from "./cuts.js";
import { setUpExport, addToExport, exportShown, pausePreview } from "./export.js";
import { showShortcuts } from "./shortcuts.js";
import { setUpVertical, openVertical, closeVertical, verticalOpen, verticalEntryId, pauseVertical, verticalEntryChanged, handleVerticalKey, verticalTime, verticalSeek, setVerticalPartStarts } from "./vertical.js";

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
    pauseVertical();
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
// Player 1 plays VODs with every member's rows on its timeline. Player 2 plays one segment, clip or video as a SOURCE
// (ui/source-timeline.js): its timeline runs from 0:00 to the item's length (a video: its segments end to end), and what's
// done there is a saved edit of it that doesn't change Player 1 until it's applied. Player 2's position is mapped between
// VOD seconds and source time (p2Position). A change to an item on one is read again by the other. The keys and the tools
// bar work on the player whose timeline tab is open; clicking a player that has something loaded opens its tab.

const PLAYER_KINDS = { p1: ["vod"], p2: ["video", "clip", "segment"] };
const FRAME_SECONDS = 1 / 30;

let player1 = null;
let player2 = null;
let timeline = null;   // Player 1's
let timeline2 = null;  // Player 2's
let p1Vod = null;
let p2Item = null;     // { type: "marker" | "clip" | "video", id, vodId, kind: "segment" | "clip" | "video", title }
let p2Part = 0;        // a video: the index of the part playing
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

// Player 2: a segment, clip or video. show = open the Player 2 timeline tab (not for new items made on Player 1's timeline,
// so marking can go on there); editId = the saved edit to show (else the one last open for the item, else Original)
async function openInPlayer2({ kind, data }, { show = true, editId } = {}) {
  if (DEMO) {
    toast("Playback isn't available in the demo.");
    return;
  }
  const vodId = data.vodId != null ? String(data.vodId) : "";
  if (!vodId || vodId === "0") {
    toast("Twitch didn't say which VOD this clip is from, so it can't play here.", "error");
    return;
  }
  const type = { clip: "clip", video: "video" }[kind] ?? "marker";
  const firstPart = kind === "video" ? [...(data.segments ?? [])].sort((a, b) => a.position - b.position)[0] : null;
  const start = kind === "clip" ? (data.startSeconds ?? data.start ?? 0) : kind === "video" ? (firstPart?.startSeconds ?? 0) : data.seconds;
  p2Part = 0;
  p2StopAt = null;
  const target = { type, id: data.id, vodId, kind, title: data.title ?? data.label ?? "" };
  if (verticalOpen() && !(type === "clip" && verticalEntryId() === data.id)) toggleVertical(false);
  p2Item = target;
  $("p2-hint").hidden = true;
  $("p2-media").hidden = false;
  renderP2Info();
  if (show) timelineTabs.pick("p2");
  showNote("p2", "Loading…");
  const lengthSeconds = data.vodLengthSeconds || (p1Vod && String(p1Vod.vodId) === vodId ? p1Vod.lengthSeconds : 0);
  const loaded = timeline2.openItem({ type, id: data.id, vodId, lengthSeconds, editId: editId ?? data.editId });
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
  // Start where the edit on show starts
  if (p2Item === target && player2.ready && !player2.playing() && !verticalOpen()) p2Seek(timeline2.keptRange().start);
  renderPlayer2();
}

function closePlayer2(message) {
  toggleVertical(false);
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

// ---------- Player 2 in source time (saved edits) ----------
//
// Player 2's timeline is in source time: seconds into its item (a video: its segments end to end). sourceParts() says where
// each part is in the VOD; p2Part is the one playing. Playing skips what the edit on show leaves out and stops at its end.

let p2StopAt = null;   // source time Player 2 stops at (Shift+Space), else the edit's end

function p2Parts() {
  return timeline2?.sourceParts() ?? [];
}

function partLength(part) {
  return part.vodEnd - part.vodStart;
}

// Player 2's position on its timeline (source time)
function p2Position() {
  if (verticalOpen()) {
    const t = verticalTime();
    return t == null ? null : fileToSource(t);
  }
  if (!player2.ready) return null;
  const position = player2.position();
  const parts = p2Parts();
  if (position == null || !parts.length) return null;
  const inPart = (part) => position >= part.vodStart - 0.05 && position <= part.vodEnd + 0.05;
  if (!parts[p2Part] || !inPart(parts[p2Part])) {
    const found = parts.findIndex(inPart);
    if (found >= 0) p2Part = found;
  }
  const part = parts[Math.min(p2Part, parts.length - 1)];
  return part.offset + Math.min(Math.max(position - part.vodStart, 0), partLength(part));
}

function p2Seek(seconds) {
  if (verticalOpen()) {
    const t = sourceToFile(seconds);
    if (t != null) verticalSeek(t);
    return;
  }
  const parts = p2Parts();
  if (!parts.length) return;
  let index = parts.findIndex((part) => seconds < part.offset + partLength(part));
  if (index < 0) index = parts.length - 1;
  p2Part = index;
  const part = parts[index];
  player2.seek(part.vodStart + Math.min(Math.max(seconds - part.offset, 0), partLength(part)));
}

// The vertical editor plays the clip's file. Files made since saved edits keep the whole clip, so seconds into the file are
// source time; an older file was made without the clip's cut-out parts (fileHasCuts), so its seconds skip those.
function fileCuts() {
  const item = timeline2.focusItem();
  return item?.data?.fileHasCuts ? normalizeCuts(item.data.cuts ?? [], item.vodStart, item.vodEnd)
    .map((c) => ({ start: c.start - item.vodStart, end: c.end - item.vodStart })) : [];
}

function fileToSource(t) {
  let at = t;
  for (const cut of fileCuts()) {
    if (at >= cut.start) at += cut.end - cut.start;
  }
  return Math.min(at, timeline2.length());
}

function sourceToFile(seconds) {
  let t = seconds;
  for (const cut of [...fileCuts()].reverse()) {
    if (seconds >= cut.end) t -= cut.end - cut.start;
    else if (seconds > cut.start) t -= seconds - cut.start;
  }
  return Math.max(0, t);
}

// Cut-out parts are skipped while playing: jumps over the one at position (or stops when it runs to end). true = jumped.
function jumpCuts(player, cuts, position, end) {
  const to = skipCut(cuts, position, end);
  if (to == null) return false;
  if (to === "end") {
    player.stopAt = null;
    player.pause();
  } else {
    player.seek(to);
  }
  return true;
}

// Player 1 skips the cut-out parts of the selected segment or clip while it plays just that
function p1Tick(position) {
  if (!player1.playing() || player1.stopAt == null) return;
  const item = timeline.selectedItem();
  if (item?.cuts?.length) jumpCuts(player1, item.cuts, position, player1.stopAt);
}

// Player 2 plays what its edit keeps: it jumps over left-out parts, from the end of one part of a video to the next, and
// stops at the edit's end (or where Shift+Space said)
function p2Tick() {
  if (!player2.playing() || verticalOpen()) return;
  const parts = p2Parts();
  const part = parts[p2Part];
  const now = p2Position();
  if (!part || now == null) return;
  const kept = timeline2.keptRange();
  const stop = Math.min(p2StopAt ?? kept.end, kept.end);
  if (now >= stop - 0.04) {
    p2StopAt = null;
    player2.pause();
    return;
  }
  const cut = timeline2.removedRanges().find((r) => now >= r.start && now < r.end - 0.04);
  if (cut) {
    if (cut.end >= stop - 0.05) {
      p2StopAt = null;
      player2.pause();
    } else {
      p2Seek(cut.end);
    }
    return;
  }
  if (player2.position() >= part.vodEnd - 0.04 && p2Part + 1 < parts.length) p2Seek(parts[p2Part + 1].offset);
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
  // The vertical editor works on a clip's file
  $("p2-vertical").hidden = p2Item?.kind !== "clip";
  $("p2-vertical").disabled = p2Item?.kind !== "clip" || DEMO;
  const source = p2Item ? timeline2.source : null;
  $("p2-show").disabled = !source;
  // Rename = the item itself (its title on Player 1 and the cards); edits are renamed in the edit picker
  $("p2-rename").disabled = !source || Boolean(me?.support) || !source.canEdit;
  $("p2-apply").hidden = !source;
  $("p2-apply").disabled = !timeline2.canApply();
  $("p2-delete").disabled = !p2Item;
  renderEditPicker();
}

// Top right of Player 2: type, length (what the edit on show keeps) and title
function renderP2Info() {
  const info = $("p2-info");
  if (!p2Item) {
    info.replaceChildren();
    return;
  }
  const focus = timeline2.focusInfo();
  const kind = { clip: "Clip", video: "Video" }[p2Item.kind] ?? "Segment";
  const title = (focus ? focus.title : p2Item.title) || `Untitled ${kind.toLowerCase()}`;
  const length = focus?.length ?? null;
  info.replaceChildren(...[
    el("span", { class: "p2-kind" }, kind),
    length != null ? el("span", { class: "p2-length num" }, formatDuration(Math.round(length))) : null,
    el("span", { class: "p2-title", title }, title),
  ].filter(Boolean));
}

// The item or the edit on show changed
function onP2FocusChanged(info) {
  if (!p2Item || !info) return;
  verticalEntryChanged();
  p2Item.title = info.title;
  renderP2Info();
  renderPlayer2();
}

// ---------- Player 2's edit picker (saved edits) ----------
//
// Top left of Player 2: the edit on show (Original = the item as on Player 1). Its menu lists Original and the team's edits
// of the item (yours first), New edit, Apply to Player 1, Undo last apply, Revert to original, and for the edit on show:
// Make my own copy (someone else's), Rename, Delete.

function renderEditPicker() {
  const t = timeline2;
  const shown = Boolean(p2Item && t?.source);
  $("p2-edit-menu").hidden = !shown;
  if (!shown) return;
  const edit = t.edit;
  const saving = t.edits.some((e) => e.saving || e.dirty);
  const failed = t.edits.some((e) => e.failed);
  $("p2-edit-name").textContent = t.editName(edit);
  $("p2-edit-state").textContent = saving ? "saving…" : failed ? "not saved" : edit?.original ? (t.canMakeEdits() ? "view only" : "") : t.isApplied(edit) ? "applied" : "";
  $("p2-edit-state").classList.toggle("bad", failed && !saving);
  $("p2-edit-pick").title = edit?.original
    ? "The item as it is on Player 1. Trim it or cut a part out to make a new edit."
    : `${t.editName(edit)}${edit?.own ? "" : ` by ${edit?.createdBy ?? "a teammate"} (view only: a change makes your own copy)`}`;
  if ($("p2-edit-menu").open) renderEditMenu();
}

function renderEditMenu() {
  const t = timeline2;
  const current = t.edit;
  const item = (value, label, { icon = null, checked = null, disabled = false, detail = null } = {}) => el("sl-menu-item", {
    value, disabled, ...(checked != null ? { type: "checkbox", checked } : {}),
  }, icon ? el("sl-icon", { slot: "prefix", library: "ph", name: icon }) : null, label, detail ? el("span", { slot: "suffix", class: "edit-detail" }, detail) : null);
  const detailOf = (edit) => [formatDuration(Math.round(t.keptTotal(t.fitted(edit.partCuts)))), t.isApplied(edit) ? "applied" : null].filter(Boolean).join(" · ");
  const entries = [item("original", "Original", { checked: Boolean(current?.original), detail: "as on Player 1" })];
  const index = (edit) => t.edits.indexOf(edit);
  const newest = (a, b) => String(b.updatedAtUtc ?? b.createdAtUtc ?? "").localeCompare(String(a.updatedAtUtc ?? a.createdAtUtc ?? ""));
  const own = t.edits.filter((e) => e.own).sort(newest);
  if (own.length) {
    entries.push(el("sl-divider"), el("sl-menu-label", {}, "Your edits"));
    for (const edit of own) entries.push(item(`edit:${index(edit)}`, t.editName(edit), { checked: edit === current, detail: detailOf(edit) }));
  }
  const byMember = new Map();
  for (const edit of t.edits.filter((e) => !e.own).sort(newest)) {
    const name = edit.createdBy ?? "A teammate";
    if (!byMember.has(name)) byMember.set(name, []);
    byMember.get(name).push(edit);
  }
  for (const [name, edits] of byMember) {
    entries.push(el("sl-divider"), el("sl-menu-label", {}, `${name}'s edits`));
    for (const edit of edits) entries.push(item(`edit:${index(edit)}`, t.editName(edit), { checked: edit === current, detail: detailOf(edit) }));
  }
  const can = t.canMakeEdits();
  entries.push(el("sl-divider"), item("new", "New edit", { icon: "plus", disabled: !can }));
  const last = t.applies[0];
  const applyName = (apply) => {
    const edit = t.edits.find((e) => e.id === apply.editId);
    return edit ? t.editName(edit) : apply.editTitle || "a deleted edit";
  };
  entries.push(el("sl-divider"),
    item("apply", t.applyMakesCopy ? "Apply to a copy on my row" : "Apply to Player 1", { icon: "arrow-fat-line-up", disabled: !t.canApply() }),
    item("undo-apply", "Undo last apply", {
      icon: "arrow-u-up-left", disabled: !t.canRevert(),
      detail: last ? `${applyName(last)}${last.username ? ` · ${last.username}` : ""} · ${ago(last.appliedAtUtc)}` : null,
    }),
    item("revert", "Revert to original", { icon: "clock-counter-clockwise", disabled: !t.canRevert() }));
  if (current && !current.original) {
    entries.push(el("sl-divider"));
    if (!current.own) entries.push(item("copy", "Make my own copy", { icon: "copy", disabled: !can }));
    if (current.own) entries.push(item("rename", "Rename this edit", { icon: "pencil-simple", disabled: Boolean(me?.support) }));
    if (current.canDelete ?? current.own) entries.push(item("delete", "Delete this edit", { icon: "trash", disabled: Boolean(me?.support) }));
  }
  $("p2-edit-items").replaceChildren(...entries);
}

function setUpEditPicker() {
  $("p2-edit-menu").addEventListener("sl-show", (event) => {
    if (event.target !== $("p2-edit-menu")) return;
    renderEditMenu();
    timeline2.loadEdits();
  });
  $("p2-edit-items").addEventListener("sl-select", (event) => {
    const value = event.detail.item.value;
    const t = timeline2;
    if (value === "original") t.pickEdit(null);
    else if (value.startsWith("edit:")) t.pickEdit(t.edits[Number(value.slice(5))] ?? null);
    else if (value === "new") t.startNewEdit();
    else if (value === "apply") t.applyEdit();
    else if (value === "undo-apply") t.revert("previous");
    else if (value === "revert") t.revert("original");
    else if (value === "copy") t.copyEdit();
    else if (value === "rename") t.renameEdit();
    else if (value === "delete") t.deleteEdit();
  });
  $("p2-apply").addEventListener("click", () => timeline2.applyEdit());
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
    onTick: p2Tick,
    onState: () => {
      playAlone(player2, player1);
      renderPlayer2();
    },
    onDetails: (details) => timeline2.setDetails(details),
  });
  timeline2 = new SourceTimeline({
    // Delete / "Unload from Player 2" only clears Player 2; nothing is deleted
    onUnload: () => closePlayer2(),
    // Split lines dragged in the Layout row (source time -> seconds into the clip's file)
    onLayoutParts: (starts) => setVerticalPartStarts(starts.map(sourceToFile)),
    onSeek: p2Seek,
    getPosition: p2Position,
    openItemEditor,
    closeItemEditor: () => closeItemEditor(false),
    onChange: () => {
      renderTools();
      renderP2Info();
      renderPlayer2();
    },
    onClipsChanged: refreshClips,
    onVideosChanged: refreshVideos,
    onEdited: () => {
      if (timeline.vodId && timeline.vodId === timeline2.vodId) timeline.load();
      verticalEntryChanged();
    },
    onFocusChanged: onP2FocusChanged,
    onEditsChanged: () => {
      renderEditPicker();
      renderP2Info();
      renderPlayer2();
      if (activePlayer === "p2") renderTools();
    },
    // An edit was applied (or undone): Player 1 and the cards show the item's new cut-out parts
    onApplied: () => {
      if (timeline.vodId && timeline.vodId === timeline2.vodId) timeline.load();
      refreshClips();
      refreshVideos();
    },
    // The first change to someone else's item made a copy: Player 2 goes on with it
    onFocusMoved: (focus) => {
      if (!p2Item) return;
      Object.assign(p2Item, { type: focus.type, id: focus.id });
      if (verticalOpen() && focus.type === "clip") openVertical(focus.id);
    },
    onFocusGone: (message) => {
      if (p2Item) closePlayer2(message);
    },
  });
  wirePlayerButtons(2, "p2");
  setUpVertical($("player2"), {
    onExit: () => toggleVertical(false),
    // The vertical editor's parts on the clip in Player 2's row (file seconds -> source time)
    onParts: (info) => timeline2.setVerticalParts(info && {
      parts: info.parts.map((part) => ({ ...part, start: fileToSource(part.start), end: fileToSource(part.end), keys: (part.keys ?? []).map(fileToSource) })),
      trim: { start: fileToSource(info.trim.start), end: fileToSource(info.trim.end) },
    }),
  });
  $("p2-vertical").addEventListener("click", () => toggleVertical(true));
  $("p2-show").addEventListener("click", showInPlayer1);
  $("p2-rename").addEventListener("click", () => {
    if (p2Item?.type === "video") {
      timeline2.editVideoTitle();
      return;
    }
    const item = timeline2.focusItem();
    if (item) timeline2.editTitle(item);
  });
  $("p2-delete").addEventListener("click", () => closePlayer2());
  setUpEditPicker();
  renderPlayer2();
}

// Player 2's crosshair: Player 1 opens the item's VOD (if it isn't open already), goes to the item and selects it
async function showInPlayer1() {
  if (!p2Item) return;
  // A video: its first part selected, all its parts in view
  const parts = p2Parts();
  if (!parts.length) return;
  const start = parts[0].vodStart;
  const range = p2Item.type === "video" ? { start: Math.min(...parts.map((p) => p.vodStart)), end: Math.max(...parts.map((p) => p.vodEnd)) } : null;
  const focus = p2Item.type === "video" ? { type: "marker", id: parts[0].id } : { ...timeline2.focus };
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
    await openVod({ vodId: info.vodId, title: info.title, lengthSeconds: info.lengthSeconds, isLive: info.isLive }, { start, paused: true });
  } else {
    player1.pause();
    player1.seek(start);
  }
  timelineTabs.pick("p1");
  timeline.reveal(focus, range);
}

// ---------- vertical (9:16) editor in Player 2 (ui/vertical.js) ----------
//
// A clip in Player 2 can turn the player into the vertical editor: Player 2 takes the whole width and shows the crop
// boxes, the 9:16 preview and the renders; the Player 2 timeline stays below (trims and cut-out parts there change the
// clip's file, which the editor reloads).

function toggleVertical(on = !verticalOpen()) {
  if (!on) {
    if (!verticalOpen()) return;
    closeVertical();
    $("view-editor").classList.remove("vertical-mode");
    return;
  }
  if (p2Item?.kind !== "clip") {
    toast("Load a clip in Player 2 to make a vertical version of it.");
    return;
  }
  player1.pause();
  player2.pause();
  $("view-editor").classList.add("vertical-mode");
  timelineTabs.pick("p2");
  openVertical(p2Item.id);
}

// ---------- tools bar (works on the timeline whose tab is open) ----------

function renderTools() {
  if (DEMO || !timeline || !timeline2) return;
  const t = activeTimeline();
  const on = Boolean(t.vodId);
  const canCreate = on && t.canCreate();
  for (const button of document.querySelectorAll("#tools [data-tool]")) {
    button.disabled = !on;
    button.toggleAttribute("data-active", on && t.tool === button.dataset.tool);
  }
  const snap = document.querySelector('#tools [data-keybind="toggleSnap"]');
  snap.disabled = !on;
  snap.toggleAttribute("data-active", on && t.snap);
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    const kind = button.dataset.create;
    button.disabled = !canCreate;
    button.toggleAttribute("data-active", canCreate && (kind === "video" ? Boolean(t.linking) : t.marking?.kind === kind));
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
    video: () => activeTimeline().startLinking(),
  };
  for (const button of document.querySelectorAll("#tools [data-create]")) {
    button.addEventListener("click", () => create[button.dataset.create]?.());
  }
  renderTools();
}

// Play / pause: with a segment or clip selected (Player 2: always its item) and the player paused, play just it
// Play / pause: with a segment or clip selected and the player paused, play just it. Player 2 plays what its edit keeps
// (from where it is, or from the edit's start once it has ended)
function playPause(which = activePlayer) {
  const { player, timeline: t } = pair(which);
  if (!player.ready) return;
  if (which === "p2") {
    if (player.playing()) {
      player.pause();
      return;
    }
    const kept = t.keptRange();
    let from = p2Position();
    if (from == null || from < kept.start - 0.01 || from >= kept.end - 0.05) from = kept.start;
    const cut = t.removedRanges().find((r) => from >= r.start && from < r.end);
    if (cut) from = cut.end;
    p2StopAt = null;
    player.stopAt = null;
    if (Math.abs(from - (p2Position() ?? -1)) > 0.01) p2Seek(from);
    player.play();
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

// Shift+Space: the last 3 s of the selected item (Player 2: of its edit)
function playLastSeconds(which = activePlayer) {
  const { player, timeline: t } = pair(which);
  if (!player.ready) return;
  if (which === "p2") {
    const kept = t.keptRange();
    if (kept.end <= kept.start) return;
    p2Seek(Math.max(kept.start, kept.end - 3));
    p2StopAt = kept.end;
    player.stopAt = null;
    player.play();
    return;
  }
  const range = t.selectedRange();
  if (!range) return;
  player.playRange(Math.max(range.start, range.end - 3), range.end);
}

// Seeking stays inside what the timeline can show (Player 2: its item)
function skip(which, delta) {
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

const VERTICAL_PASS_KEYS = new Set(["toolPointer", "toolCut", "toolZoom", "toolHand", "zoomIn", "zoomOut", "fit", "escape"]);

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
    // The vertical editor has its own keys; of the player's, only the timeline's tools and zoom still work
    if (handleVerticalKey(event)) return;
    const id = keybindFor("player", event);
    if (!id) return;
    if (verticalOpen() && !VERTICAL_PASS_KEYS.has(id)) return;
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
  setUpExport({ demo: DEMO, otherPlayers: () => [player1, player2] });
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
