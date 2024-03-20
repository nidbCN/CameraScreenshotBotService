using CameraScreenshotBotService.Services;
using Lagrange.Core.Common;
using Lagrange.OneBot.Utility;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, /*StorageService storageService,*/ ScreenshotService screenshotService, OneBotSigner signer) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    // private readonly StorageService _storageService = storageService;
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
        await Console.Out.WriteLineAsync("async out, work started");

        await Task.Delay(2000);

        var r = _screenshotService.TryCapturePngImage(out var bs);

        if (!r)
        {
            _logger.LogError("Decode Failed");
        }


        //unsafe
        //{
        //    var l = (f.data)[0];
        //    var h = 480; var w = 640;
        //    var warp = f.linesize[0];
        //    var data = new byte[warp * h];
        //    Marshal.Copy((IntPtr)l, data, 0, warp * h);

        //    var file = new FileInfo("testg.pgm");

        //    using var fs = file.OpenWrite();

        //    var head = "P5\n640 480\n255\n";

        //    var headB = Encoding.ASCII.GetBytes(head);

        //    fs.Write(headB);

        //    for (int i = 0; i < h; i++)
        //    {
        //        fs.Write(data, i*warp, w);
        //    }

        //    fs.Close();

        //}

        return;

        /*
        BotContext bot = null!;
        var keyStore = _storageService.LoadKeyStore();
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

        bot.Invoker.OnGroupMessageReceived += (_, @event) =>
        {
            var messageChain = @event.Chain;
            _logger.LogInformation(messageChain[0].ToPreviewString());
            var textMessage = messageChain.Select(m => m as TextEntity);
            if (textMessage.Any(text => text?.ToPreviewString().StartsWith("") ?? false))
            {

            }

        };

        bot.Invoker.OnFriendMessageReceived += (_, @event) =>
        {
            var messageChain = @event.Chain;
            _logger.LogInformation(messageChain[0].ToPreviewString());
        };

        var privateMessageChain = MessageBuilder.Friend(3307954433).Text("111").Build();
        await bot.SendMessage(privateMessageChain);

        await Task.Delay(1000, stoppingToken);
        */
    }
}

