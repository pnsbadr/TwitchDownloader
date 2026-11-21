// Drag and drop for the whole editor: cards onto players, timeline items onto Player 2 or other rows, items onto the
// export list. Pointer events, not HTML5 drag and drop, so canvas items (timeline bars) can be dragged the same way.
//
// While dragging, the page is darkened except for the dragged item and the drop target under the pointer, if that
// target accepts the item. The shade is one fixed layer with holes cut in it (clip-path), so it works whatever the
// stacking of the panels underneath.
//
//   dropTarget(element, { accepts(item, info) -> bool, onDrop(item, info) })
//   draggable(element, () => item, { ghost?: () => Element })      item = { kind: "vod" | "clip" | ..., label, data }
//   startDrag(pointerEvent, item, { sourceRect?: () => DOMRect, ghost?: Element })   for canvas items
//
// info = { altKey, shiftKey, x, y }: Alt + drag makes copies (e.g. a clip dropped on a video row becomes a segment).

const START_DISTANCE = 5;
const targets = new WeakMap();
const watchers = new Set();
let active = null;

// Follows every drag: { start(item), move(item, info) (before the target under the pointer is looked up, so a watcher can show
// one), end(item, droppedOn: the accepting target's element or null, cancelled: Esc) }
export function watchDrags(watcher) {
  watchers.add(watcher);
}

export function dropTarget(element, options) {
  targets.set(element, options);
  element.dataset.dropTarget = "";
}

export function removeDropTarget(element) {
  targets.delete(element);
  delete element.dataset.dropTarget;
}

export function isDragging() {
  return active !== null;
}

// Starts a drag after the pointer has moved a few pixels with the button held, so clicks and double-clicks still work
export function draggable(element, getItem, { ghost } = {}) {
  element.addEventListener("pointerdown", (event) => {
    if (event.button !== 0 || active) return;
    if (event.target.closest("sl-icon-button, button, input, a")) return;
    const startX = event.clientX;
    const startY = event.clientY;
    const move = (moveEvent) => {
      if (Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < START_DISTANCE) return;
      cleanup();
      const item = getItem();
      if (item) startDrag(moveEvent, item, { sourceRect: () => element.getBoundingClientRect(), ghost: ghost ? ghost() : cloneGhost(element) });
    };
    const cleanup = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", cleanup);
      window.removeEventListener("pointercancel", cleanup);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", cleanup);
    window.addEventListener("pointercancel", cleanup);
  });
}

// A copy of the element under the pointer while it's dragged; trim(copy) may take parts out of it first
export function cloneGhost(element, trim = null) {
  const rect = element.getBoundingClientRect();
  const ghost = element.cloneNode(true);
  ghost.removeAttribute("id");
  ghost.style.width = `${Math.min(rect.width, 320)}px`;
  trim?.(ghost);
  // A row of a virtual list sits where the list placed it (position: absolute; top: its place in the list), so the copy would float that
  // far from the pointer (badrfly 2026-11-21: the lower the row, the farther away): the copy goes back into the ghost's own flow
  ghost.style.position = "static";
  ghost.style.top = ghost.style.left = "";
  ghost.style.transform = "none";
  // The Details view's columns come from its list (a CSS variable): keep them, at most the ghost's width
  const columns = getComputedStyle(element).gridTemplateColumns;
  if (columns && columns !== "none" && !ghost.style.gridTemplateColumns) ghost.style.gridTemplateColumns = columns;
  ghost.style.overflow = "hidden";
  return ghost;
}

export function startDrag(event, item, { sourceRect = null, ghost = null } = {}) {
  if (active) return;
  const shade = document.createElement("div");
  shade.className = "drag-shade";
  const ghostBox = document.createElement("div");
  ghostBox.className = "drag-ghost";
  if (ghost) ghostBox.append(ghost);
  else ghostBox.append(defaultGhost(item));
  document.body.append(shade, ghostBox);
  document.body.classList.add("is-dragging");

  active = {
    item,
    sourceRect,
    shade,
    ghostBox,
    target: null,       // { element, options } under the pointer that accepts the item
    refused: false,     // a target is under the pointer but doesn't take this item
    x: event.clientX,
    y: event.clientY,
    altKey: event.altKey,
    shiftKey: event.shiftKey,
  };
  window.addEventListener("pointermove", onMove, true);
  window.addEventListener("pointerup", onUp, true);
  window.addEventListener("pointercancel", cancel, true);
  window.addEventListener("keydown", onKey, true);
  window.addEventListener("keyup", onKey, true);
  window.addEventListener("blur", cancel);
  for (const w of watchers) w.start?.(item);
  update();
  active.frame = requestAnimationFrame(followLayout);
}

function defaultGhost(item) {
  const box = document.createElement("div");
  box.className = "drag-ghost-label";
  box.textContent = item.label || item.kind;
  return box;
}

function swallowClick(event) {
  event.stopPropagation();
  event.preventDefault();
}

function onMove(event) {
  active.x = event.clientX;
  active.y = event.clientY;
  active.altKey = event.altKey;
  active.shiftKey = event.shiftKey;
  update();
}

function onKey(event) {
  if (event.key === "Escape") {
    event.preventDefault();
    event.stopPropagation();
    cancel();
    return;
  }
  // Holding or letting go of Alt can change what a target accepts (copy vs move)
  if (event.key === "Alt" || event.key === "Shift") {
    event.preventDefault();
    active.altKey = event.altKey;
    active.shiftKey = event.shiftKey;
    update();
  }
}

function info() {
  return { altKey: active.altKey, shiftKey: active.shiftKey, x: active.x, y: active.y };
}

function findTarget() {
  for (const element of document.elementsFromPoint(active.x, active.y)) {
    const host = element.closest("[data-drop-target]");
    if (!host) continue;
    const options = targets.get(host);
    if (!options) continue;
    return { element: host, options, ok: Boolean(options.accepts?.(active.item, info())) };
  }
  return null;
}

function update() {
  for (const w of watchers) w.move?.(active.item, info());
  const { ghostBox } = active;
  ghostBox.style.transform = `translate(${active.x + 14}px, ${active.y + 12}px)`;

  const found = findTarget();
  const target = found?.ok ? found : null;
  if (active.target?.element !== target?.element) {
    active.target?.element.classList.remove("drop-hover");
    target?.element.classList.add("drop-hover");
    active.target?.options.onLeave?.(active.item);
  }
  if (target) target.options.onOver?.(active.item, info());
  active.target = target;
  active.refused = Boolean(found && !found.ok);
  document.body.classList.toggle("drop-refused", active.refused);

  cutHoles();
}

// The shade's holes follow the dragged item and the target where they are now. Layout can move under a still pointer (Player 2's
// timeline peeks in, a list redraws, a panel resizes), so this also runs every frame while dragging (badrfly 2026-11-21: Player 2's
// screen sometimes stayed dark while it took the drop): a hole cut from where the target was a moment ago missed it.
function cutHoles() {
  const holes = [];
  const source = active.sourceRect?.();
  if (source && source.width > 0) holes.push(source);
  if (active.target?.element.isConnected) holes.push(active.target.element.getBoundingClientRect());
  const path = shadePath(holes);
  if (path !== active.path) {
    active.path = path;
    active.shade.style.clipPath = path;
  }
}

function followLayout() {
  if (!active) return;
  cutHoles();
  active.frame = requestAnimationFrame(followLayout);
}

// The whole window minus each hole (even-odd fill). Row focus (editor.js) uses it for the same lights-off look.
export function shadePath(holes) {
  const w = window.innerWidth;
  const h = window.innerHeight;
  let path = `M0 0H${w}V${h}H0Z`;
  for (const r of holes) {
    const x = Math.max(0, r.left);
    const y = Math.max(0, r.top);
    const right = Math.min(w, r.right);
    const bottom = Math.min(h, r.bottom);
    if (right <= x || bottom <= y) continue;
    path += `M${x} ${y}H${right}V${bottom}H${x}Z`;
  }
  return `path(evenodd, "${path}")`;
}

function onUp(event) {
  active.x = event.clientX;
  active.y = event.clientY;
  update();
  const { target, item } = active;
  const dropInfo = info();
  finish();
  for (const w of watchers) w.end?.(item, target?.element ?? null, false);
  if (target) target.options.onDrop(item, dropInfo);
}

function cancel() {
  if (!active) return;
  const item = active.item;
  active.target?.options.onLeave?.(item);
  finish();
  for (const w of watchers) w.end?.(item, null, true);
}

function finish() {
  // The click that follows the pointerup would land on whatever is under the pointer
  window.addEventListener("click", swallowClick, { capture: true, once: true });
  setTimeout(() => window.removeEventListener("click", swallowClick, { capture: true }), 0);
  cancelAnimationFrame(active.frame);
  active.target?.element.classList.remove("drop-hover");
  active.shade.remove();
  active.ghostBox.remove();
  document.body.classList.remove("is-dragging", "drop-refused");
  window.removeEventListener("pointermove", onMove, true);
  window.removeEventListener("pointerup", onUp, true);
  window.removeEventListener("pointercancel", cancel, true);
  window.removeEventListener("keydown", onKey, true);
  window.removeEventListener("keyup", onKey, true);
  window.removeEventListener("blur", cancel);
  active = null;
}
