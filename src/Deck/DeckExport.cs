namespace MIDIRouter.Deck;

/// <param name="Apps">The Steinberg apps written to, e.g. "Cubase, Nuendo"; empty when <paramref name="FallbackFolder"/> is set.</param>
/// <param name="FallbackFolder">Where the scripts went when no Driver Scripts folder was found.</param>
internal sealed record DeckExportResult(int Decks, string Apps, string? FallbackFolder);

/// <summary>Writes every deck's Cubase script into the Driver Scripts folder of each Steinberg app found.</summary>
internal static class DeckExport
{
	private const string FallbackFolderName = "Cubase export";

	/// <exception cref="IOException">A script could not be written.</exception>
	/// <exception cref="UnauthorizedAccessException">A script could not be written.</exception>
	public static async Task<DeckExportResult> ExportAsync(IReadOnlyList<DeckLayout> decks, string portName, CancellationToken cancellationToken)
	{
		var targets = CubaseScript.DriverScriptFolders();
		var fallback = targets.Count == 0 ? Path.Combine(DeckStore.Folder, FallbackFolderName) : null;
		if (fallback is not null)
		{
			targets = [fallback];
		}

		foreach (var target in targets)
		{
			foreach (var deck in decks)
			{
				var path = Path.Combine(target, CubaseScript.RelativePath(deck));
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				await File.WriteAllTextAsync(path, CubaseScript.Generate(deck, portName), cancellationToken).ConfigureAwait(false);
			}
		}

		// Documents/Steinberg/<app>/MIDI Remote/Driver Scripts
		var apps = fallback is null
			? string.Join(", ", targets.Select(target => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(target)))))
			: string.Empty;
		return new DeckExportResult(decks.Count, apps, fallback);
	}
}
