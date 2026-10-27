using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace MIDIRouter.Routing;

internal enum ModeSoundStyle
{
	Off,
	Beep,
	Announce,
}

internal static class ModeSound
{
	private const string MacSoundPlay = "/System/Library/Sounds/Blow.aiff";
	private const string MacSoundControl = "/System/Library/Sounds/Tink.aiff";

	private const uint SND_ASYNC = 0x0001;
	private const uint SND_NODEFAULT = 0x0002;
	private const uint SND_FILENAME = 0x00020000;

	[DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
	private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

	public static readonly IReadOnlyList<string> Names = ["off", "beep", "announce"];

	public static string Name(ModeSoundStyle style) => Names[(int)style];

	public static ModeSoundStyle? Parse(string? name)
	{
		for (var i = 0; i < Names.Count; i++)
		{
			if (string.Equals(Names[i], name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return (ModeSoundStyle)i;
			}
		}

		return null;
	}

	public static void Play(ModeSoundStyle style, RouterMode mode, ILogger logger)
	{
		// Off the Changed thread, so the deck's mode state is not held up by the beeps.
		_ = Task.Run(() =>
		{
			try
			{
				if (style == ModeSoundStyle.Announce)
				{
					Announce(mode, logger);
				}
				else
				{
					Beep(mode);
				}
			}
			catch (Exception ex)
			{
				logger.Debug(ex, "Mode switch sound failed");
			}
		});
	}

	private static void Beep(RouterMode mode)
	{
		if (OperatingSystem.IsWindows())
		{
			var dur = mode == RouterMode.Play ? 100 : 200;
			Console.Beep(880, dur);
			Thread.Sleep(50);
		}
		else if (OperatingSystem.IsMacOS())
		{
			var sound = mode == RouterMode.Play ? MacSoundPlay : MacSoundControl;
			using var player = Process.Start("afplay", [sound]);
			player?.WaitForExit();
		}
	}

	private static void Announce(RouterMode mode, ILogger logger)
	{
		var file = mode == RouterMode.Play ? "play-mode.wav" : "control-mode.wav";
		var sound = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", file);

		if (!File.Exists(sound))
		{
			logger.Information("no file");
			return;
		}
            
		if (OperatingSystem.IsWindows())
		{
			// SND_ASYNC cuts off a still-playing announcement when the mode is switched again quickly.
			PlaySound(sound, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME);
		}
		else if (OperatingSystem.IsMacOS())
		{
			using var player = Process.Start("afplay", [sound]);
			player?.WaitForExit();
		}
	}
}
