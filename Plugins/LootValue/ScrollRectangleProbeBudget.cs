using System;

namespace LootValue;

internal struct ScrollRectangleProbeBudget
{
	public int Remaining { get; private set; }

	public ScrollRectangleProbeBudget(int limit)
	{
		if (limit < 0)
		{
			throw new ArgumentOutOfRangeException("limit");
		}
		Remaining = limit;
	}

	public bool TryConsume(int count = 1)
	{
		if (count <= 0)
		{
			throw new ArgumentOutOfRangeException("count");
		}
		if (Remaining < count)
		{
			return false;
		}
		Remaining -= count;
		return true;
	}

	public ScrollProbeStatus TryReserve(int count = 1)
	{
		if (!TryConsume(count))
		{
			return ScrollProbeStatus.Unavailable;
		}
		return ScrollProbeStatus.Succeeded;
	}
}
