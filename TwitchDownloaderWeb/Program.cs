using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;
using TwitchDownloaderWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloadSettings>(builder.Configuration.GetSection(DownloadSettings.SectionName));
builder.Services.AddProblemDetails();

var databasePath = Path.GetFullPath(builder.Configuration["Database:Path"] ?? "App_Data/clipco.db", builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddDbContext<LibraryDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton<VodInfoService>();
builder.Services.AddSingleton<VodDownloadRunner>();
builder.Services.AddSingleton<LibraryStorage>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<LibraryCleanupService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.Migrate();
}

app.UseExceptionHandler();

app.MapVodDownloadEndpoints();
app.MapEntryEndpoints();

app.Run();
