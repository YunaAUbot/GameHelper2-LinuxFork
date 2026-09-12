namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Threading;

    /// <summary>
    /// A* pathfinder that operates directly on the nibble-encoded walkability grid.
    /// Uses 8-directional movement with corner-cutting prevention,
    /// Euclidean distance heuristic, and a configurable iteration cap.
    /// </summary>
    public static class Pathfinder
    {
        /// <summary>
        /// Maximum number of nodes the A* search will expand before giving up.
        /// Prevents runaway searches on disconnected or enormous maps.
        /// </summary>
        public const int DefaultMaxIterations = 1_000_000;

        /// <summary>
        /// Maximum straight-line (Euclidean) distance in grid cells between start and end.
        /// Paths beyond this distance are skipped without running A*.
        /// </summary>
        public const float DefaultMaxDistance = 2500f;

        // 8-directional neighbor offsets with movement costs.
        // Orthogonal moves cost 1.0, diagonal moves cost √2 ≈ 1.414.
        private static readonly (int dx, int dy, float cost)[] Neighbors =
        {
            ( 0, -1, 1.0f),
            ( 1, -1, 1.41421356f),
            ( 1,  0, 1.0f),
            ( 1,  1, 1.41421356f),
            ( 0,  1, 1.0f),
            (-1,  1, 1.41421356f),
            (-1,  0, 1.0f),
            (-1, -1, 1.41421356f),
        };

        /// <summary>
        /// Finds the shortest path from start to end on the walkability grid.
        /// Returns the path as a list of grid positions (start → end),
        /// or null if no path exists or start/end are blocked.
        /// </summary>
        /// <param name="walkableData">Nibble-encoded walkability byte array.</param>
        /// <param name="bytesPerRow">Number of bytes per logical row.</param>
        /// <param name="doorOverrides">Optional set of grid positions to force as walkable (open doors).</param>
        /// <param name="start">Start position in grid coordinates.</param>
        /// <param name="end">Goal position in grid coordinates.</param>
        /// <param name="maxIterations">Search budget (nodes to expand).</param>
        /// <param name="cancellationToken">Cancels search and smoothing after area changes.</param>
        /// <returns>Grid-space path from start to end, or null.</returns>
        public static List<Vector2>? FindPath(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 start,
            Vector2 end,
            HashSet<(int, int)>? doorOverrides = null,
            int maxIterations = DefaultMaxIterations,
            CancellationToken cancellationToken = default)
        {
            return FindPath(
                walkableData,
                bytesPerRow,
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y),
                (int)Math.Round(end.X),
                (int)Math.Round(end.Y),
                doorOverrides,
                maxIterations, cancellationToken);
        }

        /// <summary>
        /// Finds the shortest path from (startX,startY) to (endX,endY).
        /// </summary>
        public static List<Vector2>? FindPath(
            byte[] walkableData,
            int bytesPerRow,
            int startX,
            int startY,
            int endX,
            int endY,
            HashSet<(int, int)>? doorOverrides = null,
            int maxIterations = DefaultMaxIterations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // If start is blocked (player near a wall), find the nearest walkable cell.
            if (!LineWalker.IsWalkable(walkableData, bytesPerRow, startX, startY, doorOverrides))
            {
                if (!TryFindNearestWalkable(
                        walkableData, bytesPerRow, startX, startY, doorOverrides,
                        out startX, out startY))
                {
                    return null;
                }
            }

            // Quick distance cutoff — skip A* for very far POIs
            var straightLineDist = MathF.Sqrt(
                ((endX - startX) * (endX - startX)) +
                ((endY - startY) * (endY - startY)));
            if (straightLineDist > DefaultMaxDistance)
            {
                return null;
            }

            // If end is blocked, find the nearest walkable neighbor instead.
            // POIs are sometimes placed on non-walkable tiles at terrain boundaries.
            if (!LineWalker.IsWalkable(walkableData, bytesPerRow, endX, endY, doorOverrides))
            {
                if (!TryFindNearestWalkable(
                        walkableData, bytesPerRow, endX, endY, doorOverrides, out endX, out endY))
                {
                    return null;
                }
            }

            // Already there
            if (startX == endX && startY == endY)
            {
                return new List<Vector2> { new(startX, startY) };
            }

            var startKey = (startX, startY);
            var goalKey = (endX, endY);

            // A* data structures
            var openSet = new PriorityQueue<((int x, int y) node, float g), float>();
            var cameFrom = new Dictionary<(int x, int y), (int x, int y)>();
            var gScore = new Dictionary<(int x, int y), float>();

            float Heuristic(int x, int y)
            {
                var dx = endX - x;
                var dy = endY - y;
                return MathF.Sqrt((dx * dx) + (dy * dy));
            }

            gScore[startKey] = 0f;
            openSet.Enqueue((startKey, 0f), Heuristic(startX, startY));

            var iterations = 0;

            while (openSet.Count > 0 && iterations < maxIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = openSet.Dequeue();
                var current = entry.node;
                if (entry.g != gScore[current]) continue; // Superseded queue entry.
                iterations++;

                if (current == goalKey)
                {
                    var rawPath = ReconstructPath(cameFrom, current, startKey);
                    return SmoothPath(walkableData, bytesPerRow, rawPath, doorOverrides, cancellationToken);
                }

                var currentG = gScore[current];

                // Expand neighbors
                foreach (var (dx, dy, moveCost) in Neighbors)
                {
                    var nx = current.x + dx;
                    var ny = current.y + dy;

                    if (!LineWalker.IsWalkable(walkableData, bytesPerRow, nx, ny, doorOverrides))
                    {
                        continue;
                    }

                    // Corner-cutting prevention:
                    // Diagonal moves are only allowed if both cardinal neighbors
                    // adjacent to the corner are also walkable.
                    // Example: moving to (x+1, y-1) requires (x+1, y) and (x, y-1) walkable.
                    if (dx != 0 && dy != 0)
                    {
                        if (!LineWalker.IsWalkable(walkableData, bytesPerRow, current.x + dx, current.y, doorOverrides) ||
                            !LineWalker.IsWalkable(walkableData, bytesPerRow, current.x, current.y + dy, doorOverrides))
                        {
                            continue;
                        }
                    }

                    var neighborKey = (nx, ny);
                    var tentativeG = currentG + moveCost;

                    if (!gScore.TryGetValue(neighborKey, out var existingG) ||
                        tentativeG < existingG)
                    {
                        cameFrom[neighborKey] = current;
                        gScore[neighborKey] = tentativeG;
                        var fScore = tentativeG + Heuristic(nx, ny);
                        openSet.Enqueue((neighborKey, tentativeG), fScore);
                    }
                }
            }

            // Path not found (disconnected or exceeded iteration budget)
            return null;
        }

        /// <summary>
        /// Tries to find a walkable cell near (x,y) by scanning outward
        /// in expanding rings up to a limited radius.
        /// Uses an efficient perimeter-only scan — O(r) per ring.
        /// </summary>
        private static bool TryFindNearestWalkable(
            byte[] walkableData,
            int bytesPerRow,
            int x,
            int y,
            HashSet<(int, int)>? doorOverrides,
            out int resultX,
            out int resultY,
            int maxRadius = 75)
        {
            // Check the target cell itself first
            if (LineWalker.IsWalkable(walkableData, bytesPerRow, x, y, doorOverrides))
            {
                resultX = x;
                resultY = y;
                return true;
            }

            // Expanding ring search — only walk the perimeter of each ring
            for (var r = 1; r <= maxRadius; r++)
            {
                // Top and bottom edges (including corners)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + dx, y - r, doorOverrides))
                    {
                        resultX = x + dx;
                        resultY = y - r;
                        return true;
                    }

                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + dx, y + r, doorOverrides))
                    {
                        resultX = x + dx;
                        resultY = y + r;
                        return true;
                    }
                }

                // Left and right edges (excluding corners already checked above)
                for (var dy = -r + 1; dy <= r - 1; dy++)
                {
                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x - r, y + dy, doorOverrides))
                    {
                        resultX = x - r;
                        resultY = y + dy;
                        return true;
                    }

                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + r, y + dy, doorOverrides))
                    {
                        resultX = x + r;
                        resultY = y + dy;
                        return true;
                    }
                }
            }

            resultX = 0;
            resultY = 0;
            return false;
        }

        /// <summary>
        /// Walks the cameFrom map backwards from goal to start,
        /// producing a forward-ordered list of grid positions.
        /// </summary>
        private static List<Vector2> ReconstructPath(
            Dictionary<(int x, int y), (int x, int y)> cameFrom,
            (int x, int y) current,
            (int x, int y) start)
        {
            var path = new List<Vector2> { new(current.x, current.y) };

            while (current != start)
            {
                current = cameFrom[current];
                path.Add(new Vector2(current.x, current.y));
            }

            path.Reverse();
            return path;
        }

        /// <summary>
        /// Simplifies a grid path by greedily removing intermediate points
        /// that are visible from earlier points (line-of-sight unwalkable check).
        /// Collapses long straight segments to just their endpoints,
        /// dramatically reducing the number of rendered line segments.
        /// </summary>
        internal static List<Vector2> SmoothPath(
            byte[] walkableData,
            int bytesPerRow,
            List<Vector2> rawPath,
            HashSet<(int, int)>? doorOverrides = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rawPath.Count <= 2)
            {
                return rawPath;
            }

            var result = new List<Vector2> { rawPath[0] };
            var currentIdx = 0;

            while (currentIdx < rawPath.Count - 1)
            {
                // Try to connect to the farthest visible point ahead
                var farthest = currentIdx + 1;
                for (var i = rawPath.Count - 1; i > currentIdx; i--)
                {
                    var isClear = LineWalker.IsLineClear(
                        walkableData, bytesPerRow,
                        rawPath[currentIdx], rawPath[i],
                        doorOverrides, cancellationToken);

                    if (isClear)
                    {
                        farthest = i;
                        break;
                    }
                }

                currentIdx = farthest;
                result.Add(rawPath[currentIdx]);
            }

            return result;
        }
    }
}
