using Lagrange.Core;
using Lagrange.Core.Event;
using Lagrange.Core.Message;

namespace CameraScreenshotBot.Core.Services.Bot;

public class MessagePipe
{
    public MessageChain? MessageChain { get; set; }

    public MessageBuilder Message { get; set; }

    private readonly BotContext _botContext;

    public void BindToEvent(Func<IEnumerable<EventBase>, IEnumerable<EventBase>> select)
    {
        _botContext.Invoker.OnFriendMessageReceived += (context, @event) =>
        {
            var chain = @event.Chain;

        };
    }

    public MessagePipe ProcessText(Action<MessageChain, MessageBuilder> action)
    {
        if (MessageChain is not null)
            action.Invoke(MessageChain, Message);

        return this;
    }

    public MessagePipe FilterText(Func<string, bool> match)
    {
        if (!match.Invoke(MessageChain?.ToPreviewText() ?? string.Empty))
            MessageChain = null;

        return this;
    }

    public MessagePipe FilterGroup(params uint?[] groups)
    {
        if (!groups.Contains(MessageChain?.GroupUin))
            MessageChain = null;

        return this;
    }

    public MessagePipe FilterFriend(params uint?[] friends)
    {
        if (!friends.Contains(MessageChain?.FriendUin))
            MessageChain = null;

        return this;
    }
}
