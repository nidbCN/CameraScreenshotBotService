using CameraScreenshotBotService.Services;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Utility;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, StorageService storageService, ScreenshotService screenshotService, OneBotSigner signer) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly StorageService _storageService = storageService;
    private readonly ScreenshotService _screenshotService = screenshotService;
    private readonly OneBotSigner _signer = signer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotContext bot = null!;

        var keyStore = _storageService.LoadKeyStore();
        var deviceInfo = _storageService.LoadDeviceInfo();

        if (keyStore is null || deviceInfo is null)
        {
            deviceInfo = BotDeviceInfo.GenerateInfo();
            deviceInfo.DeviceName = "windows-camera";
            keyStore = new BotKeystore();

            //  ◊¥Œµ«¬Ω
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
                CustomSignProvider = _signer,
            }, deviceInfo, new BotKeystore());

            var (_, codeImg) = await bot.FetchQrCode()
                ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            var codeImgFile = new FileInfo("./qrcode.png");
            using var stream = codeImgFile.OpenWrite();
            await stream.WriteAsync(codeImg, cancellationToken: stoppingToken);
            stream.Close();

            _logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            await bot.LoginByQrCode();

            codeImgFile.Delete();

            _storageService.SaveKeyStore(bot.UpdateKeystore());
            _storageService.SaveDeviceInfo(deviceInfo);
        }
        else
        {
            bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
                CustomSignProvider = _signer,
            }, deviceInfo, keyStore);
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

        };

        bot.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            var messageChain = @event.Chain;

            _logger.LogInformation("Receive friend message: {json}",
                JsonSerializer.Serialize(messageChain.Select(m => m.ToPreviewString())));

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
                try
                {
                    _screenshotService.OpenInput();
                    var captureResult = _screenshotService.TryCapturePngImage(out var bs);

                    if (!captureResult)
                    {
                        _logger.LogError("Decode Failed");
                    }
                    else
                    {
                        MessageBuilder imageMessage = null;

                        if (bs is null)
                        {
                            imageMessage = MessageBuilder
                            .Friend(@event.Chain.FriendUin)
                            .Text("±‡¬Î¥Û ß∞‹");
                        }
                        else
                        {
                            imageMessage = MessageBuilder
                                .Friend(@event.Chain.FriendUin)
                                .Image(bs);
                        }
                        await bot.SendMessage(imageMessage.Build());
                    }
                }
                catch (ApplicationException e)
                {
                    _logger.LogError(e.Message);
                }
                finally
                {
                    _screenshotService.CloseInput();
                }
            }
        };

        await Task.Delay(1000, stoppingToken);
    }
}

