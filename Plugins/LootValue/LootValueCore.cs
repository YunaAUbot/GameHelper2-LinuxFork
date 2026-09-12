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

namespace LootValue;

public sealed class LootValueCore : PCore<LootValueSettings>
{
	private readonly struct LootLabel(uint entityId, Render render, string text, uint color, bool highlight)
	{
		public uint EntityId { get; } = entityId;

		public Render Render { get; } = render;

		public string Text { get; } = text;

		public uint Color { get; } = color;

		public bool Highlight { get; } = highlight;
	}

	private readonly struct SlotInfo(nint itemAddress, Vector2 position, Vector2 size, string valueText, ScrollBinding scroll)
	{
		public nint ItemAddress { get; } = itemAddress;

		public Vector2 Position { get; } = position;

		public Vector2 Size { get; } = size;

		public string ValueText { get; } = valueText;

		public ScrollBinding Scroll { get; } = scroll;
	}

	private readonly struct ExchangePriceLabel(Vector2 position, string text, uint color, bool highlight)
	{
		public Vector2 Position { get; } = position;

		public string Text { get; } = text;

		public uint Color { get; } = color;

		public bool Highlight { get; } = highlight;
	}

	private readonly struct SlotElementCandidate(nint elementAddress, nint parentAddress, ScrollBinding scroll, nint itemAddress)
	{
		public nint ElementAddress { get; } = elementAddress;

		public nint ParentAddress { get; } = parentAddress;

		public ScrollBinding Scroll { get; } = scroll;

		public nint ItemAddress { get; } = itemAddress;
	}

	private readonly record struct SlotTraversalNode(nint Address, nint ParentAddress, ScrollBinding Scroll);

	private sealed class SlotCandidateWork
	{
		public SlotElementCandidate Candidate { get; }

		public Vector2 Position { get; }

		public Vector2 Size { get; }

		public Item? Item { get; set; }

		public SlotCandidateWork(SlotElementCandidate candidate, Vector2 position, Vector2 size)
		{
			Candidate = candidate;
			Position = position;
			Size = size;
		}
	}

	private sealed class SlotScanContext
	{
		public Vector2 PanelPosition { get; }

		public Vector2 PanelSize { get; }

		public LootValuePricingPass Pricing { get; set; }

		public SlotScanReport Report { get; }

		public HashSet<nint> Visited { get; } = new HashSet<nint>();

		public HashSet<nint> UniquePointers { get; } = new HashSet<nint>();

		public SlotScanContext(nint address, Vector2 position, Vector2 size, LootValuePricingPass pricing)
		{
			PanelPosition = position;
			PanelSize = size;
			Pricing = pricing;
			Report = new SlotScanReport(address);
		}
	}

	private readonly struct ScrollBinding(nint holderAddress, nint thumbAddress, float contentHeight, float scanOffsetY, float clipTop, float clipBottom)
	{
		public bool IsActive
		{
			get
			{
				if (HolderAddress != IntPtr.Zero)
				{
					return ThumbAddress != IntPtr.Zero;
				}
				return false;
			}
		}

		public nint HolderAddress { get; } = holderAddress;

		public nint ThumbAddress { get; } = thumbAddress;

		public float ContentHeight { get; } = contentHeight;

		public float ScanOffsetY { get; } = scanOffsetY;

		public float ClipTop { get; } = clipTop;

		public float ClipBottom { get; } = clipBottom;
	}

	private readonly struct ScrollFrameState
	{
		public static ScrollFrameState None { get; } = new ScrollFrameState(IntPtr.Zero, 0f, float.NegativeInfinity, float.PositiveInfinity, ScrollProbeStatus.NotApplicable);

		public nint HolderAddress { get; }

		public float OffsetDeltaY { get; }

		public float ClipTop { get; }

		public float ClipBottom { get; }

		public ScrollProbeStatus Status { get; }

		public static ScrollFrameState Unavailable(nint holderAddress)
		{
			return new ScrollFrameState(holderAddress, 0f, float.PositiveInfinity, float.NegativeInfinity, ScrollProbeStatus.Unavailable);
		}

		public ScrollFrameState(nint holderAddress, float offsetDeltaY, float clipTop, float clipBottom, ScrollProbeStatus status = ScrollProbeStatus.Succeeded)
		{
			HolderAddress = holderAddress;
			OffsetDeltaY = offsetDeltaY;
			ClipTop = clipTop;
			ClipBottom = clipBottom;
			Status = status;
		}
	}

	private sealed class SlotScanReport
	{
		public const int MaxSamples = 8;

		private readonly HashSet<nint> sampledPointers = new HashSet<nint>();

		public nint PanelAddress { get; }

		public int VisitedElements { get; set; }

		public int NonZeroPointers { get; set; }

		public int UniquePointers { get; set; }

		public int ScrollContainers { get; set; }

		public float ScrollOffsetY { get; set; }

		public int ValidItems { get; set; }

		public int PricedCandidates { get; set; }

		public int VisibleSlots { get; set; }

		public int RejectedCandidates { get; private set; }

		public List<string> RejectedSamples { get; } = new List<string>();

		public SlotScanReport(nint panelAddress)
		{
			PanelAddress = panelAddress;
		}

		public void AddRejected(nint elementAddress, nint itemAddress, string reason)
		{
			RejectedCandidates++;
			if (RejectedSamples.Count < 8 && sampledPointers.Add(itemAddress))
			{
				RejectedSamples.Add($"ui=0x{((IntPtr)elementAddress).ToInt64():X}  candidate=0x{((IntPtr)itemAddress).ToInt64():X}  {reason}");
			}
		}
	}

	private readonly struct TagChip(nint elementAddress, string text, uint color, bool highlight)
	{
		public nint ElementAddress { get; } = elementAddress;

		public string Text { get; } = text;

		public uint Color { get; } = color;

		public bool Highlight { get; } = highlight;
	}

	private readonly struct Tracked(Vector2 pos, Vector2 vel)
	{
		public Vector2 Pos { get; } = pos;

		public Vector2 Vel { get; } = vel;
	}

	private const string ItemPathPrefix = "Metadata/Items";

	private const int UiElementItemAddressOffset = 1248;

	private static readonly int[] CurrencyExchangeRootPath = new int[3] { 114, 20, 6 };

	private readonly List<LootLabel> cachedLabels = new List<LootLabel>();

	private readonly Dictionary<uint, Tracked> trackWorld = new Dictionary<uint, Tracked>();

	private DateTime nextRecomputeUtc = DateTime.MinValue;

	private readonly List<string> diagSamples = new List<string>();

	private string diagSummary = string.Empty;

	private DateTime nextDiagUtc = DateTime.MinValue;

	private const int UiElementTextOffset = 864;

	private const int SlotTraversalBudgetPerFrame = 64;

	private const int SlotRectangleBudgetPerFrame = 8;

	private const int SlotValidationBudgetPerFrame = 8;

	private const int SlotPricingBudgetPerFrame = 8;

	private static readonly int ScrollRectangleProbeBudgetPerFrame = ScrollProbePolicy.RequiredBudget(64, 3, 3, 2);

	private const int MaxSlotTraversalElements = 5000;

	private readonly List<TagChip> cachedTagChips = new List<TagChip>();

	private readonly Dictionary<nint, Tracked> trackTag = new Dictionary<nint, Tracked>();

	private DateTime nextTagScanUtc = DateTime.MinValue;

	private object? handleObj;

	private object? uiParentsObj;

	private MethodInfo? readUiOffsetMethod;

	private MethodInfo? readStdVectorMethod;

	private MethodInfo? readStdWStringStructMethod;

	private MethodInfo? readStdWStringMethod;

	private MethodInfo? readIntPtrMethod;

	private readonly HashSet<string> groundTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> groundUniqueTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> groundOrdinaryTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, string> groundUniqueLookupTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private SlotScanReport leftSlotReport = new SlotScanReport(IntPtr.Zero);

	private SlotScanReport rightSlotReport = new SlotScanReport(IntPtr.Zero);

	private SlotScanReport ritualSlotReport = new SlotScanReport(IntPtr.Zero);

	private IReadOnlyList<SlotInfo> cachedLeftSlots = Array.Empty<SlotInfo>();

	private IReadOnlyList<SlotInfo> cachedRightSlots = Array.Empty<SlotInfo>();

	private IReadOnlyList<SlotInfo> cachedRitualSlots = Array.Empty<SlotInfo>();

	private nint cachedLeftPanelAddress;

	private nint cachedRightPanelAddress;

	private nint cachedRitualGridAddress;

	private DateTime nextSlotScanUtc = DateTime.MinValue;

	private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>? leftSlotScan;

	private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>? rightSlotScan;

	private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>? ritualSlotScan;

	private readonly IncrementalPanelScanScheduler<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> slotScanScheduler = new IncrementalPanelScanScheduler<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>();

	private SlotScanContext? leftSlotScanContext;

	private SlotScanContext? rightSlotScanContext;

	private SlotScanContext? ritualSlotScanContext;

	private ScrollRectangleProbeBudget scrollRectangleProbeBudget;

	private readonly List<ExchangePriceLabel> cachedExchangeLabels = new List<ExchangePriceLabel>();

	private DateTime nextExchangeScanUtc = DateTime.MinValue;

	public override IReadOnlyCollection<string> ConflictsWith => new string[1] { "RitualHelper" };

	public override int ConflictPriority => 100;

	private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");

	public override void OnEnable(bool isGameOpened)
	{
		bool flag = true;
		if (File.Exists(SettingPathname))
		{
			try
			{
				string text = File.ReadAllText(SettingPathname);
				flag = JObject.Parse(text)["ShowStashOverlay"] == null;
				Settings = JsonConvert.DeserializeObject<LootValueSettings>(text) ?? new LootValueSettings();
			}
			catch (Exception ex)
			{
				Console.WriteLine("[LootValue] Failed to load settings: " + ex.Message);
				Settings = new LootValueSettings();
			}
		}
		if (flag && TryMigrateStashValueSettings())
		{
			SaveSettings();
		}
	}

	private bool TryMigrateStashValueSettings()
	{
		string text = Directory.GetParent(DllDirectory)?.FullName;
		if (text == null)
		{
			return false;
		}
		string[] array = new string[2] { "StashValueByZx0", "StashValue" };
		foreach (string text2 in array)
		{
			string path = Path.Join(text, text2, "config", "settings.txt");
			if (File.Exists(path))
			{
				try
				{
					JObject jObject = JObject.Parse(File.ReadAllText(path));
					Settings.ShowStashOverlay = jObject.Value<bool?>("ShowOverlay") ?? Settings.ShowStashOverlay;
					Settings.ShowInventoryOverlay = jObject.Value<bool?>("ShowInventoryOverlay") ?? Settings.ShowInventoryOverlay;
					Settings.HideSlotPricesOnHover = jObject.Value<bool?>("HidePriceOnHover") ?? Settings.HideSlotPricesOnHover;
					Settings.ShowSlotDebugInfo = jObject.Value<bool?>("ShowDebugInfo") ?? Settings.ShowSlotDebugInfo;
					Settings.SlotFontScale = jObject.Value<float?>("PriceFontScale") ?? Settings.SlotFontScale;
					Settings.SlotOffsetX = jObject.Value<float?>("PriceOffsetX") ?? Settings.SlotOffsetX;
					Settings.SlotOffsetY = jObject.Value<float?>("PriceOffsetY") ?? Settings.SlotOffsetY;
					return true;
				}
				catch (Exception ex)
				{
					Console.WriteLine("[LootValue] Failed to migrate " + text2 + " settings: " + ex.Message);
				}
			}
		}
		return false;
	}

	public override void OnDisable()
	{
		cachedLabels.Clear();
		cachedTagChips.Clear();
		trackWorld.Clear();
		trackTag.Clear();
		nextRecomputeUtc = DateTime.MinValue;
		nextTagScanUtc = DateTime.MinValue;
		handleObj = null;
		uiParentsObj = null;
		readUiOffsetMethod = null;
		readStdVectorMethod = null;
		readStdWStringStructMethod = null;
		readStdWStringMethod = null;
		readIntPtrMethod = null;
		groundTagNames.Clear();
		groundUniqueTagNames.Clear();
		groundOrdinaryTagNames.Clear();
		groundUniqueLookupTexts.Clear();
		ClearSlotScans();
		cachedExchangeLabels.Clear();
		nextExchangeScanUtc = DateTime.MinValue;
	}

	public override void SaveSettings()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingPathname) ?? string.Empty);
			File.WriteAllText(SettingPathname, JsonConvert.SerializeObject(Settings, Formatting.Indented));
		}
		catch (Exception ex)
		{
			Console.WriteLine("[LootValue] Failed to save settings: " + ex.Message);
		}
	}

	public override void DrawSettings()
	{
		ImGui.Checkbox(base.PluginText.Label("settings.show_overlay", "Show value over ground items", "LootValueShowOverlay"), ref Settings.ShowOverlay);
		ImGui.Checkbox(base.PluginText.Label("settings.anchor_to_loot_tags", "Anchor to loot labels (no overlap when items pile up)", "LootValueAnchorToLootTags"), ref Settings.AnchorToLootTags);
		ImGui.Checkbox(base.PluginText.Label("settings.show_stash_overlay", "Show value over stash items", "LootValueShowStashOverlay"), ref Settings.ShowStashOverlay);
		ImGui.Checkbox(base.PluginText.Label("settings.show_inventory_overlay", "Show value over inventory items", "LootValueShowInventoryOverlay"), ref Settings.ShowInventoryOverlay);
		ImGui.Checkbox(base.PluginText.Label("settings.show_ritual_overlay", "Show value over Ritual rewards", "LootValueShowRitualOverlay"), ref Settings.ShowRitualOverlay);
		ImGui.Checkbox(base.PluginText.Label("settings.show_currency_exchange_overlay", "Show owned-stack values in Currency Exchange", "LootValueShowCurrencyExchangeOverlay"), ref Settings.ShowCurrencyExchangeOverlay);
		ImGui.Checkbox(base.PluginText.Label("settings.hide_when_game_unfocused", "Hide values when game is not focused", "LootValueHideWhenGameUnfocused"), ref Settings.HideWhenGameInBackground);
		ImGui.Checkbox(base.PluginText.Label("settings.hide_slot_prices_on_hover", "Hide item-panel values while hovering an item", "LootValueHideSlotPricesOnHover"), ref Settings.HideSlotPricesOnHover);
		ImGui.Checkbox(base.PluginText.Label("settings.reveal_unidentified_uniques", "Reveal unidentified uniques (by art)", "LootValueRevealUnidentifiedUniques"), ref Settings.RevealUnidentifiedUniques);
		ImGui.Checkbox(base.PluginText.Label("settings.diagnostics_window", "Diagnostics window", "LootValueDiagnosticsWindow"), ref Settings.DiagnosticsMode);
		ImGui.Checkbox(base.PluginText.Label("settings.slot_diagnostics", "Item-panel slot diagnostics", "LootValueSlotDiagnostics"), ref Settings.ShowSlotDebugInfo);
		ImGui.Separator();
		ImGui.Text(base.PluginText.T("section.display", "Display"));
		if (ImGui.RadioButton(base.PluginText.Label("currency.chaos", "Chaos", "LootValueCurrencyChaos"), Settings.DisplayCurrency == 2))
		{
			Settings.DisplayCurrency = 2;
		}
		ImGui.SameLine();
		if (ImGui.RadioButton(base.PluginText.Label("currency.exalted", "Exalted", "LootValueCurrencyExalted"), Settings.DisplayCurrency == 1))
		{
			Settings.DisplayCurrency = 1;
		}
		ImGui.SameLine();
		if (ImGui.RadioButton(base.PluginText.Label("currency.divine", "Divine", "LootValueCurrencyDivine"), Settings.DisplayCurrency == 0))
		{
			Settings.DisplayCurrency = 0;
		}
		ImGui.SliderFloat(base.PluginText.Label("settings.min_value_to_show", "Min ordinary value to show (ex)", "LootValueMinValueToShow"), ref Settings.MinValueEx, 0f, 50f, "%.2f");
		ImGui.SliderFloat(base.PluginText.Label("settings.unique_min_value_to_show", "Min Unique value to show (ex)", "LootValueUniqueMinValueToShow"), ref Settings.UniqueMinValueEx, 0f, 50f, "%.2f");
		ImGui.SliderFloat(base.PluginText.Label("settings.highlight_from", "Highlight from (ex)", "LootValueHighlightFrom"), ref Settings.HighlightMinEx, 0f, 200f, "%.1f");
		ImGui.SliderFloat(base.PluginText.Label("settings.font_size", "Font size", "LootValueFontSize"), ref Settings.FontSize, 8f, 48f, "%.0f");
		ImGui.SliderFloat(base.PluginText.Label("settings.highlight_font_size", "Highlight font size", "LootValueHighlightFontSize"), ref Settings.HighlightFontSize, 8f, 64f, "%.0f");
		ImGui.Checkbox(base.PluginText.Label("settings.highlight_bold", "Highlight bold", "LootValueHighlightBold"), ref Settings.HighlightBold);
		ImGui.SliderFloat(base.PluginText.Label("settings.vertical_offset", "Vertical offset", "LootValueVerticalOffset"), ref Settings.OffsetY, -50f, 50f);
		ImGui.SliderFloat(base.PluginText.Label("settings.slot_font_scale", "Stash/inventory font scale", "LootValueSlotFontScale"), ref Settings.SlotFontScale, 0.5f, 2f, "%.2f");
		ImGui.SliderFloat(base.PluginText.Label("settings.slot_horizontal_offset", "Stash/inventory horizontal offset", "LootValueSlotOffsetX"), ref Settings.SlotOffsetX, -50f, 50f);
		ImGui.SliderFloat(base.PluginText.Label("settings.slot_vertical_offset", "Stash/inventory vertical offset", "LootValueSlotOffsetY"), ref Settings.SlotOffsetY, -50f, 50f);
		ImGui.Checkbox(base.PluginText.Label("settings.smooth_label_motion", "Smooth label motion (velocity tracking)", "LootValueSmoothLabelMotion"), ref Settings.InterpolatePosition);
		if (Settings.InterpolatePosition)
		{
			ImGui.SliderInt(base.PluginText.Label("settings.jitter_filter", "Jitter filter (lower=stronger, no lag)", "LootValueJitterFilter"), ref Settings.InterpolationRate, 1, 1000);
		}
		ImGui.SliderInt(base.PluginText.Label("settings.rescan_interval", "Rescan interval (ms)", "LootValueRescanInterval"), ref Settings.RescanIntervalMs, 16, 1000);
		ImGui.TextDisabled(base.PluginText.T("settings.rescan_interval.tooltip", "Positions redraw every frame; rescan only re-detects items/prices."));
		ImGui.SliderInt(base.PluginText.Label("settings.slot_rescan_interval", "Stash/inventory rescan interval (ms)", "LootValueSlotRescanInterval"), ref Settings.SlotRescanIntervalMs, 100, 2000);
		ImGui.TextDisabled(base.PluginText.T("settings.slot_rescan_interval.tooltip", "Cached slot values draw every frame; panel traversal and pricing run at this interval."));
		ImGui.ColorEdit4(base.PluginText.Label("settings.text_color", "Text color", "LootValueTextColor"), ref Settings.TextColor);
		ImGui.ColorEdit4(base.PluginText.Label("settings.highlight_color", "Highlight color", "LootValueHighlightColor"), ref Settings.HighlightColor);
		if (ImGui.Button(base.PluginText.Label("button.refresh_prices_now", "Refresh prices now", "LootValueRefreshPricesNow")))
		{
			LootValuePricingPass.Capture(() => PriceProviderRegistry.Current).RequestRefreshSafely();
		}
	}

	public override void DrawUI()
	{
		scrollRectangleProbeBudget = new ScrollRectangleProbeBudget(ScrollRectangleProbeBudgetPerFrame);
		if (Core.States.GameCurrentState != GameStateTypes.InGameState)
		{
			ClearSlotScans();
			return;
		}
		LootValuePricingPass pricing = LootValuePricingPass.Capture(() => PriceProviderRegistry.Current);
		if (Settings.DiagnosticsMode)
		{
			RunDiagnostics(pricing);
			DrawDiagnosticsWindow();
		}
		if (Settings.HideWhenGameInBackground && !Core.Process.Foreground)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (Settings.ShowOverlay && Settings.AnchorToLootTags)
		{
			if (EnsureReflection())
			{
				if (utcNow >= nextTagScanUtc)
				{
					nextTagScanUtc = utcNow.AddMilliseconds(Math.Max(16, Settings.RescanIntervalMs));
					ScanLootTags(pricing);
				}
				DrawTagChips();
			}
		}
		else if (Settings.ShowOverlay)
		{
			if (utcNow >= nextRecomputeUtc)
			{
				nextRecomputeUtc = utcNow.AddMilliseconds(Math.Max(16, Settings.RescanIntervalMs));
				RecomputeLabels(pricing);
			}
			DrawLabels();
		}
		if (Settings.ShowStashOverlay || Settings.ShowInventoryOverlay || Settings.ShowRitualOverlay || Settings.ShowSlotDebugInfo)
		{
			DrawItemSlotValues(pricing);
		}
		else
		{
			ClearSlotScans();
		}
		if (Settings.ShowCurrencyExchangeOverlay)
		{
			DrawCurrencyExchangeValues(pricing);
		}
	}

	private void RecomputeLabels(LootValuePricingPass pricing)
	{
		cachedLabels.Clear();
		foreach (Entity value in Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values)
		{
			if (!value.TryGetComponent<WorldItem>(out WorldItem component) || component.ItemEntityAddress == IntPtr.Zero || !value.TryGetComponent<Render>(out Render component2))
			{
				continue;
			}
			Item item = ReadFreshItem(component.ItemEntityAddress);
			if (item != null && TryPriceItem(pricing, item, out double valueEx, out string label))
			{
				bool isUnique = item.TryGetComponent<Mods>(out Mods component3) && component3.Rarity == Rarity.Unique;
				if (!(valueEx < (double)LootValuePricingPolicy.MinimumValueEx(isUnique, Settings.MinValueEx, Settings.UniqueMinValueEx)))
				{
					bool flag = valueEx >= (double)Settings.HighlightMinEx;
					uint color = ImGui.ColorConvertFloat4ToU32(flag ? Settings.HighlightColor : Settings.TextColor);
					cachedLabels.Add(new LootLabel(value.Id, component2, label, color, flag));
				}
			}
		}
		if (trackWorld.Count <= 0)
		{
			return;
		}
		HashSet<uint> live = new HashSet<uint>(cachedLabels.Count);
		foreach (LootLabel cachedLabel in cachedLabels)
		{
			live.Add(cachedLabel.EntityId);
		}
		trackWorld.Keys.Where((uint k) => !live.Contains(k)).ToList().ForEach(delegate(uint k)
		{
			trackWorld.Remove(k);
		});
	}

	private void DrawLabels()
	{
		if (cachedLabels.Count == 0)
		{
			return;
		}
		ImDrawListPtr backgroundDrawList = ImGui.GetBackgroundDrawList();
		ImFontPtr font = ImGui.GetFont();
		float fontSize = ImGui.GetFontSize();
		WorldData currentWorldInstance = Core.States.InGameStateObject.CurrentWorldInstance;
		foreach (LootLabel cachedLabel in cachedLabels)
		{
			Vector2 vector = currentWorldInstance.WorldToScreen(cachedLabel.Render.WorldPosition, cachedLabel.Render.TerrainHeight);
			if (!(vector == Vector2.Zero))
			{
				if (Settings.InterpolatePosition)
				{
					vector = Track(trackWorld, cachedLabel.EntityId, vector, Settings.InterpolationRate);
				}
				float num = (cachedLabel.Highlight ? Settings.HighlightFontSize : Settings.FontSize);
				float num2 = ImGui.CalcTextSize(cachedLabel.Text).X * (num / fontSize);
				Vector2 pos = new Vector2(vector.X - num2 / 2f, vector.Y + Settings.OffsetY);
				DrawValueLabel(backgroundDrawList, font, fontSize, pos, cachedLabel.Text, cachedLabel.Color, cachedLabel.Highlight);
			}
		}
	}

	private void DrawValueLabel(ImDrawListPtr fg, ImFontPtr font, float baseSize, Vector2 pos, string text, uint color, bool highlight)
	{
		float num = (highlight ? Settings.HighlightFontSize : Settings.FontSize);
		bool num2 = highlight && Settings.HighlightBold;
		fg.AddRectFilled(p_max: pos + new Vector2(ImGui.CalcTextSize(text).X * (num / baseSize) + 3f, num + 1f), p_min: pos - new Vector2(3f, 1f), col: 2952790016u, rounding: 3f);
		fg.AddText(font, num, pos + new Vector2(1f, 1f), 3422552064u, text);
		fg.AddText(font, num, pos, color, text);
		if (num2)
		{
			fg.AddText(font, num, pos + new Vector2(1f, 0f), color, text);
		}
	}

	private bool EnsureReflection()
	{
		if (handleObj != null)
		{
			return true;
		}
		handleObj = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Core.Process);
		if (handleObj == null)
		{
			return false;
		}
		MethodInfo[] methods = handleObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo = methods.First((MethodInfo m) => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
		MethodInfo methodInfo2 = methods.First((MethodInfo m) => m.Name == "ReadStdVector" && m.IsGenericMethod);
		readUiOffsetMethod = methodInfo.MakeGenericMethod(typeof(UiElementBaseOffset));
		readStdVectorMethod = methodInfo2.MakeGenericMethod(typeof(nint));
		readStdWStringStructMethod = methodInfo.MakeGenericMethod(typeof(StdWString));
		readStdWStringMethod = methods.First((MethodInfo m) => m.Name == "ReadStdWString" && m.GetParameters().Length == 1);
		readIntPtrMethod = methodInfo.MakeGenericMethod(typeof(nint));
		return true;
	}

	private string ReadUiElementText(nint element)
	{
		try
		{
			object obj = readStdWStringStructMethod.Invoke(handleObj, new object[1] { element + 864 });
			if (obj == null)
			{
				return string.Empty;
			}
			return (readStdWStringMethod.Invoke(handleObj, new object[1] { obj }) as string) ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	private void ScanLootTags(LootValuePricingPass pricing)
	{
		cachedTagChips.Clear();
		RefreshGroundTagNames(pricing);
		ImportantUiElements gameUi = Core.States.InGameStateObject.GameUi;
		nint address = gameUi.Address;
		nint address2 = gameUi.LeftPanel.Address;
		nint address3 = gameUi.RightPanel.Address;
		if (address == IntPtr.Zero || readUiOffsetMethod == null || readStdVectorMethod == null)
		{
			return;
		}
		Queue<nint> queue = new Queue<nint>();
		HashSet<nint> hashSet = new HashSet<nint>();
		queue.Enqueue(address);
		while (queue.Count > 0 && hashSet.Count < 20000)
		{
			nint num = queue.Dequeue();
			if (num == IntPtr.Zero || !hashSet.Add(num) || (num != address && (num == address2 || num == address3)) || !(readUiOffsetMethod.Invoke(handleObj, new object[1] { num }) is UiElementBaseOffset uiElementBaseOffset) || (num != address && !UiElementBaseFuncs.IsVisibleChecker(uiElementBaseOffset.Flags)))
			{
				continue;
			}
			if (readStdVectorMethod.Invoke(handleObj, new object[1] { uiElementBaseOffset.ChildrensPtr }) is nint[] array)
			{
				nint[] array2 = array;
				foreach (nint item in array2)
				{
					queue.Enqueue(item);
				}
			}
			string text = ReadUiElementText(num);
			if (text.Length >= 3)
			{
				string text2 = text.Split('\n')[0].Trim();
				if (text2.Length >= 3 && TryPriceTagText(pricing, text2, out string chipText, out uint color, out bool highlight))
				{
					cachedTagChips.Add(new TagChip(num, chipText, color, highlight));
				}
			}
		}
		if (trackTag.Count <= 0)
		{
			return;
		}
		HashSet<nint> live = new HashSet<nint>(cachedTagChips.Count);
		foreach (TagChip cachedTagChip in cachedTagChips)
		{
			live.Add(cachedTagChip.ElementAddress);
		}
		trackTag.Keys.Where((nint k) => !live.Contains(k)).ToList().ForEach(delegate(nint k)
		{
			trackTag.Remove(k);
		});
	}

	private bool TryPriceTagText(LootValuePricingPass pricing, string text, out string chipText, out uint color, out bool highlight)
	{
		chipText = string.Empty;
		color = 0u;
		highlight = false;
		int result = 1;
		string text2 = text;
		Match match = Regex.Match(text, "^(\\d+)\\s*x\\s+(.+)$", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			int.TryParse(match.Groups[1].Value, out result);
			text2 = match.Groups[2].Value;
		}
		text2 = text2.Trim();
		if (text2.Length < 3)
		{
			return false;
		}
		if (!groundTagNames.Contains(text2))
		{
			return false;
		}
		string text3 = (groundUniqueLookupTexts.TryGetValue(text2, out string value) ? value : text);
		if (string.IsNullOrWhiteSpace(text3))
		{
			return false;
		}
		PriceQuery query = CreateQuery(text2, Array.Empty<string>(), string.Empty, string.Empty, text3);
		if (!pricing.TryPrice(query, Math.Max(1, result), Settings.DisplayCurrency, out var result2))
		{
			return false;
		}
		double num = (double)result2.ExaltedValue;
		bool isUnique = groundUniqueTagNames.Contains(text2);
		if (num < (double)LootValuePricingPolicy.MinimumValueEx(isUnique, Settings.MinValueEx, Settings.UniqueMinValueEx))
		{
			return false;
		}
		chipText = result2.Text;
		highlight = num >= (double)Settings.HighlightMinEx;
		color = ImGui.ColorConvertFloat4ToU32(highlight ? Settings.HighlightColor : Settings.TextColor);
		return true;
	}

	private void DrawTagChips()
	{
		if (cachedTagChips.Count == 0)
		{
			return;
		}
		if (uiParentsObj == null)
		{
			uiParentsObj = PluginUiElementReflection.CreateParents();
		}
		if (uiParentsObj == null)
		{
			return;
		}
		ImDrawListPtr backgroundDrawList = ImGui.GetBackgroundDrawList();
		ImFontPtr font = ImGui.GetFont();
		float fontSize = ImGui.GetFontSize();
		foreach (TagChip cachedTagChip in cachedTagChips)
		{
			if (!(readUiOffsetMethod.Invoke(handleObj, new object[1] { cachedTagChip.ElementAddress }) is UiElementBaseOffset uiElementBaseOffset) || (uiElementBaseOffset.Self != IntPtr.Zero && uiElementBaseOffset.Self != cachedTagChip.ElementAddress) || !UiElementBaseFuncs.IsVisibleChecker(uiElementBaseOffset.Flags))
			{
				continue;
			}
			try
			{
				object obj = PluginUiElementReflection.CreateUiElement(cachedTagChip.ElementAddress, uiParentsObj);
				if (obj == null)
				{
					continue;
				}
				Vector2 vector = (Vector2)PluginUiElementReflection.UiElementPositionProperty.GetValue(obj);
				Vector2 vector2 = (Vector2)PluginUiElementReflection.UiElementSizeProperty.GetValue(obj);
				if (!(vector2.X <= 0f) && !(vector == Vector2.Zero))
				{
					float num = (cachedTagChip.Highlight ? Settings.HighlightFontSize : Settings.FontSize);
					Vector2 vector3 = new Vector2(vector.X + vector2.X + 6f, vector.Y + (vector2.Y - num) / 2f);
					if (Settings.InterpolatePosition)
					{
						vector3 = Track(trackTag, cachedTagChip.ElementAddress, vector3, Settings.InterpolationRate);
					}
					DrawValueLabel(backgroundDrawList, font, fontSize, vector3, cachedTagChip.Text, cachedTagChip.Color, cachedTagChip.Highlight);
				}
			}
			catch
			{
			}
		}
	}

	private void RefreshGroundTagNames(LootValuePricingPass pricing)
	{
		groundTagNames.Clear();
		groundUniqueTagNames.Clear();
		groundOrdinaryTagNames.Clear();
		groundUniqueLookupTexts.Clear();
		foreach (Entity value5 in Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values)
		{
			if (!value5.TryGetComponent<WorldItem>(out WorldItem component) || component.ItemEntityAddress == IntPtr.Zero)
			{
				continue;
			}
			Item item = ReadFreshItem(component.ItemEntityAddress);
			if (item == null)
			{
				continue;
			}
			string text = ((!item.TryGetComponent<Base>(out Base component2)) ? string.Empty : (component2.BaseItemName?.Trim() ?? string.Empty));
			bool flag = item.TryGetComponent<Mods>(out Mods component3) && component3.Rarity == Rarity.Unique;
			if (!string.IsNullOrWhiteSpace(text))
			{
				groundTagNames.Add(text);
				if (!flag)
				{
					groundOrdinaryTagNames.Add(text);
					if (groundUniqueLookupTexts.ContainsKey(text))
					{
						groundUniqueLookupTexts[text] = string.Empty;
					}
					groundUniqueTagNames.Remove(text);
				}
				else if (!groundOrdinaryTagNames.Contains(text))
				{
					groundUniqueTagNames.Add(text);
				}
			}
			if (!flag || !item.TryGetComponent<RenderItem>(out RenderItem component4))
			{
				continue;
			}
			foreach (string item2 in ArtKeyVariants(ExtractArtBasename(component4.ResourcePath)))
			{
				if (!pricing.TryResolveDisplayName(item2, out string displayName) || pricing.IsGenericLookupName(displayName))
				{
					continue;
				}
				string text2 = displayName.Trim();
				groundTagNames.Add(text2);
				groundUniqueLookupTexts.TryGetValue(text2, out string value);
				string value2 = LootValuePricingPolicy.MergeGroundUniqueLookupText(value, text2, text);
				groundUniqueLookupTexts[text2] = value2;
				if (string.IsNullOrWhiteSpace(value2))
				{
					groundUniqueTagNames.Remove(text2);
				}
				else
				{
					groundUniqueTagNames.Add(text2);
				}
				if (!string.IsNullOrWhiteSpace(text))
				{
					groundUniqueLookupTexts.TryGetValue(text, out string value3);
					string value4 = (groundOrdinaryTagNames.Contains(text) ? string.Empty : LootValuePricingPolicy.MergeGroundUniqueLookupText(value3, text2, text));
					groundUniqueLookupTexts[text] = value4;
					if (string.IsNullOrWhiteSpace(value4))
					{
						groundUniqueTagNames.Remove(text);
					}
					else
					{
						groundUniqueTagNames.Add(text);
					}
				}
			}
		}
	}

	private void DrawCurrencyExchangeValues(LootValuePricingPass pricing)
	{
		if (!EnsureReflection())
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow >= nextExchangeScanUtc)
		{
			nextExchangeScanUtc = utcNow.AddMilliseconds(Math.Clamp(Settings.SlotRescanIntervalMs, 100, 2000));
			ScanCurrencyExchange(pricing);
		}
		if (cachedExchangeLabels.Count == 0)
		{
			return;
		}
		ImDrawListPtr backgroundDrawList = ImGui.GetBackgroundDrawList();
		ImFontPtr font = ImGui.GetFont();
		float fontSize = ImGui.GetFontSize();
		foreach (ExchangePriceLabel cachedExchangeLabel in cachedExchangeLabels)
		{
			DrawValueLabel(backgroundDrawList, font, fontSize, cachedExchangeLabel.Position, cachedExchangeLabel.Text, cachedExchangeLabel.Color, cachedExchangeLabel.Highlight);
		}
	}

	private void ScanCurrencyExchange(LootValuePricingPass pricing)
	{
		cachedExchangeLabels.Clear();
		nint num = ResolveUiPath(Core.States.InGameStateObject.GameUi.Address, CurrencyExchangeRootPath);
		if (num == IntPtr.Zero || !TryGetVisibleChildren(num, out nint[] children) || children.Length <= 1)
		{
			return;
		}
		nint address = children[1];
		if (!TryGetVisibleChildren(address, out nint[] children2) || !PluginUiElementReflection.TryGetAbsoluteRect(num, out var position, out var size))
		{
			return;
		}
		Vector2 vector = position + size;
		nint[] array = children2;
		foreach (nint address2 in array)
		{
			if (!TryGetVisibleChildren(address2, out nint[] children3))
			{
				continue;
			}
			nint[] array2 = children3;
			foreach (nint address3 in array2)
			{
				if (!TryGetVisibleChildren(address3, out nint[] children4))
				{
					continue;
				}
				for (int k = 1; k < children4.Length; k++)
				{
					nint address4 = children4[k];
					if (!TryGetVisibleChildren(address4, out nint[] children5) || children5.Length <= 1)
					{
						continue;
					}
					nint element = children5[0];
					nint address5 = children5[1];
					if (!TryGetVisibleChildren(address5, out nint[] children6) || children6.Length == 0)
					{
						continue;
					}
					string text = ReadUiElementText(element).Split('\n')[0].Trim();
					string text2 = ReadUiElementText(children6[0]);
					if (text.Length >= 2 && TryParseOwnedAmount(text2, out var amount) && amount > 0 && TryPriceNamedStack(pricing, text, amount, out string text3, out uint color, out bool highlight) && PluginUiElementReflection.TryGetAbsoluteRect(address5, out var position2, out var size2))
					{
						Vector2 vector2 = position2 + size2 * 0.5f;
						if (!(vector2.X < position.X) && !(vector2.X > vector.X) && !(vector2.Y < position.Y) && !(vector2.Y > vector.Y))
						{
							float num2 = (highlight ? Settings.HighlightFontSize : Settings.FontSize);
							Vector2 position3 = new Vector2(position2.X + Settings.SlotOffsetX, position2.Y + size2.Y - num2 + Settings.SlotOffsetY);
							cachedExchangeLabels.Add(new ExchangePriceLabel(position3, text3, color, highlight));
						}
					}
				}
			}
		}
	}

	private bool TryGetVisibleChildren(nint address, out nint[] children)
	{
		return TryGetChildren(address, requireVisible: true, out children);
	}

	private bool TryGetChildren(nint address, bool requireVisible, out nint[] children)
	{
		children = Array.Empty<nint>();
		if (address == IntPtr.Zero || readUiOffsetMethod == null || readStdVectorMethod == null || !(readUiOffsetMethod.Invoke(handleObj, new object[1] { address }) is UiElementBaseOffset uiElementBaseOffset) || (requireVisible && !UiElementBaseFuncs.IsVisibleChecker(uiElementBaseOffset.Flags)))
		{
			return false;
		}
		children = (readStdVectorMethod.Invoke(handleObj, new object[1] { uiElementBaseOffset.ChildrensPtr }) as nint[]) ?? Array.Empty<nint>();
		return true;
	}

	private nint ResolveUiPath(nint root, IReadOnlyList<int> path)
	{
		nint num = root;
		foreach (int item in path)
		{
			if (!TryGetChildren(num, requireVisible: false, out nint[] children) || item < 0 || item >= children.Length)
			{
				return IntPtr.Zero;
			}
			num = children[item];
		}
		return num;
	}

	private static bool TryParseOwnedAmount(string text, out long amount)
	{
		amount = 0L;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = Regex.Replace(text, "[^0-9]", string.Empty);
		if (text2.Length > 0)
		{
			return long.TryParse(text2, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
		}
		return false;
	}

	private bool TryPriceNamedStack(LootValuePricingPass pricing, string itemName, long amount, out string text, out uint color, out bool highlight)
	{
		text = string.Empty;
		color = 0u;
		highlight = false;
		PriceQuery query = CreateQuery(itemName, Array.Empty<string>(), string.Empty, string.Empty, itemName);
		if (!pricing.TryPrice(query, amount, Settings.DisplayCurrency, out var result))
		{
			return false;
		}
		double num = (double)result.ExaltedValue;
		if (num < (double)Settings.MinValueEx)
		{
			return false;
		}
		text = result.Text;
		highlight = num >= (double)Settings.HighlightMinEx;
		color = ImGui.ColorConvertFloat4ToU32(highlight ? Settings.HighlightColor : Settings.TextColor);
		return true;
	}

	private void DrawItemSlotValues(LootValuePricingPass pricing)
	{
		ImportantUiElements gameUi = Core.States.InGameStateObject.GameUi;
		if (gameUi.Address != IntPtr.Zero && EnsureReflection())
		{
			bool num = Settings.ShowStashOverlay || Settings.ShowSlotDebugInfo;
			bool flag = Settings.ShowInventoryOverlay || Settings.ShowSlotDebugInfo;
			bool flag2 = Settings.ShowRitualOverlay || Settings.ShowSlotDebugInfo;
			nint num2 = ((num && gameUi.LeftPanel.IsVisible) ? gameUi.LeftPanel.Address : IntPtr.Zero);
			nint num3 = ((flag && gameUi.RightPanel.IsVisible) ? gameUi.RightPanel.Address : IntPtr.Zero);
			nint num4 = (flag2 ? ResolveVisibleRitualRewardGrid(gameUi.Address) : IntPtr.Zero);
			if (num2 != cachedLeftPanelAddress || num3 != cachedRightPanelAddress || num4 != cachedRitualGridAddress)
			{
				cachedLeftPanelAddress = num2;
				cachedRightPanelAddress = num3;
				cachedRitualGridAddress = num4;
				nextSlotScanUtc = DateTime.MinValue;
				RestartSlotScans(gameUi, num2, num3, num4, pricing);
			}
			DateTime utcNow = DateTime.UtcNow;
			EnsureSlotScanners();
			IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>[] array = new IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>[3] { leftSlotScan, rightSlotScan, ritualSlotScan };
			if (leftSlotScanContext != null)
			{
				leftSlotScanContext.Pricing = pricing;
			}
			if (rightSlotScanContext != null)
			{
				rightSlotScanContext.Pricing = pricing;
			}
			if (ritualSlotScanContext != null)
			{
				ritualSlotScanContext.Pricing = pricing;
			}
			if (array.All((IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> scan) => scan.IsComplete) && utcNow >= nextSlotScanUtc)
			{
				RestartSlotScans(gameUi, num2, num3, num4, pricing);
			}
			PanelScanBudget budget = new PanelScanBudget(64, 8, 8, 8);
			slotScanScheduler.Advance(array, ref budget);
			cachedLeftSlots = array[0].Snapshot;
			cachedRightSlots = array[1].Snapshot;
			cachedRitualSlots = array[2].Snapshot;
			if (nextSlotScanUtc == DateTime.MinValue && array.All((IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> scan) => scan.IsComplete))
			{
				leftSlotReport = leftSlotScanContext?.Report ?? new SlotScanReport(IntPtr.Zero);
				rightSlotReport = rightSlotScanContext?.Report ?? new SlotScanReport(IntPtr.Zero);
				ritualSlotReport = ritualSlotScanContext?.Report ?? new SlotScanReport(IntPtr.Zero);
				nextSlotScanUtc = utcNow.AddMilliseconds(Math.Clamp(Settings.SlotRescanIntervalMs, 100, 2000));
			}
			ScrollFrameState scrollFrameState = GetScrollFrameState(cachedLeftSlots);
			ScrollFrameState scrollFrameState2 = GetScrollFrameState(cachedRightSlots);
			ScrollFrameState scrollFrameState3 = GetScrollFrameState(cachedRitualSlots);
			bool hidePrices = Settings.HideSlotPricesOnHover && (IsAnySlotHovered(cachedLeftSlots, scrollFrameState) || IsAnySlotHovered(cachedRightSlots, scrollFrameState2) || IsAnySlotHovered(cachedRitualSlots, scrollFrameState3));
			DrawItemSlots(cachedLeftSlots, Settings.ShowStashOverlay, hidePrices, scrollFrameState);
			DrawItemSlots(cachedRightSlots, Settings.ShowInventoryOverlay, hidePrices, scrollFrameState2);
			DrawItemSlots(cachedRitualSlots, Settings.ShowRitualOverlay, hidePrices, scrollFrameState3);
			if (Settings.ShowSlotDebugInfo)
			{
				DrawSlotDiagnosticsWindow();
			}
		}
	}

	private void EnsureSlotScanners()
	{
		if (leftSlotScan == null)
		{
			leftSlotScan = CreateSlotScanner(() => leftSlotScanContext);
		}
		if (rightSlotScan == null)
		{
			rightSlotScan = CreateSlotScanner(() => rightSlotScanContext);
		}
		if (ritualSlotScan == null)
		{
			ritualSlotScan = CreateSlotScanner(() => ritualSlotScanContext);
		}
	}

	private void ClearSlotScans()
	{
		EnsureSlotScanners();
		leftSlotScan.Clear();
		rightSlotScan.Clear();
		ritualSlotScan.Clear();
		cachedLeftSlots = Array.Empty<SlotInfo>();
		cachedRightSlots = Array.Empty<SlotInfo>();
		cachedRitualSlots = Array.Empty<SlotInfo>();
		cachedLeftPanelAddress = (cachedRightPanelAddress = (cachedRitualGridAddress = IntPtr.Zero));
		leftSlotScanContext = (rightSlotScanContext = (ritualSlotScanContext = null));
		leftSlotReport = (rightSlotReport = (ritualSlotReport = new SlotScanReport(IntPtr.Zero)));
		nextSlotScanUtc = DateTime.MinValue;
	}

	private void RestartSlotScans(ImportantUiElements gameUi, nint left, nint right, nint ritual, LootValuePricingPass pricing)
	{
		EnsureSlotScanners();
		RestartSlotScan(leftSlotScan, ref leftSlotScanContext, left, gameUi.LeftPanel.Position, gameUi.LeftPanel.Size, pricing);
		RestartSlotScan(rightSlotScan, ref rightSlotScanContext, right, gameUi.RightPanel.Position, gameUi.RightPanel.Size, pricing);
		if (ritual != IntPtr.Zero && PluginUiElementReflection.TryGetAbsoluteRect(ritual, out var position, out var size))
		{
			RestartSlotScan(ritualSlotScan, ref ritualSlotScanContext, ritual, position, size, pricing);
		}
		else
		{
			RestartSlotScan(ritualSlotScan, ref ritualSlotScanContext, IntPtr.Zero, default(Vector2), default(Vector2), pricing);
		}
		nextSlotScanUtc = DateTime.MinValue;
	}

	private void RestartSlotScan(IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> scan, ref SlotScanContext? context, nint address, Vector2 position, Vector2 size, LootValuePricingPass pricing)
	{
		if (address == IntPtr.Zero)
		{
			context = null;
			scan.Clear();
		}
		else
		{
			context = new SlotScanContext(address, position, size, pricing);
			scan.Restart(address, new SlotTraversalNode(address, IntPtr.Zero, default(ScrollBinding)));
		}
	}

	private IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo> CreateSlotScanner(Func<SlotScanContext?> context)
	{
		return new IncrementalPanelScan<SlotTraversalNode, SlotElementCandidate, SlotCandidateWork, SlotInfo>((SlotTraversalNode node) => TraverseSlotNode(context(), node), (SlotElementCandidate candidate) => InspectSlotCandidate(context(), candidate), (SlotCandidateWork work) => ValidateSlotCandidate(context(), work), (SlotCandidateWork work) => PriceSlotCandidate(context(), work), (SlotElementCandidate candidate) => candidate.ItemAddress, 5000);
	}

	private nint ResolveVisibleRitualRewardGrid(nint gameUiAddress)
	{
		foreach (int[] candidatePath in RitualRewardGridPathPolicy.CandidatePaths)
		{
			nint num = ResolveUiPath(gameUiAddress, candidatePath);
			bool flag = !TryGetVisibleChildren(num, out nint[] children);
			if (!flag)
			{
				int num2 = children.Length;
				bool flag2 = ((num2 < 1 || num2 > 32) ? true : false);
				flag = flag2;
			}
			if (flag)
			{
				continue;
			}
			if (RitualRewardGridPathPolicy.IsPreviouslyValidatedCandidate(num, cachedRitualGridAddress))
			{
				return num;
			}
			nint[] array = children;
			foreach (nint num3 in array)
			{
				if (readIntPtrMethod?.Invoke(handleObj, new object[1] { num3 + 1248 }) is nint num4 && num4 != IntPtr.Zero && PluginUiElementReflection.TryValidateItemAddress(num4, out string _, out string _))
				{
					return num;
				}
			}
		}
		return IntPtr.Zero;
	}

	private PanelTraversalStep<SlotTraversalNode, SlotElementCandidate> TraverseSlotNode(SlotScanContext? context, SlotTraversalNode node)
	{
		if (context == null || node.Address == IntPtr.Zero || !context.Visited.Add(node.Address) || readUiOffsetMethod == null || readStdVectorMethod == null || readIntPtrMethod == null)
		{
			return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(Array.Empty<SlotTraversalNode>());
		}
		context.Report.VisitedElements++;
		if (!(readUiOffsetMethod.Invoke(handleObj, new object[1] { node.Address }) is UiElementBaseOffset uiElementBaseOffset) || !UiElementBaseFuncs.IsVisibleChecker(uiElementBaseOffset.Flags))
		{
			return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(Array.Empty<SlotTraversalNode>());
		}
		nint[] array = (readStdVectorMethod.Invoke(handleObj, new object[1] { uiElementBaseOffset.ChildrensPtr }) as nint[]) ?? Array.Empty<nint>();
		bool isRitualGrid = context.Report.PanelAddress == cachedRitualGridAddress && cachedRitualGridAddress != IntPtr.Zero;
		nint scrollItemsAddress = IntPtr.Zero;
		ScrollBinding localScroll = default(ScrollBinding);
		ScrollProbeStatus scrollProbeStatus = (RitualRewardGridPathPolicy.ShouldProbeScroll(isRitualGrid) ? TryGetScrollContainer(array, out scrollItemsAddress, out localScroll) : ScrollProbeStatus.NotApplicable);
		if (scrollProbeStatus == ScrollProbeStatus.Unavailable)
		{
			context.Visited.Remove(node.Address);
			context.Report.VisitedElements--;
			return PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>.Deferred();
		}
		bool hasScroll = scrollProbeStatus == ScrollProbeStatus.Succeeded;
		if (hasScroll)
		{
			context.Report.ScrollContainers++;
			context.Report.ScrollOffsetY = localScroll.ScanOffsetY;
		}
		SlotTraversalNode[] children = array.Select((nint child) => new SlotTraversalNode(child, node.Address, (hasScroll && child == scrollItemsAddress) ? localScroll : node.Scroll)).ToArray();
		nint num = ((readIntPtrMethod.Invoke(handleObj, new object[1] { node.Address + 1248 }) is nint num2) ? num2 : IntPtr.Zero);
		if (num == IntPtr.Zero)
		{
			return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(children);
		}
		context.Report.NonZeroPointers++;
		if (context.UniquePointers.Add(num))
		{
			context.Report.UniquePointers++;
		}
		return new PanelTraversalStep<SlotTraversalNode, SlotElementCandidate>(children, new SlotElementCandidate(node.Address, node.ParentAddress, node.Scroll, num));
	}

	private PanelCandidateResult<SlotCandidateWork> InspectSlotCandidate(SlotScanContext? context, SlotElementCandidate candidate)
	{
		if (context == null || !TryGetSlotRect(candidate, out var position, out var size))
		{
			return PanelCandidateResult<SlotCandidateWork>.Rejected();
		}
		Vector2 vector = position + size * 0.5f;
		Vector2 vector2 = context.PanelPosition + context.PanelSize;
		if (vector.X < context.PanelPosition.X || vector.X > vector2.X || (!candidate.Scroll.IsActive && (vector.Y < context.PanelPosition.Y || vector.Y > vector2.Y)))
		{
			return PanelCandidateResult<SlotCandidateWork>.Rejected();
		}
		return PanelCandidateResult<SlotCandidateWork>.Accepted(new SlotCandidateWork(candidate, position, size));
	}

	private bool ValidateSlotCandidate(SlotScanContext? context, SlotCandidateWork work)
	{
		if (context == null)
		{
			return false;
		}
		if (!PluginUiElementReflection.TryValidateItemAddress(work.Candidate.ItemAddress, out string _, out string failureReason))
		{
			context.Report.AddRejected(work.Candidate.ElementAddress, work.Candidate.ItemAddress, failureReason);
			return false;
		}
		work.Item = ReadFreshItem(work.Candidate.ItemAddress);
		if (work.Item == null || string.IsNullOrEmpty(work.Item.Path) || !work.Item.Path.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase))
		{
			context.Report.AddRejected(work.Candidate.ElementAddress, work.Candidate.ItemAddress, "item changed after validation");
			return false;
		}
		context.Report.ValidItems++;
		return true;
	}

	private PanelCandidateResult<SlotInfo> PriceSlotCandidate(SlotScanContext? context, SlotCandidateWork work)
	{
		if (context == null || work.Item == null || !TryPriceItem(context.Pricing, work.Item, out double valueEx, out string label, includeUniqueName: false))
		{
			return PanelCandidateResult<SlotInfo>.Rejected();
		}
		bool isUnique = work.Item.TryGetComponent<Mods>(out Mods component) && component.Rarity == Rarity.Unique;
		if (valueEx < (double)LootValuePricingPolicy.MinimumValueEx(isUnique, Settings.MinValueEx, Settings.UniqueMinValueEx))
		{
			return PanelCandidateResult<SlotInfo>.Rejected();
		}
		context.Report.PricedCandidates++;
		context.Report.VisibleSlots++;
		return PanelCandidateResult<SlotInfo>.Accepted(new SlotInfo(work.Candidate.ItemAddress, work.Position, work.Size, label, work.Candidate.Scroll));
	}

	private ScrollProbeStatus TryGetScrollContainer(nint[] children, out nint itemsAddress, out ScrollBinding scroll)
	{
		itemsAddress = IntPtr.Zero;
		scroll = default(ScrollBinding);
		if (children.Length <= 2 || children[1] == IntPtr.Zero || children[2] == IntPtr.Zero || readUiOffsetMethod == null || readStdVectorMethod == null)
		{
			return ScrollProbeStatus.NotApplicable;
		}
		nint num = children[1];
		nint num2 = children[2];
		if (!(readUiOffsetMethod.Invoke(handleObj, new object[1] { num2 }) is UiElementBaseOffset uiElementBaseOffset) || !UiElementBaseFuncs.IsVisibleChecker(uiElementBaseOffset.Flags) || !(readStdVectorMethod.Invoke(handleObj, new object[1] { uiElementBaseOffset.ChildrensPtr }) is nint[] array) || array.Length == 0 || array[0] == IntPtr.Zero)
		{
			return ScrollProbeStatus.NotApplicable;
		}
		nint num3 = array[0];
		if (scrollRectangleProbeBudget.TryReserve(3) == ScrollProbeStatus.Unavailable)
		{
			return ScrollProbeStatus.Unavailable;
		}
        var status = ScrollContainerGeometry.Probe(ReadScrollRectangle, num, num2, num3, out var geometry);
        if (status != ScrollProbeStatus.Succeeded) return status;
        itemsAddress = num;
        scroll = new ScrollBinding(num2, num3, geometry.ContentHeight, geometry.OffsetY, geometry.ClipTop, geometry.ClipBottom);
        return ScrollProbeStatus.Succeeded;
    }

    private static ScrollRect? ReadScrollRectangle(nint address) =>
        PluginUiElementReflection.TryGetAbsoluteRect(address, out var position, out var size)
            ? new ScrollRect(position, size) : null;

	private static bool TryGetSlotRect(SlotElementCandidate candidate, out Vector2 position, out Vector2 size)
	{
		if (!PluginUiElementReflection.TryGetAbsoluteRect(candidate.ElementAddress, out position, out size))
		{
			return false;
		}
		if (candidate.ParentAddress != IntPtr.Zero && PluginUiElementReflection.TryGetAbsoluteRect(candidate.ParentAddress, out var position2, out var size2) && size2.X >= 20f && size2.Y >= 20f && ((size2.X <= 160f && size2.Y <= 256f) || (size2.X <= 256f && size2.Y <= 160f)))
		{
			position = position2;
			size = size2;
		}
		position.Y -= candidate.Scroll.ScanOffsetY;
		return true;
	}

	private static bool IsAnySlotHovered(IReadOnlyList<SlotInfo> slots, ScrollFrameState scroll)
	{
		Vector2 mousePos = ImGui.GetIO().MousePos;
		foreach (SlotInfo slot in slots)
		{
			if (ScrollProbePolicy.CanUseLivePosition(slot.Scroll.IsActive, scroll.Status))
			{
				Vector2 liveSlotPosition = GetLiveSlotPosition(slot, scroll);
				float num = liveSlotPosition.Y + slot.Size.Y * 0.5f;
				if (!(num < scroll.ClipTop) && !(num > scroll.ClipBottom) && mousePos.X >= liveSlotPosition.X && mousePos.X <= liveSlotPosition.X + slot.Size.X && mousePos.Y >= liveSlotPosition.Y && mousePos.Y <= liveSlotPosition.Y + slot.Size.Y)
				{
					return true;
				}
			}
		}
		return false;
	}

	private ScrollFrameState GetScrollFrameState(IReadOnlyList<SlotInfo> slots)
	{
		foreach (SlotInfo slot in slots)
		{
			ScrollBinding scroll = slot.Scroll;
			if (scroll.IsActive)
			{
				if (scrollRectangleProbeBudget.TryReserve(2) == ScrollProbeStatus.Unavailable)
				{
					return ScrollFrameState.Unavailable(scroll.HolderAddress);
				}
				if (!PluginUiElementReflection.TryGetAbsoluteRect(scroll.HolderAddress, out var position, out var size) || !PluginUiElementReflection.TryGetAbsoluteRect(scroll.ThumbAddress, out var position2, out var size2))
				{
					return ScrollFrameState.Unavailable(scroll.HolderAddress);
				}
				float num = size.Y - size2.Y;
				float num2 = scroll.ContentHeight - size.Y;
				if (num <= 0f || num2 <= 0f)
				{
					return new ScrollFrameState(scroll.HolderAddress, 0f, position.Y, position.Y + size.Y);
				}
				float num3 = Math.Clamp((position2.Y - position.Y) / num, 0f, 1f) * num2;
				return new ScrollFrameState(scroll.HolderAddress, num3 - scroll.ScanOffsetY, position.Y, position.Y + size.Y);
			}
		}
		return ScrollFrameState.None;
	}

	private static Vector2 GetLiveSlotPosition(SlotInfo slot, ScrollFrameState scroll)
	{
		if (!slot.Scroll.IsActive || slot.Scroll.HolderAddress != scroll.HolderAddress)
		{
			return slot.Position;
		}
		return slot.Position - new Vector2(0f, scroll.OffsetDeltaY);
	}

	private void DrawSlotDiagnosticsWindow()
	{
		ImGui.SetNextWindowSize(new Vector2(720f, 420f), ImGuiCond.FirstUseEver);
		if (ImGui.Begin(base.PluginText.Title("diagnostics.slots.window_title", "LootValue Slot Diagnostics", "LootValueSlotDiagnostics"), ref Settings.ShowSlotDebugInfo))
		{
			DrawSlotScanReport(base.PluginText.T("diagnostics.slots.left_panel", "Left panel (stash)"), leftSlotReport);
			ImGui.Separator();
			DrawSlotScanReport(base.PluginText.T("diagnostics.slots.right_panel", "Right panel (inventory)"), rightSlotReport);
			ImGui.Separator();
			DrawSlotScanReport(base.PluginText.T("diagnostics.slots.ritual_rewards", "Ritual rewards"), ritualSlotReport);
		}
		ImGui.End();
	}

	private void DrawSlotScanReport(string label, SlotScanReport report)
	{
		ImGui.TextUnformatted($"{label}: 0x{((IntPtr)report.PanelAddress).ToInt64():X}");
		ImGui.TextUnformatted(base.PluginText.F("diagnostics.slots.summary", "UI elements={0}  non-zero +0x{1:X}={2}  unique pointers={3}  valid items={4}  priced={5}  visible={6}  scroll views={7}  scroll Y={8:0.0}", report.VisitedElements, 1248, report.NonZeroPointers, report.UniquePointers, report.ValidItems, report.PricedCandidates, report.VisibleSlots, report.ScrollContainers, report.ScrollOffsetY));
		ImGui.TextUnformatted(base.PluginText.F("diagnostics.slots.rejected", "Rejected candidates={0} (showing up to {1})", report.RejectedCandidates, 8));
		foreach (string rejectedSample in report.RejectedSamples)
		{
			ImGui.TextUnformatted(rejectedSample);
		}
	}

	private void DrawItemSlots(IReadOnlyList<SlotInfo> slots, bool drawPrices, bool hidePrices, ScrollFrameState scroll)
	{
		ImDrawListPtr backgroundDrawList = ImGui.GetBackgroundDrawList();
		ImFontPtr font = ImGui.GetFont();
		float num = ImGui.GetFontSize() * Settings.SlotFontScale;
		uint col = ImGui.ColorConvertFloat4ToU32(Settings.TextColor);
		foreach (SlotInfo slot in slots)
		{
			if (!ScrollProbePolicy.CanUseLivePosition(slot.Scroll.IsActive, scroll.Status))
			{
				continue;
			}
			Vector2 liveSlotPosition = GetLiveSlotPosition(slot, scroll);
			float num2 = liveSlotPosition.Y + slot.Size.Y * 0.5f;
			if (!(num2 < scroll.ClipTop) && !(num2 > scroll.ClipBottom))
			{
				if (Settings.ShowSlotDebugInfo)
				{
					backgroundDrawList.AddRect(liveSlotPosition, liveSlotPosition + slot.Size, 4294902015u, 0f, ImDrawFlags.None, 2f);
					backgroundDrawList.AddText(font, num, liveSlotPosition, uint.MaxValue, $"E: {((IntPtr)slot.ItemAddress).ToInt64():X}");
				}
				if (!(!drawPrices | hidePrices))
				{
					float num3 = ImGui.CalcTextSize(slot.ValueText).X * Settings.SlotFontScale;
					Vector2 vector = new Vector2(liveSlotPosition.X + Settings.SlotOffsetX, liveSlotPosition.Y + slot.Size.Y - num + Settings.SlotOffsetY);
					backgroundDrawList.AddRectFilled(vector - new Vector2(3f, 1f), vector + new Vector2(num3 + 3f, num + 1f), 2952790016u, 3f);
					backgroundDrawList.AddText(font, num, vector + new Vector2(1f, 1f), 3422552064u, slot.ValueText);
					backgroundDrawList.AddText(font, num, vector, col, slot.ValueText);
				}
			}
		}
	}

	private static Vector2 Track<TKey>(Dictionary<TKey, Tracked> dict, TKey key, Vector2 measure, int rate) where TKey : notnull
	{
		float num = Math.Clamp((float)rate / 1000f, 0.01f, 1f);
		float num2 = num * num / (2f - num);
		if (dict.TryGetValue(key, out var value))
		{
			Vector2 vector = value.Pos + value.Vel;
			Vector2 vector2 = measure - vector;
			if (vector2.LengthSquared() <= 22500f)
			{
				Vector2 vector3 = vector + vector2 * num;
				Vector2 vel = value.Vel + vector2 * num2;
				dict[key] = new Tracked(vector3, vel);
				return vector3;
			}
		}
		dict[key] = new Tracked(measure, Vector2.Zero);
		return measure;
	}

	private void RunDiagnostics(LootValuePricingPass pricing)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow < nextDiagUtc)
		{
			return;
		}
		nextDiagUtc = utcNow.AddMilliseconds(500.0);
		diagSamples.Clear();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		int num7 = 0;
		foreach (Entity value3 in Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values)
		{
			num++;
			string obj = value3.Path ?? string.Empty;
			if (obj.Contains("WorldItem", StringComparison.Ordinal))
			{
				num2++;
			}
			if (obj.StartsWith("Metadata/Items", StringComparison.Ordinal))
			{
				num3++;
			}
			if (!value3.TryGetComponent<WorldItem>(out WorldItem component) || component.ItemEntityAddress == IntPtr.Zero)
			{
				continue;
			}
			num4++;
			Item item = ReadFreshItem(component.ItemEntityAddress);
			if (item == null)
			{
				continue;
			}
			num5++;
			Rarity rarity = (item.TryGetComponent<Mods>(out Mods component2) ? component2.Rarity : Rarity.Normal);
			string value = (item.TryGetComponent<Base>(out Base component3) ? component3.BaseItemName : string.Empty);
			string value2 = (item.TryGetComponent<RenderItem>(out RenderItem component4) ? ExtractArtBasename(component4.ResourcePath) : string.Empty);
			bool flag = TryPriceItem(pricing, item, out double valueEx, out string label);
			if (flag)
			{
				num6++;
				if (valueEx < (double)LootValuePricingPolicy.MinimumValueEx(rarity == Rarity.Unique, Settings.MinValueEx, Settings.UniqueMinValueEx))
				{
					num7++;
				}
			}
			if (diagSamples.Count < 20)
			{
				diagSamples.Add(flag ? $"{rarity} {value} [art={value2}] -> {label} ({valueEx:0.##} ex)" : $"{rarity} {value} [art={value2}] -> {base.PluginText.T("diagnostics.no_price", "NO PRICE")}");
			}
		}
		string text = (pricing.TryGetStatus(out PriceProviderStatus status) ? $"provider={status.ProviderName}  source={status.Source}  items={status.ItemCount}  fetching={status.IsFetching}" : "provider unavailable");
		diagSummary = base.PluginText.F("diagnostics.summary.ingame", "InGame={0}  PanelOpen={1}", Core.States.GameCurrentState == GameStateTypes.InGameState, Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen) + "\n" + base.PluginText.F("diagnostics.summary.awake_entities", "AwakeEntities={0}", num) + "\n" + base.PluginText.F("diagnostics.summary.paths", "path contains 'WorldItem'={0}    path starts 'Metadata/Items'={1}", num2, num3) + "\n" + base.PluginText.F("diagnostics.summary.components", "WorldItem component (inner!=0)={0}    inner item read OK={1}", num4, num5) + "\n" + base.PluginText.F("diagnostics.summary.pricing", "priced={0}    belowFloor(<{1}ex ordinary / <{2}ex Unique)={3}    would draw={4}", num6, Settings.MinValueEx, Settings.UniqueMinValueEx, num7, num6 - num7) + "\n" + text;
	}

	private void DrawDiagnosticsWindow()
	{
		ImGui.SetNextWindowSize(new Vector2(580f, 440f), ImGuiCond.FirstUseEver);
		if (ImGui.Begin(base.PluginText.Title("diagnostics.window_title", "LootValue Diagnostics", "LootValueDiagnostics"), ref Settings.DiagnosticsMode))
		{
			ImGui.TextUnformatted(diagSummary);
			ImGui.Separator();
			ImGui.TextUnformatted(base.PluginText.F("diagnostics.samples", "Samples ({0}):", diagSamples.Count));
			foreach (string diagSample in diagSamples)
			{
				ImGui.TextUnformatted(diagSample);
			}
		}
		ImGui.End();
	}

	private bool TryPriceItem(LootValuePricingPass pricing, Item item, out double valueEx, out string label, bool includeUniqueName = true)
	{
		valueEx = 0.0;
		label = string.Empty;
		Rarity rarity = Rarity.Normal;
		if (item.TryGetComponent<Mods>(out Mods component))
		{
			rarity = component.Rarity;
		}
		string text = ((!item.TryGetComponent<Base>(out Base component2)) ? string.Empty : (component2.BaseItemName?.Trim() ?? string.Empty));
		string text2 = (item.TryGetComponent<RenderItem>(out RenderItem component3) ? ExtractArtBasename(component3.ResourcePath) : string.Empty);
		string text3 = item.Path ?? string.Empty;
		string internalPathBasename = (text3.Contains('/') ? text3.Substring(text3.LastIndexOf('/') + 1) : text3);
		string text4 = text;
		if (rarity == Rarity.Unique && !string.IsNullOrEmpty(text2))
		{
			foreach (string item2 in ArtKeyVariants(text2))
			{
				if (pricing.TryResolveDisplayName(item2, out string displayName) && !pricing.IsGenericLookupName(displayName))
				{
					text4 = displayName;
					break;
				}
				if (pricing.HasPriceDataForName(item2))
				{
					text4 = item2;
					break;
				}
			}
		}
		if (string.IsNullOrWhiteSpace(text4))
		{
			return false;
		}
		List<string> modLines = ItemModHelper.GetModLines(item);
		int num = ((!item.TryGetComponent<Stack>(out Stack component4) || component4.Count <= 1) ? 1 : component4.Count);
		string scoutText = LootValuePricingPolicy.BuildProviderLookupText(text4, text, rarity == Rarity.Unique);
		PriceQuery query = CreateQuery(text4, modLines, internalPathBasename, text3, scoutText);
		if (!pricing.TryPrice(query, num, Settings.DisplayCurrency, out var result))
		{
			return false;
		}
		valueEx = (double)result.ExaltedValue;
		string text5 = result.Text;
		string text6 = ((includeUniqueName && rarity == Rarity.Unique && Settings.RevealUnidentifiedUniques) ? (text4 + " — ") : string.Empty);
		label = text6 + text5;
		return true;
	}

	private static PriceQuery CreateQuery(string itemName, IReadOnlyList<string> explicitMods, string internalPathBasename, string fullItemPath, string scoutText)
	{
		return new PriceQuery(itemName, explicitMods, internalPathBasename, fullItemPath, scoutText);
	}

	private static Item? ReadFreshItem(nint itemAddress)
	{
		if (itemAddress == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			return Activator.CreateInstance(typeof(Item), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[1] { itemAddress }, null) as Item;
		}
		catch
		{
			return null;
		}
	}

	private static string ExtractArtBasename(string? artPath)
	{
		if (string.IsNullOrWhiteSpace(artPath))
		{
			return string.Empty;
		}
		int num = artPath.LastIndexOfAny(new char[2] { '/', '\\' });
		string text = ((num >= 0 && num < artPath.Length - 1) ? artPath.Substring(num + 1) : artPath);
		int num2 = text.LastIndexOf('.');
		if (num2 <= 0)
		{
			return text;
		}
		return text.Substring(0, num2);
	}

	private static IEnumerable<string> ArtKeyVariants(string artBasename)
	{
		if (!string.IsNullOrWhiteSpace(artBasename))
		{
			yield return artBasename;
			if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
			{
				yield return artBasename.Substring(3);
			}
			else
			{
				yield return "The" + artBasename;
			}
		}
	}
}
