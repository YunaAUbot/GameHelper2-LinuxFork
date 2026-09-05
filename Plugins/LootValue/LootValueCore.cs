// <copyright file="LootValueCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.Plugin.Price;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    ///     LootValue plugin — prices ground, stash, and inventory items and draws their values in context.
    ///     Unidentified uniques are revealed by name via their icon art (same bridge as RitualHelper).
    /// </summary>
    public sealed class LootValueCore : PCore<LootValueSettings>
    {
        private const string ItemPathPrefix = "Metadata/Items";
        private const int UiElementItemAddressOffset = 0x4F8;
        private static readonly int[] CurrencyExchangeRootPath = { 114, 20, 6 };

        private readonly List<LootLabel> cachedLabels = new();
        private readonly Dictionary<uint, Tracked> trackWorld = new();
        private DateTime nextRecomputeUtc = DateTime.MinValue;

        private readonly List<string> diagSamples = new();
        private string diagSummary = string.Empty;
        private DateTime nextDiagUtc = DateTime.MinValue;

        // Loot-tag mode (anchors chips to the game's loot labels via a throttled UI-tree scan).
        private const int UiElementTextOffset = 0x390;
        private const int SlotTraversalBudgetPerFrame = 64;
        private const int SlotRectangleBudgetPerFrame = 8;
        private const int SlotValidationBudgetPerFrame = 8;
        private const int SlotPricingBudgetPerFrame = 8;
        private static readonly int ScrollRectangleProbeBudgetPerFrame = ScrollProbePolicy.RequiredBudget(
            SlotTraversalBudgetPerFrame,
            3,
            2,
            2);
        private const int MaxSlotTraversalElements = 5000;
        private readonly List<TagChip> cachedTagChips = new();
        private readonly Dictionary<IntPtr, Tracked> trackTag = new();
        private DateTime nextTagScanUtc = DateTime.MinValue;
        private object? handleObj;
        private object? uiParentsObj;
        private MethodInfo? readUiOffsetMethod;
        private MethodInfo? readStdVectorMethod;
        private MethodInfo? readStdWStringStructMethod;
        private MethodInfo? readStdWStringMethod;
        private MethodInfo? readIntPtrMethod;
        private readonly HashSet<string> groundTagNames = new(StringComparer.OrdinalIgnoreCase);
        private SlotScanReport leftSlotReport = new(IntPtr.Zero);
        private SlotScanReport rightSlotReport = new(IntPtr.Zero);
        private IReadOnlyList<SlotInfo> cachedLeftSlots = Array.Empty<SlotInfo>();
        private IReadOnlyList<SlotInfo> cachedRightSlots = Array.Empty<SlotInfo>();
        private IntPtr cachedLeftPanelAddress;
        private IntPtr cachedRightPanelAddress;
        private DateTime nextSlotScanUtc = DateTime.MinValue;
        private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>? leftSlotScan;
        private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>? rightSlotScan;
        private readonly IncrementalPanelScanScheduler<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> slotScanScheduler = new();
        private SlotScanContext? leftSlotScanContext;
        private SlotScanContext? rightSlotScanContext;
        private ScrollRectangleProbeBudget scrollRectangleProbeBudget;
        private readonly List<ExchangePriceLabel> cachedExchangeLabels = new();
        private DateTime nextExchangeScanUtc = DateTime.MinValue;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            var shouldMigrateStashSettings = true;
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var settingsJson = File.ReadAllText(this.SettingPathname);
                    shouldMigrateStashSettings = JObject.Parse(settingsJson)[nameof(LootValueSettings.ShowStashOverlay)] == null;
                    this.Settings = JsonConvert.DeserializeObject<LootValueSettings>(settingsJson) ?? new LootValueSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LootValue] Failed to load settings: {ex.Message}");
                    this.Settings = new LootValueSettings();
                }
            }

            if (shouldMigrateStashSettings && this.TryMigrateStashValueSettings())
            {
                this.SaveSettings();
            }

        }

        private bool TryMigrateStashValueSettings()
        {
            var pluginsDirectory = Directory.GetParent(this.DllDirectory)?.FullName;
            if (pluginsDirectory == null) return false;

            foreach (var pluginName in new[] { "StashValueByZx0", "StashValue" })
            {
                var legacyPath = Path.Join(pluginsDirectory, pluginName, "config", "settings.txt");
                if (!File.Exists(legacyPath)) continue;

                try
                {
                    var legacy = JObject.Parse(File.ReadAllText(legacyPath));
                    this.Settings.ShowStashOverlay = legacy.Value<bool?>("ShowOverlay") ?? this.Settings.ShowStashOverlay;
                    this.Settings.ShowInventoryOverlay = legacy.Value<bool?>("ShowInventoryOverlay") ?? this.Settings.ShowInventoryOverlay;
                    this.Settings.HideSlotPricesOnHover = legacy.Value<bool?>("HidePriceOnHover") ?? this.Settings.HideSlotPricesOnHover;
                    this.Settings.ShowSlotDebugInfo = legacy.Value<bool?>("ShowDebugInfo") ?? this.Settings.ShowSlotDebugInfo;
                    this.Settings.SlotFontScale = legacy.Value<float?>("PriceFontScale") ?? this.Settings.SlotFontScale;
                    this.Settings.SlotOffsetX = legacy.Value<float?>("PriceOffsetX") ?? this.Settings.SlotOffsetX;
                    this.Settings.SlotOffsetY = legacy.Value<float?>("PriceOffsetY") ?? this.Settings.SlotOffsetY;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LootValue] Failed to migrate {pluginName} settings: {ex.Message}");
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.cachedLabels.Clear();
            this.cachedTagChips.Clear();
            this.trackWorld.Clear();
            this.trackTag.Clear();
            this.nextRecomputeUtc = DateTime.MinValue;
            this.nextTagScanUtc = DateTime.MinValue;
            this.handleObj = null;
            this.uiParentsObj = null;
            this.readUiOffsetMethod = null;
            this.readStdVectorMethod = null;
            this.readStdWStringStructMethod = null;
            this.readStdWStringMethod = null;
            this.readIntPtrMethod = null;
            this.groundTagNames.Clear();
            this.ClearSlotScans();
            this.cachedExchangeLabels.Clear();
            this.nextExchangeScanUtc = DateTime.MinValue;
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LootValue] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox(this.PluginText.Label("settings.show_overlay", "Show value over ground items", "LootValueShowOverlay"), ref this.Settings.ShowOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.anchor_to_loot_tags", "Anchor to loot labels (no overlap when items pile up)", "LootValueAnchorToLootTags"), ref this.Settings.AnchorToLootTags);
            ImGui.Checkbox(this.PluginText.Label("settings.show_stash_overlay", "Show value over stash items", "LootValueShowStashOverlay"), ref this.Settings.ShowStashOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.show_inventory_overlay", "Show value over inventory items", "LootValueShowInventoryOverlay"), ref this.Settings.ShowInventoryOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.show_currency_exchange_overlay", "Show owned-stack values in Currency Exchange", "LootValueShowCurrencyExchangeOverlay"), ref this.Settings.ShowCurrencyExchangeOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_when_game_unfocused", "Hide values when game is not focused", "LootValueHideWhenGameUnfocused"), ref this.Settings.HideWhenGameInBackground);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_slot_prices_on_hover", "Hide stash/inventory values while hovering an item", "LootValueHideSlotPricesOnHover"), ref this.Settings.HideSlotPricesOnHover);
            ImGui.Checkbox(this.PluginText.Label("settings.reveal_unidentified_uniques", "Reveal unidentified uniques (by art)", "LootValueRevealUnidentifiedUniques"), ref this.Settings.RevealUnidentifiedUniques);
            ImGui.Checkbox(this.PluginText.Label("settings.diagnostics_window", "Diagnostics window", "LootValueDiagnosticsWindow"), ref this.Settings.DiagnosticsMode);
            ImGui.Checkbox(this.PluginText.Label("settings.slot_diagnostics", "Stash/inventory slot diagnostics", "LootValueSlotDiagnostics"), ref this.Settings.ShowSlotDebugInfo);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.display", "Display"));
            if (ImGui.RadioButton(this.PluginText.Label("currency.chaos", "Chaos", "LootValueCurrencyChaos"), this.Settings.DisplayCurrency == 2)) this.Settings.DisplayCurrency = 2;
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.exalted", "Exalted", "LootValueCurrencyExalted"), this.Settings.DisplayCurrency == 1)) this.Settings.DisplayCurrency = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.divine", "Divine", "LootValueCurrencyDivine"), this.Settings.DisplayCurrency == 0)) this.Settings.DisplayCurrency = 0;

            ImGui.SliderFloat(this.PluginText.Label("settings.min_value_to_show", "Min value to show (ex)", "LootValueMinValueToShow"), ref this.Settings.MinValueEx, 0f, 50f, "%.2f");
            ImGui.SliderFloat(this.PluginText.Label("settings.highlight_from", "Highlight from (ex)", "LootValueHighlightFrom"), ref this.Settings.HighlightMinEx, 0f, 200f, "%.1f");
            ImGui.SliderFloat(this.PluginText.Label("settings.font_size", "Font size", "LootValueFontSize"), ref this.Settings.FontSize, 8f, 48f, "%.0f");
            ImGui.SliderFloat(this.PluginText.Label("settings.highlight_font_size", "Highlight font size", "LootValueHighlightFontSize"), ref this.Settings.HighlightFontSize, 8f, 64f, "%.0f");
            ImGui.Checkbox(this.PluginText.Label("settings.highlight_bold", "Highlight bold", "LootValueHighlightBold"), ref this.Settings.HighlightBold);
            ImGui.SliderFloat(this.PluginText.Label("settings.vertical_offset", "Vertical offset", "LootValueVerticalOffset"), ref this.Settings.OffsetY, -50f, 50f);
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_font_scale", "Stash/inventory font scale", "LootValueSlotFontScale"), ref this.Settings.SlotFontScale, 0.5f, 2f, "%.2f");
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_horizontal_offset", "Stash/inventory horizontal offset", "LootValueSlotOffsetX"), ref this.Settings.SlotOffsetX, -50f, 50f);
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_vertical_offset", "Stash/inventory vertical offset", "LootValueSlotOffsetY"), ref this.Settings.SlotOffsetY, -50f, 50f);
            ImGui.Checkbox(this.PluginText.Label("settings.smooth_label_motion", "Smooth label motion (velocity tracking)", "LootValueSmoothLabelMotion"), ref this.Settings.InterpolatePosition);
            if (this.Settings.InterpolatePosition)
            {
                ImGui.SliderInt(this.PluginText.Label("settings.jitter_filter", "Jitter filter (lower=stronger, no lag)", "LootValueJitterFilter"), ref this.Settings.InterpolationRate, 1, 1000);
            }

            ImGui.SliderInt(this.PluginText.Label("settings.rescan_interval", "Rescan interval (ms)", "LootValueRescanInterval"), ref this.Settings.RescanIntervalMs, 16, 1000);
            ImGui.TextDisabled(this.PluginText.T("settings.rescan_interval.tooltip", "Positions redraw every frame; rescan only re-detects items/prices."));
            ImGui.SliderInt(this.PluginText.Label("settings.slot_rescan_interval", "Stash/inventory rescan interval (ms)", "LootValueSlotRescanInterval"), ref this.Settings.SlotRescanIntervalMs, 100, 2000);
            ImGui.TextDisabled(this.PluginText.T("settings.slot_rescan_interval.tooltip", "Cached slot values draw every frame; panel traversal and pricing run at this interval."));

            ImGui.ColorEdit4(this.PluginText.Label("settings.text_color", "Text color", "LootValueTextColor"), ref this.Settings.TextColor);
            ImGui.ColorEdit4(this.PluginText.Label("settings.highlight_color", "Highlight color", "LootValueHighlightColor"), ref this.Settings.HighlightColor);

        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            this.scrollRectangleProbeBudget = new ScrollRectangleProbeBudget(ScrollRectangleProbeBudgetPerFrame);
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                this.ClearSlotScans();
                return;
            }
            var pricing = LootValuePricingPass.Capture(() => PriceProviderRegistry.Current);

            if (this.Settings.DiagnosticsMode)
            {
                this.RunDiagnostics(pricing);
                this.DrawDiagnosticsWindow();
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (this.Settings.ShowOverlay && this.Settings.AnchorToLootTags)
            {
                if (this.EnsureReflection())
                {
                    if (now >= this.nextTagScanUtc)
                    {
                        this.nextTagScanUtc = now.AddMilliseconds(Math.Max(16, this.Settings.RescanIntervalMs));
                        this.ScanLootTags(pricing);
                    }

                    this.DrawTagChips();
                }
            }
            else if (this.Settings.ShowOverlay)
            {
                if (now >= this.nextRecomputeUtc)
                {
                    this.nextRecomputeUtc = now.AddMilliseconds(Math.Max(16, this.Settings.RescanIntervalMs));
                    this.RecomputeLabels(pricing);
                }

                this.DrawLabels();
            }

            if (this.Settings.ShowStashOverlay || this.Settings.ShowInventoryOverlay || this.Settings.ShowSlotDebugInfo)
            {
                this.DrawItemSlotValues(pricing);
            }
            else
            {
                this.ClearSlotScans();
            }

            if (this.Settings.ShowCurrencyExchangeOverlay)
            {
                this.DrawCurrencyExchangeValues(pricing);
            }
        }

        /// <summary>Re-reads + reprices every ground item; throttled. The drawn position is updated live each frame.</summary>
        private void RecomputeLabels(LootValuePricingPass pricing)
        {
            this.cachedLabels.Clear();

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            foreach (var entity in area.AwakeEntities.Values)
            {
                // Ground drops are identified by the WorldItem component (path-independent — the wrapper
                // entity's own path is not "Metadata/Items"; that's the inner item).
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero) continue;
                if (!entity.TryGetComponent<Render>(out var render)) continue;

                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                if (!this.TryPriceItem(pricing, item, out var valueEx, out var label)) continue;
                if (valueEx < this.Settings.MinValueEx) continue;

                var highlight = valueEx >= this.Settings.HighlightMinEx;
                var color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
                this.cachedLabels.Add(new LootLabel(entity.Id, render, label, color, highlight));
            }

            // Drop tracker state for items no longer present (picked up / left the area).
            if (this.trackWorld.Count > 0)
            {
                var live = new HashSet<uint>(this.cachedLabels.Count);
                foreach (var l in this.cachedLabels) live.Add(l.EntityId);
                this.trackWorld.Keys.Where(k => !live.Contains(k)).ToList().ForEach(k => this.trackWorld.Remove(k));
            }
        }

        private void DrawLabels()
        {
            if (this.cachedLabels.Count == 0) return;

            var fg = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();
            var world = Core.States.InGameStateObject.CurrentWorldInstance;

            foreach (var label in this.cachedLabels)
            {
                // Anchor to the GROUND (stable TerrainHeight), not WorldPosition.Z — that Z is the item's
                // animated/bobbing model height, which makes the projected point oscillate. TerrainHeight is
                // constant for a stationary drop, so the only moving input becomes the camera (smoothed below).
                var screen = world.WorldToScreen(label.Render.WorldPosition, label.Render.TerrainHeight);
                if (screen == Vector2.Zero) continue;

                // Velocity-tracking filter: GH samples the camera at 120Hz from a 90Hz source, so the raw
                // projected point of a STATIC item beats ~1-2px along the path. Tracking screen velocity and
                // advancing by it each frame removes that without the lag a plain low-pass would add.
                if (this.Settings.InterpolatePosition)
                {
                    screen = Track(this.trackWorld, label.EntityId, screen, this.Settings.InterpolationRate);
                }

                var fontSize = label.Highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                var textWidth = ImGui.CalcTextSize(label.Text).X * (fontSize / baseSize);
                var pos = new Vector2(screen.X - (textWidth / 2f), screen.Y + this.Settings.OffsetY);
                this.DrawValueLabel(fg, font, baseSize, pos, label.Text, label.Color, label.Highlight);
            }
        }

        /// <summary>Draws one value label (background chip + shadowed text, faux-bold when highlighted)
        /// at the given top-left screen position. Shared by world-space and loot-tag modes.</summary>
        private void DrawValueLabel(ImDrawListPtr fg, ImFontPtr font, float baseSize, Vector2 pos, string text, uint color, bool highlight)
        {
            const uint shadow = 0xCC000000u;
            var fontSize = highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
            var bold = highlight && this.Settings.HighlightBold;
            var textWidth = ImGui.CalcTextSize(text).X * (fontSize / baseSize);

            fg.AddRectFilled(pos - new Vector2(3f, 1f), pos + new Vector2(textWidth + 3f, fontSize + 1f), 0xB0000000u, 3f);
            fg.AddText(font, fontSize, pos + new Vector2(1f, 1f), shadow, text);
            fg.AddText(font, fontSize, pos, color, text);
            if (bold)
            {
                // Faux-bold: redraw offset by 1px so the glyphs thicken.
                fg.AddText(font, fontSize, pos + new Vector2(1f, 0f), color, text);
            }
        }

        // ---- Loot-tag mode: anchor value chips to the game's loot labels (found via a UI-tree scan) ----

        private bool EnsureReflection()
        {
            if (this.handleObj != null) return true;
            var handleProp = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            this.handleObj = handleProp?.GetValue(Core.Process);
            if (this.handleObj == null) return false;

            var methods = this.handleObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var readMem = methods.First(m => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
            var readVec = methods.First(m => m.Name == "ReadStdVector" && m.IsGenericMethod);
            this.readUiOffsetMethod = readMem.MakeGenericMethod(typeof(UiElementBaseOffset));
            this.readStdVectorMethod = readVec.MakeGenericMethod(typeof(IntPtr));
            this.readStdWStringStructMethod = readMem.MakeGenericMethod(typeof(StdWString));
            this.readStdWStringMethod = methods.First(m => m.Name == "ReadStdWString" && m.GetParameters().Length == 1);
            this.readIntPtrMethod = readMem.MakeGenericMethod(typeof(IntPtr));
            return true;
        }

        private string ReadUiElementText(IntPtr element)
        {
            try
            {
                var ws = this.readStdWStringStructMethod!.Invoke(this.handleObj, new object[] { element + UiElementTextOffset });
                if (ws == null) return string.Empty;
                return this.readStdWStringMethod!.Invoke(this.handleObj, new object[] { ws }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>BFS the visible UI tree; any text element that prices as a loot drop becomes a chip
        /// anchored to that element. Throttled; the element's live rect is re-read each frame when drawing.</summary>
        private void ScanLootTags(LootValuePricingPass pricing)
        {
            this.cachedTagChips.Clear();
            this.RefreshGroundTagNames(pricing);
            var gameUi = Core.States.InGameStateObject.GameUi;
            var root = gameUi.Address;
            var leftPanel = gameUi.LeftPanel.Address;
            var rightPanel = gameUi.RightPanel.Address;
            if (root == IntPtr.Zero || this.readUiOffsetMethod == null || this.readStdVectorMethod == null) return;

            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(root);
            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;
                // Stash, inventory, vendor, and other large-panel text cannot be a ground loot label.
                // Do not traverse those potentially enormous subtrees when a panel is open.
                if (el != root && (el == leftPanel || el == rightPanel)) continue;
                if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { el }) is not UiElementBaseOffset off) continue;
                if (el != root && !UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                if (this.readStdVectorMethod.Invoke(this.handleObj, new object[] { off.ChildrensPtr }) is IntPtr[] kids)
                {
                    foreach (var k in kids) queue.Enqueue(k);
                }

                var text = this.ReadUiElementText(el);
                if (text.Length < 3) continue;
                var firstLine = text.Split('\n')[0].Trim();
                if (firstLine.Length < 3) continue;

                if (this.TryPriceTagText(pricing, firstLine, out var chipText, out var color, out var highlight))
                {
                    this.cachedTagChips.Add(new TagChip(el, chipText, color, highlight));
                }
            }

            // Drop tracker state for labels that are gone (item picked up / left the area).
            if (this.trackTag.Count > 0)
            {
                var live = new HashSet<IntPtr>(this.cachedTagChips.Count);
                foreach (var c in this.cachedTagChips) live.Add(c.ElementAddress);
                this.trackTag.Keys.Where(k => !live.Contains(k)).ToList().ForEach(k => this.trackTag.Remove(k));
            }
        }

        private bool TryPriceTagText(LootValuePricingPass pricing, string text, out string chipText, out uint color, out bool highlight)
        {
            chipText = string.Empty;
            color = 0;
            highlight = false;

            var count = 1;
            var name = text;
            var m = Regex.Match(text, @"^(\d+)\s*x\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int.TryParse(m.Groups[1].Value, out count);
                name = m.Groups[2].Value;
            }

            name = name.Trim();
            if (name.Length < 3) return false;
            if (!this.groundTagNames.Contains(name)) return false;

            var query = CreateQuery(name, Array.Empty<string>(), string.Empty, string.Empty, text);
            if (!pricing.TryPrice(query, Math.Max(1, count), this.Settings.DisplayCurrency, out var price)) return false;
            var exVal = (double)price.ExaltedValue;
            if (exVal < this.Settings.MinValueEx) return false;

            chipText = price.Text;
            highlight = exVal >= this.Settings.HighlightMinEx;
            color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
            return true;
        }

        private void DrawTagChips()
        {
            if (this.cachedTagChips.Count == 0) return;
            this.uiParentsObj ??= PluginUiElementReflection.CreateParents();
            if (this.uiParentsObj == null) return;

            var fg = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();

            foreach (var chip in this.cachedTagChips)
            {
                // Pre-validate the address with a cheap raw read BEFORE constructing the UiElement: a real
                // UI element is self-referential (Self == its own address). If the element was freed since
                // the scan (e.g. item picked up), this no longer holds — skip it so CreateUiElement (which
                // would THROW on an invalid address) is never reached. try/catch remains as a backstop.
                if (this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { chip.ElementAddress }) is not UiElementBaseOffset off) continue;
                if (off.Self != IntPtr.Zero && off.Self != chip.ElementAddress) continue; // exact inverse of the game's "not a Ui Element" guard
                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                try
                {
                    var el = PluginUiElementReflection.CreateUiElement(chip.ElementAddress, this.uiParentsObj);
                    if (el == null) continue;

                    var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                    var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                    if (size.X <= 0f || pos == Vector2.Zero) continue;

                    var fontSize = chip.Highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                    var chipPos = new Vector2(pos.X + size.X + 6f, pos.Y + ((size.Y - fontSize) / 2f));

                    // Same velocity-tracking filter as world mode (the read rect beats against the game's
                    // update rate the same way), keyed by the label element.
                    if (this.Settings.InterpolatePosition)
                    {
                        chipPos = Track(this.trackTag, chip.ElementAddress, chipPos, this.Settings.InterpolationRate);
                    }

                    this.DrawValueLabel(fg, font, baseSize, chipPos, chip.Text, chip.Color, chip.Highlight);
                }
                catch
                {
                    // Stale/freed loot label — drop it; the next scan rebuilds from live elements.
                }
            }
        }

        /// <summary>
        /// Restricts loot-label matching to names backed by live ground-item entities. The game UI contains
        /// many unrelated text nodes (stash search, vendor listings, tooltips) whose text can also be priced;
        /// those must not be mistaken for ground labels.
        /// </summary>
        private void RefreshGroundTagNames(LootValuePricingPass pricing)
        {
            this.groundTagNames.Clear();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero) continue;
                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                if (item.TryGetComponent<Base>(out var baseComp) && !string.IsNullOrWhiteSpace(baseComp.BaseItemName))
                {
                    this.groundTagNames.Add(baseComp.BaseItemName.Trim());
                }

                if (!item.TryGetComponent<Mods>(out var mods) || mods.Rarity != Rarity.Unique ||
                    !item.TryGetComponent<RenderItem>(out var renderItem)) continue;

                foreach (var key in ArtKeyVariants(ExtractArtBasename(renderItem.ResourcePath)))
                {
                    if (pricing.TryResolveDisplayName(key, out var uniqueName) &&
                        !pricing.IsGenericLookupName(uniqueName))
                    {
                        this.groundTagNames.Add(uniqueName.Trim());
                    }
                }
            }
        }

        /// <summary>Draws cached owned-stack values in the Currency Exchange item browser.</summary>
        private void DrawCurrencyExchangeValues(LootValuePricingPass pricing)
        {
            if (!this.EnsureReflection()) return;

            var now = DateTime.UtcNow;
            if (now >= this.nextExchangeScanUtc)
            {
                this.nextExchangeScanUtc = now.AddMilliseconds(Math.Clamp(this.Settings.SlotRescanIntervalMs, 100, 2000));
                this.ScanCurrencyExchange(pricing);
            }

            if (this.cachedExchangeLabels.Count == 0) return;
            var foreground = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();
            foreach (var label in this.cachedExchangeLabels)
            {
                this.DrawValueLabel(
                    foreground,
                    font,
                    baseSize,
                    label.Position,
                    label.Text,
                    label.Color,
                    label.Highlight);
            }
        }

        private void ScanCurrencyExchange(LootValuePricingPass pricing)
        {
            this.cachedExchangeLabels.Clear();
            var root = this.ResolveUiPath(Core.States.InGameStateObject.GameUi.Address, CurrencyExchangeRootPath);
            if (root == IntPtr.Zero || !this.TryGetVisibleChildren(root, out var rootChildren) || rootChildren.Length <= 1) return;

            // [114][20][6][1] is the complete item list. Its visibility is the reliable signal that
            // Currency Exchange is open; only categories enabled by the selected tab are visible below it.
            var listAddress = rootChildren[1];
            if (!this.TryGetVisibleChildren(listAddress, out var categoryAddresses)) return;
            if (!PluginUiElementReflection.TryGetAbsoluteRect(root, out var viewportPosition, out var viewportSize)) return;
            var viewportMax = viewportPosition + viewportSize;

            foreach (var categoryAddress in categoryAddresses)
            {
                if (!this.TryGetVisibleChildren(categoryAddress, out var groupAddresses)) continue;
                foreach (var groupAddress in groupAddresses)
                {
                    if (!this.TryGetVisibleChildren(groupAddress, out var rowAddresses)) continue;

                    // Child 0 is the group headline. Every following populated child is an item row:
                    // [0] name, [1] icon container, [1][0] owned amount.
                    for (var rowIndex = 1; rowIndex < rowAddresses.Length; rowIndex++)
                    {
                        var rowAddress = rowAddresses[rowIndex];
                        if (!this.TryGetVisibleChildren(rowAddress, out var rowChildren) || rowChildren.Length <= 1) continue;
                        var nameAddress = rowChildren[0];
                        var iconAddress = rowChildren[1];
                        if (!this.TryGetVisibleChildren(iconAddress, out var iconChildren) || iconChildren.Length == 0) continue;

                        var name = this.ReadUiElementText(nameAddress).Split('\n')[0].Trim();
                        var amountText = this.ReadUiElementText(iconChildren[0]);
                        if (name.Length < 2 || !TryParseOwnedAmount(amountText, out var amount) || amount <= 0) continue;
                        if (!this.TryPriceNamedStack(pricing, name, amount, out var text, out var color, out var highlight)) continue;
                        if (!PluginUiElementReflection.TryGetAbsoluteRect(iconAddress, out var iconPosition, out var iconSize)) continue;

                        var center = iconPosition + (iconSize * 0.5f);
                        if (center.X < viewportPosition.X || center.X > viewportMax.X ||
                            center.Y < viewportPosition.Y || center.Y > viewportMax.Y) continue;

                        var fontSize = highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                        var labelPosition = new Vector2(
                            iconPosition.X + this.Settings.SlotOffsetX,
                            iconPosition.Y + iconSize.Y - fontSize + this.Settings.SlotOffsetY);
                        this.cachedExchangeLabels.Add(new ExchangePriceLabel(labelPosition, text, color, highlight));
                    }
                }
            }
        }

        private bool TryGetVisibleChildren(IntPtr address, out IntPtr[] children)
        {
            return this.TryGetChildren(address, requireVisible: true, out children);
        }

        private bool TryGetChildren(IntPtr address, bool requireVisible, out IntPtr[] children)
        {
            children = Array.Empty<IntPtr>();
            if (address == IntPtr.Zero || this.readUiOffsetMethod == null || this.readStdVectorMethod == null ||
                this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { address }) is not UiElementBaseOffset offset ||
                (requireVisible && !UiElementBaseFuncs.IsVisibleChecker(offset.Flags))) return false;

            children = this.readStdVectorMethod.Invoke(this.handleObj, new object[] { offset.ChildrensPtr }) as IntPtr[] ?? Array.Empty<IntPtr>();
            return true;
        }

        private IntPtr ResolveUiPath(IntPtr root, IReadOnlyList<int> path)
        {
            var current = root;
            foreach (var childIndex in path)
            {
                if (!this.TryGetChildren(current, requireVisible: false, out var children) ||
                    childIndex < 0 || childIndex >= children.Length) return IntPtr.Zero;
                current = children[childIndex];
            }

            return current;
        }

        private static bool TryParseOwnedAmount(string text, out long amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var digits = Regex.Replace(text, @"[^0-9]", string.Empty);
            return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
        }

        private bool TryPriceNamedStack(
            LootValuePricingPass pricing,
            string itemName,
            long amount,
            out string text,
            out uint color,
            out bool highlight)
        {
            text = string.Empty;
            color = 0;
            highlight = false;
            var query = CreateQuery(itemName, Array.Empty<string>(), string.Empty, string.Empty, itemName);
            if (!pricing.TryPrice(query, amount, this.Settings.DisplayCurrency, out var price)) return false;
            var exValue = (double)price.ExaltedValue;
            if (exValue < this.Settings.MinValueEx) return false;

            text = price.Text;
            highlight = exValue >= this.Settings.HighlightMinEx;
            color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
            return true;
        }

        /// <summary>Prices item slots in the open stash and inventory panels.</summary>
        private void DrawItemSlotValues(LootValuePricingPass pricing)
        {
            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi.Address == IntPtr.Zero || !this.EnsureReflection())
            {
                this.ClearSlotScans();
                return;
            }

            var scanLeft = this.Settings.ShowStashOverlay || this.Settings.ShowSlotDebugInfo;
            var scanRight = this.Settings.ShowInventoryOverlay || this.Settings.ShowSlotDebugInfo;
            var leftAddress = scanLeft && gameUi.LeftPanel.IsVisible ? gameUi.LeftPanel.Address : IntPtr.Zero;
            var rightAddress = scanRight && gameUi.RightPanel.IsVisible ? gameUi.RightPanel.Address : IntPtr.Zero;

            if (leftAddress != this.cachedLeftPanelAddress || rightAddress != this.cachedRightPanelAddress)
            {
                this.cachedLeftPanelAddress = leftAddress;
                this.cachedRightPanelAddress = rightAddress;
                this.nextSlotScanUtc = DateTime.MinValue;
                this.RestartSlotScan(
                    isLeft: true,
                    leftAddress,
                    gameUi.LeftPanel.Position,
                    gameUi.LeftPanel.Size,
                    pricing);
                this.RestartSlotScan(
                    isLeft: false,
                    rightAddress,
                    gameUi.RightPanel.Position,
                    gameUi.RightPanel.Size,
                    pricing);
            }

            var now = DateTime.UtcNow;
            this.EnsureSlotScanners();
            var leftScan = this.leftSlotScan!;
            var rightScan = this.rightSlotScan!;
            var scansComplete = leftScan.IsComplete && rightScan.IsComplete;
            if (scansComplete && now >= this.nextSlotScanUtc)
            {
                this.nextSlotScanUtc = DateTime.MinValue;
                this.RestartSlotScan(true, leftAddress, gameUi.LeftPanel.Position, gameUi.LeftPanel.Size, pricing);
                this.RestartSlotScan(false, rightAddress, gameUi.RightPanel.Position, gameUi.RightPanel.Size, pricing);
            }

            var budget = new PanelScanBudget(
                SlotTraversalBudgetPerFrame,
                SlotRectangleBudgetPerFrame,
                SlotValidationBudgetPerFrame,
                SlotPricingBudgetPerFrame);
            this.slotScanScheduler.Advance(leftScan, rightScan, ref budget);
            this.cachedLeftSlots = leftScan.Snapshot;
            this.cachedRightSlots = rightScan.Snapshot;

            if (this.nextSlotScanUtc == DateTime.MinValue &&
                leftScan.IsComplete && rightScan.IsComplete)
            {
                this.leftSlotReport = this.leftSlotScanContext?.Report ?? new SlotScanReport(IntPtr.Zero);
                this.rightSlotReport = this.rightSlotScanContext?.Report ?? new SlotScanReport(IntPtr.Zero);
                this.nextSlotScanUtc = now.AddMilliseconds(Math.Clamp(this.Settings.SlotRescanIntervalMs, 100, 2000));
            }

            var leftScroll = GetScrollFrameState(this.cachedLeftSlots);
            var rightScroll = GetScrollFrameState(this.cachedRightSlots);
            var hidePrices = this.Settings.HideSlotPricesOnHover &&
                             (IsAnySlotHovered(this.cachedLeftSlots, leftScroll) ||
                              IsAnySlotHovered(this.cachedRightSlots, rightScroll));
            this.DrawItemSlots(this.cachedLeftSlots, this.Settings.ShowStashOverlay, hidePrices, leftScroll);
            this.DrawItemSlots(this.cachedRightSlots, this.Settings.ShowInventoryOverlay, hidePrices, rightScroll);
            if (this.Settings.ShowSlotDebugInfo)
            {
                this.DrawSlotDiagnosticsWindow();
            }
        }

        private void EnsureSlotScanners()
        {
            this.leftSlotScan ??= this.CreateSlotScanner(() => this.leftSlotScanContext);
            this.rightSlotScan ??= this.CreateSlotScanner(() => this.rightSlotScanContext);
        }

        private void ClearSlotScans()
        {
            this.cachedLeftSlots = Array.Empty<SlotInfo>();
            this.cachedRightSlots = Array.Empty<SlotInfo>();
            this.cachedLeftPanelAddress = IntPtr.Zero;
            this.cachedRightPanelAddress = IntPtr.Zero;
            this.nextSlotScanUtc = DateTime.MinValue;
            this.leftSlotScan?.Clear();
            this.rightSlotScan?.Clear();
            this.leftSlotScanContext = null;
            this.rightSlotScanContext = null;
            this.leftSlotReport = new SlotScanReport(IntPtr.Zero);
            this.rightSlotReport = new SlotScanReport(IntPtr.Zero);
        }

        private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> CreateSlotScanner(
            Func<SlotScanContext?> getContext) => new(
                node => this.TraverseSlotNode(getContext(), node),
                candidate => this.InspectSlotCandidate(getContext(), candidate),
                work => this.ValidateSlotCandidate(getContext(), work),
                work => this.PriceSlotCandidate(getContext(), work),
                candidate => candidate.ItemAddress,
                MaxSlotTraversalElements);

        private void RestartSlotScan(
            bool isLeft,
            IntPtr panelAddress,
            Vector2 panelPosition,
            Vector2 panelSize,
            LootValuePricingPass pricing)
        {
            this.EnsureSlotScanners();
            var scanner = isLeft ? this.leftSlotScan! : this.rightSlotScan!;
            if (panelAddress == IntPtr.Zero)
            {
                scanner.Clear();
                if (isLeft) this.leftSlotScanContext = null;
                else this.rightSlotScanContext = null;
                return;
            }

            var context = new SlotScanContext(panelAddress, panelPosition, panelSize, pricing);
            if (isLeft) this.leftSlotScanContext = context;
            else this.rightSlotScanContext = context;
            scanner.Restart(panelAddress, new SlotTraversalNode(panelAddress, IntPtr.Zero, default));
        }

        private PanelTraversalStep<SlotTraversalNode, SlotElementCandidate> TraverseSlotNode(
            SlotScanContext? context,
            SlotTraversalNode node)
        {
            if (context == null || node.Address == IntPtr.Zero ||
                context.Visited.Count >= MaxSlotTraversalElements || context.Visited.Contains(node.Address) ||
                this.readUiOffsetMethod == null || this.readStdVectorMethod == null || this.readIntPtrMethod == null)
                return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(Array.Empty<SlotTraversalNode>());

            if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { node.Address }) is not UiElementBaseOffset offset ||
                !UiElementBaseFuncs.IsVisibleChecker(offset.Flags))
            {
                context.Visited.Add(node.Address);
                context.Report.VisitedElements++;
                return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(Array.Empty<SlotTraversalNode>());
            }

            var childNodes = new List<SlotTraversalNode>();
            if (this.readStdVectorMethod.Invoke(this.handleObj, new object[] { offset.ChildrensPtr }) is IntPtr[] children)
            {
                var scrollStatus = this.TryGetScrollContainer(children, out var scrollItemsAddress, out var localScroll);
                if (scrollStatus == ScrollProbeStatus.Unavailable)
                    return PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>.Deferred();
                var hasScrollContainer = scrollStatus == ScrollProbeStatus.Succeeded;
                if (hasScrollContainer)
                {
                    context.Report.ScrollContainers++;
                    context.Report.ScrollOffsetY = localScroll.ScanOffsetY;
                }

                foreach (var child in children)
                {
                    if (childNodes.Count >= MaxSlotTraversalElements) break;
                    childNodes.Add(new SlotTraversalNode(
                        child,
                        node.Address,
                        hasScrollContainer && child == scrollItemsAddress ? localScroll : node.Scroll));
                }
            }

            context.Visited.Add(node.Address);
            context.Report.VisitedElements++;

            var pointerValue = this.readIntPtrMethod.Invoke(this.handleObj, new object[] { node.Address + UiElementItemAddressOffset });
            var itemAddress = pointerValue is IntPtr pointer ? pointer : IntPtr.Zero;
            if (itemAddress == IntPtr.Zero)
                return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(childNodes);

            context.Report.NonZeroPointers++;
            if (context.UniquePointers.Add(itemAddress)) context.Report.UniquePointers++;
            return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(
                childNodes,
                new SlotElementCandidate(node.Address, node.ParentAddress, node.Scroll, itemAddress));
        }

        private PanelCandidateResult<SlotCandidateWork> InspectSlotCandidate(
            SlotScanContext? context,
            SlotElementCandidate candidate)
        {
            if (context == null) return PanelCandidateResult<SlotCandidateWork>.Rejected();
            var panelMax = context.PanelPosition + context.PanelSize;
            if (!TryGetSlotRect(candidate, out var position, out var size)) return PanelCandidateResult<SlotCandidateWork>.Rejected();
            var center = position + (size * 0.5f);
            if (center.X < context.PanelPosition.X || center.X > panelMax.X)
                return PanelCandidateResult<SlotCandidateWork>.Rejected();
            if (!candidate.Scroll.IsActive && (center.Y < context.PanelPosition.Y || center.Y > panelMax.Y))
                return PanelCandidateResult<SlotCandidateWork>.Rejected();

            return PanelCandidateResult<SlotCandidateWork>.Accepted(new SlotCandidateWork(candidate, position, size));
        }

        private bool ValidateSlotCandidate(SlotScanContext? context, SlotCandidateWork work)
        {
            if (context == null) return false;
            if (!PluginUiElementReflection.TryValidateItemAddress(work.Candidate.ItemAddress, out _, out var failureReason))
            {
                context.Report.AddRejected(work.Candidate.ElementAddress, work.Candidate.ItemAddress, failureReason);
                return false;
            }

            work.Item = ReadFreshItem(work.Candidate.ItemAddress);
            if (work.Item == null || string.IsNullOrEmpty(work.Item.Path) ||
                !work.Item.Path.StartsWith(ItemPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                context.Report.AddRejected(work.Candidate.ElementAddress, work.Candidate.ItemAddress, "item changed after validation");
                return false;
            }

            context.Report.ValidItems++;
            return true;
        }

        private PanelCandidateResult<SlotInfo> PriceSlotCandidate(SlotScanContext? context, SlotCandidateWork work)
        {
            if (context == null || work.Item == null ||
                !this.TryPriceItem(context.Pricing, work.Item, out var valueEx, out var valueText, includeUniqueName: false) ||
                valueEx < this.Settings.MinValueEx) return PanelCandidateResult<SlotInfo>.Rejected();

            context.Report.PricedCandidates++;
            context.Report.VisibleSlots++;
            return PanelCandidateResult<SlotInfo>.Accepted(
                new SlotInfo(work.Candidate.ItemAddress, work.Position, work.Size, valueText, work.Candidate.Scroll));
        }

        private ScrollProbeStatus TryGetScrollContainer(
            IntPtr[] children,
            out IntPtr itemsAddress,
            out ScrollBinding scroll)
        {
            itemsAddress = IntPtr.Zero;
            scroll = default;
            if (children.Length <= 2 || children[1] == IntPtr.Zero || children[2] == IntPtr.Zero ||
                this.readUiOffsetMethod == null || this.readStdVectorMethod == null) return ScrollProbeStatus.NotApplicable;

            var contentAddress = children[1];
            var holderAddress = children[2];
            if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { holderAddress }) is not UiElementBaseOffset holderOffset ||
                !UiElementBaseFuncs.IsVisibleChecker(holderOffset.Flags) ||
                this.readStdVectorMethod.Invoke(this.handleObj, new object[] { holderOffset.ChildrensPtr }) is not IntPtr[] holderChildren ||
                holderChildren.Length == 0 || holderChildren[0] == IntPtr.Zero) return ScrollProbeStatus.NotApplicable;

            var thumbAddress = holderChildren[0];
            if (this.scrollRectangleProbeBudget.TryReserve(3) == ScrollProbeStatus.Unavailable)
                return ScrollProbeStatus.Unavailable;
            var contentRead = PluginUiElementReflection.TryGetAbsoluteRect(contentAddress, out var contentPosition, out var contentSize);
            var holderRead = PluginUiElementReflection.TryGetAbsoluteRect(holderAddress, out var holderPosition, out var holderSize);
            var thumbRead = PluginUiElementReflection.TryGetAbsoluteRect(thumbAddress, out var thumbPosition, out var thumbSize);
            if (ScrollProbePolicy.FromReadResults(contentRead, holderRead, thumbRead) == ScrollProbeStatus.Unavailable)
                return ScrollProbeStatus.Unavailable;

            // Shape checks keep ordinary [1]/[2] child layouts from being mistaken for scroll views.
            if (holderSize.X < 4f || holderSize.X > 64f || holderSize.Y < 40f ||
                thumbSize.X < 2f || thumbSize.X > holderSize.X * 1.5f ||
                thumbSize.Y < 8f || thumbSize.Y >= holderSize.Y ||
                contentSize.Y <= holderSize.Y + 1f || holderPosition.X < contentPosition.X ||
                thumbPosition.Y < holderPosition.Y - 2f ||
                thumbPosition.Y + thumbSize.Y > holderPosition.Y + holderSize.Y + 2f) return ScrollProbeStatus.NotApplicable;

            var thumbTravel = holderSize.Y - thumbSize.Y;
            var contentOverflow = contentSize.Y - holderSize.Y;
            if (thumbTravel <= 0f || contentOverflow <= 0f) return ScrollProbeStatus.NotApplicable;

            var progress = Math.Clamp((thumbPosition.Y - holderPosition.Y) / thumbTravel, 0f, 1f);
            itemsAddress = contentAddress;
            scroll = new ScrollBinding(
                holderAddress,
                thumbAddress,
                contentSize.Y,
                progress * contentOverflow,
                holderPosition.Y,
                holderPosition.Y + holderSize.Y);
            return float.IsFinite(scroll.ScanOffsetY) && scroll.ClipBottom > scroll.ClipTop
                ? ScrollProbeStatus.Succeeded
                : ScrollProbeStatus.NotApplicable;
        }

        private static bool TryGetSlotRect(SlotElementCandidate candidate, out Vector2 position, out Vector2 size)
        {
            if (!PluginUiElementReflection.TryGetAbsoluteRect(candidate.ElementAddress, out position, out size)) return false;

            // Premium tabs can keep the item pointer on a small bookkeeping child while its parent
            // owns the visible cell rectangle.
            if (candidate.ParentAddress != IntPtr.Zero &&
                PluginUiElementReflection.TryGetAbsoluteRect(candidate.ParentAddress, out var parentPosition, out var parentSize) &&
                parentSize.X >= 20f && parentSize.Y >= 20f &&
                ((parentSize.X <= 160f && parentSize.Y <= 256f) ||
                 (parentSize.X <= 256f && parentSize.Y <= 160f)))
            {
                position = parentPosition;
                size = parentSize;
            }

            position.Y -= candidate.Scroll.ScanOffsetY;

            return true;
        }

        private static bool IsAnySlotHovered(IReadOnlyList<SlotInfo> slots, ScrollFrameState scroll)
        {
            var mousePosition = ImGui.GetIO().MousePos;
            foreach (var slot in slots)
            {
                if (!ScrollProbePolicy.CanUseLivePosition(slot.Scroll.IsActive, scroll.Status)) continue;
                var position = GetLiveSlotPosition(slot, scroll);
                var centerY = position.Y + (slot.Size.Y * 0.5f);
                if (centerY < scroll.ClipTop || centerY > scroll.ClipBottom) continue;
                if (mousePosition.X >= position.X && mousePosition.X <= position.X + slot.Size.X &&
                    mousePosition.Y >= position.Y && mousePosition.Y <= position.Y + slot.Size.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private ScrollFrameState GetScrollFrameState(IReadOnlyList<SlotInfo> slots)
        {
            foreach (var slot in slots)
            {
                var binding = slot.Scroll;
                if (!binding.IsActive) continue;
                if (this.scrollRectangleProbeBudget.TryReserve(2) == ScrollProbeStatus.Unavailable)
                    return ScrollFrameState.Unavailable(binding.HolderAddress);
                var holderRead = PluginUiElementReflection.TryGetAbsoluteRect(
                    binding.HolderAddress,
                    out var holderPosition,
                    out var holderSize);
                var thumbRead = PluginUiElementReflection.TryGetAbsoluteRect(
                    binding.ThumbAddress,
                    out var thumbPosition,
                    out var thumbSize);
                if (ScrollProbePolicy.FromReadResults(holderRead, thumbRead) == ScrollProbeStatus.Unavailable)
                {
                    return ScrollFrameState.Unavailable(binding.HolderAddress);
                }

                var thumbTravel = holderSize.Y - thumbSize.Y;
                var contentOverflow = binding.ContentHeight - holderSize.Y;
                if (thumbTravel <= 0f || contentOverflow <= 0f)
                {
                    return new ScrollFrameState(binding.HolderAddress, 0f, holderPosition.Y, holderPosition.Y + holderSize.Y);
                }

                var progress = Math.Clamp((thumbPosition.Y - holderPosition.Y) / thumbTravel, 0f, 1f);
                var currentOffset = progress * contentOverflow;
                return new ScrollFrameState(
                    binding.HolderAddress,
                    currentOffset - binding.ScanOffsetY,
                    holderPosition.Y,
                    holderPosition.Y + holderSize.Y);
            }

            return ScrollFrameState.None;
        }

        private static Vector2 GetLiveSlotPosition(SlotInfo slot, ScrollFrameState scroll)
        {
            if (!slot.Scroll.IsActive || slot.Scroll.HolderAddress != scroll.HolderAddress) return slot.Position;
            return slot.Position - new Vector2(0f, scroll.OffsetDeltaY);
        }

        private void DrawSlotDiagnosticsWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(720f, 420f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(
                    this.PluginText.Title("diagnostics.slots.window_title", "LootValue Slot Diagnostics", "LootValueSlotDiagnostics"),
                    ref this.Settings.ShowSlotDebugInfo))
            {
                this.DrawSlotScanReport(this.PluginText.T("diagnostics.slots.left_panel", "Left panel (stash)"), this.leftSlotReport);
                ImGui.Separator();
                this.DrawSlotScanReport(this.PluginText.T("diagnostics.slots.right_panel", "Right panel (inventory)"), this.rightSlotReport);
            }

            ImGui.End();
        }

        private void DrawSlotScanReport(string label, SlotScanReport report)
        {
            ImGui.TextUnformatted($"{label}: 0x{report.PanelAddress.ToInt64():X}");
            ImGui.TextUnformatted(this.PluginText.F(
                "diagnostics.slots.summary",
                "UI elements={0}  non-zero +0x4F8={1}  unique pointers={2}  valid items={3}  priced={4}  visible={5}  scroll views={6}  scroll Y={7:0.0}",
                report.VisitedElements,
                report.NonZeroPointers,
                report.UniquePointers,
                report.ValidItems,
                report.PricedCandidates,
                report.VisibleSlots,
                report.ScrollContainers,
                report.ScrollOffsetY));
            ImGui.TextUnformatted(this.PluginText.F(
                "diagnostics.slots.rejected",
                "Rejected candidates={0} (showing up to {1})",
                report.RejectedCandidates,
                SlotScanReport.MaxSamples));
            foreach (var sample in report.RejectedSamples)
            {
                ImGui.TextUnformatted(sample);
            }
        }

        private void DrawItemSlots(
            IReadOnlyList<SlotInfo> slots,
            bool drawPrices,
            bool hidePrices,
            ScrollFrameState scroll)
        {
            var foreground = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize() * this.Settings.SlotFontScale;
            var color = ImGui.ColorConvertFloat4ToU32(this.Settings.TextColor);

            foreach (var slot in slots)
            {
                if (!ScrollProbePolicy.CanUseLivePosition(slot.Scroll.IsActive, scroll.Status)) continue;
                var position = GetLiveSlotPosition(slot, scroll);
                var centerY = position.Y + (slot.Size.Y * 0.5f);
                if (centerY < scroll.ClipTop || centerY > scroll.ClipBottom) continue;

                if (this.Settings.ShowSlotDebugInfo)
                {
                    foreground.AddRect(position, position + slot.Size, 0xFFFF00FFu, 0f, ImDrawFlags.None, 2f);
                    foreground.AddText(font, fontSize, position, 0xFFFFFFFFu, $"E: {slot.ItemAddress.ToInt64():X}");
                }

                if (!drawPrices || hidePrices) continue;
                var textWidth = ImGui.CalcTextSize(slot.ValueText).X * this.Settings.SlotFontScale;
                var drawPosition = new Vector2(
                    position.X + this.Settings.SlotOffsetX,
                    position.Y + slot.Size.Y - fontSize + this.Settings.SlotOffsetY);
                foreground.AddRectFilled(
                    drawPosition - new Vector2(3f, 1f),
                    drawPosition + new Vector2(textWidth + 3f, fontSize + 1f),
                    0xB0000000u,
                    3f);
                foreground.AddText(font, fontSize, drawPosition + new Vector2(1f, 1f), 0xCC000000u, slot.ValueText);
                foreground.AddText(font, fontSize, drawPosition, color, slot.ValueText);
            }
        }

        /// <summary>Alpha-beta filter on a screen position (per tracked key). It estimates screen-space
        /// VELOCITY and advances by it each frame, then nudges toward the noisy measurement by alpha — so
        /// constant-velocity motion tracks with no lag while the per-frame sampling jitter is rejected.
        /// A large jump (teleport / zone change) resets the tracker. Velocity is in px/frame (assumes a
        /// roughly steady frame rate, which is fine for jitter rejection).</summary>
        private static Vector2 Track<TKey>(Dictionary<TKey, Tracked> dict, TKey key, Vector2 measure, int rate)
            where TKey : notnull
        {
            var alpha = Math.Clamp(rate / 1000f, 0.01f, 1f);
            var beta = alpha * alpha / (2f - alpha);
            if (dict.TryGetValue(key, out var t))
            {
                var predicted = t.Pos + t.Vel;
                var residual = measure - predicted;
                if (residual.LengthSquared() <= 150f * 150f)
                {
                    var pos = predicted + (residual * alpha);
                    var vel = t.Vel + (residual * beta);
                    dict[key] = new Tracked(pos, vel);
                    return pos;
                }
            }

            dict[key] = new Tracked(measure, Vector2.Zero);
            return measure;
        }

        /// <summary>Walks every awake entity and reports the ground-item detection funnel + sample reads,
        /// so we can see which stage drops items. Throttled. Independent of the overlay gates.</summary>
        private void RunDiagnostics(LootValuePricingPass pricing)
        {
            var now = DateTime.UtcNow;
            if (now < this.nextDiagUtc) return;
            this.nextDiagUtc = now.AddMilliseconds(500);

            this.diagSamples.Clear();
            int total = 0, wiPath = 0, metaItemsPath = 0, wiComp = 0, innerOk = 0, priced = 0, belowFloor = 0;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            foreach (var entity in area.AwakeEntities.Values)
            {
                total++;
                var p = entity.Path ?? string.Empty;
                if (p.Contains("WorldItem", StringComparison.Ordinal)) wiPath++;
                if (p.StartsWith(ItemPathPrefix, StringComparison.Ordinal)) metaItemsPath++;

                if (!entity.TryGetComponent<WorldItem>(out var wi) || wi.ItemEntityAddress == IntPtr.Zero) continue;
                wiComp++;

                var item = ReadFreshItem(wi.ItemEntityAddress);
                if (item == null) continue;
                innerOk++;

                var rarity = item.TryGetComponent<Mods>(out var m) ? m.Rarity : Rarity.Normal;
                var baseName = item.TryGetComponent<Base>(out var b) ? b.BaseItemName : string.Empty;
                var art = item.TryGetComponent<RenderItem>(out var ri) ? ExtractArtBasename(ri.ResourcePath) : string.Empty;
                var ok = this.TryPriceItem(pricing, item, out var ex, out var lbl);
                if (ok)
                {
                    priced++;
                    if (ex < this.Settings.MinValueEx) belowFloor++;
                }

                if (this.diagSamples.Count < 20)
                {
                    this.diagSamples.Add(ok
                        ? $"{rarity} {baseName} [art={art}] -> {lbl} ({ex:0.##} ex)"
                        : $"{rarity} {baseName} [art={art}] -> {this.PluginText.T("diagnostics.no_price", "NO PRICE")}");
                }
            }

            var hasProviderStatus = pricing.TryGetStatus(out var status);
            this.diagSummary =
                this.PluginText.F("diagnostics.summary.ingame", "InGame={0}  PanelOpen={1}", Core.States.GameCurrentState == GameStateTypes.InGameState, Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen) + "\n" +
                this.PluginText.F("diagnostics.summary.awake_entities", "AwakeEntities={0}", total) + "\n" +
                this.PluginText.F("diagnostics.summary.paths", "path contains 'WorldItem'={0}    path starts 'Metadata/Items'={1}", wiPath, metaItemsPath) + "\n" +
                this.PluginText.F("diagnostics.summary.components", "WorldItem component (inner!=0)={0}    inner item read OK={1}", wiComp, innerOk) + "\n" +
                this.PluginText.F("diagnostics.summary.pricing", "priced={0}    belowFloor(<{1}ex)={2}    would draw={3}", priced, this.Settings.MinValueEx, belowFloor, priced - belowFloor) + "\n" +
                this.PluginText.F(
                    "diagnostics.summary.price_db",
                    "priceDB items={0}  fetching={1}",
                    hasProviderStatus ? status.ItemCount : 0,
                    hasProviderStatus && status.IsFetching);
        }

        private void DrawDiagnosticsWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(580, 440), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(this.PluginText.Title("diagnostics.window_title", "LootValue Diagnostics", "LootValueDiagnostics"), ref this.Settings.DiagnosticsMode))
            {
                ImGui.TextUnformatted(this.diagSummary);
                ImGui.Separator();
                ImGui.TextUnformatted(this.PluginText.F("diagnostics.samples", "Samples ({0}):", this.diagSamples.Count));
                foreach (var s in this.diagSamples)
                {
                    ImGui.TextUnformatted(s);
                }
            }

            ImGui.End();
        }

        /// <summary>Resolve an item's display value + label text. Uniques price by icon art (revealing
        /// unidentified ones); everything else by base-type name. Mirrors RitualHelper's resolution.</summary>
        private bool TryPriceItem(LootValuePricingPass pricing, Item item, out double valueEx, out string label, bool includeUniqueName = true)
        {
            valueEx = 0;
            label = string.Empty;

            var rarity = Rarity.Normal;
            if (item.TryGetComponent<Mods>(out var mods)) rarity = mods.Rarity;

            var baseName = item.TryGetComponent<Base>(out var baseComp) ? baseComp.BaseItemName?.Trim() ?? string.Empty : string.Empty;
            var artBasename = item.TryGetComponent<RenderItem>(out var renderItem) ? ExtractArtBasename(renderItem.ResourcePath) : string.Empty;
            var fullItemPath = item.Path ?? string.Empty;
            var internalName = fullItemPath.Contains('/') ? fullItemPath[(fullItemPath.LastIndexOf('/') + 1)..] : fullItemPath;
            var modLines = ItemModHelper.GetModLines(item);

            var itemName = baseName;
            if (rarity == Rarity.Unique && !string.IsNullOrEmpty(artBasename))
            {
                foreach (var key in ArtKeyVariants(artBasename))
                {
                    if (pricing.TryResolveDisplayName(key, out var uniqueName) &&
                        !pricing.IsGenericLookupName(uniqueName))
                    {
                        itemName = uniqueName;
                        break;
                    }

                    if (pricing.HasPriceDataForName(key))
                    {
                        itemName = key;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(itemName)) return false;

            var stack = item.TryGetComponent<Stack>(out var stackComp) && stackComp.Count > 1 ? stackComp.Count : 1;
            var query = CreateQuery(itemName, modLines, internalName, fullItemPath, BuildScoutText(itemName, modLines));
            if (!pricing.TryPrice(query, stack, this.Settings.DisplayCurrency, out var price)) return false;
            valueEx = (double)price.ExaltedValue;

            // valueText is already the stack TOTAL; only uniques get a name prefix.
            var nameForLabel = includeUniqueName && rarity == Rarity.Unique && this.Settings.RevealUnidentifiedUniques ? $"{itemName} — " : string.Empty;
            label = $"{nameForLabel}{price.Text}";
            return true;
        }

        private static PriceQuery CreateQuery(
            string itemName,
            IReadOnlyList<string> modLines,
            string internalName,
            string fullItemPath,
            string scoutText) => new(itemName, modLines, internalName, fullItemPath, scoutText);

        private static string BuildScoutText(string itemName, IReadOnlyList<string> modLines) =>
            modLines.Count == 0 ? itemName : itemName + "\n" + string.Join("\n", modLines);

        private static Item? ReadFreshItem(IntPtr itemAddress)
        {
            if (itemAddress == IntPtr.Zero) return null;
            try
            {
                return Activator.CreateInstance(
                    typeof(Item),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[] { itemAddress },
                    null) as Item;
            }
            catch
            {
                return null;
            }
        }

        // "Art/2DItems/.../Uniques/Deidbell.dds" -> "Deidbell".
        private static string ExtractArtBasename(string? artPath)
        {
            if (string.IsNullOrWhiteSpace(artPath)) return string.Empty;
            var slash = artPath.LastIndexOfAny(new[] { '/', '\\' });
            var file = slash >= 0 && slash < artPath.Length - 1 ? artPath[(slash + 1)..] : artPath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        // GGG art basenames and the price DB disagree on a leading "The" (both directions).
        private static IEnumerable<string> ArtKeyVariants(string artBasename)
        {
            if (string.IsNullOrWhiteSpace(artBasename)) yield break;
            yield return artBasename;
            if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
                yield return artBasename[3..];
            else
                yield return "The" + artBasename;
        }

        private readonly struct LootLabel
        {
            public LootLabel(uint entityId, Render render, string text, uint color, bool highlight)
            {
                this.EntityId = entityId;
                this.Render = render;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public uint EntityId { get; }

            public Render Render { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct SlotInfo
        {
            public SlotInfo(
                IntPtr itemAddress,
                Vector2 position,
                Vector2 size,
                string valueText,
                ScrollBinding scroll)
            {
                this.ItemAddress = itemAddress;
                this.Position = position;
                this.Size = size;
                this.ValueText = valueText;
                this.Scroll = scroll;
            }

            public IntPtr ItemAddress { get; }

            public Vector2 Position { get; }

            public Vector2 Size { get; }

            public string ValueText { get; }

            public ScrollBinding Scroll { get; }
        }

        private readonly struct ExchangePriceLabel
        {
            public ExchangePriceLabel(Vector2 position, string text, uint color, bool highlight)
            {
                this.Position = position;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public Vector2 Position { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct SlotElementCandidate
        {
            public SlotElementCandidate(
                IntPtr elementAddress,
                IntPtr parentAddress,
                ScrollBinding scroll,
                IntPtr itemAddress)
            {
                this.ElementAddress = elementAddress;
                this.ParentAddress = parentAddress;
                this.Scroll = scroll;
                this.ItemAddress = itemAddress;
            }

            public IntPtr ElementAddress { get; }

            public IntPtr ParentAddress { get; }

            public ScrollBinding Scroll { get; }

            public IntPtr ItemAddress { get; }
        }

        private readonly record struct SlotTraversalNode(
            IntPtr Address,
            IntPtr ParentAddress,
            ScrollBinding Scroll);

        private sealed class SlotCandidateWork
        {
            public SlotCandidateWork(SlotElementCandidate candidate, Vector2 position, Vector2 size)
            {
                this.Candidate = candidate;
                this.Position = position;
                this.Size = size;
            }

            public SlotElementCandidate Candidate { get; }

            public Vector2 Position { get; }

            public Vector2 Size { get; }

            public Item? Item { get; set; }
        }

        private sealed class SlotScanContext
        {
            public SlotScanContext(
                IntPtr panelAddress,
                Vector2 panelPosition,
                Vector2 panelSize,
                LootValuePricingPass pricing)
            {
                this.PanelPosition = panelPosition;
                this.PanelSize = panelSize;
                this.Pricing = pricing;
                this.Report = new SlotScanReport(panelAddress);
            }

            public Vector2 PanelPosition { get; }

            public Vector2 PanelSize { get; }

            public LootValuePricingPass Pricing { get; }

            public SlotScanReport Report { get; }

            public HashSet<IntPtr> Visited { get; } = new();

            public HashSet<IntPtr> UniquePointers { get; } = new();
        }

        private readonly struct ScrollBinding
        {
            public ScrollBinding(
                IntPtr holderAddress,
                IntPtr thumbAddress,
                float contentHeight,
                float scanOffsetY,
                float clipTop,
                float clipBottom)
            {
                this.HolderAddress = holderAddress;
                this.ThumbAddress = thumbAddress;
                this.ContentHeight = contentHeight;
                this.ScanOffsetY = scanOffsetY;
                this.ClipTop = clipTop;
                this.ClipBottom = clipBottom;
            }

            public bool IsActive => this.HolderAddress != IntPtr.Zero && this.ThumbAddress != IntPtr.Zero;

            public IntPtr HolderAddress { get; }

            public IntPtr ThumbAddress { get; }

            public float ContentHeight { get; }

            public float ScanOffsetY { get; }

            public float ClipTop { get; }

            public float ClipBottom { get; }
        }

        private readonly struct ScrollFrameState
        {
            public static ScrollFrameState None { get; } = new(
                IntPtr.Zero,
                0f,
                float.NegativeInfinity,
                float.PositiveInfinity,
                ScrollProbeStatus.NotApplicable);

            public ScrollFrameState(IntPtr holderAddress, float offsetDeltaY, float clipTop, float clipBottom)
                : this(holderAddress, offsetDeltaY, clipTop, clipBottom, ScrollProbeStatus.Succeeded)
            {
            }

            private ScrollFrameState(
                IntPtr holderAddress,
                float offsetDeltaY,
                float clipTop,
                float clipBottom,
                ScrollProbeStatus status)
            {
                this.HolderAddress = holderAddress;
                this.OffsetDeltaY = offsetDeltaY;
                this.ClipTop = clipTop;
                this.ClipBottom = clipBottom;
                this.Status = status;
            }

            public static ScrollFrameState Unavailable(IntPtr holderAddress) => new(
                holderAddress,
                0f,
                float.PositiveInfinity,
                float.NegativeInfinity,
                ScrollProbeStatus.Unavailable);

            public IntPtr HolderAddress { get; }

            public float OffsetDeltaY { get; }

            public float ClipTop { get; }

            public float ClipBottom { get; }

            public ScrollProbeStatus Status { get; }
        }

        private sealed class SlotScanReport
        {
            public const int MaxSamples = 8;
            private readonly HashSet<IntPtr> sampledPointers = new();

            public SlotScanReport(IntPtr panelAddress)
            {
                this.PanelAddress = panelAddress;
            }

            public IntPtr PanelAddress { get; }

            public int VisitedElements { get; set; }

            public int NonZeroPointers { get; set; }

            public int UniquePointers { get; set; }

            public int ScrollContainers { get; set; }

            public float ScrollOffsetY { get; set; }

            public int ValidItems { get; set; }

            public int PricedCandidates { get; set; }

            public int VisibleSlots { get; set; }

            public int RejectedCandidates { get; private set; }

            public List<string> RejectedSamples { get; } = new();

            public void AddRejected(IntPtr elementAddress, IntPtr itemAddress, string reason)
            {
                this.RejectedCandidates++;
                if (this.RejectedSamples.Count >= MaxSamples || !this.sampledPointers.Add(itemAddress)) return;
                this.RejectedSamples.Add(
                    $"ui=0x{elementAddress.ToInt64():X}  candidate=0x{itemAddress.ToInt64():X}  {reason}");
            }
        }

        private readonly struct TagChip
        {
            public TagChip(IntPtr elementAddress, string text, uint color, bool highlight)
            {
                this.ElementAddress = elementAddress;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public IntPtr ElementAddress { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct Tracked
        {
            public Tracked(Vector2 pos, Vector2 vel)
            {
                this.Pos = pos;
                this.Vel = vel;
            }

            public Vector2 Pos { get; }

            public Vector2 Vel { get; }
        }
    }
}
