// Calendar days in the viewer's time zone (Local or Pacific, from the account setting), turned into UTC instants for the
// API's from/to filters. Same method as the old page's calendar (zonedMidnightUtc in app.js).

import { me } from "./core.js";

const PACIFIC = "America/Los_Angeles";
const partsFormatters = new Map();

export function viewerTimeZone() {
  return me?.timeZone === "pacific" ? PACIFIC : Intl.DateTimeFormat().resolvedOptions().timeZone;
}

export function zonedParts(ms, timeZone) {
  let formatter = partsFormatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("en-US", {
      timeZone, hourCycle: "h23", year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric",
    });
    partsFormatters.set(timeZone, formatter);
  }
  const parts = Object.fromEntries(formatter.formatToParts(new Date(ms)).map((p) => [p.type, p.value]));
  return { year: +parts.year, month: +parts.month, day: +parts.day, hour: +parts.hour % 24, minute: +parts.minute, second: +parts.second };
}

// UTC instant (ms) of midnight at the start of year-month-day in a time zone. Day/month overflow rolls over.
export function zonedMidnightUtc(year, month, day, timeZone) {
  const wall = Date.UTC(year, month - 1, day);
  let utc = wall;
  for (let i = 0; i < 3; i++) {
    const p = zonedParts(utc, timeZone);
    const offset = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - utc;
    utc = wall - offset;
  }
  return utc;
}

export function dayRange(day, timeZone) {
  return {
    from: new Date(zonedMidnightUtc(day.year, day.month, day.day, timeZone)).toISOString(),
    to: new Date(zonedMidnightUtc(day.year, day.month, day.day + 1, timeZone)).toISOString(),
  };
}

// "2026-11-15" for today in a time zone (the value of an <input type=date>)
export function todayIn(timeZone) {
  const p = zonedParts(Date.now(), timeZone);
  return `${p.year}-${String(p.month).padStart(2, "0")}-${String(p.day).padStart(2, "0")}`;
}
