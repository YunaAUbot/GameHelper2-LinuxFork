namespace ClickableTransparentOverlay
{
    using System;
    using System.Diagnostics;
    using System.Net.Sockets;

    // Best-effort loopback transport. A failed/slow native renderer never
    // blocks GameHelper2's render thread: frames are simply dropped until the
    // next reconnect attempt.
    internal sealed class NativeGpuTransport : IDisposable
    {
        private readonly int port;
        private readonly byte[] authentication;
        private TcpClient client;
        private NetworkStream stream;
        private DateTime nextConnectAttempt = DateTime.MinValue;
        private int expectedIncoming = -1;
        private byte[] incoming;
        private int incomingCount;

        internal NativeGpuTransport(int port, byte[] authentication)
        {
            this.port = port;
            this.authentication = authentication;
        }

        internal bool IsConnected => this.stream is not null && this.client?.Connected == true;

        internal bool WaitForReady(TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                if (!EnsureConnected()) { System.Threading.Thread.Sleep(10); continue; }
                try
                {
                    var auth = new byte[8 + this.authentication.Length];
                    BitConverter.GetBytes(auth.Length - 4).CopyTo(auth, 0);
                    BitConverter.GetBytes(NativeGpuFrameProtocol.AuthMagic).CopyTo(auth, 4);
                    this.authentication.CopyTo(auth, 8);
                    this.stream.Write(auth, 0, auth.Length);
                    var ready = new byte[4];
                    this.stream.ReadExactly(ready);
                    if (BitConverter.ToUInt32(ready, 0) == NativeGpuFrameProtocol.ReadyMagic) return true;
                }
                catch { }
                DisposeConnection();
            }
            return false;
        }

        internal bool TrySend(byte[] frame)
        {
            try
            {
                if (!EnsureConnected()) return false;
                var length = BitConverter.GetBytes(frame.Length);
                this.stream.Write(length, 0, length.Length);
                this.stream.Write(frame, 0, frame.Length);
                return true;
            }
            catch
            {
                DisposeConnection();
                return false;
            }
        }

        internal bool TryReadEvent(out uint kind, out int code, out bool down, out uint value, out uint value2)
        {
            kind = 0; code = 0; down = false; value = 0; value2 = 0;
            try
            {
                if (this.stream is null || this.client.Available == 0) return false;
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
            catch { DisposeConnection(); return false; }
        }

        private bool EnsureConnected()
        {
            if (this.stream is not null) return true;
            if (DateTime.UtcNow < this.nextConnectAttempt) return false;
            this.nextConnectAttempt = DateTime.UtcNow.AddMilliseconds(500);
            var candidate = new TcpClient { NoDelay = true, SendTimeout = 100, ReceiveTimeout = 100 };
            try
            {
                var connect = candidate.ConnectAsync("127.0.0.1", this.port);
                if (!connect.Wait(10)) { candidate.Dispose(); return false; }
                this.client = candidate;
                this.stream = candidate.GetStream();
                return true;
            }
            catch { candidate.Dispose(); return false; }
        }

        private void DisposeConnection()
        {
            this.stream?.Dispose(); this.stream = null;
            this.client?.Dispose(); this.client = null;
            this.expectedIncoming = -1; this.incoming = null; this.incomingCount = 0;
        }

        public void Dispose() => DisposeConnection();
    }
}
