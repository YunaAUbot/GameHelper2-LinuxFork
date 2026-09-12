namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Threading;

    /// <summary>
    /// Grid-space line walkability checker.
    /// Uses Bresenham's line algorithm to sample grid cells along a straight line
    /// and checks each against the nibble-encoded walkability data.
    /// </summary>
    public static partial class LineWalker
    {
        /// <summary>
        /// Checks whether a single grid cell is walkable.
        /// Decodes the 4-bit nibble from the packed walkability byte array.
        /// 0 = blocked, 1-5 = walkable.
        /// Optionally consults a door-override set (positions forced walkable).
        /// </summary>
        public static bool IsWalkable(
            byte[] walkableData,
            int bytesPerRow,
            int x,
            int y,
            HashSet<(int, int)>? doorOverrides = null)
        {
            if (bytesPerRow <= 0 || x < 0 || (long)x >= (long)bytesPerRow * 2 ||
                y < 0 || y >= walkableData.Length / bytesPerRow)
            {
                return false;
            }

            if (doorOverrides != null && doorOverrides.Contains((x, y)))
            {
                return true;
            }

            var byteIndex = (y * bytesPerRow) + (x / 2);

            if ((uint)byteIndex >= (uint)walkableData.Length)
            {
                return false;
            }

            var shift = ((x & 1) == 0) ? 0 : 4;
            var value = (walkableData[byteIndex] >> shift) & 0xF;
            return value != 0;
        }

        /// <summary>
        /// Result of a line walkability check.
        /// </summary>
        public struct LineResult
        {
            /// <summary>
            /// True if every cell along the line is walkable.
            /// </summary>
            public bool IsClear;

            /// <summary>
            /// Number of blocked (non-walkable) cells encountered.
            /// </summary>
            public int BlockedCells;

            /// <summary>
            /// Total number of cells sampled along the line.
            /// </summary>
            public int TotalCells;
        }

        /// <summary>
        /// Walks a Bresenham line from start to end (Vector2 variant),
        /// checking each grid cell for walkability.
        /// </summary>
        public static LineResult CheckLine(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 start,
            Vector2 end,
            HashSet<(int, int)>? doorOverrides = null)
        {
            return CheckLine(
                walkableData,
                bytesPerRow,
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y),
                (int)Math.Round(end.X),
                (int)Math.Round(end.Y),
                doorOverrides);
        }

        /// <summary>
        /// Walks a Bresenham line from (x0,y0) to (x1,y1),
        /// checking each grid cell for walkability.
        /// </summary>
        public static LineResult CheckLine(
            byte[] walkableData,
            int bytesPerRow,
            int x0,
            int y0,
            int x1,
            int y1,
            HashSet<(int, int)>? doorOverrides = null)
        {
            return CheckLineCore(walkableData, bytesPerRow, x0, y0, x1, y1, doorOverrides, false);
        }

        /// <summary>
        /// Tests visibility without counting blocked cells; stops at the first obstacle.
        /// </summary>
        public static bool IsLineClear(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 start,
            Vector2 end,
            HashSet<(int, int)>? doorOverrides = null,
            CancellationToken cancellationToken = default)
        {
            return CheckLineCore(
                walkableData, bytesPerRow,
                (int)Math.Round(start.X), (int)Math.Round(start.Y),
                (int)Math.Round(end.X), (int)Math.Round(end.Y),
                doorOverrides, true, cancellationToken).IsClear;
        }

        private static LineResult CheckLineCore(
            byte[] walkableData,
            int bytesPerRow,
            int x0,
            int y0,
            int x1,
            int y1,
            HashSet<(int, int)>? doorOverrides,
            bool stopAtFirstBlocked, CancellationToken cancellationToken = default)
        {
            var result = new LineResult { IsClear = true };

            var dx = Math.Abs(x1 - x0);
            var dy = Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            var x = x0;
            var y = y0;

            while (true)
            {
                if ((result.TotalCells & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                result.TotalCells++;

                if (!IsWalkable(walkableData, bytesPerRow, x, y, doorOverrides))
                {
                    result.IsClear = false;
                    result.BlockedCells++;
                    if (stopAtFirstBlocked)
                    {
                        return result;
                    }
                }

                if (x == x1 && y == y1)
                {
                    break;
                }

                var e2 = err * 2;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }

            return result;
        }

    }
}
