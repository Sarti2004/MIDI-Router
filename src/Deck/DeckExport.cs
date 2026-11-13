namespace MIDIRouter.Deck;

internal sealed record DeckExportResult(string Apps, string? FallbackFolder);

internal static class DeckExport
{
	private static readonly string FallbackFolder = OperatingSystem.IsMacOS()
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MIDI Router", "Cubase export")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIDI Router", "Cubase export");

	public static async Task<DeckExportResult> ExportAsync(VirtualDevice device, CancellationToken cancellationToken)
	{
		var targets = CubaseScript.DriverScriptFolders();
		var fallback = targets.Count == 0 ? FallbackFolder : null;
		if (fallback is not null)
		{
			targets = [fallback];
		}

		var script = CubaseScript.Generate(device);
		foreach (var target in targets)
		{
			var path = Path.Combine(target, CubaseScript.RelativePath());
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, script, cancellationToken).ConfigureAwait(false);
		}

		var apps = fallback is null
			? string.Join(", ", targets.Select(target => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(target)))))
			: string.Empty;
		return new DeckExportResult(apps, fallback);
	}
}
