"use strict";

// Shared by login.html, join.html, team.html and admin.html. The main page (app.js) has its own copies.

const $ = (id) => document.getElementById(id);

async function api(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    const error = new Error(problemMessage(data, response.status));
    error.status = response.status;
    error.code = data?.code;
    throw error;
  }
  return data;
}

function problemMessage(problem, status) {
  if (problem?.errors) return Object.values(problem.errors).flat().join(" ");
  if (status === 429 && !problem?.detail) return "Too many attempts. Wait a minute and try again.";
  return problem?.detail || problem?.title || `Request failed (${status}).`;
}

function setMessage(element, text, kind) {
  element.textContent = text || "";
  element.className = "message" + (kind ? ` ${kind}` : "");
}

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (value !== undefined && value !== null) node.setAttribute(key, value);
  }
  for (const child of children) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// Follows the main page's Local/Pacific choice (same localStorage key)
function formatWhen(utcString) {
  if (!utcString) return "";
  let pacific = false;
  try { pacific = localStorage.getItem("clipLibrary.timeZone") === "pacific"; } catch { /* storage blocked */ }
  const options = { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit", timeZoneName: "short" };
  if (pacific) options.timeZone = "America/Los_Angeles";
  return new Intl.DateTimeFormat(undefined, options).format(new Date(utcString));
}

// A made link is shown once (its token isn't stored), with a Copy button
function linkBox(url) {
  const input = el("input", { class: "grow", readonly: "", value: url });
  const copy = el("button", { type: "button", class: "secondary", onclick: async () => {
    try {
      await navigator.clipboard.writeText(url);
      copy.textContent = "Copied";
    } catch {
      input.select();
    }
  } }, "Copy");
  return el("div", { class: "link-box" },
    el("div", { class: "row" }, input, copy),
    el("p", { class: "muted small-text" }, "Shown only now. It works once and expires in 7 days."));
}

function linkState(link) {
  if (link.usedAtUtc) return { text: `Used by ${link.usedBy ?? "someone"} · ${formatWhen(link.usedAtUtc)}`, open: false };
  if (link.revokedAtUtc) return { text: `Revoked · ${formatWhen(link.revokedAtUtc)}`, open: false };
  if (new Date(link.expiresAtUtc) <= new Date()) return { text: `Expired · ${formatWhen(link.expiresAtUtc)}`, open: false };
  return { text: `Open until ${formatWhen(link.expiresAtUtc)}`, open: true };
}
