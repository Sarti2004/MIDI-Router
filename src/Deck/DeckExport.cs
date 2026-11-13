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
			var path = Path.Combine(target, CubaseScript.RelativePath(device));
			RemoveOtherScripts(Path.Combine(target, CubaseScript.VendorFolder()), Path.GetDirectoryName(path)!);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, script, cancellationToken).ConfigureAwait(false);
		}

		var apps = fallback is null
			? string.Join(", ", targets.Select(target => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(target)))))
			: string.Empty;
		return new DeckExportResult(apps, fallback);
	}

	// There is one virtual device: a script exported under an earlier name would stay listed in Cubase. Only folders
	// holding nothing but our generated script are removed.
	private static void RemoveOtherScripts(string vendorFolder, string keep)
	{
		if (!Directory.Exists(vendorFolder))
		{
			return;
		}

		foreach (var folder in Directory.GetDirectories(vendorFolder))
		{
			var files = Directory.GetFileSystemEntries(folder);
			if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)
				&& files.Length == 1
				&& Path.GetFileName(files[0]).Equals($"{CubaseScript.Vendor}_{Path.GetFileName(folder)}.js", StringComparison.OrdinalIgnoreCase))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}
}
