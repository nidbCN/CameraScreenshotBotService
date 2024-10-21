using System.IO.IsolatedStorage;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CameraScreenshotBot.Core.Configs;
using CameraScreenshotBot.Core.Services;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Microsoft.Extensions.Options;
using BotLogLevel = Lagrange.Core.Event.EventArg.LogLevel;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CameraScreenshotBot.Core;

public class Worker(ILogger<Worker> logger,
    CaptureService captureService,
    BotContext botCtx,
    IsolatedStorageFile isoStorage,
    IOptions<BotOption> botOptions) : BackgroundService
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs)
    };

    private async Task SendCaptureMessage(MessageBuilder message, BotContext bot)
    {
        try
        {
            var (result, image) = await captureService.CaptureImageAsync();

            if (!result || image is null)
            {
                // 编解码失败
                logger.LogError("Decode failed, send error message.");
                message.Text("杰哥不要！（图像编解码失败）");
            }
            else
            {
                message.Text("开玩笑，我超勇的好不好");
                message.Image(image);
            }
        }
        catch (Exception e)
        {
            logger.LogError("Failed to decode and encode, {error}\n{trace}", e.Message, e.StackTrace);
            message.Text("杰哥不要！（图像编码器崩溃）");
        }
        finally
        {
            var sendTask = bot.SendMessage(message.Build());
            var flushTask = captureService.FlushDecoderBufferAsync(CancellationToken.None);
            await Task.WhenAll(sendTask, flushTask);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loggedIn = await botCtx.LoginByPassword(stoppingToken);
        if (!loggedIn)
        {
            logger.LogWarning("Failed to login by password, try QRCode.");

            var (url, _) = await botCtx.FetchQrCode()
                           ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            var loginTimeoutTokenSrc = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            var link = new UriBuilder("https://util-functions.azurewebsites.net/api/QrCode")
            {
                Query = await new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        {"content", url}
                    }).ReadAsStringAsync(stoppingToken)
            };

            logger.LogInformation("Open link `{url}` and scan the QRCode to login.", link);

            // use external stopping token and a login timeout token.
            using var loginStoppingTokenSrc = CancellationTokenSource
                .CreateLinkedTokenSource(stoppingToken, loginTimeoutTokenSrc.Token);

            await botCtx.LoginByQrCode(loginStoppingTokenSrc.Token);

            // save device info and keystore
            await using var deviceInfoFileStream =  isoStorage.OpenFile(botOptions.Value.DeviceInfoFile, FileMode.OpenOrCreate, FileAccess.Write);
            await JsonSerializer.SerializeAsync(deviceInfoFileStream, botCtx.UpdateDeviceInfo(), cancellationToken: stoppingToken);

            await using var keyFileStream = isoStorage.OpenFile(botOptions.Value.KeyStoreFile, FileMode.OpenOrCreate, FileAccess.Write);
            await JsonSerializer.SerializeAsync(keyFileStream, botCtx.UpdateKeystore(), cancellationToken: stoppingToken);
        }

        botCtx.Invoker.OnBotLogEvent += (_, @event) =>
        {
            using (logger.BeginScope($"[{nameof(Lagrange.Core)}]"))
            {
                logger.Log(@event.Level switch
                {
                    BotLogLevel.Debug => LogLevel.Trace,
                    BotLogLevel.Verbose => LogLevel.Debug,
                    BotLogLevel.Information => LogLevel.Information,
                    BotLogLevel.Warning => LogLevel.Warning,
                    BotLogLevel.Exception => LogLevel.Error,
                    BotLogLevel.Fatal => LogLevel.Critical,
                    _ => throw new NotImplementedException(),
                }, "[{time}]:{msg}", @event.EventTime, @event.EventMessage);
            }
        };

        botCtx.Invoker.OnBotCaptchaEvent += (bot, @event) =>
        {
            logger.LogWarning("Need captcha, url: {msg}", @event.Url);
            logger.LogInformation("Input response json string:");
            var json = Console.ReadLine();

            if (json is null || string.IsNullOrWhiteSpace(json))
            {
                logger.LogError("You input nothing! can't boot.");
                throw new ApplicationException("Can't boot without captcha.");
            }

            try
            {
                var jsonObj = JsonSerializer.Deserialize<IDictionary<string, string>>(json);

                if (jsonObj is null)
                {
                    logger.LogError("Deserialize `{json}` failed, result is null.", json);
                }
                else
                {
                    const string ticket = "ticket";
                    const string randStr = "randstr";

                    if (jsonObj.TryGetValue(ticket, out var ticketValue)
                        && jsonObj.TryGetValue(randStr, out var randStrValue))
                    {
                        logger.LogInformation("Receive captcha, ticket {t}, rand-str {s}", ticketValue, randStrValue);
                        bot.SubmitCaptcha(ticketValue, randStrValue);
                    }
                    else
                    {
                        throw new ApplicationException("Can't boot without captcha.");
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Deserialize failed! str: {s}", json);
                throw;
            }
        };

        botCtx.Invoker.OnBotOnlineEvent += (_, _) =>
        {
            logger.LogInformation("Login Success!");
        };

        botCtx.Invoker.OnGroupMessageReceived += async (bot, @event) =>
        {
            var receivedMessage = @event.Chain;

            logger.LogInformation("Receive group message: {json}",
                JsonSerializer.Serialize(
                    receivedMessage.Select(
                        m => m.ToPreviewString())
                    , _jsonSerializerOptions));

            var textMessages = receivedMessage
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (!textMessages.Any(m => m!.Text.StartsWith("让我看看")))
                return;

            var sendMessage = MessageBuilder.Group(@event.Chain.GroupUin ?? 0);
            await SendCaptureMessage(sendMessage, bot);
        };

        botCtx.Invoker.OnFriendMessageReceived += async (bot, @event) =>
        {
            var receivedMessage = @event.Chain;

            logger.LogInformation("Receive friend message: {json}",
                JsonSerializer.Serialize(
                    receivedMessage.Select(
                        m => m.ToPreviewString())
                    , _jsonSerializerOptions));

            var textMessages = receivedMessage
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (!textMessages.Any(m => m!.Text.StartsWith("让我看看")))
                return;

            var sendMessage = MessageBuilder.Friend(@event.Chain.FriendUin);
            await SendCaptureMessage(sendMessage, bot);
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
