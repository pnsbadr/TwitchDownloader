using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;
using TwitchDownloaderWeb.Services;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Shortform;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloadSettings>(builder.Configuration.GetSection(DownloadSettings.SectionName));
builder.Services.Configure<AudioRescueSettings>(builder.Configuration.GetSection(AudioRescueSettings.SectionName));
builder.Services.AddProblemDetails();

var databasePath = Path.GetFullPath(builder.Configuration["Database:Path"] ?? "App_Data/clipco.db", builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddDbContext<LibraryDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton<VodInfoService>();
builder.Services.AddSingleton<IVodLookup>(sp => sp.GetRequiredService<VodInfoService>());
builder.Services.AddSingleton<ChannelService>();
builder.Services.AddHttpClient(nameof(AudioRescueService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(nameof(AudioRestoreService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient(nameof(ExactTrimService), client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<IVodPlaylistSource, TwitchVodPlaylistSource>();
builder.Services.AddSingleton<AudioRestoreService>();
builder.Services.AddSingleton<AudioRescueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioRescueService>());
builder.Services.AddSingleton<VodDownloadRunner>();
builder.Services.AddSingleton<ExactTrimService>();
builder.Services.AddSingleton<LibraryStorage>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<ShortformService>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<LibraryCleanupService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseDefaultFiles();
// The page has no build step or versioned file names, so make browsers check for a newer app.js/app.css on every load
// (a cheap 304 when unchanged) instead of guessing a cache lifetime and running old code after an update
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapVodDownloadEndpoints();
app.MapEntryEndpoints();
app.MapChannelEndpoints();
app.MapAudioRescueEndpoints();
app.MapShortformEndpoints();

app.Run();
