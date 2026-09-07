// <copyright file="GitPluginInstaller.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using System;
    using System.IO;
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
        private static int busy;
        private sealed class Source
        {
            public string id = string.Empty;
            public string name = string.Empty;
            public string version = string.Empty;
            public string installedVersion = string.Empty;
            public string status = string.Empty;
            public bool update = true;
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
                        var file = Path.Combine(Home, "sources.json");
                        if (File.Exists(file))
                            Volatile.Write(ref sources, JsonConvert.DeserializeObject<Source[]>(await File.ReadAllTextAsync(file, Lifetime.Token)) ?? Array.Empty<Source>());
                        file = Path.Combine(Home, "result.json");
                        if (File.Exists(file))
                            status = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(file, Lifetime.Token))["status"]?.ToString() ?? string.Empty;
                    }
                    catch (OperationCanceledException) { break; }
                    catch { status = OverlayLocalization.T("settings.plugin.git.read_error", "Unable to read installer status."); }
                    try { await Task.Delay(1000, Lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });
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

        internal static void Draw()
        {
            if (!ImGui.CollapsingHeader(OverlayLocalization.Title("settings.plugin.git.title", "Install from Git repository", "GitPlugins"))) return;
            ImGui.InputText(OverlayLocalization.Label("settings.plugin.git.url", "Repository HTTPS URL", "GitUrl"), ref url, 512);
            ImGui.Checkbox(OverlayLocalization.Label("settings.plugin.git.trust", "I trust this source: building and loading plugins executes code with my user permissions.", "GitTrust"), ref trust);
            ImGui.BeginDisabled(!trust || Volatile.Read(ref busy) != 0);
            if (ImGui.Button(OverlayLocalization.Label("settings.plugin.git.install", "Download, build and install", "GitInstall")))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" ||
                    !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
                    !string.IsNullOrEmpty(parsed.Fragment) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(parsed.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$"))
                    status = OverlayLocalization.T("settings.plugin.git.invalid_url", "Use a credential-free HTTPS repository URL with no query or fragment.");
                else
                    Submit(new { action = "install", url, trust });
                trust = false;
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped(OverlayLocalization.T("settings.plugin.git.restart", "New installs: use Reload all plugins; no restart required. Updates activate on next launch. Failures retain existing plugins and configuration."));
            ImGui.TextWrapped(status);
            foreach (var source in Volatile.Read(ref sources))
            {
                ImGui.TextUnformatted($"{source.name}: {source.status}");
                ImGui.TextUnformatted(OverlayLocalization.F("settings.plugin.git.versions", "Installed: {0}  Latest build: {1}", source.installedVersion, source.version));
                var update = source.update;
                if (ImGui.Checkbox(OverlayLocalization.Label("settings.plugin.git.update", "Check for updates at startup", "GitUpdate" + source.id), ref update))
                    Submit(new { action = "toggle", id = source.id, update });
            }
        }
    }
}
