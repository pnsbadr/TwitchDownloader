// A time box with a fixed h:mm:ss mask (IMask): the colons can't be typed over or deleted, digits overwrite in place.
// Used everywhere a time is typed (VOD pop-up on the export list, the selection row, trims).
//
//   const box = timeBox({ seconds: 75, fraction: true, label: "Start", onChange: (s) => ... });
//   box.seconds        -> 75 (number, or null when the box was cleared)
//   box.seconds = 90   -> shows 00:01:30.00
//
// IMask is a classic script (global IMask) loaded by the page.

import { el } from "./core.js";

const pad = (n, width = 2) => String(n).padStart(width, "0");

export function formatTimeBox(seconds, fraction = false) {
  if (seconds === null || seconds === undefined || Number.isNaN(seconds)) return "";
  const hundredths = Math.round(Math.max(0, seconds) * 100);
  const whole = Math.floor(hundredths / 100);
  const text = `${pad(Math.floor(whole / 3600))}:${pad(Math.floor((whole % 3600) / 60))}:${pad(whole % 60)}`;
  return fraction ? `${text}.${pad(hundredths % 100)}` : text;
}

export function parseTimeBox(text) {
  const match = /^(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{2}))?$/.exec(text);
  if (!match) return null;
  const [, h, m, s, f] = match;
  return Number(h) * 3600 + Number(m) * 60 + Number(s) + (f ? Number(f) / 100 : 0);
}

export function timeBox({ seconds = null, fraction = false, label, min = 0, max = Infinity, onChange } = {}) {
  const input = el("input", {
    class: "timebox" + (fraction ? " fraction" : ""),
    inputmode: "numeric",
    autocomplete: "off",
    spellcheck: "false",
    "aria-label": label,
  });
  const range = (to) => ({ mask: IMask.MaskedRange, from: 0, to, maxLength: 2 });
  const mask = IMask(input, {
    mask: fraction ? "HH:MM:SS.FF" : "HH:MM:SS",
    blocks: { HH: range(99), MM: range(59), SS: range(59), FF: range(99) },
    lazy: false,            // always shows the whole pattern, so the colons are there from the start
    placeholderChar: "0",
    overwrite: true,
  });

  let last = seconds;
  const show = (value) => {
    mask.value = value === null ? formatTimeBox(0, fraction) : formatTimeBox(value, fraction);
    input.classList.remove("invalid");
  };
  show(seconds);

  const commit = () => {
    const value = parseTimeBox(mask.value);
    if (value === null || value < min || value > max) {
      input.classList.add("invalid");
      return;
    }
    input.classList.remove("invalid");
    if (value !== last) {
      last = value;
      onChange?.(value);
    }
  };
  input.addEventListener("change", commit);
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") commit();
    if (event.key === "Escape") show(last);
  });

  Object.defineProperty(input, "seconds", {
    get: () => parseTimeBox(mask.value),
    set: (value) => { last = value; show(value); },
  });
  input.mask = mask;
  return input;
}
