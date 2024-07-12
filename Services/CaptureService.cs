
using System.ComponentModel;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CameraScreenshotBotService.Extensions;
using CameraScreenshotBotService.Configs;
using Microsoft.Extensions.Options;

namespace CameraScreenshotBotService.Services;

public unsafe struct AVCodecContextWrapper(AVCodecContext* ctx)
{
    public AVCodecContext* Value { get; } = ctx;
}

public sealed class CaptureService
{
    private readonly ILogger<CaptureService> _logger;
    private readonly StreamOption _streamOption;

    private readonly unsafe AVCodecContext* _decoderCtx;
    private readonly unsafe AVCodecContext* _encoderCtx;

    private unsafe AVFormatContext* _inputFormatCtx;
    private readonly unsafe AVDictionary* _openOptions = null;

    private readonly unsafe AVFrame* _frame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVFrame* _webpOutputFrame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVPacket* _packet = ffmpeg.av_packet_alloc();

    private av_log_set_callback_callback _logCallback;

    private readonly int _streamIndex;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _codecCancellationToken;

    public byte[]? LastCapturedImage { get; private set; }
    public DateTime LastCaptureTime { get; private set; }

    public string StreamDecoderName { get; }
    public AVPixelFormat StreamPixelFormat { get; }
    public int StreamHeight { get; }
    public int StreamWidth { get; }

    public unsafe CaptureService(ILogger<CaptureService> logger, IOptions<StreamOption> option)
    {
        _logger = logger;
        _logCallback = FfmpegLogInvoke;
        _streamOption = option.Value;

        if (_streamOption.Url is null)
            throw new ArgumentNullException(nameof(option), "StreamOption.Url can not be null.");

        _codecCancellationToken = new(TimeSpan.FromMilliseconds(
            _streamOption.CodecTimeout));

        // 设置超时
        var openOptions = _openOptions;
        ffmpeg.av_dict_set(&openOptions, "timeout", _streamOption.ConnectTimeout.ToString(), 0);

        #region 初始化视频流解码器
        {
            OpenInput();

            ffmpeg.avformat_find_stream_info(_inputFormatCtx, null)
                .ThrowExceptionIfError();

            // 匹配解码器信息
            AVCodec* decoder = null;
            _streamIndex = ffmpeg
                .av_find_best_stream(_inputFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0)
                .ThrowExceptionIfError();

            // 创建解码器
            _decoderCtx = CreateCodecCtx(decoder, codec =>
            {
                ffmpeg.avcodec_parameters_to_context(codec.Value, _inputFormatCtx->streams[_streamIndex]->codecpar)
                    .ThrowExceptionIfError();

                codec.Value->thread_count = (int)_streamOption.CodecThreads;
                codec.Value->skip_frame = AVDiscard.AVDISCARD_NONKEY;
            });

            var pixFormat = _decoderCtx->pix_fmt switch
            {
                AVPixelFormat.AV_PIX_FMT_YUVJ420P => AVPixelFormat.AV_PIX_FMT_YUV420P,
                AVPixelFormat.AV_PIX_FMT_YUVJ422P => AVPixelFormat.AV_PIX_FMT_YUV422P,
                AVPixelFormat.AV_PIX_FMT_YUVJ444P => AVPixelFormat.AV_PIX_FMT_YUV444P,
                AVPixelFormat.AV_PIX_FMT_YUVJ440P => AVPixelFormat.AV_PIX_FMT_YUV440P,
                _ => _decoderCtx->pix_fmt,
            };

            // 设置输入流信息
            StreamDecoderName = ffmpeg.avcodec_get_name(decoder->id);
            StreamPixelFormat = pixFormat;
            StreamWidth = _decoderCtx->width;
            StreamHeight = _decoderCtx->height;

            CloseInput();
        }
        #endregion

        #region 初始化图片编码器
        {
            // ReSharper disable once StringLiteralTypo
            _encoderCtx = CreateCodecCtx("libwebp",
                config =>
            {
                config.Value->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                config.Value->gop_size = 1;
                config.Value->thread_count = (int)_streamOption.CodecThreads;
                config.Value->time_base = new() { den = 1, num = 1000 };
                config.Value->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                config.Value->width = StreamWidth;
                config.Value->height = StreamHeight;

                ffmpeg.av_opt_set(config.Value->priv_data, "preset", "photo", ffmpeg.AV_OPT_SEARCH_CHILDREN)
                    .ThrowExceptionIfError();
            });
        }
        #endregion

        // 设置日志
        if (option.Value.LogLevel is not null)
        {
            var level = option.Value.LogLevel.ToUpper() switch
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

            ffmpeg.av_log_set_level(level);
            ffmpeg.av_log_set_callback(_logCallback);
        }
    }

    /// <summary>
    /// ffmpeg 日志回调
    /// </summary>
    /// <param name="p0"></param>
    /// <param name="level"></param>
    /// <param name="format"></param>
    /// <param name="vl"></param>
    private unsafe void FfmpegLogInvoke(void* p0, int level, string format, byte* vl)
    {
        if (level > ffmpeg.av_log_get_level()) return;

        const int lineSize = 128;
        var lineBuffer = stackalloc byte[128];
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

    private unsafe void OpenInput()
    {
        _logger.LogInformation("Open Input {url}.", _streamOption.Url.AbsoluteUri);

        _inputFormatCtx = ffmpeg.avformat_alloc_context();
        var formatCtx = _inputFormatCtx;

        // 设置超时
        var openOptions = _openOptions;

        // 打开流
        ffmpeg.avformat_open_input(&formatCtx, _streamOption.Url.AbsoluteUri, null, &openOptions)
            .ThrowExceptionIfError();
    }

    private unsafe void CloseInput()
    {
        _logger.LogInformation("Close Input.");

        var formatCtx = _inputFormatCtx;
        ffmpeg.avformat_close_input(&formatCtx);
        ffmpeg.avformat_free_context(formatCtx);
    }

    // 会引发异常，待排查
    public unsafe void Dispose()
    {
        var formatCtx = _inputFormatCtx;
        ffmpeg.avformat_free_context(formatCtx);

        var pFrame = _frame;
        ffmpeg.av_frame_free(&pFrame);
        var pPacket = _packet;
        ffmpeg.av_packet_free(&pPacket);
        var pWebpOutputFrame = _webpOutputFrame;
        ffmpeg.av_frame_free(&pWebpOutputFrame);

        ffmpeg.avcodec_close(_decoderCtx);
        ffmpeg.avcodec_close(_encoderCtx);
    }

    private TimeSpan FfmpegTimeToTimeSpan(long value, AVRational timebase)
    {
        if (timebase.den == 0)
        {

            timebase.num = 1;
            timebase.den = ffmpeg.AV_TIME_BASE;
            _logger.LogWarning("Timebase den not set, reset to {num}/{den}",
                timebase.num, timebase.den);
        }

        var milliseconds = (double)(value * timebase.num) / ((long)timebase.den * 1000);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 解码下一关键帧（非线程安全）
    /// </summary>
    /// <param name="frame">帧指针，用完后需要使用 unref 释放</param>
    /// <returns></returns>
    public unsafe bool TryDecodeNextFrame(out AVFrame* frame)
    {
        while (true)
        {
            int decodeResult;

            // 遍历流找到 bestStream
            do
            {
                ffmpeg.av_packet_unref(_packet);
                var readResult = ffmpeg.av_read_frame(_inputFormatCtx, _packet);

                // 视频流已经结束
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    _logger.LogError("Receive EOF in stream, return.");
                    frame = null;
                    return false;
                }

                readResult.ThrowExceptionIfError();
            } while (_packet->stream_index != _streamIndex);

            // 取到了 stream 中的包
            _logger.LogDebug(
                "Find packet of stream {index}, pts:{pts}, dts:{dts}, {isContain} key frame",
                _packet->stream_index,
                FfmpegTimeToTimeSpan(_packet->pts, _decoderCtx->time_base).ToString("c"),
                FfmpegTimeToTimeSpan(_packet->dts, _decoderCtx->time_base).ToString("c"),
                (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 1 ? "contains" : "no");

            var sendResult = ffmpeg.avcodec_send_packet(_decoderCtx, _packet);
            if (sendResult == 0)
            {
                // 发送成功
                _logger.LogDebug("Packet sent success, try get decoded frame.");
                decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
            }
            else if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // 如果前一次发送失败，尝试清除堵塞的输出缓冲区重试发送
                while (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    FlushDecoderBufferUnsafe();

                    _logger.LogWarning("Codec buffer full. Try get a decoded frame(DROP) and send again.");
                    sendResult = ffmpeg.avcodec_send_packet(_decoderCtx, _packet);
                }

                _logger.LogDebug("Packet sent success after some retry, try get decoded frame.");
                decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
            }
            else
            {
                // 无法处理的发送失败
                _logger.LogError("Packet sent failed, message: {msg}", FFMpegExtension.av_strerror(sendResult));
                frame = null;
                return false;
            }

            if (decodeResult == 0 || decodeResult == ffmpeg.AVERROR_EOF)
            {
                if (decodeResult == ffmpeg.AVERROR_EOF)
                    _logger.LogWarning("Receive frame with EOF, maybe stream disconnected.");


                if (_frame->pts < 0)
                {
                    _logger.LogInformation("Decode video success. type {type}, pts {pts} < 0, drop.",
                        _frame->pict_type.ToString(),
                        FfmpegTimeToTimeSpan(_frame->pts, _decoderCtx->time_base).ToString("c"));
                    continue;
                }

                _logger.LogInformation("Decode video success. type {type}, pts {pts}.",
                    _frame->pict_type.ToString(),
                    FfmpegTimeToTimeSpan(_frame->pts, _decoderCtx->time_base).ToString("c"));

                break;
            }

            if (decodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;

            frame = null;
            return false;
        }

        ffmpeg.av_packet_unref(_packet);

        if (_decoderCtx->hw_device_ctx != null)
        {
            _logger.LogError("Hardware decode is unsupported, skip.");
            // 硬件解码数据转换
            // ffmpeg.av_hwframe_transfer_data(frame, _frame, 0).ThrowExceptionIfError();
        }

        frame = _frame;
        return true;
    }

    #region 创建编码器
    /// <summary>
    /// 创建编解码器
    /// </summary>
    /// <param name="codecId"></param>
    /// <param name="config">是否有效未知</param>
    /// <param name="pixelFormat"></param>
    /// <returns></returns>
    private unsafe AVCodecContext* CreateCodecCtx(AVCodecID codecId, Action<AVCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder(codecId);

        return CreateCodecCtx(codec, ctx =>
        {
            ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(ctx);
        });
    }

    private unsafe AVCodecContext* CreateCodecCtx(string codecName, Action<AVCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);

        return CreateCodecCtx(codec, ctx =>
        {
            ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(ctx);
        });
    }

    private unsafe AVCodecContext* CreateCodecCtx(AVCodec* codec, Action<AVCodecContextWrapper>? config = null)
    {
        // 编解码器
        var ctx = ffmpeg.avcodec_alloc_context3(codec);

        ctx->time_base = new() { num = 1, den = 25 }; // 设置时间基准
        ctx->framerate = new() { num = 25, den = 1 };

        config?.Invoke(new(ctx));

        ffmpeg.avcodec_open2(ctx, codec, null).ThrowExceptionIfError();

        return ctx;
    }
    #endregion

    public async Task FlushDecoderBufferAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            await Task.Run(FlushDecoderBufferUnsafe, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 丢弃解码器结果中所有的帧
    /// </summary>
    private unsafe void FlushDecoderBufferUnsafe()
    {
        var cnt = 0;
        int result;
        do
        {
            result = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
            cnt++;
        } while (result != ffmpeg.AVERROR(ffmpeg.EAGAIN));

        ffmpeg.av_frame_unref(_frame);
        _logger.LogInformation("Drop all {cnt} frames in decoder buffer.", cnt);
    }

    /// <summary>
    /// 创建转换上下文
    /// </summary>
    /// <param name="targetCodecCtx"></param>
    /// <param name="targetWidth"></param>
    /// <param name="targetHeight"></param>
    /// <returns></returns>
    private unsafe SwsContext* CreateSwsContext(AVCodecContext* targetCodecCtx, int targetWidth, int targetHeight)
        => ffmpeg.sws_getContext(StreamWidth, StreamHeight, StreamPixelFormat,
            targetWidth, targetHeight, targetCodecCtx->pix_fmt,
            ffmpeg.SWS_BICUBIC, null, null, null);

    public async Task<(bool, byte[]?)> CaptureImageAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            var captureTimeSpan = DateTime.Now - LastCaptureTime;
            if (LastCapturedImage != null && captureTimeSpan <= TimeSpan.FromSeconds(20))
            {
                _logger.LogInformation("Return image cached {time} ago.", captureTimeSpan);
                return (true, LastCapturedImage);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        var result = await Task.Run(async () =>
        {
            await _semaphore.WaitAsync(cancellationToken);

            _logger.LogInformation("Cache image expired, capture new.");
            try
            {

                var success = TryCaptureWebpImageUnsafe(out var image);

                if (success)
                {
                    LastCapturedImage = image;
                    LastCaptureTime = DateTime.Now;
                }

                return (success, image);
            }
            finally
            {
                _semaphore.Release();
            }
        }, cancellationToken);
        return result;
    }

    public unsafe bool TryCaptureWebpImageUnsafe(out byte[]? image)
    {
        OpenInput();

        ffmpeg.av_seek_frame(_inputFormatCtx, _streamIndex, 0, ffmpeg.AVSEEK_FLAG_ANY);

        var result = false;
        image = null;

        if (!TryDecodeNextFrame(out var frame))
        {
            if (frame != null)
            {
                ffmpeg.av_frame_unref(frame);
            }

            return false;
        }

        CloseInput();

        var outFrame = frame;
        var encoderCtx = _encoderCtx;

        if (outFrame->format != (int)encoderCtx->pix_fmt)
        {
            outFrame = _webpOutputFrame;
            var swsCtx = CreateSwsContext(encoderCtx, StreamWidth, StreamHeight);

            outFrame->width = StreamWidth;
            outFrame->height = StreamHeight;
            outFrame->format = (int)encoderCtx->pix_fmt;

            // 分配内存
            //ffmpeg.av_frame_get_buffer(outFrame, 32)
            //    .ThrowExceptionIfError();

            // 复制 AVFrame 属性数据
            ffmpeg.av_frame_copy_props(outFrame, frame);
            // 转换
            // ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0,
            //outFrame->height, outFrame->data, outFrame->linesize)
            //    .ThrowExceptionIfError();

            ffmpeg.sws_scale_frame(swsCtx, outFrame, frame)
                .ThrowExceptionIfError();

            ffmpeg.av_frame_unref(frame);
        }

        // 开始编码
        ffmpeg.avcodec_send_frame(encoderCtx, outFrame)
            .ThrowExceptionIfError();

        using var memStream = new MemoryStream();

        // 第一个包
        var ret = ffmpeg.avcodec_receive_packet(encoderCtx, _packet);

        var ct = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_streamOption.CodecTimeout));

        while (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Encode message {msg}, retry.", FFMpegExtension.av_strerror(ret));
            ret = ffmpeg.avcodec_receive_packet(encoderCtx, _packet);
        }

        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            // 依旧不可用
            _logger.LogError("Encode image failed! {msg}", FFMpegExtension.av_strerror(ret));
            result = false;
        }
        else if (ret >= 0)
        {
            //正常接收到数据
            _logger.LogInformation("Save packet with size {s} to buffer.", _packet->size);
            WriteToStream(memStream, _packet);
            result = true;

            while (ret != ffmpeg.AVERROR_EOF)
            {
                ret = ffmpeg.avcodec_receive_packet(encoderCtx, _packet);
                if (_packet->size != 0)
                {
                    _logger.LogInformation("Continue received packet, save {s} to buffer.", _packet->size);
                    WriteToStream(memStream, _packet);
                }
                else
                {
                    _logger.LogInformation("Received empty packet, no data to save.");
                    break;
                }
            }
        }
        else if (ret == ffmpeg.AVERROR_EOF)
        {
            if (_packet->size != 0)
            {
                _logger.LogInformation("Received EOF, save {s} to buffer.", _packet->size);
                WriteToStream(memStream, _packet);
            }
            else
            {
                _logger.LogInformation("Received EOF, no data to save.");
            }
        }

        // 释放资源
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_frame_unref(outFrame);

        image = memStream.ToArray();
        memStream.Close();

        return result;
    }

    private static unsafe void WriteToStream(Stream stream, AVPacket* packet)
    {
        var buffer = new byte[packet->size];
        Marshal.Copy((IntPtr)packet->data, buffer, 0, packet->size);
        stream.Write(buffer, 0, packet->size);
    }
}