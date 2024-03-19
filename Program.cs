using Lagrange.OneBot.Utility;
using CameraScreenshotBotService;
using CameraScreenshotBotService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<OneBotSigner>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<ScreenshotService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
