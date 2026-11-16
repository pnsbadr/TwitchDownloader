using System.Diagnostics;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>Runs ffmpeg with an argument list. Paths go in as separate arguments, so no quoting is needed.</summary>
    public static class FfmpegRunner
    {
        /// <param name="onOutputLine">Receives each stdout line, e.g. for "-progress pipe:1" output.</param>
        public static async Task RunAsync(string ffmpegPath, IReadOnlyList<string> args, string workingDirectory, ILogger logger, CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            using var process = new Process
            {
                StartInfo =
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    // ffmpeg reads keys from stdin ("q" stops it); never give it the app's console
                    RedirectStandardInput = true,
                    WorkingDirectory = workingDirectory,
                }
            };
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            // Keep the tail of stderr for the error message; ffmpeg writes progress there continuously
            var tail = new Queue<string>();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (tail)
                {
                    tail.Enqueue(e.Data);
                    if (tail.Count > 15) tail.Dequeue();
                }
            };

            logger.LogDebug("Running ffmpeg {Args}", string.Join(' ', args));
            process.Start();
            process.StandardInput.Close();
            process.BeginErrorReadLine();
            if (onOutputLine is null)
            {
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            }
            else
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null) onOutputLine(e.Data);
                };
                process.BeginOutputReadLine();
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                    // Let it exit so its output file is closed: on Windows the caller's cleanup can't delete a file ffmpeg still has open
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException) { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                string details;
                lock (tail) details = string.Join(" | ", tail);
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {details}");
            }
        }
    }
}
