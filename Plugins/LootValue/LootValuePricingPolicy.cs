using System;

namespace LootValue;

internal static class LootValuePricingPolicy
{
	public static float MinimumValueEx(bool isUnique, float generalFloor, float uniqueFloor)
	{
		return Math.Max(0f, isUnique ? uniqueFloor : generalFloor);
	}

	public static string BuildProviderLookupText(string itemName, string baseType, bool isUnique)
	{
		if (string.IsNullOrWhiteSpace(itemName))
		{
			return string.Empty;
		}
		string text = itemName.Trim();
		if (!isUnique || string.IsNullOrWhiteSpace(baseType))
		{
			return text;
		}
		if (baseType.Contains("Runemastered", StringComparison.OrdinalIgnoreCase))
		{
			if (!text.EndsWith(" Runemastered", StringComparison.OrdinalIgnoreCase))
			{
				return text + " Runemastered";
			}
			return text;
		}
		if (baseType.Contains("Runeforged", StringComparison.OrdinalIgnoreCase))
		{
			if (!text.EndsWith(" Runeforged", StringComparison.OrdinalIgnoreCase))
			{
				return text + " Runeforged";
			}
			return text;
		}
		return text;
	}

	public static string MergeGroundUniqueLookupText(string? existing, string itemName, string baseType)
	{
		string text = BuildProviderLookupText(itemName?.Trim() ?? string.Empty, baseType, isUnique: true);
		if (existing == null || existing.Equals(text, StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		return string.Empty;
	}
}
