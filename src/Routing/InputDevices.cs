using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace MIDIRouter.Routing;

internal enum InputState
{
	Open,
	OpenPartialMatch,
	Missing,
	Busy,
	IsRouterOutput,
}

internal sealed record InputStatus(string Name, InputState State, string? ActualName, string? Error);

internal sealed partial class InputDevices(Action<InputPort, MidiEvent> onEvent, Action<InputPort> onClosed) : IDisposable
{
	private readonly Dictionary<string, OpenInput> _open = new(StringComparer.Ordinal);

	public int OpenCount => _open.Count;

	public static bool IsRouterOutput(string input, string outputName)
	{
		if (string.IsNullOrEmpty(outputName))
		{
			return false;
		}

		if (string.Equals(input, outputName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var inputBase = LoopbackSide().Replace(input, string.Empty).Trim();
		var outputBase = LoopbackSide().Replace(outputName, string.Empty).Trim();
		return inputBase.Length > 0
			&& inputBase.Length != input.Length
			&& string.Equals(inputBase, outputBase, StringComparison.OrdinalIgnoreCase);
	}

	// loopMIDI ports ("loopMIDI Port", "loopMIDI Port 1", ...) are loopbacks, never keyboards.
	public static bool IsLoopMidiPort(string input) =>
		input.StartsWith("loopMIDI Port", StringComparison.OrdinalIgnoreCase);

	public static (string? Actual, bool Partial) Resolve(string wanted, IReadOnlyList<string> present, Func<string, bool> excluded)
	{
		if (present.FirstOrDefault(name => string.Equals(name, wanted, StringComparison.Ordinal)) is { } exact)
		{
			return excluded(exact) ? (null, false) : (exact, false);
		}

		var candidates = present
			.Where(name => !excluded(name))
			.Where(name =>
				string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
				|| name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
				|| wanted.Contains(name, StringComparison.OrdinalIgnoreCase))
			.OrderBy(name => Math.Abs(name.Length - wanted.Length))
			.ThenBy(name => name, StringComparer.Ordinal);

		return candidates.FirstOrDefault() is { } partial ? (partial, true) : (null, false);
	}

	public static IReadOnlyList<string> GetPresentNames()
	{
		try
		{
			var devices = InputDevice.GetAll();
			try
			{
				return [.. devices.Select(device => device.Name).Distinct(StringComparer.Ordinal)];
			}
			finally
			{
				foreach (var device in devices)
				{
					device.Dispose();
				}
			}
		}
		catch (Exception ex) when (ex is MidiDeviceException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
		{
			return [];
		}
	}

	public void InvalidateAll()
	{
		foreach (var input in _open.Values)
		{
			input.Faulted = true;
		}
	}

	public void Invalidate(string actualName)
	{
		foreach (var input in _open.Values.Where(input => string.Equals(input.ActualName, actualName, StringComparison.Ordinal)))
		{
			input.Faulted = true;
		}
	}

	public IReadOnlyList<InputStatus> Refresh(IReadOnlyList<string> wanted, bool enabled, IReadOnlyList<string> present, string outputName)
	{
		bool Excluded(string name) => IsRouterOutput(name, outputName);

		var statuses = new List<InputStatus>();
		var targets = new Dictionary<string, (string Actual, bool Partial)>(StringComparer.Ordinal);
		var claimedActual = new HashSet<string>(StringComparer.Ordinal);

		foreach (var name in wanted)
		{
			if (Excluded(name))
			{
				statuses.Add(new InputStatus(name, InputState.IsRouterOutput, null, null));
				continue;
			}

			var (actual, partial) = Resolve(name, present, other => Excluded(other) || claimedActual.Contains(other));
			if (actual is null)
			{
				statuses.Add(new InputStatus(name, InputState.Missing, null, null));
				continue;
			}

			claimedActual.Add(actual);
			targets[name] = (actual, partial);
		}

		foreach (var (name, input) in _open.ToList())
		{
			if (!enabled
				|| input.Faulted
				|| !targets.TryGetValue(name, out var target)
				|| !string.Equals(target.Actual, input.ActualName, StringComparison.Ordinal))
			{
				Close(name);
			}
		}

		if (!enabled)
		{
			return statuses;
		}

		foreach (var (name, (actual, partial)) in targets)
		{
			if (!_open.ContainsKey(name) && TryOpen(name, actual, out var error) is false)
			{
				statuses.Add(new InputStatus(name, InputState.Busy, actual, error));
				continue;
			}

			statuses.Add(new InputStatus(name, partial ? InputState.OpenPartialMatch : InputState.Open, actual, null));
		}

		return statuses;
	}

	public void CloseAll()
	{
		foreach (var name in _open.Keys.ToList())
		{
			Close(name);
		}
	}

	public void Dispose() => CloseAll();

	private bool TryOpen(string name, string actual, out string? error)
	{
		InputDevice? device = null;
		try
		{
			device = InputDevice.GetByName(actual);
			// Keep note-ons with velocity 0 as they arrived; the engine treats them as note-offs itself
			// and forwards the original bytes.
			device.SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOn;
			var input = new OpenInput(new InputPort(name), device, actual);
			device.EventReceived += input.OnEventReceived;
			device.ErrorOccurred += input.OnError;
			input.Forward = onEvent;
			device.StartEventsListening();
			_open[name] = input;
			error = null;
			return true;
		}
		catch (Exception ex) when (ex is MidiDeviceException or ArgumentException or InvalidOperationException)
		{
			// Typically held exclusively by another app (Windows drivers are single-client).
			DeviceCloser.Dispose(device);
			error = ex.Message;
			return false;
		}
	}

	private void Close(string name)
	{
		if (!_open.Remove(name, out var input))
		{
			return;
		}

		input.Device.EventReceived -= input.OnEventReceived;
		input.Device.ErrorOccurred -= input.OnError;
		DeviceCloser.Dispose(input.Device, input.Device.StopEventsListening);
		onClosed(input.Port);
	}

	// "Name (A)", "Name [B]", "Name A".
	[GeneratedRegex(@"\s*(\((A|B)\)|\[(A|B)\]|\s(A|B))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex LoopbackSide();

	private sealed class OpenInput(InputPort port, InputDevice device, string actualName)
	{
		public InputPort Port { get; } = port;

		public InputDevice Device { get; } = device;

		public string ActualName { get; } = actualName;

		public Action<InputPort, MidiEvent>? Forward { get; set; }

		public volatile bool Faulted;

		public void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
		{
			try
			{
				Forward?.Invoke(Port, e.Event);
			}
			catch (Exception)
			{
			}
		}

		public void OnError(object? sender, ErrorOccurredEventArgs e) => Faulted = true;
	}
}
