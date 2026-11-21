// Things done to several selected items at once that both Player 1 and the library offer (roadmap 1c, badrfly 2026-11-21).
//
// makeVideoFrom: a video of the selection in time order. Videos are made of the member's own segments only (badrfly: "only segments"):
// selected clips are copied onto a new segments row as segments exactly as they are (same times, same cut-out parts, same color; the
// server's copy-as-segment), after a question that can be cancelled; teammates' segments are copied onto the member's own row; moments
// and Twitch clips are left out.
//
// items: [{ kind: "segment" | "moment" | "clip", id, vodId, order (sorts them: stream time), own, title, color, twitch, copyRowId? }]
// Parts may come from several VODs (a video may span streams): each keeps its own VOD, clips get a row per VOD.
// shownOnVodId: Player 1's VOD. New rows for clips of another VOD are then synced rows shown on it, so they appear where they were picked.

import { api, toast } from "./core.js";
import { askText, confirmAction } from "./dialogs.js";

const ROW_NAME = "Clips as segments";

export async function makeVideoFrom(items, { shownOnVodId = null } = {}) {
  const ordered = [...items].sort((a, b) => a.order - b.order);
  const clips = ordered.filter((it) => it.kind === "clip" && !it.twitch);
  const theirs = ordered.filter((it) => it.kind === "segment" && !it.own);
  const used = ordered.filter((it) => it.kind === "segment" || (it.kind === "clip" && !it.twitch));
  const leftOut = ordered.length - used.length;
  if (!used.length) {
    toast("Pick segments or clips to make a video: moments and Twitch clips can't be in one.");
    return null;
  }
  if (clips.length || theirs.length) {
    const parts = [];
    if (clips.length) {
      parts.push(`Videos are made of segments, and ${clips.length === 1 ? "1 of these is a clip" : `${clips.length} of these are clips`}. `
        + `${clips.length === 1 ? "It's" : "They're"} copied onto a new "${ROW_NAME}" row as segments, exactly as ${clips.length === 1 ? "it is" : "they are"} `
        + "(same times, same cut-out parts); the clips stay as they are.");
    }
    if (theirs.length) parts.push(`${theirs.length === 1 ? "1 segment is" : `${theirs.length} segments are`} a teammate's: copied onto your own row first.`);
    const ok = await confirmAction({
      heading: clips.length ? "Copy the clips as segments?" : "Copy teammates' segments?",
      text: parts.join(" "),
      confirm: clips.length ? "Copy and make the video" : "Copy and make the video",
      danger: false,
    });
    if (!ok) return null;
  }
  const title = await askText({ heading: `Make a video of ${used.length} part${used.length === 1 ? "" : "s"}`, label: "Title", value: used[0].title ?? "", confirm: "Make video" });
  if (title === null) return null;
  if (!title.trim()) {
    toast("A video needs a title.");
    return null;
  }

  // One new segments row per VOD the clips come from
  const rows = new Map();
  const rowFor = async (vodId) => {
    const key = String(vodId);
    if (!rows.has(key)) {
      const body = { kind: "video", name: ROW_NAME };
      let on = key;
      if (shownOnVodId != null && String(shownOnVodId) !== key) {
        on = String(shownOnVodId);
        body.syncedVodId = Number(key);
      }
      rows.set(key, api("POST", `/api/vods/${encodeURIComponent(on)}/rows`, body));
    }
    return (await rows.get(key)).id;
  };

  const markerIds = [];
  let firstVod = null;
  try {
    for (const it of used) {
      let id = it.id;
      if (it.kind === "clip") {
        const marker = await api("POST", `/api/entries/${it.id}/copy-as-segment`, { rowId: await rowFor(it.vodId) });
        id = marker.id;
        // the clip's color too (the server gives a copied clip the first segment color)
        const color = it.color ?? "violet";
        if (marker.color !== color) {
          await api("PUT", `/api/markers/${id}`, { seconds: marker.seconds, endSeconds: marker.endSeconds, label: marker.label, color, cuts: null });
        }
      } else if (!it.own) {
        // onto the member's own row of the segment's VOD (on Player 1 the one shown there, for a synced row's segment)
        id = (await api("POST", `/api/markers/${it.id}/copy`, it.copyRowId != null ? { rowId: it.copyRowId } : {})).id;
      }
      markerIds.push(id);
      firstVod ??= it.vodId;
    }
    const video = await api("POST", `/api/vods/${encodeURIComponent(firstVod)}/videos`, { title: title.trim(), markerIds });
    toast(`Made "${video.title}" of ${markerIds.length} part${markerIds.length === 1 ? "" : "s"}${leftOut ? ` (${leftOut} moment${leftOut === 1 ? "" : "s"} or Twitch clip${leftOut === 1 ? "" : "s"} left out)` : ""}.`);
    return video;
  } catch (error) {
    toast(`Couldn't make the video: ${error.message}`, "error");
    return null;
  }
}
