using CameraScreenshotBot.Core;
using CameraScreenshotBot.Core.Configs;
using CameraScreenshotBot.Core.Extensions.DependencyInjection;
using CameraScreenshotBot.Core.Services;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

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
        return;
    }

    // ÉèÖÃÈÕÖ¾
    if (config?.LogLevel is null) return;

    var level = config.LogLevel.ToUpper() switch
    {
        "TRACE" => ffmpeg.AV_LOG_TRACE,
        "VERBOSE" => ffmpeg.AV_LOG_VERBOSE,
        "DEBUG" => ffmpeg.AV_LOG_DEBUG,
        "INFO" => ffmpeg.AV_LOG_INFO,
        "WARNING" => ffmpeg.AV_LOG_WARNING,
        "ERROR" => ffmpeg.AV_LOG_ERROR,
        "FATAL" => ffmpeg.AV_LOG_FATAL,
        "PANIC" => ffmpeg.AV_LOG_PANIC,
        _ => ffmpeg.AV_LOG_INFO,
    };

    unsafe
    {
        av_log_set_callback_callback logCallback = FfMpegLogInvoke;
        ffmpeg.av_log_set_level(level);
        ffmpeg.av_log_set_callback(logCallback);
    }
}

unsafe void FfMpegLogInvoke(void* p0, int level, string format, byte* vl)
{
    if (level > ffmpeg.av_log_get_level()) return;

    const int lineSize = 128;
    var lineBuffer = stackalloc byte[lineSize];
    var printPrefix = ffmpeg.AV_LOG_SKIP_REPEATED | ffmpeg.AV_LOG_PRINT_LEVEL;

    ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
    var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);

    if (line is null) return;

    line = line.ReplaceLineEndings();

    using (logger.BeginScope(nameof(ffmpeg)))
    {
        switch (level)
        {
            case ffmpeg.AV_LOG_PANIC:
                logger.LogCritical("{msg}", line);
                break;
            case ffmpeg.AV_LOG_FATAL:
                logger.LogCritical("{msg}", line);
                break;
            case ffmpeg.AV_LOG_ERROR:
                logger.LogError("{msg}", line);
                break;
            case ffmpeg.AV_LOG_WARNING:
                logger.LogWarning("{msg}", line);
                break;
            case ffmpeg.AV_LOG_INFO:
                logger.LogInformation("{msg}", line);
                break;
            case ffmpeg.AV_LOG_VERBOSE:
                logger.LogInformation("{msg}", line);
                break;
            case ffmpeg.AV_LOG_DEBUG:
                logger.LogDebug("{msg}", line);
                break;
            case ffmpeg.AV_LOG_TRACE:
                logger.LogTrace("{msg}", line);
                break;
            default:
                logger.LogWarning("[level {level}]{msg}", level, line);
                break;
        }
    }
}
