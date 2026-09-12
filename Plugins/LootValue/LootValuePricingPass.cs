using System;
using System.Globalization;
using GameHelper.Plugin.Price;

namespace LootValue;

internal sealed class LootValuePricingPass
{
	private readonly IPriceProvider? provider;

	private LootValuePricingPass(IPriceProvider? provider)
	{
		this.provider = provider;
	}

	public static LootValuePricingPass Capture(Func<IPriceProvider?> getProvider)
	{
		ArgumentNullException.ThrowIfNull(getProvider, "getProvider");
		try
		{
			return new LootValuePricingPass(getProvider());
		}
		catch (Exception)
		{
			return new LootValuePricingPass(null);
		}
	}

	public void RequestRefreshSafely()
	{
		try
		{
			provider?.RequestRefresh();
		}
		catch (Exception)
		{
		}
	}

	public bool TryPrice(PriceQuery query, long stack, int displayCurrency, out LootValuePrice result)
	{
		result = default(LootValuePrice);
		if (provider == null || stack <= 0)
		{
			return false;
		}
		try
		{
			if (!provider.TryGetPrice(query, out PriceQuote quote) || quote == null || !IsValid(quote))
			{
				return false;
			}
			decimal d = quote.ChaosValue * (decimal)stack;
			decimal d2 = quote.DivineValue * (decimal)stack;
			decimal num = quote.ExaltedValue * (decimal)stack;
			(decimal Value, string Currency) pair = displayCurrency switch
			{
				2 => (decimal.Round(d, 1), "chaos"),
				1 => (decimal.Round(num, 1), "ex"),
				_ => (decimal.Round(d2, 3), "divine"),
			};
			decimal item = pair.Value;
			string item2 = pair.Currency;
			result = new LootValuePrice(num, item, FormatValue(item, item2));
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public bool TryResolveDisplayName(string lookupKey, out string displayName)
	{
		displayName = string.Empty;
		if (provider == null || string.IsNullOrWhiteSpace(lookupKey))
		{
			return false;
		}
		try
		{
			PriceQuery query = new PriceQuery(lookupKey, Array.Empty<string>(), lookupKey, string.Empty, lookupKey);
			return provider.TryResolveDisplayName(query, out displayName) && !string.IsNullOrWhiteSpace(displayName);
		}
		catch (Exception)
		{
			displayName = string.Empty;
			return false;
		}
	}

	public bool IsGenericLookupName(string itemName)
	{
		try
		{
			return provider?.IsGenericLookupName(itemName) ?? true;
		}
		catch (Exception)
		{
			return true;
		}
	}

	public bool HasPriceDataForName(string itemName)
	{
		try
		{
			return provider?.HasPriceDataForName(itemName) ?? false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public bool TryGetStatus(out PriceProviderStatus status)
	{
		status = null;
		if (provider == null)
		{
			return false;
		}
		try
		{
			status = provider.Status;
			return status != null;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsValid(PriceQuote quote)
	{
		if (quote.ChaosValue > 0m && quote.DivineValue > 0m && quote.ExaltedValue > 0m)
		{
			return !string.IsNullOrWhiteSpace(quote.SourceName);
		}
		return false;
	}

	private static string FormatValue(decimal value, string currency)
	{
		if (!(currency == "divine"))
		{
			if (currency == "chaos")
			{
				return value.ToString("0.#", CultureInfo.InvariantCulture) + " c";
			}
			return value.ToString("0.#", CultureInfo.InvariantCulture) + " ex";
		}
		return value.ToString("0.00", CultureInfo.InvariantCulture) + " div";
	}
}
