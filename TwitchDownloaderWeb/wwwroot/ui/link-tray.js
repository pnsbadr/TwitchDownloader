// The link tray (badrfly 2026-11-20, "videos across streams"): while the link tool links a video, the segments picked so far show as
// chips under the tools bar. The picks stay when another VOD is opened in Player 1, so a video can take a story started in
// yesterday's stream and finished in today's. Chips: drag to reorder, × takes one out, a click opens its VOD at it. "Sort by stream
// time" puts them in the order they happened. The tray is kept in this tab (sessionStorage) so a reload doesn't lose the picks.

import { $, el, api, iconButton, toast } from "./core.js";
import { MARKER_COLORS } from "./colors.js";

const STORAGE_KEY = "clipco.p1.linking";

let hooks = null;   // { timeline, currentVodId(), openVodAt(vodId, seconds), seek(seconds), titleValue() }
let sortable = null;
const infos = new Map(); // vodId → promise of /api/vods/info (or null when it failed)

// A VOD's info, looked up once per page (chips and Player 1's "on another stream" pills show its date)
export function vodInfo(vodId) {
  const key = String(vodId);
  if (!infos.has(key)) infos.set(key, api("GET", `/api/vods/info?vod=${encodeURIComponent(key)}`).catch(() => null));
  return infos.get(key);
}

// "19 Nov" for a VOD whose info has arrived, else null (and the lookup starts; onReady runs once it's in)
const known = new Map();
export function vodDay(vodId, onReady) {
  const key = String(vodId);
  if (known.has(key)) return known.get(key);
  vodInfo(key).then((info) => {
    if (known.has(key)) return;
    known.set(key, info?.streamDateUtc ? shortDay(info.streamDateUtc) : null);
    onReady?.();
  });
  return null;
}

function shortDay(iso) {
  return new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

export function setUpLinkTray(options) {
  hooks = options;
  $("link-cancel").addEventListener("click", () => hooks.timeline.hooks.closeItemEditor?.());
  $("link-sort").addEventListener("click", () => hooks.timeline.sortPicksByStreamTime());
  // the title typed in the tools bar is kept with the picks
  $("item-title").addEventListener("sl-input", () => {
    if (hooks.timeline.linking) save(hooks.timeline.linking);
  });
}

// Linking saved in this tab, to go on after a reload (Player 1's first VOD opens, then the tray comes back)
export function savedLinking() {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function save(linking) {
  try {
    if (!linking) sessionStorage.removeItem(STORAGE_KEY);
    else sessionStorage.setItem(STORAGE_KEY, JSON.stringify({
      ids: linking.ids, items: [...linking.items.values()], ownerId: linking.ownerId, videoId: linking.videoId, owner: linking.owner,
      title: hooks.titleValue(),
    }));
  } catch {
    // Storage blocked: the tray still works for this page
  }
}

export function renderLinkTray(linking) {
  save(linking);
  const tray = $("link-tray");
  tray.hidden = !linking;
  document.body.classList.toggle("linking", Boolean(linking));
  if (!linking) return;

  const here = String(hooks.currentVodId() ?? "");
  const picks = linking.ids.map((id) => linking.items.get(id)).filter(Boolean);
  const streams = new Set(picks.map((p) => p.vodId));
  const what = linking.videoId ? "Adding segments" : "Linking a video";
  $("link-text").textContent = picks.length
    ? `${what}: ${picks.length} segment${picks.length === 1 ? "" : "s"}${streams.size > 1 ? ` from ${streams.size} streams` : ""}. Open another VOD to add its segments too.`
    : `${what}: click segments in the order they play. You can open other VODs and keep clicking.`;
  $("link-sort").disabled = picks.length < 2;

  const chips = picks.map((pick, i) => {
    const day = vodDay(pick.vodId, () => renderLinkTray(hooks.timeline.linking));
    const chip = el("div", {
      class: `link-chip${pick.vodId === here ? " here" : ""}${pick.removed ? " removed" : ""}`,
      "data-id": String(pick.id),
      title: pick.removed
        ? "Deleted from its timeline, still in this video. × takes it out."
        : pick.vodId === here ? "On this VOD: click to go to it" : "On another stream: click to open it there",
    },
      el("span", { class: "link-num num" }, String(i + 1)),
      el("span", { class: "link-dot", style: `background: ${MARKER_COLORS[pick.color] ?? MARKER_COLORS.yellow}` }),
      el("span", { class: "link-label" }, pick.label || "Segment"),
      el("span", { class: "link-day" }, day ?? ""),
    );
    const remove = iconButton({ icon: "x", label: "Take it out", onClick: (event) => {
      event.stopPropagation();
      hooks.timeline.unlinkPick(pick.id);
    } });
    remove.classList.add("link-x");
    chip.append(remove);
    chip.addEventListener("click", () => {
      if (pick.removed) return;
      if (pick.vodId === here) hooks.seek(pick.seconds ?? 0);
      else hooks.openVodAt(pick.vodId, pick.seconds ?? 0);
    });
    return chip;
  });
  // A bar where the stream changes
  const out = [];
  chips.forEach((chip, i) => {
    if (i > 0 && picks[i].vodId !== picks[i - 1].vodId) out.push(el("span", { class: "link-break", "aria-hidden": "true" }));
    out.push(chip);
  });
  $("link-chips").replaceChildren(...out);

  if (!sortable && window.Sortable) {
    sortable = window.Sortable.create($("link-chips"), {
      animation: 120,
      draggable: ".link-chip",
      filter: ".link-x",
      preventOnFilter: false,
      onEnd: () => {
        const ids = [...$("link-chips").querySelectorAll(".link-chip")].map((chip) => Number(chip.dataset.id));
        hooks.timeline.reorderPicks(ids);
      },
    });
  }
}

// "Add segments…" on a video: its segments go in the tray and linking starts on a VOD it uses (the one open in Player 1, else the one
// its last part is from, where it most likely goes on)
export async function addSegmentsTo(video) {
  if (!video?.canEdit) {
    toast("This video is view only. Make a copy to change it.");
    return;
  }
  const full = video.segments ? video : await api("GET", `/api/videos/${video.id}`).catch((error) => (toast(error.message, "error"), null));
  if (!full) return;
  const vods = full.segments.map((s) => String(s.vodId ?? full.vodId));
  const here = String(hooks.currentVodId() ?? "");
  if (!vods.includes(here)) {
    const last = [...full.segments].sort((a, b) => a.position - b.position).filter((s) => !s.removed).at(-1);
    await hooks.openVodAt(String(last?.vodId ?? full.vodId), last?.startSeconds ?? 0);
  }
  if (!hooks.timeline.vodId) return;
  hooks.timeline.startLinking(full);
}
