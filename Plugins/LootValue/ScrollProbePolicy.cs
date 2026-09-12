namespace LootValue;

internal static class ScrollProbePolicy
{
	public static int RequiredBudget(int traversalCallbacks, int probesPerTraversal, int livePanels, int probesPerLivePanel)
	{
		return checked(traversalCallbacks * probesPerTraversal + livePanels * probesPerLivePanel);
	}

	public static bool CanUseLivePosition(bool isScrollBound, ScrollProbeStatus status)
	{
		if (isScrollBound)
		{
			return status == ScrollProbeStatus.Succeeded;
		}
		return true;
	}

	public static ScrollProbeStatus FromReadResults(bool first, bool second)
	{
		if (!(first & second))
		{
			return ScrollProbeStatus.Unavailable;
		}
		return ScrollProbeStatus.Succeeded;
	}

	public static ScrollProbeStatus FromReadResults(bool first, bool second, bool third)
	{
		if (!(first & second & third))
		{
			return ScrollProbeStatus.Unavailable;
		}
		return ScrollProbeStatus.Succeeded;
	}
}
