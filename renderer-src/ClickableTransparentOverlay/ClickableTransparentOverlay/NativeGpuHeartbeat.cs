namespace ClickableTransparentOverlay
{
    using System;
    using System.Drawing;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Keeps the native compositor owner heartbeat alive independently of the render loop.
    /// </summary>
    internal sealed class NativeGpuHeartbeat : IDisposable
    {
        private readonly object gate = new();
        private readonly string path;
        private readonly Timer timer;
        private readonly Action<string, string> writer;
        private Rectangle bounds;
        private bool menuInput;
        private bool disposed;

        internal NativeGpuHeartbeat(string path, Rectangle initialBounds, TimeSpan interval)
            : this(path, initialBounds, interval, File.WriteAllText)
        {
        }

        internal NativeGpuHeartbeat(
            string path,
            Rectangle initialBounds,
            TimeSpan interval,
            Action<string, string> writer)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Heartbeat path is required.", nameof(path));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            ArgumentNullException.ThrowIfNull(writer);

            this.path = path;
            this.bounds = initialBounds;
            this.writer = writer;
            this.WriteSnapshot();
            this.timer = new Timer(_ => this.WriteSnapshot(), null, interval, interval);
        }

        internal void Update(Rectangle currentBounds, bool wantsMenuInput)
        {
            lock (this.gate)
            {
                if (this.disposed) return;
                this.bounds = currentBounds;
                this.menuInput = wantsMenuInput;
            }

            this.WriteSnapshot();
        }

        public void Dispose()
        {
            lock (this.gate)
            {
                if (this.disposed) return;
                this.disposed = true;
            }

            using var completed = new ManualResetEvent(false);
            if (this.timer.Dispose(completed)) completed.WaitOne();
        }

        private void WriteSnapshot()
        {
            lock (this.gate)
            {
                if (this.disposed) return;
                try
                {
                    this.writer(
                        this.path,
                        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {(this.menuInput ? 1 : 0)} {this.bounds.X} {this.bounds.Y} {this.bounds.Width} {this.bounds.Height}");
                }
                catch
                {
                    // The native compositor retains its last valid observation during transient writes.
                }
            }
        }
    }
}
