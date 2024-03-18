using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using System.IO.IsolatedStorage;
using System.Text.Json;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly IsolatedStorageFile _isoStore = IsolatedStorageFile.GetStore(
IsolatedStorageScope.User | IsolatedStorageScope.Application, null, null);

    private readonly BotDeviceInfo _deviceInfo = new()
    {
        Guid = Guid.NewGuid(),
        MacAddress = [0x3C, 0x22, 0x48, 0xFF, 0x2B, 0xE3],
        DeviceName = $"linux-work",
        SystemKernel = "Windows 10.0.19042",
        KernelVersion = "10.0.19042.0"
    };

    private BotKeystore? LoadKeyStore()
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        if (!_isoStore.FileExists($"{AppDomain.CurrentDomain.FriendlyName}/key"))
        {
            return null;
        }

        using var keyStream = _isoStore.OpenFile($"{AppDomain.CurrentDomain.FriendlyName}/key", FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<BotKeystore>(keyStream);
    }
    private void SaveKeyStore(BotKeystore keyStore)
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        using var keyStream = _isoStore.OpenFile($"{AppDomain.CurrentDomain.FriendlyName}/key", FileMode.OpenOrCreate, FileAccess.Write);
        JsonSerializer.Serialize(keyStream, keyStore);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotContext bot = null!;
        var keyStore = LoadKeyStore();
        if (keyStore is null)
        {
            // Ê×´ÎµÇÂ½
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,

            }, _deviceInfo, new BotKeystore());

            var (_, bytes) = await bot.FetchQrCode() ?? throw new Exception(message: "NoQRCode");

            await File.WriteAllBytesAsync("qrc.png", bytes, stoppingToken);
            await bot.LoginByQrCode();
            SaveKeyStore(bot.UpdateKeystore());
        } else
        {
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,

            }, _deviceInfo, keystore);
            await bot.LoginByPassword();
        }

        SaveKeyStore(bot.UpdateKeystore());

        bot.Invoker.OnBotOnlineEvent += (_, @event) =>
        {
            _logger.LogInformation(@event.ToString());
        };

        bot.Invoker.OnGroupMessageReceived += (_, @event) =>
        {
            _logger.LogInformation(@event.ToString());
            var messageChain = @event.Chain;
            _logger.LogInformation(messageChain[0].ToPreviewString());
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
