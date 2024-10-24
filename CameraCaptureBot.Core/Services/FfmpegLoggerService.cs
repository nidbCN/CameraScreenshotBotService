using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CameraCaptureBot.Core.Configs;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Options;

namespace CameraCaptureBot.Core.Services
{
    public class FfmpegLoggerService
    {
        private readonly av_log_set_callback_callback _logCallback;
        private readonly ILogger<FfmpegLoggerService> _logger;
        private readonly StreamOption _streamOption;
        public FfmpegLoggerService(ILogger<FfmpegLoggerService> logger, IOptions<StreamOption> streamOptions)
        {
            _logger = logger;
            _streamOption = streamOptions.Value;

            // 设置日志
            if (_streamOption?.LogLevel is null) return;

            var level = _streamOption.LogLevel.ToUpper() switch
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
                _logCallback = FfMpegLogInvoke;
                ffmpeg.av_log_set_level(level);
                ffmpeg.av_log_set_callback(_logCallback);
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

            using (_logger.BeginScope(nameof(ffmpeg)))
            {
                switch (level)
                {
                    case ffmpeg.AV_LOG_PANIC:
                        _logger.LogCritical("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_FATAL:
                        _logger.LogCritical("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_ERROR:
                        _logger.LogError("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_WARNING:
                        _logger.LogWarning("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_INFO:
                        _logger.LogInformation("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_VERBOSE:
                        _logger.LogInformation("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_DEBUG:
                        _logger.LogDebug("{msg}", line);
                        break;
                    case ffmpeg.AV_LOG_TRACE:
                        _logger.LogTrace("{msg}", line);
                        break;
                    default:
                        _logger.LogWarning("[level {level}]{msg}", level, line);
                        break;
                }
            }
        }

    }

}
