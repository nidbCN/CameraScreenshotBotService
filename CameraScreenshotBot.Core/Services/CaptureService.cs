using CameraCaptureBot.Core.Configs;
using CameraCaptureBot.Core.Extensions;
using CameraCaptureBot.Core.Utils;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Options;

namespace CameraCaptureBot.Core.Services;

public readonly unsafe struct AvCodecContextWrapper(AVCodecContext* ctx)
{
    public AVCodecContext* Value { get; } = ctx;
}

public sealed class CaptureService : IDisposable
{
    private readonly ILogger<CaptureService> _logger;
    private readonly StreamOption _streamOption;

    private readonly unsafe AVCodecContext* _decoderCtx;
    private readonly unsafe AVCodecContext* _webpEncoderCtx;

    private unsafe AVFormatContext* _inputFormatCtx;
    private readonly unsafe AVDictionary* _openOptions = null;

    private readonly unsafe AVFrame* _frame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVFrame* _webpOutputFrame = ffmpeg.av_frame_alloc();
    private readonly unsafe AVPacket* _packet = ffmpeg.av_packet_alloc();

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
        _streamOption = option.Value;

        if (_streamOption.Url is null)
            throw new ArgumentNullException(nameof(option), "StreamOption.Url can not be null.");

        _codecCancellationToken = new(TimeSpan.FromMilliseconds(
            _streamOption.CodecTimeout));

        // 设置超时
        var openOptions = _openOptions;
        ffmpeg.av_dict_set(&openOptions, "timeout", _streamOption.ConnectTimeout.ToString(), 0);

        #region 初始化视频流解码器
        OpenInput();

        ffmpeg.avformat_find_stream_info(_inputFormatCtx, null)
            .ThrowExceptionIfError();

        // 匹配解码器信息
        AVCodec* decoder = null;
        _streamIndex = ffmpeg
            .av_find_best_stream(_inputFormatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0)
            .ThrowExceptionIfError();

        // 创建解码器
        _decoderCtx = FfMpegUtils.CreateCodecCtx(decoder, codec =>
        {
            ffmpeg.avcodec_parameters_to_context(codec.Value, _inputFormatCtx->streams[_streamIndex]->codecpar)
                .ThrowExceptionIfError();

            codec.Value->thread_count = (int)_streamOption.CodecThreads;
            codec.Value->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            if (_streamOption.KeyFrameOnly)
            {
                codec.Value->skip_frame = AVDiscard.AVDISCARD_NONKEY;
            }
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
        #endregion

        #region 初始化图片编码器
        _webpEncoderCtx = FfMpegUtils.CreateCodecCtx(AVCodecID.AV_CODEC_ID_WEBP, config =>
            {
                config.Value->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                config.Value->gop_size = 1;
                config.Value->thread_count = (int)_streamOption.CodecThreads;
                config.Value->time_base = new() { den = 1, num = 1000 };
                config.Value->flags |= ffmpeg.AV_CODEC_FLAG_COPY_OPAQUE;
                config.Value->width = StreamWidth;
                config.Value->height = StreamHeight;

                ffmpeg.av_opt_set(config.Value->priv_data, "preset", "photo", ffmpeg.AV_OPT_SEARCH_CHILDREN)
                    .ThrowExceptionIfError();
            });
        #endregion
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
        var pFrame = _frame;
        ffmpeg.av_frame_free(&pFrame);
        var pWebpOutputFrame = _webpOutputFrame;
        ffmpeg.av_frame_free(&pWebpOutputFrame);
        var pPacket = _packet;
        ffmpeg.av_packet_free(&pPacket);

        ffmpeg.avcodec_close(_decoderCtx);
        ffmpeg.avcodec_close(_webpEncoderCtx);
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
    /// <param name="frame">帧指针，指向_frame或null</param>
    /// <returns></returns>
    public unsafe bool TryDecodeNextFrameUnsafe(out AVFrame* frame)
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

            // 尝试发送
            _logger.LogDebug("Try send packet to decoder.");
            var sendResult = ffmpeg.avcodec_send_packet(_decoderCtx, _packet);
            if (sendResult == 0)
            {
                // 发送成功
                _logger.LogDebug("Packet sent success, try get decoded frame.");
                // 获取解码结果
                _logger.LogDebug("Try receive frame from decoder.");
                decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
            }
            else if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                do
                {
                    // 如果发送失败，尝试清除堵塞的输出缓冲区重试发送
                    _logger.LogWarning("Packet send failed with '{msg}', clean output buffer and try again.", FfMpegExtension.av_strerror(sendResult));

                    FlushDecoderBufferUnsafe();
                    _logger.LogDebug("Try send packet to decoder.");
                    sendResult = ffmpeg.avcodec_send_packet(_decoderCtx, _packet);
                } while (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN));

                _logger.LogDebug("Packet sent success after some retry, try get decoded frame.");
                _logger.LogDebug("Try receive frame from decoder.");
                decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
            }
            else
            {
                // 无法处理的发送失败
                _logger.LogError("Packet sent failed with '{msg}', return.", FfMpegExtension.av_strerror(sendResult));
                frame = null;
                return false;
            }

            // 解码正常
            if (decodeResult == 0 || decodeResult == ffmpeg.AVERROR_EOF)
            {
                if (decodeResult == ffmpeg.AVERROR_EOF)
                {
                    _logger.LogWarning("Receive EOF, maybe stream disconnected.");
                    frame = null;
                    return false;
                }

                if (_frame->pts < 0)
                {
                    _logger.LogWarning("Decode video success. type {type}, but pts {pts} < 0, drop.",
                        _frame->pict_type.ToString(),
                        FfmpegTimeToTimeSpan(_frame->pts, _decoderCtx->time_base).ToString("c"));
                    continue;
                }

                _logger.LogInformation("Decode video success. type {type}, pts {pts}.",
                    _frame->pict_type.ToString(),
                    FfmpegTimeToTimeSpan(_frame->pts, _decoderCtx->time_base).ToString("c"));

                break;
            }

            /* 接收解码异常
             * 需要发送packet来填充缓冲区，但实际上
             * libwebp 或设置了 `AV_CODEC_FLAG_LOW_DELAY` 的 libwebp_anim
             * 应该不会执行到这里
             */
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

    /// <summary>
    /// 丢弃解码器结果中所有的帧（异步）
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
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
            _logger.LogDebug("Drop frame {num} in decoder buffer.", _decoderCtx->frame_num);
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

    /// <summary>
    /// 截取图片（异步）
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>是否成功，图片字节码</returns>
    public async Task<(bool, byte[]?)> CaptureImageAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);

        // Check image cache.
        try
        {
            var captureTimeSpan = DateTime.Now - LastCaptureTime;
            if (LastCapturedImage != null && captureTimeSpan <= TimeSpan.FromSeconds(5))
            {
                _logger.LogInformation("Return image cached {time} ago.", captureTimeSpan);
                return (true, LastCapturedImage);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        // Capture new image and process it.
        var result = await Task.Run(async () =>
        {
            await _semaphore.WaitAsync(cancellationToken);

            _logger.LogInformation("Cache image expired, capture new.");
            try
            {
                unsafe
                {
                    OpenInput();

                    if (!TryDecodeNextFrameUnsafe(out var decodedFrame))
                    {
                        if (decodedFrame != null)
                        {
                            ffmpeg.av_frame_unref(decodedFrame);
                        }

                        return (false, null);
                    }

                    CloseInput();

                    if (TryEncodeWebpUnsafe(decodedFrame, out var outputImage))
                    {
                        if (decodedFrame != null)
                        {
                            ffmpeg.av_frame_unref(decodedFrame);
                        }

                        return (false, null);
                    }

                    LastCapturedImage = outputImage;
                    LastCaptureTime = DateTime.Now;
                    return (true, outputImage);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }, cancellationToken);
        return result;
    }

    public unsafe bool TryEncodeWebpUnsafe(AVFrame* frameToEncode, out byte[]? image)
    {
        // 开始编码
        _logger.LogDebug("Send frameToEncode {num} to encoder.", _webpEncoderCtx->frame_num);
        ffmpeg.avcodec_send_frame(_webpEncoderCtx, frameToEncode)
            .ThrowExceptionIfError();

        using var memStream = new MemoryStream();

        var ct = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_streamOption.CodecTimeout));

        int encodeResult;

        do
        {
            // 尝试接收包
            _logger.LogDebug("Receive packet from encoder.");
            encodeResult = ffmpeg.avcodec_receive_packet(_webpEncoderCtx, _packet);
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
                encodeResult = ffmpeg.avcodec_receive_packet(_webpEncoderCtx, _packet);
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
        ffmpeg.av_frame_unref(frameToEncode);

        image = memStream.ToArray();
        memStream.Close();

        return false;
    }
}