// Keyboard shortcuts help (frontend rework step 10): a dialog built from KEYBINDS / KEYBIND_SCOPES in keybinds.js, so it
// always lists what the page really does. Opened with ? (shortcutsHelp) or the keyboard icon in the top bar.

import { el } from "./core.js";

const KEY_NAMES = {
  ArrowLeft: "←", ArrowRight: "→", ArrowUp: "↑", ArrowDown: "↓",
  Escape: "Esc", Delete: "Del", Backspace: "⌫", ContextMenu: "Menu key",
};
const IS_MAC = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

// "Ctrl+Shift+Z" → <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Z</kbd>; mouse names ("Right click") stay as one plain chip
function keyChips(key) {
  const parts = key === "+" ? ["+"] : key.split("+");
  const nodes = [];
  parts.forEach((part, index) => {
    if (index) nodes.push(el("span", { class: "kb-plus" }, "+"));
    const name = part === "Ctrl" && IS_MAC ? "⌘" : KEY_NAMES[part] ?? part;
    nodes.push(el("kbd", { class: /\s|click|wheel|drag/i.test(part) ? "kb-mouse" : "" }, name));
  });
  return el("span", { class: "kb-keys" }, ...nodes);
}

let open = null;

export function showShortcuts() {
  if (open) return;
  const search = el("sl-input", { size: "small", clearable: true, autofocus: true, placeholder: "Search shortcuts", "aria-label": "Search shortcuts" },
    el("sl-icon", { library: "ph", name: "magnifying-glass", slot: "prefix" }));
  const sections = KEYBIND_SCOPES.map((scope) => {
    const binds = KEYBINDS.filter((bind) => bind.scope === scope.id);
    const rows = binds.map((bind) => {
      const row = el("tr", {},
        el("td", { class: "kb-cell" }, ...bind.keys.flatMap((key, i) => (i ? [el("span", { class: "kb-or" }, "or"), keyChips(key)] : [keyChips(key)]))),
        el("td", {}, bind.description));
      row.searchText = `${bind.keys.join(" ")} ${bind.description}`.toLowerCase();
      return row;
    });
    const section = el("section", { class: "kb-section" },
      el("h3", { class: "display" }, scope.title),
      el("p", { class: "faint" }, scope.description),
      el("table", { class: "kb-table" }, el("tbody", {}, ...rows)));
    section.rows = rows;
    return section;
  });
  const empty = el("p", { class: "faint", hidden: true }, "No shortcut matches.");
  search.addEventListener("sl-input", () => {
    const words = search.value.toLowerCase().split(/\s+/).filter(Boolean);
    let any = false;
    for (const section of sections) {
      let shown = 0;
      for (const row of section.rows) {
        row.hidden = !words.every((word) => row.searchText.includes(word));
        if (!row.hidden) shown += 1;
      }
      section.hidden = shown === 0;
      any ||= shown > 0;
    }
    empty.hidden = any;
  });

  const box = el("sl-dialog", { label: "Keyboard shortcuts", class: "shortcuts-dialog" },
    search, el("div", { class: "kb-sections" }, ...sections, empty));
  box.addEventListener("sl-after-hide", (event) => {
    if (event.target !== box) return;
    box.remove();
    open = null;
  });
  open = box;
  document.body.append(box);
  customElements.whenDefined("sl-dialog").then(() => box.show());
}
