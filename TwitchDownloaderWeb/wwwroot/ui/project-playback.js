// Plays a project in Player 2 (step 4): from 0:00 to its end, what the timeline shows (ui/project.js flatten): the piece on the
// highest visible row at every moment, gaps black and silent. One MediaPlayer plays the VOD of the piece under the playhead;
// when the next stretch is from another VOD it opens that one (a short stutter in the preview; the render is frame-exact),
// from the same VOD it seeks (or plays on when it continues). Pieces on muted rows play without sound.
//
// Project time is kept here (time()): from the player's position inside a piece, from a clock in gaps.

import { flatten, stretchAt, projectLength } from "./project.js";

const TICK_MS = 40;
const END_SLACK = 0.04;

export class ProjectPlayback {
  // player: MediaPlayer; getContent() → the project content; black: element shown over the video in gaps;
  // onState(): playing/paused or the stretch changed
  constructor(player, { getContent, black, onState }) {
    this.player = player;
    this.getContent = getContent;
    this.black = black;
    this.onState = onState;
    this.t = 0;
    this.stretches = [];
    this.current = null;
    this.playingFlag = false;
    this.opening = null;     // { vodId, offset } while a VOD opens
    this.clock = null;       // { at: performance.now(), t } while a gap plays
    this.forcedMute = false;
    this.userMuted = false;
    this.timer = setInterval(() => this.tick(), TICK_MS);
  }

  get length() {
    return projectLength(this.getContent());
  }

  // The content changed: stay at the same time, and follow if what plays there changed
  refresh() {
    const before = this.current;
    this.stretches = flatten(this.getContent());
    const now = this.time();
    const s = stretchAt(this.stretches, now);
    const same = before && s && before.piece?.id === s.piece?.id && Math.abs(before.vodStart - s.vodStart) < 1e-6 && Math.abs(before.at - s.at) < 1e-6;
    if (!same || (s?.piece && before?.piece && (s.piece.start !== before.piece.start || s.piece.vodId !== before.piece.vodId))) this.seek(now, { quiet: true });
    else this.current = s;
  }

  // Project seconds at the playhead
  time() {
    const s = this.current;
    if (s?.piece && !this.opening && this.player.ready && String(this.player.vodId) === String(s.piece.vodId)) {
      const position = this.player.position();
      if (position != null && position >= s.vodStart - 0.5 && position <= s.vodEnd + 0.5) {
        return Math.min(Math.max(s.at + (position - s.vodStart), s.at), s.at + s.duration);
      }
    }
    if (this.clock) return this.clock.t + (performance.now() - this.clock.at) / 1000;
    return this.t;
  }

  playing() {
    return this.playingFlag;
  }

  seek(t, { quiet = false } = {}) {
    const length = this.length;
    t = Math.max(0, Math.min(t, length));
    this.t = t;
    this.stretches = flatten(this.getContent());
    const s = stretchAt(this.stretches, t);
    this.current = s;
    this.clock = this.playingFlag && !s?.piece ? { at: performance.now(), t } : null;
    if (!s?.piece) {
      this.showBlack(true);
      this.setMuted(false);
      if (this.player.ready && this.player.playing()) this.player.pause();
      if (!quiet) this.onState?.();
      return;
    }
    this.showBlack(false);
    this.setMuted(s.muted);
    const offset = s.vodStart + (t - s.at);
    const vodId = String(s.piece.vodId);
    if (this.player.vodId === vodId && (this.player.ready || !this.opening)) {
      if (this.player.ready) {
        const position = this.player.position();
        // a stretch that continues where the player is: no seek (no stutter)
        if (position == null || Math.abs(position - offset) > 0.12 || !this.player.playing()) this.player.seek(offset);
        if (this.playingFlag && !this.player.playing()) this.player.play();
      }
    } else {
      this.openVod(vodId, offset);
    }
    if (!quiet) this.onState?.();
  }

  async openVod(vodId, offset) {
    const target = { vodId, offset };
    this.opening = target;
    try {
      await this.player.open(vodId, offset);
    } catch (error) {
      if (this.opening === target) this.opening = null;
      this.onError?.(error);
      return;
    }
    if (this.opening !== target) return;
    this.opening = null;
    this.setMuted(this.current?.muted ?? false);
    // The time may have moved on while it opened (a seek elsewhere opened something else, or was in the same VOD)
    if (this.playingFlag) this.player.play();
    else this.player.pause();
    this.onState?.();
  }

  play() {
    if (!this.length) return;
    let t = this.time();
    if (t >= this.length - END_SLACK) t = 0;
    this.playingFlag = true;
    this.seek(t);
    if (this.current?.piece && this.player.ready && !this.opening) this.player.play();
    this.onState?.();
  }

  pause() {
    this.t = this.time();
    this.playingFlag = false;
    this.clock = null;
    if (this.player.ready) this.player.pause();
    this.onState?.();
  }

  toggle() {
    if (this.playingFlag) this.pause();
    else this.play();
  }

  // The player itself was paused (its own controls, the space bar on the video, the other player starting)
  playerPaused() {
    if (this.playingFlag && this.current?.piece && !this.opening && this.player.ready && !this.player.playing() && !this.advancing) {
      this.t = this.time();
      this.playingFlag = false;
      this.onState?.();
    }
  }

  tick() {
    if (!this.playingFlag) return;
    const s = this.current;
    const t = this.time();
    const end = s ? s.at + s.duration : this.length;
    if (!s) {
      this.pause();
      return;
    }
    if (s.piece) {
      if (this.opening || !this.player.ready) return;
      const position = this.player.position();
      if (position != null && position < s.vodEnd - END_SLACK) return;
    } else if (t < end - 0.001) {
      return;
    }
    // this stretch is over: the next one, or the end
    if (end >= this.length - END_SLACK) {
      this.t = this.length;
      this.playingFlag = false;
      this.clock = null;
      if (this.player.ready) this.player.pause();
      this.onState?.();
      return;
    }
    this.advancing = true;
    this.seek(end + 0.001);
    this.advancing = false;
  }

  showBlack(on) {
    if (this.black) this.black.hidden = !on;
  }

  // Muted rows: the video's own sound off while their piece plays, then back to what the user had
  setMuted(on) {
    const video = this.player.backend?.video;
    if (!video) return;
    if (on && !this.forcedMute) {
      this.userMuted = video.muted;
      video.muted = true;
      this.forcedMute = true;
    } else if (!on && this.forcedMute) {
      video.muted = this.userMuted;
      this.forcedMute = false;
    }
  }

  stop() {
    this.playingFlag = false;
    this.clock = null;
    this.current = null;
    this.t = 0;
    this.opening = null;
    this.setMuted(false);
    this.showBlack(false);
  }
}
