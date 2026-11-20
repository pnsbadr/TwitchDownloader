// Sound waves (badrfly 2026-11-20, "Sync with"): how loud a VOD is, for Player 2's pieces and the side-by-side sync view, so two
// streams can be lined up by eye (a laugh or a shout is a spike on both). The server reads them in blocks of 60 s
// (GET /api/vods/{id}/waves?block=n: 20 levels a second, 0 silent … 255 loud) and keeps them; here they're kept for the page's life.
// Blocks load a few at a time, only for what's on screen; `onWaves` callbacks redraw when one arrives.

import { api } from "./core.js";

const BLOCK = 60;
const RUNNING_MAX = 3;
const blocks = new Map();       // "vodId/n" -> { levels: Uint8Array, rate } | "loading" | "failed"
const queue = [];
const listeners = new Set();
let running = 0;

export function onWaves(callback) {
  listeners.add(callback);
}

function key(vodId, n) {
  return `${vodId}/${n}`;
}

function pump() {
  while (running < RUNNING_MAX && queue.length) {
    const { vodId, n } = queue.shift();
    running++;
    api("GET", `/api/vods/${vodId}/waves?block=${n}`)
      .then((answer) => {
        const raw = atob(answer.levels ?? "");
        const levels = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) levels[i] = raw.charCodeAt(i);
        blocks.set(key(vodId, n), { levels, rate: answer.rate });
      })
      .catch(() => {
        blocks.set(key(vodId, n), "failed");
        // try again later (Twitch unreachable for a moment, a live VOD's newest block)
        setTimeout(() => blocks.delete(key(vodId, n)), 60_000);
      })
      .finally(() => {
        running--;
        for (const callback of listeners) callback();
        pump();
      });
  }
}

// Ask for the blocks covering a VOD range (newest asks first: what's on screen now matters more than what was)
export function want(vodId, start, end) {
  if (vodId == null || !(end > start)) return;
  const first = Math.max(0, Math.floor(start / BLOCK));
  const last = Math.floor(Math.max(start, end - 0.001) / BLOCK);
  for (let n = last; n >= first; n--) {
    const k = key(vodId, n);
    if (blocks.has(k)) continue;
    blocks.set(k, "loading");
    queue.unshift({ vodId, n });
  }
  // what scrolled away long ago: drop from the queue
  if (queue.length > 24) for (const dropped of queue.splice(24)) blocks.delete(key(dropped.vodId, dropped.n));
  pump();
}

// Loudness 0–1 at a VOD second (the loudest level over [t, t + span)), or null when not loaded
export function levelAt(vodId, t, span = 0.05) {
  let best = null;
  const end = t + Math.max(span, 0.05);
  for (let s = t; s < end; ) {
    const n = Math.floor(s / BLOCK);
    const block = blocks.get(key(vodId, n));
    const blockEnd = (n + 1) * BLOCK;
    if (block && typeof block === "object") {
      const from = Math.max(0, Math.floor((s - n * BLOCK) * block.rate));
      const to = Math.min(block.levels.length, Math.ceil((Math.min(end, blockEnd) - n * BLOCK) * block.rate));
      for (let i = from; i < Math.max(to, from + 1) && i < block.levels.length; i++) best = Math.max(best ?? 0, block.levels[i] / 255);
    }
    s = blockEnd;
  }
  return best;
}

// Draws a VOD range's wave into x1…x2 (centered on midY, ±halfHeight), asking for what's missing. maxSeconds: don't load more
// than this much at a time (a whole 3 h piece fitted on screen would be 180 blocks): longer ranges show only what's loaded.
export function drawWave(ctx, { vodId, start, end, x1, x2, midY, halfHeight, color, clipX1 = -Infinity, clipX2 = Infinity, maxSeconds = 1200 }) {
  const width = x2 - x1;
  if (width < 4 || !(end > start)) return;
  const from = Math.max(x1, clipX1, 0);
  const to = Math.min(x2, clipX2);
  if (to <= from) return;
  const perPx = (end - start) / width;
  const shownStart = start + (from - x1) * perPx;
  const shownEnd = start + (to - x1) * perPx;
  if (shownEnd - shownStart <= maxSeconds) want(vodId, shownStart, shownEnd);
  ctx.save();
  ctx.fillStyle = color;
  const step = 2;
  for (let x = Math.floor(from); x < to; x += step) {
    const level = levelAt(vodId, start + (x - x1) * perPx, perPx * step);
    if (level == null || level <= 0.02) continue;
    const h = Math.max(1, level * halfHeight);
    ctx.fillRect(x, midY - h, step - 0.5, h * 2);
  }
  ctx.restore();
}
