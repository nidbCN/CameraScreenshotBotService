
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CameraScreenshotBotService.Extensions;
using CameraScreenshotBotService.Configs;
using Microsoft.Extensions.Options;
using static System.Net.Mime.MediaTypeNames;
using vectors = FFmpeg.AutoGen.Abstractions.vectors;

namespace CameraScreenshotBotService.Services;

public sealed class ScreenshotService
{
    private readonly ILogger<ScreenshotService> _logger;
    private readonly StreamOption _streamOption;

    private readonly unsafe SwsContext* _pixConverterCtx;
    private readonly unsafe AVCodecContext* _decoderCtx;
    private readonly unsafe AVCodecContext* _encoderCtx;

    private unsafe AVFormatContext* _inputFormatCtx;
    private readonly unsafe AVDictionary* _openOptions;

    private readonly unsafe AVFrame* _inputFrame;
    private readonly unsafe AVPacket* _pPacket;
    private unsafe AVFrame* _receivedFrame;

    private av_log_set_callback_callback _logCallback;

    private readonly int _streamIndex;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsDecoding { get; private set; }
    public byte[]? LastCapturedImage { get; private set; }
    public DateTime LastCaptureTime { get; private set; }

    public string StreamCodecName { get; }
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
        ffmpeg.av_dict_set(&openOptions, "timeout", "3000", 0);

        // 初始化 ffmpeg 输入
        OpenInput();

        ffmpeg.avformat_find_stream_info(_inputFormatCtx, null).ThrowExceptionIfError();

        // 初始化解码器
        {
            AVCodec* decoder = null;
            _streamIndex = ffmpeg
                .av_find_best_stream(_inputFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0)
                .ThrowExceptionIfError();

            _decoderCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(_decoderCtx, _inputFormatCtx->streams[_streamIndex]->codecpar)
                .ThrowExceptionIfError();
            _decoderCtx->thread_count = (int)_streamOption.DecodeThreads;
            ffmpeg.avcodec_open2(_decoderCtx, decoder, null).ThrowExceptionIfError();

            StreamCodecName = ffmpeg.avcodec_get_name(decoder->id);

            var defaultPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
            if (_decoderCtx->codec is not null)
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
            StreamPixelFormat = pixFormat;
            StreamWidth = _decoderCtx->width;
            StreamHeight = _decoderCtx->height;
        }


        CloseInput();

        // 初始化 PNG 编码器
        {
            var encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_PNG);
            _encoderCtx = ffmpeg.avcodec_alloc_context3(encoder);

            // 设置编码器参数
            _encoderCtx->width = StreamWidth;
            _encoderCtx->height = StreamHeight;
            _encoderCtx->time_base = new() { num = 1, den = 25 }; // 设置时间基准
            _encoderCtx->framerate = new() { num = 25, den = 1 };
            _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB24;

            // 打开编码器
            ffmpeg.avcodec_open2(_encoderCtx, encoder, null).ThrowExceptionIfError();
        }

        {
            // 初始化 SwsContext
            _pixConverterCtx = ffmpeg.sws_getContext(StreamWidth, StreamHeight, StreamPixelFormat,
                StreamWidth, StreamHeight, _encoderCtx->pix_fmt,
                ffmpeg.SWS_BICUBIC, null, null, null);
        }

        _pPacket = ffmpeg.av_packet_alloc();
        _inputFrame = ffmpeg.av_frame_alloc();

        // 设置日志

        if (option.Value.LogLevel is not null)
        {
            ffmpeg.av_log_set_level(option.Value.LogLevel.ToUpper() switch
            {
                "DEBUG" => ffmpeg.AV_LOG_DEBUG,
                "WARNING" => ffmpeg.AV_LOG_WARNING,
                "ERROR" => ffmpeg.AV_LOG_ERROR,
                _ => ffmpeg.AV_LOG_INFO,
            });
            ffmpeg.av_log_set_callback(_logCallback);
        }
    }

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
                    _logger.LogDebug("[verbose]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_DEBUG:
                    _logger.LogDebug("[debug]{msg}", line);
                    break;
                case ffmpeg.AV_LOG_TRACE:
                    _logger.LogTrace("[trace]{msg}", line);
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
        _receivedFrame = ffmpeg.av_frame_alloc();
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

        var inputFormatCtx = _inputFormatCtx;
        ffmpeg.avformat_close_input(&inputFormatCtx);

        ffmpeg.av_frame_unref(_inputFrame);
        ffmpeg.av_frame_unref(_receivedFrame);
    }

    // 会引发异常，待排查
    public unsafe void Dispose()
    {
        var pFrame = _inputFrame;
        ffmpeg.av_frame_free(&pFrame);

        var pPacket = _pPacket;
        ffmpeg.av_packet_free(&pPacket);

        ffmpeg.avcodec_close(_decoderCtx);
        var pFormatContext = _inputFormatCtx;
        ffmpeg.avformat_close_input(&pFormatContext);

        ffmpeg.sws_freeContext(_pixConverterCtx);
    }

    public unsafe bool TryDecodeNextKeyFrame(out AVFrame* frame)
    {
        ffmpeg.av_frame_unref(_inputFrame);
        ffmpeg.av_frame_unref(_receivedFrame);
        var ret = 0;

        for (var cnt = 0; cnt < 60; cnt++)
        {
            do
            {
                // 尝试解码一个包
                try
                {
                    // 遍历流找到 bestStream
                    do
                    {
                        ffmpeg.av_packet_unref(_pPacket);
                        ret = ffmpeg.av_read_frame(_inputFormatCtx, _pPacket);

                        // 视频流已经结束
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            frame = null;
                            return false;
                        }

                        ret.ThrowExceptionIfError();
                    } while (_pPacket->stream_index != _streamIndex);

                    _logger.LogDebug("Find stream {index} packet. pts {pts}, dts {dts}, timebase {den}/{num}",
                        _pPacket->stream_index, _pPacket->pts, _pPacket->dts, _pPacket->time_base.den, _pPacket->time_base.num);

                    // 发送包到解码器
                    ffmpeg.avcodec_send_packet(_decoderCtx, _pPacket).ThrowExceptionIfError();
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                ret = ffmpeg.avcodec_receive_frame(_decoderCtx, _inputFrame);
                // -11 资源暂不可用时候重试
            } while (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            // 非关键帧，跳过
            if (_inputFrame->pict_type != AVPictureType.AV_PICTURE_TYPE_I) continue;

            // 关键帧，退出循环
            _logger.LogInformation("Drop non-I frame: {cnt}, decode frame type {type}.",
                cnt, _inputFrame->pict_type.ToString());
            break;
        }

        ret.ThrowExceptionIfError();

        // 硬件解码数据转换
        if (_decoderCtx->hw_device_ctx != null)
        {
            ffmpeg.av_hwframe_transfer_data(_receivedFrame, _inputFrame, 0).ThrowExceptionIfError();
            frame = _receivedFrame;
        }
        else
        {
            frame = _inputFrame;
        }

        return true;
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
                var success = TryCapturePngImageUnsafe(out var image);

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

    public unsafe bool TryCapturePngImageUnsafe(out byte[]? image)
    {
        OpenInput();

        var result = false;
        image = null;

        if (!TryDecodeNextKeyFrame(out var frame))
            return false;

        var width = frame->width;
        var height = frame->height;
        var sourcePixelFormat = StreamPixelFormat;
        var destPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

        // 创建 AVFrame 用于编码
        var imageFrame = ffmpeg.av_frame_alloc();
        imageFrame->width = frame->width;
        imageFrame->height = frame->height;
        imageFrame->format = (int)destPixelFormat;
        ffmpeg.av_frame_get_buffer(imageFrame, 32); // 分配内存

        // 将 AVFrame 数据复制到 pngFrame
        ffmpeg.av_frame_copy(imageFrame, frame);
        ffmpeg.av_frame_copy_props(imageFrame, frame);

        // 转换
        ffmpeg.sws_scale(_pixConverterCtx, frame->data, frame->linesize, 0,
                  height, imageFrame->data, imageFrame->linesize);

        // 开始编码
        ffmpeg.avcodec_send_frame(_encoderCtx, imageFrame);

        using var memStream = new MemoryStream();

        var ret = ffmpeg.avcodec_receive_packet(_encoderCtx, _pPacket);
        if (ret < 0)
        {
            // 首次编码出现 EOF 等，不正常
            _logger.LogError("Encode image failed! {msg}", FFMpegExtension.av_strerror(ret));
            result = false;
        }
        else
        {
            _logger.LogInformation("Save packet with size {s} to buffer.", _pPacket->size);
            WriteToStream(memStream, _pPacket);
            ffmpeg.av_packet_unref(_pPacket);

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_packet(_encoderCtx, _pPacket);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    // 没有更多数据，正常结束
                    result = true;
                }
                else if (ret < 0)
                {
                    _logger.LogWarning("Encode failed! {msg}", FFMpegExtension.av_strerror(ret));
                    result = false;
                }
                else
                {
                    _logger.LogInformation("Save packet with size {s} to buffer.", _pPacket->size);
                    WriteToStream(memStream, _pPacket);

                }

                ffmpeg.av_packet_unref(_pPacket);
            }
        }

        // 释放资源
        ffmpeg.av_frame_free(&imageFrame);

        // result
        image = memStream.ToArray();
        memStream.Close();

        CloseInput();
        return result;
    }

    private static unsafe void WriteToStream(Stream stream, AVPacket* packet)
    {
        var buffer = new byte[packet->size];
        Marshal.Copy((IntPtr)packet->data, buffer, 0, packet->size);
        stream.Write(buffer, 0, packet->size);
    }

    public unsafe IReadOnlyDictionary<string, string?> GetContextInfo()
    {
        AVDictionaryEntry* tag = null;

        var result = new Dictionary<string, string?>();

        while ((tag = ffmpeg.av_dict_get(_inputFormatCtx->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
            var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);

            if (key is not null)
                result.Add(key, value);
        }

        return result;
    }
}
