using Lagrange.OneBot.Utility;
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

var streamConfig = builder.Configuration.GetSection(nameof(StreamOption)).Get<StreamOption>();

ffmpeg.RootPath =
    streamConfig?.FfmpegRoot;

builder.Services.AddSingleton<OneBotSigner>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<ScreenshotService>();
builder.Services.AddSingleton<BotService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// test ffmpeg load
var version = ffmpeg.av_version_info();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Load ffmpeg version {v}", version);

host.Run();
