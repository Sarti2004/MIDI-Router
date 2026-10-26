namespace MIDIRouter.Routing;

internal sealed class SubscriptionTable
{
	public const int Numbers = 128;

	public static readonly SubscriptionTable Empty = new([], [], perKeyboardModes: false);

	private readonly Dictionary<string, Claims> _claims;
	private readonly CompiledSubscriber[] _subscribers;

	private SubscriptionTable(Dictionary<string, Claims> claims, CompiledSubscriber[] subscribers, bool perKeyboardModes)
	{
		_claims = claims;
		_subscribers = subscribers;
		PerKeyboardModes = perKeyboardModes;
	}

	public bool IsEmpty => _claims.Count == 0;

	// Mode shortcuts on 2+ keyboards: each switches only its own keyboard.
	public bool PerKeyboardModes { get; }

	public static SubscriptionTable Build(
		IReadOnlyCollection<string> devices,
		IEnumerable<Subscriber> subscribers,
		IEnumerable<ModeShortcut> shortcuts)
	{
		var claims = new Dictionary<string, Claims>(StringComparer.Ordinal);
		var compiled = new List<CompiledSubscriber>();

		foreach (var subscriber in subscribers)
		{
			var perDevice = new Dictionary<string, KindMasks>(StringComparer.Ordinal);
			foreach (var rule in subscriber.Rules.Where(rule => rule.Kinds != MidiKinds.None))
			{
				foreach (var device in devices.Where(rule.Device))
				{
					if (!perDevice.TryGetValue(device, out var masks))
					{
						masks = new KindMasks();
						perDevice[device] = masks;
					}

					for (var number = 0; number < Numbers; number++)
					{
						if (rule.Number(number))
						{
							masks.NoteOn[number] |= rule.Kinds.HasFlag(MidiKinds.NoteOn);
							masks.NoteOff[number] |= rule.Kinds.HasFlag(MidiKinds.NoteOff);
							masks.ControlChange[number] |= rule.Kinds.HasFlag(MidiKinds.ControlChange);
						}
					}
				}
			}

			if (perDevice.Count == 0)
			{
				continue;
			}

			compiled.Add(new CompiledSubscriber(subscriber, perDevice));
			foreach (var (device, masks) in perDevice)
			{
				var claim = ClaimsFor(claims, device);
				for (var number = 0; number < Numbers; number++)
				{
					claim.Note[number] |= masks.NoteOn[number] || masks.NoteOff[number];
					claim.ControlChange[number] |= masks.ControlChange[number];
				}
			}
		}

		var shortcutDevices = new HashSet<string>(StringComparer.Ordinal);
		foreach (var shortcut in shortcuts.Where(shortcut => IsValid(shortcut) && devices.Contains(shortcut.Device)))
		{
			shortcutDevices.Add(shortcut.Device);
			var claim = ClaimsFor(claims, shortcut.Device);
			var slots = shortcut.Kind == MidiKinds.ControlChange ? claim.ControlShortcut : claim.NoteShortcut;
			slots[shortcut.Number] = shortcut.Action;
		}

		return new SubscriptionTable(claims, [.. compiled], perKeyboardModes: shortcutDevices.Count > 1);
	}

	public bool ClaimsNote(string device, int note) =>
		_claims.TryGetValue(device, out var claim) && claim.Note[note];

	public bool ClaimsControlChange(string device, int controller) =>
		_claims.TryGetValue(device, out var claim) && claim.ControlChange[controller];

	public ShortcutAction NoteShortcut(string device, int note) =>
		_claims.TryGetValue(device, out var claim) ? claim.NoteShortcut[note] : ShortcutAction.None;

	public ShortcutAction ControlShortcut(string device, int controller) =>
		_claims.TryGetValue(device, out var claim) ? claim.ControlShortcut[controller] : ShortcutAction.None;

	public void CollectRecipients(string device, MidiKinds kind, int number, List<Subscriber> recipients)
	{
		foreach (var subscriber in _subscribers)
		{
			if (subscriber.Devices.TryGetValue(device, out var masks) && masks.For(kind)[number])
			{
				recipients.Add(subscriber.Source);
			}
		}
	}

	private static bool IsValid(ModeShortcut shortcut) =>
		shortcut.Action != ShortcutAction.None
		&& shortcut.Kind is MidiKinds.Note or MidiKinds.ControlChange
		&& shortcut.Number is >= 0 and < Numbers;

	private static Claims ClaimsFor(Dictionary<string, Claims> claims, string device)
	{
		if (!claims.TryGetValue(device, out var claim))
		{
			claim = new Claims();
			claims[device] = claim;
		}

		return claim;
	}

	private sealed class Claims
	{
		public bool[] Note { get; } = new bool[Numbers];

		public bool[] ControlChange { get; } = new bool[Numbers];

		public ShortcutAction[] NoteShortcut { get; } = new ShortcutAction[Numbers];

		public ShortcutAction[] ControlShortcut { get; } = new ShortcutAction[Numbers];
	}

	private sealed class KindMasks
	{
		public bool[] NoteOn { get; } = new bool[Numbers];

		public bool[] NoteOff { get; } = new bool[Numbers];

		public bool[] ControlChange { get; } = new bool[Numbers];

		public bool[] For(MidiKinds kind) => kind switch
		{
			MidiKinds.NoteOn => NoteOn,
			MidiKinds.NoteOff => NoteOff,
			_ => ControlChange,
		};
	}

	private sealed record CompiledSubscriber(Subscriber Source, Dictionary<string, KindMasks> Devices);
}
