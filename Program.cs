using Lagrange.OneBot.Utility;
using CameraScreenshotBotService;
using CameraScreenshotBotService.Services;
using FFmpeg.AutoGen;



DynamicallyLoadedBindings.Initialize();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<OneBotSigner>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<ScreenshotService>();
builder.Services.AddHostedService<Worker>();

ffmpeg.RootPath =
    builder.Configuration.GetSection("ffmpegRoot").Value
    ?? throw new ApplicationException("Œ¥…Ë÷√ffmpeg");

var host = builder.Build();
host.Run();
