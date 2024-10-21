using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CameraScreenshotBot.Core.Services;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using BotLogLevel = Lagrange.Core.Event.EventArg.LogLevel;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CameraScreenshotBot.Core;

public class Worker(ILogger<Worker> logger,
    CaptureService captureService,
    BotService botService) : BackgroundService
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
        await botService.LoginAsync(stoppingToken);

        botService.Invoker!.OnBotLogEvent += (_, @event) =>
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

        botService.Invoker.OnBotCaptchaEvent += (bot, @event) =>
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

        botService.Invoker.OnBotOnlineEvent += (_, _) =>
        {
            logger.LogInformation("Login Success!");
        };

        botService.Invoker.OnGroupMessageReceived += async (bot, @event) =>
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

        botService.Invoker.OnFriendMessageReceived += async (bot, @event) =>
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
