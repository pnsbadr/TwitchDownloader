// Their stream in Player 1's corner (badrfly 2026-11-20): a synced row's VOD plays small over Player 1, following it (same moment:
// Player 1's position + the row's shift, play/pause together), so both streams can be watched while marking. It starts muted; the
// speaker turns its sound on. Double-click (or the arrows) opens their VOD in Player 1; X closes it.

import { el, withTooltip } from "./core.js";
import { MediaPlayer } from "./media.js";

const DRIFT = 0.4;   // seconds off before it's seeked back in line

export class SyncedPip {
  // { vodId, title, shift(), clock() (Player 1's moment on this VOD's clock, null when unknown), playing(), onClose(), onSwap() }
  constructor(screen, options) {
    this.options = options;
    this.vodId = String(options.vodId);
    this.muted = true;
    this.media = el("div", { class: "pip-media" });
    this.note = el("p", { class: "pip-note", hidden: true });
    const sound = el("sl-icon-button", { library: "ph", name: "speaker-slash", label: "Sound on" });
    sound.addEventListener("click", (event) => {
      event.stopPropagation();
      this.muted = !this.muted;
      sound.name = this.muted ? "speaker-slash" : "speaker-high";
      sound.label = this.muted ? "Sound on" : "Sound off";
      this.applyMute();
    });
    const swap = el("sl-icon-button", { library: "ph", name: "arrows-in-simple", label: "Open their VOD in Player 1" });
    swap.addEventListener("click", (event) => {
      event.stopPropagation();
      options.onSwap();
    });
    const close = el("sl-icon-button", { library: "ph", name: "x", label: "Close" });
    close.addEventListener("click", (event) => {
      event.stopPropagation();
      this.close();
      options.onClose();
    });
    this.box = el("div", { class: "p1-pip" },
      el("div", { class: "pip-head" },
        el("span", { class: "pip-title" }, options.title),
        withTooltip(sound, "Their sound on/off"), withTooltip(swap, "Open their VOD in Player 1"), withTooltip(close, "Close")),
      el("div", { class: "pip-screen" }, this.media, this.note));
    this.box.addEventListener("dblclick", () => options.onSwap());
    screen.append(this.box);
    this.player = new MediaPlayer(this.media, { onState: () => this.applyMute() });
    this.opening = this.start();
  }

  async start() {
    const at = this.target() ?? 0;
    try {
      await this.player.open(this.vodId, Math.max(0, at));
      this.player.pause();
      this.applyMute();
    } catch (error) {
      this.say(`Couldn't play their stream: ${error.message}`);
    } finally {
      this.opening = null;
    }
  }

  target() {
    const clock = this.options.clock();
    return clock == null ? null : clock + this.options.shift();
  }

  applyMute() {
    const video = this.player.backend?.video;
    if (video) {
      video.dataset.ownVolume = "1";
      video.muted = this.muted;
    } else {
      this.player.backend?.player?.setMuted?.(this.muted);
    }
  }

  say(text) {
    this.note.textContent = text ?? "";
    this.note.hidden = !text;
  }

  // Every Player 1 tick: same moment, playing when Player 1 plays
  follow() {
    if (this.opening || !this.player.ready) return;
    const at = this.target();
    if (at == null) {
      if (this.player.playing()) this.player.pause();
      return;
    }
    const length = this.player.duration();
    if (at < 0 || (length > 0 && at > length + 1)) {
      this.say(at < 0 ? "Not streaming yet" : "Stream over");
      if (this.player.playing()) this.player.pause();
      return;
    }
    this.say(null);
    const playing = this.options.playing();
    const now = this.player.position();
    if (now == null || Math.abs(now - at) > DRIFT) this.player.seek(at);
    if (playing && !this.player.playing()) this.player.play();
    else if (!playing && this.player.playing()) this.player.pause();
  }

  close() {
    this.player.close();
    this.box.remove();
  }
}
