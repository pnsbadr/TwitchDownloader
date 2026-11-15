"use strict";

// ---------- every keyboard shortcut and mouse binding in the app ----------
//
// One list, so a new shortcut can be checked against the ones that exist (a duplicate in the same scope is reported in
// the console when the page loads), and the shortcuts help screen (? on the editor) is built from it. The editor
// dispatches its keys from here (keybindFor); a few places handle their own keys but are still listed. Add new
// shortcuts HERE first.
//
// keys: what to press, written like "Space", "K", "Ctrl+Shift+Z", "ArrowLeft", "Shift+ArrowLeft", "Delete".
//       Letters are upper case and work with or without Shift unless Ctrl/Alt is held too ("K" = k or K).
//       "Ctrl" also means ⌘ on a Mac. Mouse bindings use names like "Wheel", "Click", "Right click".
// scope: where it works (KEYBIND_SCOPES). Two bindings may share keys only in different scopes.

const KEYBIND_SCOPES = [
  { id: "player", title: "Editor", description: "Anywhere on the Editor screen except while typing. They work on the player whose timeline tab is open." },
  { id: "timeline-mouse", title: "Timeline (mouse)", description: "On the Player 1 and Player 2 timelines." },
  { id: "library", title: "Cards and lists", description: "A focused card in the left panel or a row of the export list." },
  { id: "vertical", title: "Vertical editor", description: "While Player 2 is the vertical (9:16) editor. The timeline's tool and zoom keys still work." },
  { id: "vertical-box", title: "Vertical editor: selected box", description: "After clicking a crop box." },
  { id: "form", title: "Titles and dialogs", description: "In the tools bar's title box and in pop-ups." },
];

const KEYBINDS = [
  { id: "playPause", scope: "player", keys: ["Space", "K"], description: "Play / pause (a selected item plays from its start to its end)" },
  { id: "playEnd", scope: "player", keys: ["Shift+Space"], description: "Play the last 3 s of the selected item" },
  { id: "setStart", scope: "player", keys: ["I"], description: "Move the selected item's start to the playhead" },
  { id: "setEnd", scope: "player", keys: ["O"], description: "Move the selected item's end to the playhead" },
  { id: "back10", scope: "player", keys: ["J"], description: "Back 10 s" },
  { id: "fwd10", scope: "player", keys: ["L"], description: "Forward 10 s" },
  { id: "back5", scope: "player", keys: ["ArrowLeft"], description: "Back 5 s" },
  { id: "fwd5", scope: "player", keys: ["ArrowRight"], description: "Forward 5 s" },
  { id: "frameBack", scope: "player", keys: [","], description: "One frame back" },
  { id: "frameFwd", scope: "player", keys: ["."], description: "One frame forward" },
  { id: "marker", scope: "player", keys: ["M"], description: "Add a moment marker at the playhead (Player 1)" },
  { id: "segment", scope: "player", keys: ["S"], description: "Start a segment here, press again to end it (Player 1)" },
  { id: "clip", scope: "player", keys: ["X"], description: "Start a clip here, press again to end it (up to 3 min; Player 1)" },
  { id: "fullscreen", scope: "player", keys: ["F"], description: "Fullscreen" },
  { id: "zoomIn", scope: "player", keys: ["+", "="], description: "Zoom the timeline in" },
  { id: "zoomOut", scope: "player", keys: ["-"], description: "Zoom the timeline out" },
  { id: "fit", scope: "player", keys: ["0"], description: "Fit the timeline (the whole VOD, or Player 2's item)" },
  { id: "toolPointer", scope: "player", keys: ["V"], description: "Pointer tool: click to seek, click an item to select it" },
  { id: "toolCut", scope: "player", keys: ["C"], description: "Cut tool: click a segment to split it in two (Player 1), drag across a part to cut it out (Player 2)" },
  { id: "toolZoom", scope: "player", keys: ["Z"], description: "Zoom tool: click the timeline to zoom in, right click to zoom out" },
  { id: "toolHand", scope: "player", keys: ["H"], description: "Hand tool: drag the zoomed timeline to scroll it (only moves the view)" },
  { id: "toggleSnap", scope: "player", keys: ["N"], description: "Snapping on/off: dragged edges stick to markers, segments, clips, cut-out parts and the playhead" },
  { id: "undo", scope: "player", keys: ["Ctrl+Z"], description: "Undo your last timeline change (up to 10)" },
  { id: "redo", scope: "player", keys: ["Ctrl+Shift+Z", "Ctrl+Y"], description: "Redo" },
  { id: "deleteSelected", scope: "player", keys: ["Delete", "Backspace"], description: "Delete the selected item (in a video in Player 2: take it out of the video)" },
  { id: "verticalToggle", scope: "player", keys: ["E"], description: "Turn Player 2 into the vertical (9:16) editor (a clip in Player 2)" },
  { id: "shortcutsHelp", scope: "player", keys: ["?"], description: "Show this list of shortcuts" },
  { id: "escape", scope: "player", keys: ["Escape"], description: "Cancel marking or linking, or unselect" },

  { id: "clickSeek", scope: "timeline-mouse", keys: ["Click"], description: "Seek; on an item, select it and go to its start (pointer tool). Split (cut tool), zoom in (zoom tool), pick segments for a video (link tool)" },
  { id: "doubleClickItem", scope: "timeline-mouse", keys: ["Double-click"], description: "Change the item's title and color (a row's name: rename the row)" },
  { id: "rightClickMenu", scope: "timeline-mouse", keys: ["Right click"], description: "The item's menu: title/color, make a copy, add to the export list, put cut parts back… (zoom tool: zoom out)" },
  { id: "dragEdge", scope: "timeline-mouse", keys: ["Drag an edge"], description: "Change the selected item's start or end" },
  { id: "dragItem", scope: "timeline-mouse", keys: ["Drag an item"], description: "Onto Player 2 to open it there, or onto another of your rows to move it" },
  { id: "altDragCopy", scope: "timeline-mouse", keys: ["Alt+Drag"], description: "Copy instead of move; a clip dropped on a video row becomes a segment at the same times" },
  { id: "cutDrag", scope: "timeline-mouse", keys: ["Cut tool drag"], description: "Cut that part out of Player 2's item (click a cut-out part to put it back)" },
  { id: "wheelZoom", scope: "timeline-mouse", keys: ["Wheel"], description: "Zoom around the mouse" },
  { id: "wheelPan", scope: "timeline-mouse", keys: ["Shift+Wheel"], description: "Scroll the zoomed timeline" },
  { id: "handDrag", scope: "timeline-mouse", keys: ["Hand tool drag"], description: "Scroll the zoomed timeline" },

  { id: "cardLoad", scope: "library", keys: ["Enter", "Double-click"], description: "Load the card in its player (export list: show its settings)" },
  { id: "cardMenu", scope: "library", keys: ["ContextMenu", "Shift+F10", "Right click"], description: "The card's menu: load, export, rename, delete" },
  { id: "cardDrag", scope: "library", keys: ["Drag"], description: "Drop a card on a player, a timeline row or the export list" },
  { id: "exportRemove", scope: "library", keys: ["Delete"], description: "Take the focused row off the export list" },
  { id: "dragCancel", scope: "library", keys: ["Escape"], description: "Cancel a drag" },

  { id: "verticalSplit", scope: "vertical", keys: ["ArrowUp", "ArrowDown"], description: "Move the top/bottom split (the split pill focused)" },
  { id: "verticalPlay", scope: "vertical", keys: ["Space", "K"], description: "Play / pause the part that will be rendered" },
  { id: "verticalStart", scope: "vertical", keys: ["I"], description: "Leave out everything before the playhead" },
  { id: "verticalEnd", scope: "vertical", keys: ["O"], description: "Leave out everything after the playhead" },
  { id: "verticalOneBox", scope: "vertical", keys: ["1"], description: "One box" },
  { id: "verticalTwoBoxes", scope: "vertical", keys: ["2"], description: "Two boxes (top and bottom)" },
  { id: "verticalSafeZones", scope: "vertical", keys: ["G"], description: "Safe zones: none, TikTok, Shorts, Reels" },
  { id: "verticalSnap", scope: "vertical", keys: ["N"], description: "Snapping on/off: boxes stick to the video's edges and middle and to each other (Alt while dragging: free)" },
  { id: "verticalUndo", scope: "vertical", keys: ["Ctrl+Z"], description: "Undo a box, layout, trim or part change" },
  { id: "verticalRedo", scope: "vertical", keys: ["Ctrl+Shift+Z", "Ctrl+Y"], description: "Redo" },
  { id: "verticalRender", scope: "vertical", keys: ["Ctrl+Enter"], description: "Render the vertical video" },
  { id: "verticalSplitPart", scope: "vertical", keys: ["S"], description: "Split into parts at the playhead (each part gets its own layout)" },
  { id: "verticalJoinPart", scope: "vertical", keys: ["J"], description: "Join the part at the playhead with the part before" },
  { id: "verticalRemovePart", scope: "vertical", keys: ["Delete", "Backspace"], description: "Leave the part at the playhead out of the render (the gap closes), or put it back" },
  { id: "verticalExit", scope: "vertical", keys: ["E"], description: "Back to the normal player" },
  { id: "verticalNudge", scope: "vertical-box", keys: ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"], description: "Move the selected box a little" },
  { id: "verticalNudgeBig", scope: "vertical-box", keys: ["Shift+ArrowLeft", "Shift+ArrowRight", "Shift+ArrowUp", "Shift+ArrowDown"], description: "Move the selected box more" },

  { id: "formSave", scope: "form", keys: ["Enter"], description: "Save (Edit original / Make a copy: Enter makes a copy)" },
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
