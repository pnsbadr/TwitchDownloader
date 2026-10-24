using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record ShortformDto(string Status, double Progress, string? Error, long? FileSizeBytes, ShortformLayout? Layout)
    {
        public static ShortformDto From(ShortformState state) =>
            new(state.Status.ToString(), state.Progress, state.Error, state.FileSizeBytes, state.Layout);
    }

    /// <summary>Portrait (9:16) versions of entries, made from the entry's finished file with a crop layout chosen on the page.</summary>
    public static class ShortformEndpoints
    {
        public static IEndpointRouteBuilder MapShortformEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/entries/{id:int}/shortform");
            group.MapGet("/", GetShortform);
            group.MapPost("/", StartShortform);
            group.MapGet("/file", DownloadShortform);
            return app;
        }

        private static async Task<IResult> GetShortform(int id, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            if (await db.Entries.FindAsync([id], cancellationToken) is null)
                return EntryNotFound(id);

            return Results.Ok(ShortformDto.From(shortform.GetState(id)));
        }

        /// <summary>Starts a render from the entry's file (stored, or an unexpired on-demand file). 202 with the new state.</summary>
        private static async Task<IResult> StartShortform(int id, ShortformLayout layout, LibraryDbContext db, LibraryStorage storage,
            ShortformService shortform, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            if (layout.Validate() is { } error)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = [error] });

            var relativePath = entry.Status == EntryStatus.Ready ? entry.FilePath
                : EntryDto.HasUnexpiredGeneratedFile(entry) ? entry.GeneratedFilePath
                : null;
            if (relativePath is null || !File.Exists(storage.Resolve(relativePath)))
                return Results.Problem(detail: "The entry's file isn't ready, so it can't be made vertical yet.", statusCode: StatusCodes.Status409Conflict);

            shortform.Start(id, storage.Resolve(relativePath), entry.ClipLengthSeconds, layout);
            return Results.Accepted($"/api/entries/{id}/shortform", ShortformDto.From(shortform.GetState(id)));
        }

        private static async Task<IResult> DownloadShortform(int id, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            var state = shortform.GetState(id);
            if (state.Status != ShortformStatus.Ready)
                return Results.Problem(detail: $"The vertical video for entry {id} is not ready ({state.Status}).", statusCode: StatusCodes.Status409Conflict);

            var fileName = Path.GetFileNameWithoutExtension(EntryEndpoints.GetDownloadFileName(entry)) + " (vertical).mp4";
            return Results.File(shortform.FilePath(id), "video/mp4", fileName, enableRangeProcessing: true);
        }

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} not found.", statusCode: StatusCodes.Status404NotFound);
    }
}
