using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;

namespace MIDIRouter.Deck;

/// <summary>
/// The MIDI Router actions an element can be set up with, and sending them on the device's own port as hardware does:
/// a button sends its message when pressed and releases it when let go, a slider sends its CC with its position as the
/// value. Switched off, nothing is sent. Each slider also has a writable variable holding that position, so other
/// widgets and automations can read it and move the slider. What the DAW sends back to the port lights buttons and
/// moves sliders bound to the same message.
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
	public const string NoteTypeParameter = "type";
	public const string VelocityParameter = "velocity";
	public const string DurationParameter = "duration";

	// Send MIDI note's types: on a device button, a tap is held for as long as the button is.
	private const string NoteOn = "on";
	private const string NoteOff = "off";

	public const string SliderPrefix = "slider-";

	// A centered slider's rest, and how near it a move snaps there, like a pan pot's detent.
	public const int Center = 64;
	private const int Detent = 2;
	// As a touch-sensitive motor fader: a slider being moved ignores the DAW for this long after each move, so the
	// DAW's echo of an older position doesn't pull it back under the finger.
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

	/// <summary>A slider moved, by a touch, a variable or the DAW: its variable id and its value, 0-127.</summary>
	public event Action<string, int>? SliderMoved;

	/// <summary>The DAW turned a button's LED on or off: the element id and whether it is lit.</summary>
	public event Action<string, bool>? LedChanged;

	public bool IsOn => _port.State == DevicePortState.On;

	public IReadOnlyList<IActionDefinition> All { get; }

	public IReadOnlyDictionary<string, IActionDefinition> ById { get; }

	/// <summary>The actions an element of this kind may run: a slider only sends CCs.</summary>
	public IReadOnlyList<IActionDefinition> For(DeviceElementKind kind) =>
		kind is DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider
			? [.. All.Where(action => action.Id == SendControlChange)]
			: All;

	/// <summary>
	/// The parameters the editor shows: a slider's value comes from its position, and a note lasts as long as its button
	/// is held.
	/// </summary>
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

	/// <summary>
	/// A button went down: its note on (or note off, for a note-off button), its CC at the set value, or its program.
	/// </summary>
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

	/// <summary>
	/// A button came up: its note off, or its CC at 0, so the DAW sees a release as from a hardware button. A note-on
	/// button keeps its note sounding until a note-off button ends it.
	/// </summary>
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

	/// <summary>
	/// Sends the slider's CC at <paramref name="value"/> (0-127) and keeps it as the slider's position. Switched off, the
	/// slider stays where it is.
	/// </summary>
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

	/// <summary>The value a move to <paramref name="value"/> sends: within range, and at the center when near it on a centered slider.</summary>
	public static int Snap(DeviceElement element, int value)
	{
		value = Math.Clamp(value, 0, 127);
		return element.Centered && Math.Abs(value - Center) <= Detent ? Center : value;
	}

	/// <summary>Whether the DAW has the button's LED on.</summary>
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

	// Another widget or an automation set the variable: the slider moves and sends its CC.
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

	// The DAW's feedback: a note or CC lights the buttons sending it and moves the sliders sending it; a program change
	// lights the buttons sending that program on its channel and puts out the others there.
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

	// The slider takes the DAW's position without sending it back.
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

	// Switched off, every LED goes out and nothing is held any more; the sliders stay where they are.
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
