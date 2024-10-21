using System.IO.IsolatedStorage;
using System.Text.Json;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;

namespace CameraScreenshotBot.Core.Extensions.DependencyInjection;

public static class HostApplicationBuilderExtension
{
    public static void ConfigureBot(this HostApplicationBuilder builder)
    {
        var isoStore = IsolatedStorageFile.GetStore(
            IsolatedStorageScope.User | IsolatedStorageScope.Application, null, null);

        const string deviceInfoPath = "deviceInfo.json";
        const string keyStorePath = "key.json";

        var botConfig = new BotConfig
        {
            GetOptimumServer = true,
            UseIPv6Network = false,
            Protocol = Protocols.Linux,
        };

        var deviceInfo = isoStore.FileExists(deviceInfoPath)
            ? JsonSerializer.Deserialize<BotDeviceInfo>(
                  isoStore.OpenFile(deviceInfoPath, FileMode.Open, FileAccess.Read))
              ?? BotDeviceInfo.GenerateInfo()
            : BotDeviceInfo.GenerateInfo();
        deviceInfo.DeviceName = "linux-capture";

        var keyStore = isoStore.FileExists(keyStorePath)
            ? JsonSerializer.Deserialize<BotKeystore>(
                  isoStore.OpenFile(keyStorePath, FileMode.Open, FileAccess.Read))
              ?? new()
            : new();

        builder.Services.AddSingleton(BotFactory.Create(botConfig, deviceInfo, keyStore));
    }
}