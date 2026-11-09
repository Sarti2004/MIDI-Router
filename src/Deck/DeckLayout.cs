using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MIDIRouter.Deck;

internal enum DeckControlKind
{
	Pad,
	Fader,
}

internal enum DeckMessage
{
	Note,
	ControlChange,
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
	int Channel,
	DeckMessage Message,
	int Number,
	string? Color,
	bool Vertical);

internal sealed partial record DeckLayout(string Id, string Name, int Columns, int Rows, IReadOnlyList<DeckControl> Controls)
{
	public const int MaxGridSize = 16;
	public const int DefaultChannel = 16;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	// The file name without extension, reduced to what Cubase script folders and Macro Deck variable names accept.
	public static string IdFromFileName(string path)
	{
		var stem = Path.GetFileNameWithoutExtension(path);
		var id = new StringBuilder(stem.Length);
		foreach (var c in stem)
		{
			var keep = char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_';
			if (keep != '_' || (id.Length > 0 && id[^1] != '_'))
			{
				id.Append(keep);
			}
		}

		return id.ToString().TrimEnd('_');
	}

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

		var controls = new List<DeckControl>();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var occupied = new string?[columns, rows];
		foreach (var (entry, index) in (file.Controls ?? []).Select((entry, index) => (entry, index)))
		{
			var control = ParseControl(entry, index, channel);
			if (!ids.Add(control.Id))
			{
				throw new DeckLayoutException($"Two controls have the id \"{control.Id}\".");
			}

			if (control.X + control.Width > columns || control.Y + control.Height > rows)
			{
				throw new DeckLayoutException(string.Create(CultureInfo.InvariantCulture,
					$"\"{control.Id}\" does not fit the {columns} x {rows} grid."));
			}

			for (var x = control.X; x < control.X + control.Width; x++)
			{
				for (var y = control.Y; y < control.Y + control.Height; y++)
				{
					if (occupied[x, y] is { } other)
					{
						throw new DeckLayoutException($"\"{control.Id}\" overlaps \"{other}\".");
					}

					occupied[x, y] = control.Id;
				}
			}

			controls.Add(control);
		}

		var name = string.IsNullOrWhiteSpace(file.Name) ? id : file.Name.Trim();
		return new DeckLayout(id, name, columns, rows, controls);
	}

	private static DeckControl ParseControl(ControlEntry entry, int index, int deckChannel)
	{
		var label = entry.Label?.Trim() ?? string.Empty;
		var id = string.IsNullOrWhiteSpace(entry.Id) ? IdFromFileName(label) : IdFromFileName(entry.Id);
		if (id.Length == 0)
		{
			id = string.Create(CultureInfo.InvariantCulture, $"control_{index + 1}");
		}

		var kind = entry.Type?.Trim().ToLowerInvariant() switch
		{
			"pad" => DeckControlKind.Pad,
			"fader" => DeckControlKind.Fader,
			_ => throw new DeckLayoutException($"\"{id}\": type must be \"pad\" or \"fader\"."),
		};

		(DeckMessage Message, int Number) binding = (entry.Note, entry.Cc) switch
		{
			({ } note, null) when kind == DeckControlKind.Pad => (DeckMessage.Note, Range(note, 0, 127, $"{id}.note")),
			(null, { } cc) => (DeckMessage.ControlChange, Range(cc, 0, 127, $"{id}.cc")),
			(not null, _) when kind == DeckControlKind.Fader => throw new DeckLayoutException($"\"{id}\": a fader sends a CC, not a note."),
			_ => throw new DeckLayoutException($"\"{id}\": set either \"note\" or \"cc\"."),
		};

		var width = Range(entry.W ?? 1, 1, MaxGridSize, $"{id}.w");
		var height = Range(entry.H ?? 1, 1, MaxGridSize, $"{id}.h");
		var vertical = entry.Orientation?.Trim().ToLowerInvariant() switch
		{
			null or "" => height > width,
			"vertical" => true,
			"horizontal" => false,
			_ => throw new DeckLayoutException($"\"{id}\": orientation must be \"horizontal\" or \"vertical\"."),
		};

		return new DeckControl(
			id,
			kind,
			label.Length > 0 ? label : id,
			Range(entry.X ?? -1, 0, MaxGridSize - 1, $"{id}.x"),
			Range(entry.Y ?? -1, 0, MaxGridSize - 1, $"{id}.y"),
			width,
			height,
			Range(entry.Channel ?? deckChannel, 1, 16, $"{id}.channel"),
			binding.Message,
			binding.Number,
			string.IsNullOrWhiteSpace(entry.Color) ? null : entry.Color.Trim(),
			vertical);
	}

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
		int? Note,
		int? Cc,
		int? Channel,
		string? Color,
		string? Orientation);
}

internal sealed class DeckLayoutException(string message) : Exception(message);
