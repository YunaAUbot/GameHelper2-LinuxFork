using System;
using System.Collections.Generic;

namespace LootValue;

internal sealed class IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult>
{
	private enum ScanPhase
	{
		Traversal,
		NextGroup,
		Rectangle,
		Validation,
		Pricing
	}

	private readonly Func<TNode, PanelTraversalStep<TNode, TCandidate>> traverse;

	private readonly Func<TCandidate, PanelCandidateResult<TPrepared>> inspectRectangle;

	private readonly Func<TPrepared, bool> validate;

	private readonly Func<TPrepared, PanelCandidateResult<TResult>> price;

	private readonly Func<TCandidate, nint> candidateKey;

	private readonly int maxTraversalElements;

	private readonly int maxDeferredTraversalAttempts;

	private readonly Queue<TNode> traversalQueue = new Queue<TNode>();

	private readonly Dictionary<nint, List<TCandidate>> candidates = new Dictionary<nint, List<TCandidate>>();

	private readonly List<TResult> workingSnapshot = new List<TResult>();

	private IEnumerator<KeyValuePair<nint, List<TCandidate>>>? candidateEnumerator;

	private IReadOnlyList<TCandidate>? currentGroup;

	private int currentCandidateIndex;

	private TPrepared? prepared;

	private ScanPhase phase;

	private int scheduledTraversalElements;

	private int deferredTraversalAttempts;

	public nint Identity { get; private set; }

	public bool IsComplete { get; private set; } = true;

	public bool LastRunSucceeded { get; private set; } = true;

	public IReadOnlyList<TResult> Snapshot { get; private set; } = Array.Empty<TResult>();

	public IncrementalPanelScan(Func<TNode, PanelTraversalStep<TNode, TCandidate>> traverse, Func<TCandidate, PanelCandidateResult<TPrepared>> inspectRectangle, Func<TPrepared, bool> validate, Func<TPrepared, PanelCandidateResult<TResult>> price, Func<TCandidate, nint> candidateKey, int maxTraversalElements, int maxDeferredTraversalAttempts = 3)
	{
		this.traverse = traverse ?? throw new ArgumentNullException("traverse");
		this.inspectRectangle = inspectRectangle ?? throw new ArgumentNullException("inspectRectangle");
		this.validate = validate ?? throw new ArgumentNullException("validate");
		this.price = price ?? throw new ArgumentNullException("price");
		this.candidateKey = candidateKey ?? throw new ArgumentNullException("candidateKey");
		if (maxTraversalElements <= 0)
		{
			throw new ArgumentOutOfRangeException("maxTraversalElements");
		}
		this.maxTraversalElements = maxTraversalElements;
		if (maxDeferredTraversalAttempts <= 0)
		{
			throw new ArgumentOutOfRangeException("maxDeferredTraversalAttempts");
		}
		this.maxDeferredTraversalAttempts = maxDeferredTraversalAttempts;
	}

	public void Restart(nint identity, TNode root)
	{
		if (identity == 0)
		{
			Clear();
			return;
		}
		if (identity != Identity)
		{
			Snapshot = Array.Empty<TResult>();
		}
		ResetWork();
		Identity = identity;
		traversalQueue.Enqueue(root);
		scheduledTraversalElements = 1;
		phase = ScanPhase.Traversal;
		IsComplete = false;
		LastRunSucceeded = false;
	}

	public void Clear()
	{
		ResetWork();
		Identity = 0;
		IsComplete = true;
		LastRunSucceeded = true;
		Snapshot = Array.Empty<TResult>();
	}

	public void Advance(ref PanelScanBudget budget)
	{
		while (TryAdvance(ref budget))
		{
		}
	}

	public bool TryAdvance(ref PanelScanBudget budget)
	{
		if (IsComplete)
		{
			return false;
		}
		MoveToActionablePhase();
		if (IsComplete)
		{
			return false;
		}
		switch (phase)
		{
		case ScanPhase.Traversal:
		{
			if (budget.Traversal <= 0)
			{
				break;
			}
			budget.Traversal--;
			TNode val = traversalQueue.Dequeue();
			PanelTraversalStep<TNode, TCandidate> panelTraversalStep = traverse(val);
			if (panelTraversalStep.IsDeferred)
			{
				deferredTraversalAttempts++;
				if (deferredTraversalAttempts >= maxDeferredTraversalAttempts)
				{
					Abort();
					return true;
				}
				traversalQueue.Enqueue(val);
				budget.Traversal = 0;
				return true;
			}
			deferredTraversalAttempts = 0;
			foreach (TNode child in panelTraversalStep.Children)
			{
				if (scheduledTraversalElements >= maxTraversalElements)
				{
					break;
				}
				traversalQueue.Enqueue(child);
				scheduledTraversalElements++;
			}
			if (panelTraversalStep.HasCandidate)
			{
				TCandidate candidate = panelTraversalStep.Candidate;
				nint key = candidateKey(candidate);
				if (!candidates.TryGetValue(key, out List<TCandidate> value))
				{
					value = new List<TCandidate>();
					candidates.Add(key, value);
				}
				value.Add(candidate);
			}
			return true;
		}
		case ScanPhase.Rectangle:
			if (budget.Rectangles > 0)
			{
				budget.Rectangles--;
				PanelCandidateResult<TPrepared> panelCandidateResult2 = inspectRectangle(currentGroup[currentCandidateIndex++]);
				if (panelCandidateResult2.HasValue)
				{
					prepared = panelCandidateResult2.Value;
					phase = ScanPhase.Validation;
				}
				return true;
			}
			break;
		case ScanPhase.Validation:
			if (budget.Validations > 0)
			{
				budget.Validations--;
				phase = ((!validate(prepared)) ? ScanPhase.NextGroup : ScanPhase.Pricing);
				return true;
			}
			break;
		case ScanPhase.Pricing:
			if (budget.Pricing > 0)
			{
				budget.Pricing--;
				PanelCandidateResult<TResult> panelCandidateResult = price(prepared);
				if (panelCandidateResult.HasValue)
				{
					workingSnapshot.Add(panelCandidateResult.Value);
				}
				phase = ScanPhase.NextGroup;
				return true;
			}
			break;
		}
		return false;
	}

	private void MoveToActionablePhase()
	{
		while (!IsComplete)
		{
			if (phase == ScanPhase.Traversal && traversalQueue.Count == 0)
			{
				candidateEnumerator = candidates.GetEnumerator();
				phase = ScanPhase.NextGroup;
				continue;
			}
			if (phase == ScanPhase.Rectangle && currentCandidateIndex >= currentGroup.Count)
			{
				phase = ScanPhase.NextGroup;
				continue;
			}
			if (phase == ScanPhase.NextGroup)
			{
				if (!candidateEnumerator.MoveNext())
				{
					Complete();
					break;
				}
				currentGroup = candidateEnumerator.Current.Value;
				currentCandidateIndex = 0;
				prepared = default(TPrepared);
				phase = ScanPhase.Rectangle;
				continue;
			}
			break;
		}
	}

	private void Complete()
	{
		Snapshot = workingSnapshot.ToArray();
		ResetWork();
		IsComplete = true;
		LastRunSucceeded = true;
	}

	private void Abort()
	{
		ResetWork();
		IsComplete = true;
		LastRunSucceeded = false;
	}

	private void ResetWork()
	{
		traversalQueue.Clear();
		candidates.Clear();
		workingSnapshot.Clear();
		candidateEnumerator?.Dispose();
		candidateEnumerator = null;
		currentGroup = null;
		currentCandidateIndex = 0;
		prepared = default(TPrepared);
		scheduledTraversalElements = 0;
		deferredTraversalAttempts = 0;
		phase = ScanPhase.Traversal;
	}
}
