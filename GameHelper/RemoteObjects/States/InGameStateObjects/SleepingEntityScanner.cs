namespace GameHelper.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.States.InGameState;

    /// <summary>
    /// Single-worker, incremental scan of static objects beyond the network bubble.
    /// Each instance belongs to exactly one area identity; discard it on area changes.
    /// </summary>
    public sealed class SleepingEntityScanner
    {
        private readonly AreaInstance area;
        private readonly IntPtr address;
        private readonly string hash;
        private readonly Queue<IntPtr> pending = new();
        private readonly HashSet<IntPtr> visited = new();
        private Dictionary<(IntPtr, uint, IntPtr), string> paths = new();
        private Dictionary<(IntPtr, uint, IntPtr), string> nextPaths = new();

        public SleepingEntityScanner(AreaInstance area)
        {
            this.area = area;
            this.address = area.Address;
            this.hash = area.AreaHash;
        }

        /// <summary>Processes at most 2048 nodes / approximately 10 ms, preserving its cursor.</summary>
        public void ScanNext(Func<string, bool> filter, Action<Entity> onMatch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (this.address == IntPtr.Zero || this.area.Address != this.address || this.area.AreaHash != this.hash) return;
            var reader = Core.Process.Handle;
            if (this.pending.Count == 0)
            {
                this.visited.Clear();
                this.nextPaths = new();
                if (!reader.TryReadMemory<AreaInstanceOffsets>(this.address, out var areaData)) return;
                var tree = areaData.Entities.SleepingEntities;
                if (tree.Size <= 0 || tree.Size > 500000) return;
                if (!reader.TryReadMemory<StdMapNode<EntityNodeKey, EntityNodeValue>>(tree.Head, out var head)) return;
                this.pending.Enqueue(head.Parent);
            }

            var timer = Stopwatch.StartNew();
            for (var count = 0; count < 2048 && timer.ElapsedMilliseconds < 10 && this.pending.TryDequeue(out var pointer); count++)
            {
                token.ThrowIfCancellationRequested();
                if (!this.visited.Add(pointer)) continue;
                if (this.visited.Count > 500000) { this.pending.Clear(); break; }
                if (!reader.TryReadMemory<StdMapNode<EntityNodeKey, EntityNodeValue>>(pointer, out var node) || node.IsNil || node.Color > 1) continue;
                if (SafeMemoryHandle.IsValidAddress(node.Left)) this.pending.Enqueue(node.Left);
                if (SafeMemoryHandle.IsValidAddress(node.Right)) this.pending.Enqueue(node.Right);
                var entityPointer = node.Data.Value.EntityPtr;
                if (!reader.TryReadMemory<EntityOffsets>(entityPointer, out var identity)) continue;
                var key = (entityPointer, identity.Id, identity.ItemBase.EntityDetailsPtr);
                if (!this.paths.TryGetValue(key, out var path))
                {
                    if (!reader.TryReadMemory<EntityDetails>(identity.ItemBase.EntityDetailsPtr, out var details)) continue;
                    path = reader.ReadStdWString(details.name);
                }
                if (string.IsNullOrEmpty(path)) continue;
                this.nextPaths[key] = path;
                if (filter(path))
                {
                    var entity = new Entity(entityPointer);
                    if (entity.Id == identity.Id && entity.Path == path) onMatch(entity);
                }
            }
            if (this.pending.Count == 0) this.paths = this.nextPaths;
        }
    }
}
