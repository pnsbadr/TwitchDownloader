"use strict";

let team = null;
let editingRole = null;
let logUserId = null;
let logOldestId = null;

const ACTION_LABELS = {
  "entry.added": "added", "entry.renamed": "renamed", "entry.retrimmed": "re-cut", "entry.deleted": "deleted",
  "file.served": "downloaded", "generation.requested": "asked for a file", "shortform.requested": "started a vertical render",
  "shortform.deleted": "deleted a vertical render", "channel.added": "saved a channel", "channel.removed": "removed a channel",
  "audio.rescue_started": "started saving audio", "team.member_joined": "joined", "team.member_removed": "removed a member",
  "team.member_role_changed": "changed a role", "role.created": "created a role", "role.changed": "changed a role", "role.deleted": "deleted a role",
  "link.created": "made a link", "link.revoked": "revoked a link", "team.settings_changed": "changed a setting", "team.created": "created the team",
  "team.owner_changed": "became the owner", "user.password_reset_link": "made a password reset link", "user.password_reset": "reset their password",
};

function has(permission) {
  return team.myPermissions.includes(permission);
}

async function load() {
  // A support visit (app staff, read-only) shows the banner with the way out
  api("GET", "/api/auth/me").then((me) => {
    if (!me.support) return;
    $("support-team").textContent = me.support.teamName;
    $("support-banner").hidden = false;
  }).catch(() => {});

  try {
    team = await api("GET", "/api/team");
  } catch (error) {
    if (error.status === 401) { location.href = "/login.html"; return; }
    setMessage($("team-message"), error.message, "error");
    return;
  }
  $("team-title").textContent = team.name;
  document.title = `${team.name} · Clip Library`;

  $("members-panel").hidden = false;
  $("roles-panel").hidden = !(has("ManageRoles") || has("AssignRoles"));
  $("new-role").hidden = !has("ManageRoles");
  $("activity-panel").hidden = !has("ViewActivity");
  $("log-panel").hidden = !has("ViewActivity");
  $("invites-panel").hidden = !has("InviteMembers");
  renderMembers();
  renderRoles();
  if (has("ViewActivity")) { loadActivity(); loadLog(true); }
  if (has("InviteMembers")) loadInvites();
  loadSettings();
}

async function reloadTeam() {
  team = await api("GET", "/api/team");
  renderMembers();
  renderRoles();
}

// ---------- members ----------

function renderMembers() {
  const assignable = team.roles.filter((r) => r.permissions.every(has));
  $("members").replaceChildren(...team.members.map((member) => {
    let roleControl;
    if (member.isOwner) {
      roleControl = el("span", { class: "badge" }, "Owner");
    } else if (member.canManage && has("AssignRoles")) {
      const select = el("select", { "aria-label": `Role for ${member.username}` },
        el("option", { value: "" }, "No role (no access)"),
        ...assignable.map((r) => el("option", { value: r.id }, r.name)));
      select.value = member.roleId ?? "";
      select.addEventListener("change", () => assignRole(member, select.value ? Number(select.value) : null));
      roleControl = select;
    } else {
      roleControl = el("span", { class: member.roleName ? "badge" : "badge ref" }, member.roleName ?? "No role");
    }

    const actions = [];
    if (member.canManage && has("ResetPasswords")) {
      actions.push(el("button", { type: "button", class: "link small", onclick: () => makeResetLink(member) }, "Password reset link"));
    }
    if (has("ViewActivity")) {
      actions.push(el("button", { type: "button", class: "link small", onclick: () => showLogFor(member) }, "Activity"));
    }
    if (member.canManage && has("RemoveMembers")) {
      actions.push(el("button", { type: "button", class: "link danger small", onclick: () => removeMember(member) }, "Remove"));
    }

    return el("li", { class: "list-row" },
      el("div", { class: "grow" },
        el("strong", {}, member.username),
        el("div", { class: "muted small-text" },
          `Joined ${formatWhen(member.joinedAtUtc)} · ${member.lastActiveAtUtc ? `last active ${formatWhen(member.lastActiveAtUtc)}` : "not active yet"}`)),
      roleControl, ...actions);
  }));
}

async function assignRole(member, roleId) {
  try {
    await api("PUT", `/api/team/members/${member.userId}/role`, { roleId });
    setMessage($("team-message"), `${member.username}'s role is saved.`, "ok");
    await reloadTeam();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
    renderMembers();
  }
}

async function removeMember(member) {
  if (!confirm(`Remove ${member.username} from ${team.name}? They keep their account and their other teams.`)) return;
  try {
    await api("DELETE", `/api/team/members/${member.userId}`);
    await reloadTeam();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
}

async function makeResetLink(member) {
  try {
    const link = await api("POST", `/api/team/members/${member.userId}/password-reset`, {});
    const box = linkBox(link.url);
    box.querySelector("p").textContent = `Send this to ${member.username}. It works once and expires in 24 hours.`;
    $("reset-new").replaceChildren(box);
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
}

// ---------- roles ----------

function renderRoles() {
  const labels = Object.fromEntries(team.permissionCatalog.map((p) => [p.key, p.label]));
  $("roles").replaceChildren(...team.roles.map((role) => el("li", { class: "list-row" },
    el("div", { class: "grow" },
      el("strong", {}, role.name), el("span", { class: "muted small-text" }, ` · ${role.memberCount} member${role.memberCount === 1 ? "" : "s"}`),
      el("div", { class: "muted small-text" }, role.permissions.length ? role.permissions.map((p) => labels[p]).join(", ") : "No permissions")),
    has("ManageRoles") && role.canEdit ? el("button", { type: "button", class: "secondary small", onclick: () => openRoleForm(role) }, "Edit") : null)));
  if (!team.roles.length) $("roles").append(el("li", { class: "muted" }, "No roles yet."));
}

function openRoleForm(role) {
  editingRole = role;
  $("role-form").hidden = false;
  $("role-form-title").textContent = role ? `Edit ${role.name}` : "New role";
  $("role-name").value = role?.name ?? "";
  $("role-delete").hidden = !role;

  const groups = new Map();
  for (const p of team.permissionCatalog) {
    if (!groups.has(p.group)) groups.set(p.group, []);
    groups.get(p.group).push(p);
  }
  $("role-permissions").replaceChildren(...[...groups].map(([group, perms]) => el("fieldset", {},
    el("legend", {}, group),
    ...perms.map((p) => {
      // You can only hand out what you have yourself; the rest is shown but locked
      const allowed = has(p.key);
      const box = el("input", { type: "checkbox", value: p.key });
      box.checked = Boolean(role?.permissions.includes(p.key));
      box.disabled = !allowed;
      return el("label", { class: "check-label", title: allowed ? p.description : "You don't have this permission, so you can't give it." },
        box, el("span", {}, p.label, el("span", { class: "muted small-text perm-desc" }, p.description)));
    }))));
  $("role-name").focus();
}

$("new-role").addEventListener("click", () => openRoleForm(null));
$("role-cancel").addEventListener("click", () => { $("role-form").hidden = true; });

$("role-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const body = {
    name: $("role-name").value.trim(),
    permissions: [...$("role-permissions").querySelectorAll("input:checked")].map((x) => x.value),
  };
  try {
    if (editingRole) await api("PUT", `/api/team/roles/${editingRole.id}`, body);
    else await api("POST", "/api/team/roles", body);
    $("role-form").hidden = true;
    setMessage($("team-message"), `Role "${body.name}" saved.`, "ok");
    await reloadTeam();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
});

$("role-delete").addEventListener("click", async () => {
  if (!editingRole || !confirm(`Delete the role "${editingRole.name}"? Its ${editingRole.memberCount} member(s) will have no access until they get another role.`)) return;
  try {
    await api("DELETE", `/api/team/roles/${editingRole.id}`);
    $("role-form").hidden = true;
    await reloadTeam();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
});

// ---------- activity ----------

async function loadActivity() {
  const activity = await api("GET", `/api/team/activity?days=${$("activity-days").value}`);
  $("activity-rows").replaceChildren(...activity.members.map((m) => el("tr", {},
    el("td", {}, el("strong", {}, m.username), el("div", { class: "muted small-text" }, m.isOwner ? "Owner" : m.roleName ?? "No role")),
    el("td", {}, m.lastActiveAtUtc ? formatWhen(m.lastActiveAtUtc) : "Never"),
    ...["added", "edited", "deleted", "downloaded", "generated", "vertical"].map((k) => el("td", { class: "num" }, m.counts[k] ?? 0)),
    el("td", { class: "num" }, m.entriesAdded),
    el("td", { class: "num" }, formatBytes(m.diskBytes)),
    el("td", {}, el("button", { type: "button", class: "link small", onclick: () => showLogFor(m) }, "Log")))));
}

function formatBytes(bytes) {
  if (!bytes) return "0";
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
}

$("activity-days").addEventListener("change", loadActivity);

// ---------- log ----------

async function loadLog(reset) {
  if (reset) { logOldestId = null; $("log").replaceChildren(); }
  const params = new URLSearchParams({ limit: "50" });
  if (logUserId) params.set("userId", logUserId);
  if (logOldestId) params.set("before", logOldestId);
  const events = await api("GET", `/api/team/audit?${params}`);
  if (events.length) logOldestId = events[events.length - 1].id;
  $("log-more").hidden = events.length < 50;
  $("log").append(...events.map((e) => el("li", { class: "log-row" },
    el("span", { class: "muted small-text log-time" }, formatWhen(e.timestampUtc)),
    el("span", {}, el("strong", {}, e.username ?? "App"), " ", ACTION_LABELS[e.action] ?? e.action,
      e.entryTitle ? el("span", {}, " · ", el("em", {}, e.entryTitle)) : null,
      e.details ? el("span", { class: "muted small-text" }, ` · ${e.details}`) : null))));
  if (!$("log").children.length) $("log").append(el("li", { class: "muted" }, "Nothing yet."));
}

function showLogFor(member) {
  logUserId = member.userId;
  $("log-heading").textContent = `Log: ${member.username}`;
  $("log-all").hidden = false;
  loadLog(true);
  $("log-panel").scrollIntoView({ behavior: "smooth", block: "start" });
}

$("log-all").addEventListener("click", () => {
  logUserId = null;
  $("log-heading").textContent = "Team log";
  $("log-all").hidden = true;
  loadLog(true);
});
$("log-more").addEventListener("click", () => loadLog(false));

// ---------- invites ----------

async function loadInvites() {
  const links = await api("GET", "/api/team/invites");
  $("invites").replaceChildren(...links.map((link) => {
    const state = linkState(link);
    return el("li", { class: "list-row" },
      el("div", { class: "grow" },
        el("strong", {}, link.note || "Invite link"),
        el("div", { class: "muted small-text" }, `Made ${formatWhen(link.createdAtUtc)} · ${state.text}`)),
      state.open ? el("button", { type: "button", class: "secondary small", onclick: () => revoke(link) }, "Revoke") : null);
  }));
}

async function revoke(link) {
  try {
    await api("DELETE", `/api/team/invites/${link.id}`);
    loadInvites();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
}

$("invite-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const link = await api("POST", "/api/team/invites", { note: $("invite-note").value });
    $("invite-note").value = "";
    $("invite-new").replaceChildren(linkBox(link.url));
    loadInvites();
  } catch (error) {
    setMessage($("team-message"), error.message, "error");
  }
});

// ---------- settings ----------

async function loadSettings() {
  const settings = await api("GET", "/api/team/settings");
  $("settings-panel").hidden = false;
  $("auto-save-audio").checked = settings.autoSaveAudio;
  $("auto-save-audio").disabled = !has("ManageAudioSettings");
  if (!settings.autoSaveAllowedByApp) $("auto-save-hint").textContent = "Automatic audio saving is turned off for the whole app right now.";
}

$("auto-save-audio").addEventListener("change", async () => {
  const box = $("auto-save-audio");
  try {
    await api("PUT", "/api/team/settings", { autoSaveAudio: box.checked });
    setMessage($("team-message"), `Automatic audio saving is ${box.checked ? "on" : "off"}.`, "ok");
  } catch (error) {
    box.checked = !box.checked;
    setMessage($("team-message"), error.message, "error");
  }
});

$("support-exit").addEventListener("click", async () => {
  await fetch("/api/admin/support", { method: "DELETE" });
  location.href = "/admin.html";
});

load();
