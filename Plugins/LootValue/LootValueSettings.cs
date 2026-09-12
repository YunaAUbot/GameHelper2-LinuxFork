using System.Numerics;
using GameHelper.Plugin;

namespace LootValue;

public sealed class LootValueSettings : IPSettings
{
	public bool ShowOverlay = true;

	public bool ShowStashOverlay = true;

	public bool ShowInventoryOverlay;

	public bool ShowRitualOverlay = true;

	public bool ShowCurrencyExchangeOverlay = true;

	public bool HideWhenGameInBackground = true;

	public bool HideSlotPricesOnHover = true;

	public bool ShowSlotDebugInfo;

	public bool AnchorToLootTags = true;

	public int DisplayCurrency = 1;

	public float MinValueEx = 1f;

	public float UniqueMinValueEx;

	public float HighlightMinEx = 10f;

	public bool RevealUnidentifiedUniques = true;

	public bool DiagnosticsMode;

	public float FontSize = 16f;

	public float HighlightFontSize = 22f;

	public bool HighlightBold = true;

	public float OffsetY = -10f;

	public float SlotFontScale = 1f;

	public float SlotOffsetX = 5f;

	public float SlotOffsetY = -5f;

	public bool InterpolatePosition = true;

	public int InterpolationRate = 110;

	public int RescanIntervalMs = 200;

	public int SlotRescanIntervalMs = 750;

	public Vector4 TextColor = new Vector4(1f, 47f / 51f, 28f / 51f, 1f);

	public Vector4 HighlightColor = new Vector4(0.4f, 1f, 0.4f, 1f);
}
