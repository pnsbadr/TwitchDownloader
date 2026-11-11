"use strict";

// ---------- every keyboard shortcut and mouse binding in the app ----------
//
// One list, so a new shortcut can be checked against the ones that exist (a duplicate in the same scope is reported in
// the console when the page loads) and so a help screen can list them later. The player bar (player-bar.js) dispatches
// its keys from here; other places handle their own keys but are still listed. Add new shortcuts HERE first.
//
// keys: what to press, written like "Space", "K", "Ctrl+Shift+Z", "ArrowLeft", "Shift+ArrowLeft", "Delete".
//       Letters are upper case and work with or without Shift unless Ctrl/Alt is held too ("K" = k or K).
//       "Ctrl" also means ⌘ on a Mac. Mouse bindings use names like "Wheel", "Click", "Right click".
// scope: where it works. Two bindings may share keys only in different scopes.
//   player        the Watch / Review player while it (or its control bar) has focus
//   player-mouse  mouse on the player's timeline
//   trim          a focused handle of the review dialog's trim bar (only with the Twitch player, which has no timeline of ours)
//   vertical      a focused handle in the vertical (9:16) editor
//   form          a text field in a small form (marker form, rename)

const KEYBINDS = [
  { id: "playPause", scope: "player", keys: ["Space", "K"], description: "Play / pause (a selected segment or card plays from its start to its end)" },
  { id: "playEnd", scope: "player", keys: ["Shift+Space"], description: "Play the last 3 s of the selected segment or card (in a review: of the clip)" },
  { id: "setStart", scope: "player", keys: ["I"], description: "Set the selected segment's or card's start to the playhead (in a review with nothing selected: the clip's)" },
  { id: "setEnd", scope: "player", keys: ["O"], description: "Set the selected segment's or card's end to the playhead (in a review with nothing selected: the clip's)" },
  { id: "back10", scope: "player", keys: ["J"], description: "Back 10 s" },
  { id: "fwd10", scope: "player", keys: ["L"], description: "Forward 10 s" },
  { id: "back5", scope: "player", keys: ["ArrowLeft"], description: "Back 5 s" },
  { id: "fwd5", scope: "player", keys: ["ArrowRight"], description: "Forward 5 s" },
  { id: "frameBack", scope: "player", keys: [","], description: "One frame back" },
  { id: "frameFwd", scope: "player", keys: ["."], description: "One frame forward" },
  { id: "marker", scope: "player", keys: ["M"], description: "Add a marker here" },
  { id: "segment", scope: "player", keys: ["S"], description: "Start a segment here, press again to end it" },
  { id: "clip", scope: "player", keys: ["X"], description: "Start a clip here, press again to end it (up to 3 min); it becomes a library card" },
  { id: "fullscreen", scope: "player", keys: ["F"], description: "Fullscreen" },
  { id: "zoomIn", scope: "player", keys: ["+", "="], description: "Zoom the timeline in" },
  { id: "zoomOut", scope: "player", keys: ["-"], description: "Zoom the timeline out" },
  { id: "fit", scope: "player", keys: ["0"], description: "Show the whole VOD (in a short clip's review: its 5-minute window)" },
  { id: "toolPointer", scope: "player", keys: ["V"], description: "Pointer tool: click to seek, click a segment to select it" },
  { id: "toolCut", scope: "player", keys: ["C"], description: "Cut tool: click a segment to split it in two" },
  { id: "toolZoom", scope: "player", keys: ["Z"], description: "Zoom tool: click the timeline to zoom in, right click to zoom out" },
  { id: "toggleSnap", scope: "player", keys: ["N"], description: "Snapping on/off: dragged edges stick to markers, segments, cards and the playhead" },
  { id: "toolHand", scope: "player", keys: ["H"], description: "Hand tool: drag the zoomed timeline to scroll it (only moves the view)" },
  { id: "undo", scope: "player", keys: ["Ctrl+Z"], description: "Undo your last marker/segment change (up to 10)" },
  { id: "redo", scope: "player", keys: ["Ctrl+Shift+Z", "Ctrl+Y"], description: "Redo" },
  { id: "deleteSelected", scope: "player", keys: ["Delete", "Backspace"], description: "Delete the selected segment" },
  { id: "escape", scope: "player", keys: ["Escape"], description: "Cancel marking a segment or clip, or unselect the selected segment or card" },

  { id: "wheelZoom", scope: "player-mouse", keys: ["Wheel"], description: "Zoom the timeline around the mouse" },
  { id: "wheelPan", scope: "player-mouse", keys: ["Shift+Wheel"], description: "Scroll the zoomed timeline" },
  { id: "clickSeek", scope: "player-mouse", keys: ["Click"], description: "Seek (pointer tool); select a segment, or a card on the purple lane; cut (cut tool); zoom in (zoom tool)" },
  { id: "nudgeStart", scope: "player-mouse", keys: ["Shift+Click ±10s/±1s"], description: "With a segment or card selected: move its start (without Shift: its end)" },
  { id: "rightClickZoomOut", scope: "player-mouse", keys: ["Right click"], description: "Zoom out (zoom tool)" },
  { id: "dragEdge", scope: "player-mouse", keys: ["Drag"], description: "Drag a selected segment's edge to change its start or end" },
  { id: "dragCardEdge", scope: "player-mouse", keys: ["Drag card edge"], description: "Drag a reference card's start or end (purple lane, or the reviewed/selected card's edges on the track)" },
  { id: "handDrag", scope: "player-mouse", keys: ["Hand drag"], description: "Scroll the zoomed timeline (hand tool)" },

  { id: "trimNudge", scope: "trim", keys: ["ArrowLeft", "ArrowRight"], description: "Move the focused handle 0.1 s" },
  { id: "trimNudgeBig", scope: "trim", keys: ["Shift+ArrowLeft", "Shift+ArrowRight"], description: "Move the focused handle 1 s" },

  { id: "verticalTrim", scope: "vertical", keys: ["ArrowLeft", "ArrowRight"], description: "Move the focused trim handle 0.1 s" },
  { id: "verticalTrimBig", scope: "vertical", keys: ["Shift+ArrowLeft", "Shift+ArrowRight"], description: "Move the focused trim handle 1 s" },
  { id: "verticalSplit", scope: "vertical", keys: ["ArrowUp", "ArrowDown"], description: "Move the top/bottom split" },

  { id: "formSave", scope: "form", keys: ["Enter"], description: "Save" },
  { id: "formCancel", scope: "form", keys: ["Escape"], description: "Cancel" },
];

// "Ctrl+Shift+Z", "K", "ArrowLeft", "Shift+ArrowLeft" for a key event (see the key format above)
function keybindName(event) {
  const ctrl = event.ctrlKey || event.metaKey;
  let key = event.key === " " ? "Space" : event.key;
  const single = key.length === 1;
  if (single) key = key.toUpperCase();
  // Shift is part of the name for named keys and for Ctrl/Alt combinations; "K" and "+" already say what was typed
  const shift = event.shiftKey && (!single || ctrl || event.altKey);
  return `${ctrl ? "Ctrl+" : ""}${event.altKey ? "Alt+" : ""}${shift ? "Shift+" : ""}${key}`;
}

const KEYBIND_INDEX = (() => {
  const index = new Map();
  for (const bind of KEYBINDS) {
    for (const key of bind.keys) {
      const slot = `${bind.scope}|${key}`;
      if (index.has(slot)) console.error(`Keybind clash in "${bind.scope}": ${key} is both ${index.get(slot).id} and ${bind.id}`);
      else index.set(slot, bind);
    }
  }
  return index;
})();

// The binding's id for this key event in a scope, or null
function keybindFor(scope, event) {
  return KEYBIND_INDEX.get(`${scope}|${keybindName(event)}`)?.id ?? null;
}

// "Ctrl+Z" for a binding id, for button titles (the first keys listed)
function keybindLabel(id) {
  const bind = KEYBINDS.find((x) => x.id === id);
  return bind ? bind.keys.join(", ") : "";
}
