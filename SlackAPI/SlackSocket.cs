﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using Pook.SlackAPI.RTMMessages;

namespace Pook.SlackAPI
{
    public class SlackSocket : ISlackSocket
    {
        private static UnixDateTimeConverter unixDateTimeConverter = new UnixDateTimeConverter();
        private readonly Dictionary<string, SlackEventHandler> handlers = new Dictionary<string, SlackEventHandler>();
        private readonly List<IMessageResponder> responders = new List<IMessageResponder>();

        public SlackSocket(string token, string baseAddress = null, IWebSocket webSocket = null)
        {
            cts = new CancellationTokenSource();
            socket = webSocket ?? new NetWebSocket(cts.Token);
            currentId = 1;
            API = new SlackAPI(token, baseAddress);
        }

        readonly CancellationTokenSource cts;
        private readonly ConcurrentDictionary<int, Action<ISlackSocket, Message, SlackUser>> callbacks = new ConcurrentDictionary<int, Action<ISlackSocket, Message, SlackUser>>();
        private QueueHandler<string> sendQueue;
        private QueueHandler<string> handlerQueue;
        int closedEmitted;
        private readonly IWebSocket socket;
        int currentId;

        public SlackState State { get; private set; }
        public ISlackAPI API { get; }

        public event Action Connected;
        public event Action<WebSocketException> ErrorSending;
        public event Action<WebSocketException> ErrorReceiving;
        public event Action ConnectionClosed;
        public bool IsConnected => socket.IsConnected;

        public IReadOnlyCollection<IMessageResponder> Responders => responders;
        public Action<ISlackSocket, Message, SlackUser> DefaultResponder { get; set; }

        public SlackSocket AddEventHandler(Type eventHandlerType)
        {
            bool isHandler = eventHandlerType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>));
            if (!isHandler)
                return this;
            foreach (var route in eventHandlerType.GetCustomAttributes<SlackSocketRoutingAttribute>())
                handlers.Add(route.Type, new SlackEventHandler(eventHandlerType));

            return this;
        }
        public SlackSocket AddResponder(IMessageResponder responder)
        {
            responders.Add(responder);
            return this;
        }

        public async Task Login()
        {
            Debug.WriteLine("rtm.start");
            var login = await API.GetRtmLogin();
            Debug.WriteLine("RTM Login: " + login.url);
            State = new SlackState(login);
            handlerQueue = QueueHandler<string>.StartWithAction(HandleIncoming, cts.Token);
            sendQueue = QueueHandler<string>.StartWithAsync(SendItem, cts.Token);
            await socket.ConnectAsync(new Uri(login.url));
            Debug.WriteLine("RTM: connected");
            Connected?.Invoke();

            socket.MessageReceived += msg =>
             {
                 Debug.WriteLine("received: " + msg);
                 handlerQueue.Add(msg);
             };
        }

        public void Send(Message message, Action<ISlackSocket, Message, SlackUser> callback)
        {
            message.id = Interlocked.Increment(ref currentId);
            callbacks.TryAdd(message.id, callback);

            Send(message);
        }

        public void Send(SlackSocketMessage message)
        {
            if (message.id == 0)
                message.id = Interlocked.Increment(ref currentId);
            if (string.IsNullOrEmpty(message.type))
            {
                var route = message.GetType().GetCustomAttributes<SlackSocketRoutingAttribute>().LastOrDefault();
                if (route == null)
                    throw new InvalidProgramException("Cannot send without a proper route!");
                message.type = route.Type;
            }

            sendQueue.Add(JsonConvert.SerializeObject(message, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        private void HandleIncoming(string data)
        {
            // deserialize to Messge
            SlackSocketMessage message;
            try
            {
                message = JsonConvert.DeserializeObject<SlackSocketMessage>(data, new UnixDateTimeConverter());
            }
            catch (JsonSerializationException)
            {
                Debug.WriteLine($"ERROR: bad json: " + data);
                return;
            }

            if (message == null)
                return;
            // check Slack says ok
            if (!message.ok)
            {
                if (message.error == null)
                    Debug.WriteLine($"ERROR: data");
                else
                    Debug.WriteLine($"ERROR: {message.error.code} - {message.error.msg} = {data}");
                return;
            }

            // handle Message
            try
            {
                if (string.IsNullOrEmpty(message.type))
                {
                    if (message.reply_to == 0)
                    {
                        Debug.WriteLine("Message with no Type and not reply_to: " + data);
                        return;
                    }

                    message.type = "reply_to";
                }

                if (message.type == "message")
                    HandleMessage(data);
                else
                {
                    SlackEventHandler handler;
                    if (handlers.TryGetValue(message.type, out handler))
                        handler.Handle(this, data);
                    else
                        Debug.WriteLine("No handler for " + message.type);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Handler failed: " + ex.Message);
            }
        }

        public void HandleMessage(string data)
        {
            var message = JsonConvert.DeserializeObject<Message>(data, unixDateTimeConverter);
            if (string.IsNullOrEmpty(message?.text))
            {
                Debug.WriteLine("Empty message");
                return;
            }

            Debug.WriteLine("Message: " + message.text);

            var user = State.GetUser(message.user);

            // is it a callback?
            Action<ISlackSocket, Message, SlackUser> callback;
            if (message.reply_to != 0 && callbacks.TryGetValue(message.reply_to, out callback))
            {
                try
                {
                    callback(this, message, user);
                    callbacks.TryRemove(message.reply_to, out callback);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Callback failed: " + ex.Message);
                }

                return;
            }

            bool handled = false;
            foreach (var responder in Responders)
            {
                if (responder.CanRespond(message, user))
                {
                    responder.Respond(this, message, user);
                    handled = true;
                }
            }
            if (!handled && DefaultResponder != null)
                DefaultResponder(this, message, user);
        }

        private async Task SendItem(string message)
        {
            if (cts.Token.IsCancellationRequested)
                return;
            byte[] sending = Encoding.UTF8.GetBytes(message);
            var buffer = new ArraySegment<byte>(sending);
            try
            {
                Debug.WriteLine("sending: " + message);
                await socket.SendAsync(message);
            }
            catch (WebSocketException wex)
            {
                ErrorSending?.Invoke(wex);
                Close();
            }
        }

        public void Close()
        {
            try
            {
                cts.Cancel();
                socket.Abort();
            }
            catch (Exception)
            {
                // ignored
            }

            if (Interlocked.CompareExchange(ref closedEmitted, 1, 0) == 0)
                ConnectionClosed?.Invoke();
        }


        private class SlackEventHandler
        {
            public SlackEventHandler(Type handlerType)
            {
                var handlerIf = handlerType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>));
                if (handlerIf == null)
                    throw new ArgumentException("handlerType does not implement IEventHandler<T>");
                messageType = handlerIf.GetGenericArguments()[0];
                handlerMethod = handlerType.GetMethod("Handle", new[] { typeof(ISlackSocket), messageType });
                handler = Activator.CreateInstance(handlerType);
            }

            private readonly object handler;
            private readonly Type messageType;
            private readonly MethodInfo handlerMethod;

            public void Handle(ISlackSocket socket, string data)
            {
                object message = JsonConvert.DeserializeObject(data, messageType, unixDateTimeConverter);
                handlerMethod.Invoke(handler, new[] { socket, message });
            }
        }
    }

    public class SlackSocketMessage
    {
        public bool ok = true;
        public Error error;
        public int id;
        public int reply_to;
        public string type;
    }

    public class Error
    {
        public int code;
        public string msg;
    }
}