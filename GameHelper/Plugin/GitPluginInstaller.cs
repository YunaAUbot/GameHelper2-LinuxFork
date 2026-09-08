// <copyright file="GitPluginInstaller.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using ImGuiNET;
    using Newtonsoft.Json;
    using GameHelper.Localization;

    /// <summary>File bridge to the launcher's native Linux source builder.</summary>
    internal static class GitPluginInstaller
    {
        private static readonly CancellationTokenSource Lifetime = new();
        private static readonly string Home = Path.Combine(AppContext.BaseDirectory, "PluginSources");
        private static Source[] sources = Array.Empty<Source>();
        private static string status = string.Empty;
        private static string url = string.Empty;
        private static bool trust;
        private static bool desktopAuth;
        private static bool inventoryReadable;
        private static int busy;
        private sealed class Source
        {
            public string id = string.Empty;
            public string url = string.Empty;
            public string name = string.Empty;
            public string sourceHead = string.Empty;
            public string sourceRevisionState = "unknown";
            public string pendingVersion = string.Empty;
            public string verifiedInstalledVersion = string.Empty;
            public string status = string.Empty;
            public bool update = true;
            public string revisionState = "unknown";
            public int behind = 0;
            public int sourceBehind = 0;
            public long checkedAt = 0;
        }

        internal static void Initialize()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Lifetime.Cancel();
            _ = Task.Run(async () =>
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    try
                    {
                        Volatile.Write(ref sources, await ReadFolderSnapshot(Path.Combine(AppContext.BaseDirectory, "Plugins")));
                        Volatile.Write(ref inventoryReadable, true);
                        var file = Path.Combine(Home, "result.json");
                        if (File.Exists(file))
                            status = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(file, Lifetime.Token))["status"]?.ToString() ?? string.Empty;
                    }
                    catch (OperationCanceledException) { break; }
                    catch { Volatile.Write(ref inventoryReadable, false); status = OverlayLocalization.T("settings.plugin.git.read_error", "Unable to read installer status."); }
                    try { await Task.Delay(1000, Lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        private static async Task<Source[]> ReadFolderSnapshot(string plugins)
        {
            var snapshot = new System.Collections.Generic.List<Source>();
            if (Directory.Exists(plugins))
            {
                foreach (var folder in Directory.EnumerateDirectories(plugins))
                {
                    var name = Path.GetFileName(folder);
                    if (name.StartsWith('.')) continue;
                    var linked = File.Exists(Path.Combine(folder, ".git")) || Directory.Exists(Path.Combine(folder, ".git"))
                        || File.Exists(Path.Combine(folder, ".git-source", ".git")) || Directory.Exists(Path.Combine(folder, ".git-source", ".git"));
                    var source = new Source { name = name, revisionState = linked ? "unknown" : "local" };
                    var cached = Path.Combine(folder, ".git-status.json");
                    if (linked && File.Exists(cached))
                    {
                        try { source = JsonConvert.DeserializeObject<Source>(await File.ReadAllTextAsync(cached, Lifetime.Token)) ?? source; }
                        catch (JsonException) { }
                        catch (IOException) { }
                        source.name = name;
                        source.id = name;
                    }
                    if (Directory.Exists(folder)) snapshot.Add(source);
                }
            }
            return snapshot.ToArray();
        }

        private static void Submit(object request)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("GAMEHELPER2_GIT_WORKER") != "1")
                    {
                        status = OverlayLocalization.T("settings.plugin.git.launcher_required", "Start through the Linux launcher with Python 3, Git and the .NET 10 SDK installed.");
                        return;
                    }
                    Directory.CreateDirectory(Home);
                    var path = Path.Combine(Home, "request.json");
                    if (File.Exists(path)) { status = OverlayLocalization.T("settings.plugin.git.pending", "An installer request is already pending."); return; }
                    File.Delete(Path.Combine(Home, "result.json"));
                    var temp = Path.Combine(Home, "request-ui.tmp");
                    await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(request), Lifetime.Token);
                    File.Move(temp, path, false);
                    status = OverlayLocalization.T("settings.plugin.git.queued", "Request queued; compilation may take several minutes.");
                }
                catch (OperationCanceledException) { }
                catch { status = OverlayLocalization.T("settings.plugin.git.write_error", "Unable to queue installer request."); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }

        internal static bool FolderPresent(string name)
        {
            return Volatile.Read(ref sources).Any(s => s.name == name);
        }

        // Reads only the background-loaded snapshot. No filesystem or network on render.
        internal static string RevisionLabel(string name)
        {
            if (!Volatile.Read(ref inventoryReadable))
                return OverlayLocalization.T("settings.plugin.git.unknown", "Unknown");
            var matches = Volatile.Read(ref sources).Where(s => s.name == name).ToArray();
            if (matches.Length == 0) return OverlayLocalization.T("settings.plugin.git.local", "Local");
            if (matches.Length != 1) return OverlayLocalization.T("settings.plugin.git.unknown", "Unknown");
            var source = matches[0];
            // Cached successes are explicitly dated; a failed latest check is unknown.
            return FormatRevision(source.revisionState, source.behind);
        }

        private static string FormatRevision(string state, int behind)
        {
            return state switch
            {
                "local" => OverlayLocalization.T("settings.plugin.git.local", "Local"),
                "current" => OverlayLocalization.T("settings.plugin.git.current", "Up to date"),
                "behind" when behind > 0 => OverlayLocalization.F("settings.plugin.git.behind", "{0} commits behind", behind),
                "ahead" => OverlayLocalization.T("settings.plugin.git.ahead", "Ahead"),
                "diverged" => OverlayLocalization.T("settings.plugin.git.diverged", "Diverged"),
                _ => OverlayLocalization.T("settings.plugin.git.unknown", "Unknown"),
            };
        }

        internal static string RevisionDetail(string name)
        {
            var source = Volatile.Read(ref sources).FirstOrDefault(s => s.name == name);
            if (source == null || source.checkedAt <= 0)
                return OverlayLocalization.T("settings.plugin.git.unverified", "No verified revision check. Local files are preserved.");
            // Reject corrupt timestamps without throwing from the settings render path.
            if (source.checkedAt > 253402300799) return string.Empty;
            return OverlayLocalization.F("settings.plugin.git.checked", "Last verified check: {0} UTC", DateTimeOffset.FromUnixTimeSeconds(source.checkedAt).UtcDateTime.ToString("yyyy-MM-dd HH:mm"));
        }

        private static bool IsRepositoryUrl(string value)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^git@[A-Za-z0-9_.-]+:[A-Za-z0-9_./-]+$")) return true;
            return Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == "https" || parsed.Scheme == "ssh")
                && (string.IsNullOrEmpty(parsed.UserInfo) || (parsed.Scheme == "ssh" && parsed.UserInfo == "git"))
                && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment)
                && System.Text.RegularExpressions.Regex.IsMatch(parsed.AbsolutePath, @"^/[A-Za-z0-9_./-]+$");
        }

        private static bool HasSource(Source source)
        {
            return !string.IsNullOrEmpty(source.id) && IsRepositoryUrl(source.url);
        }

        internal static void DrawRevisionUpdate(string name)
        {
            var matches = Volatile.Read(ref sources).Where(s => s.name == name && HasSource(s)).ToArray();
            if (matches.Length != 1) return;
            var source = matches[0];
            ImGui.SameLine();
            ImGui.BeginDisabled(!trust || !Volatile.Read(ref inventoryReadable) || Volatile.Read(ref busy) != 0);
            if (ImGui.Button(OverlayLocalization.Label("settings.plugin.git.manual_update", "Update", "GitManualUpdate" + source.id)))
                Submit(new { action = "update", id = source.id, trust });
            ImGui.EndDisabled();
            ImGui.TextWrapped(source.url);
            ImGui.TextUnformatted(source.status);
        }

        private static void DrawUpdateAll()
        {
            ImGui.BeginDisabled(!trust || !Volatile.Read(ref inventoryReadable) || Volatile.Read(ref busy) != 0
                || !Volatile.Read(ref sources).Any(HasSource));
            if (ImGui.Button(OverlayLocalization.Label("settings.plugin.git.update_all", "Update all", "GitUpdateAll")))
                Submit(new { action = "update-all", trust });
            ImGui.EndDisabled();
        }

        internal static void Draw()
        {
            ImGui.TextWrapped(OverlayLocalization.T("settings.plugin.git.replace_warning", "Manual updates build the displayed repositories and can replace newer or modified local code. Existing plugins activate on restart; configuration and backups are preserved."));
            foreach (var source in Volatile.Read(ref sources).Where(HasSource))
                ImGui.TextWrapped($"{source.name}: {source.url}");
            ImGui.Checkbox(OverlayLocalization.Label("settings.plugin.git.trust", "I trust this source: building and loading plugins executes code with my user permissions.", "GitTrust"), ref trust);
            DrawUpdateAll();
            ImGui.TextWrapped(status);
            if (!ImGui.CollapsingHeader(OverlayLocalization.Title("settings.plugin.git.title", "Install from Git repository", "GitPlugins"))) return;
            ImGui.InputText(OverlayLocalization.Label("settings.plugin.git.repository_url", "Repository HTTPS or SSH URL", "GitUrl"), ref url, 512);
            ImGui.Checkbox(OverlayLocalization.Label("settings.plugin.git.auth", "Use my existing desktop GitHub CLI login", "GitDesktopAuth"), ref desktopAuth);
            ImGui.BeginDisabled(!trust || Volatile.Read(ref busy) != 0);
            if (ImGui.Button(OverlayLocalization.Label("settings.plugin.git.install", "Download, build and install", "GitInstall")))
            {
                if (!IsRepositoryUrl(url))
                    status = OverlayLocalization.T("settings.plugin.git.invalid_repository_url", "Use a credential-free HTTPS or Git SSH repository URL.");
                else
                    Submit(new { action = "install", url, trust, auth = desktopAuth ? "desktop-gh" : "none" });
                trust = false;
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped(OverlayLocalization.T("settings.plugin.git.restart", "New installs: use Reload all plugins; no restart required. Updates activate on next launch. Failures retain existing plugins and configuration."));
            ImGui.TextWrapped(status);
            if (ImGui.Button(OverlayLocalization.Label("settings.plugin.git.check", "Refresh revision status", "GitCheck")))
                Submit(new { action = "check" });
            foreach (var source in Volatile.Read(ref sources))
            {
                ImGui.TextUnformatted(source.name + ": " + RevisionLabel(source.name));
                if (!HasSource(source)) continue;
                ImGui.PushID("GitSourceList");
                DrawRevisionUpdate(source.name);
                ImGui.PopID();
                ImGui.TextUnformatted(OverlayLocalization.F("settings.plugin.git.revisions", "Installed binary: {0}  Source HEAD: {1}  Pending build: {2}", string.IsNullOrEmpty(source.verifiedInstalledVersion) ? OverlayLocalization.T("settings.plugin.git.unknown", "Unknown") : source.verifiedInstalledVersion, source.sourceHead, source.pendingVersion));
                ImGui.TextUnformatted(OverlayLocalization.F("settings.plugin.git.source_revision", "Source checkout: {0}", FormatRevision(source.sourceRevisionState, source.sourceBehind)));
                var update = source.update;
                if (ImGui.Checkbox(OverlayLocalization.Label("settings.plugin.git.update", "Check for updates at startup", "GitUpdate" + source.id), ref update))
                    Submit(new { action = "toggle", id = source.id, update });
            }
        }
    }
}
