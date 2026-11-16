// Playing a Twitch VOD in the editor (frontend rework step 3). Ported from the old page (app.js): the app's own player
// (hls.js over /api/vods/{id}/play.m3u8, segments straight from Twitch's CDN or through the server) with the official
// Twitch embed as a fallback. Tokens never reach the browser. MediaPlayer below is the editor's wrapper around one
// player slot; the editor draws its own controls, so the <video> has none.

import { problemMessage, formatDuration } from "./core.js";

const TWITCH_EMBED_SRC = "https://player.twitch.tv/js/embed/v1.js";
const HLS_SRC = "https://cdn.jsdelivr.net/npm/hls.js@1.6.16/dist/hls.min.js";
const PLAYER_MODE_STORAGE_KEY = "clipLibrary.playerMode"; // "app" (default) or "twitch"
const PLAYER_VOLUME_STORAGE_KEY = "clipLibrary.playerVolume";
const HLS_START_TIMEOUT_MS = 20000;
// Through the server it's the last try before Twitch's player, and a segment may first need its saved clean audio swapped in
const HLS_PROXY_START_TIMEOUT_MS = 45000;
// Where Twitch's CDN refuses this page (no CORS header), it does every time: remember that for a day so a reload doesn't wait for it again
const SEGMENT_MODE_STORAGE_KEY = "clipLibrary.segmentMode";
const SEGMENT_MODE_REMEMBER_MS = 24 * 3600 * 1000;
const scripts = new Map();

function loadScript(src, errorMessage) {
  if (!scripts.has(src)) {
    scripts.set(src, new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = src;
      script.async = true;
      script.onload = resolve;
      script.onerror = () => {
        scripts.delete(src);
        script.remove();
        reject(new Error(errorMessage));
      };
      document.head.append(script);
    }));
  }
  return scripts.get(src);
}

function loadTwitchEmbed() {
  if (window.Twitch?.Player) return Promise.resolve();
  return loadScript(TWITCH_EMBED_SRC, "Couldn't load the Twitch player. Check the internet connection.");
}

function loadHls() {
  if (window.Hls) return Promise.resolve();
  return loadScript(HLS_SRC, "Couldn't load the app player.");
}

function loadPlayerMode() {
  try {
    return localStorage.getItem(PLAYER_MODE_STORAGE_KEY) === "twitch" ? "twitch" : "app";
  } catch {
    return "app";
  }
}

function savePlayerMode(mode) {
  try {
    localStorage.setItem(PLAYER_MODE_STORAGE_KEY, mode);
  } catch {
    // Still applies to the player that is open now
  }
}

function loadPlayerVolume(video) {
  try {
    const saved = JSON.parse(localStorage.getItem(PLAYER_VOLUME_STORAGE_KEY));
    if (saved && Number.isFinite(saved.volume)) {
      video.volume = Math.min(Math.max(saved.volume, 0), 1);
      video.muted = !!saved.muted;
      return;
    }
  } catch {
    // Unavailable or unreadable storage: use the default
  }
  video.volume = 0.5;
}

function savePlayerVolume(video) {
  try {
    localStorage.setItem(PLAYER_VOLUME_STORAGE_KEY, JSON.stringify({ volume: video.volume, muted: video.muted }));
  } catch {
    // Still applies for this visit
  }
}





// The embed's "time" option: "1h2m3s"
function embedTime(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 3600)}h${Math.floor((s % 3600) / 60)}m${s % 60}s`;
}

// Why the app player couldn't start. retryWithProxy: a loading error that might be the CDN refusing this page, so
// loading the segments through the server may work.
class PlayerUnavailable extends Error {
  constructor(message, retryWithProxy = false) {
    super(message);
    this.retryWithProxy = retryWithProxy;
  }
}

function describeHlsError(data) {
  const details = String(data?.details ?? "");
  if (/incompatible|codec/i.test(details)) return "this browser can't play this VOD's video format";
  if (data?.type === "networkError") return "couldn't load the video from Twitch";
  return `playback error (${details || "unknown"})`;
}

// The app's own player: a <video> fed by hls.js (or the browser's own HLS support, e.g. Safari)
class HlsBackend {
  constructor(container) {
    this.container = container;
    this.kind = "app";
    this.video = null;
    this.hls = null;
    this.ready = false;
    this.cleanSegments = 0;
    this.details = null; // the playing rendition's playlist (hls.js), for the control bar's timeline
    this.onDetails = null;
  }

  // Resolves once the first frame at startSeconds is loaded; rejects with PlayerUnavailable if it can't play
  async open(vodId, startSeconds, segmentMode) {
    const src = `/api/vods/${encodeURIComponent(vodId)}/play.m3u8${segmentMode === "proxy" ? "?segments=proxy" : ""}`;
    // Ask first, so a sub-only or deleted VOD gives the server's reason instead of a bare loading error (the
    // server keeps the master for a few minutes, so the player's own request for it is cheap)
    let check;
    try {
      check = await fetch(src);
    } catch {
      throw new PlayerUnavailable("couldn't reach the app");
    }
    if (!check.ok) {
      let problem = null;
      try {
        problem = await check.json();
      } catch {
        // Not a problem response
      }
      throw new PlayerUnavailable(problemMessage(problem, check.status));
    }
    // Muted segments the server will play with saved clean audio instead
    this.cleanSegments = Number(check.headers.get("X-Clean-Audio-Segments")) || 0;
    // Closed while it asked (another VOD or player opened in this slot): don't put a video in the slot it now uses
    if (this.closed) throw new PlayerUnavailable("closed");

    const video = document.createElement("video");
    video.controls = false;
    video.playsInline = true;
    video.autoplay = true;
    loadPlayerVolume(video);
    video.addEventListener("volumechange", () => savePlayerVolume(video));
    this.video = video;
    this.container.replaceChildren(video);

    try {
      await loadHls();
    } catch {
      // Without hls.js only browsers with their own HLS support (Safari) can play it
    }
    if (this.closed) throw new PlayerUnavailable("closed");
    if (window.Hls?.isSupported()) await this.openHls(src, startSeconds, segmentMode);
    else if (video.canPlayType("application/vnd.apple.mpegurl")) await this.openNative(src, startSeconds);
    else throw new PlayerUnavailable(window.Hls ? "this browser can't play HLS video" : "couldn't load hls.js");
  }

  openHls(src, startSeconds, segmentMode) {
    const video = this.video;
    const hls = new Hls({
      startPosition: startSeconds,
      // Fail over to the next option quickly if segments won't load at all instead of retrying for a minute
      fragLoadPolicy: {
        default: {
          maxTimeToFirstByteMs: 10000,
          maxLoadTimeMs: 120000,
          timeoutRetry: { maxNumRetry: 2, retryDelayMs: 0, maxRetryDelayMs: 0 },
          errorRetry: { maxNumRetry: 2, retryDelayMs: 500, maxRetryDelayMs: 2000 },
        },
      },
    });
    this.hls = hls;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        if (!this.ready) reject(new PlayerUnavailable("the video took too long to start", true));
      }, segmentMode === "proxy" ? HLS_PROXY_START_TIMEOUT_MS : HLS_START_TIMEOUT_MS);
      video.addEventListener("loadeddata", () => {
        clearTimeout(timeout);
        this.ready = true;
        resolve();
      }, { once: true });
      const onLevel = (_, data) => {
        if (this.hls !== hls) return;
        this.details = data.details;
        this.onDetails?.(data.details);
      };
      hls.on(Hls.Events.LEVEL_LOADED, onLevel);
      hls.on(Hls.Events.LEVEL_UPDATED, onLevel);
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (this.hls !== hls) return;
        // A segment straight from Twitch's CDN failed before anything played (usually the CDN refusing this page: CORS).
        // hls.js would keep trying other qualities until the start timeout; go through the server now instead.
        if (!this.ready && segmentMode === "direct" && data.type === Hls.ErrorTypes.NETWORK_ERROR && data.details === Hls.ErrorDetails.FRAG_LOAD_ERROR) {
          clearTimeout(timeout);
          reject(new PlayerUnavailable("couldn't load the video from Twitch's CDN", true));
          return;
        }
        if (!data.fatal) return;
        if (!this.ready) {
          clearTimeout(timeout);
          reject(new PlayerUnavailable(describeHlsError(data), data.type === Hls.ErrorTypes.NETWORK_ERROR));
          return;
        }
        // Once it has played, keep going: a dropped connection or a decoding hiccup shouldn't swap the player
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
        else hls.startLoad();
      });
      hls.loadSource(src);
      hls.attachMedia(video);
    });
  }

  openNative(src, startSeconds) {
    const video = this.video;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new PlayerUnavailable("the video took too long to start")), HLS_START_TIMEOUT_MS);
      video.addEventListener("loadedmetadata", () => {
        video.currentTime = startSeconds;
      }, { once: true });
      video.addEventListener("loadeddata", () => {
        clearTimeout(timeout);
        this.ready = true;
        resolve();
      }, { once: true });
      video.addEventListener("error", () => {
        clearTimeout(timeout);
        if (!this.ready) reject(new PlayerUnavailable("couldn't play the video"));
      }, { once: true });
      video.src = src;
    });
  }

  // The renditions, best first, for the quality menu (hls.js only; it keeps them lowest bitrate first)
  levels() {
    const levels = this.hls?.levels.map((level, index) => ({ index, bitrate: level.bitrate, name: level.name || (level.height ? `${level.height}p` : `Quality ${index + 1}`) })) ?? [];
    return levels.sort((a, b) => b.bitrate - a.bitrate);
  }

  get level() {
    return this.hls?.autoLevelEnabled === false ? this.hls.currentLevel : -1;
  }

  set level(index) {
    if (this.hls) this.hls.currentLevel = index; // -1 = automatic
  }

  position() {
    return this.ready ? this.video.currentTime : null;
  }

  playFrom(seconds) {
    this.video.currentTime = seconds;
    this.video.play().catch(() => {});
  }

  pause() {
    this.video?.pause();
  }

  close() {
    this.closed = true;
    this.ready = false;
    this.hls?.destroy();
    this.hls = null;
    if (this.video) {
      this.video.pause();
      this.video.removeAttribute("src");
      this.video.load();
      if (this.video.parentNode === this.container) this.video.remove();
      this.video = null;
    }
  }
}

// The embed's getCurrentTime() is a cached value that the player only refreshes every so often (about once a
// second), so on its own it lags playback by up to a second, by a different amount each time, and still shows the
// old offset for a moment after a seek. position() tracks a base offset (the last refreshed value, or a seek target)
// plus the time played since.
const PLAYER_TICK_MS = 40;
const MAX_EXTRAPOLATION_SECONDS = 1.5; // don't run ahead while buffering; the next refresh corrects it

// The official Twitch embed (fallback)
class EmbedBackend {
  constructor(container) {
    this.container = container;
    this.kind = "twitch";
    this.player = null;
    this.ready = false;
    this.onMessage = null;
    this.lastReported = null; // last value seen from getCurrentTime(); a different value means a fresh refresh
    this.base = 0; // offset at baseAt
    this.baseAt = null; // performance.now() the base was taken while playing, null while not advancing
  }

  async open(vodId, startSeconds) {
    await loadTwitchEmbed();
    this.base = startSeconds;
    this.container.replaceChildren();
    // The embed wants an element id
    this.container.id ||= `embed-${Math.random().toString(36).slice(2)}`;
    this.player = new Twitch.Player(this.container.id, {
      width: "100%",
      height: "100%",
      video: `v${vodId}`,
      time: embedTime(startSeconds),
      parent: [location.hostname],
      autoplay: true,
    });
    const player = this.player;
    player.addEventListener(Twitch.Player.READY, () => {
      if (player !== this.player) return;
      this.ready = true;
      this.lastReported = player.getCurrentTime();
      // The "time" option is whole seconds; land on the exact offset
      if (startSeconds % 1) player.seek(startSeconds);
    });
    player.addEventListener(Twitch.Player.PLAYING, () => {
      if (player !== this.player) return;
      this.baseAt = performance.now();
    });
    player.addEventListener(Twitch.Player.PAUSE, () => {
      if (player !== this.player) return;
      this.freeze();
    });
    player.addEventListener(Twitch.Player.SEEK, (event) => {
      if (player !== this.player) return;
      // Seeks from the player's own controls; hold still until it plays from the new spot
      if (typeof event?.position === "number") this.base = event.position;
      this.baseAt = null;
    });
    // The embed refreshes its cached time when a message from the player iframe arrives. Its own listener runs
    // first (it was added earlier), so reading the time here catches each refresh the moment it lands instead of
    // up to a tick later.
    this.onMessage = (event) => {
      if (event.origin === "https://player.twitch.tv" && player === this.player) this.position();
    };
    window.addEventListener("message", this.onMessage);
  }

  // Estimated current offset into the VOD in seconds, or null before the player is ready
  position() {
    if (!this.ready) return null;
    const reported = this.player.getCurrentTime();
    if (reported !== this.lastReported) {
      // A fresh refresh: trust it, and keep counting from here if playing
      this.lastReported = reported;
      this.base = reported;
      if (this.baseAt != null) this.baseAt = performance.now();
    }
    if (this.baseAt == null || this.player.isPaused()) return this.base;
    return this.base + Math.min((performance.now() - this.baseAt) / 1000, MAX_EXTRAPOLATION_SECONDS);
  }

  freeze() {
    this.base = this.position() ?? this.base;
    this.baseAt = null;
  }

  playFrom(seconds) {
    // The cached time still shows the old spot until the next refresh; don't mistake that for the new position
    this.lastReported = this.player.getCurrentTime();
    this.base = seconds;
    this.baseAt = null;
    this.player.seek(seconds);
    this.player.play();
  }

  pause() {
    this.player.pause();
    this.freeze();
  }

  close() {
    window.removeEventListener("message", this.onMessage);
    this.player = null;
    this.ready = false;
    this.container.replaceChildren();
  }
}


// Running clock: always two decimals, so the text keeps its width while playing
export function formatClock(seconds) {
  const hundredths = Math.max(0, Math.floor(seconds * 100));
  return `${formatDuration(Math.floor(hundredths / 100), { short: false })}.${String(hundredths % 100).padStart(2, "0")}`;
}

// Segments load straight from Twitch's CDN until that fails once, then through the server (remembered for a day)
let segmentMode = loadSegmentMode();

function loadSegmentMode() {
  try {
    const saved = JSON.parse(localStorage.getItem(SEGMENT_MODE_STORAGE_KEY));
    if (saved?.mode === "proxy" && Date.now() - saved.at < SEGMENT_MODE_REMEMBER_MS) return "proxy";
  } catch {
    // Unavailable or unreadable storage: try the CDN first
  }
  return "direct";
}

function saveSegmentMode(mode) {
  try {
    if (mode === "proxy") localStorage.setItem(SEGMENT_MODE_STORAGE_KEY, JSON.stringify({ mode, at: Date.now() }));
    else localStorage.removeItem(SEGMENT_MODE_STORAGE_KEY);
  } catch {
    // Still applies for this visit
  }
}

// One player slot. Plays a VOD in the app player, falling back to the Twitch embed, and pauses previews at stopAt.
//   events: onTick(position), onState() (play/pause, opened, closed, source changed), onDetails(hls playlist details)
export class MediaPlayer {
  constructor(container, events = {}) {
    this.container = container;
    this.events = events;
    this.backend = null;
    this.vodId = null;
    this.stopAt = null;
    this.timer = null;
    this.fallbackReason = null;
    this.generation = 0;
    this.details = null;
  }

  get kind() {
    return this.backend?.kind ?? null;
  }

  get segmentMode() {
    return segmentMode;
  }

  async open(vodId, startSeconds = 0, { stopAt = null, mode = null } = {}) {
    vodId = String(vodId);
    if (this.backend && this.vodId === vodId && !mode) {
      this.playRange(startSeconds, stopAt);
      return;
    }
    this.close();
    const generation = this.generation;
    this.vodId = vodId;
    this.stopAt = stopAt;
    this.timer = setInterval(() => this.tick(), PLAYER_TICK_MS);
    this.events.onState?.();

    const wanted = mode ?? loadPlayerMode();
    let failure = null;
    for (const kind of wanted === "app" ? ["app", "twitch"] : ["twitch", "app"]) {
      try {
        if (await this.startKind(kind, vodId, startSeconds, generation)) {
          this.fallbackReason = failure?.message ?? null;
          this.watchVideo();
          this.events.onState?.();
        }
        return;
      } catch (error) {
        if (generation !== this.generation) return;
        console.warn(`The ${kind === "app" ? "app" : "Twitch"} player couldn't play VOD ${vodId}:`, error);
        failure = error;
      }
    }
    this.close();
    throw failure;
  }

  async startKind(kind, vodId, startSeconds, generation) {
    if (kind === "twitch") return this.start(new EmbedBackend(this.container), vodId, startSeconds, null, generation);
    try {
      return await this.start(new HlsBackend(this.container), vodId, startSeconds, segmentMode, generation);
    } catch (error) {
      if (!error.retryWithProxy || segmentMode !== "direct" || generation !== this.generation) throw error;
      const started = await this.start(new HlsBackend(this.container), vodId, startSeconds, "proxy", generation);
      segmentMode = "proxy";
      saveSegmentMode("proxy");
      return started;
    }
  }

  async start(backend, vodId, startSeconds, segments, generation) {
    this.backend = backend;
    backend.onDetails = (details) => {
      this.details = details;
      this.events.onDetails?.(details);
    };
    try {
      await backend.open(vodId, startSeconds, segments);
    } catch (error) {
      backend.close();
      if (this.backend === backend) this.backend = null;
      throw error;
    }
    return generation === this.generation;
  }

  // play/pause events of the <video> (app player) so the play button follows the keyboard and the video itself
  watchVideo() {
    const video = this.backend?.video;
    if (!video) return;
    for (const type of ["play", "pause", "ended", "volumechange"]) video.addEventListener(type, () => this.events.onState?.());
  }

  close() {
    this.generation++;
    clearInterval(this.timer);
    this.timer = null;
    this.backend?.close();
    this.backend = null;
    this.vodId = null;
    this.stopAt = null;
    this.details = null;
    this.fallbackReason = null;
    this.events.onState?.();
  }

  get ready() {
    return Boolean(this.backend?.ready);
  }

  position() {
    return this.backend?.position() ?? null;
  }

  // Length known so far: the playlist's (grows while live), else the video's
  duration() {
    const fragments = this.details?.fragments;
    if (fragments?.length) {
      const last = fragments[fragments.length - 1];
      return last.start + last.duration;
    }
    const d = this.backend?.video?.duration;
    return Number.isFinite(d) ? d : 0;
  }

  // Still recording: the playlist has no #EXT-X-ENDLIST yet
  get live() {
    return Boolean(this.details?.live);
  }

  livePosition() {
    return this.backend?.hls?.liveSyncPosition ?? this.duration();
  }

  playing() {
    const b = this.backend;
    if (!b?.ready) return false;
    if (b.kind === "app") return !b.video.paused;
    return !b.player.isPaused();
  }

  togglePlay() {
    if (!this.ready) return;
    if (this.playing()) this.pause();
    else this.play();
  }

  play() {
    const b = this.backend;
    if (!b?.ready) return;
    if (b.kind === "app") b.video.play().catch(() => {});
    else b.player.play();
    this.events.onState?.();
  }

  pause() {
    this.backend?.ready && this.backend.pause();
    this.events.onState?.();
  }

  seek(seconds) {
    const b = this.backend;
    if (!b?.ready) return;
    const target = Math.max(0, seconds);
    if (b.kind === "app") b.video.currentTime = target;
    else {
      const wasPlaying = this.playing();
      b.playFrom(target);
      if (!wasPlaying) b.pause();
    }
    this.tick();
  }

  skip(delta) {
    const now = this.position();
    if (now != null) this.seek(now + delta);
  }

  playRange(startSeconds, stopAt = null) {
    if (!this.backend?.ready) return;
    this.stopAt = stopAt;
    this.backend.playFrom(startSeconds);
    this.events.onState?.();
  }

  get volume() {
    const video = this.backend?.video;
    return video ? { volume: video.volume, muted: video.muted } : null;
  }

  setVolume(volume, muted) {
    const video = this.backend?.video;
    if (!video) return;
    if (volume != null) video.volume = Math.min(Math.max(volume, 0), 1);
    if (muted != null) video.muted = muted;
  }

  levels() {
    return this.backend?.kind === "app" ? this.backend.levels() : [];
  }

  get level() {
    return this.backend?.level ?? -1;
  }

  set level(index) {
    if (this.backend?.kind === "app") this.backend.level = index;
    this.events.onState?.();
  }

  get cleanSegments() {
    return this.backend?.cleanSegments ?? 0;
  }

  // Reopens the VOD at the same spot in the other player, and remembers the choice
  async switchPlayer() {
    if (!this.vodId) return;
    const mode = this.backend?.kind === "app" ? "twitch" : "app";
    savePlayerMode(mode);
    await this.open(this.vodId, this.position() ?? 0, { stopAt: this.stopAt, mode });
  }

  fullscreen() {
    const target = this.container.closest(".player") ?? this.container;
    if (document.fullscreenElement) document.exitFullscreen();
    else target.requestFullscreen?.();
  }

  tick() {
    const position = this.position();
    if (position == null) return;
    this.events.onTick?.(position);
    if (this.stopAt != null && position >= this.stopAt) {
      this.stopAt = null;
      this.backend.pause();
      this.events.onState?.();
    }
  }
}
