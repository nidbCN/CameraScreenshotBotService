using CameraCaptureBot.Core.Configs;
using CameraCaptureBot.Core.Extensions;
using CameraCaptureBot.Core.Utils;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

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


    #region 创建编码器
    /// <summary>
    /// 创建编解码器
    /// </summary>
    /// <param name="codecId">编解码器ID</param>
    /// <param name="config">配置</param>
    /// <param name="pixelFormat">像素格式</param>
    /// <returns>编解码器上下文</returns>
    public static unsafe AVCodecContext* CreateCodecCtx(AVCodecID codecId, Action<AvCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder(codecId);

        return CreateCodecCtx(codec, ctx =>
        {
            ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(ctx);
        });
    }

    public static unsafe AVCodecContext* CreateCodecCtx(string codecName, Action<AvCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);

        return CreateCodecCtx(codec, innerConfig =>
        {
            innerConfig.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
            config?.Invoke(innerConfig);
        });
    }

    public static unsafe AVCodecContext* CreateCodecCtx(AVCodec* codec, Action<AvCodecContextWrapper>? config = null)
    {
        // 编解码器
        var ctx = ffmpeg.avcodec_alloc_context3(codec);

        ctx->time_base = new() { num = 1, den = 25 }; // 设置时间基准
        ctx->framerate = new() { num = 25, den = 1 };

        config?.Invoke(new AvCodecContextWrapper(ctx));

        ffmpeg.avcodec_open2(ctx, codec, null).ThrowExceptionIfError();

        return ctx;
    }
    #endregion

    public static unsafe void WriteToStream(Stream stream, AVPacket* packet)
    {
        var buffer = new byte[packet->size];
        Marshal.Copy((IntPtr)packet->data, buffer, 0, packet->size);
        stream.Write(buffer, 0, packet->size);
    }

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
        _decoderCtx = CreateCodecCtx(decoder, codec =>
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
        _webpEncoderCtx = CreateCodecCtx("libwebp", config =>
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
    public unsafe AVFrame* DecodeNextFrameUnsafe()
    {
        using (_logger.BeginScope($"Decoder@{_decoderCtx->GetHashCode()}"))
        {
            var decodeResult = -1;
            var timeoutTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            while (!timeoutTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    do
                    {
                        // 遍历流查找 bestStream
                        ffmpeg.av_packet_unref(_packet);
                        var readResult = ffmpeg.av_read_frame(_inputFormatCtx, _packet);

                        // 视频流已经结束
                        if (readResult == ffmpeg.AVERROR_EOF)
                        {
                            var error = new ApplicationException(FfMpegExtension.av_strerror(readResult));
                            const string message = "Receive EOF in stream.";

                            _logger.LogError(error, message);
                            throw new EndOfStreamException(message, error);
                        }

                        // 其他错误
                        readResult.ThrowExceptionIfError();
                    } while (_packet->stream_index != _streamIndex);

                    using (_logger.BeginScope($"Packet@0x{_packet->GetHashCode():x8}"))
                    {
                        // 取到了 stream 中的包
                        _logger.LogDebug(
                            "Find packet in stream {index}, pts(decode):{pts}, dts(display):{dts}, key frame flag:{containsKey}",
                            _packet->stream_index,
                            FfmpegTimeToTimeSpan(_packet->pts, _decoderCtx->time_base).ToString("c"),
                            FfmpegTimeToTimeSpan(_packet->dts, _decoderCtx->time_base).ToString("c"),
                            _packet->flags & ffmpeg.AV_PKT_FLAG_KEY
                        );

                        // 空包
                        if (_packet->size <= 0)
                        {
                            _logger.LogWarning("Empty packet, ignore.");
                        }

                        // 校验关键帧
                        if (_streamOption.KeyFrameOnly)
                        {
                            if ((_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0x00)
                            {
                                _logger.LogWarning("Packet {id} not contains KEY frame, {options} enabled, drop.",
                                    _packet->ToString(), nameof(StreamOption.KeyFrameOnly));
                                continue;
                            }
                        }

                        // 校验 DTS/PTS
                        if (_packet->dts < 0 || _packet->pts < 0)
                        {
                            _logger.LogWarning("Packet dts or pts < 0, drop.");
                            continue;
                        }

                        // 尝试发送
                        _logger.LogDebug("Try send packet to decoder.");
                        var sendResult = ffmpeg.avcodec_send_packet(_decoderCtx, _packet);

                        if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            // reference:
                            // * tree/release/6.1/fftools/ffmpeg_dec.c:567
                            // 理论上不会出现 EAGAIN

                            _logger.LogWarning(
                                "Receive {error} after sent, this could be cause by ffmpeg bug or some reason, ignored this message.",
                                nameof(ffmpeg.EAGAIN));
                            sendResult = 0;
                        }

                        if (sendResult == 0 || sendResult == ffmpeg.AVERROR_EOF)
                        {
                            // 发送成功
                            _logger.LogDebug("Packet sent success, try get decoded frame.");
                            // 获取解码结果
                            decodeResult = ffmpeg.avcodec_receive_frame(_decoderCtx, _frame);
                        }
                        else
                        {
                            var error = new ApplicationException(FfMpegExtension.av_strerror(sendResult));

                            // 无法处理的发送失败
                            _logger.LogError(error, "Send packet to decoder failed.");

                            throw error;
                        }

                        if (decodeResult < 0)
                        {
                            // 错误处理
                            ApplicationException error;
                            var message = FfMpegExtension.av_strerror(decodeResult);

                            if (decodeResult == ffmpeg.AVERROR_EOF)
                            {
                                // reference:
                                // * https://ffmpeg.org/doxygen/6.1/group__lavc__decoding.html#ga11e6542c4e66d3028668788a1a74217c
                                // > the codec has been fully flushed, and there will be no more output frames
                                // 理论上不会出现 EOF
                                message =
                                    "the codec has been fully flushed, and there will be no more output frames.";

                                error = new(message);

                                _logger.LogError(error, "Received EOF from decoder.");
                            }
                            else if (decodeResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            {
                                // reference:
                                // * tree/release/6.1/fftools/ffmpeg_dec.c:596
                                // * https://ffmpeg.org/doxygen/6.1/group__lavc__decoding.html#ga11e6542c4e66d3028668788a1a74217c
                                // > output is not available in this state - user must try to send new input
                                // 理论上不会出现 EAGAIN
                                message =
                                    "output is not available in this state - user must try to send new input";

                                if (_streamOption.KeyFrameOnly)
                                {
                                    // 抛出异常，仅关键帧模式中，该错误不可能通过发送更多需要的包来解决
                                    error = new(message);

                                    _logger.LogError(error, "Received EAGAIN from decoder.");
                                    throw error;
                                }

                                // 忽略错误，发送下一个包进行编码，可能足够的包进入解码器可以解决
                                _logger.LogWarning("Receive EAGAIN from decoder, retry.");
                                continue;
                            }
                            else
                            {
                                error = new(message);
                                _logger.LogError(error, "Uncaught error occured during decoding.");
                                throw error;
                            }
                        }

                        // 解码正常
                        _logger.LogInformation("Decode video success. type {type}, pts {pts}.",
                            _frame->pict_type.ToString(),
                            FfmpegTimeToTimeSpan(_frame->pts, _decoderCtx->time_base).ToString("c"));

                        break;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            if (decodeResult != 0)
            {
                // 解码失败
                var error = new TimeoutException("Decode timeout.");
                _logger.LogError(error, "Failed to decode.");
                throw error;
            }

            if (_decoderCtx->hw_device_ctx is not null)
            {
                _logger.LogError("Hardware decode is unsupported, skip.");
                // 硬件解码数据转换
                // ffmpeg.av_hwframe_transfer_data(frame, _frame, 0).ThrowExceptionIfError();
            }

            return _frame;
        }
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
                    var decodedFrame = DecodeNextFrameUnsafe();
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
            return false;
        }
        else if (encodeResult >= 0)
        {
            //正常接收到数据
            _logger.LogInformation("Save packet with size {s} to buffer.", _packet->size);
            WriteToStream(memStream, _packet);
            //  result = true;

            while (encodeResult != ffmpeg.AVERROR_EOF)
            {
                encodeResult = ffmpeg.avcodec_receive_packet(_webpEncoderCtx, _packet);
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
        else if (encodeResult == ffmpeg.AVERROR_EOF)
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
        ffmpeg.av_frame_unref(frameToEncode);

        image = memStream.ToArray();
        memStream.Close();

        return false;
    }
}