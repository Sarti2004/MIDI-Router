using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;

internal sealed class SetModeAction(MidiRouter router) :
	IActionDefinition,
	IStateProviderActionDefinition
{
	public const string ModeParameter = "mode";

	private const string ModeToggle = "toggle";

	private const string StateActive = "active";
	private const string StateInactive = "inactive";

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new(StateActive, Strings.Actions.SetMode.StateActive())
		{
			DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2E7D32" },
		},
		new(StateInactive, Strings.Actions.SetMode.StateInactive()),
	];

	internal MidiRouter Router { get; } = router;

	public string Id => "set-mode";

	public LocalizedText Name => Strings.Actions.SetMode.Name();

	public LocalizedText Description => Strings.Actions.SetMode.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		new ActionParameter
		{
			Name = ModeParameter,
			Type = ActionParameterType.Choice,
			Options =
			[
				.. ModeController.Names.Select(mode => new ActionParameterOption { Value = mode, Label = LocalizedText.FromLiteral(mode) }),
				new ActionParameterOption { Value = ModeToggle, Label = Strings.Actions.SetMode.Toggle() },
			],
			Label = Strings.Actions.SetMode.Target.Label(),
			Description = Strings.Actions.SetMode.Target.Description(),
			DefaultValue = ModeController.Name(RouterMode.Control),
			LiteralOnly = true,
		},
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows | MacroDeckPlatform.MacOs;

	public TimeSpan StatePollInterval => TimeSpan.FromMilliseconds(500);

	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
		Task.FromResult<ActionStateSnapshot?>(new(States, StateFor(Read(parameters, ModeParameter))));

	public IActionExecutor CreateExecutor() => new Executor(this);

	private string StateFor(string? mode) =>
		(mode == ModeToggle ? RouterMode.Control : ModeController.Parse(mode)) == Router.Modes.Current ? StateActive : StateInactive;

	private static string? Read<T>(IReadOnlyDictionary<string, T> parameters, string name) =>
		parameters.TryGetValue(name, out var value) ? value?.ToString() : null;

	private sealed class Executor(SetModeAction action) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var mode = Read(context.Parameters, ModeParameter);
			var outcome = mode == ModeToggle ? action.Router.Modes.Toggle() : action.Router.Modes.Request(mode);
			return Task.FromResult(outcome == ModeChangeOutcome.UnknownMode
				? ActionResult.Failed("unknown-mode", Strings.Actions.SetMode.UnknownMode())
				: ActionResult.Success(action.StateFor(mode)));
		}
	}
}
