using System.Text.Json;
using CameraCaptureBot.Core.Configs;
using CameraCaptureBot.Core.Extensions;
using CameraCaptureBot.Core.Utils;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Options;

namespace CameraCaptureBot.Core.Services;

public class FaceMosaicProcessService
    : IImageProcessService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FaceMosaicProcessService> _logger;
    private readonly StreamOption _streamOption;

    private unsafe AVFrame* _rawFrame;
    private unsafe AVFrame* _processFrame;
    private readonly unsafe AVCodecContext* _bmpEncoderCtx;
    private readonly unsafe AVPacket* _packet;

    public FaceMosaicProcessService(HttpClient httpClient,
        ILogger<FaceMosaicProcessService> logger, IOptions<StreamOption> streamOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _streamOption = streamOptions.Value;

        #region 初始化图片编码器
        unsafe
        {
            _bmpEncoderCtx = FfMpegUtils.CreateCodecCtx(AVCodecID.AV_CODEC_ID_WEBP, config =>
            {
                config.Value->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                config.Value->gop_size = 1;
                config.Value->thread_count = (int)_streamOption.CodecThreads;
                config.Value->time_base = new() { den = 1, num = 1000 };
                config.Value->flags |= ffmpeg.AV_CODEC_FLAG_COPY_OPAQUE;
                config.Value->width = 1920;
                config.Value->height = 1080;

                ffmpeg.av_opt_set(config.Value->priv_data, "preset", "photo", ffmpeg.AV_OPT_SEARCH_CHILDREN)
                    .ThrowExceptionIfError();
            });
        }
        #endregion
    }

    public unsafe bool TryProcessImage(AVFrame* inputFrame, out AVFrame* outputFrame)
    {
        _rawFrame = inputFrame;
        _processFrame = ConvertToPixelFormatUnsafe(_rawFrame, AVPixelFormat.AV_PIX_FMT_RGB24);
        // 转换到 RGB24

        // 转换到 -1:640
        // 编码 BMP

        // 推理
        // 打码
        throw new NotImplementedException();
    }

    /// <summary>
    /// 转换像素格式
    /// </summary>
    /// <param name="inputFrame">输入帧（原格式）</param>
    /// <param name="targetPixelFormat">目标像素格式</param>
    /// <returns>返回新格式的帧（与输入帧不是同一引用）</returns>
    /// <exception cref="NotImplementedException"></exception>
    public unsafe AVFrame* ConvertToPixelFormatUnsafe(AVFrame* inputFrame, AVPixelFormat targetPixelFormat)
    {
        throw new NotImplementedException();
    }


    public unsafe AVFrame* ScaleFrameUnsafe(AVFrame* inputFrame, int targetWidth, int targetHeight)
    {

        throw new NotImplementedException();
    }

    public unsafe MemoryStream OpenEncodeBmpUnsafeStream(AVFrame* inputFrame)
    {

        // 开始编码
        _logger.LogDebug("Send frameToEncode {num} to encoder.", _bmpEncoderCtx->frame_num);
        ffmpeg.avcodec_send_frame(_bmpEncoderCtx, inputFrame)
        .ThrowExceptionIfError();

        using var memStream = new MemoryStream();

        var ct = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_streamOption.CodecTimeout));
        int encodeResult;

        do
        {
            // 尝试接收包
            _logger.LogDebug("Receive packet from encoder.");
            encodeResult = ffmpeg.avcodec_receive_packet(_bmpEncoderCtx, _packet);
        } while (encodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN)
                 && !ct.IsCancellationRequested);

        if (encodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            // 超时并且依旧不可用
            _logger.LogError("Encode image failed! {msg}", FfMpegExtension.av_strerror(encodeResult));
            //result = false;
        }
        else if (encodeResult >= 0)
        {
            //正常接收到数据
            _logger.LogInformation("Save packet with size {s} to buffer.", _packet->size);
            FfMpegUtils.WriteToStream(memStream, _packet);
            //  result = true;

            while (encodeResult != ffmpeg.AVERROR_EOF)
            {
                encodeResult = ffmpeg.avcodec_receive_packet(_bmpEncoderCtx, _packet);
                if (_packet->size != 0)
                {
                    _logger.LogInformation("Continue received packet, save {s} to buffer.", _packet->size);
                    FfMpegUtils.WriteToStream(memStream, _packet);
                }
                else
                {
                    _logger.LogInformation("Received empty packet, no data to save.");
                    break;
                }
            }
        }
        else if (encodeResult == ffmpeg.AVERROR_EOF)
        {
            if (_packet->size != 0)
            {
                _logger.LogInformation("Received EOF, save {s} to buffer.", _packet->size);
                FfMpegUtils.WriteToStream(memStream, _packet);
            }
            else
            {
                _logger.LogInformation("Received EOF, no data to save.");
            }
        }

        // 释放资源
        ffmpeg.av_packet_unref(_packet);
        // 是否释放传入的帧，存疑
        //ffmpeg.av_frame_unref(rawFrame);

        return memStream;
    }

    public async Task<IList<FaceBox>> PredictFaces(Stream stream)
        => await JsonSerializer.DeserializeAsync<IList<FaceBox>>(
            await (await _httpClient.PostAsync(
                    new Uri("http://localhost:8080"), new StreamContent(stream))
                ).Content.ReadAsStreamAsync())
           ?? [];

    public unsafe AVFrame* MosaicFrameUnsafe(AVFrame* rawFrame, IList<FaceBox> mosaics)
    {
        throw new NotImplementedException();
    }
}

public struct FaceBox
{
    public uint X { get; set; }
    public uint Y { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
}
