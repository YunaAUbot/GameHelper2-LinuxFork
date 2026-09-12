namespace LootValue;

internal readonly struct PanelCandidateResult<TResult>
{
	public bool HasValue { get; }

	public TResult? Value { get; }

	private PanelCandidateResult(bool hasValue, TResult? value)
	{
		HasValue = hasValue;
		Value = value;
	}

	public static PanelCandidateResult<TResult> Accepted(TResult value)
	{
		return new PanelCandidateResult<TResult>(hasValue: true, value);
	}

	public static PanelCandidateResult<TResult> Rejected()
	{
		return new PanelCandidateResult<TResult>(hasValue: false, default(TResult));
	}
}
