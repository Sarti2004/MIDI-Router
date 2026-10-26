using System.Text.Json;
using System.Text.Json.Serialization;
using MacroDeck.Sdk.Messaging;
using MIDIRouter.Routing;
using Serilog;

namespace MIDIRouter.Integration;


internal sealed class PluginApi : IDisposable
{
	public const string SubscribeTopic = "midi-router.subscribe";
	public const string UnsubscribeTopic = "midi-router.unsubscribe";
	public const string StateTopic = "midi-router.state";
	public const string SetModeTopic = "midi-router.set-mode";
	public const string StateChangedTopic = "midi-router.state-changed";

	public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(6);
	private static readonly TimeSpan MinLease = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan MaxLease = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly MidiRouter _router;
	private readonly ILogger _logger;
	private readonly Lock _sync = new();
	private readonly Dictionary<string, DateTimeOffset> _leases = new(StringComparer.Ordinal);
	private readonly Timer _sweep;

	public PluginApi(MidiRouter router, ILogger logger)
	{
		_router = router;
		_logger = logger.ForContext<PluginApi>();
		_sweep = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
	}

	public async Task RegisterAsync(IMessageChannel channel, CancellationToken cancellationToken)
	{
		await channel.HandleRequestsAsync<SubscribeRequest, SubscribeResponse>(
			SubscribeTopic,
			(request, message, _) => Task.FromResult(Subscribe(request, message.Sender)),
			Json,
			cancellationToken).ConfigureAwait(false);

		await channel.HandleCommandsAsync<UnsubscribeRequest>(
			UnsubscribeTopic,
			(request, message, _) =>
			{
				Unsubscribe(SubscriberKey(message.Sender, request?.Key));
				return Task.CompletedTask;
			},
			Json,
			cancellationToken).ConfigureAwait(false);

		await channel.HandleRequestsAsync<JsonElement?, RouterState>(
			StateTopic,
			(_, _, _) => Task.FromResult(RouterState.From(_router)),
			Json,
			cancellationToken).ConfigureAwait(false);

		await channel.HandleRequestsAsync<SetModeRequest, SetModeResponse>(
			SetModeTopic,
			(request, _, _) => Task.FromResult(SetMode(request)),
			Json,
			cancellationToken).ConfigureAwait(false);
	}

	public void Dispose() => _sweep.Dispose();

	internal SubscribeResponse Subscribe(SubscribeRequest? request, string sender)
	{
		if (request is null)
		{
			return SubscribeResponse.Failure("request body is missing", _router);
		}

		var key = SubscriberKey(sender, request.Key);
		var subscriptions = request.Subscriptions ?? [];
		if (subscriptions.Count == 0)
		{
			Unsubscribe(key);
			return new SubscribeResponse(0, 0, null, RouterState.From(_router));
		}

		if (string.IsNullOrEmpty(request.Topic) || !MessageTopic.IsValidTopic(request.Topic))
		{
			return SubscribeResponse.Failure("'topic' must be valid message topic", _router);
		}

		var rules = new List<SubscriptionRule>();
		foreach (var subscription in subscriptions)
		{
			if (ToRule(subscription) is not { } rule)
			{
				return SubscribeResponse.Failure(
					"Each subscription needs a 'type' of note and number",
					_router);
			}

			rules.Add(rule);
		}

		var lease = TimeSpan.FromSeconds(Math.Clamp(request.LeaseSeconds ?? DefaultLease.TotalSeconds, MinLease.TotalSeconds, MaxLease.TotalSeconds));
		bool isNew;
		lock (_sync)
		{
			isNew = !_leases.ContainsKey(key);
			_leases[key] = DateTimeOffset.UtcNow + lease;
		}

		_router.SetSubscriber(new Subscriber(key, request.Topic, rules));
		if (isNew)
		{
			_logger.Information("{Sender} subscribed to {Count} MIDI rule(s) for {Topic}.", sender, rules.Count, request.Topic);
		}

		return new SubscribeResponse(rules.Count, (int)lease.TotalSeconds, null, RouterState.From(_router));
	}

	internal SetModeResponse SetMode(SetModeRequest? request)
	{
		var outcome = _router.Modes.Request(request?.Mode);
		return new SetModeResponse(JsonNamingPolicy.KebabCaseLower.ConvertName(outcome.ToString()), RouterState.From(_router));
	}

	private static SubscriptionRule? ToRule(SubscriptionDto subscription)
	{
		var kinds = subscription.Type?.ToLowerInvariant() switch
		{
			"note" => MidiKinds.Note,
			"note-on" => MidiKinds.NoteOn,
			"note-off" => MidiKinds.NoteOff,
			"cc" or "control-change" => MidiKinds.ControlChange,
			_ => MidiKinds.None,
		};

		if (kinds == MidiKinds.None)
		{
			return null;
		}

		var numbers = new HashSet<int>();
		if (subscription.Number is { } single)
		{
			numbers.Add(single);
		}

		foreach (var number in subscription.Numbers ?? [])
		{
			numbers.Add(number);
		}

		if (subscription.From is { } from || subscription.To is not null)
		{
			var start = subscription.From ?? 0;
			var end = subscription.To ?? 127;
			if (start > end)
			{
				return null;
			}

			for (var number = start; number <= end; number++)
			{
				numbers.Add(number);
			}
		}

		if (numbers.Any(number => number is < 0 or > 127))
		{
			return null;
		}

		var device = string.IsNullOrEmpty(subscription.Device)
			? SubscriptionRule.AnyName
			: name => string.Equals(name, subscription.Device, StringComparison.Ordinal);

		return new SubscriptionRule(
			device,
			kinds,
			numbers.Count == 0 ? SubscriptionRule.AnyNumber : numbers.Contains);
	}

	private static string SubscriberKey(string sender, string? key) =>
		$"plugin:{sender}:{(string.IsNullOrEmpty(key) ? "default" : key)}";

	private void Unsubscribe(string key)
	{
		lock (_sync)
		{
			_leases.Remove(key);
		}

		_router.RemoveSubscribers(subscriber => string.Equals(subscriber.Key, key, StringComparison.Ordinal));
	}

	private void Sweep()
	{
		List<string> expired;
		lock (_sync)
		{
			var now = DateTimeOffset.UtcNow;
			expired = [.. _leases.Where(lease => lease.Value < now).Select(lease => lease.Key)];
			foreach (var key in expired)
			{
				_leases.Remove(key);
			}
		}

		if (expired.Count == 0)
		{
			return;
		}

		var set = expired.ToHashSet(StringComparer.Ordinal);
		_router.RemoveSubscribers(subscriber => set.Contains(subscriber.Key));
		_logger.Information("{Count} plugin MIDI subscription(s) expired without renewal.", expired.Count);
	}
}

internal sealed record SubscriptionDto(
	string? Type,
	string? Device = null,
	int? Number = null,
	IReadOnlyList<int>? Numbers = null,
	int? From = null,
	int? To = null);

internal sealed record SubscribeRequest(
	string? Topic,
	IReadOnlyList<SubscriptionDto>? Subscriptions,
	string? Key = null,
	int? LeaseSeconds = null);

internal sealed record SubscribeResponse(int Rules, int LeaseSeconds, string? Error, RouterState State)
{
	public static SubscribeResponse Failure(string error, MidiRouter router) => new(0, 0, error, RouterState.From(router));
}

internal sealed record UnsubscribeRequest(string? Key = null);

internal sealed record SetModeRequest(string? Mode);

internal sealed record SetModeResponse(string Outcome, RouterState State);

internal sealed record RouterState(string Mode, bool Enabled, string Status, IReadOnlyList<string> Devices)
{
	public static RouterState From(MidiRouter router)
	{
		var status = router.Status;
		return new RouterState(router.Modes.CurrentName, status.Enabled, status.Text, router.Config.InputDevices);
	}
}

internal sealed record DeliveredMessage(string Device, string Type, int Channel, int Number, int Value);
