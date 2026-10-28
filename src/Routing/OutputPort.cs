using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using System.Runtime.Versioning;

namespace MIDIRouter.Routing;

internal enum OutputState
{
	Open,
	NotConfigured,
	VirtualUnsupported,
	Missing,
	Failed,
}

internal sealed class OutputPort : IDisposable
{
	private readonly Lock _sendLock = new();
	private OutputDevice? _device;
	private VirtualDevice? _virtual;
	private WindowsLoopback? _loopback;
	private string? _openedAs;
	private volatile bool _faulted;

	public OutputState State { get; private set; } = OutputState.NotConfigured;

	public string? LastError { get; private set; }

	public static bool SupportsVirtual() =>
		OperatingSystem.IsMacOS() || (OperatingSystem.IsWindows() && WindowsLoopback.IsAvailable());

	public void Send(MidiEvent midiEvent)
	{
		lock (_sendLock)
		{
			if (_device is not { } device || _faulted)
			{
				return;
			}

			try
			{
				device.SendEvent(midiEvent);
			}
			catch (Exception ex) when (ex is MidiDeviceException or ObjectDisposedException or InvalidOperationException)
			{
				// The port gone (loopMIDI closed, endpoint removed) :((
				_faulted = true;
				LastError = ex.Message;
			}
		}
	}

	public OutputState Ensure(RouterConfig config, IReadOnlyCollection<string> present)
	{
		var wanted = config.UsesVirtualOutput ? "virtual:" + config.VirtualPortName : config.OutputDevice;
		if (_device is not null && !_faulted && string.Equals(_openedAs, wanted, StringComparison.Ordinal))
		{
			if (StillPresent(config, present))
			{
				return State = OutputState.Open;
			}
		}

		Close();

		if (config.UsesVirtualOutput && OperatingSystem.IsWindows())
		{
			return EnsureLoopback(config.VirtualPortName, present, wanted);
		}

		RemoveLoopback();

		if (config.UsesVirtualOutput)
		{
			if (!OperatingSystem.IsMacOS())
			{
				return State = OutputState.VirtualUnsupported;
			}

			try
			{
				_virtual = VirtualDevice.Create(config.VirtualPortName);
				Opened(_virtual.OutputDevice, wanted);
			}
			catch (Exception ex) when (ex is MidiDeviceException or NotSupportedException or InvalidOperationException)
			{
				return Failed(ex);
			}
		}
		else if (string.IsNullOrEmpty(config.OutputDevice))
		{
			return State = OutputState.NotConfigured;
		}
		else if (!present.Contains(config.OutputDevice))
		{
			return State = OutputState.Missing;
		}
		else
		{
			try
			{
				Opened(OutputDevice.GetByName(config.OutputDevice), wanted);
			}
			catch (Exception ex) when (ex is MidiDeviceException or ArgumentException or InvalidOperationException)
			{
				return Failed(ex);
			}
		}

		LastError = null;
		return State = OutputState.Open;
	}

	public void Dispose()
	{
		Close();
		RemoveLoopback();
	}

	private static bool StillPresent(RouterConfig config, IReadOnlyCollection<string> present)
	{
		if (!config.UsesVirtualOutput)
		{
			return present.Contains(config.OutputDevice);
		}

		return !OperatingSystem.IsWindows() || WindowsLoopback.FindPort(config.VirtualPortName, present) is not null;
	}

	[SupportedOSPlatform("windows")]
	private OutputState EnsureLoopback(string name, IReadOnlyCollection<string> present, string wanted)
	{
		if (_loopback is { } current && (!string.Equals(current.Name, name, StringComparison.Ordinal) || !current.Exists()))
		{
			RemoveLoopback();
		}

		try
		{
			_loopback ??= WindowsLoopback.TryCreate(name);
		}
		catch (InvalidOperationException ex)
		{
			return Failed(ex);
		}

		if (_loopback is null)
		{
			return State = OutputState.VirtualUnsupported;
		}

		// The service adds the MIDI 1.0 port a moment after the loopback; the next poll opens it.
		if (WindowsLoopback.FindPort(name, present) is not { } port)
		{
			return State = OutputState.Missing;
		}

		try
		{
			Opened(OutputDevice.GetByName(port), wanted);
		}
		catch (Exception ex) when (ex is MidiDeviceException or ArgumentException or InvalidOperationException)
		{
			return Failed(ex);
		}

		LastError = null;
		return State = OutputState.Open;
	}

	private void RemoveLoopback()
	{
		if (OperatingSystem.IsWindows() && _loopback is { } loopback)
		{
			_loopback = null;
			loopback.Dispose();
		}
	}

	private void Opened(OutputDevice device, string openedAs)
	{
		device.PrepareForEventsSending();
		lock (_sendLock)
		{
			_device = device;
			_openedAs = openedAs;
			_faulted = false;
		}
	}

	private OutputState Failed(Exception ex)
	{
		Close();
		LastError = ex.Message;
		return State = OutputState.Failed;
	}

	private void Close()
	{
		OutputDevice? device;
		VirtualDevice? virtualDevice;
		lock (_sendLock)
		{
			device = _device;
			virtualDevice = _virtual;
			_device = null;
			_virtual = null;
			_openedAs = null;
		}

		DeviceCloser.Dispose(virtualDevice is null ? device : null);
		DeviceCloser.Dispose(virtualDevice);
	}
}
