using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Endpoints
{
    public sealed record AudioRescueDto(long VodId, string? ChannelLogin, string? Title, string Status, string? Error, int SegmentCount, int SavedCount,
        int MutedCount, int UnrecoverableCount, long StoredBytes, DateTime StreamStartUtc, DateTime CreatedAtUtc, DateTime? GrabCompletedAtUtc,
        DateTime? PruneAfterUtc)
    {
        public static AudioRescueDto From(VodAudioRescue x) => new(x.VodId, x.ChannelLogin, x.Title, x.Status.ToString(), x.Error, x.SegmentCount,
            x.SavedCount, x.MutedCount, x.UnrecoverableCount, x.StoredBytes, x.StreamStartUtc, x.CreatedAtUtc, x.GrabCompletedAtUtc, x.PruneAfterUtc);
    }

    public static class AudioRescueEndpoints
    {
        public static IEndpointRouteBuilder MapAudioRescueEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/vods/{vodId:long}/rescue", GetRescue);
            app.MapPost("/api/vods/{vodId:long}/rescue", StartRescue);
            return app;
        }

        private static async Task<IResult> GetRescue(long vodId, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var rescue = await db.AudioRescues.AsNoTracking().FirstOrDefaultAsync(x => x.VodId == vodId, cancellationToken);
            return rescue is null
                ? Results.Problem(detail: $"No audio has been saved for VOD {vodId}.", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(AudioRescueDto.From(rescue));
        }

        /// <summary>Starts saving a VOD's audio now (any VOD, saved channel or not). A failed rescue is retried; others are returned as they are.</summary>
        private static async Task<IResult> StartRescue(long vodId, LibraryDbContext db, VodInfoService vodInfo, AudioRescueService rescueService,
            IOptions<AudioRescueSettings> settings, CancellationToken cancellationToken)
        {
            if (!settings.Value.Enabled)
                return Results.Problem(detail: "Saving audio is turned off (AudioRescue:Enabled in appsettings.json).", statusCode: StatusCodes.Status409Conflict);

            var rescue = await db.AudioRescues.FirstOrDefaultAsync(x => x.VodId == vodId, cancellationToken);
            if (rescue is not null && rescue.Status != AudioRescueStatus.Failed)
                return Results.Ok(AudioRescueDto.From(rescue));

            if (rescue is not null)
            {
                rescue.Status = AudioRescueStatus.Grabbing;
                rescue.Error = null;
                rescue.NextCheckAtUtc = DateTime.UtcNow;
                rescue.UpdatedAtUtc = DateTime.UtcNow;
                db.Audit(AuditActions.AudioRescueStarted, null, $"VOD {vodId}: retry requested");
            }
            else
            {
                VodDetails? vod;
                try
                {
                    vod = await vodInfo.GetVodAsync(vodId);
                }
                catch (HttpRequestException)
                {
                    return Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway);
                }

                if (vod is null)
                    return Results.Problem(detail: $"VOD {vodId} was not found. It may be deleted, expired or private.", statusCode: StatusCodes.Status404NotFound);

                rescue = AudioRescueService.AddRescue(db, vodId, vod.ChannelName?.ToLowerInvariant(), vod.Title, vod.StreamDateUtc, "Requested from the page");
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Started by the channel watch or another request at the same moment
                rescue = await db.AudioRescues.AsNoTracking().FirstAsync(x => x.VodId == vodId, cancellationToken);
                return Results.Ok(AudioRescueDto.From(rescue));
            }

            rescueService.Wake();
            return Results.Accepted($"/api/vods/{vodId}/rescue", AudioRescueDto.From(rescue));
        }
    }
}
