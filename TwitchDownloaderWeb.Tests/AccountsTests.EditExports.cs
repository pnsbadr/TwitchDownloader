using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Segments;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Exporting saved edits and vertical renders of an edit (badrfly, 2026-11-16).</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public void EditLayoutsKeepOnlyWhatTheEditKeeps()
        {
            var box = new CropBox(0, 0, 0.3, 1);
            var plain = new ShortformLayout("single", box, null, null, TrimStart: 1, TrimEnd: 10);
            var kept = ShortformLayout.KeepOnly(plain, [(0, 3), (5, 20)], 12)!;
            Assert.Equal([(1.0, 3.0), (5.0, 10.0)], kept.Parts!.Select(x => (x.Start, x.End)).ToArray());
            Assert.Null(kept.Validate(12));
            Assert.Equal(1, kept.Normalize().TrimStart);

            // Parts are cut where the edit leaves gaps; slivers go
            var parts = plain with { TrimStart = null, TrimEnd = null, Parts = [new ShortformPart(0, 4, "single", box, null, null), new ShortformPart(4, 12, "single", box, null, null)] };
            Assert.Equal([(0.0, 3.0), (5.0, 8.0), (8.2, 12.0)],
                ShortformLayout.KeepOnly(parts, [(0, 3), (3.9, 4.1), (5, 8), (8.2, 12)], 12)!.Parts!.Select(x => (x.Start, x.End)).ToArray());
            Assert.Null(ShortformLayout.KeepOnly(plain, [(11, 12)], 12));

            // VOD ranges into a file: a whole-range file, and an old file that left out 20..25
            Assert.Equal([(2.0, 5.0)], ItemEditEndpoints.VodToFile([(12, 15)], [(10, 30)]));
            Assert.Equal([(8.0, 10.0), (10.0, 12.0)], ItemEditEndpoints.VodToFile([(18, 27)], [(10, 20), (25, 30)]));
        }

        [Fact]
        public async Task EditsAreExportedLikeTheirItemsWithoutWhatTheyRemove()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ee1");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();

            var segment = await SegmentIdAsync(owner, 470, 10, 30);
            var segmentEdit = (await PostJsonAsync(owner, "/api/edits", new { markerId = segment, title = "Tight", cuts = new[] { new { start = 10, end = 15 } } },
                HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var a = await SegmentIdAsync(owner, 470, 100, 110);
            var b = await SegmentIdAsync(owner, 470, 200, 210);
            var videoId = (await PostJsonAsync(owner, "/api/vods/470/videos", new { title = "Pair", markerIds = new[] { a, b } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            var videoEdit = (await PostJsonAsync(owner, "/api/edits", new { videoId,
                parts = new[] { new { markerId = a, cuts = new[] { new { start = 100, end = 110 } } }, new { markerId = b, cuts = new[] { new { start = 205, end = 210 } } } } },
                HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var clipId = AddStoredClip(teamId, ownerId, 470, 300, 320, "stored");
            var clipEdit = (await PostJsonAsync(owner, "/api/edits", new { entryId = clipId, cuts = new[] { new { start = 310, end = 320 } } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();

            // An edit's vertical needs a render made for that edit (the clip's own renders don't count)
            AddRender(clipId, "20261116000000", ownerId, DateTime.UtcNow, "clip render");
            var noVertical = await PostJsonAsync(owner, "/api/exports", new { items = new[] { new { kind = "edit", editId = clipEdit, horizontal = false, vertical = true } } },
                HttpStatusCode.Conflict);
            Assert.Equal("no-vertical", noVertical.GetProperty("code").GetString());

            var export = await PostJsonAsync(owner, "/api/exports", new
            {
                items = new object[]
                {
                    new { kind = "edit", editId = segmentEdit },
                    new { kind = "edit", editId = videoEdit },
                    new { kind = "edit", editId = clipEdit },
                },
            }, HttpStatusCode.Accepted);
            var items = export.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["Segment", "Video", "Clip"], items.Select(x => x.GetProperty("kind").GetString()!).ToArray());
            Assert.Equal([segmentEdit, videoEdit, clipEdit], items.Select(x => x.GetProperty("editId").GetInt32()).ToArray());
            Assert.Equal("Tight", items[0].GetProperty("fileName").GetString());
            Assert.Equal("Pair", items[1].GetProperty("fileName").GetString());
            Assert.Equal([15.0, 5.0, 10.0], items.Select(x => x.GetProperty("lengthSeconds").GetDouble()).ToArray());

            // The items themselves are unchanged, and an edit that's gone is refused
            Assert.Empty(CutsOf(await MarkerOnTimelineAsync(owner, 470, segment)));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsJsonAsync("/api/exports", new { items = new[] { new { kind = "edit", editId = 999999 } } }, ct)).StatusCode);
        }

        [Fact]
        public async Task ClipEditsAreCutFromTheStoredFile()
        {
            if (!HasFfmpeg())
                Assert.Skip("ffmpeg not found; set FFMPEG_PATH.");
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ee2");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipId = AddStoredClip(teamId, ownerId, 471, 50, 60, "");
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var entry = db.Entries.Single(x => x.Id == clipId);
                entry.TrimMode = TwitchDownloaderCore.Models.VideoTrimMode.Exact;
                db.SaveChanges();
                var file = Path.Combine(_dir, "downloads", entry.FilePath!);
                using var process = Process.Start(new ProcessStartInfo(FakeCutter.Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
                    "testsrc=size=160x90:rate=30:duration=10", "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-c:v", "libx264", "-preset",
                    "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", file]) { RedirectStandardError = true, UseShellExecute = false })!;
                await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                Assert.Equal(0, process.ExitCode);
            }

            // Keeps 50..52 and 55..58: 5 s, made from the file (nothing cut from the VOD)
            var editId = (await PostJsonAsync(owner, "/api/edits", new { entryId = clipId,
                cuts = new[] { new { start = 52, end = 55 }, new { start = 58, end = 60 } } }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var export = await PostJsonAsync(owner, "/api/exports", new { items = new[] { new { kind = "edit", editId } } }, HttpStatusCode.Accepted);
            var done = await WaitForExportAsync(owner, export.GetProperty("id").GetInt32());
            Assert.Equal("Ready", done.GetProperty("status").GetString());
            Assert.DoesNotContain(_cutter.Cuts, x => x.VodId == 471);

            var output = Path.Combine(_dir, "downloads", LibraryStorage.ExportItemRelativePath(export.GetProperty("id").GetInt32(), 1));
            using var probe = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", output])
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            var duration = double.Parse((await probe.StandardOutput.ReadToEndAsync(ct)).Trim(), CultureInfo.InvariantCulture);
            Assert.InRange(duration, 4.8, 5.3);
        }

        [Fact]
        public async Task VerticalRendersOfAnEditRememberIt()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("ee3");
            var ownerId = (await GetJsonAsync(owner, "/api/auth/me")).GetProperty("id").GetInt32();
            var clipId = AddStoredClip(teamId, ownerId, 472, 0, 10, "file");
            var otherClip = AddStoredClip(teamId, ownerId, 472, 20, 30, "file");
            var editId = (await PostJsonAsync(owner, "/api/edits", new { entryId = clipId, cuts = new[] { new { start = 0, end = 9.2 } } }, HttpStatusCode.Created))
                .GetProperty("id").GetInt32();
            var layout = new { mode = "single", top = new { x = 0, y = 0, width = 0.3, height = 1 } };

            // Not this clip's edit, or one that leaves less than a second
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/entries/{otherClip}/shortform?editId={editId}", layout, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/entries/{clipId}/shortform?editId={editId}", layout, ct)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/edits/{editId}", new { cuts = new[] { new { start = 2, end = 4 } } }, ct)).StatusCode);
            var started = await PostJsonAsync(owner, $"/api/entries/{clipId}/shortform", new { editId, layout.mode, layout.top }, HttpStatusCode.Accepted);
            Assert.Equal(editId, started.GetProperty("renderingEditId").GetInt32());
            // The render is cut down to what the edit keeps: 0..2 and 4..10
            var parts = started.GetProperty("layout").GetProperty("parts").EnumerateArray()
                .Select(x => (x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble())).ToArray();
            Assert.Equal([(0.0, 2.0), (4.0, 10.0)], parts);
        }
    }
}
