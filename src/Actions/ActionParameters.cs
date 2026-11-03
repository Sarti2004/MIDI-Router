using System.Globalization;

namespace MIDIRouter.Actions;

internal static class ActionParameters
{
	public static int ReadNumber<T>(IReadOnlyDictionary<string, T> parameters, string name, int min, int max, int fallback) =>
		parameters.TryGetValue(name, out var value)
			&& double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			&& double.IsFinite(number)
				? (int)Math.Clamp(Math.Round(number), min, max)
				: fallback;
}
