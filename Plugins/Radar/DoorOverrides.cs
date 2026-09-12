namespace Radar
{
    using System;
    using System.Collections.Generic;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;

    public static partial class LineWalker
    {
        /// <summary>
        /// Builds a door-override map from all door-like entities in AwakeEntities.
        /// Doors are detected by the TriggerableBlockage component or by entity path
        /// containing "Door". A 5x5 area around each door is marked as forced-walkable
        /// to punch through wall-type doors on the terrain grid.
        /// </summary>
        /// <param name="areaInstance">The current area instance.</param>
        /// <returns>
        /// A HashSet of (x, y) grid positions to treat as walkable,
        /// or null if no door entities exist in the area.
        /// </returns>
        public static HashSet<(int, int)>? BuildDoorOverrideMap(
            AreaInstance areaInstance)
        {
            HashSet<(int, int)>? overrides = null;
            const int doorRadius = 2; // 5x5 area

            void MarkArea(int gx, int gy)
            {
                overrides ??= new HashSet<(int, int)>();
                for (var dx = -doorRadius; dx <= doorRadius; dx++)
                {
                    for (var dy = -doorRadius; dy <= doorRadius; dy++)
                    {
                        overrides.Add((gx + dx, gy + dy));
                    }
                }
            }

            foreach (var kv in areaInstance.AwakeEntities)
            {
                var entity = kv.Value;

                // Method 1: TriggerableBlockage component (the canonical door marker)
                if (entity.TryGetComponent<TriggerableBlockage>(out var _))
                {
                    if (entity.TryGetComponent<Render>(out var render))
                    {
                        MarkArea(
                            (int)Math.Round(render.GridPosition.X),
                            (int)Math.Round(render.GridPosition.Y));
                    }

                    continue;
                }

                // Method 2: Entity path contains "Door" (catch variants that
                // may lack TriggerableBlockage, e.g. certain door subtypes)
                var path = entity.Path;
                if (!string.IsNullOrEmpty(path) &&
                    path.Contains("Door", StringComparison.OrdinalIgnoreCase))
                {
                    if (entity.TryGetComponent<Render>(out var render))
                    {
                        MarkArea(
                            (int)Math.Round(render.GridPosition.X),
                            (int)Math.Round(render.GridPosition.Y));
                    }
                }
            }

            return overrides;
        }
    }
}
