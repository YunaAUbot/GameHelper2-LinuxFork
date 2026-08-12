// <copyright file="DevBridgeCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace DevBridge
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>Exports a small, read-only snapshot of already-observed GameHelper state.</summary>
    public sealed class DevBridgeCore : PCore<DevBridgeSettings>
    {
        private const int MaxRequestBytes = 16 * 1024;
        private const int MaxSettingsBytes = 64 * 1024;
        private static readonly HashSet<string> AllowedRoots = new(StringComparer.Ordinal)
        {
            "overview", "player", "current-area", "ui",
        };

        private readonly Stopwatch statusTimer = Stopwatch.StartNew();
        private string lastResult = "waiting";

        private string SettingsFile => this.ContainedPath("config", "settings.json");
        private string StatusFile => this.ContainedPath("runtime-status.json");
        private string RequestFile => this.ContainedPath("snapshot-request.json");
        private string SnapshotFile => this.ContainedPath("game-snapshot.json");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            Directory.CreateDirectory(this.ContainedPath("config"));
            if (File.Exists(this.SettingsFile))
            {
                this.Settings = JsonConvert.DeserializeObject<DevBridgeSettings>(ReadBounded(this.SettingsFile, MaxSettingsBytes)) ?? new();
            }

            this.WriteStatus();
        }

        /// <inheritdoc/>
        public override void OnDisable() => this.WriteStatus(false);

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.TextWrapped("Read-only local snapshots: overview, player, current-area, and UI visibility.");
            ImGui.Text($"Last request: {this.lastResult}");
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (this.statusTimer.ElapsedMilliseconds >= Math.Clamp(this.Settings.StatusIntervalMilliseconds, 250, 10_000))
            {
                this.WriteStatus();
                this.statusTimer.Restart();
            }

            this.ProcessRequest();
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(this.ContainedPath("config"));
            AtomicWrite(this.SettingsFile, this.Settings);
        }

        private void ProcessRequest()
        {
            var requestPath = this.RequestFile;
            if (!File.Exists(requestPath)) return;
            var claimedPath = requestPath + "." + Guid.NewGuid().ToString("N") + ".processing";
            try
            {
                File.Move(requestPath, claimedPath, false);
                if ((File.GetAttributes(claimedPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("request must not be a link");
                var request = JsonConvert.DeserializeObject<SnapshotRequest>(ReadBounded(claimedPath, MaxRequestBytes));
                if (request == null || request.SchemaVersion != 1 || !AllowedRoots.Contains(request.Root) || !ValidRequestId(request.RequestId))
                    throw new InvalidDataException("request fields are invalid");

                AtomicWrite(this.SnapshotFile, this.BuildSnapshot(request));
                this.lastResult = $"completed {request.RequestId}";
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                this.lastResult = $"rejected: {exception.GetType().Name}";
            }
            finally
            {
                try { File.Delete(claimedPath); } catch (IOException) { }
            }
        }

        private object BuildSnapshot(SnapshotRequest request) => new
        {
            schemaVersion = 1,
            requestId = request.RequestId,
            root = request.Root,
            capturedAtUtc = DateTime.UtcNow,
            data = Core.States.GameCurrentState != GameStateTypes.InGameState
                ? (object)new { available = false, reason = "not-in-game" }
                : request.Root switch
            {
                "player" => this.PlayerSnapshot(),
                "current-area" => this.AreaSnapshot(),
                "ui" => this.UiSnapshot(),
                _ => this.OverviewSnapshot(),
            },
        };

        private object OverviewSnapshot() => new
        {
            gameAttached = Core.Process.Pid != 0,
            gameState = Core.States.GameCurrentState.ToString(),
            player = this.PlayerSnapshot(),
            currentArea = this.AreaSnapshot(),
            ui = this.UiSnapshot(),
        };

        private object PlayerSnapshot()
        {
            var entity = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            entity.TryGetComponent<Player>(out var player);
            entity.TryGetComponent<Life>(out var life);
            entity.TryGetComponent<Render>(out var render);
            return new
            {
                valid = entity.IsValid,
                name = player?.Name ?? string.Empty,
                level = player?.Level ?? 0,
                life = life == null ? null : new { alive = life.IsAlive, current = life.Health.Current, total = life.Health.Total },
                position = render == null ? null : new { x = render.GridPosition.X, y = render.GridPosition.Y },
            };
        }

        private object AreaSnapshot()
        {
            var state = Core.States.InGameStateObject;
            var instance = state.CurrentAreaInstance;
            var area = state.CurrentWorldInstance.AreaDetails;
            return new
            {
                id = area.Id,
                name = area.Name,
                act = area.Act,
                level = instance.CurrentAreaLevel,
                isTown = area.IsTown,
                isHideout = area.IsHideout,
                hasWaypoint = area.HasWaypoint,
                awakeEntityCount = Math.Min(instance.AwakeEntities.Count, 100_000),
                networkBubbleEntityCount = Math.Min(instance.NetworkBubbleEntityCount, 100_000),
            };
        }

        private object UiSnapshot()
        {
            var ui = Core.States.InGameStateObject.GameUi;
            return new
            {
                settingsOpen = Core.IsSettingsMenuOpen,
                largeMapVisible = ui.LargeMap.IsVisible,
                miniMapVisible = ui.MiniMap.IsVisible,
                worldMapVisible = ui.WorldMapPanel.IsVisible,
                atlasVisible = ui.Atlas.IsVisible,
                currencyExchangeVisible = ui.CurrencyExchangePanel.IsVisible,
                leftPanelVisible = ui.LeftPanel.IsVisible,
                rightPanelVisible = ui.RightPanel.IsVisible,
                chatVisible = ui.ChatParent.IsVisible,
            };
        }

        private void WriteStatus(bool enabled = true) => AtomicWrite(this.StatusFile, new
        {
            schemaVersion = 1,
            enabled,
            gameAttached = Core.Process.Pid != 0,
            gameState = Core.States.GameCurrentState.ToString(),
            updatedAtUtc = DateTime.UtcNow,
            allowedRoots = AllowedRoots,
            lastResult = this.lastResult,
        });

        private string ContainedPath(params string[] parts)
        {
            var root = Path.GetFullPath(this.DllDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var combined = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
            if (!combined.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("bridge path escaped plugin directory");
            return combined;
        }

        private static bool ValidRequestId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (var character in value)
                if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-') return false;
            return true;
        }

        private static void AtomicWrite(string path, object value)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(temporary, path, true);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }

        private static string ReadBounded(string path, int maxBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > maxBytes) throw new InvalidDataException("request size is invalid");
            var buffer = new byte[maxBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var count = stream.Read(buffer, total, buffer.Length - total);
                if (count == 0) break;
                total += count;
            }

            if (total <= 0 || total > maxBytes) throw new InvalidDataException("request size is invalid");
            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }

        private sealed class SnapshotRequest
        {
            public int SchemaVersion { get; set; }
            public string RequestId { get; set; } = string.Empty;
            public string Root { get; set; } = string.Empty;
        }
    }
}
