// The players' screenshot button (badrfly 2026-11-21): the frame on screen, saved straight to the computer as a PNG named after
// the VOD and the moment ("Title 1-02-03.45.png").
//
// The app player's <video> is fed by hls.js through Media Source Extensions, so its picture is the page's own and can be drawn on
// a canvas at full size, whether the segments come straight from Twitch's CDN or through the server. Twitch's player (the fallback)
// is an iframe whose picture a page can't read, so then (or if the browser refuses the canvas) the server takes the frame from the
// VOD itself with ffmpeg (GET /api/vods/{id}/frame?t=): a few seconds, and the embed's time is an estimate, so it may be a frame or
// two off. The 9:16 view saves what its preview shows, at full size (1080x1920).

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

// The <video>'s current frame at its own size, or null when there's no picture yet
function videoCanvas(video) {
  if (!video || video.readyState < 2 || !video.videoWidth || !video.videoHeight) return null;
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

let busy = false;

// player: the MediaPlayer on screen; vodId + seconds: the VOD moment it shows; title: for the file name;
// canvas: an already drawn picture to save instead (the 9:16 view)
export async function saveScreenshot({ player, vodId, seconds, title, canvas = null }) {
  if (busy) return;
  if (!vodId || seconds == null) {
    toast("Nothing is playing here to take a picture of.", "error");
    return;
  }
  const name = screenshotName(title, seconds);
  busy = true;
  try {
    let blob = null;
    let tainted = false;
    const picture = canvas ?? (player?.kind === "app" ? videoCanvas(player.backend?.video) : null);
    if (picture) {
      try {
        blob = await canvasBlob(picture);
      } catch (error) {
        // A tainted canvas (a picture the browser won't let the page read): the server takes it instead
        if (error?.name !== "SecurityError") throw error;
        tainted = true;
      }
    }
    if (!blob) {
      if (canvas) {
        toast("This picture can't be saved from the 9:16 view (the browser won't let the page read it). Save it from Player 2 instead.", "error");
        return;
      }
      const why = player?.kind === "twitch" ? "Twitch's player can't be captured by the page" : tainted ? "the browser won't let the page read this picture" : "the picture isn't loaded yet";
      const wait = toast(`Taking the picture on the server (${why})…`);
      try {
        blob = await serverFrame(vodId, seconds);
      } finally {
        wait.remove();
      }
      download(blob, name);
      toast(player?.kind === "twitch" ? `Saved ${name} (taken on the server: it may be a frame or two off what Twitch's player shows).` : `Saved ${name}.`);
      return;
    }
    download(blob, name);
    toast(`Saved ${name}.`);
  } catch (error) {
    toast(`Couldn't take the picture: ${error.message}`, "error");
  } finally {
    busy = false;
  }
}
