using System;
using System.Collections.Generic;

namespace LootValue;

internal sealed class IncrementalPanelScanScheduler<TNode, TCandidate, TPrepared, TResult>
{
	private int startIndex;

	public void Advance(IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult> left, IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult> right, ref PanelScanBudget budget)
	{
		Advance(new IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult>[2] { left, right }, ref budget);
	}

	public void Advance(IReadOnlyList<IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult>> scans, ref PanelScanBudget budget)
	{
		if (scans == null)
		{
			throw new ArgumentNullException("scans");
		}
		if (scans.Count != 0)
		{
			int num = startIndex % scans.Count;
			int num2 = 0;
			while (num2 < scans.Count)
			{
				bool num3 = scans[num].TryAdvance(ref budget);
				num = (num + 1) % scans.Count;
				num2 = ((!num3) ? (num2 + 1) : 0);
			}
			startIndex = (startIndex + 1) % scans.Count;
		}
	}
}
