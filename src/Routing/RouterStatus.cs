namespace MIDIRouter.Routing;

internal enum ProblemKind
{
	NotConfigured,
	OutputNotConfigured,
	VirtualOutputUnsupported,
	OutputMissing,
	OutputFailed,
	InputMissing,
	InputBusy,
	InputPartialMatch,
	InputIsRouterOutput,
}

internal enum ProblemSeverity
{
	Info,
	Warning,
	Error,
}

internal sealed record RouterProblem(ProblemKind Kind, string Subject, string? Detail)
{
	public string Id => Subject.Length == 0 ? Kind.ToString() : $"{Kind}:{Subject}";

	public ProblemSeverity Severity => Kind switch
	{
		ProblemKind.NotConfigured or ProblemKind.InputPartialMatch => ProblemSeverity.Info,
		ProblemKind.InputMissing or ProblemKind.InputBusy or ProblemKind.InputIsRouterOutput => ProblemSeverity.Warning,
		_ => ProblemSeverity.Error,
	};

	public string Describe() => Kind switch
	{
		ProblemKind.NotConfigured => "No keyboards selected",
		ProblemKind.OutputNotConfigured => "No output port selected",
		ProblemKind.VirtualOutputUnsupported => "A virtual port cannot be created on this OS; select a loopback port",
		ProblemKind.OutputMissing => $"Output port '{Subject}' not found",
		ProblemKind.OutputFailed => $"Output port '{Subject}' could not be opened: {Detail}",
		ProblemKind.InputMissing => $"Keyboard '{Subject}' not connected",
		ProblemKind.InputBusy => $"Keyboard '{Subject}' is in use by another app",
		ProblemKind.InputPartialMatch => $"Keyboard '{Subject}' opened as '{Detail}'",
		ProblemKind.InputIsRouterOutput => $"'{Subject}' is the router's own output and is never read",
		_ => Kind.ToString(),
	};
}

internal sealed record RouterStatus(bool Enabled, int OpenInputs, OutputState Output, IReadOnlyList<RouterProblem> Problems)
{
	public static readonly RouterStatus Starting = new(true, 0, OutputState.NotConfigured, []);

	public string Text
	{
		get
		{
			if (!Enabled)
			{
				return "Off";
			}

			var worst = Problems.Where(problem => problem.Severity > ProblemSeverity.Info).MaxBy(problem => problem.Severity)
				?? Problems.FirstOrDefault(problem => problem.Kind == ProblemKind.NotConfigured);
			return worst?.Describe() ?? "OK";
		}
	}
}
