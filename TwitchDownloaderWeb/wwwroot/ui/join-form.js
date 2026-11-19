// The form for a one-time link (team registration, team owner link, invite, password reset; staff links too on join.html).
// Used by join.html and by the app's start screen (ui/start.js), which shows team users' links in its card (badrfly 2026-11-19).
// Self-contained (no core.js) so join.html can load it without the editor.

async function request(method, url, body) {
  const response = await fetch(url, {
    method, headers: body === undefined ? {} : { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body),
  });
  window.clipcoReports?.request(method, url.replace(/#.*/, ""), response.status, 0);
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    const message = data?.errors ? Object.values(data.errors).flat().join(" ")
      : response.status === 429 && !data?.detail ? "Too many attempts. Wait a minute and try again."
      : data?.detail || data?.title || `Request failed (${response.status}).`;
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }
  return data;
}

function h(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
    else if (value !== undefined && value !== null && value !== false) node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function formatWhen(utcString) {
  let pacific = false;
  try { pacific = localStorage.getItem("clipLibrary.timeZone") === "pacific"; } catch { /* storage blocked */ }
  const options = { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit", timeZoneName: "short" };
  if (pacific) options.timeZone = "America/Los_Angeles";
  return new Intl.DateTimeFormat(undefined, options).format(new Date(utcString));
}

function setMessage(element, text, kind) {
  element.textContent = text || "";
  element.className = "message" + (kind ? ` ${kind}` : "");
}

/**
 * Inspects the link and builds its form in `container`.
 * @param {HTMLElement} container
 * @param {string} token
 * @param {{ onDone?: (result: any, link: any) => void, headingTag?: string }} options  onDone: after the link was used (default: go to the
 *   app, or the Admin page for staff links)
 * @returns {Promise<any>} the link's kind and details, or null if it doesn't work
 */
export async function mountJoinForm(container, token, { onDone, headingTag = "h2" } = {}) {
  const heading = h(headingTag, { class: "join-heading" }, "Checking your link…");
  const intro = h("p", { class: "muted" });
  const message = h("p", { class: "message", role: "status" });
  container.replaceChildren(heading, intro, message);

  if (!token) {
    heading.textContent = "This link is incomplete";
    setMessage(message, "Open the whole link you were sent.", "error");
    return null;
  }
  let link;
  try {
    link = await request("POST", "/api/links/inspect", { token });
  } catch (error) {
    heading.textContent = "This link doesn't work";
    setMessage(message, error.message, "error");
    return null;
  }

  const isReset = link.kind === "PasswordReset" || link.kind === "StaffPasswordReset";
  const registration = link.kind === "TeamRegistration";
  let mode = "new";
  let useLoggedIn = Boolean(link.loggedInAs && !isReset && link.kind !== "StaffInvite");

  // Team name: links made since 2026-11-19 suggest one after the team's channel, grayed out until the pencil is pressed; picking another
  // counts as the team's name change (the next one only after teamRenameDays days). Older links let the opener type it.
  const teamName = h("input", { maxlength: "100", placeholder: "e.g. Arky's editors", "aria-label": "Team name", id: "team-name" });
  const pencil = h("button", { type: "button", class: "link pencil", title: "Change the team name", "aria-label": "Change the team name", hidden: true }, "✏️");
  const warning = h("p", { class: "tiny-text", hidden: true });
  const nameBlock = h("div", { hidden: !registration },
    h("label", { for: "team-name" }, "Team name"), h("div", { class: "name-edit" }, teamName, pencil), warning);
  const showTeamHeading = () => {
    if (!registration) return;
    const name = teamName.value.trim() || link.teamName;
    heading.textContent = name ? `Create ${name}` : "Create your team";
  };
  const setNameEditing = (editing) => {
    teamName.readOnly = !editing;
    pencil.hidden = editing;
    warning.hidden = !editing;
    if (editing) {
      warning.replaceChildren(`You can change your team's name once every ${link.teamRenameDays} days. `,
        h("button", { type: "button", class: "link", onclick: () => setNameEditing(false) }, "Cancel"));
      teamName.focus();
      teamName.select();
    } else {
      teamName.value = link.teamName;
    }
    showTeamHeading();
  };
  pencil.addEventListener("click", () => setNameEditing(true));
  teamName.addEventListener("input", showTeamHeading);
  teamName.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && link.teamName && !teamName.readOnly) { event.preventDefault(); event.stopPropagation(); setNameEditing(false); }
  });

  const continueName = h("strong");
  const continueButton = h("button", { type: "submit" }, "Continue");
  const otherAccount = h("button", { type: "button", class: "link", onclick: () => { useLoggedIn = false; render(); } }, "Use another account");
  const continueBlock = h("div", {}, h("p", {}, "Logged in as ", continueName, "."), h("div", { class: "row" }, continueButton, otherAccount));

  const segNew = h("button", { type: "button", class: "seg", "data-mode": "new" }, "Create an account");
  const segExisting = h("button", { type: "button", class: "seg", "data-mode": "existing" }, "I already have one");
  const segs = h("div", { class: "tz", role: "group", "aria-label": "Account" }, segNew, segExisting);
  const hint = h("p", { class: "muted small-text" });
  const username = h("input", { autocomplete: "username", spellcheck: "false", autocapitalize: "off", maxlength: "32" });
  const password = h("input", { type: "password", autocomplete: "new-password", maxlength: "200" });
  const password2 = h("input", { type: "password", autocomplete: "new-password", maxlength: "200" });
  const usernameLabel = h("label", {}, "Username ", username);
  const confirmLabel = h("label", {}, "Repeat password ", password2);
  const submit = h("button", { type: "submit" }, "Create account");
  const accountBlock = h("div", { class: "stack" }, segs, hint, usernameLabel, h("label", {}, "Password ", password), confirmLabel,
    h("div", { class: "row" }, submit));
  for (const seg of [segNew, segExisting]) seg.addEventListener("click", () => { mode = seg.dataset.mode; render(); });

  const form = h("form", { class: "stack", novalidate: true }, nameBlock, continueBlock, accountBlock);
  const body = h("div", {}, form);
  container.append(body);

  if (link.kind === "StaffInvite" || isReset) segs.hidden = hint.hidden = true;
  if (isReset) usernameLabel.hidden = true;

  function render() {
    continueBlock.hidden = !useLoggedIn;
    accountBlock.hidden = useLoggedIn;
    segNew.setAttribute("aria-pressed", String(mode === "new"));
    segExisting.setAttribute("aria-pressed", String(mode === "existing"));
    confirmLabel.hidden = mode !== "new";
    password.autocomplete = mode === "new" ? "new-password" : "current-password";
    submit.textContent = isReset ? "Set new password" : mode === "new" ? "Create account" : "Log in and continue";
    hint.textContent = mode === "new"
      ? "Already in another team? Use \"I already have one\": one account works for every team you're in."
      : "Log in with the account you use for your other teams.";
  }

  const texts = {
    TeamRegistration: [link.teamName ? `Create ${link.teamName}` : "Create your team",
      "You'll be the owner of the new team: you invite your editors and clippers and decide what each of them can do."
      + (link.channels?.length ? ` The team works on ${link.channels.join(", ")}'s VODs.` : "")],
    TeamOwnership: [`Become the owner of ${link.teamName}`, `You'll own ${link.teamName}: its library, members and invite links.`],
    TeamInvite: [`Join ${link.teamName}`, "You'll start with no access; the team owner gives you a role after you join."],
    PasswordReset: [`New password for ${link.forUsername}`, "Choose a new password. You'll be signed out everywhere else."],
    StaffInvite: ["Become an app moderator", "This makes a staff account for the Admin page, separate from any team account. "
      + "You start with no permissions; the app Owner picks them."],
    StaffPasswordReset: [`New staff password for ${link.forUsername}`, "Choose a new password. You'll be signed out everywhere else."],
  };
  const [title, text] = texts[link.kind];
  heading.textContent = title;
  intro.textContent = `${text} This link works once and expires ${formatWhen(link.expiresAtUtc)}.`;
  if (useLoggedIn) {
    continueName.textContent = link.loggedInAs;
    continueButton.textContent = `Continue as ${link.loggedInAs}`;
  }
  if (registration && link.teamName) setNameEditing(false);
  render();

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const payload = { token, newAccount: !useLoggedIn && mode === "new", teamName: teamName.value };
    if (isReset) {
      payload.newAccount = false;
      payload.password = password.value;
      if (payload.password !== password2.value) { setMessage(message, "The passwords don't match.", "error"); return; }
    } else if (!useLoggedIn) {
      payload.username = username.value.trim();
      payload.password = password.value;
      if (mode === "new" && payload.password !== password2.value) { setMessage(message, "The passwords don't match.", "error"); return; }
    }
    const buttons = form.querySelectorAll("button[type=submit]");
    for (const button of buttons) button.disabled = true;
    setMessage(message, "Working…");
    try {
      const result = await request("POST", "/api/links/accept", payload);
      if (onDone) onDone(result, link);
      else location.href = result?.staff ? "/admin.html" : "/";
    } catch (error) {
      setMessage(message, error.message, "error");
      for (const button of buttons) button.disabled = false;
    }
  });
  return link;
}
