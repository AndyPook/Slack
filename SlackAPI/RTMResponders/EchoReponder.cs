﻿using System;
using System.Threading.Tasks;

using Pook.SlackAPI.RTMMessages;

namespace Pook.SlackAPI.RTMResponders
{
    public class EchoReponder : IMessageResponder
    {
        public bool CanRespond(Message message)
        {
            return message.text.StartsWith("echo", StringComparison.OrdinalIgnoreCase);
        }

        public Task Respond(ISlackSocket socket, Message message)
        {
            socket.Send(message.Reply("ECHO: " + message.text.Replace("echo", string.Empty)));

            return Task.FromResult(0);
        }
    }
}