// Cut-out parts (step 7): ranges removed from a segment, clip or a segment in one video, in VOD seconds. Same rules as the
// server's CutOuts: clipped to the item, merged, rounded to hundredths, and a kept sliver under half a second is cut too.

export const MIN_KEPT_SECONDS = 0.5;

const round = (seconds) => Math.round(seconds * 100) / 100;

export function normalizeCuts(cuts, start, end) {
  const sorted = (cuts ?? [])
    .filter((c) => Number.isFinite(c.start) && Number.isFinite(c.end) && c.end > c.start)
    .map((c) => ({ start: round(Math.max(c.start, start)), end: round(Math.min(c.end, end)) }))
    .filter((c) => c.end > c.start)
    .sort((a, b) => a.start - b.start);
  const merged = [];
  for (const cut of sorted) {
    const from = cut.start - start < MIN_KEPT_SECONDS ? start : cut.start;
    const to = end - cut.end < MIN_KEPT_SECONDS ? end : cut.end;
    const last = merged[merged.length - 1];
    if (last && from - last.end < MIN_KEPT_SECONDS) last.end = Math.max(last.end, to);
    else merged.push({ start: from, end: to });
  }
  return merged;
}

// What's left of [start, end], in order
export function keptParts(start, end, cuts) {
  const kept = [];
  let at = start;
  for (const cut of normalizeCuts(cuts, start, end)) {
    if (cut.start > at) kept.push({ start: at, end: cut.start });
    at = Math.max(at, cut.end);
  }
  if (end > at) kept.push({ start: at, end });
  return kept;
}

export function keptSeconds(start, end, cuts) {
  return keptParts(start, end, cuts).reduce((sum, part) => sum + part.end - part.start, 0);
}

// The cut-out part at t, or null
export function cutAt(cuts, t) {
  return (cuts ?? []).find((c) => t >= c.start && t < c.end) ?? null;
}

// Playback: where to go from position so cut-out parts are skipped. null = play on; a number = seek there;
// "end" = the cut runs to the end (stop, or go on with the next part)
export function skipCut(cuts, position, end) {
  const cut = (cuts ?? []).find((c) => position >= c.start && position < c.end - 0.04);
  if (!cut) return null;
  return end != null && cut.end >= end - 0.05 ? "end" : cut.end;
}
