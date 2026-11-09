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
  if (can("ViewAsSupport")) {
    actions.push(el("button", { type: "button", class: "secondary small", title: "Open this team's library read-only. The visit is logged.",
      onclick: () => viewAsSupport(team) }, "View as support"));
  }
  if (can("ManageTeamLinks")) {
    actions.push(el("button", { type: "button", class: "link small", onclick: () => makeOwnerLink(team) },
      team.ownerUsername ? "Owner link (hand over)" : "Owner link"));
  }
  if (can("DisableTeams")) {
    actions.push(el("button", { type: "button", class: team.disabledAtUtc ? "link small" : "link danger small", onclick: () => setTeamDisabled(team, !team.disabledAtUtc) },
      team.disabledAtUtc ? "Enable" : "Disable"));
  }

  return el("li", { class: "list-row admin-row" },
    el("div", { class: "grow" },
      el("strong", {}, team.name),
      team.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
      team.autoSaveAudio ? el("span", { class: "badge ok" }, "Auto audio saving") : null,
      el("div", { class: "muted small-text" },
        `${team.ownerUsername ? `Owner ${team.ownerUsername}` : "No owner yet"} · ${plural(team.memberCount, "member")} · `
        + `${plural(team.entryCount, "entry", "entries")} · ${formatBytes(team.diskBytes)} on disk · `
        + `${team.lastActiveAtUtc ? `last active ${formatWhen(team.lastActiveAtUtc)}` : "nobody active yet"} · created ${formatWhen(team.createdAtUtc)}`)),
    el("div", { class: "row admin-actions" }, ...actions),
    details);
}

async function toggleTeamDetails(team, details) {
  if (!details.hidden) { details.hidden = true; return; }
  details.hidden = false;
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
        el("div", { class: "muted small-text" }, `${what} · made ${formatWhen(link.createdAtUtc)} · ${state.text}`)),
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
    const link = await api("POST", "/api/admin/links", { kind: "registration", note: $("registration-note").value });
    $("registration-note").value = "";
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
