namespace MIDIRouter.Routing;

internal readonly record struct Route(int Value)
{
	public static readonly Route None = new(0);
	public static readonly Route Forwarded = new(1);
	public static readonly Route Claimed = new(2);

	public bool IsHeld => Value != 0;

	public bool IsForwarded => Value == 1;
}

internal sealed class HeldState
{
	public const int SustainPedal = 64;
	public const int SostenutoPedal = 66;
	public const int SoftPedal = 67;

	private const int Channels = 16;
	private const int Notes = 128;

	private readonly Route[] _notes = new Route[Channels * Notes];
	private readonly Route[] _pedals = new Route[Channels * 3];

	public static bool IsPedal(int controller) => PedalIndex(controller) >= 0;

	/// Records a note-on. A note that is already held keeps its route
	public Route NoteOn(int channel, int note, Route decided)
	{
		ref var slot = ref _notes[channel * Notes + note];
		if (slot.IsHeld)
		{
			return slot;
		}

		slot = decided;
		return decided;
	}

	public Route NoteOff(int channel, int note)
	{
		ref var slot = ref _notes[channel * Notes + note];
		var route = slot;
		slot = Route.None;
		return route;
	}

	public Route Pedal(int channel, int controller) => _pedals[channel * 3 + PedalIndex(controller)];

	public void PressPedal(int channel, int controller, Route route)
	{
		ref var slot = ref _pedals[channel * 3 + PedalIndex(controller)];
		if (!slot.IsHeld)
		{
			slot = route;
		}
	}

	public void ReleasePedal(int channel, int controller) => _pedals[channel * 3 + PedalIndex(controller)] = Route.None;

	public void ReleaseChannelNotes(int channel)
	{
		for (var note = 0; note < Notes; note++)
		{
			NoteOff(channel, note);
		}
	}

	public void Clear(Action<int, int> forwardedNote, Action<int, int> forwardedPedal)
	{
		for (var channel = 0; channel < Channels; channel++)
		{
			for (var note = 0; note < Notes; note++)
			{
				if (NoteOff(channel, note).IsForwarded)
				{
					forwardedNote(channel, note);
				}
			}

			foreach (var controller in (int[])[SustainPedal, SostenutoPedal, SoftPedal])
			{
				var route = Pedal(channel, controller);
				ReleasePedal(channel, controller);
				if (route.IsForwarded)
				{
					forwardedPedal(channel, controller);
				}
			}
		}
	}

	private static int PedalIndex(int controller) => controller switch
	{
		SustainPedal => 0,
		SostenutoPedal => 1,
		SoftPedal => 2,
		_ => -1,
	};
}
