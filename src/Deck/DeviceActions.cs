using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;

namespace MIDIRouter.Deck;

internal sealed class DeviceActions
{
	public const string SendNote = "send-note";
	public const string SendControlChange = "send-cc";
	public const string SendProgramChange = "send-program-change";

	public const string ChannelParameter = "channel";
	public const string NoteParameter = "note";
	public const string ControllerParameter = "controller";
	public const string ValueParameter = "value";
	public const string ProgramParameter = "program";
	public const string NoteTypeParameter = "type";
	public const string VelocityParameter = "velocity";
	public const string DurationParameter = "duration";

	private const string NoteOn = "on";
	private const string NoteOff = "off";

	public const string SliderPrefix = "slider-";

	public const int Center = 64;
	private const int Detent = 2;
	private const long TouchHoldMs = 400;

	private readonly DeviceStore _store;
	private readonly DevicePort _port;
	private readonly ConcurrentDictionary<string, int> _sliderValues = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, long> _touched = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> _leds = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> _held = new(StringComparer.Ordinal);

	public DeviceActions(IEnumerable<IActionDefinition> actions, DeviceStore store, DevicePort port)
	{
		_store = store;
		_port = port;
		All = [.. actions.Where(action => action.Id is SendNote or SendControlChange or SendProgramChange)];
		ById = All.ToDictionary(action => action.Id, StringComparer.Ordinal);
		_port.Received += OnReceived;
		_port.StateChanged += OnPowerChanged;
	}

	public event Action<string, int>? SliderMoved;

	public event Action<string, bool>? LedChanged;

	public bool IsOn => _port.State == DevicePortState.On;

	public IReadOnlyList<IActionDefinition> All { get; }

	public IReadOnlyDictionary<string, IActionDefinition> ById { get; }

	public IReadOnlyList<IActionDefinition> For(DeviceElementKind kind) =>
		kind is DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider
			? [.. All.Where(action => action.Id == SendControlChange)]
			: All;

	public static bool IsEdited(DeviceElement element, ActionParameter parameter) =>
		!(element.IsSlider && parameter.Name == ValueParameter) && !(element.ActionId == SendNote && parameter.Name == DurationParameter);

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

	public void Press(DeviceElement element)
	{
		if (!IsOn || element.IsSlider || Binding(element) is not { } binding)
		{
			return;
		}

		switch (binding.Kind)
		{
			case MidiMessageKind.Note when NoteType(element) == NoteOff:
				_port.SendNoteOff(binding.Channel, binding.Number);
				return;
			case MidiMessageKind.Note:
				_port.SendNoteOn(binding.Channel, binding.Number, Number(element.Parameters, VelocityParameter, 1, 127, 100));
				break;
			case MidiMessageKind.ControlChange:
				_port.SendControlChange(binding.Channel, binding.Number, Number(element.Parameters, ValueParameter, 0, 127, 127));
				break;
			default:
				_port.SendProgramChange(binding.Channel, binding.Number);
				return;
		}

		_held[element.Id] = true;
	}

	public void Release(DeviceElement element)
	{
		if (!_held.TryRemove(element.Id, out _) || !IsOn || Binding(element) is not { } binding)
		{
			return;
		}

		if (binding.Kind == MidiMessageKind.ControlChange)
		{
			_port.SendControlChange(binding.Channel, binding.Number, 0);
		}
		else if (NoteType(element) != NoteOn)
		{
			_port.SendNoteOff(binding.Channel, binding.Number);
		}
	}

	public Task SlideAsync(DeviceElement element, int value, CancellationToken cancellationToken)
	{
		if (!IsOn)
		{
			return Task.CompletedTask;
		}

		value = Snap(element, value);
		var variableId = SliderVariableId(element);
		_touched[variableId] = Environment.TickCount64;
		if (_sliderValues.TryGetValue(variableId, out var previous) && previous == value)
		{
			return Task.CompletedTask;
		}

		_sliderValues[variableId] = value;
		SliderMoved?.Invoke(variableId, value);
		if (Binding(element) is { Kind: MidiMessageKind.ControlChange } binding)
		{
			_port.SendControlChange(binding.Channel, binding.Number, value);
		}

		return Task.CompletedTask;
	}

	public int SliderValue(DeviceElement element) =>
		_sliderValues.TryGetValue(SliderVariableId(element), out var value) ? value : element.Centered ? Center : 0;

	public static int Snap(DeviceElement element, int value)
	{
		value = Math.Clamp(value, 0, 127);
		return element.Centered && Math.Abs(value - Center) <= Detent ? Center : value;
	}

	public bool Led(DeviceElement element) => _leds.GetValueOrDefault(element.Id);

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

	public async ValueTask<VariableWriteResult> WriteAsync(VirtualDevice device, string localId, object? value, CancellationToken cancellationToken)
	{
		if (device.Elements.FirstOrDefault(element => element.IsSlider && SliderVariableId(element) == localId) is not { } slider)
		{
			return VariableWriteResult.NotFound();
		}

		if (!IsOn
			|| !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			|| double.IsNaN(number))
		{
			return VariableWriteResult.Unavailable();
		}

		await SlideAsync(slider, (int)Math.Round(number), cancellationToken).ConfigureAwait(false);
		return VariableWriteResult.Applied();
	}

	private void OnReceived(DeviceMessage message)
	{
		foreach (var element in _store.Device.Elements)
		{
			if (Binding(element) is not { } binding || binding.Channel != message.Channel)
			{
				continue;
			}

			if (message.Kind == MidiMessageKind.ProgramChange)
			{
				if (binding.Kind == MidiMessageKind.ProgramChange && !element.IsSlider)
				{
					SetLed(element.Id, binding.Number == message.Number);
				}
			}
			else if (binding.Kind == message.Kind && binding.Number == message.Number)
			{
				if (element.IsSlider)
				{
					Follow(element, message.Value);
				}
				else
				{
					SetLed(element.Id, message.Value > 0);
				}
			}
		}
	}

	private void Follow(DeviceElement slider, int value)
	{
		var variableId = SliderVariableId(slider);
		if ((_touched.TryGetValue(variableId, out var touched) && Environment.TickCount64 - touched < TouchHoldMs)
			|| (_sliderValues.TryGetValue(variableId, out var previous) && previous == value))
		{
			return;
		}

		_sliderValues[variableId] = value;
		SliderMoved?.Invoke(variableId, value);
	}

	private void SetLed(string elementId, bool lit)
	{
		if (_leds.GetValueOrDefault(elementId) == lit)
		{
			return;
		}

		_leds[elementId] = lit;
		LedChanged?.Invoke(elementId, lit);
	}

	private void OnPowerChanged()
	{
		if (_port.State == DevicePortState.On)
		{
			return;
		}

		_held.Clear();
		foreach (var (elementId, lit) in _leds)
		{
			if (lit)
			{
				SetLed(elementId, false);
			}
		}
	}

	private static string? NoteType(DeviceElement element) =>
		Convert.ToString(element.Parameters.GetValueOrDefault(NoteTypeParameter), CultureInfo.InvariantCulture);

	private static int Number(IReadOnlyDictionary<string, object?> parameters, string name, int min, int max, int fallback) =>
		parameters.TryGetValue(name, out var value)
		&& double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			? (int)Math.Clamp(Math.Round(number), min, max)
			: fallback;
}
