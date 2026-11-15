// Vertical (9:16) editor in Player 2 (frontend rework, step 9). The old page's editor (one box or two stacked boxes cropped
// from the landscape video, a trim bar, renders list) plus layout presets, safe zones, snapping, undo/redo and keys.
//
// It works on the clip's file (/api/entries/{id}/file?preview=true), like the server's render: the file already leaves out
// the clip's cut-out parts, so trim times are seconds into the file. Boxes are fractions (0..1) of the video's width and
// height, and each keeps the shape of the portrait slot it fills, so nothing is stretched. The server renders the same
// layout with ffmpeg (POST /api/entries/{id}/shortform).

import { el, api, can, me, loadSetting, saveSetting, toast, formatDate, iconButton, withTooltip } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";

const OUT_W = 1080;
const OUT_H = 1920;
const MIN_RATIO = 0.2;
const MAX_RATIO = 0.8;
const MIN_BOX = 0.05;
const MIN_TRIM = 1;          // seconds, the server's minimum
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
  videoAspect: 16 / 9,
  ready: false,
  savedLayout: undefined, // the newest render's layout, applied once the video's size is known
  frame: 0,
  pollTimer: null,
  entryTimer: null,
  wasRendering: false,
  renders: [],
  selected: null,     // "single" | "top" | "bottom"
  guides: [],
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

function applyLayout(layout, { trim = true } = {}) {
  vx.boxes = defaultBoxes();
  vx.mode = "split";
  if (trim) setTrim(layout?.trimStart ?? 0, layout?.trimEnd ?? Infinity);
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

function currentLayout() {
  const layout = vx.mode === "single"
    ? { mode: "single", top: toServer(vx.boxes.single) }
    : { mode: "split", top: toServer(vx.boxes.top), bottom: toServer(vx.boxes.bottom), splitRatio: vx.ratio };
  // Null = from the start / to the end, so an untrimmed render covers the whole file
  const duration = duration_();
  layout.trimStart = vx.trim.start > 0.05 ? Math.round(vx.trim.start * 1000) / 1000 : null;
  layout.trimEnd = duration && vx.trim.end < duration - 0.05 ? Math.round(vx.trim.end * 1000) / 1000 : null;
  return layout;
}

function sameLayout(a, b) {
  if (!a || !b || a.mode !== b.mode) return false;
  const close = (x, y) => Math.abs((x ?? 0) - (y ?? 0)) < 0.0005;
  const sameBox = (p, q) => (!p && !q) || (p && q && close(p.x, q.x) && close(p.y, q.y) && close(p.width, q.width) && close(p.height, q.height));
  const sameTime = (x, y) => (x == null && y == null) || (x != null && y != null && Math.abs(x - y) < 0.05);
  if (!sameTime(a.trimStart, b.trimStart) || !sameTime(a.trimEnd, b.trimEnd)) return false;
  return sameBox(a.top, b.top) && (a.mode === "single" || (sameBox(a.bottom, b.bottom) && close(a.splitRatio, b.splitRatio)));
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
function usePreset(layout) {
  change(() => applyLayout(typeof layout === "function" ? layout() : layout, { trim: false }));
}

async function savePreset() {
  const name = await askText({ heading: "Save this layout", label: "Name", confirm: "Save", maxLength: 40 });
  if (!name) return;
  const { trimStart, trimEnd, ...layout } = currentLayout();
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

// ---------- undo / redo (boxes, layout, split, trim) ----------

function snapshot() {
  return JSON.stringify({ mode: vx.mode, ratio: vx.ratio, boxes: vx.boxes, trim: vx.trim });
}

function restore(state) {
  const data = JSON.parse(state);
  Object.assign(vx, { mode: data.mode, ratio: data.ratio, boxes: data.boxes, trim: data.trim });
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
  const portrait = el("div", { class: "vx2-portrait" }, canvas, safe, split, renderVideo);
  const previewWrap = el("div", { class: "vx2-preview-wrap" }, portrait, back);

  // Bottom bar: play, time, trim, start/end at the playhead, volume, back to the player
  const play = iconButton({ icon: "play", label: "Play / pause", keybind: "verticalPlay", onClick: togglePlay });
  const time = el("span", { class: "num vx2-time" }, "0:00.0");
  const range = el("div", { class: "vx2-trim-range" });
  const head = el("div", { class: "vx2-trim-head" });
  const startHandle = el("div", { class: "vx2-trim-handle", tabindex: "0", role: "slider", "aria-label": "Render start", "data-edge": "start" });
  const endHandle = el("div", { class: "vx2-trim-handle", tabindex: "0", role: "slider", "aria-label": "Render end", "data-edge": "end" });
  const trim = el("div", { class: "vx2-trim" }, range, head, startHandle, endHandle);
  trim.addEventListener("pointerdown", (event) => {
    if (event.target.classList.contains("vx2-trim-handle")) return;
    const rect = trim.getBoundingClientRect();
    seek(clamp((event.clientX - rect.left) / rect.width, 0, 1) * duration_());
  });
  for (const handle of [startHandle, endHandle]) {
    handle.addEventListener("pointerdown", startTrimDrag);
    handle.addEventListener("keydown", (event) => {
      const step = { ArrowLeft: -0.1, ArrowRight: 0.1 }[event.key];
      if (!step) return;
      event.preventDefault();
      event.stopPropagation();
      const delta = event.shiftKey ? step * 10 : step;
      change(() => {
        if (handle.dataset.edge === "start") setTrim(Math.min(vx.trim.start + delta, vx.trim.end - MIN_TRIM), vx.trim.end);
        else setTrim(vx.trim.start, Math.max(vx.trim.end + delta, vx.trim.start + MIN_TRIM));
      });
    });
  }
  const trimText = el("span", { class: "num vx2-trim-text" });
  const setStart = iconButton({ icon: "caret-line-left", label: "Start the render at the playhead", keybind: "verticalStart", onClick: () => trimAtPlayhead("start") });
  const setEnd = iconButton({ icon: "caret-line-right", label: "End the render at the playhead", keybind: "verticalEnd", onClick: () => trimAtPlayhead("end") });
  const reset = iconButton({ icon: "arrows-out-line-horizontal", label: "Render the whole clip", onClick: () => change(() => setTrim(0, Infinity)) });
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
  const bar = el("div", { class: "vx2-bar player-controls" }, play, time, trim, trimText, setStart, setEnd, reset, mute, volume, exit);

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

  const render = el("sl-button", { variant: "primary", class: "vx2-render-button" },
    el("sl-icon", { slot: "prefix", library: "ph", name: "export" }), "Render");
  render.addEventListener("click", startRender);
  const progress = el("sl-progress-bar", { class: "vx2-progress", value: "0", hidden: true });
  const message = el("p", { class: "vx2-message faint" });
  const list = el("ul", { class: "vx2-renders" });
  const empty = el("p", { class: "faint vx2-empty" }, "No vertical versions of this clip yet.");

  const side = el("aside", { class: "vx2-side" },
    el("div", { class: "vx2-tools", "data-tip-placement": "bottom" }, one, two, presets, el("span", { class: "tools-sep" }), safeButton, snapButton,
      el("span", { class: "tools-sep" }), undoButton, redoButton),
    safeName,
    el("div", { class: "vx2-render-row" }, withTooltip(render, "Render the vertical video", "verticalRender"), progress),
    message,
    el("h3", { class: "vx2-heading display" }, "Vertical versions"),
    empty,
    list);

  const panel = el("div", { class: "vx2", tabindex: "-1", hidden: true }, stageWrap, previewWrap, side, bar);
  vx.dom = {
    panel, video, stage, stageWrap, boxNodes, guides, note, canvas, safe, renderVideo, split, back, portrait, previewWrap,
    play, time, trim, range, head, startHandle, endHandle, trimText, mute, volume, one, two, presetMenu, safeButton, safeName,
    snapButton, undo: undoButton, redo: redoButton, render, progress, message, list, empty,
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

function draw() {
  if (!vx.open) return;
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
  const onUp = () => {
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
  const { range, startHandle, endHandle, trimText } = vx.dom;
  const duration = duration_();
  const pct = (t) => (duration ? (t / duration) * 100 : 0);
  range.style.left = `${pct(vx.trim.start)}%`;
  range.style.width = `${pct(vx.trim.end - vx.trim.start)}%`;
  startHandle.style.left = `${pct(vx.trim.start)}%`;
  endHandle.style.left = `${pct(vx.trim.end)}%`;
  startHandle.setAttribute("aria-valuetext", tenths(vx.trim.start));
  endHandle.setAttribute("aria-valuetext", tenths(vx.trim.end));
  trimText.textContent = duration ? `${tenths(vx.trim.end - vx.trim.start)}${isTrimmed() ? " of " + tenths(duration) : ""}` : "";
  trimText.title = duration ? `Renders ${tenths(vx.trim.start)} – ${tenths(vx.trim.end)} of the clip` : "";
  renderPlayer();
  renderMatches();
}

function startTrimDrag(event) {
  const handle = event.currentTarget;
  const edge = handle.dataset.edge;
  const before = snapshot();
  event.preventDefault();
  event.stopPropagation();
  handle.setPointerCapture(event.pointerId);
  const video = vx.dom.video;
  video.pause();
  const onMove = (moveEvent) => {
    const t = clamp(pointer(moveEvent, vx.dom.trim).x, 0, 1) * duration_();
    if (edge === "start") setTrim(Math.min(t, vx.trim.end - MIN_TRIM), vx.trim.end);
    else setTrim(vx.trim.start, Math.max(t, vx.trim.start + MIN_TRIM));
    video.currentTime = edge === "start" ? vx.trim.start : Math.max(vx.trim.end - 0.05, 0);
    renderTrim();
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

function trimAtPlayhead(edge) {
  if (!vx.ready) return;
  const t = vx.dom.video.currentTime;
  change(() => {
    // Moving one edge past the other pushes it out, keeping at least the minimum length
    if (edge === "start") setTrim(t, Math.max(vx.trim.end, t + MIN_TRIM));
    else setTrim(Math.min(vx.trim.start, t - MIN_TRIM), t);
  });
}

// ---------- playback ----------

function keepInTrim() {
  const video = vx.dom.video;
  if (!vx.ready || !duration_() || video.paused) return;
  if (video.currentTime >= vx.trim.end || video.currentTime < vx.trim.start - 0.25) video.currentTime = vx.trim.start;
}

function togglePlay() {
  if (!vx.ready) return;
  closePlayback();
  const video = vx.dom.video;
  if (video.paused) {
    if (video.currentTime < vx.trim.start || video.currentTime >= vx.trim.end - 0.05) video.currentTime = vx.trim.start;
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
  const { video, play, time, head } = vx.dom;
  play.querySelector("sl-icon-button").name = video.paused ? "play" : "pause";
  time.textContent = `${tenths(video.currentTime || 0)} / ${tenths(duration_())}`;
  head.style.left = `${duration_() ? ((video.currentTime || 0) / duration_()) * 100 : 0}%`;
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
        el("div", { class: "faint" }, `${r.createdBy ?? "Someone"} · ${r.layout.mode === "split" ? "two boxes" : "one box"} · ${renderLength(r.layout)}`),
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
  const first = vx.savedLayout !== null;
  vx.videoAspect = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight : 16 / 9;
  if (first) {
    // The first time: the newest render's layout (the editor reopens where it was), else the defaults
    applyLayout(vx.savedLayout ?? null);
    vx.savedLayout = null;
  } else {
    // A new file (the clip was changed): keep the boxes, fit the trim into the new length
    for (const name of ["single", "top", "bottom"]) vx.boxes[name] = fit(name, vx.boxes[name]);
    setTrim(vx.trim.start, vx.trim.end >= duration_() - 0.05 ? Infinity : vx.trim.end);
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
    verticalStart: () => trimAtPlayhead("start"),
    verticalEnd: () => trimAtPlayhead("end"),
    verticalOneBox: () => change(() => { vx.mode = "single"; select(null); }),
    verticalTwoBoxes: () => change(() => { vx.mode = "split"; select(null); }),
    verticalSafeZones: cycleSafeZone,
    verticalSnap: toggleSnap,
    verticalUndo: undo,
    verticalRedo: redo,
    verticalRender: startRender,
    verticalExit: () => vx.hooks.onExit?.(),
    verticalNudge: () => nudge(...arrows[event.key]),
    verticalNudgeBig: () => nudge(...arrows[event.key]),
  }[id];
  if (!run) return false;
  event.preventDefault();
  run();
  return true;
}
