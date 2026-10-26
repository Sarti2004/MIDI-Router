using System.Text.Json;

namespace MIDIRouter.Routing;

internal sealed record RouterConfig(
	IReadOnlyList<string> InputDevices,
	string OutputDevice,
	string VirtualPortName,
	IReadOnlyList<ModeShortcut> Shortcuts)
{
	public const string VirtualOutput = "virtual";
	public const string DefaultVirtualPortName = "MIDI Router";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static class Keys
	{
		public const string EntryTitle = "MIDI Router";
		public const string InputDevices = "InputDevices";
		public const string OutputDevice = "OutputDevice";
		public const string VirtualPortName = "VirtualPortName";
		public const string Shortcuts = "Shortcuts";

		public static readonly IReadOnlyList<string> All = [InputDevices, OutputDevice, VirtualPortName, Shortcuts];
	}

	public static RouterConfig Default { get; } = new(
		[],
		OperatingSystem.IsMacOS() ? VirtualOutput : string.Empty,
		DefaultVirtualPortName,
		[]);

	public bool UsesVirtualOutput => string.Equals(OutputDevice, VirtualOutput, StringComparison.Ordinal);

	public string OutputName => UsesVirtualOutput ? VirtualPortName : OutputDevice;

	public static RouterConfig FromStrings(Func<string, string?> read)
	{
		var virtualName = read(Keys.VirtualPortName)?.Trim();

		return new RouterConfig(
			ParseList(read(Keys.InputDevices)),
			read(Keys.OutputDevice)?.Trim() is { Length: > 0 } output ? output : Default.OutputDevice,
			string.IsNullOrEmpty(virtualName) ? DefaultVirtualPortName : virtualName,
			ParseShortcuts(read(Keys.Shortcuts)));
	}

	public IReadOnlyDictionary<string, string> ToStrings() => new Dictionary<string, string>
	{
		[Keys.InputDevices] = JsonSerializer.Serialize(InputDevices),
		[Keys.OutputDevice] = OutputDevice,
		[Keys.VirtualPortName] = VirtualPortName,
		[Keys.Shortcuts] = JsonSerializer.Serialize(Shortcuts.Select(ShortcutDto.From), Json),
	};

	public static IReadOnlyList<string> ParseList(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return [];
		}

		if (text.TrimStart().StartsWith('['))
		{
			try
			{
				return [.. (JsonSerializer.Deserialize<string[]>(text) ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)];
			}
			catch (JsonException)
			{
				// Fall through to the line format.
			}
		}

		return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
	}

	public static IReadOnlyList<ModeShortcut> ParseShortcuts(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return [];
		}

		try
		{
			return [.. (JsonSerializer.Deserialize<ShortcutDto[]>(text, Json) ?? []).Select(dto => dto.ToShortcut()).OfType<ModeShortcut>()];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	internal sealed record ShortcutDto(string? Device, string? Type, int Number, string? Action)
	{
		public const string NoteType = "note";
		public const string ControlChangeType = "cc";

		public static ShortcutDto From(ModeShortcut shortcut) => new(
			shortcut.Device,
			shortcut.Kind == MidiKinds.ControlChange ? ControlChangeType : NoteType,
			shortcut.Number,
			shortcut.Action.ToString().ToLowerInvariant());

		public ModeShortcut? ToShortcut()
		{
			var kind = Type switch
			{
				NoteType => MidiKinds.Note,
				ControlChangeType => MidiKinds.ControlChange,
				_ => MidiKinds.None,
			};

			if (string.IsNullOrEmpty(Device)
				|| kind == MidiKinds.None
				|| Number is < 0 or > 127
				|| !Enum.TryParse<ShortcutAction>(Action, ignoreCase: true, out var action)
				|| action == ShortcutAction.None)
			{
				return null;
			}

			return new ModeShortcut(Device, kind, Number, action);
		}
	}
}
