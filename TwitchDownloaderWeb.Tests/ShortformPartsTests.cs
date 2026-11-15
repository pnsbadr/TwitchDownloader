using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Vertical videos made of parts, each with its own layout. The render test needs ffmpeg (FFMPEG_PATH or on PATH).</summary>
    public class ShortformPartsTests
    {
        private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p ? p : "ffmpeg";
        // ffprobe next to the given ffmpeg (same file name with "probe" for "mpeg")
        private static string Ffprobe => Path.IsPathRooted(Ffmpeg)
            ? Path.Combine(Path.GetDirectoryName(Ffmpeg)!, Path.GetFileName(Ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase))
            : "ffprobe";
        private static readonly CropBox Face = new(0.7, 0, 0.3, 0.3);
        private static readonly CropBox Game = new(0.2, 0, 0.6, 1);

        private static ShortformPart Single(double start, double end) => new(start, end, "single", new CropBox(0.34, 0, 0.32, 1), null, null);
        private static ShortformPart Split(double start, double end) => new(start, end, "split", Face, Game, 0.3);

        private static ShortformLayout WithParts(params ShortformPart[] parts) => new("single", null, null, null, Parts: parts);

        [Fact]
        public void ValidatesParts()
        {
            Assert.Null(WithParts(Single(0, 4), Split(6, 10)).Validate(10));
            Assert.Null(WithParts(Single(0, 4), Split(4, 10)).Validate(10));
            // A part may be shorter than a whole render's 1 s minimum
            Assert.Null(WithParts(Single(0, 4), Split(4, 4.6), Single(4.6, 8)).Validate(10));
            Assert.NotNull(WithParts().Validate(10));
            Assert.Contains("order", WithParts(Single(5, 8), Split(2, 4)).Validate(10));
            Assert.Contains("order", WithParts(Single(0, 5), Split(4, 8)).Validate(10));
            Assert.Contains("0.5", WithParts(Single(0, 0.2), Split(4, 8)).Validate(10));
            Assert.Contains("after the end", WithParts(Single(0, 4), Split(10, 12)).Validate(10));
            Assert.Contains("Part 2", WithParts(Single(0, 4), new ShortformPart(5, 8, "split", Face, null, 0.3)).Validate(10));
            Assert.NotNull(WithParts(Enumerable.Range(0, ShortformLayout.MaxParts + 1).Select(i => Single(i, i + 0.6)).ToArray()).Validate(100));
        }

        [Fact]
        public void NormalizeMirrorsTheParts()
        {
            var layout = WithParts(Split(2, 4), Single(6, 9)).Normalize();
            Assert.Equal("split", layout.Mode);
            Assert.Equal(Face, layout.Top);
            Assert.Equal(Game, layout.Bottom);
            Assert.Equal(0.3, layout.SplitRatio);
            Assert.Equal(2, layout.TrimStart);
            Assert.Equal(9, layout.TrimEnd);
            Assert.Equal(5, layout.GetDuration(10));
            Assert.Equal("2 parts", layout.Summary);
            Assert.Null(layout.Validate(10));

            var plain = new ShortformLayout("single", Face, null, null, Parts: []).Normalize();
            Assert.Null(plain.Parts);
            Assert.Equal("single", plain.Summary);
        }

        [Fact]
        public void ValidatesAndBuildsMovingBoxes()
        {
            var left = new CropBox(0, 0, 0.3, 1);
            var right = new CropBox(0.7, 0, 0.3, 1);
            var wide = new CropBox(0.2, 0, 0.6, 1);
            var pan = new ShortformLayout("single", left, null, null, Keys: [new ShortformKey(1, left, null), new ShortformKey(3, right, null)]);
            Assert.Null(pan.Validate(10));
            var graph = pan.BuildFilterGraph();
            Assert.Contains("if(lt(t,1)", graph);
            Assert.DoesNotContain("eval=frame", graph);
            Assert.Equal("single, moving boxes", pan.Summary);

            var zoom = new ShortformLayout("single", left, null, null, Keys: [new ShortformKey(1, left, null), new ShortformKey(3, wide, null)]);
            Assert.Contains("eval=frame", zoom.BuildFilterGraph());

            // Key times are seconds into the file; the filter's t counts from the trim start
            var trimmed = pan with { TrimStart = 2, TrimEnd = 6, Keys = [new ShortformKey(2.5, left, null), new ShortformKey(4, right, null)] };
            Assert.Contains("if(lt(t,0.5)", trimmed.BuildFilterGraph());

            // Keys that don't move are just the box
            var still = pan with { Keys = [new ShortformKey(1, right, null), new ShortformKey(3, right, null)] };
            Assert.DoesNotContain("if(", still.BuildFilterGraph());

            Assert.Contains("order", (pan with { Keys = [new ShortformKey(3, left, null), new ShortformKey(1, right, null)] }).Validate(10));
            Assert.Contains("Keyframe 2", (pan with { Keys = [new ShortformKey(1, left, null), new ShortformKey(3, new CropBox(0.9, 0, 0.3, 1), null)] }).Validate(10));
            var split = new ShortformLayout("split", Face, Game, 0.3, Keys: [new ShortformKey(1, Face, Game), new ShortformKey(2, Face, null)]);
            Assert.Contains("bottom", split.Validate(10));

            // A part's keys reach its layout
            var parts = WithParts(Single(0, 4), Split(4, 8) with { Keys = [new ShortformKey(4, Face, Game), new ShortformKey(6, new CropBox(0, 0, 0.3, 0.3), Game)] });
            Assert.Null(parts.Validate(10));
            Assert.Equal("2 parts, moving boxes", parts.Normalize().Summary);
            Assert.Contains("if(lt(t,2)", parts.Pieces()[1].BuildFilterGraph());
        }

        [Fact]
        public async Task RendersMovingBoxes()
        {
            SkipIfNoFfmpeg();
            var dir = Directory.CreateTempSubdirectory("shortform-keys-").FullName;
            try
            {
                var source = Path.Combine(dir, "source.mp4");
                await RunAsync(Ffmpeg, "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", source);
                var service = MakeService(dir);
                // A pan in the first part, a zoom (the face box grows) in the second
                var small = new CropBox(0.7, 0, 0.15, 0.15);
                var layout = WithParts(
                    Single(0, 2) with { Keys = [new ShortformKey(0.2, new CropBox(0, 0, 0.32, 1), null), new ShortformKey(1.8, new CropBox(0.68, 0, 0.32, 1), null)] },
                    Split(2, 4) with { Keys = [new ShortformKey(2, small, Game), new ShortformKey(3.5, Face, Game)] }).Normalize();
                Assert.Null(layout.Validate(4));
                service.Start(1, source, 4, layout, userId: 7);

                var deadline = DateTime.UtcNow.AddSeconds(90);
                ShortformState state;
                while ((state = service.GetState(1)).Status == ShortformStatus.Rendering && DateTime.UtcNow < deadline)
                    await Task.Delay(200, TestContext.Current.CancellationToken);

                Assert.True(state.Status == ShortformStatus.None, state.Error);
                var render = Assert.Single(state.Renders);
                Assert.Equal(2, render.Layout.Parts![1].Keys!.Count);
                var path = service.RenderPath(1, render.Id, "mp4")!;
                var probe = await RunAsync(Ffprobe, "-v", "error", "-show_entries", "format=duration:stream=width,height", "-of", "default=nw=1", path);
                Assert.Contains("width=1080", probe);
                Assert.Contains("height=1920", probe);
                var duration = double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=")).Split('=')[1], CultureInfo.InvariantCulture);
                Assert.InRange(duration, 3.8, 4.3);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task RendersPartsBackToBack()
        {
            SkipIfNoFfmpeg();
            var dir = Directory.CreateTempSubdirectory("shortform-parts-").FullName;
            try
            {
                var source = Path.Combine(dir, "source.mp4");
                await RunAsync(Ffmpeg, "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=10",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", source);

                var service = MakeService(dir);
                // 0-3 s single, 5-7.5 s split: 5.5 s in all
                var layout = WithParts(Single(0, 3), Split(5, 7.5)).Normalize();
                Assert.Null(layout.Validate(10));
                service.Start(1, source, 10, layout, userId: 7);

                var deadline = DateTime.UtcNow.AddSeconds(90);
                ShortformState state;
                while ((state = service.GetState(1)).Status == ShortformStatus.Rendering && DateTime.UtcNow < deadline)
                    await Task.Delay(200, TestContext.Current.CancellationToken);

                Assert.Equal(ShortformStatus.None, state.Status);
                var render = Assert.Single(state.Renders);
                Assert.Equal(7, render.CreatedByUserId);
                Assert.Equal(2, render.Layout.Parts!.Count);
                Assert.Equal("split", render.Layout.Parts[1].Mode);

                var path = service.RenderPath(1, render.Id, "mp4")!;
                var probe = await RunAsync(Ffprobe, "-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "default=nw=1", path);
                Assert.Contains("width=1080", probe);
                Assert.Contains("height=1920", probe);
                Assert.Contains("codec_type=audio", probe);
                var duration = double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=")).Split('=')[1], CultureInfo.InvariantCulture);
                Assert.InRange(duration, 5.3, 5.8);

                // Only the render's own files are left (no part files or join list)
                var left = Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order().ToArray();
                Assert.Equal(new[] { $"{render.Id}.jpg", $"{render.Id}.json", $"{render.Id}.mp4" }, left);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        private static ShortformService MakeService(string dir)
        {
            var settings = Options.Create(new DownloadSettings { OutputFolder = Path.Combine(dir, "downloads") });
            var storage = new LibraryStorage(settings, new Env(dir), NullLogger<LibraryStorage>.Instance);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ffmpeg:Path"] = Ffmpeg }).Build();
            // No database: the render's audit events fail and are only logged
            var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            return new ShortformService(storage, scopes, configuration, settings, NullLogger<ShortformService>.Instance);
        }

        private sealed class Env(string root) : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = root;
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "Tests";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = root;
            public string EnvironmentName { get; set; } = Environments.Development;
        }

        private static async Task<string> RunAsync(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"{exe} failed: {await stderr}");
            return await stdout;
        }

        private static void SkipIfNoFfmpeg()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Ffmpeg, "-version") { RedirectStandardOutput = true, UseShellExecute = false });
                p!.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip($"ffmpeg not found ({Ffmpeg}); set FFMPEG_PATH to run the render test.");
            }
        }
    }
}
