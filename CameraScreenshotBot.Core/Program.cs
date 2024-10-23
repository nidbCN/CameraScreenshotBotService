using FFmpeg.AutoGen;
using CameraCaptureBot.Core;
using CameraCaptureBot.Core.Configs;
using CameraCaptureBot.Core.Extensions.DependencyInjection;
using CameraCaptureBot.Core.Services;

DynamicallyLoadedBindings.Initialize();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(s =>
{
    s.ServiceName = "Live stream capture bot";
});

builder.Services.Configure<StreamOption>(
    builder.Configuration.GetSection(nameof(StreamOption)));
builder.Services.Configure<BotOption>(
    builder.Configuration.GetSection(nameof(BotOption)));

builder.Services.AddSingleton<CaptureService>();
builder.Services.AddIsoStorages();
builder.Services.AddBots(() => builder.Configuration
    .GetSection(nameof(BotOption))
    .Get<BotOption>() ?? new BotOption());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var env = host.Services.GetRequiredService<IHostEnvironment>();

var streamConfig = builder.Configuration
    .GetSection(nameof(StreamOption))
    .Get<StreamOption>();
ConfigureFfMpeg(streamConfig);

// set current directory
logger.LogDebug("Content Root: {r}, current {c}, base {b}",
    builder.Environment.ContentRootPath,
    Directory.GetCurrentDirectory(),
    AppContext.BaseDirectory
);
Directory.SetCurrentDirectory(env.ContentRootPath);

host.Run();
return;

void ConfigureFfMpeg(StreamOption? config)
{
    // config ffmpeg
    ffmpeg.RootPath =
        config?.FfmpegRoot;

    // test ffmpeg load
    try
    {
        var version = ffmpeg.av_version_info();
        logger.LogInformation("Load ffmpeg version {v}", version ?? "unknown");
    }
    catch (NotSupportedException e)
    {
        logger.LogCritical(e, "Failed to load ffmpeg, exit.");
    }
}
