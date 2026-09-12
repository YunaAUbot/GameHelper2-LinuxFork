using System;

namespace LootValue;

internal struct PanelScanBudget
{
	public int Traversal { get; set; }

	public int Rectangles { get; set; }

	public int Validations { get; set; }

	public int Pricing { get; set; }

	public PanelScanBudget(int traversal, int rectangles, int validations, int pricing)
	{
		if (traversal < 0)
		{
			throw new ArgumentOutOfRangeException("traversal");
		}
		if (rectangles < 0)
		{
			throw new ArgumentOutOfRangeException("rectangles");
		}
		if (validations < 0)
		{
			throw new ArgumentOutOfRangeException("validations");
		}
		if (pricing < 0)
		{
			throw new ArgumentOutOfRangeException("pricing");
		}
		Traversal = traversal;
		Rectangles = rectangles;
		Validations = validations;
		Pricing = pricing;
	}
}
