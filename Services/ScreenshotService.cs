
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

public sealed class ScreenshotService
{
    private readonly ILogger<ScreenshotService> _logger;
    private readonly StreamOption _streamOption;

    private readonly unsafe AVCodecContext* _decoderCtx;
    private readonly unsafe AVCodecContext* _pngEncoderCtx;
    private readonly unsafe AVCodecContext* _webpEncoderCtx;
    private readonly unsafe SwsContext* _Yuv420pToRgb24ConverterCtx;

    private unsafe AVFormatContext* _inputFormatCtx;
    private readonly unsafe AVDictionary* _openOptions = null;

    private readonly unsafe AVFrame* _frame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVFrame* _pngOutputFrame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVFrame* _webpOutputFrame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVPacket* _packet = ffmpeg.av_packet_alloc();

    private av_log_set_callback_callback _logCallback;

    private readonly int _streamIndex;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public byte[]? LastCapturedImage { get; private set; }
    public DateTime LastCaptureTime { get; private set; }

    public string StreamDecoderName { get; }
    public AVPixelFormat StreamPixelFormat { get; }
    public int StreamHeight { get; }
    public int StreamWidth { get; }

    public unsafe ScreenshotService(ILogger<ScreenshotService> logger, IOptions<StreamOption> option)
    {
        _logger = logger;
        _logCallback = FfmpegLogInvoke;
        _streamOption = option.Value
            ?? throw new ArgumentNullException(nameof(option));
        if (_streamOption.Url is null)
            throw new ArgumentNullException(nameof(option), "StreamOption.Url can not be null.");

        // 设置超时
        var openOptions = _openOptions;
        ffmpeg.av_dict_set(&openOptions, "timeout", _streamOption.ConnectTimeout.ToString(), 0);

        #region 初始化视频流解码器
        {
            OpenInput();

            ffmpeg.avformat_find_stream_info(_inputFormatCtx, null)
                .ThrowExceptionIfError();

            AVCodec* decoder = null;
            _streamIndex = ffmpeg
                .av_find_best_stream(_inputFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0)
                .ThrowExceptionIfError();

            _decoderCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(_decoderCtx, _inputFormatCtx->streams[_streamIndex]->codecpar)
                .ThrowExceptionIfError();
            _decoderCtx->thread_count = (int)_streamOption.DecodeThreads;
            ffmpeg.avcodec_open2(_decoderCtx, decoder, null).ThrowExceptionIfError();

            var defaultPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;

            if (_decoderCtx->codec != null && _decoderCtx->codec->pix_fmts != null)
            {
                defaultPixelFormat = *_decoderCtx->codec->pix_fmts;
            }

            var pixFormat = _decoderCtx->pix_fmt switch
            {
                AVPixelFormat.AV_PIX_FMT_YUVJ420P => AVPixelFormat.AV_PIX_FMT_YUV420P,
                AVPixelFormat.AV_PIX_FMT_YUVJ422P => AVPixelFormat.AV_PIX_FMT_YUV422P,
                AVPixelFormat.AV_PIX_FMT_YUVJ444P => AVPixelFormat.AV_PIX_FMT_YUV444P,
                AVPixelFormat.AV_PIX_FMT_YUVJ440P => AVPixelFormat.AV_PIX_FMT_YUV440P,
                _ => defaultPixelFormat,
            };

            StreamDecoderName = ffmpeg.avcodec_get_name(decoder->id);
            StreamPixelFormat = pixFormat;
            StreamWidth = _decoderCtx->width;
            StreamHeight = _decoderCtx->height;

            CloseInput();
        }
        #endregion

        #region 初始化图片编码器

        {
            _pngEncoderCtx = CreateEncoderCtx(AVCodecID.AV_CODEC_ID_PNG);

            _webpEncoderCtx = CreateEncoderCtx("libwebp",
                config =>
            {
                config.Value->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            });
        }
        #endregion

        _Yuv420pToRgb24ConverterCtx = CreateSwsContext(_pngEncoderCtx, StreamWidth, StreamHeight);

        // 设置日志
        if (option.Value.LogLevel is not null)
        {
            ffmpeg.av_log_set_level(option.Value.LogLevel.ToUpper() switch
            {
                "DEBUG" => ffmpeg.AV_LOG_TRACE,
                "WARNING" => ffmpeg.AV_LOG_WARNING,
                "ERROR" => ffmpeg.AV_LOG_ERROR,
                _ => ffmpeg.AV_LOG_INFO,
            });
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

        const int lineSize = 1024;
        var lineBuffer = stackalloc byte[lineSize];
        var printPrefix = 1;

        ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
        var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);

        using (_logger.BeginScope(nameof(ffmpeg)))
        {
            switch (level)
            {
                case ffmpeg.AV_LOG_PANIC:
                    _logger.LogCritical("[panic]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_FATAL:
                    _logger.LogCritical("[fatal]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_ERROR:
                    _logger.LogError("[error]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_WARNING:
                    _logger.LogWarning("[warning]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_INFO:
                    _logger.LogInformation("[info]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_VERBOSE:
                    _logger.LogInformation("[verbose]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_DEBUG:
                    _logger.LogInformation("[debug]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_TRACE:
                    _logger.LogInformation("[trace]{msg}", line);
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

        // 初始化 ffmpeg 输入
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
        var pFrame = _frame;
        ffmpeg.av_frame_free(&pFrame);

        var pPacket = _packet;
        ffmpeg.av_packet_free(&pPacket);

        ffmpeg.avcodec_close(_decoderCtx);

        ffmpeg.sws_freeContext(_Yuv420pToRgb24ConverterCtx);
    }

    /// <summary>
    /// 解码下一关键帧（非线程安全）
    /// </summary>
    /// <param name="frame">帧指针，用完后需要使用 unref 释放</param>
    /// <returns></returns>
    public unsafe bool TryDecodeNextKeyFrame(out AVFrame* frame)
    {
        var times = _streamOption.KeyframeSearchMax;

        for (var cnt = 0; cnt < times; cnt++)
        {
            int decodeResult;
            do
            {
                // 尝试解码一个包
                try
                {
                    // 遍历流找到 bestStream
                    do
                    {
                        var result = ffmpeg.av_read_frame(_inputFormatCtx, _packet);

                        // 视频流已经结束
                        if (result == ffmpeg.AVERROR_EOF)
                        {
                            frame = null;
                            return false;
                        }
                        result.ThrowExceptionIfError();
                    } while (_packet->stream_index != _streamIndex);

                    // 取到了 stream 中的包
                    _logger.LogDebug("Find stream {index} packet. pts {pts}, dts {dts}, timebase {den}/{num}",
                        _packet->stream_index, _packet->pts, _packet->dts, _packet->time_base.den, _packet->time_base.num);

                    // 发送包到解码器
                    ffmpeg.avcodec_send_packet(_decoderCtx, _packet)
                        .ThrowExceptionIfError();
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }

                ffmpeg.av_frame_unref(_frame);
                decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
                // -11 资源暂不可用时候重试
            } while (decodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            // 校验解码结果
            decodeResult.ThrowExceptionIfError();

            var pictType = _frame->pict_type;

            // 非关键帧，跳过
            if (pictType != AVPictureType.AV_PICTURE_TYPE_I)
                continue;

            // 关键帧，退出循环
            _logger.LogInformation("Drop non-I frame: {cnt}, decode frame type {type}.",
                cnt, pictType.ToString());
            break;
        }

        if (_decoderCtx->hw_device_ctx != null)
        {
            _logger.LogError("Hardware decode is unsupported, skip.");
            // 硬件解码数据转换
            // ffmpeg.av_hwframe_transfer_data(frame, _frame, 0).ThrowExceptionIfError();
        }

        frame = _frame;
        return true;
    }

    /// <summary>
    /// 创建编解码器
    /// </summary>
    /// <param name="codecId"></param>
    /// <param name="config">是否有效未知</param>
    /// <param name="pixelFormat"></param>
    /// <returns></returns>
    private unsafe AVCodecContext* CreateEncoderCtx(AVCodecID codecId, Action<AVCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder(codecId);

        return CreateEncoderCtx(codec, ctx =>
        {
            ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(ctx);
        });
    }

    private unsafe AVCodecContext* CreateEncoderCtx(string codecName, Action<AVCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);

        return CreateEncoderCtx(codec, ctx =>
        {
            ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(ctx);
        });
    }

    private unsafe AVCodecContext* CreateEncoderCtx(AVCodec* codec, Action<AVCodecContextWrapper>? config = null)
    {
        // 编解码器
        var ctx = ffmpeg.avcodec_alloc_context3(codec);

        ctx->width = StreamWidth;
        ctx->height = StreamHeight;
        ctx->time_base = new() { num = 1, den = 25 }; // 设置时间基准
        ctx->framerate = new() { num = 25, den = 1 };

        config?.Invoke(new(ctx));

        ffmpeg.avcodec_open2(ctx, codec, null).ThrowExceptionIfError();

        return ctx;
    }

    /// <summary>
    /// 创建转换上下文
    /// </summary>
    /// <param name="targetCodecCtx"></param>
    /// <param name="targetWidth"></param>
    /// <param name="targetHeight"></param>
    /// <returns></returns>
    private unsafe SwsContext* CreateSwsContext(AVCodecContext* targetCodecCtx, int targetWidth, int targetHeight)
    {

        var ctx = ffmpeg.sws_getContext(StreamWidth, StreamHeight, StreamPixelFormat,
            targetWidth, targetHeight, targetCodecCtx->pix_fmt,
            ffmpeg.SWS_BICUBIC, null, null, null);
        return ctx;
    }

    public async Task<(bool, byte[]?)> CapturePngImageAsync(CancellationToken cancellationToken = default)
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

                //success = TryCaptureWebpImageUnsafe(out image);

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

        var result = false;
        image = null;

        if (!TryDecodeNextKeyFrame(out var frame))
        {
            if (frame != null)
            {
                ffmpeg.av_frame_unref(frame);
            }

            return false;
        }

        CloseInput();

        var outFrame = frame;
        var encoder = _webpEncoderCtx;

        if (outFrame->format != (int)encoder->pix_fmt)
        {
            outFrame = _webpOutputFrame;
            var swsCtx = CreateSwsContext(encoder, StreamWidth, StreamHeight);

            outFrame->width = StreamWidth;
            outFrame->height = StreamHeight;
            outFrame->format = (int)encoder->pix_fmt;

            // 分配内存
            ffmpeg.av_frame_get_buffer(outFrame, 32)
                .ThrowExceptionIfError();

            // 将 AVFrame 数据复制到 pngFrame
            ffmpeg.av_frame_copy_props(outFrame, frame);
            // 转换
            ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0,
           outFrame->height, outFrame->data, outFrame->linesize)
               .ThrowExceptionIfError();

            ffmpeg.av_frame_unref(frame);
        }

        // 开始编码
        ffmpeg.avcodec_send_frame(encoder, outFrame)
            .ThrowExceptionIfError();

        using var memStream = new MemoryStream();

        // 第一个包
        var ret = ffmpeg.avcodec_receive_packet(encoder, _packet);

        var ct = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_streamOption.CodecTimeout));

        while (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Encode message {msg}, retry.", FFMpegExtension.av_strerror(ret));
            ret = ffmpeg.avcodec_receive_packet(encoder, _packet);
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
                ret = ffmpeg.avcodec_receive_packet(encoder, _packet);
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