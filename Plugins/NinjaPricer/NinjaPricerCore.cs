// <copyright file="NinjaPricerCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace NinjaPricer;

using System.Numerics;
using GameHelper.Plugin;
using GameHelper.Plugin.Price;
using ImGuiNET;
using Newtonsoft.Json;

public sealed class NinjaPricerCore : PCore<NinjaPricerSettings>
{
    private ProviderRegistration? registration;
    private NinjaPriceProvider? adapter;
    private string registrationStatus = "Disabled";
    private int selectedLeagueIndex = -1;

    private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

    public override void OnEnable(bool isGameOpened)
    {
        this.LoadSettings();
        NinjaPricerSettingsNormalizer.Normalize(this.Settings);
        Directory.CreateDirectory(this.DllDirectory);
        PriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League, this.Settings.RefreshIntervalMin);

        this.adapter = new NinjaPriceProvider(() => this.Settings, this.DllDirectory);
        this.registration = new ProviderRegistration(this, this.adapter);
        if (!this.registration.Register())
        {
            var incumbent = PriceProviderRegistry.Current?.Status.ProviderName ?? "another plugin";
            this.registrationStatus = $"Inactive: {incumbent} already owns the shared price provider. Enable NinjaPricer first.";
            Console.WriteLine($"[NinjaPricer] {this.registrationStatus}");
            return;
        }

        this.registrationStatus = "Active: registered as the shared price provider.";
        LeagueProvider.EnsureLoaded();
        PriceFetcher.Initialize(this.DllDirectory);
    }

    public override void OnDisable()
    {
        PriceFetcher.Shutdown();
        LeagueProvider.Shutdown();
        if (this.registration?.Unregister() == true)
        {
            this.registrationStatus = "Disabled: shared provider unregistered.";
        }

        this.registration = null;
        this.adapter = null;
    }

    public override void DrawUI()
    {
        if (this.registration?.IsRegistered == true)
        {
            PriceFetcher.RefreshIfNeeded();
        }
    }

    public override void DrawSettings()
    {
        LeagueProvider.EnsureLoaded();
        ImGui.Text("Price source:");
        if (ImGui.RadioButton("poe.ninja", this.Settings.PriceSource == PriceFetcher.SourcePoeNinja))
        {
            this.Settings.PriceSource = PriceFetcher.SourcePoeNinja;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("poe2scout", this.Settings.PriceSource == PriceFetcher.SourcePoe2Scout))
        {
            this.Settings.PriceSource = PriceFetcher.SourcePoe2Scout;
        }

        if (PriceFetcher.IsUsingFallback)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), this.PluginText.F(
                "status.automatic_fallback", "Automatic fallback active: {0}", PriceFetcher.ActiveSourceName));
        }

        this.DrawLeagueSelector();
        ImGui.SliderInt("Refresh interval (min)", ref this.Settings.RefreshIntervalMin, 1, 120);
        NinjaPricerSettingsNormalizer.Normalize(this.Settings);
        PriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League, this.Settings.RefreshIntervalMin);

        if (ImGui.Button("Refresh Prices Now") && this.registration?.IsRegistered == true)
        {
            PriceFetcher.ForceRefresh(this.DllDirectory);
        }

        ImGui.SameLine();
        if (PriceFetcher.IsFetching || LeagueProvider.IsLoading)
        {
            var status = PriceFetcher.IsFailingOver
                ? this.PluginText.F("status.switching_provider", "Switching to {0}...", PriceFetcher.FetchingSourceName)
                : PriceFetcher.IsFetching
                    ? this.PluginText.F("status.loading_provider", "Loading from {0}...", PriceFetcher.FetchingSourceName)
                    : "Loading...";
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), status);
        }
        else if (PriceFetcher.LastFetchUtc > DateTime.MinValue)
        {
            var minutes = Math.Max(0, (int)(DateTime.UtcNow - PriceFetcher.LastFetchUtc).TotalMinutes);
            ImGui.TextColored(
                new Vector4(0.5f, 0.8f, 0.5f, 1f),
                $"{PriceFetcher.LoadedItemCount} items | {minutes} min ago");
        }

        var failureWarning = PriceFetcher.GetFailureWarningText(DateTime.UtcNow);
        if (!string.IsNullOrEmpty(failureWarning))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.25f, 1f));
            ImGui.TextWrapped(failureWarning);
            ImGui.PopStyleColor();
        }

        var statusColor = this.registration?.IsRegistered == true
            ? new Vector4(0.5f, 0.8f, 0.5f, 1f)
            : new Vector4(0.9f, 0.45f, 0.35f, 1f);
        ImGui.TextColored(statusColor, this.registrationStatus);
        ImGui.TextWrapped("Passive estimates from poe2scout / poe.ninja. No game-memory reads or input automation.");
    }

    public override void SaveSettings()
    {
        try
        {
            NinjaPricerSettingsNormalizer.Normalize(this.Settings);
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? this.DllDirectory);
            File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NinjaPricer] Failed to save settings: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        if (!File.Exists(this.SettingPathname))
        {
            return;
        }

        try
        {
            this.Settings = JsonConvert.DeserializeObject<NinjaPricerSettings>(File.ReadAllText(this.SettingPathname))
                ?? new NinjaPricerSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NinjaPricer] Failed to load settings: {ex.Message}");
            this.Settings = new NinjaPricerSettings();
        }
    }

    private void DrawLeagueSelector()
    {
        var leagues = LeagueProvider.Leagues;
        if (leagues.Count == 0)
        {
            ImGui.InputText("League", ref this.Settings.League, 64);
            return;
        }

        if (this.selectedLeagueIndex < 0 || this.selectedLeagueIndex >= leagues.Count ||
            !string.Equals(leagues[this.selectedLeagueIndex], this.Settings.League, StringComparison.OrdinalIgnoreCase))
        {
            this.selectedLeagueIndex = 0;
            for (var i = 0; i < leagues.Count; i++)
            {
                if (string.Equals(leagues[i], this.Settings.League, StringComparison.OrdinalIgnoreCase))
                {
                    this.selectedLeagueIndex = i;
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(260f);
        if (!ImGui.BeginCombo("League", leagues[this.selectedLeagueIndex]))
        {
            return;
        }

        for (var i = 0; i < leagues.Count; i++)
        {
            if (ImGui.Selectable(leagues[i], i == this.selectedLeagueIndex))
            {
                this.selectedLeagueIndex = i;
                this.Settings.League = leagues[i];
            }
        }

        ImGui.EndCombo();
    }
}
