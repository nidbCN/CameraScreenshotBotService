using Lagrange.OneBot.Utility;
using CameraScreenshotBotService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<OneBotSigner>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
