using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;
using TwitchDownloaderWeb.Services.Accounts;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Playback;
using TwitchDownloaderWeb.Services.Segments;
using TwitchDownloaderWeb.Services.Shortform;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloadSettings>(builder.Configuration.GetSection(DownloadSettings.SectionName));
builder.Services.Configure<AudioRescueSettings>(builder.Configuration.GetSection(AudioRescueSettings.SectionName));
builder.Services.AddProblemDetails();
// Bugs, errors and crashes for the Owner's Reports tab: every Error log line becomes a report (Services/ErrorReports)
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.ErrorReports.ErrorReportSink>();
builder.Services.AddSingleton<ILoggerProvider, TwitchDownloaderWeb.Services.ErrorReports.ErrorReportLoggerProvider>();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.ErrorReports.ErrorReportWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TwitchDownloaderWeb.Services.ErrorReports.ErrorReportWriter>());
builder.Services.AddExceptionHandler<TwitchUnreachableExceptionHandler>();
builder.Services.AddClipcoAuth(builder.Configuration.GetValue("Auth:LoginAttemptsPerMinute", 10));

var databasePath = Path.GetFullPath(builder.Configuration["Database:Path"] ?? "App_Data/clipco.db", builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddDbContext<LibraryDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton<VodInfoService>();
builder.Services.AddSingleton<IVodDetailsSource>(sp => sp.GetRequiredService<VodInfoService>());
builder.Services.AddSingleton<IVodQualitySource>(sp => sp.GetRequiredService<VodInfoService>());
builder.Services.AddSingleton<LiveMarkerEntryService>();
builder.Services.AddSingleton<ClipInfoService>();
builder.Services.AddSingleton<IVodLookup>(sp => sp.GetRequiredService<VodInfoService>());
builder.Services.AddSingleton<ChannelService>();
builder.Services.AddSingleton<IChannelLookup>(sp => sp.GetRequiredService<ChannelService>());
builder.Services.AddSingleton<IChannelVodSource>(sp => sp.GetRequiredService<ChannelService>());
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Sync.IVodLoudness, TwitchDownloaderWeb.Services.Sync.VodLoudness>();
builder.Services.AddHttpClient(TwitchDownloaderWeb.Services.Sync.VodLoudness.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<TeamChannelAccess>();
builder.Services.AddHttpClient(nameof(AudioRescueService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(nameof(AudioRestoreService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(nameof(ExactTrimService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<IVodPlaylistSource, TwitchVodPlaylistSource>();
builder.Services.AddSingleton<AudioRestoreService>();
builder.Services.AddSingleton<AudioRescueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioRescueService>());
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(VodPlaybackService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IVodPlaybackSource, TwitchVodPlaybackSource>();
builder.Services.AddSingleton<VodPlaybackService>();
builder.Services.AddSingleton<PlaybackAudioSwap>();
builder.Services.AddSingleton<CardThumbnailService>();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Sync.VodWaveService>();
builder.Services.AddSingleton<VodDownloadRunner>();
builder.Services.AddSingleton<ExactTrimService>();
builder.Services.AddSingleton<IVodCutter, VodCutService>();
builder.Services.AddSingleton<KeptPartsCutter>();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Projects.ProjectRenderer>();
builder.Services.AddSingleton<ClipFileService>();
builder.Services.AddSingleton<LibraryStorage>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<ShortformService>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddSingleton<SegmentExportQueue>();
builder.Services.AddHostedService<SegmentExportWorker>();
builder.Services.AddSingleton<ExportQueue>();
builder.Services.AddHostedService<ExportWorker>();
builder.Services.AddHostedService<LibraryCleanupService>();
builder.Services.AddHostedService<ReferenceVodCheckService>();
// Live stream updates: Twitch EventSub (WebSocket) → LiveStreamHub → open pages (GET /api/live/events)
builder.Services.AddHttpClient(TwitchDownloaderWeb.Services.Live.TwitchUserAuth.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient(TwitchDownloaderWeb.Services.Live.EventSubWatcher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Live.TwitchUserAuth>();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Live.LiveStreamHub>();
builder.Services.AddSingleton<IChannelCardSource, ChannelCardService>();
builder.Services.AddSingleton<TwitchDownloaderWeb.Services.Live.EventSubWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TwitchDownloaderWeb.Services.Live.EventSubWatcher>());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var startupDb = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    startupDb.Database.Migrate();
    // Write-ahead logging: readers (page polls, playback) no longer wait for the background jobs' progress writes, and writes are cheaper.
    // It is stored in the database file, so this only changes something the first time. Keep the -wal/-shm files next to clipco.db
    // when copying it (or stop the app first).
    startupDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    // Saved edits (the old Player 2) become projects, once each
    await TwitchDownloaderWeb.Services.Projects.SavedEditConversion.RunAsync(startupDb,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SavedEditConversion"));
    // Projects made before "the VOD is the project" go under their VOD and clip/video, once each
    await TwitchDownloaderWeb.Services.Projects.ProjectParents.AssignAsync(startupDb,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectParents"));
}

// A channel that just went live: the audio rescue looks for its new VOD right away instead of at its next poll
{
    var rescue = app.Services.GetRequiredService<AudioRescueService>();
    app.Services.GetRequiredService<TwitchDownloaderWeb.Services.Live.LiveStreamHub>().Changed += change =>
    {
        if (change.Live)
            rescue.DiscoverSoon(TimeSpan.FromSeconds(30));
    };
}

app.UseExceptionHandler();
app.UseDefaultFiles();
// The page has no build step or versioned file names, so make browsers check for a newer script/stylesheet on every load
// (a cheap 304 when unchanged) instead of guessing a cache lifetime and running old code after an update
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.UseRateLimiter();
app.UseAuthentication();
// After authentication: checks the login and team for /api requests and scopes the database to that team
app.UseTeamScope();
app.UseStaffScope();
// Player 2 edits that followed a clip/segment changed by this request: the page brings its open edit (and undo history) in step
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var syncs = context.RequestServices.GetService<TwitchDownloaderWeb.Services.Accounts.RequestActor>()?.SourceSyncs;
        if (syncs is { Count: > 0 })
            context.Response.Headers[TwitchDownloaderWeb.Services.Projects.ProjectSourceSync.HeaderName] =
                System.Text.Json.JsonSerializer.Serialize(syncs);
        return Task.CompletedTask;
    });
    await next(context);
});

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapAppSettingsEndpoints();
app.MapErrorReportEndpoints();
app.MapTeamEndpoints();
app.MapVodDownloadEndpoints();
app.MapEntryEndpoints();
app.MapChannelEndpoints();
app.MapAudioRescueEndpoints();
app.MapShortformEndpoints();
app.MapPlaybackEndpoints();
app.MapTimelineEndpoints();
app.MapSegmentExportEndpoints();
app.MapExportEndpoints();
app.MapCopyEndpoints();
app.MapVideoEndpoints();
app.MapItemEditEndpoints();
app.MapProjectEndpoints();
app.MapVodWorkEndpoints();
app.MapStatusEndpoints();
app.MapMarkEndpoints();
app.MapVodSyncEndpoints();
app.MapLiveEndpoints();

app.Run();

// Lets the tests start the app with WebApplicationFactory
public partial class Program;
