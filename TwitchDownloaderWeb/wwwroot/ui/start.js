// The start screen (badrfly 2026-11-19): what the app shows first, over the editor with the lights off and nothing loaded. Its card
// holds the login form (logged out), a link's form (/#join=<token>: team invites, registration and owner links, password resets) or the
// member's teams as cards (channel picture and name, live or last live, when the team and the member last changed its work). Picking a
// team opens the editor on it. The team pill in the editor's top bar brings the screen back to switch teams.

import { $, el, api, me, loadMe, timeZoneOption, formatDate } from "./core.js";
import { mountJoinForm } from "./join-form.js";

let options = { onEnter: async (_teamId) => {}, accountItems: () => [], onAccount: (_value) => {} };
let entered = false;           // the editor is open on a team (the screen came back to switch teams)
let teams = [];
let page = 0;
let clockTimer = 0;
let appInfo = null;

/**
 * @param {{ onEnter: (teamId: number) => Promise<void>, onAccount: (value: string) => void }} handlers  onEnter opens the editor on the
 *   picked team (the team cookie is already set); onAccount runs a profile menu item (account, report, logout)
 */
export function setUpStart(handlers) {
  options = { ...options, ...handlers };
  document.addEventListener("keydown", onKey, true);
}

/** Shows the screen. `entered`: the editor already runs on a team, so Esc or picking that team just closes the screen. */
export async function openStart({ entered: isEntered = false } = {}) {
  entered = isEntered;
  $("start").hidden = false;
  document.body.classList.add("start-open");
  startClock();
  loadAppInfo();
  const token = location.hash.startsWith("#join=") ? location.hash.slice("#join=".length) : null;
  if (token) return showJoin(token);
  if (!me) return showLogin();
  return showTeams();
}

export function closeStart() {
  $("start").hidden = true;
  document.body.classList.remove("start-open");
  clearInterval(clockTimer);
}

export const startIsOpen = () => !$("start").hidden;

// ---------- frame: clock, profile, corner ----------

function startClock() {
  const tick = () => {
    $("start-clock").textContent = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit", ...timeZoneOption() }).format(new Date());
  };
  tick();
  clearInterval(clockTimer);
  clockTimer = setInterval(tick, 5000);
}

async function loadAppInfo() {
  try {
    appInfo ??= await api("GET", "/api/auth/app");
  } catch {
    appInfo = { name: "Clipco", links: {} };
  }
  $("start-brand").textContent = appInfo.name;
  const links = [
    ["x", "x-logo", "X"],
    ["discord", "discord-logo", "Discord"],
  ].filter(([key]) => appInfo.links?.[key]);
  $("start-links").replaceChildren(...links.map(([key, icon, label]) =>
    el("a", { class: "start-link", href: appInfo.links[key], target: "_blank", rel: "noopener", "aria-label": label, title: label },
      el("sl-icon", { library: "ph", name: icon }))));
}

function renderFrame() {
  $("start-user").textContent = me?.username ?? "";
  if (!me) {
    $("start-profile").replaceChildren();
    return;
  }
  // The profile menu: only who's logged in, Account settings, Report a problem and Log out (badrfly 2026-11-19)
  const menu = el("sl-menu", {},
    el("sl-menu-label", {}, me.username),
    el("sl-menu-item", { value: "account" }, el("sl-icon", { slot: "prefix", library: "ph", name: "user-circle" }), "Account settings"),
    el("sl-menu-item", { value: "report" }, el("sl-icon", { slot: "prefix", library: "ph", name: "bug" }), "Report a problem"),
    el("sl-divider"),
    el("sl-menu-item", { value: "logout" }, el("sl-icon", { slot: "prefix", library: "ph", name: "sign-out" }), "Log out"));
  menu.addEventListener("sl-select", (event) => options.onAccount(event.detail.item.value));
  $("start-profile").replaceChildren(el("sl-dropdown", { placement: "bottom-end", distance: "6", hoist: true },
    el("button", { slot: "trigger", type: "button", class: "avatar", "aria-label": "Account menu" }, me.username.slice(0, 1).toUpperCase()),
    menu));
}

// ---------- logged out: the login form ----------

function showLogin() {
  renderFrame();
  const username = el("input", { autocomplete: "username", spellcheck: "false", autocapitalize: "off", id: "start-username" });
  const password = el("input", { type: "password", autocomplete: "current-password", id: "start-password" });
  const button = el("button", { type: "submit" }, "Log in");
  const message = el("p", { class: "message", role: "status" });
  const form = el("form", { class: "stack start-login", novalidate: "" },
    el("label", {}, "Username ", username), el("label", {}, "Password ", password),
    el("div", { class: "row" }, button), message,
    el("p", { class: "muted small-text" }, "New here? Accounts are made from an invite link. Ask your team owner for one. ",
      "Forgot your password? Ask your team owner for help."));
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    button.disabled = true;
    setMessage(message, "Logging in…");
    try {
      await api("POST", "/api/auth/login", { username: username.value, password: password.value });
      await loadMe();
      showTeams();
    } catch (error) {
      setMessage(message, error.message, "error");
      button.disabled = false;
    }
  });
  $("start-card").replaceChildren(el("h2", { class: "start-title display" }, "Log in"), form);
  username.focus();
}

// ---------- a one-time link ----------

async function showJoin(token) {
  renderFrame();
  const box = el("div", { class: "start-join" });
  $("start-card").replaceChildren(box);
  await mountJoinForm(box, token, {
    onDone: async (result) => {
      history.replaceState(null, "", "/"); // drop the token from the address bar and history
      if (result?.staff) { location.href = "/admin.html"; return; }
      try {
        await loadMe();
      } catch {
        // A password reset doesn't log in: log in with the new password
      }
      if (me) showTeams(); else showLogin();
    },
  });
  const heading = box.querySelector(".join-heading");
  heading?.classList.add("start-title", "display");
  const cancel = el("button", { type: "button", class: "link small", onclick: () => {
    history.replaceState(null, "", "/");
    if (me) showTeams(); else showLogin();
  } }, me ? "Not now: back to my teams" : "Not now: log in instead");
  box.append(el("div", { class: "row start-join-cancel" }, cancel));
}

// ---------- logged in: the team cards ----------

async function showTeams() {
  renderFrame();
  $("start-card").replaceChildren(el("h2", { class: "start-title display" }, "Teams"), el("p", { class: "muted start-wait" }, "Loading your teams…"));
  try {
    teams = await api("GET", "/api/auth/teams");
  } catch (error) {
    if (error.status === 401) return;
    $("start-card").replaceChildren(el("h2", { class: "start-title display" }, "Teams"), el("p", { class: "message error" }, error.message));
    return;
  }
  page = Math.max(0, Math.floor(teams.findIndex((t) => t.isCurrent) / perPage()));
  renderTeams();
  for (const team of teams) if (team.channel) loadChannel(team);
}

const perPage = () => Math.max(1, Math.min(4, Math.floor((Math.min(window.innerWidth, 1200) - 220) / 196)));

function renderTeams() {
  if (!teams.length) {
    $("start-card").replaceChildren(el("h2", { class: "start-title display" }, "Teams"),
      el("p", { class: "start-empty" }, "You aren't in a team yet. Open the invite link a team owner sent you, or ask them for one."));
    return;
  }
  const size = perPage();
  const pages = Math.ceil(teams.length / size);
  page = Math.min(Math.max(page, 0), pages - 1);
  const shown = teams.slice(page * size, page * size + size);
  const arrow = (direction) => el("sl-icon-button", {
    library: "ph", name: direction < 0 ? "caret-left" : "caret-right", label: direction < 0 ? "Previous teams" : "Next teams",
    class: "start-arrow", disabled: (direction < 0 ? page === 0 : page >= pages - 1) || null,
    onclick: () => { page += direction; renderTeams(); },
  });
  const row = el("div", { class: "start-teams", role: "list" }, ...shown.map(teamCard));
  $("start-card").replaceChildren(
    el("h2", { class: "start-title display" }, "Teams"),
    el("div", { class: "start-carousel" + (pages > 1 ? "" : " single") }, arrow(-1), row, arrow(1)),
    ...(pages > 1 ? [el("p", { class: "faint start-page num" }, `${page + 1} / ${pages}`)] : []));
  (row.querySelector(".team-card.current") ?? row.querySelector(".team-card"))?.focus({ preventScroll: true });
}

function teamCard(team) {
  const channelName = team.channel?.displayName ?? team.name;
  const avatar = el("span", { class: "team-avatar", "data-team": team.id }, channelName.slice(0, 1).toUpperCase());
  const live = el("span", { class: "team-line team-live", "data-team-live": team.id }, team.channel ? "…" : "No channel");
  const card = el("button", {
    type: "button", role: "listitem", class: "team-card" + (team.isCurrent && entered ? " current" : ""),
    "aria-label": `${channelName}, ${team.name}`, onclick: () => pick(team),
  },
  avatar,
  el("span", { class: "team-channel display" }, channelName),
  el("span", { class: "team-name faint" }, team.name),
  live,
  el("span", { class: "team-line" }, el("span", { class: "faint" }, "Team "), team.teamLastActiveUtc ? ago(team.teamLastActiveUtc) : "no work yet"),
  el("span", { class: "team-line" }, el("span", { class: "faint" }, "You "), team.myLastActiveUtc ? ago(team.myLastActiveUtc) : "not yet"),
  team.hasAccess ? null : el("span", { class: "team-tag" }, "Waiting for access"));
  if (team.teamLastActiveUtc) card.querySelector(".team-line:nth-of-type(2)")?.setAttribute("title", formatDate(team.teamLastActiveUtc));
  const wrap = el("div", { class: "team-card-wrap" }, card);
  if (team.canOpenTeamPage) {
    wrap.append(el("sl-icon-button", { library: "ph", name: "users-three", label: "Team page", class: "team-page-button",
      onclick: async () => { await setTeam(team.id); location.href = "/team.html"; } }));
  }
  return wrap;
}

// The channel's picture and live state come from Twitch, so the cards show first and fill in
async function loadChannel(team) {
  let info;
  try {
    info = await api("GET", `/api/auth/teams/${team.id}/channel`);
  } catch {
    info = null;
  }
  team.card = info;
  const avatar = document.querySelector(`.team-avatar[data-team="${team.id}"]`);
  const live = document.querySelector(`[data-team-live="${team.id}"]`);
  if (avatar && info?.profileImageUrl) {
    const img = el("img", { src: info.profileImageUrl, alt: "", loading: "lazy", referrerpolicy: "no-referrer" });
    img.addEventListener("error", () => img.remove());
    avatar.replaceChildren(img);
  }
  avatar?.classList.toggle("live", info?.live === true);
  if (!live) return;
  if (info?.live) {
    live.replaceChildren(el("span", { class: "live-dot" }), "Live now", ...(info.liveSinceUtc ? [el("span", { class: "faint" }, ` · ${since(info.liveSinceUtc)}`)] : []));
    live.classList.add("is-live");
  } else if (info?.lastLiveUtc) {
    live.replaceChildren(el("span", { class: "faint" }, "Last live "), ago(info.lastLiveUtc));
    live.title = formatDate(info.lastLiveUtc);
  } else {
    live.replaceChildren(el("span", { class: "faint" }, info ? "No streams kept" : "Couldn't reach Twitch"));
  }
}

async function setTeam(teamId) {
  if (teamId !== me.currentTeamId) await api("POST", "/api/auth/team", { teamId });
}

async function pick(team) {
  if (entered && team.id === me.currentTeamId) { closeStart(); return; }
  try {
    await setTeam(team.id);
  } catch (error) {
    $("start-card").append(el("p", { class: "message error" }, error.message));
    return;
  }
  if (entered) {
    // The editor already runs on another team: start it again on this one, without showing this screen first
    try { sessionStorage.setItem("clipco.start.enter", String(team.id)); } catch { /* storage blocked: the screen shows again */ }
    location.reload();
    return;
  }
  await loadMe();
  closeStart();
  await options.onEnter(team.id);
}

function onKey(event) {
  if ($("start").hidden) return;
  const typing = event.target.closest?.("input, textarea");
  if (event.key === "Escape" && entered && me) { event.preventDefault(); event.stopPropagation(); closeStart(); return; }
  if (!typing && teams.length && (event.key === "ArrowLeft" || event.key === "ArrowRight") && event.target.closest?.(".team-card")) {
    const cards = [...document.querySelectorAll(".team-card")];
    const index = cards.indexOf(event.target.closest(".team-card")) + (event.key === "ArrowRight" ? 1 : -1);
    if (index >= 0 && index < cards.length) cards[index].focus();
    else if (index < 0 && page > 0) { page--; renderTeams(); }
    else if (index >= cards.length && (page + 1) * perPage() < teams.length) { page++; renderTeams(); }
    event.preventDefault();
  }
  // The editor's shortcuts stay off while the screen is up
  if (!event.defaultPrevented && !typing) event.stopPropagation();
}

// ---------- small helpers ----------

function setMessage(element, text, kind) {
  element.textContent = text || "";
  element.className = "message" + (kind ? ` ${kind}` : "");
}

/** "just now", "5 min ago", "3 h ago", "2 days ago", then the date */
export function ago(utcString) {
  const seconds = (Date.now() - new Date(utcString).getTime()) / 1000;
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  if (seconds < 86400 * 14) { const days = Math.floor(seconds / 86400); return days === 1 ? "yesterday" : `${days} days ago`; }
  return formatDate(utcString, false);
}

function since(utcString) {
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(utcString).getTime()) / 60000));
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

window.addEventListener("resize", () => { if (!$("start").hidden && teams.length) renderTeams(); });
