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
  { id: "player", title: "Editor", description: "Anywhere on the Editor screen except while typing. They work on the player whose timeline tab is open (Player 2 = the open project)." },
  { id: "timeline-mouse", title: "Timeline (mouse)", description: "On the Player 1 and Player 2 timelines." },
  { id: "library", title: "Cards and lists", description: "A focused card in the left panel or a row of the export list." },
  { id: "vertical", title: "9:16 view", description: "While Player 2 shows the 9:16 view of the project. The editor's keys above still work there." },
  { id: "vertical-box", title: "9:16 view: selected box", description: "After clicking a crop box." },
  { id: "form", title: "Titles and dialogs", description: "In the tools bar's title box and in pop-ups." },
];

const KEYBINDS = [
  { id: "playPause", scope: "player", keys: ["Space", "K"], description: "Play / pause (Player 1: a selected item plays from its start to its end; Player 2: the project from the playhead)" },
  { id: "playEnd", scope: "player", keys: ["Shift+Space"], description: "Play the last 3 s of the selected item (Player 2: of the selected piece, then on)" },
  { id: "setStart", scope: "player", keys: ["I"], description: "Move the selected item's start to the playhead (Player 2: trims the selected piece; it can reach into the rest of the VOD)" },
  { id: "setEnd", scope: "player", keys: ["O"], description: "Move the selected item's end to the playhead (Player 2: the selected piece)" },
  { id: "back10", scope: "player", keys: ["J"], description: "Back 10 s" },
  { id: "fwd10", scope: "player", keys: ["L"], description: "Forward 10 s" },
  { id: "back5", scope: "player", keys: ["ArrowLeft"], description: "Back 5 s" },
  { id: "fwd5", scope: "player", keys: ["ArrowRight"], description: "Forward 5 s" },
  { id: "frameBack", scope: "player", keys: [","], description: "One frame back" },
  { id: "frameFwd", scope: "player", keys: ["."], description: "One frame forward" },
  { id: "marker", scope: "player", keys: ["M"], description: "Add a moment marker at the playhead (Player 1)" },
  { id: "segment", scope: "player", keys: ["S"], description: "Player 1: start a segment here, press again to end it. Player 2: split the piece under the playhead (the selected one if several)" },
  { id: "clip", scope: "player", keys: ["X"], description: "Start a clip here, press again to end it (up to 3 min; Player 1)" },
  { id: "fullscreen", scope: "player", keys: ["F"], description: "Fullscreen" },
  { id: "zoomIn", scope: "player", keys: ["+", "="], description: "Zoom the timeline in" },
  { id: "zoomOut", scope: "player", keys: ["-"], description: "Zoom the timeline out" },
  { id: "fit", scope: "player", keys: ["0"], description: "Fit the timeline (the whole VOD, or the whole project)" },
  { id: "toolPointer", scope: "player", keys: ["V"], description: "Pointer tool: click to seek, click an item or piece to select it, drag to move (Player 2)" },
  { id: "toolCut", scope: "player", keys: ["C"], description: "Cut tool (razor): click a segment (Player 1) or a piece (Player 2) to split it there" },
  { id: "toolZoom", scope: "player", keys: ["Z"], description: "Zoom tool: click the timeline to zoom in, right click to zoom out" },
  { id: "toolHand", scope: "player", keys: ["H"], description: "Hand tool: drag the timeline to scroll it (only moves the view)" },
  { id: "toggleSnap", scope: "player", keys: ["N"], description: "Snapping on/off: dragged edges stick to other items' edges, the playhead and 0:00 (also boxes in the 9:16 view; Alt there: free)" },
  { id: "undo", scope: "player", keys: ["Ctrl+Z"], description: "Undo (Player 1: your last 10 changes on the VOD; Player 2: the project's last 100, box changes in the 9:16 view included)" },
  { id: "redo", scope: "player", keys: ["Ctrl+Shift+Z", "Ctrl+Y"], description: "Redo" },
  { id: "deleteSelected", scope: "player", keys: ["Delete", "Backspace"], description: "Player 1: delete the selected item. Player 2: take the selected pieces out of the project, leaving a gap (the clips they came from stay)" },
  { id: "rippleDelete", scope: "player", keys: ["Shift+Delete", "Shift+Backspace"], description: "Player 2: take the selected pieces out and slide what comes after them on their rows left to close the gap" },
  { id: "copyPieces", scope: "player", keys: ["Ctrl+C"], description: "Player 2: copy the selected pieces" },
  { id: "pastePieces", scope: "player", keys: ["Ctrl+V"], description: "Player 2: paste them at the playhead on the picked row" },
  { id: "verticalToggle", scope: "player", keys: ["E"], description: "Player 2: the 9:16 view of the project (crop boxes per piece) and back" },
  { id: "shortcutsHelp", scope: "player", keys: ["?"], description: "Show this list of shortcuts" },
  { id: "escape", scope: "player", keys: ["Escape"], description: "Cancel marking, linking or a drag, back to the pointer tool, or unselect" },

  { id: "clickSeek", scope: "timeline-mouse", keys: ["Click"], description: "Seek; on an item, select it and go there (pointer tool). Split (cut tool), zoom in (zoom tool), pick segments for a video (link tool)" },
  { id: "shiftClick", scope: "timeline-mouse", keys: ["Shift+Click"], description: "Player 2: add a piece to the selection (or take it out)" },
  { id: "marquee", scope: "timeline-mouse", keys: ["Drag on empty space"], description: "Player 2: select the pieces in the box" },
  { id: "doubleClickItem", scope: "timeline-mouse", keys: ["Double-click"], description: "Change the item's (or piece's) title and color; a row's name: rename the row" },
  { id: "rightClickMenu", scope: "timeline-mouse", keys: ["Right click"], description: "The item's menu: title/color, copy, layouts, close a gap, rows, show in Player 1, add to the export list… (zoom tool: zoom out)" },
  { id: "dragEdge", scope: "timeline-mouse", keys: ["Drag an edge"], description: "Change the selected item's start or end (Player 2: trim a piece, also past the original clip)" },
  { id: "dragItem", scope: "timeline-mouse", keys: ["Drag an item"], description: "Player 1: onto Player 2 or its timeline to put it in the project, or onto another of your rows to move it. Player 2: move pieces along a row or to another (under the last row: a new row)" },
  { id: "shiftDrag", scope: "timeline-mouse", keys: ["Shift+Drag"], description: "Player 2: move pieces to another row at the same time" },
  { id: "altDragCopy", scope: "timeline-mouse", keys: ["Alt+Drag"], description: "Player 1: copy instead of move; a clip dropped on a video row becomes a segment at the same times" },
  { id: "dragRow", scope: "timeline-mouse", keys: ["Drag a row's name"], description: "Player 2: move the row up or down" },
  { id: "wheelZoom", scope: "timeline-mouse", keys: ["Wheel"], description: "Player 1: zoom around the mouse. Player 2: scroll left and right" },
  { id: "wheelPan", scope: "timeline-mouse", keys: ["Shift+Wheel"], description: "Player 1: scroll the zoomed timeline. Player 2: scroll the rows up and down" },
  { id: "ctrlWheel", scope: "timeline-mouse", keys: ["Ctrl+Wheel"], description: "Player 2: zoom around the mouse" },
  { id: "handDrag", scope: "timeline-mouse", keys: ["Hand tool drag", "Middle drag"], description: "Scroll the timeline (Player 2 also has a scroll bar at the bottom)" },

  { id: "cardLoad", scope: "library", keys: ["Enter", "Double-click"], description: "Load the card: a VOD in Player 1, a clip, segment or video into Player 2's project, a project in Player 2 (export list: show its settings)" },
  { id: "cardMenu", scope: "library", keys: ["ContextMenu", "Shift+F10", "Right click"], description: "The card's menu: load, export, rename, delete" },
  { id: "cardDrag", scope: "library", keys: ["Drag"], description: "Drop a card on a player, a timeline row or the export list" },
  { id: "exportRemove", scope: "library", keys: ["Delete"], description: "Take the focused row off the export list" },
  { id: "dragCancel", scope: "library", keys: ["Escape"], description: "Cancel a drag" },

  { id: "verticalSplit", scope: "vertical", keys: ["ArrowUp", "ArrowDown"], description: "Move the top/bottom split (the split pill focused)" },
  { id: "verticalOneBox", scope: "vertical", keys: ["1"], description: "One box" },
  { id: "verticalTwoBoxes", scope: "vertical", keys: ["2"], description: "Two boxes (top and bottom)" },
  { id: "verticalSafeZones", scope: "vertical", keys: ["G"], description: "Safe zones: none, TikTok, Shorts, Reels" },
  { id: "verticalRender", scope: "vertical", keys: ["Ctrl+Enter"], description: "Render the project as a 9:16 video" },
  { id: "verticalKeyframe", scope: "vertical", keys: ["F"], description: "Keyframe at the playhead on the piece there: add one (the boxes glide between keyframes) or remove the one there" },
  { id: "verticalPrevKeyframe", scope: "vertical", keys: ["["], description: "Go to the piece's previous keyframe" },
  { id: "verticalNextKeyframe", scope: "vertical", keys: ["]"], description: "Go to the piece's next keyframe" },
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
