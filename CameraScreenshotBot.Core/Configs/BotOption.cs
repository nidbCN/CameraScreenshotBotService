using Lagrange.Core.Common;

namespace CameraScreenshotBot.Core.Configs;

public class BotOption
{
    public string KeyStoreFile { get; set; } = "keystore.json";
    public string DeviceInfoFile { get; set; } = "deviceInfo.json";
    public BotConfig FrameworkConfig { get; set; } = new()
    {
        AutoReconnect = true,
        AutoReLogin = true,
        GetOptimumServer = true,
        Protocol = Protocols.Linux,
        UseIPv6Network = true,
    };
}
