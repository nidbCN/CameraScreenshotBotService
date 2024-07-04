using CameraScreenshotBotService.Services;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using System.Text.Json;

namespace CameraScreenshotBotService;

public class Worker(ILogger<Worker> logger, ScreenshotService screenshotService, BotService botService) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ScreenshotService _screenshotService = screenshotService;
    private readonly BotService _botService = botService;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _botService.Invoker.OnBotLogEvent += (_, @event) =>
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

        _botService.Invoker.OnBotCaptchaEvent += (_, @event) =>
        {
            _logger.LogWarning("Need captcha, url: {msg}", @event.Url);
            _logger.LogInformation("Input response json string:");
            var json = Console.ReadLine();
            if (json is null || string.IsNullOrWhiteSpace(json))
            {
                _logger.LogError("You input nothing! can't boot.");
                throw new ApplicationException("Can't boot without captcha.");
            }

            try
            {
                var jsonObj = JsonSerializer.Deserialize<IDictionary<string, string>>(json!);

                if (jsonObj is null)
                {
                    _logger.LogError("Deserialize result is null.");
                }
                else
                {
                    const string TICKET = "ticket";
                    const string RAND_STR = "randstr";

                    if (jsonObj.TryGetValue(TICKET, out var ticket) && jsonObj.TryGetValue(RAND_STR, out var randstr))
                    {
                        _logger.LogInformation("Recv captcha, ticket {t}, randstr {s}", ticket, randstr);
                        _botService.Bot.SubmitCaptcha(ticket, randstr);
                    }
                    else
                    {
                        throw new ApplicationException("Can't boot without captcha.");
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Deserialize failed! str: {s}", json);
                throw;
            }
        };

        _botService.Invoker.OnBotOnlineEvent += (_, @event) =>
        {
            _logger.LogInformation("Login Success!");
        };

        _botService.Invoker.OnGroupMessageReceived += async (_, @event) =>
        {
            var recvMessages = @event.Chain;

            _logger.LogInformation("Receive group message: {json}",
                JsonSerializer.Serialize(recvMessages.Select(m => m.ToPreviewString())));

            var textMessages = recvMessages
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text.StartsWith("让我看看！") ?? false))
            {
                var sendMessage = MessageBuilder.Group(@event.Chain.GroupUin ?? 0);

                try
                {
                    var cacheTime = DateTime.Now - _screenshotService.CacheTime;

                    if (_screenshotService.CacheImage != null
                        && cacheTime < TimeSpan.FromSeconds(10))
                    {
                        _logger.LogInformation("Send cached image from {time} ago.",
                            cacheTime);
                        sendMessage.Image(_screenshotService.CacheImage);
                    }
                    else
                    {
                        do
                        {
                            if (screenshotService.IsDecoding) continue;

                            var captureResult = _screenshotService.TryCapturePngImage(out var imageBytes);
                            if (!captureResult || imageBytes is null)
                            {
                                _logger.LogError("Decode failed, send error message.");
                                sendMessage.Text("杰哥不要！（图像编解码失败）");
                            }
                            else
                            {
                                sendMessage.Image(imageBytes);
                            }

                            break;
                        } while (!stoppingToken.IsCancellationRequested);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Failed to decode and encode, {error}\n{trace}", e.Message, e.StackTrace);
                    sendMessage.Text("杰哥不要！（图像编码器崩溃）");
                }
                finally
                {
                    await _botService.Bot.SendMessage(sendMessage.Build());
                }
            }
        };

        _botService.Invoker.OnFriendMessageReceived += async (_, @event) =>
        {
            var recvMessages = @event.Chain;

            _logger.LogInformation("Receive friend message: {json}",
                JsonSerializer.Serialize(recvMessages.Select(m => m.ToPreviewString())));

            var textMessages = recvMessages
                .Select(m => m as TextEntity)
                .Where(m => m != null);

            if (textMessages.Any(m => m?.Text.StartsWith("让我看看") ?? false))
            {
                var sendMessage = MessageBuilder.Friend(@event.Chain.FriendUin);

                try
                {
                    var cacheTime = DateTime.Now - _screenshotService.CacheTime;

                    if (_screenshotService.CacheImage != null
                        && cacheTime < TimeSpan.FromSeconds(10))
                    {
                        _logger.LogInformation("Send cached image from {time} ago.",
                            cacheTime);
                        sendMessage.Image(_screenshotService.CacheImage);
                    }
                    else
                    {
                        do
                        {
                            if (screenshotService.IsDecoding) continue;

                            var captureResult = _screenshotService.TryCapturePngImage(out var imageBytes);
                            if (!captureResult || imageBytes is null)
                            {
                                _logger.LogError("Decode failed, send error message.");
                                sendMessage.Text("杰哥不要！（图像编解码失败）");
                            }
                            else
                            {
                                sendMessage.Image(imageBytes);
                            }

                            break;
                        } while (!stoppingToken.IsCancellationRequested);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError("Failed to decode and encode, {error}\n{trace}", e.Message, e.StackTrace);
                    sendMessage.Text("杰哥不要！（图像编解码器崩溃）");
                }
                finally
                {
                    await _botService.Bot.SendMessage(sendMessage.Build());
                }
            }
        };

        await Task.Delay(200, stoppingToken);
    }
}

