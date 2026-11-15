using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;

using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{

    public sealed record ChannelDto(int Id, string Login, string DisplayName, bool WatchMutedAudio, DateTime AddedAtUtc, string? AddedByUserId)
    {
        public static ChannelDto From(SavedChannel channel) =>
            new(channel.Id, channel.Login, channel.DisplayName, channel.WatchMutedAudio, channel.AddedAtUtc, channel.AddedByUserId);
    }

    public sealed record ChannelVodDto(long VodId, string Title, int LengthSeconds, string Length, DateTime CreatedAtUtc, string? ThumbnailUrl,
        int ViewCount, string? Game, bool UnmuteWindowOpen, string? RescueStatus, bool RescuePaused, int? RescueMutedCount, int? RescueUnrecoverableCount);

    public sealed record ChannelVodPageDto(string Login, string Type, IEnumerable<ChannelVodDto> Items, string? NextCursor, int TotalCount);

    public static class ChannelEndpoints
    {
        private const int DefaultPageSize = 20;

        public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
        {
            var channels = app.MapGroup("/api/channels");
            // The team's channels are assigned by the app Owner (Admin page, registration links); members can't add or remove them
            // (badrfly, 2026-11-15). See AdminEndpoints' /api/admin/teams/{id}/channels.
            channels.MapGet("/", ListChannels).RequireTeam(TeamPermission.ViewLibrary);
            channels.MapGet("/{login}/vods", ListChannelVods).RequireTeam(TeamPermission.ViewLibrary);

            return app;
        }

        private static async Task<IResult> ListChannels(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var channels = await db.Channels.AsNoTracking().Where(x => x.AccessEndedAtUtc == null).OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
            return Results.Ok(channels.Select(ChannelDto.From));
        }

        /// <param name="login">One of the team's channels.</param>
        /// <param name="type">"archive" (past broadcasts, default), "highlight", "upload" or "all".</param>
        /// <param name="cursor">nextCursor from the previous page.</param>
        private static async Task<IResult> ListChannelVods(string login, string? type, string? cursor, int? limit, ChannelService channelService,
            LibraryDbContext db, TeamChannelAccess access, IOptions<AudioRescueSettings> rescueSettings, CancellationToken cancellationToken)
        {
            if (ChannelService.NormalizeLogin(login) is not { } normalizedLogin)
                return InvalidLogin("login");

            if (!await access.IsAssignedAsync(normalizedLogin, cancellationToken))
                return NotAssigned(normalizedLogin);

            if (!ChannelService.TryParseVodType(type, out var vodType))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Type must be archive, highlight, upload or all."] });

            if (!ChannelService.IsValidCursor(cursor))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor."] });

            ChannelVodPage? page;
            try
            {
                page = await channelService.GetVodsAsync(normalizedLogin, vodType, cursor, limit ?? DefaultPageSize);
            }
            catch (HttpRequestException)
            {
                return TwitchUnreachable();
            }

            if (page is null)
                return ChannelNotFound(normalizedLogin);

            var vodIds = page.Items.Select(x => x.VodId).ToArray();
            var rescues = await db.AudioRescues.AsNoTracking().Where(x => vodIds.Contains(x.VodId))
                .ToDictionaryAsync(x => x.VodId, cancellationToken);
            var enabled = rescueSettings.Value.Enabled;
            var watched = enabled ? (await AudioRescueService.AutoWatchedLogins(db).Distinct().ToListAsync(cancellationToken)).ToHashSet() : [];

            var items = page.Items.Select(x =>
            {
                var rescue = rescues.GetValueOrDefault(x.VodId);
                return new ChannelVodDto(x.VodId, x.Title, x.LengthSeconds, TrimTime.Format(TimeSpan.FromSeconds(x.LengthSeconds)),
                    x.CreatedAtUtc, x.ThumbnailUrl, x.ViewCount, x.Game, x.UnmuteWindowOpen, rescue?.Status.ToString(),
                    rescue is { ManuallyRequested: false, Status: AudioRescueStatus.Grabbing or AudioRescueStatus.WaitingToPrune }
                    && !(rescue.ChannelLogin is not null && watched.Contains(rescue.ChannelLogin)),
                    rescue?.MutedCount, rescue?.UnrecoverableCount);
            });

            return Results.Ok(new ChannelVodPageDto(normalizedLogin, vodType.ToString().ToLowerInvariant(), items, page.NextCursor, page.TotalCount));
        }

        internal static IResult NotAssigned(string login) =>
            Results.Problem(detail: $"\"{login}\" isn't one of your team's channels.", statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "channel-not-assigned" });

        internal static IResult InvalidLogin(string field) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["Enter a Twitch channel name (letters, numbers and underscores) or a twitch.tv link."] });

        internal static IResult TwitchUnreachable() =>
            Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway);

        internal static IResult ChannelNotFound(string login) =>
            Results.Problem(detail: $"Twitch channel \"{login}\" was not found.", statusCode: StatusCodes.Status404NotFound);
    }
}
