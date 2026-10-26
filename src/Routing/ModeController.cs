namespace MIDIRouter.Routing;

internal enum RouterMode
{
	Play,
	Control,
}

internal enum ModeChangeOutcome
{
	Applied,
	Unchanged,
	UnknownMode,
}

internal sealed class ModeController
{
	public static readonly IReadOnlyList<string> Names = ["Play", "Control"];

	private int _current;

	private long _epoch;

	public event Action? Changed;

	public RouterMode Current => (RouterMode)Volatile.Read(ref _current);

	public long Epoch => Interlocked.Read(ref _epoch);

	public string CurrentName => Name(Current);

	public static string Name(RouterMode mode) => Names[(int)mode];

	public static RouterMode? Parse(string? name)
	{
		for (var i = 0; i < Names.Count; i++)
		{
			if (string.Equals(Names[i], name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return (RouterMode)i;
			}
		}

		return null;
	}

	public ModeChangeOutcome Request(string? mode) =>
		Parse(mode) is { } parsed ? Set(parsed) : ModeChangeOutcome.UnknownMode;

	public ModeChangeOutcome Set(RouterMode mode)
	{
		// Even an unchanged mode resets the keyboards to it.
		Interlocked.Increment(ref _epoch);
		if (Interlocked.Exchange(ref _current, (int)mode) == (int)mode)
		{
			return ModeChangeOutcome.Unchanged;
		}

		ThreadPool.UnsafeQueueUserWorkItem(static self => self.Changed?.Invoke(), this, preferLocal: false);
		return ModeChangeOutcome.Applied;
	}

	public ModeChangeOutcome Toggle() => Set(Current == RouterMode.Play ? RouterMode.Control : RouterMode.Play);
}
