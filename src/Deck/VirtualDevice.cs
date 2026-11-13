using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MIDIRouter.Deck;

internal enum DeviceElementKind
{
	Button,

	SmallButton,
	VerticalSlider,
	HorizontalSlider,
}

internal sealed record DeviceElement(
	string Id,
	DeviceElementKind Kind,
	string Label,
	int X,
	int Y,
	int Width,
	int Height,
	int Slot,
	string? Color,
	string ActionId,
	IReadOnlyDictionary<string, object?> Parameters)
{
	[JsonIgnore]
	public bool IsSlider => Kind is DeviceElementKind.VerticalSlider or DeviceElementKind.HorizontalSlider;

	public bool Centered { get; init; }

	public bool Covers(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

	public bool Overlaps(DeviceElement other) =>
		other.X < X + Width && X < other.X + other.Width && other.Y < Y + Height && Y < other.Y + other.Height
		// Two small buttons share a cell when they take different slots.
		&& !(Kind == DeviceElementKind.SmallButton && other.Kind == DeviceElementKind.SmallButton && Slot != other.Slot);
}

internal enum MidiMessageKind
{
	Note,
	ControlChange,
	ProgramChange,
}

internal readonly record struct MidiBinding(MidiMessageKind Kind, int Channel, int Number)
{
	public override string ToString() => Kind switch
	{
		MidiMessageKind.Note => string.Create(CultureInfo.InvariantCulture, $"note {Number}, channel {Channel}"),
		MidiMessageKind.ControlChange => string.Create(CultureInfo.InvariantCulture, $"CC {Number}, channel {Channel}"),
		_ => string.Create(CultureInfo.InvariantCulture, $"program {Number}, channel {Channel}"),
	};
}

internal sealed record VirtualDevice(string Name, int Columns, int Rows, IReadOnlyList<DeviceElement> Elements)
{
	public const int MaxGridSize = 8;

	public const string Id = "virtual-device";

	public static readonly VirtualDevice Default = new("Virtual Device", 6, 4, []);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
	};

	public string? PlacementError(DeviceElement element)
	{
		if (element.X < 0 || element.Y < 0 || element.X + element.Width > Columns || element.Y + element.Height > Rows)
		{
			return string.Create(CultureInfo.InvariantCulture, $"\"{element.Label}\" does not fit the {Columns} × {Rows} grid.");
		}

		return Elements.FirstOrDefault(other => other.Id != element.Id && element.Overlaps(other)) is { } blocking
			? $"\"{element.Label}\" overlaps \"{blocking.Label}\"."
			: null;
	}

	public IReadOnlyList<(DeviceElement Element, DeviceElement Twin, MidiBinding Binding)> DuplicateBindings()
	{
		var seen = new Dictionary<MidiBinding, DeviceElement>();
		var duplicates = new List<(DeviceElement, DeviceElement, MidiBinding)>();
		foreach (var element in Elements)
		{
			if (DeviceActions.Binding(element) is not { } binding)
			{
				continue;
			}

			if (seen.TryGetValue(binding, out var twin))
			{
				duplicates.Add((element, twin, binding));
			}
			else
			{
				seen[binding] = element;
			}
		}

		return duplicates;
	}

	public VirtualDevice With(DeviceElement element) => this with
	{
		Elements = Elements.Any(other => other.Id == element.Id)
			? [.. Elements.Select(other => other.Id == element.Id ? element : other)]
			: [.. Elements, element],
	};

	public VirtualDevice Without(string elementId) => this with { Elements = [.. Elements.Where(other => other.Id != elementId)] };

	public string ToJson() => JsonSerializer.Serialize(new DeviceDocument(DeviceDocument.CurrentVersion, this), Json);

	public static VirtualDevice? FromJson(string json)
	{
		DeviceDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<DeviceDocument>(json, Json);
		}
		catch (JsonException)
		{
			return null;
		}

		if (document?.Device is not { } stored)
		{
			return null;
		}

		var device = new VirtualDevice(
			string.IsNullOrWhiteSpace(stored.Name) ? Default.Name : stored.Name,
			Math.Clamp(stored.Columns, 1, MaxGridSize),
			Math.Clamp(stored.Rows, 1, MaxGridSize),
			[]);
		foreach (var element in stored.Elements ?? [])
		{
			if (element is null || string.IsNullOrWhiteSpace(element.Id) || string.IsNullOrWhiteSpace(element.ActionId))
			{
				continue;
			}

			var normalized = element with
			{
				Label = element.Label ?? string.Empty,
				Slot = Math.Clamp(element.Slot, 0, 1),
				Parameters = (element.Parameters ?? new Dictionary<string, object?>())
					.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value), StringComparer.Ordinal),
			};
			if (device.PlacementError(normalized) is null && device.Elements.All(other => other.Id != normalized.Id))
			{
				device = device.With(normalized);
			}
		}

		return device;
	}

	private static object? Normalize(object? value) => value switch
	{
		JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
		JsonElement { ValueKind: JsonValueKind.True } => true,
		JsonElement { ValueKind: JsonValueKind.False } => false,
		JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
		JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
		JsonElement element => element.GetRawText(),
		_ => value,
	};

	private sealed record DeviceDocument(int Version, VirtualDevice? Device)
	{
		public const int CurrentVersion = 1;
	}
}
