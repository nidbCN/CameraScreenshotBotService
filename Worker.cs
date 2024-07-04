using CameraScreenshotBotService.Services;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using LogLevel = Lagrange.Core.Event.EventArg.LogLevel;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger,
    ScreenshotService screenshotService,
    BotService botService) : BackgroundService
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs)
    };

    private async Task SendCaptureMessage(MessageBuilder message)
    {
        try
        {
            var (result, image) = await screenshotService.CapturePngImageAsync();

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
            await botService.Bot.SendMessage(message.Build());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        botService.Invoker.OnBotLogEvent += (_, @event) =>
        {
            switch (@event.Level)
            {
                case LogLevel.Verbose:
                    logger.LogTrace("bot: {msg}", @event.EventMessage);
                    break;
                case LogLevel.Debug:
                    logger.LogDebug("bot: {msg}", @event.EventMessage);
                    break;
                case LogLevel.Information:
                    logger.LogInformation("bot: {msg}", @event.EventMessage);
                    break;
                case LogLevel.Warning:
                    logger.LogWarning("bot: {msg}", @event.EventMessage);
                    break;
                case LogLevel.Exception:
                    logger.LogError("bot: {msg}", @event.EventMessage);
                    break;
                case LogLevel.Fatal:
                    logger.LogCritical("bot: {msg}", @event.EventMessage);
                    break;
                default:
                    logger.LogWarning("bot, unknown level: {msg}", @event.EventMessage);
                    break;
            }
        };

        botService.Invoker.OnBotCaptchaEvent += (_, @event) =>
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
                var jsonObj = JsonSerializer.Deserialize<IDictionary<string, string>>(json!);

                if (jsonObj is null)
                {
                    logger.LogError("Deserialize result is null.");
                }
                else
                {
                    const string TICKET = "ticket";
                    const string RAND_STR = "randstr";

                    if (jsonObj.TryGetValue(TICKET, out var ticket) && jsonObj.TryGetValue(RAND_STR, out var randstr))
                    {
                        logger.LogInformation("Recv captcha, ticket {t}, randstr {s}", ticket, randstr);
                        botService.Bot.SubmitCaptcha(ticket, randstr);
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

        botService.Invoker.OnBotOnlineEvent += (_, @event) =>
        {
            logger.LogInformation("Login Success!");
        };

        botService.Invoker.OnGroupMessageReceived += async (_, @event) =>
        {
            var receivedMessage = @event.Chain;

            logger.LogInformation("Receive group message: {json}",
                JsonSerializer.Serialize(receivedMessage.Select(m => m.ToPreviewString())
                    , _jsonSerializerOptions));

            var textMessages = receivedMessage
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text.StartsWith("让我看看") ?? false))
            {
                var sendMessage = MessageBuilder.Group(@event.Chain.GroupUin ?? 0);

                await SendCaptureMessage(sendMessage);
            }
        };

        botService.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            var receivedMessage = @event.Chain;

            logger.LogInformation("Receive group message: {json}",
                JsonSerializer.Serialize(receivedMessage.Select(m => m.ToPreviewString())
                    , _jsonSerializerOptions));

            var textMessages = receivedMessage
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text.StartsWith("让我看看") ?? false))
            {
                var sendMessage = MessageBuilder.Friend(@event.Chain.FriendUin);
                await SendCaptureMessage(sendMessage);
            }
        };

        await Task.Delay(200, stoppingToken);
    }
}

