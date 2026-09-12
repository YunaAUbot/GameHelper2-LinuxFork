using System.Collections.Generic;

namespace LootValue;

internal static class RitualRewardGridPathPolicy
{
	private static readonly int[][] Paths = new int[2][]
	{
		new int[2] { 75, 13 },
		new int[2] { 76, 13 }
	};

	public static IReadOnlyList<int[]> CandidatePaths => Paths;

	public static bool IsPreviouslyValidatedCandidate(nint candidate, nint previous)
	{
		if (candidate != 0)
		{
			return candidate == previous;
		}
		return false;
	}

	public static bool ShouldProbeScroll(bool isRitualGrid)
	{
		return !isRitualGrid;
	}
}
