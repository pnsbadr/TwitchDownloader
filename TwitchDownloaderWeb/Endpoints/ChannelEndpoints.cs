using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Login">A Twitch login or twitch.tv/&lt;login&gt; URL.</param>
    public sealed record AddChannelRequest(string? Login);

    public sealed record ChannelDto(int Id, string Login, string DisplayName, bool WatchMutedAudio, DateTime AddedAtUtc, string? AddedByUserId)
    {
        public static ChannelDto From(SavedChannel channel) =>
            new(channel.Id, channel.Login, channel.DisplayName, channel.WatchMutedAudio, channel.AddedAtUtc, channel.AddedByUserId);
    }

    public sealed record ChannelVodDto(long VodId, string Title, int LengthSeconds, string Length, DateTime CreatedAtUtc, string? ThumbnailUrl,
        int ViewCount, string? Game, bool UnmuteWindowOpen, string? RescueStatus, int? RescueMutedCount, int? RescueUnrecoverableCount);

    public sealed record ChannelVodPageDto(string Login, string Type, IEnumerable<ChannelVodDto> Items, string? NextCursor, int TotalCount,
        bool RescueEnabled);

    public static class ChannelEndpoints
    {
        private const int DefaultPageSize = 20;

        public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
        {
            var channels = app.MapGroup("/api/channels");
            channels.MapGet("/", ListChannels);
            channels.MapPost("/", AddChannel);
            channels.MapDelete("/{id:int}", DeleteChannel);
            channels.MapGet("/{login}/vods", ListChannelVods);

            return app;
        }

        private static async Task<IResult> ListChannels(LibraryDbContext db, CancellationToken cancellationToken)
        {
            var channels = await db.Channels.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
            return Results.Ok(channels.Select(ChannelDto.From));
        }

        private static async Task<IResult> AddChannel(AddChannelRequest request, LibraryDbContext db, ChannelService channelService, CancellationToken cancellationToken)
        {
            if (ChannelService.NormalizeLogin(request.Login) is not { } login)
                return InvalidLogin("login");

            if (await db.Channels.AsNoTracking().FirstOrDefaultAsync(x => x.Login == login, cancellationToken) is { } existing)
                return Results.Problem(detail: $"{existing.DisplayName} is already saved.", statusCode: StatusCodes.Status409Conflict);

            ChannelDetails? details;
            try
            {
                details = await channelService.GetChannelAsync(login);
            }
            catch (HttpRequestException)
            {
                return TwitchUnreachable();
            }

            if (details is null)
                return ChannelNotFound(login);

            var channel = new SavedChannel
            {
                Login = details.Login.ToLowerInvariant(),
                DisplayName = details.DisplayName,
                TwitchUserId = details.TwitchUserId,
                AddedAtUtc = DateTime.UtcNow,
            };

            db.Channels.Add(channel);
            db.Audit(AuditActions.ChannelAdded, null, $"{channel.DisplayName} ({channel.Login})");
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Saved by another request between the check and the insert
                return Results.Problem(detail: $"{channel.DisplayName} is already saved.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created($"/api/channels/{channel.Id}", ChannelDto.From(channel));
        }

        private static async Task<IResult> DeleteChannel(int id, LibraryDbContext db, CancellationToken cancellationToken)
        {
            var channel = await db.Channels.FindAsync([id], cancellationToken);
            if (channel is null)
                return Results.Problem(detail: $"Saved channel {id} was not found.", statusCode: StatusCodes.Status404NotFound);

            db.Channels.Remove(channel);
            db.Audit(AuditActions.ChannelRemoved, null, $"{channel.DisplayName} ({channel.Login})");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }

        /// <param name="login">Any channel, saved or not.</param>
        /// <param name="type">"archive" (past broadcasts, default), "highlight", "upload" or "all".</param>
        /// <param name="cursor">nextCursor from the previous page.</param>
        private static async Task<IResult> ListChannelVods(string login, string? type, string? cursor, int? limit, ChannelService channelService,
            LibraryDbContext db, IOptions<AudioRescueSettings> rescueSettings, CancellationToken cancellationToken)
        {
            if (ChannelService.NormalizeLogin(login) is not { } normalizedLogin)
                return InvalidLogin("login");

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

            var items = page.Items.Select(x =>
            {
                var rescue = rescues.GetValueOrDefault(x.VodId);
                return new ChannelVodDto(x.VodId, x.Title, x.LengthSeconds, TrimTime.Format(TimeSpan.FromSeconds(x.LengthSeconds)),
                    x.CreatedAtUtc, x.ThumbnailUrl, x.ViewCount, x.Game, x.UnmuteWindowOpen, rescue?.Status.ToString(), rescue?.MutedCount, rescue?.UnrecoverableCount);
            });

            return Results.Ok(new ChannelVodPageDto(normalizedLogin, vodType.ToString().ToLowerInvariant(), items, page.NextCursor, page.TotalCount,
                rescueSettings.Value.Enabled));
        }

        private static IResult InvalidLogin(string field) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["Enter a Twitch channel name (letters, numbers and underscores) or a twitch.tv link."] });

        private static IResult TwitchUnreachable() =>
            Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway);

        private static IResult ChannelNotFound(string login) =>
            Results.Problem(detail: $"Twitch channel \"{login}\" was not found.", statusCode: StatusCodes.Status404NotFound);
    }
}
