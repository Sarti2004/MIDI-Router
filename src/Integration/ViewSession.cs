using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;

namespace MIDIRouter.Integration;

/// <summary>The adapter from a view to the host's session, as in the Macro Deck UI guide.</summary>
internal sealed class ViewSession : IUiSession
{
	private readonly UiView _view;
	private readonly Action _onDisposed;

	public ViewSession(UiView view, Action onDisposed)
	{
		_view = view;
		_onDisposed = onDisposed;
		_view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
		_view.HandlerFaulted += (_, fault) => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree() => _view.Tree;

	public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

	public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

	public ValueTask DisposeAsync()
	{
		_onDisposed();
		_view.Dispose();
		return ValueTask.CompletedTask;
	}
}
