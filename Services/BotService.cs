using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common;
using Lagrange.Core.Event;

namespace CameraScreenshotBotService.Services;

public class BotService(
    ILogger<BotService> logger,
    StorageService storageService)
    : IDisposable
{
    public BotContext? Bot { get; private set; }

    public EventInvoker? Invoker => Bot?.Invoker;

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Start login.");

        var keyStore = storageService.LoadKeyStore();
        var deviceInfo = storageService.LoadDeviceInfo();

        if (keyStore is null || deviceInfo is null)
        {
            deviceInfo = BotDeviceInfo.GenerateInfo();
            deviceInfo.DeviceName = "linux-capture";
            keyStore = new();

            // 首次登陆
            Bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
            }, deviceInfo, keyStore);

            var (_, codeImg) = await Bot.FetchQrCode()
                               ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            var codeImgFile = new FileInfo("./Images/qrcode.png");
            await using var stream = codeImgFile.OpenWrite();
            await stream.WriteAsync(codeImg, cancellationToken);
            stream.Close();

            logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            await Bot.LoginByQrCode();
            codeImgFile.Delete();

            storageService.SaveKeyStore(Bot.UpdateKeystore());
            storageService.SaveDeviceInfo(deviceInfo);
        }
        else
        {
            Bot = BotFactory.Create(new()
            {
                GetOptimumServer = true,
                UseIPv6Network = false,
                Protocol = Protocols.Linux,
            }, deviceInfo, keyStore);

            var loggedIn = await Bot.LoginByPassword();

            if (!loggedIn)
            {
                logger.LogError("Login failed!");
            }
        }

        storageService.SaveKeyStore(Bot.UpdateKeystore());
    }

    public void Dispose()
    {
        Bot.Dispose();
        GC.SuppressFinalize(this);
    }
}
