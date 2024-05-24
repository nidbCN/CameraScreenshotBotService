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
            var jsonObj = JsonSerializer.Deserialize<IDictionary<string, string>>(json);

            if (jsonObj?.ContainsKey("ticket") != null && jsonObj?.ContainsKey("randstr") != null)
                _botService.Bot.SubmitCaptcha(jsonObj["ticket"], jsonObj["randstr"]);

        };

        _botService.Invoker.OnBotOnlineEvent += (_, @event) =>
        {
            _logger.LogInformation("Login Success!");
        };

        _botService.Invoker.OnGroupMessageReceived += async (_, @event) =>
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
                    await _botService.Bot.SendMessage(sendMessage.Build());
                }
            }
        };

        await Task.Delay(1000, stoppingToken);
    }
}

