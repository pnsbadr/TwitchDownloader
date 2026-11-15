// Vertical (9:16) editor in Player 2 (frontend rework, step 9). The old page's editor (one box or two stacked boxes cropped
// from the landscape video, a trim bar, renders list) plus layout presets, safe zones, snapping, undo/redo and keys.
//
// It works on the clip's file (/api/entries/{id}/file?preview=true), like the server's render: the file already leaves out
// the clip's cut-out parts, so trim times are seconds into the file. Boxes are fractions (0..1) of the video's width and
// height, and each keeps the shape of the portrait slot it fills, so nothing is stretched. The server renders the same
// layout with ffmpeg (POST /api/entries/{id}/shortform).
//
// Parts: the split tool (S) cuts the file at the playhead into parts, each with its own layout (face cam, split, gameplay).
// The boxes on screen are always the layout of the part under the playhead. A part can be left out (Delete): it's skipped in
// the preview and the render closes the gap (ripple). vx.parts is a partition of the whole file (part i runs from its start to
// the next one's, the last to the end of the file). There are no trim handles: I / O leave out everything before / after the
// playhead, and a render's trim becomes left-out parts at the ends (trimToParts; vx.trim stays the whole file). Rendered parts
// are the kept ones, neighbors with the same layout joined; one part is sent as a plain layout (with its trim), more as `parts`.

import { el, api, can, me, loadSetting, saveSetting, toast, formatDate, iconButton, withTooltip } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";

const OUT_W = 1080;
const OUT_H = 1920;
const MIN_RATIO = 0.2;
const MAX_RATIO = 0.8;
const MIN_BOX = 0.05;
const MIN_TRIM = 1;          // seconds, the server's minimum
const MIN_PART = 0.5;        // seconds, the server's minimum part
const SNAP_PX = 8;
const HISTORY_LIMIT = 50;
const VOLUME_KEY = "clipLibrary.previewVolume"; // shared with the old page's editor
const SNAP_KEY = "clipLibrary.snap";            // shared with the timelines
const SAFE_KEY = "clipco.vertical.safeZone";
const PRESETS_KEY = "clipco.vertical.presets";

// Where each app's buttons and captions cover the video, as fractions of the 9:16 frame (approximate; they change)
const SAFE_ZONES = {
  none: null,
  tiktok: { name: "TikTok", top: 0.07, bottom: 0.24, left: 0.05, right: 0.14 },
  shorts: { name: "YouTube Shorts", top: 0.1, bottom: 0.22, left: 0.05, right: 0.13 },
  reels: { name: "Instagram Reels", top: 0.11, bottom: 0.2, left: 0.05, right: 0.11 },
};
const SAFE_ORDER = ["none", "tiktok", "shorts", "reels"];

const vx = {
  host: null,         // #player2
  hooks: {},
  open: false,
  entry: null,
  entryKey: "",
  dom: null,
  mode: "split",
  ratio: 0.4,
  boxes: { single: null, top: null, bottom: null },
  trim: { start: 0, end: 0 },
  parts: [],          // [{ start, removed, mode, ratio, boxes }]; the one at vx.part is live in vx.mode/ratio/boxes
  part: 0,
  videoAspect: 16 / 9,
  ready: false,
  savedLayout: undefined, // the newest render's layout, applied once the video's size is known
  laidOut: false,     // savedLayout applied (the first file's metadata); later files keep the boxes
  frame: 0,
  pollTimer: null,
  entryTimer: null,
  wasRendering: false,
  renders: [],
  selected: null,     // "single" | "top" | "bottom"
  guides: [],
  dragging: false,    // a box is being dragged: the boxes don't switch parts meanwhile
  history: [],
  future: [],
  snap: true,
  safeZone: "none",
};

// ---------- geometry ----------

function slotAspect(name) {
  if (name === "single") return OUT_W / OUT_H;
  const share = name === "top" ? vx.ratio : 1 - vx.ratio;
  return OUT_W / (OUT_H * share);
}

function heightFor(name, w) {
  return (w * vx.videoAspect) / slotAspect(name);
}

// Reshape a box for its slot: keep its center and width where possible, shrink to fit inside the video
function fit(name, box) {
  const k = vx.videoAspect / slotAspect(name);
  let w = Math.min(box.w, 1, 1 / k);
  w = Math.max(w, MIN_BOX);
  const h = w * k;
  const cx = box.x + box.w / 2;
  const cy = box.y + box.h / 2;
  return { x: clamp(cx - w / 2, 0, 1 - w), y: clamp(cy - h / 2, 0, 1 - h), w, h };
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}

function centered(name) {
  const box = fit(name, { x: 0, y: 0, w: 1, h: 1 });
  return { ...box, x: (1 - box.w) / 2, y: (1 - box.h) / 2 };
}

function defaultBoxes() {
  // One box: full-height center crop. Two boxes: a facecam corner (top right) over a full-height center crop
  const topW = 0.25;
  return {
    single: centered("single"),
    top: fit("top", { x: 1 - topW, y: 0, w: topW, h: heightFor("top", topW) }),
    bottom: centered("bottom"),
  };
}

const fromServer = (box) => (box ? { x: box.x, y: box.y, w: box.width, h: box.height } : null);
const toServer = (box) => ({ x: box.x, y: box.y, width: box.w, height: box.h });

// Sets the live (current part's) boxes from one layout
function applyOne(layout) {
  vx.boxes = defaultBoxes();
  vx.mode = "split";
  if (!layout) return;
  if (layout.mode === "single") {
    vx.mode = "single";
    if (layout.top) vx.boxes.single = fit("single", fromServer(layout.top));
  } else if (layout.mode === "split") {
    vx.ratio = clamp(layout.splitRatio ?? 0.4, MIN_RATIO, MAX_RATIO);
    const fresh = defaultBoxes();
    vx.boxes.top = layout.top ? fit("top", fromServer(layout.top)) : fresh.top;
    vx.boxes.bottom = layout.bottom ? fit("bottom", fromServer(layout.bottom)) : fresh.bottom;
  }
}

const liveState = () => ({ mode: vx.mode, ratio: vx.ratio, boxes: structuredClone(vx.boxes) });

// Starts over from a layout (a render's, or null for the defaults): its parts, and with trim its start and end
function applyLayout(layout, { trim = true } = {}) {
  const list = layout?.parts?.length ? layout.parts : null;
  if (!list) {
    applyOne(layout);
    vx.parts = [{ start: 0, removed: false, ...liveState() }];
    vx.part = 0;
    if (trim) trimToParts(layout?.trimStart ?? 0, layout?.trimEnd ?? Infinity);
    return;
  }
  // The render's parts, with the gaps between them as left-out parts (they keep the layout before them)
  const parts = [];
  list.forEach((part, i) => {
    const previousEnd = i ? list[i - 1].end : 0;
    if (i > 0 && part.start > previousEnd + 0.01) parts.push({ ...structuredClone(parts.at(-1)), start: previousEnd, removed: true });
    applyOne(part);
    parts.push({ start: i ? part.start : 0, removed: false, ...liveState() });
  });
  vx.parts = parts;
  if (trim) trimToParts(list[0].start, list.at(-1).end);
  loadPart(partAt(vx.dom?.video.currentTime || 0));
}

// ---------- parts ----------

function partEnd(i) {
  return i + 1 < vx.parts.length ? vx.parts[i + 1].start : (duration_() || Infinity);
}

function partAt(t) {
  let i = 0;
  while (i + 1 < vx.parts.length && vx.parts[i + 1].start <= t + 1e-6) i++;
  return i;
}

// The live boxes back into their part
function stash() {
  const part = vx.parts[vx.part];
  if (part) Object.assign(part, liveState());
}

function loadPart(i) {
  vx.part = clamp(i, 0, vx.parts.length - 1);
  const part = vx.parts[vx.part];
  if (!part) return;
  vx.mode = part.mode;
  vx.ratio = part.ratio;
  vx.boxes = structuredClone(part.boxes);
  if (vx.selected && !visibleBoxes().includes(vx.selected)) vx.selected = null;
}

// The boxes follow the part under the playhead
function followPlayhead() {
  if (!vx.ready || vx.dragging) return;
  const i = partAt(vx.dom.video.currentTime || 0);
  if (i === vx.part) return;
  stash();
  loadPart(i);
  renderBoxes();
  renderParts();
}

// The part of the file each kept part renders (inside the trim), neighbors with the same layout joined
function pieces() {
  stash();
  const list = [];
  vx.parts.forEach((part, i) => {
    if (part.removed) return;
    const start = Math.max(part.start, vx.trim.start);
    const end = Math.min(partEnd(i), vx.trim.end);
    if (end - start < MIN_PART) return;
    const layout = singleLayout(part);
    const last = list.at(-1);
    if (last && Math.abs(last.end - start) < 0.001 && sameLook(last.layout, layout)) last.end = end;
    else list.push({ start, end, layout });
  });
  return list;
}

const keptSeconds = () => pieces().reduce((sum, piece) => sum + piece.end - piece.start, 0);

// Where playback goes from t: t itself in a kept part, else the next kept part's start; null after the last one
function playable(t) {
  for (let i = partAt(t); i < vx.parts.length; i++) {
    const start = Math.max(vx.parts[i].start, vx.trim.start);
    const end = Math.min(partEnd(i), vx.trim.end);
    if (vx.parts[i].removed || end - start <= 0.01) continue;
    if (t < end) return Math.max(t, start);
  }
  return null;
}

function splitPart() {
  if (!vx.ready) return;
  const t = vx.dom.video.currentTime;
  const i = partAt(t);
  if (t - vx.parts[i].start < MIN_PART || partEnd(i) - t < MIN_PART) {
    toast(`Too close to the edge of a part: parts are at least ${MIN_PART} s long.`);
    return;
  }
  change(() => {
    stash();
    vx.parts.splice(i + 1, 0, { ...structuredClone(vx.parts[i]), start: t });
    loadPart(i + 1);
  });
}

function joinPart() {
  if (!vx.ready) return;
  if (vx.part === 0) {
    toast("This is the first part: there's no part before it to join.");
    return;
  }
  // The part before keeps its layout; the joined part is kept unless both were left out
  change(() => {
    const [joined] = vx.parts.splice(vx.part, 1);
    vx.parts[vx.part - 1].removed &&= joined.removed;
    loadPart(vx.part - 1);
  });
}

function toggleRemoved() {
  if (!vx.ready) return;
  const part = vx.parts[vx.part];
  if (!part.removed && !vx.parts.some((other, i) => i !== vx.part && !other.removed)) {
    toast("Split the clip first (S): a part can only be left out when another one is kept.");
    return;
  }
  change(() => {
    vx.parts[vx.part].removed = !vx.parts[vx.part].removed;
  });
}

function layoutToAllParts() {
  if (vx.parts.length < 2) return;
  change(() => {
    const live = liveState();
    for (const part of vx.parts) Object.assign(part, structuredClone(live));
  });
  toast("Every part now has this layout.");
}

// A new file (the clip was changed): drop parts that start past its end
function fitParts() {
  const duration = duration_();
  stash();
  vx.parts = vx.parts.filter((part, i) => i === 0 || part.start <= duration - MIN_PART);
  if (!vx.parts.some((part) => !part.removed)) vx.parts[0].removed = false;
  loadPart(partAt(vx.dom.video.currentTime || 0));
}

// A part's (or the live) boxes as the server's layout, without times
function singleLayout({ mode, ratio, boxes }) {
  return mode === "single"
    ? { mode: "single", top: toServer(boxes.single) }
    : { mode: "split", top: toServer(boxes.top), bottom: toServer(boxes.bottom), splitRatio: ratio };
}

const seconds3 = (t) => Math.round(t * 1000) / 1000;

function currentLayout() {
  const list = pieces();
  const duration = duration_();
  if (list.length <= 1) {
    const piece = list[0] ?? { start: vx.trim.start, end: vx.trim.end, layout: singleLayout(liveState()) };
    // Null = from the start / to the end, so an untrimmed render covers the whole file
    return {
      ...piece.layout,
      trimStart: piece.start > 0.05 ? seconds3(piece.start) : null,
      trimEnd: duration && piece.end < duration - 0.05 ? seconds3(piece.end) : null,
    };
  }
  // The server mirrors the first part in the plain fields too
  return {
    ...list[0].layout,
    trimStart: list[0].start > 0 ? seconds3(list[0].start) : null,
    trimEnd: seconds3(list.at(-1).end),
    parts: list.map((piece) => ({ start: seconds3(piece.start), end: seconds3(piece.end), ...piece.layout })),
  };
}

const close = (x, y) => Math.abs((x ?? 0) - (y ?? 0)) < 0.0005;
const sameBox = (p, q) => (!p && !q) || (p && q && close(p.x, q.x) && close(p.y, q.y) && close(p.width, q.width) && close(p.height, q.height));
const sameTime = (x, y) => (x == null && y == null) || (x != null && y != null && Math.abs(x - y) < 0.05);

// Same boxes, ignoring times
function sameLook(a, b) {
  if (!a || !b || a.mode !== b.mode) return false;
  return sameBox(a.top, b.top) && (a.mode === "single" || (sameBox(a.bottom, b.bottom) && close(a.splitRatio, b.splitRatio)));
}

function sameLayout(a, b) {
  if (!a || !b) return false;
  const pa = a.parts?.length > 1 ? a.parts : null;
  const pb = b.parts?.length > 1 ? b.parts : null;
  if (pa || pb) {
    return Boolean(pa && pb) && pa.length === pb.length
      && pa.every((p, i) => sameTime(p.start, pb[i].start) && sameTime(p.end, pb[i].end) && sameLook(p, pb[i]));
  }
  if (!sameTime(a.trimStart, b.trimStart) || !sameTime(a.trimEnd, b.trimEnd)) return false;
  return sameLook(a, b);
}

const visibleBoxes = () => (vx.mode === "single" ? ["single"] : ["top", "bottom"]);

// ---------- presets ----------

// Built-in layouts, made for the video's shape when picked
const BUILT_IN_PRESETS = [
  {
    name: "Center crop",
    icon: "square",
    layout: () => ({ mode: "single", top: toServer(centered("single")) }),
  },
  {
    name: "Facecam top left, gameplay below",
    icon: "rows",
    layout: () => facecam("left"),
  },
  {
    name: "Facecam top right, gameplay below",
    icon: "rows",
    layout: () => facecam("right"),
  },
  {
    name: "Left and right halves",
    icon: "columns",
    layout: () => {
      withRatio(0.5);
      const top = fit("top", { x: 0, y: 0, w: 1, h: 1 });
      const bottom = fit("bottom", { x: 0, y: 0, w: 1, h: 1 });
      return { mode: "split", splitRatio: 0.5, top: toServer({ ...top, x: 0 }), bottom: toServer({ ...bottom, x: 1 - bottom.w }) };
    },
  },
];

function facecam(side) {
  withRatio(0.35);
  const w = 0.25;
  const top = fit("top", { x: side === "left" ? 0 : 1 - w, y: 0, w, h: heightFor("top", w) });
  return { mode: "split", splitRatio: 0.35, top: toServer(top), bottom: toServer(centered("bottom")) };
}

// Presets are worked out for a split ratio; the shapes depend on it
function withRatio(ratio) {
  vx.ratio = ratio;
}

function ownPresets() {
  const list = loadSetting(PRESETS_KEY, []);
  return Array.isArray(list) ? list.filter((p) => p && typeof p.name === "string" && p.layout) : [];
}

// layout: a layout, or a function making one (built-ins work out their boxes for the video's shape)
// A layout goes on the part under the playhead
function usePreset(layout) {
  change(() => applyOne(typeof layout === "function" ? layout() : layout));
}

async function savePreset() {
  const name = await askText({ heading: "Save this layout", label: "Name", confirm: "Save", maxLength: 40 });
  if (!name) return;
  const layout = singleLayout(liveState());
  const list = ownPresets().filter((p) => p.name !== name);
  list.push({ name, layout });
  saveSetting(PRESETS_KEY, list);
  renderPresetMenu();
  toast(`Saved the layout "${name}". It's kept in this browser.`);
}

async function deletePreset(name) {
  if (!await confirmAction({ heading: "Delete layout", text: `Delete your layout "${name}"?`, confirm: "Delete" })) return;
  saveSetting(PRESETS_KEY, ownPresets().filter((p) => p.name !== name));
  renderPresetMenu();
}

function renderPresetMenu() {
  const own = ownPresets();
  const menu = vx.dom.presetMenu;
  const item = (label, icon, run, extra = {}) => {
    const entry = el("sl-menu-item", extra, el("sl-icon", { slot: "prefix", library: "ph", name: icon }), label);
    entry.run = run;
    return entry;
  };
  menu.replaceChildren(
    el("sl-menu-label", {}, "Layouts"),
    ...BUILT_IN_PRESETS.map((preset) => item(preset.name, preset.icon, () => usePreset(preset.layout))),
    el("sl-divider"),
    el("sl-menu-label", {}, own.length ? "Yours (this browser)" : "Yours: none yet"),
    ...own.map((preset) => item(preset.name, "bookmark-simple", () => usePreset(preset.layout))),
    el("sl-divider"),
    item("Save this layout…", "floppy-disk", savePreset),
    ...own.length ? [el("sl-divider"), el("sl-menu-label", {}, "Delete one of yours"),
      ...own.map((preset) => item(preset.name, "trash", () => deletePreset(preset.name), { class: "danger" }))] : [],
  );
}

// ---------- undo / redo (boxes, layout, split, trim, parts) ----------

function snapshot() {
  stash();
  return JSON.stringify({ parts: vx.parts, trim: vx.trim });
}

function restore(state) {
  const data = JSON.parse(state);
  vx.parts = data.parts;
  vx.trim = data.trim;
  loadPart(partAt(vx.dom.video.currentTime || 0));
  renderAll();
}

function record(before) {
  if (before === snapshot()) return;
  vx.history.push(before);
  if (vx.history.length > HISTORY_LIMIT) vx.history.shift();
  vx.future = [];
  renderUndo();
}

function change(fn) {
  const before = snapshot();
  fn();
  closePlayback();
  renderAll();
  record(before);
}

function undo() {
  const state = vx.history.pop();
  if (!state) return;
  vx.future.push(snapshot());
  restore(state);
  renderUndo();
}

function redo() {
  const state = vx.future.pop();
  if (!state) return;
  vx.history.push(snapshot());
  restore(state);
  renderUndo();
}

function renderUndo() {
  vx.dom.undo.querySelector("sl-icon-button").disabled = !vx.history.length;
  vx.dom.redo.querySelector("sl-icon-button").disabled = !vx.future.length;
}

// ---------- building the panel ----------

function build() {
  const video = el("video", { playsinline: true, preload: "auto" });
  const stage = el("div", { class: "vx2-stage" }, video);
  const boxNodes = {};
  for (const name of ["top", "bottom"]) {
    const node = el("div", { class: `vx2-box vx2-${name}`, tabindex: "-1" },
      el("span", { class: "vx2-tag" }, name === "top" ? "Top" : "Bottom"),
      ...["nw", "ne", "sw", "se"].map((corner) => el("span", { class: `vx2-handle vx2-${corner}`, "data-corner": corner })));
    node.addEventListener("pointerdown", startBoxDrag);
    boxNodes[name] = node;
    stage.append(node);
  }
  const guides = el("div", { class: "vx2-guides" });
  stage.append(guides);
  stage.addEventListener("pointerdown", (event) => {
    if (event.target === stage || event.target === video) select(null);
  });
  const note = el("div", { class: "vx2-note", hidden: true });
  const stageWrap = el("div", { class: "vx2-stage-wrap" }, stage, note);

  const canvas = el("canvas", { width: String(OUT_W / 2), height: String(OUT_H / 2) });
  const safe = el("div", { class: "vx2-safe", hidden: true });
  const renderVideo = el("video", { class: "vx2-render", playsinline: true, controls: true, hidden: true });
  const split = el("div", { class: "vx2-split", role: "slider", tabindex: "0", "aria-label": "Top and bottom split", "aria-valuemin": "20", "aria-valuemax": "80" },
    el("span", {}));
  split.addEventListener("pointerdown", startSplitDrag);
  split.addEventListener("keydown", (event) => {
    const step = { ArrowUp: -0.02, ArrowDown: 0.02 }[event.key];
    if (!step) return;
    event.preventDefault();
    event.stopPropagation();
    change(() => setRatio(vx.ratio + step));
  });
  const back = el("sl-button", { class: "vx2-back", size: "small", hidden: true },
    el("sl-icon", { slot: "prefix", library: "ph", name: "arrow-left" }), "Back to the editor");
  back.addEventListener("click", closePlayback);
  const leftOut = el("div", { class: "vx2-left-out", hidden: true }, "Left out of the render");
  const portrait = el("div", { class: "vx2-portrait" }, canvas, safe, leftOut, split, renderVideo);
  const previewWrap = el("div", { class: "vx2-preview-wrap" }, portrait, back);

  // Bottom bar: play, time, what will be rendered, leave out before/after the playhead, volume, back to the player.
  // No trim handles: the Player 2 timeline seeks, and leaving parts out does what a trim did
  const play = iconButton({ icon: "play", label: "Play / pause", keybind: "verticalPlay", onClick: togglePlay });
  const time = el("span", { class: "num vx2-time" }, "0:00.0");
  const mute = iconButton({ icon: "speaker-high", label: "Mute the preview", onClick: toggleMute });
  const volume = el("sl-range", { class: "volume", min: "0", max: "100", tooltip: "none", "aria-label": "Preview volume" });
  volume.addEventListener("sl-input", () => {
    video.volume = Number(volume.value) / 100;
    video.muted = video.volume === 0;
    saveVolume();
    renderVolume();
  });
  const exit = iconButton({ icon: "device-mobile", label: "Back to the normal player", keybind: "verticalExit", onClick: () => vx.hooks.onExit?.() });
  exit.querySelector("sl-icon-button").toggleAttribute("data-active", true);
  const trimText = el("span", { class: "num vx2-trim-text" });
  const setStart = iconButton({ icon: "caret-line-left", label: "Leave out everything before the playhead", keybind: "verticalStart", onClick: () => leaveOutSide("before") });
  const setEnd = iconButton({ icon: "caret-line-right", label: "Leave out everything after the playhead", keybind: "verticalEnd", onClick: () => leaveOutSide("after") });
  const reset = iconButton({ icon: "arrows-out-line-horizontal", label: "Put every part back", onClick: putAllBack });
  const bar = el("div", { class: "vx2-bar player-controls" }, play, time, el("span", { class: "vx2-bar-gap" }), trimText, setStart, setEnd, reset, mute, volume, exit);

  // Side panel: layout, safe zones, snapping, undo/redo, render, renders
  const one = iconButton({ icon: "square", label: "One box", keybind: "verticalOneBox", onClick: () => change(() => { vx.mode = "single"; select(null); }) });
  const two = iconButton({ icon: "rows", label: "Two boxes (top and bottom)", keybind: "verticalTwoBoxes", onClick: () => change(() => { vx.mode = "split"; select(null); }) });
  const presetMenu = el("sl-menu");
  presetMenu.addEventListener("sl-select", (event) => event.detail.item.run?.());
  const presets = el("sl-dropdown", { hoist: true, placement: "bottom-start", distance: "4" },
    el("sl-icon-button", { slot: "trigger", library: "ph", name: "layout", label: "Layouts", title: "Layouts" }), presetMenu);
  // The menu would keep the keyboard (Space, Enter): give it back to the editor
  presets.addEventListener("sl-after-hide", (event) => {
    if (event.target === presets && vx.open && !document.querySelector("sl-dialog[open]")) takeFocus();
  });
  const safeButton = iconButton({ icon: "frame-corners", label: "Safe zones", keybind: "verticalSafeZones", onClick: cycleSafeZone });
  const snapButton = iconButton({ icon: "magnet", label: "Snapping", keybind: "verticalSnap", onClick: toggleSnap });
  const undoButton = iconButton({ icon: "arrow-u-up-left", label: "Undo", keybind: "verticalUndo", onClick: undo });
  const redoButton = iconButton({ icon: "arrow-u-up-right", label: "Redo", keybind: "verticalRedo", onClick: redo });
  const safeName = el("span", { class: "vx2-safe-name faint" });
  const splitButton = iconButton({ icon: "scissors", label: "Split into parts at the playhead", keybind: "verticalSplitPart", onClick: splitPart });
  const joinButton = iconButton({ icon: "arrows-in-line-horizontal", label: "Join with the part before", keybind: "verticalJoinPart", onClick: joinPart });
  const removeButton = iconButton({ icon: "minus-circle", label: "Leave this part out / put it back", keybind: "verticalRemovePart", onClick: toggleRemoved });
  const allButton = iconButton({ icon: "copy-simple", label: "Use this layout on every part", onClick: layoutToAllParts });
  const partInfo = el("span", { class: "vx2-part-info faint" });

  const render = el("sl-button", { variant: "primary", class: "vx2-render-button" },
    el("sl-icon", { slot: "prefix", library: "ph", name: "export" }), "Render");
  render.addEventListener("click", startRender);
  const progress = el("sl-progress-bar", { class: "vx2-progress", value: "0", hidden: true });
  const message = el("p", { class: "vx2-message faint" });
  const list = el("ul", { class: "vx2-renders" });
  const empty = el("p", { class: "faint vx2-empty" }, "No vertical versions of this clip yet.");

  const side = el("aside", { class: "vx2-side" },
    el("div", { class: "vx2-group faint" }, "Layout of this part"),
    el("div", { class: "vx2-tools", "data-tip-placement": "bottom" }, one, two, presets, el("span", { class: "tools-sep" }), safeButton, snapButton,
      el("span", { class: "tools-sep" }), undoButton, redoButton),
    el("div", { class: "vx2-group faint" }, "Parts"),
    el("div", { class: "vx2-tools", "data-tip-placement": "bottom" }, splitButton, joinButton, removeButton, allButton),
    partInfo,
    safeName,
    el("div", { class: "vx2-render-row" }, withTooltip(render, "Render the vertical video", "verticalRender"), progress),
    message,
    el("h3", { class: "vx2-heading display" }, "Vertical versions"),
    empty,
    list);

  const panel = el("div", { class: "vx2", tabindex: "-1", hidden: true }, stageWrap, previewWrap, side, bar);
  vx.dom = {
    panel, video, stage, stageWrap, boxNodes, guides, note, canvas, safe, renderVideo, split, back, portrait, previewWrap,
    play, time, trimText, mute, volume, one, two, presetMenu, safeButton, safeName,
    snapButton, undo: undoButton, redo: redoButton, render, progress, message, list, empty,
    leftOut, partInfo, joinButton, removeButton, allButton,
  };

  for (const type of ["play", "pause", "timeupdate", "durationchange"]) video.addEventListener(type, renderPlayer);
  video.addEventListener("loadedmetadata", onMetadata);
  video.addEventListener("error", () => {
    if (vx.open && video.getAttribute("src")) showNote("Couldn't load the clip's file.");
  });
  const saved = loadVolume();
  video.volume = saved.volume;
  video.muted = saved.muted;

  new ResizeObserver(layoutPanel).observe(panel);
  return panel;
}

// The stage keeps the video's shape and the portrait 9:16, each as large as its cell allows
function layoutPanel() {
  if (!vx.open) return;
  const { stage, stageWrap, portrait, previewWrap } = vx.dom;
  const wrap = stageWrap.getBoundingClientRect();
  let w = wrap.width - 16;
  let h = w / vx.videoAspect;
  if (h > wrap.height - 16) {
    h = wrap.height - 16;
    w = h * vx.videoAspect;
  }
  stage.style.width = `${Math.max(w, 0)}px`;
  stage.style.height = `${Math.max(h, 0)}px`;
  const cell = previewWrap.getBoundingClientRect();
  const ph = Math.max(cell.height - 16, 0);
  portrait.style.height = `${ph}px`;
  portrait.style.width = `${(ph * 9) / 16}px`;
}

// ---------- rendering the panel ----------

function renderAll() {
  renderBoxes();
  renderParts();
  renderTrim();
  renderSafe();
  renderUndo();
  renderMatches();
}

function renderBoxes() {
  const { boxNodes, split, one, two } = vx.dom;
  boxNodes.bottom.hidden = vx.mode === "single";
  boxNodes.top.querySelector(".vx2-tag").textContent = vx.mode === "single" ? "Crop" : "Top";
  for (const name of visibleBoxes()) {
    const box = vx.boxes[name];
    const node = name === "bottom" ? boxNodes.bottom : boxNodes.top;
    node.dataset.box = name;
    node.style.left = `${box.x * 100}%`;
    node.style.top = `${box.y * 100}%`;
    node.style.width = `${box.w * 100}%`;
    node.style.height = `${box.h * 100}%`;
    node.classList.toggle("selected", vx.selected === name);
  }
  if (vx.mode === "single") boxNodes.top.classList.toggle("selected", vx.selected === "single");
  split.hidden = vx.mode === "single" || !vx.dom.renderVideo.hidden;
  split.style.top = `${vx.ratio * 100}%`;
  split.setAttribute("aria-valuenow", String(Math.round(vx.ratio * 100)));
  one.querySelector("sl-icon-button").toggleAttribute("data-active", vx.mode === "single");
  two.querySelector("sl-icon-button").toggleAttribute("data-active", vx.mode === "split");
  vx.dom.guides.replaceChildren(...vx.guides.map((g) => el("div", {
    class: `vx2-guide ${g.axis}`,
    style: g.axis === "x" ? `left:${g.at * 100}%` : `top:${g.at * 100}%`,
  })));
  renderMatches();
}

function renderSafe() {
  const zone = SAFE_ZONES[vx.safeZone];
  const { safe, safeButton, safeName, snapButton } = vx.dom;
  safe.hidden = !zone || !vx.dom.renderVideo.hidden;
  safeButton.querySelector("sl-icon-button").toggleAttribute("data-active", Boolean(zone));
  safeName.textContent = zone ? `Safe zone: ${zone.name} (approximate)` : "";
  snapButton.querySelector("sl-icon-button").toggleAttribute("data-active", vx.snap);
  if (!zone) return;
  const pct = (v) => `${v * 100}%`;
  safe.replaceChildren(
    el("div", { class: "vx2-unsafe", style: `left:0;right:0;top:0;height:${pct(zone.top)}` }),
    el("div", { class: "vx2-unsafe", style: `left:0;right:0;bottom:0;height:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-unsafe", style: `left:0;width:${pct(zone.left)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-unsafe", style: `right:0;width:${pct(zone.right)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-safe-box", style: `left:${pct(zone.left)};right:${pct(zone.right)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }));
}

function renderParts() {
  if (!vx.dom) return;
  const { leftOut, partInfo, joinButton, removeButton, allButton } = vx.dom;
  const duration = duration_();
  const many = vx.parts.length > 1;
  sendParts(duration);
  const part = vx.parts[vx.part];
  const removed = Boolean(part?.removed);
  leftOut.hidden = !removed || !vx.dom.renderVideo.hidden;
  partInfo.textContent = !part ? "" : many
    ? `Part ${vx.part + 1} of ${vx.parts.length} · ${lookName(part)} · ${tenths(part.start)} – ${tenths(Math.min(partEnd(vx.part), duration))}${removed ? " · left out" : ""}`
    : "One part. Press S at the playhead to split the clip and give each part its own layout.";
  joinButton.querySelector("sl-icon-button").disabled = vx.part === 0;
  allButton.querySelector("sl-icon-button").disabled = !many;
  const remove = removeButton.querySelector("sl-icon-button");
  remove.name = removed ? "plus-circle" : "minus-circle";
  remove.toggleAttribute("data-active", removed);
}

const lookName = (part) => (part.mode === "single" ? "One box" : "Two boxes");

// The Player 2 timeline draws the parts on the clip (file seconds; editor.js maps them to the VOD)
function sendParts(duration) {
  if (!vx.ready || !duration) {
    vx.hooks.onParts?.(null);
    return;
  }
  vx.hooks.onParts?.({
    parts: vx.parts.map((part, i) => ({
      start: part.start, end: Math.min(partEnd(i), duration), removed: part.removed, current: i === vx.part, label: lookName(part),
    })),
    trim: { ...vx.trim },
  });
}

function draw() {
  if (!vx.open) return;
  followPlayhead();
  const { canvas, video } = vx.dom;
  const ctx = canvas.getContext("2d");
  const cw = canvas.width;
  const ch = canvas.height;
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, cw, ch);
  if (vx.ready && video.readyState >= 2) {
    const vw = video.videoWidth;
    const vh = video.videoHeight;
    const put = (box, dy, dh) => ctx.drawImage(video, box.x * vw, box.y * vh, box.w * vw, box.h * vh, 0, dy, cw, dh);
    if (vx.mode === "single") {
      put(vx.boxes.single, 0, ch);
    } else {
      const topH = Math.round(ch * vx.ratio);
      put(vx.boxes.top, 0, topH);
      put(vx.boxes.bottom, topH, ch - topH);
    }
  }
  keepInTrim();
  vx.frame = requestAnimationFrame(draw);
}

function showNote(text, action = null) {
  vx.dom.note.hidden = !text;
  vx.dom.note.replaceChildren(...(text ? [el("p", {}, text), action].filter(Boolean) : []));
}

// ---------- boxes: drag, resize, snap, select ----------

function pointer(event, element) {
  const rect = element.getBoundingClientRect();
  return { x: (event.clientX - rect.left) / rect.width, y: (event.clientY - rect.top) / rect.height };
}

function select(name) {
  vx.selected = name;
  renderBoxes();
}

// The closest snap target within SNAP_PX of any of the edges, or null
function snapAxis(edges, targets, threshold) {
  let best = null;
  for (const edge of edges) {
    for (const target of targets) {
      const delta = target - edge;
      if (Math.abs(delta) <= threshold && (!best || Math.abs(delta) < Math.abs(best.delta))) best = { delta, at: target };
    }
  }
  return best;
}

function snapTargets(name, axis) {
  const targets = [0, 0.5, 1];
  for (const other of visibleBoxes()) {
    if (other === name) continue;
    const box = vx.boxes[other];
    if (axis === "x") targets.push(box.x, box.x + box.w / 2, box.x + box.w);
    else targets.push(box.y, box.y + box.h / 2, box.y + box.h);
  }
  return targets;
}

function startBoxDrag(event) {
  if (event.button !== 0) return;
  closePlayback();
  const node = event.currentTarget;
  const name = node.dataset.box;
  select(name);
  node.focus({ preventScroll: true });
  const box = { ...vx.boxes[name] };
  const stage = vx.dom.stage;
  const rect = stage.getBoundingClientRect();
  const tx = SNAP_PX / rect.width;
  const ty = SNAP_PX / rect.height;
  const start = pointer(event, stage);
  const corner = event.target.dataset.corner;
  const before = snapshot();
  event.preventDefault();
  event.stopPropagation();
  node.setPointerCapture(event.pointerId);
  const anchor = corner && {
    x: corner.includes("w") ? box.x + box.w : box.x,
    y: corner.includes("n") ? box.y + box.h : box.y,
  };
  const onMove = (moveEvent) => {
    const p = pointer(moveEvent, stage);
    const snapping = vx.snap && !moveEvent.altKey;
    vx.guides = [];
    if (!corner) {
      let x = clamp(box.x + p.x - start.x, 0, 1 - box.w);
      let y = clamp(box.y + p.y - start.y, 0, 1 - box.h);
      if (snapping) {
        const sx = snapAxis([x, x + box.w / 2, x + box.w], snapTargets(name, "x"), tx);
        const sy = snapAxis([y, y + box.h / 2, y + box.h], snapTargets(name, "y"), ty);
        if (sx) { x = clamp(x + sx.delta, 0, 1 - box.w); vx.guides.push({ axis: "x", at: sx.at }); }
        if (sy) { y = clamp(y + sy.delta, 0, 1 - box.h); vx.guides.push({ axis: "y", at: sy.at }); }
      }
      vx.boxes[name] = { ...box, x, y };
    } else {
      const k = vx.videoAspect / slotAspect(name);
      const left = corner.includes("w");
      const up = corner.includes("n");
      const maxW = Math.min(left ? anchor.x : 1 - anchor.x, (up ? anchor.y : 1 - anchor.y) / k);
      let w = Math.max(Math.abs(p.x - anchor.x), Math.abs(p.y - anchor.y) / k);
      if (snapping) {
        // The moving side edge sticks to a target; the height follows the slot's shape
        const edgeX = left ? anchor.x - w : anchor.x + w;
        const sx = snapAxis([edgeX], snapTargets(name, "x"), tx);
        if (sx) {
          w = Math.abs(sx.at - anchor.x);
          vx.guides.push({ axis: "x", at: sx.at });
        }
      }
      w = clamp(w, MIN_BOX, maxW);
      const h = w * k;
      vx.boxes[name] = { x: left ? anchor.x - w : anchor.x, y: up ? anchor.y - h : anchor.y, w, h };
    }
    renderBoxes();
  };
  vx.dragging = true;
  const onUp = () => {
    vx.dragging = false;
    node.removeEventListener("pointermove", onMove);
    node.removeEventListener("pointerup", onUp);
    node.removeEventListener("pointercancel", onUp);
    vx.guides = [];
    renderBoxes();
    record(before);
  };
  node.addEventListener("pointermove", onMove);
  node.addEventListener("pointerup", onUp);
  node.addEventListener("pointercancel", onUp);
}

function nudge(dx, dy) {
  const name = vx.selected;
  if (!name || !visibleBoxes().includes(name)) return;
  change(() => {
    const box = vx.boxes[name];
    vx.boxes[name] = { ...box, x: clamp(box.x + dx, 0, 1 - box.w), y: clamp(box.y + dy, 0, 1 - box.h) };
  });
}

function setRatio(ratio) {
  vx.ratio = clamp(ratio, MIN_RATIO, MAX_RATIO);
  vx.boxes.top = fit("top", vx.boxes.top);
  vx.boxes.bottom = fit("bottom", vx.boxes.bottom);
}

function startSplitDrag(event) {
  const handle = event.currentTarget;
  const before = snapshot();
  event.preventDefault();
  handle.setPointerCapture(event.pointerId);
  const onMove = (moveEvent) => {
    setRatio(pointer(moveEvent, vx.dom.portrait).y);
    renderBoxes();
  };
  const onUp = () => {
    handle.removeEventListener("pointermove", onMove);
    handle.removeEventListener("pointerup", onUp);
    handle.removeEventListener("pointercancel", onUp);
    record(before);
  };
  handle.addEventListener("pointermove", onMove);
  handle.addEventListener("pointerup", onUp);
  handle.addEventListener("pointercancel", onUp);
}

function toggleSnap() {
  vx.snap = !vx.snap;
  try {
    localStorage.setItem(SNAP_KEY, vx.snap ? "on" : "off");
  } catch {
    // Only for this visit
  }
  renderSafe();
  toast(vx.snap ? "Snapping on: boxes stick to the edges, the middle and each other (hold Alt to drag freely)." : "Snapping off.");
}

function cycleSafeZone() {
  vx.safeZone = SAFE_ORDER[(SAFE_ORDER.indexOf(vx.safeZone) + 1) % SAFE_ORDER.length];
  saveSetting(SAFE_KEY, vx.safeZone);
  renderSafe();
}

// ---------- trim (seconds into the clip's file) ----------

function duration_() {
  const d = vx.dom?.video.duration;
  return Number.isFinite(d) && d > 0 ? d : 0;
}

function setTrim(start, end) {
  const duration = duration_();
  if (!duration) {
    vx.trim = { start: 0, end: 0 };
    return;
  }
  let s = clamp(start || 0, 0, Math.max(duration - MIN_TRIM, 0));
  const e = clamp(Number.isFinite(end) ? end : duration, s + MIN_TRIM, duration);
  if (e - s < MIN_TRIM) s = Math.max(e - MIN_TRIM, 0);
  vx.trim = { start: s, end: e };
}

function isTrimmed() {
  const duration = duration_();
  return duration > 0 && (vx.trim.start > 0.05 || vx.trim.end < duration - 0.05);
}

function tenths(seconds) {
  const t = Math.round(Math.max(seconds, 0) * 10);
  const m = Math.floor(t / 600);
  return `${m}:${((t % 600) / 10).toFixed(1).padStart(4, "0")}`;
}

function renderTrim() {
  const duration = duration_();
  const kept = duration ? keptSeconds() : 0;
  vx.dom.trimText.textContent = duration ? `${tenths(kept)}${kept < duration - 0.05 ? " of " + tenths(duration) : ""}` : "";
  vx.dom.trimText.title = duration ? "Length of the vertical video (parts left out don't count)" : "";
  renderPlayer();
  renderMatches();
  renderParts();
}

// I / O: leave out everything before / after the playhead (a split there, the parts on that side left out)
function leaveOutSide(side) {
  if (!vx.ready) return;
  const t = vx.dom.video.currentTime;
  const duration = duration_();
  if ((side === "before" && t < MIN_PART) || (side === "after" && duration - t < MIN_PART)) {
    toast(`Too close to the ${side === "before" ? "start" : "end"} of the clip.`);
    return;
  }
  const before = snapshot();
  cutAt(t);
  const keep = vx.parts.some((part, i) => !part.removed && (side === "before" ? part.start >= t - 0.01 : partEnd(i) <= t + 0.01));
  if (!keep) {
    restore(before);
    toast("That would leave nothing to render.");
    return;
  }
  vx.parts.forEach((part, i) => {
    if (side === "before" ? partEnd(i) <= t + 0.01 : part.start >= t - 0.01) part.removed = true;
  });
  loadPart(partAt(t));
  closePlayback();
  renderAll();
  record(before);
}

function putAllBack() {
  if (!vx.parts.some((part) => part.removed)) return;
  change(() => {
    for (const part of vx.parts) part.removed = false;
    setTrim(0, Infinity);
  });
}

// A split at t unless one is (nearly) there; parts shorter than MIN_PART aren't made
function cutAt(t) {
  stash();
  const i = partAt(t);
  if (t - vx.parts[i].start < MIN_PART || partEnd(i) - t < MIN_PART) return;
  vx.parts.splice(i + 1, 0, { ...structuredClone(vx.parts[i]), start: t });
  loadPart(partAt(vx.dom.video.currentTime || 0));
}

// A render's trim (older renders, or a part list's first start / last end) becomes left-out parts at the ends
function trimToParts(start, end) {
  const duration = duration_();
  if (start > 0.05) {
    cutAt(start);
    vx.parts.forEach((part, i) => { if (partEnd(i) <= start + 0.01) part.removed = true; });
  }
  if (Number.isFinite(end) && end < duration - 0.05) {
    cutAt(end);
    vx.parts.forEach((part) => { if (part.start >= end - 0.01) part.removed = true; });
  }
  setTrim(0, Infinity);
  loadPart(partAt(vx.dom?.video.currentTime || 0));
}

// ---------- playback ----------

// Playback stays inside the trim and skips parts left out, looping at the end
function keepInTrim() {
  const video = vx.dom.video;
  if (!vx.ready || !duration_() || video.paused) return;
  const t = video.currentTime;
  const next = playable(t < vx.trim.start - 0.25 ? vx.trim.start : t) ?? playable(vx.trim.start) ?? vx.trim.start;
  if (next - t > 0.05 || next < t - 0.05) video.currentTime = next;
}

function togglePlay() {
  if (!vx.ready) return;
  closePlayback();
  const video = vx.dom.video;
  if (video.paused) {
    const t = video.currentTime;
    const from = t < vx.trim.start || t >= vx.trim.end - 0.05 ? vx.trim.start : t;
    const next = playable(from) ?? playable(vx.trim.start) ?? vx.trim.start;
    if (Math.abs(next - t) > 0.01) video.currentTime = next;
    video.play().catch(() => {});
  } else {
    video.pause();
  }
}

function seek(seconds) {
  if (!vx.ready) return;
  closePlayback();
  vx.dom.video.currentTime = clamp(seconds, 0, duration_());
}

function renderPlayer() {
  if (!vx.dom) return;
  const { video, play, time } = vx.dom;
  play.querySelector("sl-icon-button").name = video.paused ? "play" : "pause";
  time.textContent = `${tenths(video.currentTime || 0)} / ${tenths(duration_())}`;
}

function loadVolume() {
  const saved = loadSetting(VOLUME_KEY, null);
  if (saved && Number.isFinite(saved.volume)) return { volume: clamp(saved.volume, 0, 1), muted: Boolean(saved.muted) };
  return { volume: 0.3, muted: false };
}

function saveVolume() {
  const video = vx.dom.video;
  saveSetting(VOLUME_KEY, { volume: video.volume, muted: video.muted });
}

function toggleMute() {
  const video = vx.dom.video;
  video.muted = !video.muted;
  if (!video.muted && video.volume === 0) video.volume = 0.3;
  saveVolume();
  renderVolume();
}

function renderVolume() {
  const { video, volume, mute } = vx.dom;
  const silent = video.muted || video.volume === 0;
  volume.value = Math.round((video.muted ? 0 : video.volume) * 100);
  mute.querySelector("sl-icon-button").name = silent ? "speaker-slash" : "speaker-high";
}

// A finished render plays in the portrait frame
function playRender(render) {
  const { renderVideo, video, back } = vx.dom;
  video.pause();
  renderVideo.dataset.renderId = render.id;
  renderVideo.src = `${render.fileUrl}${render.fileUrl.includes("?") ? "&" : "?"}inline=true`;
  renderVideo.volume = video.volume;
  renderVideo.muted = video.muted;
  renderVideo.hidden = false;
  back.hidden = false;
  renderBoxes();
  renderSafe();
  renderParts();
  renderVideo.play().catch(() => {});
}

function closePlayback() {
  const { renderVideo, back } = vx.dom ?? {};
  if (!renderVideo || renderVideo.hidden) return;
  renderVideo.pause();
  renderVideo.removeAttribute("src");
  renderVideo.load();
  delete renderVideo.dataset.renderId;
  renderVideo.hidden = true;
  back.hidden = true;
  renderBoxes();
  renderSafe();
  renderParts();
}

// ---------- renders ----------

async function poll() {
  if (!vx.open || !vx.entry) return;
  const entryId = vx.entry.id;
  try {
    const state = await api("GET", `/api/entries/${entryId}/shortform`);
    if (vx.entry?.id === entryId) renderState(state);
  } catch (error) {
    if (vx.entry?.id === entryId) vx.dom.message.textContent = error.message;
  }
}

function renderState(state) {
  const { render, progress, message } = vx.dom;
  const busy = state.status === "Rendering";
  const finished = vx.wasRendering && !busy && state.status !== "Failed";
  vx.wasRendering = busy;
  render.disabled = busy || !can("MakeVertical") || Boolean(me?.support) || !vx.ready;
  render.loading = busy;
  progress.hidden = !busy;
  progress.value = Math.round((state.progress || 0) * 100);
  if (busy) message.textContent = `Rendering… ${Math.round((state.progress || 0) * 100)}%`;
  else if (state.status === "Failed") message.textContent = state.error || "Rendering failed.";
  else if (finished) message.textContent = "The vertical video is ready: it's at the top of the list.";
  else if (!can("MakeVertical")) message.textContent = "Your role can't render vertical videos.";
  else message.textContent = "";
  message.classList.toggle("error", state.status === "Failed");
  vx.renders = state.renders || [];
  renderRenders(finished ? vx.renders[0]?.id : null);
  clearTimeout(vx.pollTimer);
  if (busy && vx.open) vx.pollTimer = setTimeout(poll, 1500);
}

function renderLength(layout) {
  if (layout.parts?.length > 1) return `${layout.parts.length} parts, ${tenths(layout.parts.reduce((sum, p) => sum + p.end - p.start, 0))}`;
  if (layout.trimStart == null && layout.trimEnd == null) return "whole clip";
  const start = layout.trimStart ?? 0;
  const end = layout.trimEnd ?? (duration_() || start);
  return `${tenths(start)} – ${tenths(end)}`;
}

function formatSize(bytes) {
  if (!bytes) return "";
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`;
}

function renderRenders(newId) {
  const { list, empty } = vx.dom;
  empty.hidden = vx.renders.length > 0;
  list.replaceChildren(...vx.renders.map((r) => {
    const play = () => playRender(r);
    const thumb = el("button", { type: "button", class: "vx2-thumb", "aria-label": "Play this version", title: "Play this version" },
      r.thumbnailUrl ? el("img", { src: r.thumbnailUrl, alt: "", loading: "lazy" }) : el("sl-icon", { library: "ph", name: "play" }));
    thumb.addEventListener("click", play);
    const download = el("a", { href: r.fileUrl, download: "", class: "vx2-icon-link", "aria-label": "Download" },
      el("sl-icon", { library: "ph", name: "download-simple" }));
    const item = el("li", { class: r.id === newId ? "vx2-render-item new" : "vx2-render-item" },
      thumb,
      el("div", { class: "vx2-render-body" },
        el("div", { class: "vx2-render-title" }, formatDate(r.createdAtUtc), el("span", { class: "vx2-match", hidden: true }, "Matches")),
        el("div", { class: "faint" }, `${r.createdBy ?? "Someone"} · ${r.layout.parts?.length > 1 ? "" : (r.layout.mode === "split" ? "two boxes · " : "one box · ")}${renderLength(r.layout)}`),
        el("div", { class: "faint" }, `${formatSize(r.sizeBytes)} · until ${formatDate(r.expiresAtUtc)}`)),
      el("div", { class: "vx2-render-actions" },
        withTooltip(download, "Download"),
        iconButton({ icon: "sliders-horizontal", label: "Use these settings", onClick: () => change(() => applyLayout(r.layout)) }),
        can("DeleteRenders") && !me?.support ? iconButton({ icon: "trash", label: "Delete", onClick: () => deleteRender(r) }) : null));
    item.layout = r.layout;
    return item;
  }));
  renderMatches();
}

// The render made from exactly what's on screen gets a "Matches" badge, so a stale file isn't taken for the new one
function renderMatches() {
  if (!vx.ready || !vx.dom) return;
  const current = currentLayout();
  for (const item of vx.dom.list.children) item.querySelector(".vx2-match").hidden = !sameLayout(item.layout, current);
}

async function deleteRender(render) {
  if (!await confirmAction({ heading: "Delete vertical version", text: `Delete the vertical version from ${formatDate(render.createdAtUtc)}? This can't be undone.`, confirm: "Delete" })) return;
  try {
    await api("DELETE", `/api/entries/${vx.entry.id}/shortform/${render.id}`);
  } catch (error) {
    toast(error.message, "error");
  }
  if (vx.dom.renderVideo.dataset.renderId === render.id) closePlayback();
  await poll();
}

async function startRender() {
  if (!vx.ready || !vx.entry || vx.dom.render.disabled) return;
  const layout = currentLayout();
  closePlayback();
  vx.dom.render.disabled = true;
  try {
    renderState(await api("POST", `/api/entries/${vx.entry.id}/shortform`, layout));
  } catch (error) {
    vx.dom.render.disabled = false;
    vx.dom.message.textContent = error.message;
    vx.dom.message.classList.add("error");
  }
}

// ---------- the clip's file ----------

// What makes the file change: a new cut or a new download
function fileKey(entry) {
  return JSON.stringify([entry.status, entry.generationStatus, entry.startTime, entry.endTime, entry.fileSizeBytes, entry.generatedFileSizeBytes]);
}

function hasFile(entry) {
  return Boolean(entry?.canDownload);
}

function loadFile() {
  const { video } = vx.dom;
  vx.ready = false;
  video.pause();
  if (!hasFile(vx.entry)) {
    video.removeAttribute("src");
    video.load();
    explainNoFile();
    return;
  }
  showNote("Loading the clip…");
  video.src = `/api/entries/${vx.entry.id}/file?preview=true&v=${encodeURIComponent(vx.entryKey.length + ":" + Date.now())}`;
}

function explainNoFile() {
  const entry = vx.entry;
  const busy = entry.status === "Queued" || entry.status === "Downloading" || entry.generationStatus === "Queued" || entry.generationStatus === "Generating";
  if (busy) {
    showNote("The clip's file is being made. The editor opens as soon as it's ready.");
  } else if (entry.status === "ReferenceOnly" && can("GenerateFiles") && !me?.support) {
    const button = el("sl-button", { size: "small" }, el("sl-icon", { slot: "prefix", library: "ph", name: "file-video" }), "Make the file");
    button.addEventListener("click", async () => {
      button.loading = true;
      try {
        await api("POST", `/api/entries/${entry.id}/generate`);
        await refreshEntry();
      } catch (error) {
        toast(error.message, "error");
        button.loading = false;
      }
    });
    showNote("This long clip has no file yet. The vertical editor works on the clip's file.", button);
  } else if (entry.status === "Failed") {
    showNote("The clip's download failed, so there's no file to work on.");
  } else if (entry.status === "VodUnavailable") {
    showNote("The VOD was deleted, so this clip has no file.");
  } else {
    showNote("This clip has no file yet. The vertical editor works on the clip's file.");
  }
  scheduleEntryCheck(busy);
}

function scheduleEntryCheck(busy) {
  clearTimeout(vx.entryTimer);
  if (busy && vx.open) vx.entryTimer = setTimeout(refreshEntry, 3000);
}

// Re-read the clip; a new file (re-cut, finished download) reloads the video
async function refreshEntry() {
  if (!vx.open || !vx.entry) return;
  const id = vx.entry.id;
  let entry;
  try {
    entry = await api("GET", `/api/entries/${id}`);
  } catch (error) {
    if (vx.entry?.id === id) showNote(error.status === 404 ? "This clip was deleted." : error.message);
    return;
  }
  if (!vx.open || vx.entry?.id !== id) return;
  vx.entry = entry;
  const key = fileKey(entry);
  if (key === vx.entryKey && (vx.ready || !hasFile(entry))) {
    if (!hasFile(entry)) explainNoFile();
    return;
  }
  vx.entryKey = key;
  loadFile();
}

function onMetadata() {
  const video = vx.dom.video;
  // savedLayout is undefined until the clip's renders are read, then the newest one's layout or null (none yet)
  const first = !vx.laidOut;
  vx.videoAspect = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight : 16 / 9;
  if (first) {
    // The first time: the newest render's layout (the editor reopens where it was), else the defaults
    applyLayout(vx.savedLayout ?? null);
    vx.laidOut = true;
  } else {
    // A new file (the clip was changed): keep the boxes, fit the trim into the new length
    stash();
    for (const part of vx.parts) {
      vx.ratio = part.ratio;
      for (const name of ["single", "top", "bottom"]) part.boxes[name] = fit(name, part.boxes[name]);
    }
    setTrim(vx.trim.start, vx.trim.end >= duration_() - 0.05 ? Infinity : vx.trim.end);
    fitParts();
  }
  vx.ready = true;
  showNote("");
  layoutPanel();
  renderAll();
  renderState({ status: vx.wasRendering ? "Rendering" : "None", renders: vx.renders });
  poll();
}

// The editor's keys work at once (unless the user is typing somewhere)
function takeFocus() {
  const active = document.activeElement;
  if (active && /^(INPUT|TEXTAREA|SL-INPUT|SL-TEXTAREA)$/.test(active.tagName)) return;
  vx.dom.panel.focus({ preventScroll: true });
}

// ---------- open / close ----------

export function setUpVertical(host, hooks) {
  vx.host = host;
  vx.hooks = hooks;
  const panel = build();
  host.querySelector(".player-screen").after(panel);
  vx.snap = (() => {
    try {
      return localStorage.getItem(SNAP_KEY) !== "off";
    } catch {
      return true;
    }
  })();
  vx.safeZone = SAFE_ZONES[loadSetting(SAFE_KEY, "none")] !== undefined ? loadSetting(SAFE_KEY, "none") : "none";
}

export function verticalOpen() {
  return vx.open;
}

export function verticalEntryId() {
  return vx.open ? vx.entry?.id ?? null : null;
}

// entryId: the clip in Player 2
export async function openVertical(entryId) {
  if (vx.open && vx.entry?.id === entryId) return;
  closeVertical();
  vx.open = true;
  vx.entry = { id: entryId };
  vx.entryKey = "";
  vx.ready = false;
  vx.renders = [];
  vx.history = [];
  vx.future = [];
  vx.selected = null;
  vx.wasRendering = false;
  vx.savedLayout = undefined;
  vx.laidOut = false;
  vx.host.classList.add("vertical");
  vx.dom.panel.hidden = false;
  vx.dom.message.textContent = "";
  renderPresetMenu();
  renderRenders(null);
  applyLayout(null, { trim: false });
  renderAll();
  renderVolume();
  showNote("Loading the clip…");
  layoutPanel();
  takeFocus();
  cancelAnimationFrame(vx.frame);
  vx.frame = requestAnimationFrame(draw);

  // The newest render's layout opens the editor where the last render left it
  let state = null;
  try {
    [vx.entry, state] = await Promise.all([
      api("GET", `/api/entries/${entryId}`),
      api("GET", `/api/entries/${entryId}/shortform`).catch(() => null),
    ]);
  } catch (error) {
    if (vx.open) showNote(error.message);
    return;
  }
  if (!vx.open || vx.entry.id !== entryId) return;
  vx.savedLayout = state?.layout ?? null;
  if (state) {
    vx.renders = state.renders || [];
    vx.wasRendering = state.status === "Rendering";
    renderState(state);
  }
  vx.entryKey = fileKey(vx.entry);
  loadFile();
}

export function closeVertical() {
  if (!vx.open) return;
  vx.open = false;
  closePlayback();
  const video = vx.dom.video;
  video.pause();
  video.removeAttribute("src");
  video.load();
  clearTimeout(vx.pollTimer);
  clearTimeout(vx.entryTimer);
  cancelAnimationFrame(vx.frame);
  vx.entry = null;
  vx.ready = false;
  vx.hooks.onParts?.(null);
  vx.host.classList.remove("vertical");
  vx.dom.panel.hidden = true;
}

// Seconds into the clip's file shown in the editor, or null
export function verticalTime() {
  return vx.open && vx.ready ? vx.dom.video.currentTime : null;
}

export function verticalSeek(seconds) {
  seek(seconds);
}

export function pauseVertical() {
  if (!vx.open) return;
  vx.dom.video.pause();
  vx.dom.renderVideo.pause();
}

// The clip in Player 2 changed (trimmed, cut, renamed): its file may be new
export function verticalEntryChanged() {
  if (!vx.open) return;
  clearTimeout(vx.entryTimer);
  vx.entryTimer = setTimeout(refreshEntry, 400);
}

// Keys while the vertical editor is open (keybinds.js scopes "vertical" and "vertical-box"); true when handled
export function handleVerticalKey(event) {
  if (!vx.open) return false;
  // A selected box takes the arrow keys (a focused trim handle or split pill handles its own)
  const id = (vx.selected ? keybindFor("vertical-box", event) : null) ?? keybindFor("vertical", event);
  if (!id) return false;
  const big = event.shiftKey ? 0.05 : 0.005;
  const arrows = { ArrowLeft: [-big, 0], ArrowRight: [big, 0], ArrowUp: [0, -big], ArrowDown: [0, big] };
  const run = {
    verticalPlay: togglePlay,
    verticalStart: () => leaveOutSide("before"),
    verticalEnd: () => leaveOutSide("after"),
    verticalOneBox: () => change(() => { vx.mode = "single"; select(null); }),
    verticalTwoBoxes: () => change(() => { vx.mode = "split"; select(null); }),
    verticalSafeZones: cycleSafeZone,
    verticalSnap: toggleSnap,
    verticalUndo: undo,
    verticalRedo: redo,
    verticalRender: startRender,
    verticalSplitPart: splitPart,
    verticalJoinPart: joinPart,
    verticalRemovePart: toggleRemoved,
    verticalExit: () => vx.hooks.onExit?.(),
    verticalNudge: () => nudge(...arrows[event.key]),
    verticalNudgeBig: () => nudge(...arrows[event.key]),
  }[id];
  if (!run) return false;
  event.preventDefault();
  run();
  return true;
}
