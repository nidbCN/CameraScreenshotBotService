using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Event;

namespace CameraScreenshotBot.Core.Services;
public class BotService(
    ILogger<BotService> logger,
    IsoStoreService isoStoreService)
    : IDisposable
{
    public BotContext? Bot { get; private set; }

    public EventInvoker? Invoker => Bot?.Invoker;

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var botConfig = new BotConfig
        {
            GetOptimumServer = true,
            UseIPv6Network = false,
            Protocol = Protocols.Linux,
        };

        logger.LogDebug("Start login.");

        var keyStore = isoStoreService.LoadKeyStore();
        var deviceInfo = isoStoreService.LoadDeviceInfo();

        if (keyStore is null || deviceInfo is null)
        {
            deviceInfo = BotDeviceInfo.GenerateInfo();
            deviceInfo.DeviceName = "linux-capture";
            keyStore = new();

            // 首次登陆
            Bot = BotFactory.Create(botConfig, deviceInfo, keyStore);

            var (url, _) = await Bot.FetchQrCode()
                               ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            var link = new UriBuilder("https://util-functions.azurewebsites.net/api/QrCode")
            {
                Query = await new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        {"content", url}
                    }).ReadAsStringAsync(cancellationToken)
            };

            logger.LogInformation("Open link '{url}' and scan the QRCode to login.", link);

            //var codeImgFile = new FileInfo("./Images/qrcode.png");
            //await using var stream = codeImgFile.OpenWrite();
            //await stream.WriteAsync(codeImg, cancellationToken);
            //stream.Close();

            //logger.LogInformation("Scan QRCode to login. QRCode image has been saved to {path}.", codeImgFile.FullName);

            await Bot.LoginByQrCode().WaitAsync(cancellationToken);
            //codeImgFile.Delete();

            isoStoreService.SaveKeyStore(Bot.UpdateKeystore());
            isoStoreService.SaveDeviceInfo(deviceInfo);
        }
        else
        {
            Bot = BotFactory.Create(botConfig, deviceInfo, keyStore);

            if (!await Bot.LoginByPassword())
            {
                logger.LogError("Login failed!");
            }
        }

        isoStoreService.SaveKeyStore(Bot.UpdateKeystore());
    }

    public void Dispose()
    {
        Bot?.Dispose();
        GC.SuppressFinalize(this);
    }


}
