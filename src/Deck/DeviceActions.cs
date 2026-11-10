using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace MIDIRouter.Deck;

/// <summary>
/// The MIDI Router actions an element can run, and running them: a button runs its action as configured, a slider
/// runs Send MIDI CC with its position as the value. Each slider also has a writable variable holding that position,
/// so other widgets and automations can read it and move the slider.
/// </summary>
internal sealed class DeviceActions
{
	public const string SendNote = "send-note";
	public const string SendControlChange = "send-cc";
	public const string SendProgramChange = "send-program-change";

	// The parameter names of those actions, read for the Cubase export and filled in for new elements.
	public const string ChannelParameter = "channel";
	public const string NoteParameter = "note";
	public const string ControllerParameter = "controller";
	public const string ValueParameter = "value";
	public const string ProgramParameter = "program";

	public const string SliderPrefix = "slider-";
	private static readonly TimeSpan ExecutionLimit = TimeSpan.FromSeconds(30);

	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, int> _sliderValues = new(StringComparer.Ordinal);

	public DeviceActions(IEnumerable<IActionDefinition> actions, ILogger logger)
	{
		_logger = logger.ForContext<DeviceActions>();
		All = [.. actions.Where(action => action.Id is SendNote or SendControlChange or SendProgramChange)];
		ById = All.ToDictionary(action => action.Id, StringComparer.Ordinal);
	}

	/// <summary>A slider moved: its variable id and its value, 0-127.</summary>
	public event Action<string, int>? SliderMoved;

	public IReadOnlyList<IActionDefinition> All { get; }

	public IReadOnlyDictionary<string, IActionDefinition> ById { get; }

	/// <summary>The actions an element of this kind may run: a slider only sends CCs.</summary>
	public IReadOnlyList<IActionDefinition> For(DeviceElementKind kind) =>
		kind is DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider
			? [.. All.Where(action => action.Id == SendControlChange)]
			: All;

	/// <summary>The parameters the editor shows: a slider's value comes from its position.</summary>
	public static bool IsEdited(DeviceElement element, ActionParameter parameter) =>
		!(element.IsSlider && parameter.Name == ValueParameter);

	public static MidiBinding? Binding(DeviceElement element)
	{
		var channel = Number(element.Parameters, ChannelParameter, 1, 16, 1);
		return element.ActionId switch
		{
			SendNote => new MidiBinding(MidiMessageKind.Note, channel, Number(element.Parameters, NoteParameter, 0, 127, 60)),
			SendControlChange => new MidiBinding(MidiMessageKind.ControlChange, channel, Number(element.Parameters, ControllerParameter, 0, 127, 7)),
			SendProgramChange => new MidiBinding(MidiMessageKind.ProgramChange, channel, Number(element.Parameters, ProgramParameter, 0, 127, 0)),
			_ => null,
		};
	}

	/// <summary>Every parameter at its default, then the channel and number given.</summary>
	public Dictionary<string, object?> Defaults(string actionId, int channel, int number)
	{
		var parameters = ById.TryGetValue(actionId, out var action)
			? action.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.DefaultValue, StringComparer.Ordinal)
			: new Dictionary<string, object?>(StringComparer.Ordinal);
		parameters[ChannelParameter] = (double)channel;
		parameters[actionId switch
		{
			SendControlChange => ControllerParameter,
			SendProgramChange => ProgramParameter,
			_ => NoteParameter,
		}] = (double)number;
		return parameters;
	}

	public Task PressAsync(DeviceElement element, CancellationToken cancellationToken) =>
		element.IsSlider ? Task.CompletedTask : RunAsync(element, element.Parameters, cancellationToken);

	/// <summary>Sends the slider's CC at <paramref name="value"/> (0-127) and keeps it as the slider's position.</summary>
	public Task SlideAsync(DeviceElement element, int value, CancellationToken cancellationToken)
	{
		value = Math.Clamp(value, 0, 127);
		var variableId = SliderVariableId(element);
		if (_sliderValues.TryGetValue(variableId, out var previous) && previous == value)
		{
			return Task.CompletedTask;
		}

		_sliderValues[variableId] = value;
		SliderMoved?.Invoke(variableId, value);
		var parameters = new Dictionary<string, object?>(element.Parameters, StringComparer.Ordinal) { [ValueParameter] = (double)value };
		return RunAsync(element, parameters, cancellationToken);
	}

	public int SliderValue(DeviceElement element) => _sliderValues.GetValueOrDefault(SliderVariableId(element));

	public static string SliderVariableId(DeviceElement element) => SliderPrefix + element.Id;

	public static bool OwnsVariable(string localId) => localId.StartsWith(SliderPrefix, StringComparison.Ordinal);

	public static IReadOnlyList<VariableDefinition> Variables(VirtualDevice device) =>
	[
		.. device.Elements.Where(element => element.IsSlider).Select(element =>
			VariableDefinition.Eager(VariableNames.Sanitize("midi_router_" + element.Id.Replace('-', '_')), VariableType.Numeric) with
			{
				Id = SliderVariableId(element),
				DisplayName = LocalizedText.FromLiteral($"{device.Name}: {element.Label}"),
				Write = new VariableWriteCapability(),
			}),
	];

	public VariableReading Read(VirtualDevice device, string localId) =>
		device.Elements.FirstOrDefault(element => element.IsSlider && SliderVariableId(element) == localId) is { } slider
			? VariableReading.Of(SliderValue(slider), 0, 127, 1)
			: VariableReading.Unavailable;

	// Another widget or an automation set the variable: the slider moves and sends its CC.
	public async ValueTask<VariableWriteResult> WriteAsync(VirtualDevice device, string localId, object? value, CancellationToken cancellationToken)
	{
		if (device.Elements.FirstOrDefault(element => element.IsSlider && SliderVariableId(element) == localId) is not { } slider)
		{
			return VariableWriteResult.NotFound();
		}

		if (!double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			|| double.IsNaN(number))
		{
			return VariableWriteResult.Unavailable();
		}

		await SlideAsync(slider, (int)Math.Round(number), cancellationToken).ConfigureAwait(false);
		return VariableWriteResult.Applied();
	}

	private async Task RunAsync(DeviceElement element, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		if (!ById.TryGetValue(element.ActionId, out var action))
		{
			_logger.Warning("Element {Element} runs unknown action {ActionId}.", element.Id, element.ActionId);
			return;
		}

		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(ExecutionLimit);
		try
		{
			var result = await action.CreateExecutor().ExecuteAsync(new ActionExecutionContext
			{
				Parameters = parameters.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal),
				CancellationToken = limit.Token,
			}).ConfigureAwait(false);

			if (result.Status == ActionResultStatus.Failed)
			{
				_logger.Warning("Element {Element} → {ActionId} failed: {ErrorCode}.", element.Id, element.ActionId, result.ErrorCode);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private static int Number(IReadOnlyDictionary<string, object?> parameters, string name, int min, int max, int fallback) =>
		parameters.TryGetValue(name, out var value)
		&& double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			? (int)Math.Clamp(Math.Round(number), min, max)
			: fallback;
}
