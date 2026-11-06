using MacroDeck.Localization;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

// helper class for error messages in one place;
internal static class IssueText
{
	public static LocalizedText Title(RouterProblem problem) => problem.Kind switch
	{
		ProblemKind.NotConfigured => Strings.Issues.NotConfigured.Title(),
		ProblemKind.OutputFailed => Strings.Issues.OutputFailed.Title(problem.Subject),
		ProblemKind.InputMissing => Strings.Issues.InputMissing.Title(problem.Subject),
		ProblemKind.InputBusy => Strings.Issues.InputBusy.Title(problem.Subject),
		ProblemKind.InputIsRouterOutput => Strings.Issues.InputIsRouterOutput.Title(problem.Subject),
		_ => LocalizedText.FromLiteral(problem.Describe()),
	};

	public static LocalizedText Description(RouterProblem problem) => problem.Kind switch
	{
		ProblemKind.NotConfigured => Strings.Issues.NotConfigured.Description(),
		ProblemKind.OutputFailed => Strings.Issues.OutputFailed.Description(problem.Detail ?? string.Empty),
		ProblemKind.InputMissing => Strings.Issues.InputMissing.Description(),
		ProblemKind.InputBusy => Strings.Issues.InputBusy.Description(),
		ProblemKind.InputIsRouterOutput => Strings.Issues.InputIsRouterOutput.Description(),
		_ => LocalizedText.FromLiteral(problem.Describe()),
	};
}
