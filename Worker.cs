using CameraScreenshotBotService.Services;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Utility;
using System.Text.Json;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, StorageService storageService, ScreenshotService screenshotService, OneBotSigner signer, IConfiguration config) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly StorageService _storageService = storageService;
    private readonly ScreenshotService _screenshotService = screenshotService;
    private readonly OneBotSigner _signer = signer;
    // private readonly IList<uint> _allowGroups = JsonSerializer.Deserialize<IList<uint>>(config["AllowGroups"]);

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

            // Ê×´ÎµÇÂ½
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
            var recvMessages = @event.Chain;

            _logger.LogInformation("Receive friend message: {json}",
                JsonSerializer.Serialize(recvMessages.Select(m => m.ToPreviewString())));

            var textMessages = recvMessages
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text?.StartsWith("ÈÃÎÒ¿µ¿µ") ?? false))
            {
                var sendMessage = MessageBuilder.Group(@event.Chain.GroupUin ?? 0);

                try
                {
                    _screenshotService.OpenInput();
                    var captureResult = _screenshotService.TryCapturePngImage(out var imageBytes);

                    if (!captureResult || imageBytes is null)
                    {
                        _logger.LogError("Decode failed, send error message.");
                        sendMessage.Text("½Ü¸ç²»Òª£¡£¨Í¼Ïñ±à½âÂëÊ§°Ü£©");
                    }
                    else
                    {
                        sendMessage.Image(imageBytes);
                    }
                }
                catch (ApplicationException e)
                {
                    _logger.LogError("Faile to decode and encode, {error}", e.Message);
                    sendMessage.Text("½Ü¸ç²»Òª£¡£¨Í¼Ïñ±à½âÂë±ÀÀ££©");
                }
                finally
                {
                    _screenshotService.CloseInput();
                    await bot.SendMessage(sendMessage.Build());
                }
            }
        };

        bot.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            var recvMessages = @event.Chain;

            _logger.LogInformation("Receive friend message: {json}",
                JsonSerializer.Serialize(recvMessages.Select(m => m.ToPreviewString())));

            var textMessages = recvMessages
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text?.StartsWith("ÈÃÎÒ¿µ¿µ") ?? false))
            {
                var sendMessage = MessageBuilder.Friend(@event.Chain.FriendUin);

                try
                {
                    _screenshotService.OpenInput();
                    var captureResult = _screenshotService.TryCapturePngImage(out var imageBytes);

                    if (!captureResult || imageBytes is null)
                    {
                        _logger.LogError("Decode failed, send error message.");
                        sendMessage.Text("½Ü¸ç²»Òª£¡£¨Í¼Ïñ±à½âÂëÊ§°Ü£©");
                    }
                    else
                    {
                        sendMessage.Image(imageBytes);
                    }
                }
                catch (ApplicationException e)
                {
                    _logger.LogError("Faile to decode and encode, {error}", e.Message);
                    sendMessage.Text("½Ü¸ç²»Òª£¡£¨Í¼Ïñ±à½âÂë±ÀÀ££©");
                }
                finally
                {
                    _screenshotService.CloseInput();
                    await bot.SendMessage(sendMessage.Build());
                }
            }
        };

        await Task.Delay(1000, stoppingToken);
    }
}

