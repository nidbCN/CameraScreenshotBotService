using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lagrange.Core.Message;

namespace CameraScreenshotBotService.Services.Bot;

public class MessagePipe
{
    public MessageChain MessageChain { get; set; }
    public MessageBuilder Message { get; set; }

    public MessagePipe PreProcessAllText(Action<MessageChain, MessageBuilder> action)
    {
        action.Invoke(MessageChain, Message);
        return this;
    }

    public MessagePipe SelectText(Func<MessageChain, bool> invoke)
    {
        return this;
    }

    public MessagePipe SelectGroup()
    {
        return this;
    }
}
