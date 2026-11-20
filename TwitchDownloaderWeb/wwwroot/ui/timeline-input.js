// What the mouse does on every timeline (Player 1's rows and Player 2's project), so tools and the wheel work and look the same
// everywhere: timelineCursor() / rulerCursor() pick the cursor for a tool and what's under the mouse, timelineWheel() handles the
// wheel. Both timelines call them; change them here, not in one timeline.

// The cut tool's cursor, like Premiere Pro's Razor: a blade with a dotted line through the hotspot (where the cut goes); crossed out
// in red where a click can't cut
const razorSvg = (blocked) => `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">`
  + `<line x1="5.5" y1="1" x2="5.5" y2="27" stroke="#000" stroke-width="3"/>`
  + `<line x1="5.5" y1="1" x2="5.5" y2="27" stroke="#fff" stroke-width="1.5" stroke-dasharray="2 2"/>`
  + `<g transform="translate(16 12) rotate(40)"><rect x="-8" y="-4.5" width="16" height="9" rx="1.5" fill="${blocked ? "#9a9a9a" : "#fff"}" stroke="#000" stroke-width="1.5"/>`
  + `<rect x="-4.5" y="-1.2" width="9" height="2.4" rx="1.2" fill="#000"/><line x1="-8" y1="3" x2="8" y2="3" stroke="#000" stroke-width="1"/></g>`
  + (blocked ? `<line x1="8" y1="21" x2="24" y2="3" stroke="#e5484d" stroke-width="3" stroke-linecap="round"/>` : "")
  + `</svg>`;
const razorCursor = (blocked) => `url("data:image/svg+xml,${encodeURIComponent(razorSvg(blocked))}") 5 14, ${blocked ? "not-allowed" : "col-resize"}`;

// The clip tool's cursor: a film strip beside a line through the hotspot (where the clip starts when dragged from there)
const clipSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">`
  + `<line x1="5.5" y1="1" x2="5.5" y2="27" stroke="#000" stroke-width="3"/>`
  + `<line x1="5.5" y1="1" x2="5.5" y2="27" stroke="#fff" stroke-width="1.5"/>`
  + `<rect x="10" y="7" width="16" height="14" rx="2" fill="#8b7cf6" stroke="#000" stroke-width="1.5"/>`
  + `<rect x="14" y="10" width="8" height="8" rx="1" fill="#fff" stroke="#000" stroke-width="1"/>`
  + [9.5, 13.5, 17.5].map((y) => `<rect x="11.2" y="${y}" width="1.6" height="1.6" fill="#000"/><rect x="23.2" y="${y}" width="1.6" height="1.6" fill="#000"/>`).join("")
  + `</svg>`;
const clipCursor = `url("data:image/svg+xml,${encodeURIComponent(clipSvg)}") 5 14, crosshair`;

export const CURSORS = {
  arrow: "default",        // pointer tool over nothing it can take hold of
  item: "grab",            // pointer tool over an item / piece that can be dragged
  dragging: "grabbing",    // while moving an item, or the hand tool while panning
  edge: "ew-resize",       // over (or dragging) an edge that trims
  razor: razorCursor(false),
  razorBlocked: razorCursor(true),
  clip: clipCursor,        // clip tool: drag across the timeline to mark a clip
  zoom: "zoom-in",         // zoom tool (click zooms in, right-click out)
  hand: "grab",
  ruler: "col-resize",     // the ruler scrubs the playhead (every tool but the hand, which pans)
};

// tool: pointer | cut | clip | zoom | hand; over: null | "item" | "edge"; pressing: null | "drag" | "edge" | "pan";
// canCut: the cut tool would cut here
export function timelineCursor({ tool, over = null, pressing = null, canCut = false }) {
  if (pressing === "pan" || pressing === "drag") return CURSORS.dragging;
  if (pressing === "edge") return CURSORS.edge;
  switch (tool) {
    case "cut": return canCut ? CURSORS.razor : CURSORS.razorBlocked;
    case "clip": return CURSORS.clip;
    case "zoom": return CURSORS.zoom;
    case "hand": return CURSORS.hand;
    default: return over === "edge" ? CURSORS.edge : over === "item" ? CURSORS.item : CURSORS.arrow;
  }
}

export function rulerCursor(tool, pressing = null) {
  if (tool === "hand") return pressing === "pan" ? CURSORS.dragging : CURSORS.hand;
  if (tool === "zoom") return CURSORS.zoom;
  return CURSORS.ruler;
}

// The wheel, like video editors: scroll left/right (a trackpad's sideways swipe too); Ctrl (or Cmd) + wheel zooms around the
// mouse; Shift + wheel scrolls the rows up and down. zoomAt(factor, seconds), panBy(seconds), span = seconds shown.
export function timelineWheel(event, { x, width, span, xToTime, zoomAt, panBy, rows }) {
  event.preventDefault();
  if (event.ctrlKey || event.metaKey) {
    zoomAt(event.deltaY > 0 ? 1.25 : 0.8, xToTime(x, width));
    return;
  }
  if (event.shiftKey && Math.abs(event.deltaY) > Math.abs(event.deltaX)) {
    if (rows) rows.scrollTop += event.deltaY;
    return;
  }
  const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
  panBy((delta / width) * span);
}
