using System;
using System.Collections.Generic;

namespace LootValue;

internal readonly struct PanelTraversalStep<TNode, TCandidate>
{
	public IReadOnlyList<TNode> Children { get; }

	public TCandidate? Candidate { get; }

	public bool HasCandidate { get; }

	public bool IsDeferred { get; }

	public PanelTraversalStep(IReadOnlyList<TNode> children)
	{
		Children = children;
		Candidate = default(TCandidate);
		HasCandidate = false;
		IsDeferred = false;
	}

	public PanelTraversalStep(IReadOnlyList<TNode> children, TCandidate candidate)
	{
		Children = children;
		Candidate = candidate;
		HasCandidate = true;
		IsDeferred = false;
	}

	private PanelTraversalStep(bool isDeferred)
	{
		Children = Array.Empty<TNode>();
		Candidate = default(TCandidate);
		HasCandidate = false;
		IsDeferred = isDeferred;
	}

	public static PanelTraversalStep<TNode, TCandidate> Deferred()
	{
		return new PanelTraversalStep<TNode, TCandidate>(isDeferred: true);
	}
}
