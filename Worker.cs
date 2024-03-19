using CameraScreenshotBotService.Helpers;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.OneBot.Utility;
using System.IO.IsolatedStorage;
using System.Text.Json;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, OneBotSigner signer) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly OneBotSigner _signer = signer;

    private readonly IsolatedStorageFile _isoStore = IsolatedStorageFile.GetStore(
IsolatedStorageScope.User | IsolatedStorageScope.Application, null, null);

    private readonly BotDeviceInfo _deviceInfo = new()
    {
        Guid = Guid.NewGuid(),
        MacAddress = [0x3C, 0x22, 0x48, 0xFF, 0x2B, 0xE3],
        DeviceName = $"lagrange-worker",
        SystemKernel = "Windows 10.0.19042",
        KernelVersion = "10.0.19042.0"
    };

    private readonly string _devieInfoPath = Path.Combine(AppDomain.CurrentDomain.FriendlyName, "deviceInfo.json");
    private readonly string _keyStorePath = Path.Combine(AppDomain.CurrentDomain.FriendlyName, "key.json");

    private BotDeviceInfo? LoadDeviceInfo()
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        if (!_isoStore.FileExists(_devieInfoPath))
        {
            return null;
        }

        using var infoStream = _isoStore.OpenFile(_devieInfoPath, FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<BotDeviceInfo>(infoStream);
    }

    private void SaveDeviceInfo(BotDeviceInfo deviceInfo)
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        using var infoStream = _isoStore.OpenFile(_devieInfoPath, FileMode.OpenOrCreate, FileAccess.Write);
        JsonSerializer.Serialize(infoStream, deviceInfo);
    }

    private BotKeystore? LoadKeyStore()
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        if (!_isoStore.FileExists(_keyStorePath))
        {
            return null;
        }

        using var keyStream = _isoStore.OpenFile(_keyStorePath, FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<BotKeystore>(keyStream);
    }
    private void SaveKeyStore(BotKeystore keyStore)
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        using var keyStream = _isoStore.OpenFile(_keyStorePath, FileMode.OpenOrCreate, FileAccess.Write);
        JsonSerializer.Serialize(keyStream, keyStore);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotContext bot = null!;
        var keyStore = LoadKeyStore();
        if (keyStore is null)
        // if (true)
        {
            // Ê×´ÎµÇÂ½
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
                CustomSignProvider = _signer,

            }, _deviceInfo, new BotKeystore());

            var (_, codeImg) = await bot.FetchQrCode() ?? throw new Exception(message: "Fetch QRCode failed.");

            var codeImgFile = new FileInfo("./qrcode.png");
            using var stream = codeImgFile.OpenWrite();
            await stream.WriteAsync(codeImg, cancellationToken: stoppingToken);

            _logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            await bot.LoginByQrCode();

            codeImgFile.Delete();
           
            SaveKeyStore(bot.UpdateKeystore());
        }
        else
        {
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Windows
            }, _deviceInfo, keyStore);
            await bot.LoginByPassword();
        }

        SaveKeyStore(bot.UpdateKeystore());

        bot.Invoker.OnBotLogEvent += (_, @event) =>
        {
            switch (@event.Level)
            {
                case Lagrange.Core.Event.EventArg.LogLevel.Debug:
                    _logger.LogDebug(@event.EventMessage);
                    break;
                case Lagrange.Core.Event.EventArg.LogLevel.Warning:
                    _logger.LogWarning(@event.EventMessage);
                    break;
                case Lagrange.Core.Event.EventArg.LogLevel.Fatal:
                    _logger.LogError(@event.EventMessage);
                    break;
            }
        };

        bot.Invoker.OnBotCaptchaEvent += (_, @event) =>
        {
            _logger.LogWarning(@event.ToString());
            _logger.LogWarning("Need captcha!");
            Console.WriteLine(@event.ToString());
            var captcha = Console.ReadLine();
            var randStr = Console.ReadLine();
            if (captcha != null && randStr != null) bot.SubmitCaptcha(captcha, randStr);
        };

        bot.Invoker.OnBotOnlineEvent += (_, @event) =>
        {
            _logger.LogInformation("Login Success!");
        };

        bot.Invoker.OnGroupMessageReceived += (_, @event) =>
        {
            var messageChain = @event.Chain;
            _logger.LogInformation(messageChain[0].ToPreviewString());
        };

        bot.Invoker.OnFriendMessageReceived += (_, @event) =>
        {
            var messageChain = @event.Chain;
            _logger.LogInformation(messageChain[0].ToPreviewString());
        };

        var privateMessageChain = MessageBuilder.Friend(3307954433).Text("111").Build();
        await bot.SendMessage(privateMessageChain);

        await Task.Delay(1000, stoppingToken);
    }
}

