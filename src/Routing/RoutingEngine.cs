using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace MIDIRouter.Routing;

// he-he; lot of copy-paste from DryWetMidi's examples, use with care.
internal sealed class InputPort(string name)
{
	public string Name { get; } = name;

	public HeldState Held { get; } = new();

	public bool[] ShortcutsDown { get; } = new bool[SubscriptionTable.Numbers * 2];

	// Mode switched on this keyboard alone, packed as epoch * 2 + mode; -1 follows the global mode.
	private long _mode = -1;

	public RouterMode? OwnMode(long epoch)
	{
		var packed = Volatile.Read(ref _mode);
		return packed >= 0 && packed >> 1 == epoch ? (RouterMode)(packed & 1) : null;
	}

	public void SetOwnMode(RouterMode mode, long epoch) => Volatile.Write(ref _mode, (epoch << 1) | (long)mode);
}

internal sealed class RoutingEngine(ModeController modes, Action<MidiEvent> forward, Action<ClaimedMessage> claim)
{
	private const int AllSoundOff = 120;
	private const int AllNotesOff = 123;

	private const int ShortcutThreshold = 64;

	private volatile SubscriptionTable _table = SubscriptionTable.Empty;

	private LearnRequest? _learn;

	public SubscriptionTable Table
	{
		get => _table;
		set => _table = value;
	}

	public IDisposable Learn(string device, Action<MidiKinds, int> learned)
	{
		var request = new LearnRequest(device, learned);
		Volatile.Write(ref _learn, request);
		return new LearnCancellation(this, request);
	}

	public void Process(InputPort input, MidiEvent midiEvent)
	{
		if (Volatile.Read(ref _learn) is { } learn && TryLearn(learn, input, midiEvent))
		{
			return;
		}

		switch (midiEvent)
		{
			case NoteOnEvent noteOn when noteOn.Velocity > 0:
				OnNoteOn(input, noteOn);
				return;
			case NoteOnEvent noteOn:
				OnNoteOff(input, noteOn, noteOn.Channel, noteOn.NoteNumber, noteOn.Velocity);
				return;
			case NoteOffEvent noteOff:
				OnNoteOff(input, noteOff, noteOff.Channel, noteOff.NoteNumber, noteOff.Velocity);
				return;
			case ControlChangeEvent controlChange:
				OnControlChange(input, controlChange);
				return;
			default:
				forward(midiEvent);
				return;
		}
	}

	public void Release(InputPort input)
	{
		input.Held.Clear(
			(channel, note) => forward(new NoteOffEvent((SevenBitNumber)note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel }),
			(channel, controller) => forward(new ControlChangeEvent((SevenBitNumber)controller, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel }));

		var table = _table;
		for (var slot = 0; slot < input.ShortcutsDown.Length; slot++)
		{
			if (input.ShortcutsDown[slot])
			{
				Shortcut(table, input, slot, ShortcutAt(table, input, slot), down: false);
			}
		}
	}

	private void OnNoteOn(InputPort input, NoteOnEvent noteOn)
	{
		int channel = noteOn.Channel;
		int note = noteOn.NoteNumber;
		var table = _table;
		if (table.NoteShortcut(input.Name, note) is var shortcut and not ShortcutAction.None)
		{
			Shortcut(table, input, note, shortcut, down: true);
			return;
		}

		var decided = Decide(table, input, table.ClaimsNote(input.Name, note));
		var route = input.Held.NoteOn(channel, note, decided);
		Send(input, noteOn, route, MidiKinds.NoteOn, channel, note, noteOn.Velocity);
	}

	private void OnNoteOff(InputPort input, MidiEvent midiEvent, int channel, int note, int velocity)
	{
		var route = input.Held.NoteOff(channel, note);
		if (!route.IsHeld)
		{
			var table = _table;
			if (table.NoteShortcut(input.Name, note) is var shortcut and not ShortcutAction.None)
			{
				Shortcut(table, input, note, shortcut, down: false);
				return;
			}

			route = Decide(table, input, table.ClaimsNote(input.Name, note));
		}

		Send(input, midiEvent, route, MidiKinds.NoteOff, channel, note, velocity);
	}

	private void OnControlChange(InputPort input, ControlChangeEvent controlChange)
	{
		int channel = controlChange.Channel;
		int controller = controlChange.ControlNumber;
		int value = controlChange.ControlValue;
		var table = _table;

		Route route;
		if (HeldState.IsPedal(controller))
		{
			route = input.Held.Pedal(channel, controller);
			if (!route.IsHeld)
			{
				if (table.ControlShortcut(input.Name, controller) is var shortcut and not ShortcutAction.None)
				{
					Shortcut(table, input, SubscriptionTable.Numbers + controller, shortcut, value >= ShortcutThreshold);
					return;
				}

				route = Decide(table, input, table.ClaimsControlChange(input.Name, controller));
			}

			if (value > 0)
			{
				input.Held.PressPedal(channel, controller, route);
			}
			else
			{
				input.Held.ReleasePedal(channel, controller);
			}
		}
		else
		{
			if (table.ControlShortcut(input.Name, controller) is var shortcut and not ShortcutAction.None)
			{
				Shortcut(table, input, SubscriptionTable.Numbers + controller, shortcut, value >= ShortcutThreshold);
				return;
			}

			route = Decide(table, input, table.ClaimsControlChange(input.Name, controller));
			if (controller is AllSoundOff or AllNotesOff)
			{
				input.Held.ReleaseChannelNotes(channel);
			}
		}

		Send(input, controlChange, route, MidiKinds.ControlChange, channel, controller, value);
	}

	private bool TryLearn(LearnRequest learn, InputPort input, MidiEvent midiEvent)
	{
		if (!string.Equals(learn.Device, input.Name, StringComparison.Ordinal))
		{
			return false;
		}

		(MidiKinds Kind, int Number) learned;
		switch (midiEvent)
		{
			case NoteOnEvent noteOn when noteOn.Velocity > 0:
				learned = (MidiKinds.Note, noteOn.NoteNumber);
				break;
			case ControlChangeEvent controlChange when controlChange.ControlValue > 0:
				learned = (MidiKinds.ControlChange, controlChange.ControlNumber);
				break;
			default:
				return false;
		}

		if (Interlocked.CompareExchange(ref _learn, null, learn) != learn)
		{
			return false;
		}

		learn.Learned(learned.Kind, learned.Number);
		return true;
	}

	private Route Decide(SubscriptionTable table, InputPort input, bool subscribed) =>
		subscribed && ModeOf(table, input) == RouterMode.Control ? Route.Claimed : Route.Forwarded;

	private RouterMode ModeOf(SubscriptionTable table, InputPort input) =>
		(table.PerKeyboardModes ? input.OwnMode(modes.Epoch) : null) ?? modes.Current;

	private void Shortcut(SubscriptionTable table, InputPort input, int slot, ShortcutAction action, bool down)
	{
		ref var isDown = ref input.ShortcutsDown[slot];
		if (isDown == down)
		{
			return;
		}

		isDown = down;
		RouterMode? target = action switch
		{
			ShortcutAction.Toggle when down => ModeOf(table, input) == RouterMode.Play ? RouterMode.Control : RouterMode.Play,
			ShortcutAction.Hold => down ? RouterMode.Control : RouterMode.Play,
			ShortcutAction.Play when down => RouterMode.Play,
			ShortcutAction.Control when down => RouterMode.Control,
			_ => null,
		};

		if (target is not { } mode)
		{
			return;
		}

		// With shortcuts on several keyboards each one switches only itself, e.g. play on one while controlling with the other.
		if (table.PerKeyboardModes)
		{
			input.SetOwnMode(mode, modes.Epoch);
		}
		else
		{
			modes.Set(mode);
		}
	}

	private static ShortcutAction ShortcutAt(SubscriptionTable table, InputPort input, int slot) =>
		slot < SubscriptionTable.Numbers
			? table.NoteShortcut(input.Name, slot)
			: table.ControlShortcut(input.Name, slot - SubscriptionTable.Numbers);

	private void Send(InputPort input, MidiEvent midiEvent, Route route, MidiKinds kind, int channel, int number, int value)
	{
		if (route.IsForwarded)
		{
			forward(midiEvent);
		}
		else
		{
			claim(new ClaimedMessage(input.Name, kind, channel + 1, number, value));
		}
	}

	private sealed record LearnRequest(string Device, Action<MidiKinds, int> Learned);

	private sealed class LearnCancellation(RoutingEngine engine, LearnRequest request) : IDisposable
	{
		public void Dispose() => Interlocked.CompareExchange(ref engine._learn, null, request);
	}
}
