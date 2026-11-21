// The players' screenshot button (badrfly 2026-11-21): the frame on screen, saved straight to the computer as a PNG named after
// the VOD and the moment ("Title 1-02-03.45.png"), always in the VOD's best quality whatever the player shows (badrfly 2026-11-21).
//
// When the app player is showing the best rendition, its <video> (hls.js through Media Source Extensions, so the picture is the
// page's own, direct from the CDN or through the server alike) is drawn on a canvas at full size: instant and exactly the frame on
// screen. Otherwise (a lower quality, Twitch's player, whose iframe a page can't read, or a canvas the browser won't let the page
// read) the server takes the frame from the best rendition with ffmpeg (GET /api/vods/{id}/frame?t=): a few seconds; with Twitch's
// player the time is an estimate, so it may be a frame or two off. The 9:16 view crops that best-quality frame with its boxes
// (compose), at 1080x1920.

import { toast, problemMessage } from "./core.js";

// "Title 1-02-03.45" (characters Windows refuses in file names swapped for "-")
export function screenshotName(title, seconds) {
  const hundredths = Math.max(0, Math.floor((seconds ?? 0) * 100));
  const s = Math.floor(hundredths / 100);
  const time = `${Math.floor(s / 3600)}-${String(Math.floor((s % 3600) / 60)).padStart(2, "0")}-${String(s % 60).padStart(2, "0")}.${String(hundredths % 100).padStart(2, "0")}`;
  const clean = String(title || "VOD").replace(/[\\/:*?"<>|\u0000-\u001f]+/g, "-").replace(/\s+/g, " ").trim().replace(/[. ]+$/, "").slice(0, 120);
  return `${clean || "VOD"} ${time}.png`;
}

function canvasBlob(canvas) {
  // toBlob throws a SecurityError on a tainted canvas; it calls back with null when it can't encode
  return new Promise((resolve, reject) => {
    try {
      canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("The browser couldn't make the picture."))), "image/png");
    } catch (error) {
      reject(error);
    }
  });
}

function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.style.display = "none";
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

// The app player's <video> when the frame it shows is from the VOD's best rendition, else null (then the server takes it)
export function bestQualityVideo(player) {
  if (player?.kind !== "app") return null;
  const video = player.backend?.video;
  const levels = player.backend?.hls?.levels;
  if (!video || video.readyState < 2 || !video.videoWidth || !video.videoHeight || !levels?.length) return null;
  const best = Math.max(...levels.map((level) => level.height || 0));
  return best > 0 && video.videoHeight >= best ? video : null;
}

function videoCanvas(video) {
  const canvas = document.createElement("canvas");
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  canvas.getContext("2d").drawImage(video, 0, 0);
  return canvas;
}

async function serverFrame(vodId, seconds) {
  const response = await fetch(`/api/vods/${encodeURIComponent(vodId)}/frame?t=${encodeURIComponent(seconds.toFixed(3))}`);
  if (!response.ok) {
    let problem = null;
    try {
      problem = await response.json();
    } catch {
      // Not a problem response
    }
    throw new Error(problemMessage(problem, response.status));
  }
  return response.blob();
}

// A picture made from source (a <video> or an image): the plain frame, or compose's (the 9:16 view's) crop of it
function picture(source, compose) {
  const canvas = compose ? compose(source) : videoCanvas(source);
  if (!canvas) throw new Error("nothing plays at the playhead.");
  return canvas;
}

let busy = false;

// player: the MediaPlayer on screen; vodId + seconds: the VOD moment it shows; title: for the file name;
// compose(source): draws the 9:16 view's picture from a source frame (null in a gap)
export async function saveScreenshot({ player, vodId, seconds, title, compose = null }) {
  if (busy) return;
  if (!vodId || seconds == null) {
    toast("Nothing is playing here to take a picture of.", "error");
    return;
  }
  const name = screenshotName(title, seconds);
  busy = true;
  try {
    let blob = null;
    const video = bestQualityVideo(player);
    if (video) {
      try {
        blob = await canvasBlob(picture(video, compose));
      } catch (error) {
        // A tainted canvas (a picture the browser won't let the page read): the server takes it instead
        if (error?.name !== "SecurityError") throw error;
      }
    }
    let fromServer = false;
    if (!blob) {
      const wait = toast("Taking the picture in the best quality…");
      try {
        blob = await serverFrame(vodId, seconds);
      } finally {
        wait.remove();
      }
      if (compose) {
        const frame = await createImageBitmap(blob);
        try {
          blob = await canvasBlob(picture(frame, compose));
        } finally {
          frame.close();
        }
      }
      fromServer = true;
    }
    download(blob, name);
    toast(fromServer && player?.kind === "twitch" ? `Saved ${name} (Twitch's player only tells the time roughly, so it may be a frame or two off).` : `Saved ${name}.`);
  } catch (error) {
    toast(`Couldn't take the picture: ${error.message}`, "error");
  } finally {
    busy = false;
  }
}
