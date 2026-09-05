// <copyright file="IncrementalPanelScan.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace LootValue
{
    using System;
    using System.Collections.Generic;

    /// <summary>Aggregate allowance for scroll-related absolute-rectangle probes in one render pass.</summary>
    internal struct ScrollRectangleProbeBudget
    {
        public ScrollRectangleProbeBudget(int limit)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            this.Remaining = limit;
        }

        public int Remaining { get; private set; }

        public bool TryConsume(int count = 1)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (this.Remaining < count) return false;
            this.Remaining -= count;
            return true;
        }

        public ScrollProbeStatus TryReserve(int count = 1) =>
            this.TryConsume(count) ? ScrollProbeStatus.Succeeded : ScrollProbeStatus.Unavailable;
    }

    /// <summary>Remaining aggregate work allowed for one render pass.</summary>
    internal struct PanelScanBudget
    {
        public PanelScanBudget(int traversal, int rectangles, int validations, int pricing)
        {
            if (traversal < 0) throw new ArgumentOutOfRangeException(nameof(traversal));
            if (rectangles < 0) throw new ArgumentOutOfRangeException(nameof(rectangles));
            if (validations < 0) throw new ArgumentOutOfRangeException(nameof(validations));
            if (pricing < 0) throw new ArgumentOutOfRangeException(nameof(pricing));
            this.Traversal = traversal;
            this.Rectangles = rectangles;
            this.Validations = validations;
            this.Pricing = pricing;
        }

        public int Traversal { get; set; }

        public int Rectangles { get; set; }

        public int Validations { get; set; }

        public int Pricing { get; set; }
    }

    /// <summary>A render-thread scan with independently bounded traversal, rectangle, validation, and pricing work.</summary>
    internal sealed class IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult>
    {
        private readonly Func<TNode, PanelTraversalStep<TNode, TCandidate>> traverse;
        private readonly Func<TCandidate, PanelCandidateResult<TPrepared>> inspectRectangle;
        private readonly Func<TPrepared, bool> validate;
        private readonly Func<TPrepared, PanelCandidateResult<TResult>> price;
        private readonly Func<TCandidate, nint> candidateKey;
        private readonly int maxTraversalElements;
        private readonly int maxDeferredTraversalAttempts;
        private readonly Queue<TNode> traversalQueue = new();
        private readonly Dictionary<nint, List<TCandidate>> candidates = new();
        private readonly List<TResult> workingSnapshot = new();
        private IEnumerator<KeyValuePair<nint, List<TCandidate>>>? candidateEnumerator;
        private IReadOnlyList<TCandidate>? currentGroup;
        private int currentCandidateIndex;
        private TPrepared? prepared;
        private ScanPhase phase;
        private int scheduledTraversalElements;
        private int deferredTraversalAttempts;

        public IncrementalPanelScan(
            Func<TNode, PanelTraversalStep<TNode, TCandidate>> traverse,
            Func<TCandidate, PanelCandidateResult<TPrepared>> inspectRectangle,
            Func<TPrepared, bool> validate,
            Func<TPrepared, PanelCandidateResult<TResult>> price,
            Func<TCandidate, nint> candidateKey,
            int maxTraversalElements,
            int maxDeferredTraversalAttempts = 3)
        {
            this.traverse = traverse ?? throw new ArgumentNullException(nameof(traverse));
            this.inspectRectangle = inspectRectangle ?? throw new ArgumentNullException(nameof(inspectRectangle));
            this.validate = validate ?? throw new ArgumentNullException(nameof(validate));
            this.price = price ?? throw new ArgumentNullException(nameof(price));
            this.candidateKey = candidateKey ?? throw new ArgumentNullException(nameof(candidateKey));
            this.maxTraversalElements = maxTraversalElements > 0
                ? maxTraversalElements
                : throw new ArgumentOutOfRangeException(nameof(maxTraversalElements));
            this.maxDeferredTraversalAttempts = maxDeferredTraversalAttempts > 0
                ? maxDeferredTraversalAttempts
                : throw new ArgumentOutOfRangeException(nameof(maxDeferredTraversalAttempts));
        }

        public nint Identity { get; private set; }

        public bool IsComplete { get; private set; } = true;

        public bool LastRunSucceeded { get; private set; } = true;

        public IReadOnlyList<TResult> Snapshot { get; private set; } = Array.Empty<TResult>();

        public void Restart(nint identity, TNode root)
        {
            if (identity == 0)
            {
                this.Clear();
                return;
            }

            if (identity != this.Identity) this.Snapshot = Array.Empty<TResult>();
            this.ResetWork();
            this.Identity = identity;
            this.traversalQueue.Enqueue(root);
            this.scheduledTraversalElements = 1;
            this.phase = ScanPhase.Traversal;
            this.IsComplete = false;
            this.LastRunSucceeded = false;
        }

        public void Clear()
        {
            this.ResetWork();
            this.Identity = 0;
            this.IsComplete = true;
            this.LastRunSucceeded = true;
            this.Snapshot = Array.Empty<TResult>();
        }

        public void Advance(ref PanelScanBudget budget)
        {
            while (this.TryAdvance(ref budget))
            {
            }
        }

        public bool TryAdvance(ref PanelScanBudget budget)
        {
            if (this.IsComplete) return false;
            this.MoveToActionablePhase();
            if (this.IsComplete) return false;

            switch (this.phase)
            {
                case ScanPhase.Traversal when budget.Traversal > 0:
                    budget.Traversal--;
                    var node = this.traversalQueue.Dequeue();
                    var step = this.traverse(node);
                    if (step.IsDeferred)
                    {
                        this.deferredTraversalAttempts++;
                        if (this.deferredTraversalAttempts >= this.maxDeferredTraversalAttempts)
                        {
                            this.Abort();
                            return true;
                        }

                        this.traversalQueue.Enqueue(node);
                        budget.Traversal = 0;
                        return true;
                    }

                    this.deferredTraversalAttempts = 0;
                    foreach (var child in step.Children)
                    {
                        if (this.scheduledTraversalElements >= this.maxTraversalElements) break;
                        this.traversalQueue.Enqueue(child);
                        this.scheduledTraversalElements++;
                    }

                    if (step.HasCandidate)
                    {
                        var candidate = step.Candidate!;
                        var key = this.candidateKey(candidate);
                        if (!this.candidates.TryGetValue(key, out var group))
                        {
                            group = new List<TCandidate>();
                            this.candidates.Add(key, group);
                        }

                        group.Add(candidate);
                    }

                    return true;
                case ScanPhase.Rectangle when budget.Rectangles > 0:
                    budget.Rectangles--;
                    var inspected = this.inspectRectangle(this.currentGroup![this.currentCandidateIndex++]);
                    if (inspected.HasValue)
                    {
                        this.prepared = inspected.Value;
                        this.phase = ScanPhase.Validation;
                    }

                    return true;
                case ScanPhase.Validation when budget.Validations > 0:
                    budget.Validations--;
                    this.phase = this.validate(this.prepared!) ? ScanPhase.Pricing : ScanPhase.NextGroup;
                    return true;
                case ScanPhase.Pricing when budget.Pricing > 0:
                    budget.Pricing--;
                    var result = this.price(this.prepared!);
                    if (result.HasValue) this.workingSnapshot.Add(result.Value!);
                    this.phase = ScanPhase.NextGroup;
                    return true;
                default:
                    return false;
            }
        }

        private void MoveToActionablePhase()
        {
            while (!this.IsComplete)
            {
                if (this.phase == ScanPhase.Traversal && this.traversalQueue.Count == 0)
                {
                    this.candidateEnumerator = this.candidates.GetEnumerator();
                    this.phase = ScanPhase.NextGroup;
                    continue;
                }

                if (this.phase == ScanPhase.Rectangle && this.currentCandidateIndex >= this.currentGroup!.Count)
                {
                    this.phase = ScanPhase.NextGroup;
                    continue;
                }

                if (this.phase == ScanPhase.NextGroup)
                {
                    if (!this.candidateEnumerator!.MoveNext())
                    {
                        this.Complete();
                        return;
                    }

                    this.currentGroup = this.candidateEnumerator.Current.Value;
                    this.currentCandidateIndex = 0;
                    this.prepared = default;
                    this.phase = ScanPhase.Rectangle;
                    continue;
                }

                return;
            }
        }

        private void Complete()
        {
            this.Snapshot = this.workingSnapshot.ToArray();
            this.ResetWork();
            this.IsComplete = true;
            this.LastRunSucceeded = true;
        }

        private void Abort()
        {
            this.ResetWork();
            this.IsComplete = true;
            this.LastRunSucceeded = false;
        }

        private void ResetWork()
        {
            this.traversalQueue.Clear();
            this.candidates.Clear();
            this.workingSnapshot.Clear();
            this.candidateEnumerator?.Dispose();
            this.candidateEnumerator = null;
            this.currentGroup = null;
            this.currentCandidateIndex = 0;
            this.prepared = default;
            this.scheduledTraversalElements = 0;
            this.deferredTraversalAttempts = 0;
            this.phase = ScanPhase.Traversal;
        }

        private enum ScanPhase
        {
            Traversal,
            NextGroup,
            Rectangle,
            Validation,
            Pricing,
        }
    }

    /// <summary>Round-robins two panel scans through one aggregate per-frame budget.</summary>
    internal sealed class IncrementalPanelScanScheduler<TNode, TCandidate, TPrepared, TResult>
    {
        private bool startWithRight;

        public void Advance(
            IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult> left,
            IncrementalPanelScan<TNode, TCandidate, TPrepared, TResult> right,
            ref PanelScanBudget budget)
        {
            var chooseRight = this.startWithRight;
            var stalled = 0;
            while (stalled < 2)
            {
                var progressed = (chooseRight ? right : left).TryAdvance(ref budget);
                chooseRight = !chooseRight;
                stalled = progressed ? 0 : stalled + 1;
            }

            this.startWithRight = !this.startWithRight;
        }
    }

    internal readonly struct PanelTraversalStep<TNode, TCandidate>
    {
        public PanelTraversalStep(IReadOnlyList<TNode> children)
        {
            this.Children = children;
            this.Candidate = default;
            this.HasCandidate = false;
            this.IsDeferred = false;
        }

        public PanelTraversalStep(IReadOnlyList<TNode> children, TCandidate candidate)
        {
            this.Children = children;
            this.Candidate = candidate;
            this.HasCandidate = true;
            this.IsDeferred = false;
        }

        private PanelTraversalStep(bool isDeferred)
        {
            this.Children = Array.Empty<TNode>();
            this.Candidate = default;
            this.HasCandidate = false;
            this.IsDeferred = isDeferred;
        }

        public IReadOnlyList<TNode> Children { get; }

        public TCandidate? Candidate { get; }

        public bool HasCandidate { get; }

        public bool IsDeferred { get; }

        public static PanelTraversalStep<TNode, TCandidate> Deferred() => new(true);
    }

    internal readonly struct PanelCandidateResult<TResult>
    {
        private PanelCandidateResult(bool hasValue, TResult? value)
        {
            this.HasValue = hasValue;
            this.Value = value;
        }

        public bool HasValue { get; }

        public TResult? Value { get; }

        public static PanelCandidateResult<TResult> Accepted(TResult value) => new(true, value);

        public static PanelCandidateResult<TResult> Rejected() => new(false, default);
    }

    internal enum ScrollProbeStatus
    {
        NotApplicable,
        Succeeded,
        Unavailable,
    }

    /// <summary>Deterministic policy shared by traversal and live scroll probes.</summary>
    internal static class ScrollProbePolicy
    {
        public static int RequiredBudget(
            int traversalCallbacks,
            int probesPerTraversal,
            int livePanels,
            int probesPerLivePanel) =>
            checked((traversalCallbacks * probesPerTraversal) + (livePanels * probesPerLivePanel));

        public static bool CanUseLivePosition(bool isScrollBound, ScrollProbeStatus status) =>
            !isScrollBound || status == ScrollProbeStatus.Succeeded;

        public static ScrollProbeStatus FromReadResults(bool first, bool second) =>
            first && second ? ScrollProbeStatus.Succeeded : ScrollProbeStatus.Unavailable;

        public static ScrollProbeStatus FromReadResults(bool first, bool second, bool third) =>
            first && second && third ? ScrollProbeStatus.Succeeded : ScrollProbeStatus.Unavailable;
    }
}
