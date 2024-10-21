namespace CameraScreenshotBot.Core.Configs;

public class StreamOption
{
    public required Uri Url { get; set; } = null!;
    public uint ConnectTimeout { get; set; } = 1200;
    public uint CodecTimeout { get; set; } = 100;
    public uint KeyframeSearchMax { get; set; } = 60;
    public uint CodecThreads { get; set; } = 4;
    public string FfmpegRoot { get; set; } = null!;
    public string? LogLevel { get; set; }
    public bool KeyFrameOnly { get; set; } = false;
}
