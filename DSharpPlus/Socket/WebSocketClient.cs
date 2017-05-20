﻿using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DSharpPlus
{
    public class WebSocketClient : BaseWebSocketClient
    {
        private const int BUFFER_SIZE = 32768;

        private static UTF8Encoding UTF8 { get; set; }
        
        private ConcurrentQueue<string> SocketMessageQueue { get; set; }
        private CancellationTokenSource TokenSource { get; set; }
        private CancellationToken Token => this.TokenSource.Token;

        private ClientWebSocket Socket { get; set; }
        private Task WsListener { get; set; }
        private Task SocketQueueManager { get; set; }

        #region Events
        public override event AsyncEventHandler OnConnect
        {
            add { this._on_connect.Register(value); }
            remove { this._on_connect.Unregister(value); }
        }
        private AsyncEvent _on_connect;

        public override event AsyncEventHandler OnDisconnect
        {
            add { this._on_disconnect.Register(value); }
            remove { this._on_disconnect.Unregister(value); }
        }
        private AsyncEvent _on_disconnect;

        public override event AsyncEventHandler<WebSocketMessageEventArgs> OnMessage
        {
            add { this._on_message.Register(value); }
            remove { this._on_message.Unregister(value); }
        }
        private AsyncEvent<WebSocketMessageEventArgs> _on_message;
        #endregion

        public WebSocketClient()
        {
            this._on_connect = new AsyncEvent(this.EventErrorHandler, "WS_CONNECT");
            this._on_disconnect = new AsyncEvent(this.EventErrorHandler, "WS_DISCONNECT");
            this._on_message = new AsyncEvent<WebSocketMessageEventArgs>(this.EventErrorHandler, "WS_MESSAGE");
        }

        static WebSocketClient()
        {
            UTF8 = new UTF8Encoding(false);
        }

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <returns></returns>
        public static new WebSocketClient Create()
        {
            return new WebSocketClient();
        }

        /// <summary>
        /// Connects to the WebSocket server.
        /// </summary>
        /// <param name="uri">The URI of the WebSocket server.</param>
        /// <returns></returns>
        public override async Task<BaseWebSocketClient> ConnectAsync(string uri)
        {
            this.SocketMessageQueue = new ConcurrentQueue<string>();
            this.TokenSource = new CancellationTokenSource();

            await InternalConnectAsync(new Uri(uri));
            return this;
        }

        /// <summary>
        /// Set the Action to call when the connection has been established.
        /// </summary>
        /// <returns></returns>
        public override async Task<BaseWebSocketClient> OnConnectAsync()
        {
            await _on_connect.InvokeAsync();
            return this;
        }

        /// <summary>
        /// Set the Action to call when the connection has been terminated.
        /// </summary>
        /// <returns></returns>
        public override async Task<BaseWebSocketClient> OnDisconnectAsync()
        {
            await _on_disconnect.InvokeAsync();
            return this;
        }

        /// <summary>
        /// Send a message to the WebSocket server.
        /// </summary>
        /// <param name="message">The message to send</param>
        public override void SendMessage(string message)
        {
            SendMessageAsync(message);
        }

        internal void SendMessageAsync(string message)
        {
            if (Socket.State != WebSocketState.Open)
                return;

            this.SocketMessageQueue.Enqueue(message);
        }

        internal async Task InternalConnectAsync(Uri uri)
        {
            try
            {
                this.Socket = new ClientWebSocket();
                this.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                await Socket.ConnectAsync(uri, this.Token);
                await CallOnConnectedAsync();
                this.WsListener = Task.Run(this.Listen, this.Token);
                this.SocketQueueManager = Task.Run(this.SmqTask, this.Token);
            }
            catch (Exception) { }
        }

        public override async Task InternalDisconnectAsync()
        {
            if (this.Socket.State != WebSocketState.Open)
                return;
            
            try
            {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", this.Token);
                Socket.Abort();
                Socket.Dispose();
            }
            catch (Exception)
            { }
            finally
            {
                await CallOnDisconnectedAsync();
            }
        }

        internal async Task Listen()
        {
            await Task.Yield();

            var buff = new byte[BUFFER_SIZE];
            var buffseg = new ArraySegment<byte>(buff);
            var rsb = new StringBuilder();
            var result = (WebSocketReceiveResult)null;
            
            try
            {
                while (!this.Token.IsCancellationRequested && this.Socket.State == WebSocketState.Open)
                {
                    do
                    {
                        result = await this.Socket.ReceiveAsync(buffseg, this.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new WebSocketException("Server requested the connection to be terminated.");
                        else
                            rsb.Append(UTF8.GetString(buff, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    await this.CallOnMessageAsync(rsb.ToString());
                    rsb.Clear();
                }
            }
            catch (Exception) { }

            await InternalDisconnectAsync();
        }

        internal async Task SmqTask()
        {
            while (!this.Token.IsCancellationRequested && this.Socket.State == WebSocketState.Open)
            {
                if (!this.SocketMessageQueue.TryDequeue(out var message))
                    continue;

                var buff = UTF8.GetBytes(message);
                var msgc = buff.Length / BUFFER_SIZE;
                if (buff.Length % BUFFER_SIZE != 0)
                    msgc++;

                for (var i = 0; i < msgc; i++)
                {
                    var off = BUFFER_SIZE * i;
                    var cnt = Math.Min(BUFFER_SIZE, buff.Length - off);

                    var lm = i == msgc - 1;
                    await Socket.SendAsync(new ArraySegment<byte>(buff, off, cnt), WebSocketMessageType.Text, lm, this.Token);
                }
            }
        }

        internal async Task CallOnMessageAsync(string result)
        {
            await _on_message.InvokeAsync(new WebSocketMessageEventArgs() { Message = result });
        }

        internal async Task CallOnDisconnectedAsync()
        {
            await _on_disconnect.InvokeAsync();
        }

        internal async Task CallOnConnectedAsync()
        {
            await _on_connect.InvokeAsync();
        }

        private void EventErrorHandler(string evname, Exception ex)
        {
            Console.WriteLine($"WSERROR: {ex.GetType()} in {evname}!");
        }
    }
}