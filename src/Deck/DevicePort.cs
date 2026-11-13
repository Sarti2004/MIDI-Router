using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MIDIRouter.Routing;
using Serilog;

namespace MIDIRouter.Deck;

internal enum DevicePortState
{
	Off,

	Starting,
	On,
}

internal enum DevicePowerFailure
{
	None,

	Unsupported,

	NameTaken,
	Failed,
}

internal readonly record struct DevicePowerResult(DevicePowerFailure Failure, string? Error = null)
{
	public static DevicePowerResult Ok { get; } = new(DevicePowerFailure.None);
}

internal readonly record struct DeviceMessage(MidiMessageKind Kind, int Channel, int Number, int Value);

internal sealed class DevicePort(ILogger logger) : IDisposable
{
	private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PortPoll = TimeSpan.FromMilliseconds(250);

	private readonly ILogger _logger = logger.ForContext<DevicePort>();
	private readonly SemaphoreSlim _switching = new(1, 1);
	private readonly Lock _sync = new();
	private IPortPair? _ports;
	private CancellationTokenSource? _starting;
	private readonly ConcurrentDictionary<(MidiMessageKind, int, int), bool> _heard = new();
	private volatile DevicePortState _state;
	private bool _disposed;

	public event Action? StateChanged;

	public event Action<DeviceMessage>? Received;

	public event Action<bool>? Activity;

	public DevicePortState State => _state;

	public string? Name { get; private set; }

	public async Task<DevicePowerResult> SwitchOnAsync(string name, IReadOnlyCollection<string> reserved)
	{
		var starting = new CancellationTokenSource();
		await _switching.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_disposed || (_state == DevicePortState.On && Name == name))
			{
				return DevicePowerResult.Ok;
			}

			if (reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
			{
				return new DevicePowerResult(DevicePowerFailure.NameTaken);
			}

			CloseLocked();
			_starting = starting;
			Name = name;
			SetState(DevicePortState.Starting);

			IPortPair? ports = null;
			var result = DevicePowerResult.Ok;
			try
			{
				if (OperatingSystem.IsMacOS())
				{
					ports = MacPorts.Open(name, OnReceived);
				}
				else if (OperatingSystem.IsWindows())
				{
					ports = await WindowsPorts.OpenAsync(name, OnReceived, starting.Token).ConfigureAwait(false);
				}

				if (ports is null)
				{
					result = new DevicePowerResult(DevicePowerFailure.Unsupported);
				}
			}
			catch (OperationCanceledException) when (starting.IsCancellationRequested)
			{
			}
			catch (Exception ex) when (ex is InvalidOperationException or MidiDeviceException or ArgumentException
				or DllNotFoundException or EntryPointNotFoundException)
			{
				_logger.Warning("The virtual device's ports could not be created: {Error}", ex.Message);
				result = new DevicePowerResult(DevicePowerFailure.Failed, ex.Message);
			}

			_starting = null;
			if (ports is null)
			{
				Name = null;
				SetState(DevicePortState.Off);
				return result;
			}

			lock (_sync)
			{
				_ports = ports;
			}

			_heard.Clear();

			_logger.Information("Virtual device on: MIDI ports \"{Name}\".", name);
			SetState(DevicePortState.On);
			return result;
		}
		finally
		{
			_switching.Release();
		}
	}

	public async Task SwitchOffAsync()
	{
		_starting?.Cancel();
		await _switching.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_state == DevicePortState.Off)
			{
				return;
			}

			CloseLocked();
			_logger.Information("Virtual device off: MIDI ports \"{Name}\" removed.", Name);
			Name = null;
			SetState(DevicePortState.Off);
		}
		finally
		{
			_switching.Release();
		}
	}

	public void SendNoteOn(int channel, int note, int velocity) => Send(new DeviceMessage(MidiMessageKind.Note, channel, note, Math.Max(1, velocity)));

	public void SendNoteOff(int channel, int note) => Send(new DeviceMessage(MidiMessageKind.Note, channel, note, 0));

	public void SendControlChange(int channel, int controller, int value) => Send(new DeviceMessage(MidiMessageKind.ControlChange, channel, controller, value));

	public void SendProgramChange(int channel, int program) => Send(new DeviceMessage(MidiMessageKind.ProgramChange, channel, program, 0));

	public void Dispose()
	{
		_starting?.Cancel();
		_switching.Wait();
		try
		{
			_disposed = true;
			CloseLocked();
			_state = DevicePortState.Off;
		}
		finally
		{
			_switching.Release();
		}
	}

	private void Send(DeviceMessage message)
	{
		lock (_sync)
		{
			if (_ports is not { } ports)
			{
				return;
			}

			try
			{
				ports.Send(message with
				{
					Channel = Math.Clamp(message.Channel, 1, 16),
					Number = Math.Clamp(message.Number, 0, 127),
					Value = Math.Clamp(message.Value, 0, 127),
				});
			}
			catch (Exception ex) when (ex is MidiDeviceException or ObjectDisposedException or InvalidOperationException)
			{
				_logger.Debug(ex, "The virtual device could not send.");
				return;
			}
		}

		Activity?.Invoke(false);
	}

	private void OnReceived(DeviceMessage message)
	{
		/* if (_heard.TryAdd((message.Kind, message.Channel, message.Number), true))
		{
			_logger.Information("From the DAW on \"{Name}\": {Kind} {Number} = {Value}, channel {Channel} (first since switch-on).",
				Name, message.Kind, message.Number, message.Value, message.Channel);
		} */

		Activity?.Invoke(true);
		Received?.Invoke(message);
	}

	private void CloseLocked()
	{
		IPortPair? ports;
		lock (_sync)
		{
			ports = _ports;
			_ports = null;
		}

		ports?.Dispose();
	}

	private void SetState(DevicePortState state)
	{
		if (_state == state)
		{
			return;
		}

		_state = state;
		StateChanged?.Invoke();
	}

	private static DeviceMessage? FromShort(ShortMessage message)
	{
		var channel = (message.Status & 0x0F) + 1;
		return (message.Status & 0xF0) switch
		{
			0x80 => new DeviceMessage(MidiMessageKind.Note, channel, message.Data1, 0),
			0x90 => new DeviceMessage(MidiMessageKind.Note, channel, message.Data1, message.Data2),
			0xB0 => new DeviceMessage(MidiMessageKind.ControlChange, channel, message.Data1, message.Data2),
			0xC0 => new DeviceMessage(MidiMessageKind.ProgramChange, channel, message.Data1, 0),
			_ => null,
		};
	}

	private interface IPortPair : IDisposable
	{
		void Send(DeviceMessage message);
	}

	[SupportedOSPlatform("macos")]
	private sealed class MacPorts(CoreMidiPorts ports) : IPortPair
	{
		public static MacPorts Open(string name, Action<DeviceMessage> received) =>
			new(CoreMidiPorts.Create(name, message =>
			{
				if (FromShort(message) is { } device)
				{
					received(device);
				}
			}));

		public void Send(DeviceMessage message)
		{
			var channel = (byte)(message.Channel - 1);
			var number = (byte)message.Number;
			var value = (byte)message.Value;
			switch (message.Kind)
			{
				case MidiMessageKind.Note when value == 0:
					ports.Send([(byte)(0x80 | channel), number, 0]);
					break;
				case MidiMessageKind.Note:
					ports.Send([(byte)(0x90 | channel), number, value]);
					break;
				case MidiMessageKind.ControlChange:
					ports.Send([(byte)(0xB0 | channel), number, value]);
					break;
				default:
					ports.Send([(byte)(0xC0 | channel), number]);
					break;
			}
		}

		public void Dispose() => ports.Dispose();
	}

	[SupportedOSPlatform("windows")]
	private sealed class WindowsPorts : IPortPair
	{
		private readonly WindowsLoopback _loopback;
		private readonly OutputDevice _output;
		private readonly InputDevice _input;
		private readonly Action<DeviceMessage> _received;

		private WindowsPorts(WindowsLoopback loopback, OutputDevice output, InputDevice input, Action<DeviceMessage> received)
		{
			_loopback = loopback;
			_output = output;
			_input = input;
			_received = received;
		}

		// Null without Windows MIDI Services.
		public static async Task<WindowsPorts?> OpenAsync(string name, Action<DeviceMessage> received, CancellationToken cancellationToken)
		{
			if (WindowsLoopback.TryCreate(name, MidiRouter.GetOutputNames(), pairOnly: true) is not { } loopback)
			{
				return null;
			}

			OutputDevice? output = null;
			InputDevice? input = null;
			try
			{
				var waited = Stopwatch.StartNew();
				string? outputName, inputName;
				while ((outputName = loopback.FindPort(MidiRouter.GetOutputNames())) is null
					|| (inputName = WindowsLoopback.FindPort(loopback.PortName, InputDevices.GetPresentNames())) is null)
				{
					if (waited.Elapsed > PortWait)
					{
						throw new InvalidOperationException($"WMS cant create port \"{name}\".");
					}

					await Task.Delay(PortPoll, cancellationToken).ConfigureAwait(false);
				}

				output = OutputDevice.GetByName(outputName);
				output.PrepareForEventsSending();
				input = InputDevice.GetByName(inputName);
				var ports = new WindowsPorts(loopback, output, input, received);
				input.EventReceived += ports.OnEventReceived;
				input.StartEventsListening();
				return ports;
			}
			catch
			{
				DeviceCloser.Dispose(input);
				DeviceCloser.Dispose(output);
				loopback.Dispose();
				throw;
			}
		}

		public void Send(DeviceMessage message)
		{
			var channel = (FourBitNumber)(message.Channel - 1);
			var number = (SevenBitNumber)message.Number;
			var value = (SevenBitNumber)message.Value;
			_output.SendEvent(message.Kind switch
			{
				MidiMessageKind.Note when message.Value == 0 => new NoteOffEvent(number, SevenBitNumber.MinValue) { Channel = channel },
				MidiMessageKind.Note => new NoteOnEvent(number, value) { Channel = channel },
				MidiMessageKind.ControlChange => new ControlChangeEvent(number, value) { Channel = channel },
				_ => new ProgramChangeEvent(number) { Channel = channel },
			});
		}

		public void Dispose()
		{
			_input.EventReceived -= OnEventReceived;
			DeviceCloser.Dispose(_input, _input.StopEventsListening);
			DeviceCloser.Dispose(_output);
			_loopback.Dispose();
		}

		private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
		{
			DeviceMessage? message = e.Event switch
			{
				NoteOnEvent on => new DeviceMessage(MidiMessageKind.Note, on.Channel + 1, on.NoteNumber, on.Velocity),
				NoteOffEvent off => new DeviceMessage(MidiMessageKind.Note, off.Channel + 1, off.NoteNumber, 0),
				ControlChangeEvent cc => new DeviceMessage(MidiMessageKind.ControlChange, cc.Channel + 1, cc.ControlNumber, cc.ControlValue),
				ProgramChangeEvent pc => new DeviceMessage(MidiMessageKind.ProgramChange, pc.Channel + 1, pc.ProgramNumber, 0),
				_ => null,
			};

			if (message is { } received)
			{
				try
				{
					_received(received);
				}
				catch (Exception)
				{
					// The driver's thread must not see failures
				}
			}
		}
	}
}
