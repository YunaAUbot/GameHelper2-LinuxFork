using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GameHelper.RemoteObjects.Components;
using GameHelper.RemoteObjects.States.InGameStateObjects;

namespace LootValue;

internal static class ItemModHelper
{
	public static List<string> GetModLines(Item item)
	{
		List<string> list = new List<string>();
		if (item == null)
		{
			return list;
		}
		if (item.TryGetComponent<Mods>(out Mods component))
		{
			AddModGroup(list, component.ImplicitMods);
			AddModGroup(list, component.ExplicitMods);
			AddModGroup(list, component.EnchantMods);
		}
		if (item.TryGetComponent<ObjectMagicProperties>(out ObjectMagicProperties component2))
		{
			AddModGroup(list, component2.Mods);
		}
		return list;
	}

	public static Item? ReadFreshItem(nint itemAddress)
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

	private static void AddModGroup(List<string> lines, List<(string name, (float value0, float value1) values)> mods)
	{
		foreach (var mod in mods)
		{
			string item = mod.name;
			(float, float) item2 = mod.values;
			string text = FormatModLine(item, item2);
			if (!string.IsNullOrWhiteSpace(text))
			{
				lines.Add(text);
			}
		}
	}

	private static string FormatModLine(string template, (float value0, float value1) values)
	{
		if (string.IsNullOrWhiteSpace(template))
		{
			return string.Empty;
		}
		string text = template;
		if (!float.IsNaN(values.value0))
		{
			text = text.Replace("{0}", FormatNumber(values.value0), StringComparison.Ordinal);
			if (!float.IsNaN(values.value1))
			{
				text = text.Replace("{1}", FormatNumber(values.value1), StringComparison.Ordinal);
			}
		}
		return text.Trim();
	}

	private static string FormatNumber(float value)
	{
		if (Math.Abs(value - MathF.Round(value)) < 0.001f)
		{
			return ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
		}
		return value.ToString("0.##", CultureInfo.InvariantCulture);
	}
}
