// The right-click menu shared by cards and the timeline: openMenu(x, y, [{ icon, label, run, danger }]). An item may instead have a color
// dot (swatch), be checked (✓ on the right) or show a key (hint); { divider: true } draws a line.
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
    if (item.divider) return el("sl-divider");
    const entry = el("sl-menu-item", { class: item.danger ? "danger" : "" },
      item.swatch
        ? el("span", { slot: "prefix", class: "menu-swatch", style: `background:${item.swatch}` })
        : el("sl-icon", { slot: "prefix", library: "ph", name: item.icon }),
      item.label,
      item.checked ? el("sl-icon", { slot: "suffix", library: "ph", name: "check", class: "menu-check" })
        : item.hint ? el("span", { slot: "suffix", class: "menu-hint" }, item.hint) : null);
    entry.run = item.run;
    return entry;
  }));
  menu.hidden = false;
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
  place(menu, x, y);
  // The items get their real height once Shoelace has drawn them: place it again then, so it's never cut off at the bottom
  requestAnimationFrame(() => place(menu, x, y));
  Promise.all([customElements.whenDefined("sl-menu-item"), ...[...menu.children].map((item) => item.updateComplete)])
    .then(() => requestAnimationFrame(() => place(menu, x, y)));
  menu.querySelector("sl-menu-item")?.focus();
}

// Keep it on screen: opens up from the pointer when there's no room below, left of it when there's none on the right
function place(menu, x, y) {
  if (menu.hidden) return;
  const rect = menu.getBoundingClientRect();
  const fitsBelow = y + rect.height + 8 <= window.innerHeight;
  const top = fitsBelow ? y : Math.max(8, y - rect.height);
  const left = x + rect.width + 8 <= window.innerWidth ? x : Math.max(8, x - rect.width);
  menu.style.left = `${left}px`;
  menu.style.top = `${top}px`;
}

export function closeMenu() {
  $("context-menu").hidden = true;
}

export function isMenuOpen() {
  return !$("context-menu").hidden;
}
