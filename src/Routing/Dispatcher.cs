using Serilog;

namespace MIDIRouter.Routing;

internal readonly record struct ClaimedMessage(string Device, MidiKinds Kind, int Channel, int Number, int Value);

internal sealed class Dispatcher : IDisposable
{
	private const int MaxQueued = 4096;

	private readonly Func<ClaimedMessage, ValueTask> _deliver;
	private readonly ILogger _logger;
	private readonly Lock _sync = new();
	private readonly Queue<Box> _queue = new();
	private readonly Dictionary<(string Device, int Channel, int Number), Box> _openControllers = [];
	private readonly SemaphoreSlim _signal = new(0);
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _loop;
	private bool _overflowLogged;

	public Dispatcher(Func<ClaimedMessage, ValueTask> deliver, ILogger logger)
	{
		_deliver = deliver;
		_logger = logger;
		_loop = Task.Run(RunAsync);
	}

	//Called from the MIDI callback: a lock held for a few instructions, no allocation for a coalesced value.
	public void Post(ClaimedMessage message)
	{
		var wake = false;
		lock (_sync)
		{
			if (message.Kind == MidiKinds.ControlChange)
			{
				var key = (message.Device, message.Channel, message.Number);
				if (_openControllers.TryGetValue(key, out var open) && open.Message.Value > 0 == message.Value > 0)
				{
					open.Message = message;
					return;
				}

				if (!TryEnqueue(message, out var box, ref wake))
				{
					return;
				}

				_openControllers[key] = box;
			}
			else
			{
				TryEnqueue(message, out _, ref wake);
			}
		}

		if (wake)
		{
			_signal.Release();
		}
	}

	public void Dispose()
	{
		_stop.Cancel();
		try
		{
			_loop.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
			// The loop ends by cancellation; nothing to report.
		}

		_stop.Dispose();
		_signal.Dispose();
	}

	private bool TryEnqueue(ClaimedMessage message, out Box box, ref bool wake)
	{
		box = new Box { Message = message };
		if (_queue.Count >= MaxQueued)
		{
			// Macro Deck is not keeping up (or not connected). Drop it.
			if (!_overflowLogged)
			{
				_overflowLogged = true;
				_logger.Warning("Dispatch queue full");
			}

			return false;
		}

		wake = _queue.Count == 0;
		_queue.Enqueue(box);
		return true;
	}

	private async Task RunAsync()
	{
		var token = _stop.Token;
		while (!token.IsCancellationRequested)
		{
			try
			{
				await _signal.WaitAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			var failing = false;
			while (TryDequeue(out var message))
			{
				try
				{
					await _deliver(message).ConfigureAwait(false);
					failing = false;
				}
				catch (Exception ex)
				{
					if (!failing)
					{
						failing = true;
						_logger.Debug(ex, "Delivering MIDI messages to subscribers failed.");
					}
				}
			}
		}
	}

	private bool TryDequeue(out ClaimedMessage message)
	{
		lock (_sync)
		{
			if (!_queue.TryDequeue(out var box))
			{
				_overflowLogged = false;
				message = default;
				return false;
			}

			message = box.Message;
			if (message.Kind == MidiKinds.ControlChange)
			{
				var key = (message.Device, message.Channel, message.Number);
				if (_openControllers.TryGetValue(key, out var open) && ReferenceEquals(open, box))
				{
					_openControllers.Remove(key);
				}
			}

			return true;
		}
	}

	private sealed class Box
	{
		public ClaimedMessage Message;
	}
}
