using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Projects
{
    /// <summary>
    /// Where a project (an "edit" on the page) belongs: its VOD and the clip or video it's an edit of (badrfly 2026-11-17: "the VOD is the
    /// project"). Projects made before that are placed once at startup from what they hold: a project converted from a saved edit goes
    /// under the edit's clip or video; otherwise under what its first piece (on the project's clock) was copied from, on that piece's VOD.
    /// A piece copied from a segment or a VOD range gives no clip or video, so the edit sits on the VOD itself.
    /// </summary>
    public static class ProjectParents
    {
        public sealed record Parent(long VodId, int? EntryId, int? VideoId);

        /// <summary>Places every project that has no VOD yet but has pieces. Returns how many were placed.</summary>
        public static async Task<int> AssignAsync(LibraryDbContext db, ILogger logger, CancellationToken cancellationToken = default)
        {
            var projects = await db.Projects.IgnoreQueryFilters().Where(x => x.VodId == 0 && x.PieceCount > 0).ToListAsync(cancellationToken);
            if (projects.Count == 0)
                return 0;

            foreach (var project in projects)
            {
                var parent = await FindAsync(db, project.TeamId, ProjectContent.Read(project.Content), project.ConvertedFromEditId, cancellationToken);
                if (parent is null)
                    continue;
                project.VodId = parent.VodId;
                project.EntryId = parent.EntryId;
                project.VideoId = parent.VideoId;
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Placed {Count} project(s) under their VOD, clip or video", projects.Count(x => x.VodId != 0));
            return projects.Count(x => x.VodId != 0);
        }

        /// <summary>Where content belongs, from its first piece (null when it has none).</summary>
        public static async Task<Parent?> FindAsync(LibraryDbContext db, int teamId, ProjectContent content, int? convertedFromEditId,
            CancellationToken cancellationToken)
        {
            var first = (content.Pieces ?? []).OrderBy(x => x.At).FirstOrDefault();
            if (first is null)
                return null;

            if (convertedFromEditId is { } fromEdit && await EditParentAsync(db, teamId, fromEdit, cancellationToken) is { } editParent)
                return editParent;

            switch (first.Source?.Kind)
            {
                case "clip":
                    var entry = await db.Entries.IgnoreQueryFilters().AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == (int)first.Source.Id && x.TeamId == teamId, cancellationToken);
                    if (entry is not null && entry.VodId == first.VodId)
                        return new Parent(first.VodId, entry.Id, null);
                    break;
                case "video":
                    var video = await db.Videos.IgnoreQueryFilters().AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == (int)first.Source.Id && x.TeamId == teamId, cancellationToken);
                    if (video is not null && video.VodId == first.VodId)
                        return new Parent(first.VodId, null, video.Id);
                    break;
                case "edit":
                    if (await EditParentAsync(db, teamId, (int)first.Source.Id, cancellationToken) is { } parent && parent.VodId == first.VodId)
                        return parent;
                    break;
            }
            return new Parent(first.VodId, null, null);
        }

        private static async Task<Parent?> EditParentAsync(LibraryDbContext db, int teamId, int editId, CancellationToken cancellationToken)
        {
            var edit = await db.ItemEdits.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == editId && x.TeamId == teamId, cancellationToken);
            if (edit is null || edit.VodId <= 0)
                return null;
            if (edit.EntryId is { } entryId && await db.Entries.IgnoreQueryFilters().AnyAsync(x => x.Id == entryId, cancellationToken))
                return new Parent(edit.VodId, entryId, null);
            if (edit.VideoId is { } videoId && await db.Videos.IgnoreQueryFilters().AnyAsync(x => x.Id == videoId, cancellationToken))
                return new Parent(edit.VodId, null, videoId);
            return new Parent(edit.VodId, null, null);
        }
    }
}
