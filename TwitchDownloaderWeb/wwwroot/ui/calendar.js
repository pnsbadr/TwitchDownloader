// The library's calendar (badrfly 2026-11-21, header version D): one month of days, each with how many items it has, the month's total,
// arrows to the months around it and a strip of the months that have items. It replaced the per-month counters and the jump menu.
// What a click does is up to the caller (library.js: jump there; Shift+click two days: show only those days).

import { el } from "./core.js";

const iso = (y, m, d) => `${y}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;

// Monday first, like the mockup
const WEEKDAYS = (() => {
  const format = new Intl.DateTimeFormat(undefined, { weekday: "narrow", timeZone: "UTC" });
  return [0, 1, 2, 3, 4, 5, 6].map((i) => format.format(new Date(Date.UTC(2024, 0, 1 + i)))); // 2024-01-01 was a Monday
})();

const monthLong = new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric", timeZone: "UTC" });
const monthShort = new Intl.DateTimeFormat(undefined, { month: "short", timeZone: "UTC" });
const monthShortYear = new Intl.DateTimeFormat(undefined, { month: "short", year: "2-digit", timeZone: "UTC" });

/**
 * @param {object} o
 * @param {number} o.year, o.month  the month shown (1-12)
 * @param {Map<string, number>} o.counts  "2026-11-14" → items that day
 * @param {(n: number) => string} o.countLabel  "8 clips"
 * @param {string} o.today  "2026-11-21" in the viewer's time zone
 * @param {{ from: string, to: string } | null} o.range  the days shown only (highlighted)
 * @param {string} o.pending  the first day of a Shift+click range, waiting for the second
 * @param {boolean} o.loading  the counts are still coming
 * @param {string} o.note  a line under the days (how clicks work, "only loaded VODs")
 * @param {Node[]} o.actions  buttons for the footer
 */
export function calendarView(o) {
  const { year, month, counts, countLabel, today } = o;
  const monthKey = `${year}-${String(month).padStart(2, "0")}`;
  let monthTotal = 0;
  let total = 0;
  const months = new Map(); // "2026-11" → n
  for (const [day, n] of counts) {
    total += n;
    const key = day.slice(0, 7);
    months.set(key, (months.get(key) ?? 0) + n);
    if (key === monthKey) monthTotal += n;
  }
  const step = (delta) => {
    const at = new Date(Date.UTC(year, month - 1 + delta, 1));
    o.onMonth(at.getUTCFullYear(), at.getUTCMonth() + 1);
  };
  const arrow = (delta, label, icon) => {
    const button = el("button", { type: "button", class: "cal-arrow", "aria-label": label, title: label }, el("sl-icon", { library: "ph", name: icon }));
    button.addEventListener("click", () => step(delta));
    return button;
  };

  const first = new Date(Date.UTC(year, month - 1, 1));
  const lead = (first.getUTCDay() + 6) % 7; // blanks before the 1st (Monday first)
  const days = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const cells = [];
  for (let i = 0; i < lead; i++) cells.push(el("span", { class: "cal-day blank", "aria-hidden": "true" }));
  for (let d = 1; d <= days; d++) {
    const day = iso(year, month, d);
    const n = counts.get(day) ?? 0;
    const inRange = o.range && day >= o.range.from && day <= o.range.to;
    const button = el("button", {
      type: "button",
      class: `cal-day${n ? " has" : ""}${day === today ? " today" : ""}${inRange ? " on" : ""}${o.pending === day ? " pending" : ""}${day > today ? " future" : ""}`,
      "aria-label": `${day}${n ? `, ${countLabel(n)}` : ""}`,
      title: n ? countLabel(n) : "",
    }, el("span", { class: "cal-num" }, String(d)), n ? el("span", { class: "cal-n" }, n > 999 ? "999+" : String(n)) : null);
    button.addEventListener("click", (event) => o.onDay(day, event));
    cells.push(button);
  }

  // The months that have items, newest first (a click shows that month)
  const strip = [...months].sort((a, b) => (a[0] < b[0] ? 1 : -1)).slice(0, 12).map(([key, n]) => {
    const [y, m] = key.split("-").map(Number);
    const date = new Date(Date.UTC(y, m - 1, 15));
    const name = y === Number(today.slice(0, 4)) ? monthShort.format(date) : monthShortYear.format(date);
    const button = el("button", { type: "button", class: `cal-month${key === monthKey ? " on" : ""}`, title: countLabel(n) }, name, el("span", { class: "cal-n" }, String(n)));
    button.addEventListener("click", () => o.onMonth(y, m));
    return button;
  });

  return el("div", { class: "cal", role: "dialog", "aria-label": "Calendar" },
    el("div", { class: "cal-total faint" }, o.loading ? "Counting…" : countLabel(total)),
    el("div", { class: "cal-head" },
      el("span", { class: "cal-title" }, monthLong.format(first), el("span", { class: "cal-mtotal faint" }, monthTotal ? countLabel(monthTotal) : "")),
      arrow(-1, "Month before", "caret-left"),
      arrow(1, "Month after", "caret-right")),
    el("div", { class: "cal-grid" },
      ...WEEKDAYS.map((w) => el("span", { class: "cal-wd faint", "aria-hidden": "true" }, w)),
      ...cells),
    strip.length ? el("div", { class: "cal-strip" }, ...strip) : null,
    o.note ? el("div", { class: "cal-note faint" }, o.note) : null,
    o.actions?.length ? el("div", { class: "cal-actions" }, ...o.actions) : null);
}

// A small pop-up under an element: closes on Esc or a click outside it (not on the element itself, which usually toggles it)
export function openPopover(anchor, node, { onClose, className = "" } = {}) {
  const pop = el("div", { class: `lpop ${className}` }, node);
  document.body.append(pop);
  // The anchor may be drawn again while the pop-up is open (the list's headings are): it stays where the anchor was
  let rect = anchor.getBoundingClientRect();
  const place = () => {
    if (anchor.isConnected) rect = anchor.getBoundingClientRect();
    const r = rect;
    const w = pop.offsetWidth;
    const h = pop.offsetHeight;
    const left = Math.max(8, Math.min(r.right - w, window.innerWidth - w - 8));
    const below = r.bottom + 4;
    const top = below + h + 8 <= window.innerHeight ? below : Math.max(8, r.top - h - 4);
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  };
  const close = () => {
    pop.remove();
    document.removeEventListener("pointerdown", outside, true);
    document.removeEventListener("keydown", key, true);
    window.removeEventListener("resize", place);
    onClose?.();
  };
  const outside = (event) => {
    if (pop.contains(event.target) || (anchor.isConnected && anchor.contains(event.target))) return;
    close();
  };
  const key = (event) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopImmediatePropagation();
    close();
  };
  document.addEventListener("pointerdown", outside, true);
  document.addEventListener("keydown", key, true);
  window.addEventListener("resize", place);
  place();
  requestAnimationFrame(place);
  return { pop, close, place, replace: (next) => { pop.replaceChildren(next); place(); } };
}
