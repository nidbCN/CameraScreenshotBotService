using CameraScreenshotBotService.Configs;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common;
using Microsoft.Extensions.Options;
using Lagrange.OneBot.Utility;
using Lagrange.Core.Event;

namespace CameraScreenshotBotService.Services;

public class BotService : IDisposable
{
    private readonly ILogger<BotService> _logger;
    private readonly BotOption _botOption;
    private readonly StorageService _storageService;
    private readonly OneBotSigner _signer;

    public readonly BotContext Bot;
    public EventInvoker Invoker => Bot.Invoker;

    public BotService(ILogger<BotService> logger, IOptions<BotOption> options, StorageService storageService, OneBotSigner signer)
    {
        _logger = logger;
        _botOption = options.Value;
        _storageService = storageService;
        _signer = signer;

        var keyStore = _storageService.LoadKeyStore();
        var deviceInfo = _storageService.LoadDeviceInfo();

        if (keyStore is null || deviceInfo is null)
        {
            deviceInfo = BotDeviceInfo.GenerateInfo();
            deviceInfo.DeviceName = "windows-camera";
            keyStore = new BotKeystore();

            // 首次登陆
            Bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
                CustomSignProvider = _signer,
            }, deviceInfo, keyStore);

            var (_, codeImg) = Bot.FetchQrCode().Result
                ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            var codeImgFile = new FileInfo("./Images/qrcode.png");
            using var stream = codeImgFile.OpenWrite();
            stream.Write(codeImg);
            stream.Close();

            _logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            Bot.LoginByQrCode().Wait();

            codeImgFile.Delete();

            _storageService.SaveKeyStore(Bot.UpdateKeystore());
            _storageService.SaveDeviceInfo(deviceInfo);
        }
        else
        {
            Bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
                CustomSignProvider = _signer,
            }, deviceInfo, keyStore);
            var logined = Bot.LoginByPassword().Result;
            if (!logined)
            {
                _logger.LogError("Login failed!");
            }
        }

        _storageService.SaveKeyStore(Bot.UpdateKeystore());
    }

    public void Dispose()
    {
        Bot.Dispose();
        GC.SuppressFinalize(this);
    }
}
