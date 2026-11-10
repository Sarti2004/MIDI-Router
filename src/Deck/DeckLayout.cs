using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MIDIRouter.Deck;

internal enum DeckControlKind
{
	Button,

	// Half a cell: up to two share one, in slot 0 (top) and slot 1 (bottom).
	SmallButton,
	VerticalSlider,
	HorizontalSlider,
}

internal enum DeckMessage
{
	Note,
	ControlChange,
	ProgramChange,
}

// Channel is 1-16, like everywhere else in the router. Grid cells are Macro Deck cells and Cubase surface units.
internal sealed record DeckControl(
	string Id,
	DeckControlKind Kind,
	string Label,
	int X,
	int Y,
	int Width,
	int Height,
	int Slot,
	int Channel,
	DeckMessage Message,
	int Number,
	string? Color)
{
	public bool IsSlider => Kind is DeckControlKind.VerticalSlider or DeckControlKind.HorizontalSlider;

	public bool Covers(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

internal sealed partial record DeckLayout(string Id, string Name, int Columns, int Rows, int Channel, IReadOnlyList<DeckControl> Controls)
{
	public const int MaxGridSize = 16;
	public const int DefaultChannel = 16;

	private const string ButtonType = "button";
	private const string SmallType = "small";
	private const string VerticalSliderType = "vslider";
	private const string HorizontalSliderType = "hslider";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	private static readonly JsonSerializerOptions WriteJson = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	// The file name without extension as an id, e.g. "My Deck (2).json" is "my-deck-2".
	public static string IdFromFileName(string path) => ToId(Path.GetFileNameWithoutExtension(path), "deck");

	// Macro Deck ids are lowercase and hyphen-separated, starting with a letter; Cubase script folders accept them too.
	public static string ToId(string text, string prefix)
	{
		var id = new StringBuilder(text.Length);
		foreach (var c in text.Normalize(NormalizationForm.FormD))
		{
			// "Ü" is "U" and a combining diaeresis: keep the letter
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			var keep = char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-';
			if (keep != '-' || (id.Length > 0 && id[^1] != '-'))
			{
				id.Append(keep);
			}
		}

		var result = id.ToString().TrimEnd('-');
		return result.Length > 0 && char.IsAsciiDigit(result[0]) ? $"{prefix}-{result}" : result;
	}

	// Ids join with hyphens, so two decks can produce the same one: the store keeps the first deck that claims it.
	public string FaderVariableId(DeckControl control) => $"fader-{Id}-{control.Id}";

	/// <exception cref="DeckLayoutException">The file is not valid JSON or breaks a layout rule.</exception>
	public static DeckLayout Parse(string id, string json)
	{
		DeckFile? file;
		try
		{
			file = JsonSerializer.Deserialize<DeckFile>(json, Json);
		}
		catch (JsonException ex)
		{
			throw new DeckLayoutException(ex.LineNumber is { } line
				? string.Create(CultureInfo.InvariantCulture, $"Line {line + 1}: {Readable(ex)}")
				: Readable(ex));
		}

		if (file is null)
		{
			throw new DeckLayoutException("The file is empty.");
		}

		var columns = Range(file.Columns ?? 0, 1, MaxGridSize, "columns");
		var rows = Range(file.Rows ?? 0, 1, MaxGridSize, "rows");
		var channel = Range(file.Channel ?? DefaultChannel, 1, 16, "channel");
		var name = string.IsNullOrWhiteSpace(file.Name) ? id : file.Name.Trim();
		var controls = (file.Controls ?? []).Select((entry, index) => ParseControl(entry, index, channel)).ToList();

		var deck = new DeckLayout(id, name, columns, rows, channel, controls);
		deck.Validate();
		return deck;
	}

	/// <summary>Checks the rules that span controls: unique ids, grid bounds, overlaps and unique bindings.</summary>
	/// <exception cref="DeckLayoutException">The first rule broken.</exception>
	public void Validate()
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var bindings = new Dictionary<(int Channel, DeckMessage Message, int Number), string>();
		foreach (var control in Controls)
		{
			if (!ids.Add(control.Id))
			{
				throw new DeckLayoutException($"Two controls have the id \"{control.Id}\".");
			}

			if (PlacementError(control) is { } placement)
			{
				throw new DeckLayoutException(placement);
			}

			// Cubase tells controls apart by what they send: two with the same binding would be one control there.
			if (bindings.TryGetValue((control.Channel, control.Message, control.Number), out var twin))
			{
				throw new DeckLayoutException(string.Create(CultureInfo.InvariantCulture,
					$"\"{control.Id}\" sends the same {Describe(control)} on channel {control.Channel} as \"{twin}\"."));
			}

			bindings[(control.Channel, control.Message, control.Number)] = control.Id;
		}
	}

	/// <summary>Why the control can't go where it is, ignoring itself: outside the grid, or on another control.</summary>
	public string? PlacementError(DeckControl control)
	{
		if (control.X < 0 || control.Y < 0 || control.X + control.Width > Columns || control.Y + control.Height > Rows)
		{
			return string.Create(CultureInfo.InvariantCulture, $"\"{control.Label}\" does not fit the {Columns} x {Rows} grid.");
		}

		var other = Controls.FirstOrDefault(other =>
			!string.Equals(other.Id, control.Id, StringComparison.Ordinal)
			&& other.X < control.X + control.Width && control.X < other.X + other.Width
			&& other.Y < control.Y + control.Height && control.Y < other.Y + other.Height
			// Two small buttons share a cell when they take different slots.
			&& !(other.Kind == DeckControlKind.SmallButton && control.Kind == DeckControlKind.SmallButton && other.Slot != control.Slot));
		return other is null ? null : $"\"{control.Label}\" overlaps \"{other.Label}\".";
	}

	public DeckControl? At(int x, int y, int slot = 0) =>
		Controls.FirstOrDefault(control => control.Covers(x, y) && (control.Kind != DeckControlKind.SmallButton || control.Slot == slot));

	public string ToJson() => JsonSerializer.Serialize(
		new DeckFile(
			Name,
			Columns,
			Rows,
			Channel,
			[
				.. Controls.Select(control => new ControlEntry(
					control.Id,
					TypeName(control.Kind),
					control.Label,
					control.X,
					control.Y,
					control.Kind == DeckControlKind.SmallButton ? null : control.Width,
					control.Kind == DeckControlKind.SmallButton ? null : control.Height,
					control.Kind == DeckControlKind.SmallButton ? control.Slot : null,
					control.Message == DeckMessage.Note ? control.Number : null,
					control.Message == DeckMessage.ControlChange ? control.Number : null,
					control.Message == DeckMessage.ProgramChange ? control.Number : null,
					control.Channel == Channel ? null : control.Channel,
					control.Color,
					null)),
			]),
		WriteJson);

	public static string Describe(DeckControl control) => Describe(control.Message, control.Number);

	public static string Describe(DeckMessage message, int number) => message switch
	{
		DeckMessage.Note => string.Create(CultureInfo.InvariantCulture, $"note {number}"),
		DeckMessage.ControlChange => string.Create(CultureInfo.InvariantCulture, $"CC {number}"),
		_ => string.Create(CultureInfo.InvariantCulture, $"program {number}"),
	};

	private static DeckControl ParseControl(ControlEntry entry, int index, int deckChannel)
	{
		var label = entry.Label?.Trim() ?? string.Empty;
		var id = ToId(string.IsNullOrWhiteSpace(entry.Id) ? label : entry.Id, "control");
		if (id.Length == 0)
		{
			id = string.Create(CultureInfo.InvariantCulture, $"control-{index + 1}");
		}

		var width = Range(entry.W ?? 1, 1, MaxGridSize, $"{id}.w");
		var height = Range(entry.H ?? 1, 1, MaxGridSize, $"{id}.h");
		var kind = entry.Type?.Trim().ToLowerInvariant() switch
		{
			ButtonType or "pad" => DeckControlKind.Button,
			SmallType => DeckControlKind.SmallButton,
			VerticalSliderType => DeckControlKind.VerticalSlider,
			HorizontalSliderType => DeckControlKind.HorizontalSlider,
			// Before the slider types: a fader is vertical when it is taller than wide, unless it says otherwise.
			"fader" => entry.Orientation?.Trim().ToLowerInvariant() switch
			{
				null or "" => height > width ? DeckControlKind.VerticalSlider : DeckControlKind.HorizontalSlider,
				"vertical" => DeckControlKind.VerticalSlider,
				"horizontal" => DeckControlKind.HorizontalSlider,
				_ => throw new DeckLayoutException($"\"{id}\": orientation must be \"horizontal\" or \"vertical\"."),
			},
			_ => throw new DeckLayoutException($"\"{id}\": type must be \"button\", \"small\", \"vslider\" or \"hslider\"."),
		};

		if (entry.Orientation is not null && entry.Type?.Trim().ToLowerInvariant() != "fader")
		{
			throw new DeckLayoutException($"\"{id}\": orientation is only for \"fader\"; use \"vslider\" or \"hslider\".");
		}

		var slot = 0;
		if (kind == DeckControlKind.SmallButton)
		{
			if (width != 1 || height != 1)
			{
				throw new DeckLayoutException($"\"{id}\": a small button is half a cell and has no size.");
			}

			slot = Range(entry.Slot ?? 0, 0, 1, $"{id}.slot");
		}
		else if (entry.Slot is not null)
		{
			throw new DeckLayoutException($"\"{id}\": only a small button has a slot.");
		}

		(DeckMessage Message, int Number) binding = (entry.Note, entry.Cc, entry.Program) switch
		{
			({ } note, null, null) => (DeckMessage.Note, Range(note, 0, 127, $"{id}.note")),
			(null, { } cc, null) => (DeckMessage.ControlChange, Range(cc, 0, 127, $"{id}.cc")),
			(null, null, { } program) => (DeckMessage.ProgramChange, Range(program, 0, 127, $"{id}.program")),
			_ => throw new DeckLayoutException($"\"{id}\": set one of \"note\", \"cc\" or \"program\"."),
		};

		if (kind is DeckControlKind.VerticalSlider or DeckControlKind.HorizontalSlider && binding.Message != DeckMessage.ControlChange)
		{
			throw new DeckLayoutException($"\"{id}\": a slider sends a CC.");
		}

		return new DeckControl(
			id,
			kind,
			label.Length > 0 ? label : id,
			Range(entry.X ?? -1, 0, MaxGridSize - 1, $"{id}.x"),
			Range(entry.Y ?? -1, 0, MaxGridSize - 1, $"{id}.y"),
			width,
			height,
			slot,
			Range(entry.Channel ?? deckChannel, 1, 16, $"{id}.channel"),
			binding.Message,
			binding.Number,
			string.IsNullOrWhiteSpace(entry.Color) ? null : entry.Color.Trim());
	}

	private static string TypeName(DeckControlKind kind) => kind switch
	{
		DeckControlKind.SmallButton => SmallType,
		DeckControlKind.VerticalSlider => VerticalSliderType,
		DeckControlKind.HorizontalSlider => HorizontalSliderType,
		_ => ButtonType,
	};

	// System.Text.Json names .NET types and repeats the position; keep what a person editing the file needs.
	private static string Readable(JsonException ex)
	{
		if (UnknownProperty().Match(ex.Message) is { Success: true } unknown)
		{
			return $"unknown setting \"{unknown.Groups[1].Value}\".";
		}

		var message = ex.Message;
		var path = message.IndexOf(" Path:", StringComparison.Ordinal);
		return path > 0 ? message[..path] : message;
	}

	[GeneratedRegex("property '([^']+)' could not be mapped")]
	private static partial Regex UnknownProperty();

	private static int Range(int value, int min, int max, string name) =>
		value >= min && value <= max
			? value
			: throw new DeckLayoutException(string.Create(CultureInfo.InvariantCulture, $"\"{name}\" must be {min}-{max}."));

	private sealed record DeckFile(string? Name, int? Columns, int? Rows, int? Channel, IReadOnlyList<ControlEntry>? Controls);

	private sealed record ControlEntry(
		string? Id,
		string? Type,
		string? Label,
		int? X,
		int? Y,
		int? W,
		int? H,
		int? Slot,
		int? Note,
		int? Cc,
		int? Program,
		int? Channel,
		string? Color,
		string? Orientation);
}

internal sealed class DeckLayoutException(string message) : Exception(message);
