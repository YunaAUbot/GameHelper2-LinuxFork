namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Numerics;
    using System.Threading;
    using System.Threading.Tasks;

    // Shared across all key types: at most one Radar path batch consumes CPU at a time.
    internal static class PathWorkLimiter
    {
        internal static readonly SemaphoreSlim Gate = new(1, 1);
    }

    /// <summary>Render-thread-owned cache with cancellable, immutable worker snapshots.</summary>
    internal sealed class PathWorkCache<TKey> where TKey : notnull
    {
        private sealed record Entry(Vector2 Target, Vector2 Origin, int Doors,
            List<Vector2>? Path, long Attempt, long Full);

        private Dictionary<TKey, Entry> entries = new();
        private CancellationTokenSource cancellation = new();
        private Task<Dictionary<TKey, Entry>>? pending;
        public bool IsBusy => this.pending != null && !this.pending.IsCompleted;
        public Dictionary<TKey, List<Vector2>?> Paths { get; private set; } = new();

        public void Pump()
        {
            if (this.pending == null || !this.pending.IsCompleted) return;
            if (this.pending.IsCompletedSuccessfully)
            {
                this.entries = this.pending.Result;
                this.Paths = this.entries.ToDictionary(kv => kv.Key, kv => kv.Value.Path);
            }
            else { _ = this.pending.Exception; }
            this.pending = null;
        }

        public void Reset()
        {
            var previous = this.cancellation;
            previous.Cancel();
            if (this.pending is { } task)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; previous.Dispose(); }, TaskScheduler.Default);
            }
            else previous.Dispose();
            this.cancellation = new();
            this.pending = null;
            this.entries = new();
            this.Paths = new();
        }

        public void Schedule(IEnumerable<(TKey Key, Vector2 Position)> targets, Vector2 origin,
            int doors, int segments, int fullInterval,
            Func<Vector2, List<Vector2>?, int, CancellationToken, List<Vector2>?> compute)
        {
            this.Pump();
            if (this.pending != null) return;
            var snapshot = targets.GroupBy(t => t.Key).Select(g => g.First()).ToArray();
            var previous = this.entries;
            var token = this.cancellation.Token;
            this.pending = Task.Run(async () =>
            {
                await PathWorkLimiter.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var result = new Dictionary<TKey, Entry>();
                    foreach (var target in snapshot)
                        if (previous.TryGetValue(target.Key, out var entry)) result[target.Key] = entry;

                    // Oldest attempted first prevents distant targets starving behind nearby ones.
                    var ordered = snapshot.OrderBy(t => previous.TryGetValue(t.Key, out var e) ? e.Attempt : 0)
                        .ThenBy(t => Vector2.DistanceSquared(origin, t.Position));
                    var timer = Stopwatch.StartNew();
                    var completed = 0;
                    foreach (var target in ordered)
                    {
                        token.ThrowIfCancellationRequested();
                        var now = Environment.TickCount64;
                        previous.TryGetValue(target.Key, out var old);
                        bool changed = old == null || old.Target != target.Position || old.Doors != doors;
                        if (!changed && old!.Path == null && now - old.Attempt < 3000 &&
                            Vector2.DistanceSquared(origin, old.Origin) < 2500) continue;
                        bool full = changed || now - old!.Full >= Math.Max(1000, fullInterval);
                        if (!full && old!.Path != null && old.Origin == origin) continue;
                        var path = compute(target.Position, changed ? null : old?.Path, full ? 0 : segments, token);
                        result[target.Key] = new(target.Position, origin, doors, path, now, full ? now : old!.Full);
                        // Soft batch budget: finish one bounded A* search, then yield to other batches.
                        if (++completed >= 4 || timer.ElapsedMilliseconds >= 40) break;
                    }
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                finally { PathWorkLimiter.Gate.Release(); }
            }, token);
        }
    }
}
