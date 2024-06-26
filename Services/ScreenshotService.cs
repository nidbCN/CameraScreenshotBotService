﻿
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CameraScreenshotBotService.Extensions;
using CameraScreenshotBotService.Configs;
using Microsoft.Extensions.Options;

namespace CameraScreenshotBotService.Services;

public sealed unsafe class ScreenshotService
{
    private readonly ILogger<ScreenshotService> _logger;
    private readonly StreamOption _streamOption;

    private readonly SwsContext* _pixConverterCtx;

    private readonly AVCodecContext* _decoderCtx;
    private readonly AVCodecContext* _encoderCtx;

    private AVFormatContext* _inputFormatCtx;
    private readonly AVDictionary* _openOptions;

    private readonly AVFrame* _inputFrame;
    private readonly AVPacket* _pPacket;
    private AVFrame* _receivedFrame;

    private readonly int _streamIndex;

    public ScreenshotService(ILogger<ScreenshotService> logger, IOptions<StreamOption> option)
    {
        _logger = logger;
        _streamOption = option?.Value
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
            var pixFormat = _decoderCtx->pix_fmt switch
            {
                AVPixelFormat.AV_PIX_FMT_YUVJ420P => AVPixelFormat.AV_PIX_FMT_YUV420P,
                AVPixelFormat.AV_PIX_FMT_YUVJ422P => AVPixelFormat.AV_PIX_FMT_YUV422P,
                AVPixelFormat.AV_PIX_FMT_YUVJ444P => AVPixelFormat.AV_PIX_FMT_YUV444P,
                AVPixelFormat.AV_PIX_FMT_YUVJ440P => AVPixelFormat.AV_PIX_FMT_YUV440P,
                _ => *_decoderCtx->codec->pix_fmts,
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
            _encoderCtx->time_base = new AVRational { num = 1, den = 25 }; // 设置时间基准
            _encoderCtx->framerate = new AVRational { num = 25, den = 1 };
            _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB24;

            // 打开编码器
            ffmpeg.avcodec_open2(_encoderCtx, encoder, null).ThrowExceptionIfError();
        }

        {
            // 初始化 SwsContext
            _pixConverterCtx = ffmpeg.sws_getContext(StreamWidth, StreamHeight,StreamPixelFormat,
                              StreamWidth, StreamHeight, _encoderCtx->pix_fmt,
                              ffmpeg.SWS_BICUBIC, null, null, null);
        }

        _pPacket = ffmpeg.av_packet_alloc();
        _inputFrame = ffmpeg.av_frame_alloc();

        // 设置日志
        {
            //ffmpeg.av_log_set_level(config["Camera:Log"]?.ToUpper() switch
            //{
            //    "DEBUG" => ffmpeg.AV_LOG_DEBUG,
            //    "WARNING" => ffmpeg.AV_LOG_WARNING,
            //    "ERROR" => ffmpeg.AV_LOG_ERROR,
            //    _ => ffmpeg.AV_LOG_INFO
            //});

            //// do not convert to local function
            //av_log_set_callback_callback logCallback = (p0, level, format, vl) =>
            //{
            //    if (level > ffmpeg.av_log_get_level()) return;

            //    var lineSize = 1024;
            //    var lineBuffer = stackalloc byte[lineSize];
            //    var printPrefix = 1;
            //    ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
            //    var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);

            //    switch (level)
            //    {
            //        case ffmpeg.AV_LOG_DEBUG:
            //            _logger.LogDebug(line);
            //            break;
            //        case ffmpeg.AV_LOG_WARNING:
            //            _logger.LogWarning(line);
            //            break;
            //        case ffmpeg.AV_LOG_INFO:
            //            _logger.LogInformation(line);
            //            break;
            //        default:
            //            _logger.LogInformation(line);
            //            break;
            //    }
            //};

            // ffmpeg.av_log_set_callback(logCallback);
        }
    }

    public void OpenInput()
    {
        _logger.LogInformation("Open Input.");

        // 初始化 ffmpeg 输入
        _inputFormatCtx = ffmpeg.avformat_alloc_context();
        _receivedFrame = ffmpeg.av_frame_alloc();
        var fotmatCtx = _inputFormatCtx;

        // 设置超时
        var openOptions = _openOptions;

        // 打开流
        ffmpeg.avformat_open_input(&fotmatCtx, _streamOption.Url.AbsoluteUri, null, &openOptions)
            .ThrowExceptionIfError();
    }

    public void CloseInput()
    {
        _logger.LogInformation("Close Input.");

        var inputFormatCtx = _inputFormatCtx;
        ffmpeg.avformat_close_input(&inputFormatCtx);

        ffmpeg.av_frame_unref(_inputFrame);
        ffmpeg.av_frame_unref(_receivedFrame);
    }

    public string StreamCodecName { get; }
    public AVPixelFormat StreamPixelFormat { get; }
    public int StreamHeight { get; }
    public int StreamWidth { get; }

    public void Dispose()
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

    public bool TryDecodeNextKeyFrame(out AVFrame* frame)
    {
        ffmpeg.av_frame_unref(_inputFrame);
        ffmpeg.av_frame_unref(_receivedFrame);
        int ret = 0;

        for (int cnt = 0; cnt < 120; cnt++)
        {
            do
            {
                try
                {
                    do
                    {
                        // 遍历流找到 bestStream
                        ffmpeg.av_packet_unref(_pPacket);
                        ret = ffmpeg.av_read_frame(_inputFormatCtx, _pPacket);

                        // 视频流已经结束
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            frame = _inputFrame;
                            return false;
                        }

                        ret.ThrowExceptionIfError();
                    } while (_pPacket->stream_index != _streamIndex);

                    ffmpeg.avcodec_send_packet(_decoderCtx, _pPacket).ThrowExceptionIfError();
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                ret = ffmpeg.avcodec_receive_frame(_decoderCtx, _inputFrame);

                // -11 资源暂不可用时候重试
            } while (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            _logger.LogInformation("Frame counter: {cnt}", ++cnt);

            // 关键帧
            if (_inputFrame->pict_type == AVPictureType.AV_PICTURE_TYPE_I)
            {
                break;
            }
        }

        ret.ThrowExceptionIfError();

        if (_decoderCtx->hw_device_ctx != null)
        {
            ffmpeg.av_hwframe_transfer_data(_receivedFrame, _inputFrame, 0).ThrowExceptionIfError();
            frame = _receivedFrame;
        }
        else
            frame = _inputFrame;

        return true;
    }

    public bool TryCapturePngImage(out byte[]? image)
    {
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

        int ret = ffmpeg.avcodec_receive_packet(_encoderCtx, _pPacket);
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
        return result;
    }

    private static void WriteToStream(Stream stream, AVPacket* packet)
    {
        var buffer = new byte[packet->size];
        Marshal.Copy((IntPtr)packet->data, buffer, 0, packet->size);
        stream.Write(buffer, 0, packet->size);
    }

    public IReadOnlyDictionary<string, string?>? GetContextInfo()
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
