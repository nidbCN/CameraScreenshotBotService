
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;
using CameraScreenshotBotService.Extensions;

namespace CameraScreenshotBotService.Services;

public sealed unsafe class ScreenshotService
{
    private readonly ILogger<ScreenshotService> _logger;
    private readonly IConfiguration _config;

    private readonly AVCodecContext* _pCodecContext;
    private readonly AVFormatContext* _pFormatContext;
    private readonly AVFrame* _pFrame;
    private readonly AVPacket* _pPacket;
    private readonly AVFrame* _receivedFrame;
    private readonly int _streamIndex;

    public ScreenshotService(ILogger<ScreenshotService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        var url = config["CameraUrl"] ?? throw new ArgumentNullException("CameraUrl");

        _pFormatContext = ffmpeg.avformat_alloc_context();
        _receivedFrame = ffmpeg.av_frame_alloc();
        var pFormatContext = _pFormatContext;
        ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
        ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
        AVCodec* codec = null;
        _streamIndex = ffmpeg
            .av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0)
            .ThrowExceptionIfError();
        _pCodecContext = ffmpeg.avcodec_alloc_context3(codec);

        ffmpeg.avcodec_parameters_to_context(_pCodecContext, _pFormatContext->streams[_streamIndex]->codecpar)
            .ThrowExceptionIfError();
        ffmpeg.avcodec_open2(_pCodecContext, codec, null).ThrowExceptionIfError();

        CodecName = ffmpeg.avcodec_get_name(codec->id);
        // FrameSize = new Size(_pCodecContext->width, _pCodecContext->height);
        PixelFormat = _pCodecContext->pix_fmt;

        _pPacket = ffmpeg.av_packet_alloc();
        _pFrame = ffmpeg.av_frame_alloc();
    }

    public string CodecName { get; }
    // public Size FrameSize { get; }
    public AVPixelFormat PixelFormat { get; }

    public void Dispose()
    {
        var pFrame = _pFrame;
        ffmpeg.av_frame_free(&pFrame);

        var pPacket = _pPacket;
        ffmpeg.av_packet_free(&pPacket);

        ffmpeg.avcodec_close(_pCodecContext);
        var pFormatContext = _pFormatContext;
        ffmpeg.avformat_close_input(&pFormatContext);
    }

    public bool TryDecodeNextKeyFrame(out AVFrame frame)
    {
        ffmpeg.av_frame_unref(_pFrame);
        ffmpeg.av_frame_unref(_receivedFrame);
        int error;

        do
        {
            try
            {
                do
                {
                    // 遍历流

                    ffmpeg.av_packet_unref(_pPacket);
                    error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);

                    if (error == ffmpeg.AVERROR_EOF)
                    {
                        frame = *_pFrame;
                        return false;
                    }

                    error.ThrowExceptionIfError();
                } while (_pPacket->stream_index != _streamIndex);

                ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket).ThrowExceptionIfError();
            }
            finally
            {
                ffmpeg.av_packet_unref(_pPacket);
            }

            error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);


            if (_pPacket->flags != ffmpeg.AV_PKT_FLAG_KEY)
            {
                // 非关键帧
                continue;
            }
        } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

        error.ThrowExceptionIfError();

        if (_pCodecContext->hw_device_ctx != null)
        {
            ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0).ThrowExceptionIfError();
            frame = *_receivedFrame;
        }
        else
            frame = *_pFrame;

        return true;
    }

    public void SaveFrameAsPNG(AVFrame frame, string filename)
    {
        var pFrame = &frame;

        // 创建 AVCodecContext 和 AVCodec
        AVCodec* pngCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_PNG);
        AVCodecContext* pngCodecContext = ffmpeg.avcodec_alloc_context3(pngCodec);

        // 设置编码器参数
        pngCodecContext->width = pFrame->width;
        pngCodecContext->height = pFrame->height;
        pngCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGBA; // 使用 RGBA 像素格式

        // 打开编码器
        ffmpeg.avcodec_open2(pngCodecContext, pngCodec, null);

        // 创建 AVFrame 用于编码
        AVFrame* pngFrame = ffmpeg.av_frame_alloc();
        pngFrame->width = pFrame->width;
        pngFrame->height = pFrame->height;
        pngFrame->format = (int)pngCodecContext->pix_fmt;
        ffmpeg.av_frame_get_buffer(pngFrame, 32); // 分配内存

        // 将 AVFrame 数据复制到 pngFrame
        ffmpeg.av_frame_copy(pngFrame, pFrame);
        ffmpeg.av_frame_copy_props(pngFrame, pFrame);

        // 编码并保存为 PNG 文件
        using (var outFile = File.OpenWrite(filename))
        {
            AVPacket* pkt = ffmpeg.av_packet_alloc();

            pkt->data = null;
            pkt->size = 0;

            int ret;
            if ((ret = ffmpeg.avcodec_send_frame(pngCodecContext, pngFrame)) < 0)
            {
                Console.WriteLine($"Error sending frame for encoding");
                return;
            }

            byte[] buffer = new byte[pkt->size];
            Marshal.Copy((IntPtr)pkt->data, buffer, 0, pkt->size);
            
            while ((ret = ffmpeg.avcodec_receive_packet(pngCodecContext, pkt)) >= 0)
            {
                outFile.Write(buffer, 0, pkt->size);
                ffmpeg.av_packet_unref(pkt);
            }

            if (ret == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_frame(pngCodecContext, null);
                while ((ret = ffmpeg.avcodec_receive_packet(pngCodecContext, pkt)) >= 0)
                {
                    outFile.Write(buffer, 0, pkt->size);
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            else
            {
                Console.WriteLine($"Error during encoding");
            }
        }

        // 释放资源
        ffmpeg.av_frame_free(&pngFrame);
        ffmpeg.avcodec_free_context(&pngCodecContext);
    }

    public IReadOnlyDictionary<string, string?>? GetContextInfo()
    {
        AVDictionaryEntry* tag = null;

        var result = new Dictionary<string, string?>();

        while ((tag = ffmpeg.av_dict_get(_pFormatContext->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
            var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);

            if (key is not null)
                result.Add(key, value);
        }

        return result;
    }
}
