using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Playback;

using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{

    /// <param name="WatchMutedAudio">Audio is saved automatically (set by the app Owner).</param>
    /// <param name="SaveAudio">Save audio now may be used on its VODs (set by the app Owner).</param>
    /// <param name="IsPrimary">The channel the team is known by (<see cref="TeamLabels.Primary"/>): the library starts on it.</param>
    public sealed record ChannelDto(int Id, string Login, string DisplayName, bool WatchMutedAudio, bool SaveAudio, DateTime AddedAtUtc, string? AddedByUserId,
        bool IsPrimary = false)
    {
        public static ChannelDto From(SavedChannel channel) =>
            new(channel.Id, channel.Login, channel.DisplayName, channel.WatchMutedAudio, channel.AllowSaveAudio || channel.WatchMutedAudio,
                channel.AddedAtUtc, channel.AddedByUserId);
    }

    /// <summary>A team channel for the library's channel bar: its Twitch picture and whether it's live (badrfly 2026-11-19).</summary>
    public sealed record ChannelCardDto(string Login, string? ProfileImageUrl, bool? Live, DateTime? LiveSinceUtc);

    public sealed record ChannelVodDto(long VodId, string Title, int LengthSeconds, string Length, DateTime CreatedAtUtc, string? ThumbnailUrl,
        int ViewCount, string? Game, bool UnmuteWindowOpen, string? RescueStatus, bool RescuePaused, int? RescueMutedCount, int? RescueUnrecoverableCount)
    {
        /// <summary>A highlight's broadcast's start (its createdAt), when it can be told (<see cref="Services.Playback.HighlightSources"/>).</summary>
        public DateTime? SourceStartUtc { get; init; }
    }

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
            channels.MapGet("/cards", ListChannelCards).RequireTeam(TeamPermission.ViewLibrary);

            return app;
        }

        private static async Task<IResult> ListChannels(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var channels = await db.Channels.AsNoTracking().Where(x => x.AccessEndedAtUtc == null).OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
            var primary = TeamLabels.Primary(channels);
            return Results.Ok(channels.Select(x => ChannelDto.From(x) with { IsPrimary = x.Id == primary?.Id }));
        }

        /// <summary>
        /// The team's channels' Twitch pictures and live state for the library's channel bar (the start screen's cards use the same source,
        /// cached 5 min; the live watcher's state wins when it watches the channel). A channel Twitch couldn't be asked about has nulls.
        /// </summary>
        private static async Task<IResult> ListChannelCards(LibraryDbContext db, IChannelCardSource cards, CancellationToken cancellationToken)
        {
            var channels = await db.Channels.AsNoTracking().Where(x => x.AccessEndedAtUtc == null).ToListAsync(cancellationToken);
            var infos = await Task.WhenAll(channels.Select(x => cards.GetAsync(x.Login, x.TwitchUserId, cancellationToken)));
            return Results.Ok(channels.Select((x, i) => new ChannelCardDto(x.Login, infos[i].ProfileImageUrl, infos[i].Live, infos[i].LiveSinceUtc)));
        }

        /// <summary>
        /// The names clips and videos may carry for these team channels (<c>channels=login,login</c>), lower-case: each channel's login and
        /// display name, since entries and videos keep the name Twitch gave the VOD's owner. Null when no filter was asked for; empty when
        /// none of them is the team's (so nothing matches).
        /// </summary>
        public static async Task<List<string>?> ChannelNamesAsync(LibraryDbContext db, string? channels, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(channels))
                return null;
            var logins = channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant()).Distinct().Take(50).ToList();
            var rows = await db.Channels.AsNoTracking().Where(x => logins.Contains(x.Login)).ToListAsync(cancellationToken);
            return rows.SelectMany(x => new[] { x.Login, x.DisplayName }).Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x.ToLowerInvariant()).Distinct().ToList();
        }

        /// <param name="login">One of the team's channels.</param>
        /// <param name="type">"archive" (past broadcasts, default), "highlight", "upload" or "all".</param>
        /// <param name="cursor">nextCursor from the previous page.</param>
        private static async Task<IResult> ListChannelVods(string login, string? type, string? cursor, int? limit, IChannelVodSource channelService,
            LibraryDbContext db, TeamChannelAccess access, IOptions<AudioRescueSettings> rescueSettings, HighlightSources highlights,
            CancellationToken cancellationToken)
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

            // Highlights: the broadcast each was cut from (badrfly 2026-11-21: the library shows them under it)
            var sources = new Dictionary<long, DateTime?>();
            if (vodType == VodType.Highlight)
            {
                var found = await Task.WhenAll(page.Items.Select(async x => (x.VodId, Start: await highlights.GetSourceStartAsync(x.VodId, cancellationToken))));
                foreach (var (id, start) in found)
                    sources[id] = start;
            }

            var items = page.Items.Select(x =>
            {
                var rescue = rescues.GetValueOrDefault(x.VodId);
                return new ChannelVodDto(x.VodId, x.Title, x.LengthSeconds, TrimTime.Format(TimeSpan.FromSeconds(x.LengthSeconds)),
                    x.CreatedAtUtc, x.ThumbnailUrl, x.ViewCount, x.Game, x.UnmuteWindowOpen, rescue?.Status.ToString(),
                    rescue is { ManuallyRequested: false, Status: AudioRescueStatus.Grabbing or AudioRescueStatus.WaitingToPrune }
                    && !(rescue.ChannelLogin is not null && watched.Contains(rescue.ChannelLogin)),
                    rescue?.MutedCount, rescue?.UnrecoverableCount)
                { SourceStartUtc = sources.GetValueOrDefault(x.VodId) };
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
