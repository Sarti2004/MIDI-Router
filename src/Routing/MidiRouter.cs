using Melanchall.DryWetMidi.Multimedia;
using Serilog;

namespace MIDIRouter.Routing;

internal interface IClaimSink
{
	ValueTask DeliverAsync(ClaimedMessage message, IReadOnlyList<Subscriber> recipients);
}

internal sealed class MidiRouter : IDisposable
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan HotplugDelay = TimeSpan.FromMilliseconds(250);

	private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(10);

	private readonly ILogger _logger;
	private readonly OutputPort _output = new();
	private readonly Dispatcher _dispatcher;
	private readonly RoutingEngine _engine;
	private readonly InputDevices _inputs;
	private readonly Timer _pollTimer;
	private readonly Lock _pollLock = new();
	private readonly Lock _stateLock = new();
	private readonly Dictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);

	private RouterConfig _config = RouterConfig.Default;
	private RouterStatus _status = RouterStatus.Starting;
	private bool _enabled = true;
	private bool _started;
	private bool _modeStarted;
	private bool _watching;
	private bool _disposed;
	private long _lastPoll;
	private volatile IClaimSink? _sink;

	public MidiRouter(ILogger logger)
	{
		_logger = logger.ForContext<MidiRouter>();
		Modes = new ModeController();
		_dispatcher = new Dispatcher(DeliverAsync, _logger);
		_engine = new RoutingEngine(Modes, _output.Send, _dispatcher.Post);
		_inputs = new InputDevices(_engine.Process, _engine.Release);
		_pollTimer = new Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		Modes.Changed += OnModeChanged;
	}

	public event Action? StatusChanged;

	public ModeController Modes { get; }

	public IClaimSink? Sink
	{
		get => _sink;
		set => _sink = value;
	}

	public RouterConfig Config
	{
		get
		{
			lock (_stateLock)
			{
				return _config;
			}
		}
	}

	public bool Enabled
	{
		get
		{
			lock (_stateLock)
			{
				return _enabled;
			}
		}
	}

	public RouterStatus Status
	{
		get
		{
			lock (_stateLock)
			{
				return _status;
			}
		}
	}

	public SubscriptionTable Table => _engine.Table;

	public static IReadOnlyList<string> GetOutputNames()
	{
		try
		{
			var devices = OutputDevice.GetAll();
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

	public static IReadOnlyList<string> GetSelectableInputs(RouterConfig config) =>
	[
		.. InputDevices.GetPresentNames()
			.Where(name => !InputDevices.IsRouterOutput(name, config.OutputName) && !InputDevices.IsLoopMidiPort(name))
			.Order(StringComparer.OrdinalIgnoreCase),
	];

	public IDisposable Learn(string device, Action<MidiKinds, int> learned) =>
		_engine.Learn(device, (kind, number) => ThreadPool.QueueUserWorkItem(_ => learned(kind, number)));

	public void Apply(RouterConfig config)
	{
		lock (_stateLock)
		{
			// Only the first config sets the mode: later saves re-apply the config and must not switch it.
			if (!_modeStarted)
			{
				_modeStarted = true;
				Modes.Start(config.StartMode);
			}

			_config = config;
			RebuildTableLocked();
		}

		Start();
		Poll();
	}

	public void SetEnabled(bool enabled)
	{
		lock (_stateLock)
		{
			if (_enabled == enabled)
			{
				return;
			}

			_enabled = enabled;
		}

		_logger.Information("MIDI routing turned {State}.", enabled ? "on" : "off");
		Poll();
	}

	public void SetSubscriber(Subscriber subscriber)
	{
		lock (_stateLock)
		{
			if (subscriber.Rules.Count == 0)
			{
				if (!_subscribers.Remove(subscriber.Key))
				{
					return;
				}
			}
			else
			{
				_subscribers[subscriber.Key] = subscriber;
			}

			RebuildTableLocked();
		}
	}

	public void RemoveSubscribers(Func<Subscriber, bool> predicate)
	{
		lock (_stateLock)
		{
			var removed = _subscribers.Values.Where(predicate).Select(subscriber => subscriber.Key).ToList();
			if (removed.Count == 0)
			{
				return;
			}

			foreach (var key in removed)
			{
				_subscribers.Remove(key);
			}

			RebuildTableLocked();
		}
	}

	private void OnModeChanged()
	{
		if (Config.ModeSound is not ModeSoundStyle.Off and var style)
		{
			ModeSound.Play(style, Modes.Current, _logger);
		}
	}

	public void Dispose()
	{
		lock (_pollLock)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			Modes.Changed -= OnModeChanged;
			_pollTimer.Dispose();
			if (_watching)
			{
				DevicesWatcher.Instance.DeviceAdded -= OnDeviceAdded;
				DevicesWatcher.Instance.DeviceRemoved -= OnDeviceRemoved;
			}

			_inputs.Dispose();
			_output.Dispose();
		}

		_dispatcher.Dispose();
	}

	private void Start()
	{
		lock (_pollLock)
		{
			if (_started || _disposed)
			{
				return;
			}

			_started = true;
			try
			{
				DevicesWatcher.Instance.DeviceAdded += OnDeviceAdded;
				DevicesWatcher.Instance.DeviceRemoved += OnDeviceRemoved;
				_watching = true;
			}
			catch (Exception ex) when (ex is NotSupportedException or MidiDeviceException or TypeInitializationException or DllNotFoundException)
			{
				_logger.Debug(ex, "MIDI device watching is not available; relying on polling.");
			}

			_pollTimer.Change(PollInterval, PollInterval);
		}
	}

	private void OnDeviceAdded(object? sender, DeviceAddedRemovedEventArgs e)
	{
		var name = SafeName(e.Device);
		lock (_pollLock)
		{
			if (name is not null && !_disposed)
			{
				_inputs.Invalidate(name);
			}
		}

		PollSoon();
	}

	private void OnDeviceRemoved(object? sender, DeviceAddedRemovedEventArgs e) => PollSoon();

	private void PollSoon()
	{
		try
		{
			_pollTimer.Change(HotplugDelay, PollInterval);
		}
		catch (ObjectDisposedException)
		{
			// Shutting down.
		}
	}

	private void Poll()
	{
		if (!_pollLock.TryEnter())
		{
			return;
		}

		RouterStatus status;
		try
		{
			if (_disposed)
			{
				return;
			}

			var now = Environment.TickCount64;
			if (_lastPoll != 0 && now - _lastPoll > SleepGap.TotalMilliseconds)
			{
				//_logger.Information("Resumed after {Seconds} s; reopening MIDI devices.", (now - _lastPoll) / 1000);
				_inputs.InvalidateAll();
			}

			_lastPoll = now;

			RouterConfig config;
			bool enabled;
			lock (_stateLock)
			{
				config = _config;
				enabled = _enabled;
			}

			var outputState = _output.Ensure(config, config.UsesVirtualOutput ? [] : GetOutputNames());
			var present = enabled && config.InputDevices.Count > 0 ? InputDevices.GetPresentNames() : [];
			var inputs = _inputs.Refresh(config.InputDevices, enabled, present, config.OutputName);
			status = new RouterStatus(enabled, _inputs.OpenCount, outputState, CollectProblems(config, outputState, inputs));
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Refresh MIDI devices failed.");
			return;
		}
		finally
		{
			_pollLock.Exit();
		}

		UpdateStatus(status);
	}

	private List<RouterProblem> CollectProblems(RouterConfig config, OutputState output, IReadOnlyList<InputStatus> inputs)
	{
		var problems = new List<RouterProblem>();
		if (config.InputDevices.Count == 0)
		{
			problems.Add(new RouterProblem(ProblemKind.NotConfigured, string.Empty, null));
		}

		switch (output)
		{
			case OutputState.NotConfigured:
				problems.Add(new RouterProblem(ProblemKind.OutputNotConfigured, string.Empty, null));
				break;
			case OutputState.VirtualUnsupported:
				problems.Add(new RouterProblem(ProblemKind.VirtualOutputUnsupported, string.Empty, null));
				break;
			case OutputState.Missing:
				problems.Add(new RouterProblem(ProblemKind.OutputMissing, config.OutputName, null));
				break;
			case OutputState.Failed:
				problems.Add(new RouterProblem(ProblemKind.OutputFailed, config.OutputName, _output.LastError));
				break;
		}

		foreach (var input in inputs)
		{
			var kind = input.State switch
			{
				InputState.Missing => ProblemKind.InputMissing,
				InputState.Busy => ProblemKind.InputBusy,
				InputState.OpenPartialMatch => ProblemKind.InputPartialMatch,
				InputState.IsRouterOutput => ProblemKind.InputIsRouterOutput,
				_ => (ProblemKind?)null,
			};

			if (kind is { } problem)
			{
				problems.Add(new RouterProblem(problem, input.Name, input.State == InputState.Busy ? input.Error : input.ActualName));
			}
		}

		return problems;
	}

	private void UpdateStatus(RouterStatus status)
	{
		RouterStatus previous;
		lock (_stateLock)
		{
			previous = _status;
			_status = status;
		}

		var before = previous.Problems.Select(problem => problem.Id).ToHashSet(StringComparer.Ordinal);
		var after = status.Problems.Select(problem => problem.Id).ToHashSet(StringComparer.Ordinal);

		foreach (var problem in status.Problems.Where(problem => !before.Contains(problem.Id)))
		{
			if (problem.Severity == ProblemSeverity.Info && problem.Kind != ProblemKind.InputPartialMatch)
			{
				_logger.Information("{Problem}.", problem.Describe());
			}
			else
			{
				_logger.Warning("{Problem}.", problem.Describe());
			}
		}

		foreach (var problem in previous.Problems.Where(problem => !after.Contains(problem.Id)))
		{
			//_logger.Information("Resolved: {Problem}.", problem.Describe());
		}

		if (!before.SetEquals(after)
			|| previous.Enabled != status.Enabled
			|| previous.Output != status.Output
			|| previous.OpenInputs != status.OpenInputs)
		{
			StatusChanged?.Invoke();
		}
	}

	private void RebuildTableLocked() =>
		_engine.Table = SubscriptionTable.Build(_config.InputDevices, _subscribers.Values, _config.Shortcuts);

	private async ValueTask DeliverAsync(ClaimedMessage message)
	{
		if (_sink is not { } sink)
		{
			return;
		}

		var table = _engine.Table;
		var recipients = new List<Subscriber>();
		table.CollectRecipients(message.Device, message.Kind, message.Number, recipients);
		if (recipients.Count > 0)
		{
			await sink.DeliverAsync(message, recipients).ConfigureAwait(false);
		}
	}

	private static string? SafeName(MidiDevice device)
	{
		try
		{
			return device switch
			{
				InputDevice input => input.Name,
				_ => null,
			};
		}
		catch (Exception)
		{
			return null;
		}
	}
}
