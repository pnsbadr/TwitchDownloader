using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record MarkIdsDto(IReadOnlyList<long> Clips, IReadOnlyList<long> Videos, IReadOnlyList<long> Vods);

    /// <param name="Recent">Newest opened first, at most <see cref="MemberMark.MaxRecent"/> of each.</param>
    public sealed record MyMarksDto(MarkIdsDto Starred, MarkIdsDto Recent);

    public sealed record OpenedRequest(string? Kind, long? Id);

    /// <summary>
    /// A member's own stars and recently opened clips, videos and VODs (badrfly 2026-11-20, "Redesign the library for large teams",
    /// step 5): the library's ★ and Recent chips and Player 2's picker. Personal, so viewing the library is enough.
    /// </summary>
    public static class MarkEndpoints
    {
        public static IEndpointRouteBuilder MapMarkEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/my/marks", List).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPut("/api/my/stars/{kind}/{id:long}", Star).RequireTeam(TeamPermission.ViewLibrary);
            app.MapDelete("/api/my/stars/{kind}/{id:long}", Unstar).RequireTeam(TeamPermission.ViewLibrary);
            app.MapPost("/api/my/opened", Opened).RequireTeam(TeamPermission.ViewLibrary);
            return app;
        }

        internal static bool TryParseKind(string? text, out MarkKind kind)
        {
            switch (text?.ToLowerInvariant())
            {
                case "clip" or "clips" or "entry": kind = MarkKind.Clip; return true;
                case "video" or "videos": kind = MarkKind.Video; return true;
                case "vod" or "vods": kind = MarkKind.Vod; return true;
                default: kind = default; return false;
            }
        }

        private static IResult BadKind() =>
            Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Kind must be clip, video or vod."] });

        private static IResult NotFound() =>
            Results.Problem(detail: "That isn't one of your team's.", statusCode: StatusCodes.Status404NotFound);

        /// <summary>The member's marks of clips and videos that still exist (and that the query filters let them see).</summary>
        private static IQueryable<MemberMark> Live(LibraryDbContext db, int userId) =>
            db.MemberMarks.AsNoTracking().Where(x => x.UserId == userId
                && (x.Kind == MarkKind.Vod
                    || (x.Kind == MarkKind.Clip && db.Entries.Any(e => e.Id == x.ItemId))
                    || (x.Kind == MarkKind.Video && db.Videos.Any(v => v.Id == x.ItemId))));

        private static async Task<IResult> List(TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var marks = await Live(db, team.UserId).ToListAsync(cancellationToken);
            MarkIdsDto Ids(IEnumerable<MemberMark> rows) => new(
                rows.Where(x => x.Kind == MarkKind.Clip).Select(x => x.ItemId).ToList(),
                rows.Where(x => x.Kind == MarkKind.Video).Select(x => x.ItemId).ToList(),
                rows.Where(x => x.Kind == MarkKind.Vod).Select(x => x.ItemId).ToList());
            return Results.Ok(new MyMarksDto(
                Ids(marks.Where(x => x.StarredAtUtc != null).OrderByDescending(x => x.StarredAtUtc)),
                Ids(marks.Where(x => x.OpenedAtUtc != null).OrderByDescending(x => x.OpenedAtUtc))));
        }

        private static async Task<bool> ExistsAsync(LibraryDbContext db, MarkKind kind, long id, CancellationToken cancellationToken) => kind switch
        {
            MarkKind.Clip => await db.Entries.AnyAsync(x => x.Id == id, cancellationToken),
            MarkKind.Video => await db.Videos.AnyAsync(x => x.Id == id, cancellationToken),
            _ => id > 0,
        };

        private static async Task<MemberMark?> FindAsync(LibraryDbContext db, TeamContext team, MarkKind kind, long id, CancellationToken cancellationToken) =>
            await db.MemberMarks.FirstOrDefaultAsync(x => x.UserId == team.UserId && x.Kind == kind && x.ItemId == id, cancellationToken);

        private static async Task<IResult> Star(string kind, long id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (!TryParseKind(kind, out var markKind))
                return BadKind();
            if (!await ExistsAsync(db, markKind, id, cancellationToken))
                return NotFound();

            var mark = await FindAsync(db, team, markKind, id, cancellationToken);
            if (mark is null)
                db.MemberMarks.Add(mark = new MemberMark { TeamId = team.TeamId, UserId = team.UserId, Kind = markKind, ItemId = id });
            mark.StarredAtUtc ??= DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        private static async Task<IResult> Unstar(string kind, long id, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (!TryParseKind(kind, out var markKind))
                return BadKind();

            var mark = await FindAsync(db, team, markKind, id, cancellationToken);
            if (mark is not null)
            {
                mark.StarredAtUtc = null;
                if (mark.OpenedAtUtc is null)
                    db.MemberMarks.Remove(mark);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        /// <summary>The page loaded an item in a player: it goes to the top of the member's Recent, the oldest beyond the limit drop off.</summary>
        private static async Task<IResult> Opened(OpenedRequest request, TeamContext team, LibraryDbContext db, CancellationToken cancellationToken)
        {
            if (!TryParseKind(request.Kind, out var markKind))
                return BadKind();
            if (request.Id is not { } id || !await ExistsAsync(db, markKind, id, cancellationToken))
                return NotFound();

            var mark = await FindAsync(db, team, markKind, id, cancellationToken);
            if (mark is null)
                db.MemberMarks.Add(mark = new MemberMark { TeamId = team.TeamId, UserId = team.UserId, Kind = markKind, ItemId = id });
            mark.OpenedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var old = await db.MemberMarks.Where(x => x.UserId == team.UserId && x.Kind == markKind && x.OpenedAtUtc != null)
                .OrderByDescending(x => x.OpenedAtUtc).Skip(MemberMark.MaxRecent).ToListAsync(cancellationToken);
            if (old.Count > 0)
            {
                foreach (var row in old)
                {
                    row.OpenedAtUtc = null;
                    if (row.StarredAtUtc is null)
                        db.MemberMarks.Remove(row);
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.NoContent();
        }

        /// <summary>
        /// The <c>mark=</c> filter of the clip and video lists: <c>starred</c> = the caller's stars, <c>recent</c> = what they opened
        /// last (<see cref="MemberMark.MaxRecent"/>). Null = no filter; a problem for anything else.
        /// </summary>
        internal static (IQueryable<long>? Ids, IResult? Problem) Filter(LibraryDbContext db, string? mark, MarkKind kind, int userId)
        {
            var mine = db.MemberMarks.Where(x => x.UserId == userId && x.Kind == kind);
            switch (mark?.ToLowerInvariant())
            {
                case null or "":
                    return (null, null);
                case "starred":
                    return (mine.Where(x => x.StarredAtUtc != null).Select(x => x.ItemId), null);
                case "recent":
                    return (mine.Where(x => x.OpenedAtUtc != null).OrderByDescending(x => x.OpenedAtUtc).Take(MemberMark.MaxRecent).Select(x => x.ItemId), null);
                default:
                    return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["mark"] = ["Mark must be starred or recent."] }));
            }
        }
    }
}
