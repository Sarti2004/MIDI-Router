namespace MIDIRouter.Deck;

/// <param name="Apps">The Steinberg apps written to, e.g. "Cubase, Nuendo"; empty when <paramref name="FallbackFolder"/> is set.</param>
/// <param name="FallbackFolder">Where the script went when no Driver Scripts folder was found.</param>
internal sealed record DeckExportResult(string Apps, string? FallbackFolder);

/// <summary>Writes the virtual device's Cubase script into the Driver Scripts folder of each Steinberg app found.</summary>
internal static class DeckExport
{
	// Not Documents on macOS: it is privacy-protected there.
	private static readonly string FallbackFolder = OperatingSystem.IsMacOS()
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MIDI Router", "Cubase export")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIDI Router", "Cubase export");

	/// <exception cref="IOException">A script could not be written.</exception>
	/// <exception cref="UnauthorizedAccessException">A script could not be written.</exception>
	public static async Task<DeckExportResult> ExportAsync(VirtualDevice device, string portName, CancellationToken cancellationToken)
	{
		var targets = CubaseScript.DriverScriptFolders();
		var fallback = targets.Count == 0 ? FallbackFolder : null;
		if (fallback is not null)
		{
			targets = [fallback];
		}

		var script = CubaseScript.Generate(device, portName);
		foreach (var target in targets)
		{
			var path = Path.Combine(target, CubaseScript.RelativePath());
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, script, cancellationToken).ConfigureAwait(false);
		}

		// Documents/Steinberg/<app>/MIDI Remote/Driver Scripts
		var apps = fallback is null
			? string.Join(", ", targets.Select(target => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(target)))))
			: string.Empty;
		return new DeckExportResult(apps, fallback);
	}
}
