using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.Shortform;

using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record ShortformRenderDto(string Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, long SizeBytes, ShortformLayout Layout,
        string FileUrl, string? ThumbnailUrl)
    {
        /// <summary>Who made it (null for renders from before this was kept).</summary>
        public int? CreatedByUserId { get; init; }

        /// <summary>The saved edit it was made for; null = the clip itself.</summary>
        public int? EditId { get; init; }

        public string? CreatedBy { get; init; }
    }

    /// <param name="Layout">The running render's layout, else the newest kept render's (so the editor reopens with it).</param>
    public sealed record ShortformDto(string Status, double Progress, string? Error, ShortformLayout? Layout, IReadOnlyList<ShortformRenderDto> Renders)
    {
        /// <summary>The saved edit the running render is for (null = the clip itself, or nothing running).</summary>
        public int? RenderingEditId { get; init; }

        public static ShortformDto From(int entryId, ShortformState state, IReadOnlyDictionary<int, string>? names = null) => new(state.Status.ToString(),
            state.Progress, state.Error, state.Layout,
            state.Renders.Select(x => new ShortformRenderDto(x.Id, x.CreatedAtUtc, x.ExpiresAtUtc, x.SizeBytes, x.Layout,
                $"/api/entries/{entryId}/shortform/{x.Id}/file",
                x.HasThumbnail ? $"/api/entries/{entryId}/shortform/{x.Id}/thumbnail" : null)
            {
                CreatedByUserId = x.CreatedByUserId,
                EditId = x.EditId,
                CreatedBy = x.CreatedByUserId is { } u ? names?.GetValueOrDefault(u) : null,
            }).ToList()) { RenderingEditId = state.RenderingEditId };

        public static async Task<ShortformDto> FromAsync(LibraryDbContext db, int entryId, ShortformState state, CancellationToken cancellationToken) =>
            From(entryId, state, await TimelineEndpoints.UserNamesAsync(db, state.Renders.Where(x => x.CreatedByUserId is not null).Select(x => x.CreatedByUserId!.Value),
                cancellationToken));
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
            group.MapGet("/", GetShortform).RequireTeam(TeamPermission.ViewLibrary);
            group.MapPost("/", StartShortform).RequireTeam(TeamPermission.MakeVertical);
            group.MapGet("/{renderId}/file", DownloadRender).RequireTeam(TeamPermission.ViewLibrary);
            group.MapGet("/{renderId}/thumbnail", GetThumbnail).RequireTeam(TeamPermission.ViewLibrary);
            group.MapDelete("/{renderId}", DeleteRender).RequireTeam(TeamPermission.DeleteRenders);
            return app;
        }

        private static async Task<IResult> GetShortform(int id, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            if (await db.Entries.FindAsync([id], cancellationToken) is null)
                return EntryNotFound(id);

            return Results.Ok(await ShortformDto.FromAsync(db, id, shortform.GetState(id), cancellationToken));
        }

        /// <summary>Starts a render from the entry's file (stored, or an unexpired on-demand file). 202 with the new state.</summary>
        /// <param name="editId">Render a saved edit of the clip (also accepted as <c>editId</c> in the body): what the edit removes is left out
        /// of the layout (its parts are cut down to what the edit keeps) and the render remembers the edit.</param>
        private static async Task<IResult> StartShortform(int id, ShortformLayout layout, int? editId, TeamContext team, LibraryDbContext db, LibraryStorage storage,
            ShortformService shortform, CancellationToken cancellationToken)
        {
            var entry = await db.Entries.FindAsync([id], cancellationToken);
            if (entry is null)
                return EntryNotFound(id);

            var fileSeconds = EntryDto.FileSeconds(entry);
            editId = layout.EditId ?? editId;
            layout = layout with { EditId = null };
            if (layout.Validate(fileSeconds) is { } error)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = [error] });
            layout = layout.Normalize();

            if (editId is { } edit)
            {
                var kept = await ItemEditEndpoints.ClipEditFilePartsAsync(db, edit, entry, cancellationToken);
                if (kept is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["editId"] = ["That isn't an edit of this clip."] });
                if (ShortformLayout.KeepOnly(layout, kept, fileSeconds) is not { } restricted)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = ["The edit leaves nothing of this layout."] });
                if (restricted.Validate(fileSeconds) is { } editError)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = [editError] });
                layout = restricted.Normalize();
            }
            else if (ItemEditEndpoints.ClipOwnFileParts(entry) is { } own)
            {
                // The file holds the whole clip, so a plain render leaves out the clip's own cut-out parts like its other files do
                if (ShortformLayout.KeepOnly(layout, own, fileSeconds) is not { } restricted)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = ["The clip's cut-out parts leave nothing of this layout."] });
                if (restricted.Validate(fileSeconds) is { } cutError)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["layout"] = [cutError] });
                layout = restricted.Normalize();
            }

            var relativePath = entry.Status == EntryStatus.Ready ? entry.FilePath
                : EntryDto.HasUnexpiredGeneratedFile(entry) ? entry.GeneratedFilePath
                : null;
            if (relativePath is null || !File.Exists(storage.Resolve(relativePath)))
                return Results.Problem(detail: "The entry's file isn't ready, so it can't be made vertical yet.", statusCode: StatusCodes.Status409Conflict);

            shortform.Start(id, storage.Resolve(relativePath), fileSeconds, layout, team.UserId, editId);
            // The render itself is audited by the background job, which doesn't know who asked; this records who did
            db.Audit(AuditActions.ShortformRequested, id, layout.Summary + (editId is { } e ? $", edit {e}" : ""));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Accepted($"/api/entries/{id}/shortform", await ShortformDto.FromAsync(db, id, shortform.GetState(id), cancellationToken));
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

        private static async Task<IResult> GetThumbnail(int id, string renderId, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            // The lookup is team-scoped, so another team's renders 404 like missing ones
            if (await db.Entries.FindAsync([id], cancellationToken) is null)
                return EntryNotFound(id);

            var path = shortform.RenderPath(id, renderId, "jpg");
            return path is not null && File.Exists(path) ? Results.File(path, "image/jpeg") : RenderNotFound();
        }

        private static async Task<IResult> DeleteRender(int id, string renderId, LibraryDbContext db, ShortformService shortform, CancellationToken cancellationToken)
        {
            if (await db.Entries.FindAsync([id], cancellationToken) is null)
                return EntryNotFound(id);

            if (!shortform.DeleteRender(id, renderId))
                return RenderNotFound();

            db.Audit(AuditActions.ShortformDeleted, id, renderId);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static IResult EntryNotFound(int id) =>
            Results.Problem(detail: $"Entry {id} not found.", statusCode: StatusCodes.Status404NotFound);

        private static IResult RenderNotFound() =>
            Results.Problem(detail: "That vertical render doesn't exist or has expired.", statusCode: StatusCodes.Status404NotFound);
    }
}
