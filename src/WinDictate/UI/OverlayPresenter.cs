using System.Windows.Threading;
using WinDictate.Core;

namespace WinDictate.UI;

/// <summary>
/// Connects the controller to the overlay window. Every call is posted to the
/// UI thread (same priority, so in call order) and returns immediately, which
/// makes it safe to call from the keyboard-hook thread.
/// </summary>
internal sealed class OverlayPresenter : IDictationOverlay
{
    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindow _window;

    public OverlayPresenter(Dispatcher dispatcher, OverlayWindow window)
    {
        _dispatcher = dispatcher;
        _window = window;
    }

    // The title is read here, on the UI thread: GetWindowText sends WM_GETTEXT
    // when the target is one of WinDictate's own windows.
    public void ShowListening(IntPtr targetWindow) =>
        Post(() => _window.ShowListening(NativeMethods.GetWindowTitle(targetWindow)));

    public void ShowStatus(string text) => Post(() => _window.ShowStatus(text));

    public void SetLevel(float peak) => Post(() => _window.SetLevel(peak));

    public void Flash(string text, bool success, int holdMs) => Post(() => _window.Flash(text, success, holdMs));

    public void Hide() => Post(_window.HideOverlay);

    private void Post(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
}
