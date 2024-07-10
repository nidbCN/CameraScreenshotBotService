using CameraScreenshotBotService;
using CameraScreenshotBotService.Services;
using FFmpeg.AutoGen;
using CameraScreenshotBotService.Configs;

DynamicallyLoadedBindings.Initialize();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<StreamOption>(
    builder.Configuration.GetSection(nameof(StreamOption)));
builder.Services.Configure<BotOption>(
    builder.Configuration.GetSection(nameof(BotOption)));

var streamConfig = builder.Configuration.GetSection(nameof(StreamOption))
    .Get<StreamOption>();

ffmpeg.RootPath =
    streamConfig?.FfmpegRoot;

builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<BotService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
// test ffmpeg load
try
{
    var version = ffmpeg.av_version_info();
    logger.LogInformation("Load ffmpeg version {v}", version);
}
catch (NotSupportedException e)
{
    logger.LogCritical(e, "Failed to load ffmpeg, exit.");
    return;
}

host.Run();