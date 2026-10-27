using System.Diagnostics;
using Serilog;

namespace MIDIRouter.Routing;

// One beep for Play, two for Control.
internal static class ModeSound
{
	private const string MacSound = "/System/Library/Sounds/Tink.aiff";

	public static void Play(RouterMode mode, ILogger logger)
	{
		var count = mode == RouterMode.Play ? 1 : 2;

		// Off the Changed thread, so the deck's mode state is not held up by the beeps.
		_ = Task.Run(() =>
		{
			try
			{
				for (var i = 0; i < count; i++)
				{
					Beep();
				}
			}
			catch (Exception ex)
			{
				logger.Debug(ex, "Mode switch sound failed");
			}
		});
	}

	private static void Beep()
	{
		if (OperatingSystem.IsWindows())
		{
			Console.Beep(880, 100);
			Thread.Sleep(60);
		}
		else if (OperatingSystem.IsMacOS())
		{
			using var player = Process.Start("afplay", MacSound);
			player?.WaitForExit();
		}
	}
}
