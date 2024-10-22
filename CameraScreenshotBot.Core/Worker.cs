using System.IO.IsolatedStorage;
using System.Text.Json;
using CameraCaptureBot.Core.Configs;
using CameraCaptureBot.Core.Services;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Microsoft.Extensions.Options;
using BotLogLevel = Lagrange.Core.Event.EventArg.LogLevel;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CameraCaptureBot.Core;

public class Worker(ILogger<Worker> logger,
    CaptureService captureService,
    BotContext botCtx,
    IsolatedStorageFile isoStorage,
    IOptions<BotOption> botOptions) : BackgroundService
{
    private async Task SendCaptureMessage(MessageBuilder message, BotContext thisBot)
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
            message.Text("杰哥不要！（图像编解码器崩溃）");
        }
        finally
        {
            var sendTask = thisBot.SendMessage(message.Build());
            var flushTask = captureService.FlushDecoderBufferAsync(CancellationToken.None);
            await Task.WhenAll(sendTask, flushTask);
        }
    }
    private async Task LoginAsync(CancellationToken stoppingToken)
    {
        var loggedIn = await botCtx.LoginByPassword(stoppingToken);
        if (!loggedIn)
        {
            logger.LogWarning("Failed to login by password, try QRCode.");

            var (url, _) = await botCtx.FetchQrCode()
                           ?? throw new ApplicationException(message: "Fetch QRCode failed.");

            // The QrCode will be expired in 2 minutes.
            var loginTimeoutTokenSrc = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var link = new UriBuilder("https://util-functions.azurewebsites.net/api/QrCode")
            {
                Query = await new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        {"content", url}
                    }).ReadAsStringAsync(stoppingToken)
            };

            logger.LogInformation("Open link `{url}` and scan the QRCode to login.", link);

            // Use both external stopping token and login timeout token.
            using var loginStoppingTokenSrc = CancellationTokenSource
                .CreateLinkedTokenSource(stoppingToken, loginTimeoutTokenSrc.Token);

            await botCtx.LoginByQrCode(loginStoppingTokenSrc.Token);
        }

        // save device info and keystore
        await using var deviceInfoFileStream = isoStorage.OpenFile(botOptions.Value.DeviceInfoFile, FileMode.OpenOrCreate, FileAccess.Write);
        await JsonSerializer.SerializeAsync(deviceInfoFileStream, botCtx.UpdateDeviceInfo(), cancellationToken: stoppingToken);

        await using var keyFileStream = isoStorage.OpenFile(botOptions.Value.KeyStoreFile, FileMode.OpenOrCreate, FileAccess.Write);
        await JsonSerializer.SerializeAsync(keyFileStream, botCtx.UpdateKeystore(), cancellationToken: stoppingToken);
    }

    private async Task ProcessMessage(MessageChain recMessage, BotContext thisBot, MessageBuilder sendMessage)
    {
        using (logger.BeginScope(recMessage.GroupUin is null
                   ? "Friend" + recMessage.FriendUin
                   : "Group" + recMessage.GroupUin))
        {
            try
            {
                logger.LogInformation("Received: {msg}", recMessage.ToPreviewText());

                var textMessages = recMessage
                    .Select(m => m as TextEntity)
                    .Where(m => m != null);

                if (!textMessages.Any(m => m!.Text.StartsWith("让我看看")))
                    return;

                await SendCaptureMessage(sendMessage, thisBot);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to process message.");
            }
        }
    }

    private void ConfigureEvents()
    {
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
            logger.LogInformation("Login Success! Bot online.");
        };

        botCtx.Invoker.OnGroupMessageReceived += async (bot, @event) =>
        {
            await ProcessMessage(@event.Chain, bot, MessageBuilder.Group(@event.Chain.GroupUin!.Value));
        };

        botCtx.Invoker.OnFriendMessageReceived += async (bot, @event) =>
        {
            await ProcessMessage(@event.Chain, bot, MessageBuilder.Friend(@event.Chain.FriendUin));
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoginAsync(stoppingToken);
        ConfigureEvents();

        //new ManualResetEvent(false).WaitOne();

    }
}
