using CameraScreenshotBotService.Services;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Utility;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, StorageService storageService, ScreenshotService screenshotService, OneBotSigner signer) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly StorageService _storageService = storageService;
    private readonly ScreenshotService _screenshotService = screenshotService;
    private readonly OneBotSigner _signer = signer;

    private readonly BotDeviceInfo _deviceInfo = new()
    {
        Guid = Guid.NewGuid(),
        MacAddress = [0x3C, 0x22, 0x48, 0xFF, 0x2B, 0xE3],
        DeviceName = $"lagrange-worker",
        SystemKernel = "Windows 10.0.19042",
        KernelVersion = "10.0.19042.0"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotContext bot = null!;
        var keyStore = _storageService.LoadKeyStore();
        if (keyStore is null)
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
            stream.Close();

            _logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            await bot.LoginByQrCode();

            codeImgFile.Delete();

            _storageService.SaveKeyStore(bot.UpdateKeystore());
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

        _storageService.SaveKeyStore(bot.UpdateKeystore());

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

        bot.Invoker.OnGroupMessageReceived += async (_, @event) =>
        {
            var messageChain = @event.Chain;
            var textMessage = messageChain.Select(m =>
            {
                if (m is TextEntity text)
                {
                    return text;
                }
                return null;
            }).Where(m => m != null);
            if (textMessage.Any(text =>
            {
                var textStr = text?.Text;
                return textStr?.StartsWith("see") ?? false;
            }))
            {
                var captureResult = _screenshotService.TryCapturePngImage(out var bs);

                if (!captureResult)
                {
                    _logger.LogError("Decode Failed");
                }
                else
                {
                    var imageMessage = MessageBuilder
                        .Group()
                        .Image(bs);

                    await bot.SendMessage(imageMessage.Build());
                }
            }
        };

        bot.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            var messageChain = @event.Chain;
            var textMessage = messageChain.Select(m =>
            {
                if (m is TextEntity text)
                {
                    return text;
                }
                return null;
            }).Where(m => m != null);
            if (textMessage.Any(text =>
            {
                var textStr = text?.Text;
                return textStr?.StartsWith("see") ?? false;
            }))
            {
                var captureResult = _screenshotService.TryCapturePngImage(out var bs);

                if (!captureResult)
                {
                    _logger.LogError("Decode Failed");
                }
                else
                {
                    var imageMessage = MessageBuilder
                        .Friend(messageChain.FriendUin)
                        .Image(bs);

                    await bot.SendMessage(imageMessage.Build());
                }
            }
        };

        await Task.Delay(1000, stoppingToken);
    }
}

