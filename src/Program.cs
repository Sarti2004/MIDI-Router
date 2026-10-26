using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using MIDIRouter;
using MIDIRouter.Routing;
using Microsoft.Extensions.DependencyInjection;

// Identity, description and icon are not set here: they come from manifest.json at the content root.
var builder = MacroDeckPlugin.CreatePlugin(args);

builder.Services.AddSingleton<MidiRouter>();

var plugin = builder
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PluginIntegration>()
	.Build();

await plugin.RunAsync();
