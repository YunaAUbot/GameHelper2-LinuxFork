namespace ClickableTransparentOverlay
{
    using System;
    using System.Diagnostics;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    // Best-effort authenticated loopback transport. Connection and authentication
    // work stay off the render thread; frames are dropped until the next ready link.
    internal sealed class NativeGpuTransport : IDisposable
    {
        private readonly object gate = new();
        private readonly int port;
        private readonly byte[] authentication;
        private TcpClient client;
        private NetworkStream stream;
        private DateTime nextConnectAttempt = DateTime.MinValue;
        private bool ready;
        private bool reconnecting;
        private bool disposed;
        private int expectedIncoming = -1;
        private byte[] incoming;
        private int incomingCount;

        internal NativeGpuTransport(int port, byte[] authentication)
        {
            this.port = port;
            this.authentication = authentication;
        }

        internal bool IsConnected
        {
            get
            {
                lock (this.gate)
                {
                    return this.ready && this.stream is not null && this.client?.Connected == true;
                }
            }
        }

        internal bool WaitForReady(TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                StartReconnectIfNeeded();
                if (this.IsConnected) return true;
                Thread.Sleep(10);
            }

            return false;
        }

        internal bool TrySend(byte[] frame)
        {
            var reconnect = false;
            lock (this.gate)
            {
                if (!this.ready || this.stream is null)
                {
                    reconnect = true;
                }
                else
                {
                    try
                    {
                        var length = BitConverter.GetBytes(frame.Length);
                        this.stream.Write(length, 0, length.Length);
                        this.stream.Write(frame, 0, frame.Length);
                        return true;
                    }
                    catch
                    {
                        DisposeConnectionLocked();
                        reconnect = true;
                    }
                }
            }

            if (reconnect) StartReconnectIfNeeded();
            return false;
        }

        internal bool TryReadEvent(out uint kind, out int code, out bool down, out uint value, out uint value2)
        {
            kind = 0; code = 0; down = false; value = 0; value2 = 0;
            var reconnect = false;
            lock (this.gate)
            {
                try
                {
                    if (!this.ready || this.stream is null || this.client is null || this.client.Available == 0) return false;
                    if (this.expectedIncoming < 0)
                    {
                        if (this.client.Available < 4) return false;
                        var length = new byte[4]; this.stream.ReadExactly(length);
                        this.expectedIncoming = BitConverter.ToInt32(length, 0);
                        if (this.expectedIncoming != 20) { this.expectedIncoming = -1; return false; }
                        this.incoming = new byte[this.expectedIncoming]; this.incomingCount = 0;
                    }

                    var available = Math.Min(this.client.Available, this.expectedIncoming - this.incomingCount);
                    if (available > 0) this.incomingCount += this.stream.Read(this.incoming, this.incomingCount, available);
                    if (this.incomingCount != this.expectedIncoming) return false;
                    var message = this.incoming; this.expectedIncoming = -1; this.incoming = null;
                    kind = BitConverter.ToUInt32(message, 0);
                    if (kind != NativeGpuFrameProtocol.MouseInputMagic &&
                        kind != NativeGpuFrameProtocol.KeyInputMagic &&
                        kind != NativeGpuFrameProtocol.NativeTextureAckMagic) return false;
                    code = BitConverter.ToInt32(message, 4);
                    down = BitConverter.ToUInt32(message, 8) != 0;
                    value = BitConverter.ToUInt32(message, 12);
                    value2 = BitConverter.ToUInt32(message, 16);
                    return true;
                }
                catch
                {
                    DisposeConnectionLocked();
                    reconnect = true;
                }
            }

            if (reconnect) StartReconnectIfNeeded();
            return false;
        }

        private void StartReconnectIfNeeded()
        {
            lock (this.gate)
            {
                if (this.disposed || this.ready || this.reconnecting || DateTime.UtcNow < this.nextConnectAttempt) return;
                this.reconnecting = true;
                this.nextConnectAttempt = DateTime.UtcNow.AddMilliseconds(500);
            }

            _ = Task.Run(this.ConnectAndAuthenticate);
        }

        private void ConnectAndAuthenticate()
        {
            TcpClient? candidate = null;
            NetworkStream? candidateStream = null;
            try
            {
                candidate = new TcpClient { NoDelay = true, SendTimeout = 10, ReceiveTimeout = 1000 };
                var connect = candidate.ConnectAsync("127.0.0.1", this.port);
                if (!connect.Wait(1000)) return;
                candidateStream = candidate.GetStream();
                var auth = new byte[8 + this.authentication.Length];
                BitConverter.GetBytes(auth.Length - 4).CopyTo(auth, 0);
                BitConverter.GetBytes(NativeGpuFrameProtocol.AuthMagic).CopyTo(auth, 4);
                this.authentication.CopyTo(auth, 8);
                candidateStream.Write(auth, 0, auth.Length);
                var response = new byte[4];
                candidateStream.ReadExactly(response);
                if (BitConverter.ToUInt32(response, 0) != NativeGpuFrameProtocol.ReadyMagic) return;

                lock (this.gate)
                {
                    if (this.disposed) return;
                    DisposeConnectionLocked();
                    this.client = candidate;
                    this.stream = candidateStream;
                    this.ready = true;
                    candidate = null;
                    candidateStream = null;
                }
            }
            catch
            {
            }
            finally
            {
                candidateStream?.Dispose();
                candidate?.Dispose();
                lock (this.gate)
                {
                    this.reconnecting = false;
                }
            }
        }

        private void DisposeConnectionLocked()
        {
            this.stream?.Dispose(); this.stream = null;
            this.client?.Dispose(); this.client = null;
            this.ready = false;
            this.expectedIncoming = -1; this.incoming = null; this.incomingCount = 0;
        }

        public void Dispose()
        {
            lock (this.gate)
            {
                this.disposed = true;
                DisposeConnectionLocked();
            }
        }
    }
}
