// Side by side (badrfly 2026-11-20, "Sync with"): two streams of the same moment playing next to each other, so a synced row can be
// lined up by eye and ear instead of one row at a time. Opened from a Player 2 piece's menu ("Line up side by side…") on a piece that
// plays at the same time as a piece of another VOD on another row: `top` = that partner (it leads), `below` = the piece being lined up.
//
// The partner plays; the other follows it at the same moment of the edit's clock, plus what's been moved here. < / > (or the buttons)
// move it 0.1 s, Shift 1 s; "Match by sound" asks the server (same as after a sync). Under the screens both sound waves around the
// playhead, so a laugh or a shout shows where they meet. Save slides the piece's footage (an edit change: undoable, and saved for
// the pair, so the next Sync with between these two VODs lands lined up); Cancel leaves it as it was.

import { el, api, toast, iconButton } from "./core.js";
import { MediaPlayer, formatClock } from "./media.js";
import { drawWave, want } from "./waves.js";

const FOLLOW_TOLERANCE = 0.2;   // seconds the follower may drift before it's moved back
const WAVE_SPAN = 16;           // seconds of sound wave shown around the playhead
const TICK_MS = 100;
const AUDIO_MATCH_SECONDS = 45;

const end = (p) => p.at + (p.end - p.start);
const round = (x) => Math.round(x * 1000) / 1000;

// top, below: the two pieces as they are now; names: what to call each; now: the edit's clock at the playhead.
// onSave(delta): slide `below` by delta seconds. Resolves when closed.
export function openSyncView({ top, below, names, now, onSave }) {
  return new Promise((resolve) => {
    const from = Math.max(top.at, below.at);
    const to = Math.min(end(top), end(below));
    if (to - from < 0.5) {
      toast("These two don't play at the same time.", "error");
      resolve(false);
      return;
    }
    // below's VOD second = top's + base + moved
    const base = (below.start - below.at) - (top.start - top.at);
    let moved = 0;
    let t = Math.min(Math.max(now ?? from, from), to - 0.5);   // the edit's clock, while paused
    let playing = false;
    let saved = false;

    const screen = (label) => {
      const media = el("div", { class: "player-media" });
      const note = el("p", { class: "player-note" }, "Loading…");
      const clock = el("span", { class: "num sv-clock" }, "");
      const mute = iconButton({ icon: "speaker-high", label: `Sound of ${label} on/off` });
      return { media, note, clock, mute, box: el("div", { class: "sv-side" },
        el("div", { class: "player-screen sv-screen" }, media, note),
        el("div", { class: "sv-caption" }, el("span", { class: "sv-name" }, label), clock, mute)) };
    };
    const a = screen(names.top);
    const b = screen(names.below);
    const movedText = el("span", { class: "num sv-moved" }, "");
    const waves = el("canvas", { class: "sv-waves" });
    const playButton = iconButton({ icon: "play", label: "Play / pause (Space)", onClick: () => toggle() });
    const nudge = (d, icon, label) => iconButton({ icon, label, onClick: () => move(d) });
    const matchButton = el("sl-button", { size: "small" }, el("sl-icon", { slot: "prefix", library: "ph", name: "waveform" }), "Match by sound");
    const resetButton = el("sl-button", { size: "small", variant: "text" }, "Reset");
    const save = el("sl-button", { slot: "footer", variant: "primary" }, "Save");
    const cancel = el("sl-button", { slot: "footer" }, "Cancel");
    const box = el("sl-dialog", { label: "Line up side by side", class: "sync-dialog" },
      el("div", { class: "sv-screens" }, a.box, b.box),
      waves,
      el("div", { class: "player-controls sv-controls" },
        playButton,
        iconButton({ icon: "arrow-counter-clockwise", label: "Back 5 s", onClick: () => seek(time() - 5) }),
        el("span", { class: "controls-gap" }),
        nudge(-1, "caret-double-left", `${names.below} 1 s earlier (Shift+<)`),
        nudge(-0.1, "caret-left", `${names.below} 0.1 s earlier (<)`),
        movedText,
        nudge(0.1, "caret-right", `${names.below} 0.1 s later (>)`),
        nudge(1, "caret-double-right", `${names.below} 1 s later (Shift+>)`),
        el("span", { class: "controls-gap" }),
        matchButton, resetButton),
      el("p", { class: "faint xs-note" },
        `${names.top} leads; ${names.below} follows at the same moment. Move ${names.below} until both say and do the same thing at once: `
        + "the spikes in the sound waves meet on the line."),
      cancel, save);

    const pa = new MediaPlayer(a.media, { onState: () => renderButtons() });
    const pb = new MediaPlayer(b.media, { onState: () => renderButtons() });
    const vodTime = (which, at) => which === "a" ? top.start + (at - top.at) : top.start + (at - top.at) + base + moved;

    function time() {
      if (playing && pa.ready) {
        const position = pa.position();
        if (position != null) return Math.min(Math.max(top.at + (position - top.start), from), to);
      }
      return t;
    }

    function renderButtons() {
      const icon = playButton.querySelector("sl-icon-button");
      if (icon) icon.name = playing ? "pause" : "play";
      for (const [side, player] of [[a, pa], [b, pb]]) {
        const v = player.volume;
        const button = side.mute.querySelector("sl-icon-button");
        if (button) {
          button.name = v?.muted ? "speaker-slash" : "speaker-high";
          button.disabled = !v;
        }
      }
      movedText.textContent = `${moved > 0 ? "+" : moved < 0 ? "−" : "±"}${Math.abs(moved).toFixed(2)} s`;
      save.disabled = Math.abs(moved) < 0.0005;
      resetButton.disabled = Math.abs(moved) < 0.0005;
    }

    function seek(at) {
      t = Math.min(Math.max(at, from), to - 0.05);
      if (pa.ready) pa.seek(vodTime("a", t));
      if (pb.ready) pb.seek(vodTime("b", t));
    }

    function toggle() {
      if (!pa.ready || !pb.ready) return;
      if (playing) {
        t = time();
        playing = false;
        pa.pause();
        pb.pause();
      } else {
        if (time() >= to - 0.1) seek(from);
        playing = true;
        pa.seek(vodTime("a", t));
        pb.seek(vodTime("b", t));
        pa.play();
        pb.play();
      }
      renderButtons();
    }

    function move(d) {
      const limit = -(below.start + moved); // not before its VOD's start
      moved = round(moved + Math.max(d, limit));
      if (pb.ready) pb.seek(vodTime("b", time()));
      renderButtons();
    }

    // The follower stays with the leader
    const timer = setInterval(() => {
      if (!pa.ready || !pb.ready) return;
      const now = time();
      a.clock.textContent = formatClock(vodTime("a", now));
      b.clock.textContent = formatClock(vodTime("b", now));
      if (!playing) return;
      if (now >= to - 0.05 || !pa.playing()) {
        t = now;
        playing = false;
        pa.pause();
        pb.pause();
        renderButtons();
        return;
      }
      const target = vodTime("b", now);
      const position = pb.position();
      if (position == null || Math.abs(position - target) > FOLLOW_TOLERANCE) pb.seek(target);
      if (!pb.playing()) pb.play();
    }, TICK_MS);

    // Both sound waves around the playhead: top's above the line, below's under it (with what's been moved)
    let frame = 0;
    const draw = () => {
      frame = requestAnimationFrame(draw);
      const width = waves.clientWidth;
      const height = waves.clientHeight;
      if (!width || !height) return;
      const dpr = window.devicePixelRatio || 1;
      if (waves.width !== Math.round(width * dpr)) {
        waves.width = Math.round(width * dpr);
        waves.height = Math.round(height * dpr);
      }
      const ctx = waves.getContext("2d");
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, width, height);
      const now = time();
      const half = height / 2;
      for (const [which, piece, y, color] of [["a", top, half / 2, "#c9c3ff"], ["b", below, half + half / 2, "#7fd6c2"]]) {
        const s = vodTime(which, now - WAVE_SPAN / 2);
        want(piece.vodId, s, s + WAVE_SPAN);
        drawWave(ctx, { vodId: piece.vodId, start: s, end: s + WAVE_SPAN, x1: 0, x2: width, midY: y, halfHeight: half / 2 - 3, color, maxSeconds: WAVE_SPAN + 1 });
      }
      ctx.fillStyle = "rgb(255 255 255 / 0.15)";
      ctx.fillRect(0, half - 0.5, width, 1);
      ctx.fillStyle = "#fff";
      ctx.fillRect(Math.round(width / 2) - 0.5, 0, 1, height);
      ctx.font = "11px Barlow, system-ui, sans-serif";
      ctx.fillStyle = "rgb(255 255 255 / 0.7)";
      ctx.textBaseline = "top";
      ctx.fillText(names.top, 6, 4);
      ctx.fillText(names.below, 6, half + 4);
    };
    frame = requestAnimationFrame(draw);

    matchButton.addEventListener("click", async () => {
      matchButton.loading = true;
      try {
        const at = vodTime("a", time());
        const otherAt = vodTime("b", time());
        const result = await api("POST", `/api/vods/${top.vodId}/sync/${below.vodId}/audio`, { at, otherAt, seconds: AUDIO_MATCH_SECONDS });
        if (!box.isConnected) return;
        if (result.trusted) {
          move(result.shift);
          toast(`Matched by sound (${Math.round(result.confidence * 100)}% sure): ${result.shift > 0 ? "+" : ""}${result.shift.toFixed(2)} s. Press Save to keep it.`);
        } else {
          toast(`Couldn't match by sound: ${result.reason}`, "error");
        }
      } catch (error) {
        toast(error.message, "error");
      } finally {
        matchButton.loading = false;
      }
    });
    resetButton.addEventListener("click", () => move(-moved));
    for (const [side, player] of [[a, pa], [b, pb]]) {
      side.mute.addEventListener("click", () => {
        const v = player.volume;
        if (v) player.setVolume(null, !v.muted);
        renderButtons();
      });
    }

    // Keys while it's open: Space, < / > (Shift: 1 s), Esc closes (the dialog's own)
    const onKey = (event) => {
      if (event.target?.closest?.("sl-input, input, textarea")) return;
      const step = event.shiftKey ? 1 : 0.1;
      if (event.key === " ") toggle();
      else if (event.key === "<" || event.key === ",") move(-step);
      else if (event.key === ">" || event.key === ".") move(step);
      else return;
      event.preventDefault();
      event.stopPropagation();
    };
    document.addEventListener("keydown", onKey, true);

    save.addEventListener("click", () => {
      saved = true;
      box.hide();
    });
    cancel.addEventListener("click", () => box.hide());
    box.addEventListener("sl-after-hide", (event) => {
      if (event.target !== box) return;
      document.removeEventListener("keydown", onKey, true);
      clearInterval(timer);
      cancelAnimationFrame(frame);
      pa.close();
      pb.close();
      box.remove();
      if (saved && Math.abs(moved) >= 0.0005) onSave(moved);
      resolve(saved);
    });
    document.body.append(box);
    customElements.whenDefined("sl-dialog").then(() => box.show());
    renderButtons();

    const opened = (side, player, which) => player.open(which === "a" ? top.vodId : below.vodId, vodTime(which, t))
      .then(() => {
        if (!box.isConnected) return;
        player.pause();
        side.note.hidden = true;
        renderButtons();
      })
      .catch((error) => { side.note.textContent = `Couldn't play this VOD: ${error.message}`; });
    opened(a, pa, "a");
    opened(b, pb, "b");
  });
}
