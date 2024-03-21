using CameraScreenshotBotService.Services;
using FFmpeg.AutoGen;

namespace CameraScreenshotBotService.Workers;

public sealed class VideoStreamWorker : BackgroundService
{
    private ILogger<VideoStreamWorker> _logger;
    private readonly IConfiguration _config;

    private unsafe readonly SwsContext* _pixConverterCtx;

    private unsafe readonly AVCodecContext* _decoderCtx;
    private unsafe readonly AVCodecContext* _encoderCtx;

    private unsafe readonly AVFormatContext* _inputFormatCtx;
    private unsafe readonly AVFrame* _inputFrame;
    private unsafe readonly AVPacket* _pPacket;
    private unsafe readonly AVFrame* _receivedFrame;

    private readonly int _streamIndex;
    public string StreamCodecName { get; }
    public AVPixelFormat StreamPixelFormat { get; }
    public int StreamHeight { get; }
    public int StreamWidth { get; }

    public VideoStreamWorker(ILogger<VideoStreamWorker> logger)
    {
        _logger = logger;
    }

    public unsafe new void Dispose()
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
