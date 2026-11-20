// Marker/segment colors: same names and order as VodMarker.Colors on the server and the old page (now removed)
// (add new ones at the end)
export const MARKER_COLORS = {
  yellow: "#f2c230", blue: "#3d8bfd", pink: "#ff5fa2", orange: "#ff8a3d",
  red: "#ff4d4d", lime: "#9be15d", teal: "#2ec4b6", violet: "#a78bfa", gray: "#a1a1aa",
};

// A clip's color (badrfly 2026-11-20: clips have colors too; none / violet = the clip violet)
export const CLIP_COLOR = "#8b7cf6";
export const clipColor = (name) => (name && name !== "violet" && MARKER_COLORS[name]) || CLIP_COLOR;

// Streams in a video that spans several VODs (link tray chips): first stream, second, …
export const STREAM_COLORS = ["#f5c542", "#4fb3ff", "#ff5fa2", "#2ec4b6", "#ff8a3d", "#9be15d"];
