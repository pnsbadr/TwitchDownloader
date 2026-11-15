// The right-click menu shared by cards and the timeline: openMenu(x, y, [{ icon, label, run, danger }]).
// One <sl-menu id="context-menu"> at the end of the page; it closes on a click elsewhere, Esc, scrolling or leaving the window.

import { $, el } from "./core.js";

let wired = false;

function wire() {
  if (wired) return;
  wired = true;
  const menu = $("context-menu");
  menu.addEventListener("sl-select", (event) => {
    const run = event.detail.item.run;
    closeMenu();
    run?.();
  });
  document.addEventListener("pointerdown", (event) => {
    if (!menu.hidden && !event.composedPath().includes(menu)) closeMenu();
  }, true);
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !menu.hidden) {
      event.preventDefault();
      closeMenu();
    }
  }, true);
  window.addEventListener("blur", closeMenu);
  document.addEventListener("scroll", closeMenu, true);
}

export function openMenu(x, y, items) {
  wire();
  if (!items.length) return;
  const menu = $("context-menu");
  menu.replaceChildren(...items.map((item) => {
    const entry = el("sl-menu-item", { class: item.danger ? "danger" : "" },
      el("sl-icon", { slot: "prefix", library: "ph", name: item.icon }), item.label);
    entry.run = item.run;
    return entry;
  }));
  menu.hidden = false;
  // Keep it on screen
  const rect = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, window.innerWidth - rect.width - 8)}px`;
  menu.style.top = `${Math.min(y, window.innerHeight - rect.height - 8)}px`;
  menu.querySelector("sl-menu-item")?.focus();
}

export function closeMenu() {
  $("context-menu").hidden = true;
}

export function isMenuOpen() {
  return !$("context-menu").hidden;
}
