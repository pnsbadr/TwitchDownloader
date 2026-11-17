// The 9:16 view of the project in Player 2 (multi-track step 5). Player 2 takes the whole width: on the left the landscape picture of
// the piece under the playhead with its crop boxes, in the middle the live 9:16 result, on the right the layout tools and the
// project's vertical renders. The project timeline stays below and plays as usual (the picture is Player 2's own video, drawn
// on canvases).
//
// The layout belongs to each piece (piece.layout, the server's ProjectLayout: mode, top, bottom, splitRatio, keys; boxes are
// fractions of the video, key times VOD seconds). Different layouts in one clip = razor the piece and give each half its own.
// Every change is a project change (one undo history with the timeline): a box drag is one step when it's let go.
//
// Moving boxes: a piece can have keyframes; its boxes glide between them (eased like the server's ShortformLayout.Ease) and
// hold before the first / after the last. F adds one at the playhead or removes the one there, [ / ] go to the previous / next;
// once a piece has one, moving a box sets the keyframe at the playhead. The diamonds show on the piece in the timeline.

import { el, api, toast, formatDate, formatDuration, iconButton, withTooltip, loadSetting, saveSetting } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";
import { flatten, stretchAt, pieceEnd, round } from "./project.js";
// "1:05.20"
function shortClock(seconds) {
  const hundredths = Math.max(0, Math.floor(seconds * 100));
  return `${formatDuration(Math.floor(hundredths / 100))}.${String(hundredths % 100).padStart(2, "0")}`;
}

const OUT_W = 1080;
const OUT_H = 1920;
const MIN_RATIO = 0.2;
const MAX_RATIO = 0.8;
const MIN_BOX = 0.05;
const MAX_KEYS = 30;
const KEY_NEAR = 0.05;
const SNAP_PX = 8;
const SAFE_KEY = "clipco.vertical.safeZone";
const PRESETS_KEY = "clipco.vertical.presets";
const POLL_MS = 2000;

const SAFE_ZONES = {
  none: null,
  tiktok: { name: "TikTok", top: 0.07, bottom: 0.24, left: 0.05, right: 0.14 },
  shorts: { name: "YouTube Shorts", top: 0.1, bottom: 0.22, left: 0.05, right: 0.13 },
  reels: { name: "Instagram Reels", top: 0.11, bottom: 0.2, left: 0.05, right: 0.11 },
};
const SAFE_ORDER = ["none", "tiktok", "shorts", "reels"];

const vx = {
  open: false,
  hooks: null,
  host: null,
  dom: null,
  piece: null,        // the piece being framed (under the playhead)
  vodT: 0,            // the playhead in that piece's VOD seconds
  live: null,         // { mode, ratio, boxes: { single, top, bottom } } shown now
  dragging: false,
  selected: null,
  guides: [],
  videoAspect: 16 / 9,
  safeZone: "none",
  frame: 0,
  exports: [],
  pollTimer: null,
  rendering: false,
};

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
const store = () => vx.hooks.store;
const fromServer = (box) => (box ? { x: box.x, y: box.y, w: box.width, h: box.height } : null);
const toServer = (box) => ({ x: round(box.x * 1000) / 1000, y: round(box.y * 1000) / 1000, width: round(box.w * 1000) / 1000, height: round(box.h * 1000) / 1000 });

// ---------- box shapes ----------

function slotAspect(name, ratio) {
  if (name === "single") return OUT_W / OUT_H;
  const share = name === "top" ? ratio : 1 - ratio;
  return OUT_W / (OUT_H * share);
}

function heightFor(name, w, ratio) {
  return (w * vx.videoAspect) / slotAspect(name, ratio);
}

// Reshape a box for its slot: keep its center and width where possible, shrink to fit inside the video
function fit(name, box, ratio) {
  const k = vx.videoAspect / slotAspect(name, ratio);
  let w = Math.min(box.w, 1, 1 / k);
  w = Math.max(w, MIN_BOX);
  const h = w * k;
  const cx = box.x + box.w / 2;
  const cy = box.y + box.h / 2;
  return { x: clamp(cx - w / 2, 0, 1 - w), y: clamp(cy - h / 2, 0, 1 - h), w, h };
}

function centered(name, ratio) {
  const box = fit(name, { x: 0, y: 0, w: 1, h: 1 }, ratio);
  return { ...box, x: (1 - box.w) / 2, y: (1 - box.h) / 2 };
}

function defaultBoxes(ratio) {
  const topW = 0.25;
  return {
    single: centered("single", ratio),
    top: fit("top", { x: 1 - topW, y: 0, w: topW, h: heightFor("top", topW, ratio) }, ratio),
    bottom: centered("bottom", ratio),
  };
}

// A piece's layout as live boxes at VOD second t (keyframes eased between). No layout = the centered box, like the render.
function stateAt(layout, t) {
  const ratio = clamp(layout?.splitRatio ?? 0.4, MIN_RATIO, MAX_RATIO);
  const mode = layout?.mode === "split" ? "split" : "single";
  const boxes = defaultBoxes(ratio);
  const put = (top, bottom) => {
    if (mode === "single") {
      if (top) boxes.single = fit("single", fromServer(top), ratio);
    } else {
      if (top) boxes.top = fit("top", fromServer(top), ratio);
      if (bottom) boxes.bottom = fit("bottom", fromServer(bottom), ratio);
    }
  };
  put(layout?.top, layout?.bottom);
  const keys = layout?.keys ?? [];
  if (keys.length) {
    const at = (key) => {
      const b = structuredClone(boxes);
      if (mode === "single") {
        if (key.top) b.single = fit("single", fromServer(key.top), ratio);
      } else {
        if (key.top) b.top = fit("top", fromServer(key.top), ratio);
        if (key.bottom) b.bottom = fit("bottom", fromServer(key.bottom), ratio);
      }
      return b;
    };
    let shown;
    if (t <= keys[0].time) shown = at(keys[0]);
    else if (t >= keys[keys.length - 1].time) shown = at(keys[keys.length - 1]);
    else {
      let i = 0;
      while (keys[i + 1].time <= t) i++;
      const a = at(keys[i]);
      const b = at(keys[i + 1]);
      const u = ease((t - keys[i].time) / (keys[i + 1].time - keys[i].time));
      shown = { single: mix(a.single, b.single, u), top: mix(a.top, b.top, u), bottom: mix(a.bottom, b.bottom, u) };
    }
    return { mode, ratio, boxes: shown };
  }
  return { mode, ratio, boxes };
}

const ease = (u) => u * u * (3 - 2 * u);
function mix(a, b, u) {
  return { x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u, w: a.w + (b.w - a.w) * u, h: a.h + (b.h - a.h) * u };
}

// Live boxes → the piece's layout. With keyframes the boxes are written to the keyframe at the playhead (made if needed);
// without, to the layout itself.
function layoutWith(layout, live, t) {
  const plain = live.mode === "single"
    ? { mode: "single", top: toServer(live.boxes.single), bottom: null, splitRatio: null }
    : { mode: "split", top: toServer(live.boxes.top), bottom: toServer(live.boxes.bottom), splitRatio: round(live.ratio * 1000) / 1000 };
  const keys = layout?.keys ?? [];
  if (!keys.length) return { ...plain, keys: null };
  const key = { time: round(t), top: plain.top, bottom: plain.bottom };
  const others = keys.filter((k) => Math.abs(k.time - t) >= KEY_NEAR);
  const next = [...others, key].sort((a, b) => a.time - b.time);
  // a mode or split change applies to the whole piece: its keyframes are reshaped for it
  const reshaped = next.map((k) => {
    if (k === key) return k;
    const s = stateAt({ ...plain, keys: [k] }, k.time);
    return live.mode === "single"
      ? { time: k.time, top: toServer(s.boxes.single), bottom: null }
      : { time: k.time, top: toServer(s.boxes.top), bottom: toServer(s.boxes.bottom) };
  });
  return { ...plain, keys: reshaped.slice(0, MAX_KEYS) };
}

// ---------- the piece under the playhead ----------

function locate() {
  const content = store().content;
  if (!content) {
    vx.piece = null;
    return;
  }
  const t = vx.hooks.playback.time();
  const s = stretchAt(flatten(content), t);
  if (s?.piece) {
    vx.piece = content.pieces.find((p) => p.id === s.piece.id) ?? null;
    vx.vodT = s.vodStart + (t - s.at);
  } else {
    // a gap: the selected piece's boxes (at its start) can still be set
    const selected = vx.hooks.timeline().selectedPieces()[0] ?? null;
    vx.piece = selected;
    vx.vodT = selected ? selected.start : 0;
  }
}

function canChange() {
  if (!vx.piece || !store().canEdit) return false;
  return !store().content.tracks.find((t) => t.id === vx.piece.trackId)?.locked;
}

// One project change on the framed piece
function changeLayout(fn) {
  const piece = vx.piece;
  if (!piece) return;
  if (!canChange()) {
    toast(store().canEdit ? "That piece's row is locked." : (store().project?.canCopy === false ? "This edit is view only." : "This edit is view only. Make your own copy to change it."));
    return;
  }
  store().edit((content) => {
    const p = content.pieces.find((x) => x.id === piece.id);
    if (!p) return false;
    const next = fn(p.layout ?? null, p);
    if (next === false) return false;
    p.layout = next;
  });
}

function commitLive() {
  const live = vx.live;
  const t = vx.vodT;
  changeLayout((layout) => layoutWith(layout, live, t));
}

// ---------- keyframes ----------

function keyIndexAt(layout, t) {
  return layout?.keys?.findIndex((k) => Math.abs(k.time - t) < KEY_NEAR) ?? -1;
}

function toggleKeyframe() {
  if (!vx.piece) return;
  const t = vx.vodT;
  const live = vx.live;
  changeLayout((layout) => {
    const keys = layout?.keys ?? [];
    const index = keyIndexAt(layout, t);
    if (index >= 0) {
      const rest = keys.filter((_, i) => i !== index);
      // the last one gone: the boxes stay where that keyframe had them
      return rest.length ? { ...layout, keys: rest } : { ...layoutWith(null, live, t), keys: null };
    }
    if (keys.length >= MAX_KEYS) {
      toast(`A piece can have at most ${MAX_KEYS} keyframes.`, "error");
      return false;
    }
    const base = layoutWith(layout?.keys?.length ? layout : null, live, t);
    const key = { time: round(t), top: base.top, bottom: base.bottom };
    return { ...base, keys: [...keys, key].sort((a, b) => a.time - b.time) };
  });
}

function clearKeyframes() {
  if (!vx.piece?.layout?.keys?.length) return;
  const live = vx.live;
  changeLayout(() => ({ ...layoutWith(null, live, vx.vodT), keys: null }));
}

function jumpKeyframe(direction) {
  const piece = vx.piece;
  const keys = (piece?.layout?.keys ?? []).filter((k) => k.time >= piece.start - 0.001 && k.time <= piece.end + 0.001);
  if (!keys.length) return;
  const t = vx.vodT;
  const target = direction < 0 ? [...keys].reverse().find((k) => k.time < t - KEY_NEAR) : keys.find((k) => k.time > t + KEY_NEAR);
  if (!target) return;
  vx.hooks.playback.pause();
  vx.hooks.playback.seek(piece.at + (target.time - piece.start));
}

// ---------- presets ----------

const BUILT_IN_PRESETS = [
  { name: "Center crop", icon: "square", layout: () => ({ mode: "single", ratio: 0.4, boxes: { ...defaultBoxes(0.4), single: centered("single", 0.4) } }) },
  { name: "Facecam top left, gameplay below", icon: "rows", layout: () => facecam("left") },
  { name: "Facecam top right, gameplay below", icon: "rows", layout: () => facecam("right") },
  {
    name: "Left and right halves",
    icon: "columns",
    layout: () => {
      const ratio = 0.5;
      const top = fit("top", { x: 0, y: 0, w: 1, h: 1 }, ratio);
      const bottom = fit("bottom", { x: 0, y: 0, w: 1, h: 1 }, ratio);
      return { mode: "split", ratio, boxes: { ...defaultBoxes(ratio), top: { ...top, x: 0 }, bottom: { ...bottom, x: 1 - bottom.w } } };
    },
  },
];

function facecam(side) {
  const ratio = 0.35;
  const w = 0.25;
  const top = fit("top", { x: side === "left" ? 0 : 1 - w, y: 0, w, h: heightFor("top", w, ratio) }, ratio);
  return { mode: "split", ratio, boxes: { ...defaultBoxes(ratio), top, bottom: centered("bottom", ratio) } };
}

function ownPresets() {
  const list = loadSetting(PRESETS_KEY, []);
  return Array.isArray(list) ? list.filter((p) => p && typeof p.name === "string" && p.layout) : [];
}

// Saved presets are server-shaped layouts (shared with the old editor's saved ones)
function presetToLive(layout) {
  return stateAt({ ...layout, keys: null }, 0);
}

function usePreset(make) {
  const live = typeof make === "function" ? make() : presetToLive(make);
  vx.live = live;
  commitLive();
}

async function savePreset() {
  const name = await askText({ heading: "Save this layout", label: "Name", confirm: "Save", maxLength: 40 });
  if (!name) return;
  const layout = { ...layoutWith(null, vx.live, 0), keys: null };
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
  const item = (label, icon, run, extra = {}) => {
    const entry = el("sl-menu-item", extra, el("sl-icon", { slot: "prefix", library: "ph", name: icon }), label);
    entry.run = run;
    return entry;
  };
  vx.dom.presetMenu.replaceChildren(
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

function layoutEverywhere() {
  const piece = vx.piece;
  if (!piece) return;
  vx.hooks.timeline().layoutEverywhere({ ...piece, layout: layoutWith(null, vx.live, vx.vodT) });
}

// ---------- the panel ----------

function build() {
  const source = el("canvas", { class: "vx2-source" });
  const stage = el("div", { class: "vx2-stage" }, source);
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
    if (event.target === stage || event.target === source) select(null);
  });
  const note = el("div", { class: "vx2-note", hidden: true });
  const stageWrap = el("div", { class: "vx2-stage-wrap" }, stage, note);

  const canvas = el("canvas", { width: String(OUT_W / 2), height: String(OUT_H / 2) });
  const safe = el("div", { class: "vx2-safe", hidden: true });
  const split = el("div", { class: "vx2-split", role: "slider", tabindex: "0", "aria-label": "Top and bottom split", "aria-valuemin": "20", "aria-valuemax": "80" },
    el("span", {}));
  split.addEventListener("pointerdown", startSplitDrag);
  split.addEventListener("keydown", (event) => {
    const step = { ArrowUp: -0.02, ArrowDown: 0.02 }[event.key];
    if (!step || !vx.live) return;
    event.preventDefault();
    event.stopPropagation();
    setRatio(vx.live.ratio + step);
    commitLive();
  });
  const gap = el("div", { class: "vx2-left-out", hidden: true }, "Gap: black in the video");
  const portrait = el("div", { class: "vx2-portrait" }, canvas, safe, gap, split);
  const previewWrap = el("div", { class: "vx2-preview-wrap" }, portrait);

  const play = iconButton({ icon: "play", label: "Play / pause", keybind: "playPause", onClick: () => vx.hooks.playback.toggle() });
  const time = el("span", { class: "num vx2-time" }, "0:00.00");
  const pieceText = el("span", { class: "vx2-trim-text" });
  const mute = iconButton({ icon: "speaker-high", label: "Mute", onClick: toggleMute });
  const volume = el("sl-range", { class: "volume", min: "0", max: "100", tooltip: "none", "aria-label": "Volume" });
  volume.addEventListener("sl-input", () => {
    vx.hooks.player.setVolume(Number(volume.value) / 100, false);
    renderBar();
  });
  const exit = iconButton({ icon: "device-mobile", label: "Back to the normal player", keybind: "verticalToggle", onClick: () => vx.hooks.onExit?.() });
  exit.querySelector("sl-icon-button").toggleAttribute("data-active", true);
  const bar = el("div", { class: "vx2-bar player-controls" }, play, time, el("span", { class: "vx2-bar-gap" }), pieceText, mute, volume, exit);

  const one = iconButton({ icon: "square", label: "One box", keybind: "verticalOneBox", onClick: () => setMode("single") });
  const two = iconButton({ icon: "rows", label: "Two boxes (top and bottom)", keybind: "verticalTwoBoxes", onClick: () => setMode("split") });
  const presetMenu = el("sl-menu");
  presetMenu.addEventListener("sl-select", (event) => event.detail.item.run?.());
  const presets = el("sl-dropdown", { hoist: true, placement: "bottom-start", distance: "4" },
    el("sl-icon-button", { slot: "trigger", library: "ph", name: "layout", label: "Layouts", title: "Layouts" }), presetMenu);
  presets.addEventListener("sl-after-hide", (event) => {
    if (event.target === presets && vx.open && !document.querySelector("sl-dialog[open]")) takeFocus();
  });
  const safeButton = iconButton({ icon: "frame-corners", label: "Safe zones", keybind: "verticalSafeZones", onClick: cycleSafeZone });
  const allButton = iconButton({ icon: "copy-simple", label: "Use this layout on every piece", onClick: layoutEverywhere });
  const safeName = el("span", { class: "vx2-safe-name faint" });
  const pieceInfo = el("span", { class: "vx2-part-info faint" });
  const keyButton = iconButton({ icon: "diamond", label: "Keyframe at the playhead (add / remove)", keybind: "verticalKeyframe", onClick: toggleKeyframe });
  const prevKey = iconButton({ icon: "caret-left", label: "Previous keyframe", keybind: "verticalPrevKeyframe", onClick: () => jumpKeyframe(-1) });
  const nextKey = iconButton({ icon: "caret-right", label: "Next keyframe", keybind: "verticalNextKeyframe", onClick: () => jumpKeyframe(1) });
  const clearKeys = iconButton({ icon: "x-circle", label: "Remove this piece's keyframes (the boxes stay put)", onClick: clearKeyframes });
  const keyInfo = el("span", { class: "vx2-part-info faint" });

  const render = el("sl-button", { variant: "primary", class: "vx2-render-button" },
    el("sl-icon", { slot: "prefix", library: "ph", name: "export" }), "Render 9:16");
  render.addEventListener("click", startRender);
  const progress = el("sl-progress-bar", { class: "vx2-progress", value: "0", hidden: true });
  const message = el("p", { class: "vx2-message faint" });
  const list = el("ul", { class: "vx2-renders" });
  const empty = el("p", { class: "faint vx2-empty" }, "No 9:16 renders of this edit yet. Render makes one (the Export tab has more options).");

  const side = el("aside", { class: "vx2-side" },
    el("div", { class: "vx2-group faint" }, "Layout of this piece"),
    el("div", { class: "vx2-tools", "data-tip-placement": "bottom" }, one, two, presets, el("span", { class: "tools-sep" }), safeButton, allButton),
    pieceInfo,
    el("div", { class: "vx2-group faint" }, "Moving boxes"),
    el("div", { class: "vx2-tools", "data-tip-placement": "bottom" }, keyButton, prevKey, nextKey, clearKeys),
    keyInfo,
    safeName,
    el("div", { class: "vx2-render-row" }, withTooltip(render, "Render the whole edit as one 9:16 video", "verticalRender"), progress),
    message,
    el("h3", { class: "vx2-heading display" }, "9:16 renders"),
    empty,
    list);

  const panel = el("div", { class: "vx2", tabindex: "-1", hidden: true }, stageWrap, previewWrap, side, bar);
  vx.dom = {
    panel, source, stage, stageWrap, boxNodes, guides, note, canvas, safe, split, portrait, previewWrap, gap,
    play, time, pieceText, mute, volume, one, two, presetMenu, safeButton, safeName, allButton, pieceInfo,
    keyButton, clearKeys, keyInfo, render, progress, message, list, empty,
  };
  new ResizeObserver(layoutPanel).observe(panel);
  return panel;
}

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

function takeFocus() {
  vx.dom.panel.focus({ preventScroll: true });
}

const visibleBoxes = () => (vx.live?.mode === "split" ? ["top", "bottom"] : ["single"]);

function renderBoxes() {
  const { boxNodes, split, one, two, stage } = vx.dom;
  const live = vx.live;
  stage.classList.toggle("vx2-readonly", !canChange());
  boxNodes.top.hidden = !live;
  boxNodes.bottom.hidden = !live || live.mode === "single";
  if (live) {
    boxNodes.top.querySelector(".vx2-tag").textContent = live.mode === "single" ? "Crop" : "Top";
    for (const name of visibleBoxes()) {
      const box = live.boxes[name];
      const node = name === "bottom" ? boxNodes.bottom : boxNodes.top;
      node.dataset.box = name;
      node.style.left = `${box.x * 100}%`;
      node.style.top = `${box.y * 100}%`;
      node.style.width = `${box.w * 100}%`;
      node.style.height = `${box.h * 100}%`;
      node.classList.toggle("selected", vx.selected === name);
    }
  }
  split.hidden = !live || live.mode === "single";
  if (live) {
    split.style.top = `${live.ratio * 100}%`;
    split.setAttribute("aria-valuenow", String(Math.round(live.ratio * 100)));
  }
  one.querySelector("sl-icon-button").toggleAttribute("data-active", live?.mode === "single");
  two.querySelector("sl-icon-button").toggleAttribute("data-active", live?.mode === "split");
  vx.dom.guides.replaceChildren(...vx.guides.map((g) => el("div", {
    class: `vx2-guide ${g.axis}`,
    style: g.axis === "x" ? `left:${g.at * 100}%` : `top:${g.at * 100}%`,
  })));
}

function renderSafe() {
  const zone = SAFE_ZONES[vx.safeZone];
  const { safe, safeButton, safeName } = vx.dom;
  safe.hidden = !zone;
  safeButton.querySelector("sl-icon-button").toggleAttribute("data-active", Boolean(zone));
  safeName.textContent = zone ? `Safe zone: ${zone.name} (approximate)` : "";
  if (!zone) return;
  const pct = (v) => `${v * 100}%`;
  safe.replaceChildren(
    el("div", { class: "vx2-unsafe", style: `left:0;right:0;top:0;height:${pct(zone.top)}` }),
    el("div", { class: "vx2-unsafe", style: `left:0;right:0;bottom:0;height:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-unsafe", style: `left:0;width:${pct(zone.left)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-unsafe", style: `right:0;width:${pct(zone.right)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }),
    el("div", { class: "vx2-safe-box", style: `left:${pct(zone.left)};right:${pct(zone.right)};top:${pct(zone.top)};bottom:${pct(zone.bottom)}` }));
}

function renderInfo() {
  const { pieceInfo, keyButton, clearKeys, keyInfo, allButton, pieceText, gap } = vx.dom;
  const piece = vx.piece;
  const playing = stretchAt(flatten(store().content ?? { tracks: [], pieces: [] }), vx.hooks.playback.time());
  gap.hidden = Boolean(playing?.piece) || !store().content?.pieces.length;
  const text = !piece
    ? (store().content?.pieces.length ? "A gap: nothing to frame here. Move the playhead onto a piece." : "Drop clips, segments or videos on the timeline below.")
    : `${piece.label || "Untitled piece"} · ${formatDuration(piece.end - piece.start, { short: false })}${piece.layout ? "" : " · centered box (no layout yet)"}${canChange() ? "" : " · view only"}`;
  if (pieceInfo.textContent !== text) pieceInfo.textContent = text;
  pieceText.textContent = piece ? (piece.label || "Untitled piece") : "";
  allButton.querySelector("sl-icon-button").disabled = !piece || !canChange();
  const count = piece?.layout?.keys?.length ?? 0;
  const on = keyIndexAt(piece?.layout, vx.vodT) >= 0;
  keyButton.querySelector("sl-icon-button").toggleAttribute("data-active", on);
  clearKeys.querySelector("sl-icon-button").disabled = !count || !canChange();
  const keys = !piece ? ""
    : !count ? "The boxes stay put. Press F to add a keyframe, then move the playhead and a box: they glide between keyframes."
      : `${count} keyframe${count === 1 ? "" : "s"} on this piece${on ? " · on one now (moving a box changes it)" : " · moving a box adds one here"}`;
  if (keyInfo.textContent !== keys) keyInfo.textContent = keys;
}

function renderBar() {
  const { play, time, mute, volume } = vx.dom;
  const playing = vx.hooks.playback.playing();
  const button = play.querySelector("sl-icon-button");
  button.name = playing ? "pause" : "play";
  const content = store().content;
  const length = content?.pieces.length ? Math.max(...content.pieces.map(pieceEnd)) : 0;
  time.textContent = `${shortClock(vx.hooks.playback.time())} / ${shortClock(length)}`;
  const v = vx.hooks.player.volume;
  const muteButton = mute.querySelector("sl-icon-button");
  muteButton.disabled = !v;
  volume.disabled = !v;
  if (v) {
    volume.value = Math.round(v.volume * 100);
    muteButton.name = v.muted || v.volume === 0 ? "speaker-slash" : "speaker-high";
  }
}

function toggleMute() {
  const v = vx.hooks.player.volume;
  if (!v) return;
  vx.hooks.player.setVolume(null, !v.muted);
  renderBar();
}

// ---------- drawing (every frame) ----------

function videoElement() {
  const player = vx.hooks.player;
  return player.kind === "app" ? player.backend?.video ?? null : null;
}

function draw() {
  if (!vx.open) return;
  locate();
  if (!vx.dragging) {
    vx.live = vx.piece ? stateAt(vx.piece.layout, vx.vodT) : null;
    renderBoxes();
  }
  const video = videoElement();
  const playing = stretchAt(flatten(store().content ?? { tracks: [], pieces: [] }), vx.hooks.playback.time());
  const showing = Boolean(playing?.piece) && video && video.readyState >= 2;
  if (video?.videoWidth && video.videoHeight) {
    const aspect = video.videoWidth / video.videoHeight;
    if (Math.abs(aspect - vx.videoAspect) > 0.01) {
      vx.videoAspect = aspect;
      layoutPanel();
    }
  }
  const { source, canvas } = vx.dom;
  // the landscape picture
  const rect = vx.dom.stage.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const sw = Math.max(1, Math.round(rect.width * dpr));
  const sh = Math.max(1, Math.round(rect.height * dpr));
  if (source.width !== sw || source.height !== sh) {
    source.width = sw;
    source.height = sh;
  }
  const sctx = source.getContext("2d");
  sctx.fillStyle = "#000";
  sctx.fillRect(0, 0, sw, sh);
  if (showing) sctx.drawImage(video, 0, 0, sw, sh);
  // the 9:16 result
  const ctx = canvas.getContext("2d");
  const cw = canvas.width;
  const ch = canvas.height;
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, cw, ch);
  const live = vx.live;
  if (showing && live) {
    const vw = video.videoWidth;
    const vh = video.videoHeight;
    const put = (box, dy, dh) => ctx.drawImage(video, box.x * vw, box.y * vh, box.w * vw, box.h * vh, 0, dy, cw, dh);
    if (live.mode === "single") put(live.boxes.single, 0, ch);
    else {
      const topH = Math.round(ch * live.ratio);
      put(live.boxes.top, 0, topH);
      put(live.boxes.bottom, topH, ch - topH);
    }
  }
  const twitch = vx.hooks.player.ready && vx.hooks.player.kind === "twitch";
  showNote(twitch ? "The 9:16 view needs the app player (this VOD plays in Twitch's player). The boxes still render as set." : "");
  renderInfo();
  renderBar();
  vx.frame = requestAnimationFrame(draw);
}

function showNote(text) {
  const note = vx.dom.note;
  if (note.textContent === text) return;
  note.hidden = !text;
  note.replaceChildren(...(text ? [el("p", {}, text)] : []));
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
    const box = vx.live.boxes[other];
    if (axis === "x") targets.push(box.x, box.x + box.w / 2, box.x + box.w);
    else targets.push(box.y, box.y + box.h / 2, box.y + box.h);
  }
  return targets;
}

function snapOn() {
  return vx.hooks.timeline().snap;
}

function startBoxDrag(event) {
  if (event.button !== 0 || !vx.live) return;
  const node = event.currentTarget;
  const name = node.dataset.box;
  select(name);
  if (!canChange()) return;
  node.focus({ preventScroll: true });
  vx.hooks.playback.pause();
  const live = vx.live;
  const ratio = live.ratio;
  const box = { ...live.boxes[name] };
  const stage = vx.dom.stage;
  const rect = stage.getBoundingClientRect();
  const tx = SNAP_PX / rect.width;
  const ty = SNAP_PX / rect.height;
  const start = pointer(event, stage);
  const corner = event.target.dataset.corner;
  event.preventDefault();
  event.stopPropagation();
  node.setPointerCapture(event.pointerId);
  const anchor = corner && {
    x: corner.includes("w") ? box.x + box.w : box.x,
    y: corner.includes("n") ? box.y + box.h : box.y,
  };
  let moved = false;
  const onMove = (moveEvent) => {
    moved = true;
    const p = pointer(moveEvent, stage);
    const snapping = snapOn() && !moveEvent.altKey;
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
      live.boxes[name] = { ...box, x, y };
    } else {
      const k = vx.videoAspect / slotAspect(name, ratio);
      const left = corner.includes("w");
      const up = corner.includes("n");
      const maxW = Math.min(left ? anchor.x : 1 - anchor.x, (up ? anchor.y : 1 - anchor.y) / k);
      let w = Math.max(Math.abs(p.x - anchor.x), Math.abs(p.y - anchor.y) / k);
      if (snapping) {
        const edgeX = left ? anchor.x - w : anchor.x + w;
        const sx = snapAxis([edgeX], snapTargets(name, "x"), tx);
        if (sx) {
          w = Math.abs(sx.at - anchor.x);
          vx.guides.push({ axis: "x", at: sx.at });
        }
      }
      w = clamp(w, MIN_BOX, maxW);
      const h = w * k;
      live.boxes[name] = { x: left ? anchor.x - w : anchor.x, y: up ? anchor.y - h : anchor.y, w, h };
    }
    renderBoxes();
  };
  vx.dragging = true;
  const onUp = () => {
    node.removeEventListener("pointermove", onMove);
    node.removeEventListener("pointerup", onUp);
    node.removeEventListener("pointercancel", onUp);
    vx.guides = [];
    if (moved) commitLive();
    vx.dragging = false;
    renderBoxes();
  };
  node.addEventListener("pointermove", onMove);
  node.addEventListener("pointerup", onUp);
  node.addEventListener("pointercancel", onUp);
}

function nudge(dx, dy) {
  const name = vx.selected;
  if (!name || !vx.live || !visibleBoxes().includes(name) || !canChange()) return;
  const box = vx.live.boxes[name];
  vx.live.boxes[name] = { ...box, x: clamp(box.x + dx, 0, 1 - box.w), y: clamp(box.y + dy, 0, 1 - box.h) };
  commitLive();
}

function setRatio(ratio) {
  const live = vx.live;
  live.ratio = clamp(ratio, MIN_RATIO, MAX_RATIO);
  live.boxes.top = fit("top", live.boxes.top, live.ratio);
  live.boxes.bottom = fit("bottom", live.boxes.bottom, live.ratio);
}

function setMode(mode) {
  if (!vx.live || !canChange()) return;
  vx.live.mode = mode;
  select(null);
  commitLive();
}

function startSplitDrag(event) {
  if (!vx.live || !canChange()) return;
  const handle = event.currentTarget;
  event.preventDefault();
  handle.setPointerCapture(event.pointerId);
  vx.dragging = true;
  const onMove = (moveEvent) => {
    setRatio(pointer(moveEvent, vx.dom.portrait).y);
    renderBoxes();
  };
  const onUp = () => {
    handle.removeEventListener("pointermove", onMove);
    handle.removeEventListener("pointerup", onUp);
    handle.removeEventListener("pointercancel", onUp);
    commitLive();
    vx.dragging = false;
  };
  handle.addEventListener("pointermove", onMove);
  handle.addEventListener("pointerup", onUp);
  handle.addEventListener("pointercancel", onUp);
}

function cycleSafeZone() {
  vx.safeZone = SAFE_ORDER[(SAFE_ORDER.indexOf(vx.safeZone) + 1) % SAFE_ORDER.length];
  saveSetting(SAFE_KEY, vx.safeZone);
  renderSafe();
}

// ---------- render (an export of the project, 9:16) and the project's renders ----------

async function startRender() {
  const project = store().project;
  if (!project || !store().content?.pieces.length) {
    toast("Put something on the timeline first.");
    return;
  }
  if (project.canDownload === false) {
    toast("Your role doesn't let you render teammates' vertical edits. Start your own from the original clip.", "error");
    return;
  }
  await store().saveNow();
  vx.dom.message.textContent = "";
  vx.dom.message.classList.remove("error");
  try {
    await api("POST", "/api/exports", { items: [{ kind: "project", projectId: project.id, vertical: true, horizontal: false }] });
    vx.dom.message.textContent = "Rendering… you can keep working; it's also on the Export tab.";
    vx.rendering = true;
    await loadRenders();
  } catch (error) {
    vx.dom.message.textContent = error.message;
    vx.dom.message.classList.add("error");
  }
}

async function loadRenders() {
  clearTimeout(vx.pollTimer);
  if (!vx.open || !store().project) return;
  const id = store().project.id;
  let batches = [];
  try {
    batches = await api("GET", "/api/exports");
  } catch {
    return;
  }
  if (!vx.open || store().project?.id !== id) return;
  const items = [];
  for (const batch of batches) {
    for (const item of batch.items ?? []) {
      if (item.projectId === id && item.kind === "ProjectVertical") items.push({ batch, item });
    }
  }
  vx.exports = items;
  renderRenders();
  const busy = items.some(({ item }) => item.status === "Queued" || item.status === "Running");
  if (vx.rendering && !busy) {
    vx.rendering = false;
    const newest = items[0]?.item;
    vx.dom.message.textContent = newest?.status === "Failed" ? `The render failed: ${newest.error ?? "unknown error"}` : "Rendered.";
    vx.dom.message.classList.toggle("error", newest?.status === "Failed");
  }
  if (busy) vx.pollTimer = setTimeout(loadRenders, POLL_MS);
}

function renderRenders() {
  const { list, empty, progress, render } = vx.dom;
  empty.hidden = vx.exports.length > 0;
  const running = vx.exports.find(({ item }) => item.status === "Queued" || item.status === "Running");
  progress.hidden = !running;
  if (running) progress.value = running.item.partsTotal ? Math.round((running.item.partsDone / running.item.partsTotal) * 100) : 0;
  render.loading = Boolean(running);
  list.replaceChildren(...vx.exports.map(({ batch, item }) => {
    const ready = item.status === "Ready" && item.fileUrl;
    const state = ready ? `${formatSize(item.fileSizeBytes)} · until ${formatDate(batch.expiresAtUtc)}`
      : item.status === "Failed" ? `Failed: ${item.error ?? ""}` : item.status === "Running" ? `Rendering ${item.partsDone}/${item.partsTotal}` : "Waiting";
    return el("li", { class: "vx2-render-item" },
      el("div", { class: "vx2-thumb" }, el("sl-icon", { library: "ph", name: ready ? "film-strip" : item.status === "Failed" ? "warning" : "hourglass" })),
      el("div", {},
        el("div", { class: "vx2-render-title" }, formatDate(batch.createdAtUtc)),
        el("div", { class: "faint" }, `${formatDuration(Math.round(item.lengthSeconds ?? 0))} · ${state}`)),
      el("div", { class: "vx2-render-actions" },
        ready ? el("a", { class: "vx2-icon-link", href: item.fileUrl, download: "", title: "Download", "aria-label": "Download" },
          el("sl-icon", { library: "ph", name: "download-simple" })) : null));
  }));
}

function formatSize(bytes) {
  if (!bytes) return "";
  return bytes > 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`;
}

// ---------- open / close / keys ----------

export function setUpProjectVertical(host, hooks) {
  vx.host = host;
  vx.hooks = hooks;
  const panel = build();
  host.querySelector(".player-screen").after(panel);
  vx.safeZone = SAFE_ZONES[loadSetting(SAFE_KEY, "none")] !== undefined ? loadSetting(SAFE_KEY, "none") : "none";
}

export function projectVerticalOpen() {
  return vx.open;
}

export function openProjectVertical() {
  if (vx.open) return;
  vx.open = true;
  vx.selected = null;
  vx.exports = [];
  vx.host.classList.add("vertical");
  vx.dom.panel.hidden = false;
  vx.dom.message.textContent = "";
  renderPresetMenu();
  renderSafe();
  renderRenders();
  layoutPanel();
  takeFocus();
  cancelAnimationFrame(vx.frame);
  vx.frame = requestAnimationFrame(draw);
  loadRenders();
}

export function closeProjectVertical() {
  if (!vx.open) return;
  vx.open = false;
  cancelAnimationFrame(vx.frame);
  clearTimeout(vx.pollTimer);
  vx.host.classList.remove("vertical");
  vx.dom.panel.hidden = true;
}

// The project changed (another project opened, a change, undo): the next frame reads it again
export function refreshProjectVertical() {
  if (!vx.open) return;
  if (vx.lastProject !== store().project?.id) {
    vx.lastProject = store().project?.id;
    vx.exports = [];
    renderRenders();
    loadRenders();
  }
}

export function pauseProjectVertical() {
  // Player 2 itself is paused by the editor; nothing else plays here
}

// Keys of the 9:16 view (keybinds.js scopes "vertical" and "vertical-box"); true = handled
export function handleProjectVerticalKey(event) {
  if (!vx.open) return false;
  if (vx.selected && vx.live) {
    const id = keybindFor("vertical-box", event);
    if (id === "verticalNudge" || id === "verticalNudgeBig") {
      event.preventDefault();
      const step = id === "verticalNudgeBig" ? 0.05 : 0.005;
      const d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[event.key];
      if (d) nudge(...d);
      return true;
    }
  }
  const id = keybindFor("vertical", event);
  const run = {
    verticalOneBox: () => setMode("single"),
    verticalTwoBoxes: () => setMode("split"),
    verticalSafeZones: cycleSafeZone,
    verticalKeyframe: toggleKeyframe,
    verticalPrevKeyframe: () => jumpKeyframe(-1),
    verticalNextKeyframe: () => jumpKeyframe(1),
    verticalRender: startRender,
  }[id];
  if (!run) return false;
  event.preventDefault();
  run();
  return true;
}
