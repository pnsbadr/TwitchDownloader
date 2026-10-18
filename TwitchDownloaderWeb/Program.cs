using TwitchDownloaderWeb;
using TwitchDownloaderWeb.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloadSettings>(builder.Configuration.GetSection(DownloadSettings.SectionName));
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.MapVodDownloadEndpoints();

app.Run();
