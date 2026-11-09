"use strict";

let teams = [];

function show(panel) {
  for (const id of ["setup-panel", "login-panel", "teams-panel", "links-panel"]) $(id).hidden = !panel.includes(id);
}

async function start() {
  try {
    const me = await api("GET", "/api/admin/me");
    $("staff-name").textContent = me.username + (me.isOwner ? " (Owner)" : "");
    $("staff-bar").hidden = false;
    show(["teams-panel", "links-panel"]);
    await Promise.all([loadTeams(), loadLinks()]);
    return;
  } catch (error) {
    if (error.status !== 401) { setMessage($("admin-message"), error.message, "error"); return; }
  }

  const setup = await api("GET", "/api/admin/setup");
  if (setup.needed) {
    show(["setup-panel"]);
    $("setup-remote").hidden = setup.allowedHere;
    $("setup-form").hidden = !setup.allowedHere;
  } else {
    show(["login-panel"]);
    $("staff-username").focus();
  }
}

async function loadTeams() {
  teams = await api("GET", "/api/admin/teams");
  $("teams").replaceChildren(...teams.map((team) => el("li", { class: "list-row" },
    el("div", { class: "grow" },
      el("strong", {}, team.name), team.disabledAtUtc ? el("span", { class: "badge error" }, "Disabled") : null,
      el("div", { class: "muted small-text" },
        `${team.ownerUsername ? `Owner ${team.ownerUsername}` : "No owner yet"} · ${team.memberCount} member${team.memberCount === 1 ? "" : "s"} · `
        + `${team.entryCount} entr${team.entryCount === 1 ? "y" : "ies"} · created ${formatWhen(team.createdAtUtc)}`)),
    el("button", { type: "button", class: "secondary small", onclick: () => makeLink({ kind: "ownership", teamId: team.id, note: `Owner of ${team.name}` }) },
      team.ownerUsername ? "Make owner link (hand over)" : "Make owner link"))));
}

async function loadLinks() {
  const links = await api("GET", "/api/admin/links");
  $("links").replaceChildren(...links.map((link) => {
    const state = linkState(link);
    const what = link.kind === "TeamRegistration" ? "Team registration" : `Owner of ${link.teamName ?? "a deleted team"}`;
    return el("li", { class: "list-row" },
      el("div", { class: "grow" },
        el("strong", {}, link.note || what),
        el("div", { class: "muted small-text" }, `${what} · made ${formatWhen(link.createdAtUtc)} · ${state.text}`)),
      state.open ? el("button", { type: "button", class: "secondary small", onclick: () => revoke(link) }, "Revoke") : null);
  }));
}

async function makeLink(body) {
  try {
    const link = await api("POST", "/api/admin/links", body);
    $("link-new").replaceChildren(linkBox(link.url));
    $("link-new").scrollIntoView({ block: "nearest" });
    loadLinks();
  } catch (error) {
    setMessage($("admin-message"), error.message, "error");
  }
}

async function revoke(link) {
  try {
    await api("DELETE", `/api/admin/links/${link.id}`);
    loadLinks();
  } catch (error) {
    setMessage($("admin-message"), error.message, "error");
  }
}

$("registration-form").addEventListener("submit", (event) => {
  event.preventDefault();
  makeLink({ kind: "registration", note: $("registration-note").value });
  $("registration-note").value = "";
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
    setMessage($("admin-message"), error.message, "error");
  }
});

$("staff-login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    await api("POST", "/api/admin/login", { username: $("staff-username").value, password: $("staff-password").value });
    setMessage($("admin-message"), "");
    start();
  } catch (error) {
    setMessage($("admin-message"), error.message, "error");
  }
});

$("staff-logout").addEventListener("click", async () => {
  await api("POST", "/api/admin/logout");
  location.reload();
});

start();
