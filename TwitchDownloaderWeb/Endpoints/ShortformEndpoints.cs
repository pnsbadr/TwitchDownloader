using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record ShortformRenderDto(string Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, long SizeBytes, ShortformLayout Layout,
        string FileUrl, string? ThumbnailUrl);

    /// <param name="Layout">The running render's layout, else the newest kept render's (so the editor reopens with it).</param>
    public sealed record ShortformDto(string Status, double Progress, string? Error, ShortformLayout? Layout, IReadOnlyList<ShortformRenderDto> Renders)
    {
        public static ShortformDto From(int entryId, ShortformState state) => new(state.Status.ToString(), state.Progress, state.Error, state.Layout,
            state.Renders.Select(x => new ShortformRenderDto(x.Id, x.CreatedAtUtc, x.ExpiresAtUtc, x.SizeBytes, x.Layout,
                $"/api/entries/{entryId}/shortform/{x.Id}/file",
                x.HasThumbnail ? $"/api/entries/{entryId}/shortform/{x.Id}/thumbnail" : null)).ToList());
    }

    /// <summary>
    /// Portrait (9:16) versions of entries, made from the entry's finished file with a crop layout chosen on the page.
    /// Renders are kept for the team (newest few, until they expire) so anyone can preview or download an earlier one.
    /// </summary>
    public static class ShortformEndpoints
    {
        public static IEndpointRouteBuilder MapShortformEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/entries/{id:int}/shortform");
            group.MapGet("/", GetShortform);
            group.MapPost("/", StartShortform);
            group.MapGet("/{renderId}/file", DownloadRender);
            group.MapGet("/{renderId}/thumbnail", GetThumbnail);
            group.MapDelete("/{renderId}", DeleteRender);
            return app;
        }

        private static async Task<IResult> GetShortform(int id, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            if (await db.Entries.FindAsync([id], cancellationToken) is null)
                return EntryNotFound(id);

            return Results.Ok(ShortformDto.From(id, shortform.GetState(id)));
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
            return Results.Accepted($"/api/entries/{id}/shortform", ShortformDto.From(id, shortform.GetState(id)));
        }

        /// <param name="inline">True plays the file in the page's preview player instead of downloading it (not audited).</param>
        private static async Task<IResult> DownloadRender(int id, string renderId, bool? inline, LibraryDbContext db, ShortformService shortform,
            CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            var render = shortform.ListRenders(id).FirstOrDefault(x => x.Id == renderId);
            if (render is null || shortform.RenderPath(id, renderId, "mp4") is not { } path)
                return RenderNotFound();

            if (inline == true)
                return Results.File(path, "video/mp4", enableRangeProcessing: true);

            db.Audit(AuditActions.FileServed, id, $"vertical {renderId}");
            await db.SaveChangesAsync(cancellationToken);

            var fileName = Path.GetFileNameWithoutExtension(EntryEndpoints.GetDownloadFileName(entry)) + " (vertical).mp4";
            return Results.File(path, "video/mp4", fileName, enableRangeProcessing: true);
        }

        private static IResult GetThumbnail(int id, string renderId, ShortformService shortform)
        {
            var path = shortform.RenderPath(id, renderId, "jpg");
            return path is not null && File.Exists(path) ? Results.File(path, "image/jpeg") : RenderNotFound();
        }

        private static IResult DeleteRender(int id, string renderId, ShortformService shortform) =>
            shortform.DeleteRender(id, renderId) ? Results.NoContent() : RenderNotFound();

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult RenderNotFound() =>
            Results.Problem(detail: "That vertical render doesn't exist or has expired.", statusCode: StatusCodes.Status404NotFound);
    }
}
