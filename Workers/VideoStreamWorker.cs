using CameraScreenshotBotService.Extensions;
using FFmpeg.AutoGen;

namespace CameraScreenshotBotService.Workers;

public sealed class VideoStreamWorker : BackgroundService
{
    private ILogger<VideoStreamWorker> _logger;
    private readonly IConfiguration _config;

    private readonly unsafe SwsContext* _pixConverterCtx;

    private readonly unsafe AVCodecContext* _decoderCtx;
    private readonly unsafe AVCodecContext* _encoderCtx;

    private readonly unsafe AVFormatContext* _inputFormatCtx;
    private readonly unsafe AVFrame* _inputFrame;
    private readonly unsafe AVPacket* _pPacket;
    private readonly unsafe AVFrame* _receivedFrame;

    private readonly int _streamIndex;
    public string StreamCodecName { get; }
    public AVPixelFormat StreamPixelFormat { get; }
    public int StreamHeight { get; }
    public int StreamWidth { get; }

    public unsafe VideoStreamWorker(ILogger<VideoStreamWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        var url = config["Camera:Url"]
            ?? throw new ArgumentNullException("Camera:Url");

        // 初始化 ffmpeg 输入
        _inputFormatCtx = ffmpeg.avformat_alloc_context();
        _receivedFrame = ffmpeg.av_frame_alloc();
        var fotmatCtx = _inputFormatCtx;

        // 设置超时
        AVDictionary* openOptions = null;
        ffmpeg.av_dict_set(&openOptions, "stimeout", "3", 0);

        ffmpeg.avformat_open_input(&fotmatCtx, url, null, &openOptions).ThrowExceptionIfError();
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
            _decoderCtx->thread_count = ushort.Parse(config["DecodeThread"] ?? "4");
            ffmpeg.avcodec_open2(_decoderCtx, decoder, null).ThrowExceptionIfError();

            StreamCodecName = ffmpeg.avcodec_get_name(decoder->id);
            StreamPixelFormat = _decoderCtx->pix_fmt;
            StreamWidth = _decoderCtx->width;
            StreamHeight = _decoderCtx->height;
        }

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
            _pixConverterCtx = ffmpeg.sws_getContext(StreamWidth, StreamHeight, _decoderCtx->pix_fmt,
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

    public new unsafe void Dispose()
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {


        await Task.Delay(1000);
    }
}
