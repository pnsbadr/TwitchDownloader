"use strict";

// The Admin page for app staff (the Owner and moderators). The server checks every permission; the page only hides what won't work.

const TAB_STORAGE_KEY = "clipAdmin.tab";
const KINDS = [["added", "Added"], ["edited", "Edited"], ["deleted", "Deleted"], ["downloaded", "Downloads"], ["generated", "Files made"], ["vertical", "Vertical"]];

let staffMe = null;
let teams = [];
let logOldestId = null;

const TABS = [
  { id: "teams", label: "Teams", panel: "teams-panel", allowed: () => canAny("ViewTeamsAndUsers", "ManageTeamLinks", "DisableTeams", "ViewAsSupport"), load: loadTeams },
  { id: "users", label: "Users", panel: "users-panel", allowed: () => canAny("ViewTeamsAndUsers", "ResetUserPasswords", "DisableUsers"), load: loadUsers },
  { id: "links", label: "Links", panel: "links-panel", allowed: () => canAny("ManageTeamLinks", "ResetUserPasswords"), load: loadLinks },
  { id: "staff", label: "Moderators", panel: "staff-panel", allowed: () => staffMe.isOwner, load: loadStaff },
  { id: "twitch", label: "Twitch", panel: "twitch-panel", allowed: () => staffMe.isOwner, load: loadTwitch },
  { id: "reports", label: "Reports", panel: "reports-panel", allowed: () => can("ViewErrorReports"), load: () => loadReports(true) },
  { id: "log", label: "App log", panel: "log-panel", allowed: () => can("ViewAuditLog"), load: () => loadLog(true) },
];

function can(permission) {
  return Boolean(staffMe?.permissions.includes(permission));
}

function canAny(...permissions) {
  return permissions.some(can);
}

function showError(error) {
  setMessage($("admin-message"), error.message, "error");
}

function plural(count, one, many = `${one}s`) {
  return `${count} ${count === 1 ? one : many}`;
}

function formatBytes(bytes) {
  if (!bytes) return "0 MB";
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
}

function show(ids) {
  for (const id of ["setup-panel", "login-panel"]) $(id).hidden = !ids.includes(id);
}

async function start() {
  try {
    staffMe = await api("GET", "/api/admin/me");
  } catch (error) {
    if (error.status !== 401) { showError(error); return; }
    const setup = await api("GET", "/api/admin/setup");
    if (setup.needed) {
      show(["setup-panel"]);
      $("setup-remote").hidden = setup.allowedHere;
      $("setup-form").hidden = !setup.allowedHere;
    } else {
      show(["login-panel"]);
      $("staff-username").focus();
    }
    return;
  }

  show([]);
  $("staff-name").textContent = staffMe.username + (staffMe.isOwner ? " (Owner)" : "");
  $("staff-bar").hidden = false;

  const tabs = TABS.filter((tab) => tab.allowed());
  $("no-permissions").hidden = tabs.length > 0;
  if (!tabs.length) return;

  $("registration-block").hidden = !can("ManageTeamLinks");
  $("registration-channels-label").hidden = !can("ManageTeamChannels");
  $("tabs").replaceChildren(...tabs.map((tab) => el("button", { type: "button", class: "seg", "data-tab": tab.id, onclick: () => openTab(tab) }, tab.label)));
  $("tabs").hidden = false;

  let saved = null;
  try { saved = localStorage.getItem(TAB_STORAGE_KEY); } catch { /* storage blocked */ }
  openTab(tabs.find((x) => x.id === saved) ?? tabs[0]);
}

function openTab(tab) {
  for (const t of TABS) $(t.panel).hidden = t !== tab;
  for (const button of $("tabs").querySelectorAll(".seg")) button.setAttribute("aria-pressed", String(button.dataset.tab === tab.id));
  try { localStorage.setItem(TAB_STORAGE_KEY, tab.id); } catch { /* storage blocked */ }
  setMessage($("admin-message"), "");
  Promise.resolve(tab.load()).catch(showError);
}

function activityTable(headLabel, rows) {
  return el("div", { class: "table-wrap" }, el("table", { class: "activity" },
    el("thead", {}, el("tr", {}, el("th", {}, headLabel), ...KINDS.map(([, label]) => el("th", {}, label)),
      el("th", { title: "Entries they added, all time" }, "Entries"), el("th", { title: "Files of their entries on disk now" }, "Disk"))),
    el("tbody", {}, ...rows.map((row) => el("tr", {},
      el("td", {}, ...row.head),
      ...KINDS.map(([key]) => el("td", { class: "num" }, row.counts[key] ?? 0)),
      el("td", { class: "num" }, row.entriesAdded),
      el("td", { class: "num" }, formatBytes(row.diskBytes)))))));
}

function periodSelect(onChange) {
  const select = el("select", { "aria-label": "Period" },
    el("option", { value: "1" }, "Last 24 hours"), el("option", { value: "7" }, "Last 7 days"),
    el("option", { value: "30" }, "Last 30 days"), el("option", { value: "90" }, "Last 90 days"));
  select.value = "7";
  select.addEventListener("change", () => onChange(select.value));
  return select;
}

// ---------- teams ----------

async function loadTeams() {
  teams = await api("GET", "/api/admin/teams");
  $("teams").replaceChildren(...teams.map(teamRow));
  if (!teams.length) $("teams").append(el("li", { class: "muted" }, "No teams yet. Make a team registration link under Links."));
}

function teamRow(team) {
  const details = el("div", { class: "admin-details", hidden: "" });
  const actions = [];
  if (can("ViewTeamsAndUsers")) {
    actions.push(el("button", { type: "button", class: "secondary small", onclick: () => toggleTeamDetails(team, details) }, "Members and activity"));
  }
  if (can("ManageTeamChannels")) {
    actions.push(el("button", { type: "button", class: "secondary small", onclick: () => toggleTeamChannels(team, details) }, "Channels"));
  }
  if (can("ViewAsSupport")) {
    actions.push(el("button", { type: "button", class: "secondary small", title: "Open this team's library read-only. The visit is logged.",
      onclick: () => viewAsSupport(team) }, "View as support"));
  }
  if (can("ManageTeamLinks")) {
    actions.push(el("button", { type: "button", class: "link small", onclick: () => makeOwnerLink(team) },
      team.ownerUsername ? "Owner link (hand over)" : "Owner link"));
  }
  // Features still being tried out: only the Owner turns them on, per team
  if (staffMe.isOwner) {
    actions.push(el("button", { type: "button", class: "link small", onclick: () => setTeamFeature(team, { combineSegmentDownloads: !team.combineSegmentDownloads }) },
      team.combineSegmentDownloads ? "Turn off joined segments" : "Allow joined segments"));    actions.push(el("button", { type: "button", class: "link small",
      title: "Clips of 3 minutes or less are downloaded and kept as files. Off: they are references, like segments (files are made when needed).",
      onclick: () => setTeamFeature(team, { storeClips: !team.storeClips }) },
      team.storeClips ? "Stop storing clips" : "Store clips"));
  }
  if (can("DisableTeams")) {
    actions.push(el("button", { type: "button", class: team.disabledAtUtc ? "link small" : "link danger small", onclick: () => setTeamDisabled(team, !team.disabledAtUtc) },
      team.disabledAtUtc ? "Enable" : "Disable"));
  }

  return el("li", { class: "list-row admin-row" },
    el("div", { class: "grow" },
      el("strong", {}, team.name),
      team.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
      team.storeClips ? el("span", { class: "badge ok", title: "Clips of 3 minutes or less are downloaded and kept as files" }, "Stores clips") : null,
      audioBadge(team),
      team.combineSegmentDownloads
        ? el("span", { class: "badge ok", title: "Members can download picked segments joined into one file" }, "Joined segments")
        : null,
      el("div", { class: "muted small-text" },
        channelSummary(team)),
      el("div", { class: "muted small-text" },
        `${team.ownerUsername ? `Owner ${team.ownerUsername}` : "No owner yet"} · ${plural(team.memberCount, "member")} · `
        + `${plural(team.entryCount, "entry", "entries")} · ${formatBytes(team.diskBytes)} on disk · `
        + `${team.lastActiveAtUtc ? `last active ${formatWhen(team.lastActiveAtUtc)}` : "nobody active yet"} · created ${formatWhen(team.createdAtUtc)}`)),
    el("div", { class: "row admin-actions" }, ...actions),
    details);
}

async function toggleTeamDetails(team, details) {
  if (!details.hidden && details.dataset.view === "members") { details.hidden = true; return; }
  details.hidden = false;
  details.dataset.view = "members";
  const render = async (days) => {
    try {
      const detail = await api("GET", `/api/admin/teams/${team.id}?days=${days}`);
      table.replaceChildren(activityTable("Member", detail.members.map((m) => ({
        head: [el("strong", {}, m.username),
          m.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
          el("div", { class: "muted small-text" },
            `${m.isOwner ? "Owner" : m.roleName ?? "No role"} · ${m.lastActiveAtUtc ? `active ${formatWhen(m.lastActiveAtUtc)}` : "never active"}`
            + (m.otherTeams ? ` · in ${plural(m.otherTeams, "other team")}` : ""))],
        counts: m.counts, entriesAdded: m.entriesAdded, diskBytes: m.diskBytes,
      }))));
      if (!detail.members.length) table.replaceChildren(el("p", { class: "muted" }, "No members yet."));
    } catch (error) {
      showError(error);
    }
  };
  const table = el("div");
  details.replaceChildren(el("div", { class: "row" }, el("label", { class: "inline-label" }, "Period", periodSelect(render))), table);
  render("7");
}

function audioBadge(team) {
  const channels = team.channels.filter((c) => !c.accessEndedAtUtc);
  const auto = channels.filter((c) => c.autoSaveAudio).length;
  const manual = channels.filter((c) => c.saveAudio || c.autoSaveAudio).length;
  if (!manual) return null;
  return el("span", { class: "badge ok", title: `${auto} saved automatically, Save audio now on ${manual}` },
    auto ? `Auto audio: ${plural(auto, "channel")}` : `Save audio: ${plural(manual, "channel")}`);
}

function channelSummary(team) {
  const active = team.channels.filter((c) => !c.accessEndedAtUtc).map((c) => c.displayName);
  const ended = team.channels.filter((c) => c.accessEndedAtUtc).map((c) => c.displayName);
  if (!active.length && !ended.length) return "No channels yet: the team sees no VODs";
  return [active.length ? `Channels: ${active.join(", ")}` : "No channels assigned", ended.length ? `existing work only: ${ended.join(", ")}` : null]
    .filter(Boolean).join(" · ");
}

// The Twitch channels a team works on: its members only see these channels' VODs
function toggleTeamChannels(team, details) {
  if (!details.hidden && details.dataset.view === "channels") { details.hidden = true; return; }
  details.hidden = false;
  details.dataset.view = "channels";
  const input = el("input", { maxlength: "100", placeholder: "Twitch name or link" });
  const add = el("form", { class: "row", novalidate: "" },
    el("label", { class: "grow" }, "Add a channel", input),
    el("button", { type: "submit" }, "Add"));
  add.addEventListener("submit", async (event) => {
    event.preventDefault();
    try {
      await api("POST", `/api/admin/teams/${team.id}/channels`, { login: input.value });
      await refreshTeam(team.id, details);
    } catch (error) {
      showError(error);
    }
  });
  const run = async (method, url, body) => {
    try {
      await api(method, url, body);
      await refreshTeam(team.id, details);
    } catch (error) {
      showError(error);
    }
  };
  const remove = (channel, mode, label, title, danger) => el("button", { type: "button", class: danger ? "link danger small" : "link small", title,
    onclick: () => {
      if (mode === "revoke" && !confirm(`Revoke ${channel.displayName} from ${team.name} now? The team loses every VOD of this channel, and its clips, `
        + "videos, segments and files from them are hidden. Nothing is deleted: assigning the channel again brings it all back.")) return;
      run("DELETE", `/api/admin/teams/${team.id}/channels/${channel.id}?mode=${mode}`);
    } }, label);
  const list = el("ul", { class: "plain-list" }, ...team.channels.map((channel) => el("li", { class: "list-row" },
    el("div", { class: "grow" }, el("strong", {}, channel.displayName),
      channel.accessEndedAtUtc ? el("span", { class: "badge", title: "No new VODs; VODs the team already worked on stay open" }, "Existing work only") : null,
      el("span", { class: "muted small-text" }, ` ${channel.login} · added ${formatWhen(channel.addedAtUtc)}`
        + (channel.accessEndedAtUtc ? ` · new VODs closed ${formatWhen(channel.accessEndedAtUtc)}` : ""))),
    el("div", { class: "row admin-actions" },
      ...audioSwitches(team, channel, run),
      channel.accessEndedAtUtc
        ? el("button", { type: "button", class: "link small", onclick: () => run("POST", `/api/admin/teams/${team.id}/channels`, { login: channel.login }) },
          "Assign again")
        : remove(channel, "future", "Stop new VODs", "The team keeps the VODs it already has clips or markers on, and loses the rest.", false),
      remove(channel, "revoke", "Revoke now", "The team loses every VOD of this channel at once, and its work on them is hidden until you assign it again.", true)))));
  if (!team.channels.length) list.append(el("li", { class: "muted" }, "No channels yet."));
  for (const channel of team.revokedChannels || []) {
    list.append(el("li", { class: "list-row" },
      el("div", { class: "grow" }, el("strong", {}, channel.displayName),
        el("span", { class: "badge", title: "The team's work on this channel's VODs is hidden, not deleted" }, "Revoked"),
        el("span", { class: "muted small-text" }, ` ${channel.login} · revoked ${formatWhen(channel.revokedAtUtc)}`
          + ` · work on ${channel.hiddenVods} VOD${channel.hiddenVods === 1 ? "" : "s"} hidden`)),
      el("div", { class: "row admin-actions" },
        el("button", { type: "button", class: "link small", title: "Opens its VODs and brings the hidden work back",
          onclick: () => run("POST", `/api/admin/teams/${team.id}/channels`, { login: channel.login }) }, "Assign again"))));
  }
  details.replaceChildren(
    el("p", { class: "muted small-text" }, "Members only see and work on these channels' VODs. \"Stop new VODs\" keeps the VODs the team "
      + "already worked on; \"Revoke now\" closes every VOD of the channel and hides the team's work on them (kept, and back when you assign "
      + "the channel again)."
      + (staffMe.isOwner ? " Audio saving is yours to decide per channel: \"Save audio now\" allows the button, \"Save automatically\" saves every stream." : "")),
    list, add);
  input.focus();
}

// Audio saving per channel: only the app Owner decides (badrfly 2026-11-18)
function audioSwitches(team, channel, run) {
  if (!staffMe.isOwner || channel.accessEndedAtUtc) return [];
  const box = (label, title, checked, body) => {
    const input = el("input", { type: "checkbox" });
    input.checked = checked;
    input.addEventListener("change", () => run("PUT", `/api/admin/teams/${team.id}/channels/${channel.id}/audio`, body(input.checked)));
    return el("label", { class: "check-label small-text", title }, input, ` ${label}`);
  };
  return [
    box("Save audio now", "Members with the Save audio now permission can save a live or recent VOD's audio by hand.",
      channel.saveAudio, (on) => ({ saveAudio: on })),
    box("Save automatically", "Every stream's audio is saved while it's live (about 1 GB per channel per day until the muted parts are picked out).",
      channel.autoSaveAudio, (on) => ({ autoSaveAudio: on })),
  ];
}

async function refreshTeam(teamId, details) {
  teams = await api("GET", "/api/admin/teams");
  const team = teams.find((t) => t.id === teamId);
  const row = details.closest("li");
  const fresh = teamRow(team);
  row.replaceWith(fresh);
  const freshDetails = fresh.querySelector(".admin-details");
  toggleTeamChannels(team, freshDetails);
}

async function viewAsSupport(team) {
  try {
    await api("POST", "/api/admin/support", { teamId: team.id });
    location.href = "/";
  } catch (error) {
    showError(error);
  }
}

async function makeOwnerLink(team) {
  try {
    const link = await api("POST", "/api/admin/links", { kind: "ownership", teamId: team.id, note: `Owner of ${team.name}` });
    const box = linkBox(link.url);
    box.querySelector("p").textContent = `Whoever opens this becomes the owner of ${team.name}. Shown only now; it works once and expires in 7 days.`;
    showLinkIn(box, $("teams").parentElement);
  } catch (error) {
    showError(error);
  }
}

function showLinkIn(box, panel) {
  panel.querySelector(".link-box")?.remove();
  panel.querySelector("h2").after(box);
  box.scrollIntoView({ block: "nearest" });
}

async function setTeamFeature(team, features) {
  try {
    await api("PUT", `/api/admin/teams/${team.id}/features`, features);
    loadTeams();
  } catch (error) {
    showError(error);
  }
}

async function setTeamDisabled(team, disabled) {
  if (disabled && !confirm(`Disable ${team.name}? Its members lose access to it (their accounts and other teams keep working). You can enable it again.`)) return;
  try {
    await api("PUT", `/api/admin/teams/${team.id}/disabled`, { disabled });
    loadTeams();
  } catch (error) {
    showError(error);
  }
}

// ---------- users ----------

async function loadUsers() {
  const search = $("user-search").value.trim();
  const users = await api("GET", `/api/admin/users${search ? `?search=${encodeURIComponent(search)}` : ""}`);
  $("users").replaceChildren(...users.map(userRow));
  if (!users.length) $("users").append(el("li", { class: "muted" }, search ? "Nobody matches." : "No users yet."));
}

function userRow(user) {
  const details = el("div", { class: "admin-details", hidden: "" });
  const actions = [];
  if (can("ViewTeamsAndUsers")) {
    actions.push(el("button", { type: "button", class: "secondary small", onclick: () => toggleUserDetails(user, details) }, "Activity by team"));
  }
  if (can("ResetUserPasswords")) {
    actions.push(el("button", { type: "button", class: "link small", onclick: () => makeUserResetLink(user) }, "Password reset link"));
  }
  if (can("DisableUsers")) {
    actions.push(el("button", { type: "button", class: user.disabledAtUtc ? "link small" : "link danger small", onclick: () => setUserDisabled(user, !user.disabledAtUtc) },
      user.disabledAtUtc ? "Enable" : "Disable"));
  }

  const teamsText = user.teams.length
    ? user.teams.map((t) => `${t.teamName} (${t.isOwner ? "owner" : t.roleName ?? "no role"}${t.teamDisabled ? ", team disabled" : ""})`).join(", ")
    : "No team";
  return el("li", { class: "list-row admin-row" },
    el("div", { class: "grow" },
      el("strong", {}, user.username),
      user.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
      user.lockedUntilUtc ? el("span", { class: "badge ref" }, "Locked out") : null,
      el("div", { class: "small-text" }, teamsText),
      el("div", { class: "muted small-text" },
        `${user.lastActiveAtUtc ? `Last active ${formatWhen(user.lastActiveAtUtc)}` : "Never active"} · ${plural(user.recentActions, "action")} in 7 days · `
        + `${plural(user.entriesAdded, "entry", "entries")} · ${formatBytes(user.diskBytes)} on disk · joined ${formatWhen(user.createdAtUtc)}`)),
    el("div", { class: "row admin-actions" }, ...actions),
    details);
}

async function toggleUserDetails(user, details) {
  if (!details.hidden) { details.hidden = true; return; }
  details.hidden = false;
  const table = el("div");
  const render = async (days) => {
    try {
      const detail = await api("GET", `/api/admin/users/${user.id}?days=${days}`);
      table.replaceChildren(activityTable("Team", detail.activity.map((a) => ({
        head: [el("strong", {}, a.teamName)], counts: a.counts, entriesAdded: a.entriesAdded, diskBytes: a.diskBytes,
      }))));
      if (!detail.activity.length) table.replaceChildren(el("p", { class: "muted" }, "Not in any team yet."));
    } catch (error) {
      showError(error);
    }
  };
  details.replaceChildren(el("div", { class: "row" }, el("label", { class: "inline-label" }, "Period", periodSelect(render))), table);
  render("7");
}

async function makeUserResetLink(user) {
  try {
    const link = await api("POST", `/api/admin/users/${user.id}/password-reset`, {});
    const box = linkBox(link.url);
    box.querySelector("p").textContent = `Send this to ${user.username} only: whoever opens it sets their password. Works once, expires in 24 hours.`;
    $("user-link-new").replaceChildren(box);
    box.scrollIntoView({ block: "nearest" });
  } catch (error) {
    showError(error);
  }
}

async function setUserDisabled(user, disabled) {
  if (disabled && !confirm(`Disable ${user.username}? They're signed out everywhere and can't log in until you enable them again.`)) return;
  try {
    await api("PUT", `/api/admin/users/${user.id}/disabled`, { disabled });
    loadUsers();
  } catch (error) {
    showError(error);
  }
}

$("user-search-form").addEventListener("submit", (event) => {
  event.preventDefault();
  loadUsers().catch(showError);
});

// ---------- links ----------

const LINK_KINDS = {
  TeamRegistration: () => "Team registration",
  TeamOwnership: (link) => `Owner of ${link.teamName ?? "a deleted team"}`,
  PasswordReset: () => "Password reset",
  StaffInvite: () => "Moderator invite",
  StaffPasswordReset: () => "Moderator password reset",
};

async function loadLinks() {
  const links = await api("GET", "/api/admin/links");
  $("links").replaceChildren(...links.map((link) => {
    const state = linkState(link);
    const what = (LINK_KINDS[link.kind] ?? (() => link.kind))(link);
    return el("li", { class: "list-row" },
      el("div", { class: "grow" },
        el("strong", {}, link.note || what),
        el("div", { class: "muted small-text" }, `${what} · made ${formatWhen(link.createdAtUtc)} · ${state.text}`
          + (link.channels?.length ? ` · channels: ${link.channels.join(", ")}` : ""))),
      state.open ? el("button", { type: "button", class: "secondary small", onclick: () => revoke(link) }, "Revoke") : null);
  }));
  if (!links.length) $("links").append(el("li", { class: "muted" }, "No links in the last 30 days."));
}

async function revoke(link) {
  try {
    await api("DELETE", `/api/admin/links/${link.id}`);
    loadLinks();
  } catch (error) {
    showError(error);
  }
}

$("registration-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const channels = $("registration-channels").value.split(/[,\s]+/).filter(Boolean);
    const link = await api("POST", "/api/admin/links", { kind: "registration", note: $("registration-note").value, channels });
    $("registration-note").value = "";
    $("registration-channels").value = "";
    $("link-new").replaceChildren(linkBox(link.url));
    loadLinks();
  } catch (error) {
    showError(error);
  }
});

// ---------- moderators (Owner only) ----------

async function loadStaff() {
  const staff = await api("GET", "/api/admin/staff");
  $("staff").replaceChildren(...staff.map(staffRow));
}

function staffRow(account) {
  const meta = el("div", { class: "muted small-text" },
    `Since ${formatWhen(account.createdAtUtc)} · ${account.lastLoginAtUtc ? `last login ${formatWhen(account.lastLoginAtUtc)}` : "never logged in"}`);
  if (account.isOwner) {
    return el("li", { class: "list-row" }, el("div", { class: "grow" }, el("strong", {}, account.username), el("span", { class: "badge" }, "Owner"), meta));
  }

  const boxes = staffMe.permissionCatalog.map((p) => {
    const box = el("input", { type: "checkbox", value: p.key });
    box.checked = account.permissions.includes(p.key);
    return el("label", { class: "check-label", title: p.description }, box, el("span", {}, p.label, el("span", { class: "muted small-text perm-desc" }, p.description)));
  });
  const save = el("button", { type: "button", class: "small", onclick: async () => {
    const permissions = boxes.map((label) => label.querySelector("input")).filter((x) => x.checked).map((x) => x.value);
    try {
      await api("PUT", `/api/admin/staff/${account.id}/permissions`, { permissions });
      setMessage($("admin-message"), `${account.username}'s permissions are saved.`, "ok");
      loadStaff();
    } catch (error) {
      showError(error);
    }
  } }, "Save permissions");

  return el("li", { class: "list-row admin-row" },
    el("div", { class: "grow" },
      el("strong", {}, account.username),
      account.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
      account.permissions.length ? null : el("span", { class: "badge ref" }, "No permissions"),
      meta),
    el("div", { class: "row admin-actions" },
      el("button", { type: "button", class: "link small", onclick: () => makeStaffResetLink(account) }, "Password reset link"),
      el("button", { type: "button", class: account.disabledAtUtc ? "link small" : "link danger small", onclick: () => setStaffDisabled(account, !account.disabledAtUtc) },
        account.disabledAtUtc ? "Enable" : "Disable")),
    el("div", { class: "admin-details" }, el("fieldset", { class: "staff-perms" }, el("legend", {}, "Can"), ...boxes), el("div", { class: "row" }, save)));
}

async function makeStaffResetLink(account) {
  try {
    const link = await api("POST", `/api/admin/staff/${account.id}/password-reset`, {});
    const box = linkBox(link.url);
    box.querySelector("p").textContent = `Send this to ${account.username} only. Works once, expires in 24 hours.`;
    $("staff-link-new").replaceChildren(box);
    box.scrollIntoView({ block: "nearest" });
  } catch (error) {
    showError(error);
  }
}

async function setStaffDisabled(account, disabled) {
  if (disabled && !confirm(`Disable ${account.username}? They're signed out and can't log in until you enable them again.`)) return;
  try {
    await api("PUT", `/api/admin/staff/${account.id}/disabled`, { disabled });
    loadStaff();
  } catch (error) {
    showError(error);
  }
}

$("staff-invite-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const link = await api("POST", "/api/admin/staff/invites", { note: $("staff-invite-note").value });
    $("staff-invite-note").value = "";
    const box = linkBox(link.url);
    box.querySelector("p").textContent = "Shown only now. Whoever opens it makes a moderator account with no permissions; it works once and expires in 7 days.";
    $("staff-link-new").replaceChildren(box);
    loadStaff();
  } catch (error) {
    showError(error);
  }
});

// ---------- app log ----------

async function loadLog(reset) {
  if (reset) {
    logOldestId = null;
    $("log").replaceChildren();
    if ($("log-team").options.length === 1 && canAny("ViewTeamsAndUsers", "ManageTeamLinks", "DisableTeams", "ViewAsSupport")) {
      if (!teams.length) teams = await api("GET", "/api/admin/teams");
      $("log-team").append(...teams.map((t) => el("option", { value: t.id }, t.name)));
    }
  }
  const params = new URLSearchParams({ limit: "50" });
  if ($("log-team").value) params.set("teamId", $("log-team").value);
  if (logOldestId) params.set("before", logOldestId);
  const events = await api("GET", `/api/admin/audit?${params}`);
  if (events.length) logOldestId = events[events.length - 1].id;
  $("log-more").hidden = events.length < 50;
  $("log").append(...events.map((e) => el("li", { class: "log-row" },
    el("span", { class: "muted small-text log-time" }, formatWhen(e.timestampUtc)),
    el("span", {},
      el("strong", {}, e.staffUsername ? `${e.staffUsername} (staff)` : e.username ?? "App"), " ",
      el("code", { class: "small-text" }, e.action),
      e.teamName ? el("span", { class: "muted small-text" }, ` · ${e.teamName}`) : null,
      e.details ? el("span", { class: "muted small-text" }, ` · ${e.details}`) : null))));
  if (!$("log").children.length) $("log").append(el("li", { class: "muted" }, "Nothing yet."));
}

$("log-team").addEventListener("change", () => loadLog(true).catch(showError));
$("log-more").addEventListener("click", () => loadLog(false).catch(showError));

// ---------- reports (bugs, errors, crashes) ----------

const REPORT_KINDS = { ServerError: "Server error", JobError: "Job failed", PageError: "Page crash", BugReport: "Bug report" };
const REPORT_FACETS = [
  // [query key, facets field, label, all label]
  ["kind", "kinds", "Kind", "All kinds"],
  ["userId", "users", "Person", "Everyone"],
  ["teamId", "teams", "Team", "All teams"],
  ["area", "areas", "Where", "Anywhere"],
  ["errorType", "errorTypes", "Error", "Any error"],
  ["browser", "browsers", "Browser", "Any browser"],
  ["os", "systems", "System", "Any system"],
  ["appVersion", "appVersions", "Version", "Any version"],
];
const LIST_SORTS = [
  ["time:desc", "Newest first"], ["time:asc", "Oldest first"], ["user:asc", "Person A–Z"], ["team:asc", "Team A–Z"],
  ["kind:asc", "Kind"], ["area:asc", "Where"], ["type:asc", "Error type"], ["browser:asc", "Browser"], ["os:asc", "System"],
  ["version:desc", "Version"],
];
const GROUP_SORTS = [["count:desc", "Most often"], ["people:desc", "Most people"], ["last:desc", "Latest"], ["first:asc", "Seen first"]];
const REPORT_PAGE = 100;
const PAGE_NAMES = { "/": "Editor", "/index.html": "Editor", "/team.html": "Team page", "/account.html": "Account page",
  "/login.html": "Login page", "/join.html": "Link page", "/admin.html": "Admin page" };

// "Editor (/)" for pages; requests ("GET /api/…") and jobs as they are
function whereLabel(area) {
  return PAGE_NAMES[area] ? `${PAGE_NAMES[area]} (${area})` : area;
}
const reportFilters = {};   // query key → value picked in a select (or by clicking a name)
let reportOffset = 0;
let reportsWired = false;
let reportSearchTimer = null;

function reportQuery({ paging = false } = {}) {
  const params = new URLSearchParams();
  const days = $("rep-period").value;
  if (days) params.set("from", new Date(Date.now() - Number(days) * 86400000).toISOString());
  for (const [key, value] of Object.entries(reportFilters)) if (value !== "" && value != null) params.set(key, value);
  const search = $("rep-search").value.trim();
  if (search) params.set("search", search);
  const [sort, order] = $("rep-sort").value.split(":");
  params.set("sort", sort);
  params.set("order", order);
  if (paging) {
    params.set("offset", String(reportOffset));
    params.set("limit", String(REPORT_PAGE));
  }
  return params;
}

function fillSorts() {
  const sorts = $("rep-view").value === "groups" ? GROUP_SORTS : LIST_SORTS;
  $("rep-sort").replaceChildren(...sorts.map(([value, label]) => el("option", { value }, label)));
}

function wireReports() {
  if (reportsWired) return;
  reportsWired = true;
  fillSorts();
  const reload = () => loadReports(true).catch(showError);
  $("rep-period").addEventListener("change", reload);
  $("rep-view").addEventListener("change", () => { fillSorts(); reload(); });
  $("rep-sort").addEventListener("change", reload);
  $("rep-search").addEventListener("input", () => { clearTimeout(reportSearchTimer); reportSearchTimer = setTimeout(reload, 350); });
  $("rep-more").addEventListener("click", () => loadReports(false).catch(showError));
  $("rep-clear").addEventListener("click", () => {
    for (const key of Object.keys(reportFilters)) delete reportFilters[key];
    $("rep-search").value = "";
    reload();
  });
  $("rep-csv").addEventListener("click", () => downloadReports("csv"));
  $("rep-json").addEventListener("click", () => downloadReports("json"));
  $("rep-delete").addEventListener("click", deleteShownReports);
}

function setReportFilter(key, value) {
  reportFilters[key] = value == null ? "" : String(value);
  if ($("rep-view").value === "groups" && key === "fingerprint") {
    $("rep-view").value = "list";
    fillSorts();
  }
  loadReports(true).catch(showError);
}

async function loadReports(reset) {
  wireReports();
  if (reset) {
    reportOffset = 0;
    $("reports").replaceChildren();
  }
  const grouped = $("rep-view").value === "groups";
  // Facets (what there is to pick from) come with the list; grouped view asks for one item just to get them
  const listParams = reportQuery({ paging: true });
  if (grouped) { listParams.set("limit", "1"); listParams.set("offset", "0"); listParams.set("sort", "time"); }
  const list = await api("GET", `/api/admin/reports?${listParams}`);
  renderFacets(list.facets);
  const filtered = Object.values(reportFilters).some((v) => v !== "" && v != null) || $("rep-search").value.trim();
  $("rep-clear").hidden = !filtered;
  $("rep-delete").disabled = list.total === 0;
  $("rep-csv").disabled = $("rep-json").disabled = list.total === 0;

  if (grouped) {
    const groups = await api("GET", `/api/admin/reports/groups?${reportQuery()}`);
    $("rep-summary").textContent = `${plural(list.total, "report")} · ${plural(groups.length, "different problem")}`;
    $("reports").replaceChildren(...groups.map(groupRow));
    $("rep-more").hidden = true;
  } else {
    reportOffset += list.items.length;
    $("rep-summary").textContent = plural(list.total, "report") + (list.total > reportOffset ? ` · showing ${reportOffset}` : "");
    $("reports").append(...list.items.map(reportRow));
    $("rep-more").hidden = reportOffset >= list.total;
  }
  if (!$("reports").children.length) $("reports").append(el("li", { class: "muted" }, filtered ? "No reports match." : "No reports. Nothing went wrong in this time."));
}

function renderFacets(facets) {
  const selects = REPORT_FACETS.map(([key, field, label, allLabel]) => {
    const current = reportFilters[key] ?? "";
    const options = facets[field] ?? [];
    const name = (o) => key === "kind" ? REPORT_KINDS[o.label] ?? o.label : key === "area" ? whereLabel(o.label) : o.label;
    const select = el("select", { "aria-label": label, onchange: (event) => setReportFilter(key, event.target.value) },
      el("option", { value: "" }, allLabel),
      ...options.map((o) => el("option", { value: o.value }, `${name(o)} (${o.count})`)));
    // A filter picked by clicking (a person, a fingerprint) that isn't in the time range any more still shows
    if (current && !options.some((o) => o.value === current)) select.append(el("option", { value: current }, `${current} (0)`));
    select.value = current;
    return el("label", { class: "inline-label" }, label, select);
  });
  if (reportFilters.fingerprint) {
    selects.push(el("span", { class: "badge" }, "One error only ",
      el("button", { type: "button", class: "link small", "aria-label": "Show all errors", onclick: () => setReportFilter("fingerprint", "") }, "×")));
  }
  if (reportFilters.staffId) {
    selects.push(el("span", { class: "badge" }, "One staff account ",
      el("button", { type: "button", class: "link small", "aria-label": "Show everyone", onclick: () => setReportFilter("staffId", "") }, "×")));
  }
  $("rep-facets").replaceChildren(...selects);
}

function whoOf(report) {
  if (report.username) return { label: report.username, key: "userId", value: report.userId };
  if (report.staffUsername) return { label: `${report.staffUsername} (staff)`, key: "staffId", value: report.staffId };
  if (report.kind === "JobError") return { label: "Background job", key: "userId", value: 0 };
  return { label: "Not logged in", key: "userId", value: 0 };
}

function reportRow(report) {
  const who = whoOf(report);
  const detail = el("div", { class: "report-detail", hidden: true });
  const row = el("li", { class: "report-row" },
    el("div", { class: "report-head", role: "button", tabindex: "0", "aria-expanded": "false",
      onclick: (event) => { if (!event.target.closest("button")) toggleReport(report, row, detail); },
      onkeydown: (event) => { if (event.key === "Enter" && !event.target.closest("button")) toggleReport(report, row, detail); } },
      el("span", { class: "muted small-text" }, formatWhen(report.createdAtUtc)),
      el("span", {}, el("span", { class: `badge kind-${report.kind}` }, REPORT_KINDS[report.kind] ?? report.kind)),
      el("span", { class: "report-who" },
        el("button", { type: "button", class: "link small", title: "Only this person's reports", onclick: () => setReportFilter(who.key, who.value) }, who.label),
        report.teamName ? el("span", { class: "muted small-text" }, ` · ${report.teamName}`) : null),
      el("span", {},
        el("span", { class: "report-msg" }, report.errorType ? `${report.errorType}: ` : "", report.message),
        el("div", { class: "report-where" }, [whereLabel(report.area), report.statusCode, report.browser, report.os].filter(Boolean).join(" · ")))),
    detail);
  return row;
}

async function toggleReport(report, row, detail) {
  const head = row.querySelector(".report-head");
  if (!detail.hidden) {
    detail.hidden = true;
    head.setAttribute("aria-expanded", "false");
    return;
  }
  detail.hidden = false;
  head.setAttribute("aria-expanded", "true");
  detail.replaceChildren(el("p", { class: "muted" }, "Loading…"));
  try {
    const full = await api("GET", `/api/admin/reports/${report.id}`);
    detail.replaceChildren(...reportDetail(full, row));
  } catch (error) {
    detail.replaceChildren(el("p", { class: "message error" }, error.message));
  }
}

function prettyJson(text) {
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
}

function reportDetail(report, row) {
  const facts = [
    ["When", `${formatWhen(report.createdAtUtc)} (${report.createdAtUtc.replace("T", " ").slice(0, 19)} UTC)`],
    ["Who", whoOf(report).label + (report.userId ? ` (user ${report.userId})` : "")],
    ["Team", report.teamName ? `${report.teamName} (team ${report.teamId})` : null],
    ["Staff", report.staffUsername && report.username ? `${report.staffUsername} (viewing as support or also logged in as staff)` : null],
    ["Where", whereLabel(report.area)],
    ["Address", report.url],
    ["Status", report.statusCode],
    ["Error", report.errorType],
    ["VOD", report.vodId],
    ["Clip", report.entryId],
    ["Export", report.exportId],
    ["Segment download", report.segmentDownloadId],
    ["Browser", [report.browser, report.os].filter(Boolean).join(" on ")],
    ["User agent", report.userAgent],
    ["App version", report.appVersion],
    ["Request / visit", report.requestId],
  ].filter(([, value]) => value !== null && value !== undefined && value !== "");
  return [
    report.note ? el("div", {}, el("strong", {}, "What they wrote"), el("div", { class: "report-note" }, report.note)) : null,
    report.kind !== "BugReport" ? el("div", {}, el("strong", {}, "Message"), el("div", { class: "report-note" }, report.message)) : null,
    el("dl", { class: "report-facts" }, ...facts.flatMap(([label, value]) => [el("dt", {}, label), el("dd", {}, String(value))])),
    report.details ? el("details", {}, el("summary", {}, "Stack trace and log"), el("pre", {}, report.details)) : null,
    report.context ? el("details", { open: report.kind === "BugReport" ? "" : null }, el("summary", {}, "What was going on (page state, recent requests)"),
      el("pre", {}, prettyJson(report.context))) : null,
    el("div", { class: "row" },
      report.kind !== "BugReport"
        ? el("button", { type: "button", class: "secondary small", onclick: () => setReportFilter("fingerprint", report.fingerprint) }, "Every time this happened")
        : null,
      el("button", { type: "button", class: "secondary small", onclick: () => downloadOne(report) }, "Download this report"),
      el("button", { type: "button", class: "secondary danger small", onclick: () => deleteReport(report, row) }, "Delete")),
  ].filter(Boolean);
}

function groupRow(group) {
  return el("li", { class: "report-row" },
    el("div", { class: "report-head", role: "button", tabindex: "0", title: "Show each time it happened",
      onclick: () => setReportFilter("fingerprint", group.fingerprint),
      onkeydown: (event) => { if (event.key === "Enter") setReportFilter("fingerprint", group.fingerprint); } },
      el("span", {}, el("span", { class: "report-count" }, `${group.count}×`), el("div", { class: "muted small-text" }, `last ${formatWhen(group.lastAtUtc)}`)),
      el("span", {}, el("span", { class: `badge kind-${group.kind}` }, REPORT_KINDS[group.kind] ?? group.kind)),
      el("span", { class: "small-text" }, group.people ? plural(group.people, "person", "people") : "No one logged in",
        group.usernames.length ? el("div", { class: "muted small-text" }, group.usernames.join(", ")) : null),
      el("span", {},
        el("span", { class: "report-msg" }, group.errorType ? `${group.errorType}: ` : "", group.message),
        el("div", { class: "report-where" }, `${whereLabel(group.area)} · first ${formatWhen(group.firstAtUtc)}`))));
}

function downloadReports(format) {
  const params = reportQuery();
  params.set("format", format);
  location.href = `/api/admin/reports/download?${params}`;
}

function downloadOne(report) {
  const blob = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
  const link = el("a", { href: URL.createObjectURL(blob), download: `clipco-report-${report.id}.json` });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

async function deleteReport(report, row) {
  if (!confirm("Delete this report?")) return;
  try {
    await api("DELETE", `/api/admin/reports/${report.id}`);
    row.remove();
  } catch (error) {
    showError(error);
  }
}

async function deleteShownReports() {
  const summary = $("rep-summary").textContent.split(" · ")[0];
  if (!confirm(`Delete ${summary} that match these filters? This can't be undone. Download them first if you might need them.`)) return;
  try {
    const params = reportQuery();
    params.delete("sort");
    params.delete("order");
    const result = await api("DELETE", `/api/admin/reports?${params}`);
    setMessage($("admin-message"), `${plural(result.deleted, "report")} deleted.`, "ok");
    await loadReports(true);
  } catch (error) {
    showError(error);
  }
}

// ---------- own account, setup, login ----------

$("password-toggle").addEventListener("click", () => {
  $("password-panel").hidden = !$("password-panel").hidden;
  if (!$("password-panel").hidden) $("current-password").focus();
});
$("password-cancel").addEventListener("click", () => { $("password-panel").hidden = true; });

$("password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if ($("new-password").value !== $("new-password2").value) {
    setMessage($("admin-message"), "The new passwords don't match.", "error");
    return;
  }
  try {
    await api("POST", "/api/admin/password", { currentPassword: $("current-password").value, newPassword: $("new-password").value });
    $("password-form").reset();
    $("password-panel").hidden = true;
    setMessage($("admin-message"), "Password changed. Your other staff sessions are signed out.", "ok");
  } catch (error) {
    showError(error);
  }
});

$("setup-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if ($("setup-password").value !== $("setup-password2").value) {
    setMessage($("admin-message"), "The passwords don't match.", "error");
    return;
  }
  try {
    await api("POST", "/api/admin/setup", { username: $("setup-username").value.trim(), password: $("setup-password").value });
    setMessage($("admin-message"), "Owner account created.", "ok");
    start();
  } catch (error) {
    showError(error);
  }
});

$("staff-login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    await api("POST", "/api/admin/login", { username: $("staff-username").value, password: $("staff-password").value });
    setMessage($("admin-message"), "");
    start();
  } catch (error) {
    showError(error);
  }
});

$("staff-logout").addEventListener("click", async () => {
  await api("POST", "/api/admin/logout");
  location.reload();
});

start();

// ---------- Twitch (live stream updates; Owner only) ----------

const TWITCH_STATUS = {
  NotConnected: "Not connected",
  Connecting: "Connecting to Twitch…",
  Watching: "Watching for streams starting and ending",
  NoChannels: "Connected; no team has a channel yet",
  Retrying: "Lost the connection to Twitch",
};
let twitchTimer = null;
let twitchFormWired = false;

async function loadTwitch() {
  if (!twitchFormWired) {
    twitchFormWired = true;
    $("twitch-client-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      try {
        renderTwitch(await api("PUT", "/api/admin/twitch/client-id", { clientId: $("twitch-client-id").value.trim() }));
        setMessage($("admin-message"), "Client ID saved.", "ok");
      } catch (error) {
        showError(error);
      }
    });
  }
  renderTwitch(await api("GET", "/api/admin/twitch"));
}

function renderTwitch(state) {
  clearTimeout(twitchTimer);
  if (document.activeElement !== $("twitch-client-id")) $("twitch-client-id").value = state.clientIdFromConfig ? "" : (state.clientId ?? "");
  $("twitch-client-id").placeholder = state.clientIdFromConfig ? `${state.clientId} (from appsettings)` : "from dev.twitch.tv/console/apps";

  const status = $("twitch-status");
  const parts = [];
  if (state.login) {
    parts.push(el("p", {}, "Signed in to Twitch as ", el("strong", {}, state.login), ` since ${formatWhen(state.connectedAtUtc)}. `,
      el("span", { class: "muted" }, `${TWITCH_STATUS[state.status] ?? state.status}.${state.statusDetail ? ` ${state.statusDetail}` : ""}`)));
  } else if (state.problem) {
    parts.push(el("p", { class: "error-text" }, state.problem));
  }

  if (state.pending) {
    parts.push(el("div", { class: "twitch-code" },
      el("p", {}, "Open ", el("a", { href: state.pending.verificationUri, target: "_blank", rel: "noopener" }, state.pending.verificationUri),
        ", log in with the Twitch account the app should use, and enter this code:"),
      el("p", { class: "display twitch-user-code" }, state.pending.userCode),
      el("p", { class: "muted small-text" }, `Waiting… the code works until ${formatWhen(state.pending.expiresAtUtc)}.`)));
    // Only while a code is waiting: the sign-in finishes on Twitch's site, so ask this server every few seconds until it's done
    twitchTimer = setTimeout(() => loadTwitch().catch(showError), 3000);
  }

  const buttons = [];
  if (state.clientIdSet && !state.pending) {
    buttons.push(el("button", { type: "button", onclick: () => connectTwitch() }, state.login ? "Connect a different account" : "Connect Twitch"));
  }
  if (state.login) {
    buttons.push(el("button", { type: "button", class: "link danger small", onclick: () => disconnectTwitch() }, "Disconnect"));
  }
  if (!state.clientIdSet) parts.push(el("p", { class: "muted" }, "Enter your Twitch app's Client ID first."));
  if (buttons.length) parts.push(el("div", { class: "row" }, ...buttons));
  status.replaceChildren(...parts);

  $("twitch-channels").replaceChildren(...state.channels.map((channel) => el("li", { class: "list-row" },
    el("strong", { class: "grow" }, channel.login),
    channel.live === true ? el("span", { class: "badge error" }, "LIVE") : channel.live === false ? el("span", { class: "badge ref" }, "Offline") : null,
    el("span", { class: "muted small-text" }, channel.watched ? "Watched" : state.login ? "Not watched (over Twitch's limit)" : "Not watched"))));
}

async function connectTwitch() {
  try {
    renderTwitch(await api("POST", "/api/admin/twitch/connect"));
  } catch (error) {
    showError(error);
  }
}

async function disconnectTwitch() {
  if (!confirm("Disconnect Twitch? Pages stop updating by themselves when streams start or end.")) return;
  try {
    renderTwitch(await api("DELETE", "/api/admin/twitch"));
  } catch (error) {
    showError(error);
  }
}
