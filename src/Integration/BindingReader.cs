using System.Globalization;
using System.Text.Json;
using MacroDeck.Sdk.Events;
using MIDIRouter.Routing;

namespace MIDIRouter.Integration;


internal static class BindingReader
{
	public static Subscriber Read(IReadOnlyList<EventBinding> bindings, Action<string> unsupportedOperator)
	{
		var rules = new List<SubscriptionRule>();
		foreach (var binding in bindings)
		{
			var (kinds, numberParameter) = binding.EventId switch
			{
				RouterEvents.NoteOn => (MidiKinds.NoteOn, RouterEvents.NoteParameter),
				RouterEvents.NoteOff => (MidiKinds.NoteOff, RouterEvents.NoteParameter),
				RouterEvents.ControlChange => (MidiKinds.ControlChange, RouterEvents.ControllerParameter),
				_ => (MidiKinds.None, string.Empty),
			};

			if (kinds == MidiKinds.None)
			{
				continue;
			}

			var device = binding.Parameters.GetValueOrDefault(RouterEvents.DeviceParameter);
			var number = binding.Parameters.GetValueOrDefault(numberParameter);

			if (!IsSupported(device, unsupportedOperator) || !IsSupported(number, unsupportedOperator))
			{
				// itcould silence keys the trigger never fires for; leave them alone.
				continue;
			}

			rules.Add(new SubscriptionRule(
				device is null ? SubscriptionRule.AnyName : name => Matches(device, name),
				kinds,
				number is null ? SubscriptionRule.AnyNumber : value => Matches(number, value.ToString(CultureInfo.InvariantCulture))));
		}

		return new Subscriber(Subscriber.MacroDeckKey, null, rules);
	}

	public static bool Matches(EventBindingValue condition, string candidate)
	{
		switch (condition.Operator)
		{
			case "isEmpty":
				return candidate.Length == 0;
			case "isNotEmpty":
				return candidate.Length > 0;
			case "isAvailable":
				return true;
			case "isNotAvailable":
				return false;
		}

		if (ExpectedText(condition.Value) is not { } expected)
		{
			// A comparison without a value filters nothing.
			return true;
		}

		int comparison;
		if (double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber)
			&& double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var candidateNumber))
		{
			comparison = candidateNumber.CompareTo(expectedNumber);
		}
		else
		{
			comparison = string.CompareOrdinal(candidate, expected);
		}

		return (condition.Operator ?? "==") switch
		{
			"==" => comparison == 0,
			"!=" => comparison != 0,
			">" => comparison > 0,
			">=" => comparison >= 0,
			"<" => comparison < 0,
			"<=" => comparison <= 0,
			_ => false,
		};
	}

	private static bool IsSupported(EventBindingValue? condition, Action<string> unsupportedOperator)
	{
		if (condition is null)
		{
			return true;
		}

		switch (condition.Operator ?? "==")
		{
			case "==" or "!=" or ">" or ">=" or "<" or "<=" or "isEmpty" or "isNotEmpty" or "isAvailable" or "isNotAvailable":
				return true;
			default:
				unsupportedOperator(condition.Operator ?? string.Empty);
				return false;
		}
	}

	private static string? ExpectedText(JsonElement? value) => value switch
	{
		{ ValueKind: JsonValueKind.String } text => text.GetString(),
		{ ValueKind: JsonValueKind.Number } number => number.GetRawText(),
		{ ValueKind: JsonValueKind.True } => "true",
		{ ValueKind: JsonValueKind.False } => "false",
		_ => null,
	};
}
