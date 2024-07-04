namespace CameraScreenshotBotService.Configs;

public class StreamOption
{
    public required Uri Url { get; set; } = null!;
    public uint DecodeThreads { get; set; } = 4;
    public string FfmpegRoot { get; set; } = null!;
    public string? LogLevel { get; set; }
}
