using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinDictate.Core;
using WinDictate.UI;
using WinDictate.UI.Indicators;
using Xunit;
using Xunit.Abstractions;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace WinDictate.UiSmoke;

/// <summary>
/// The parts of the app that only exist on Windows, run for real: the bridge
/// from the dictation controller to the indicators, the Settings window, the
/// clipboard, typing and pasting into a focused text box, and the keyboard hook.
/// </summary>
public class AppSmokeTests
{
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private readonly ITestOutputHelper _output;

    public AppSmokeTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(IndicatorStyle.Pill)]
    [InlineData(IndicatorStyle.Glow)]
    public void Presenter_TurnsTheControllersMessages_IntoIndicatorStates(IndicatorStyle style) => Ui.Run(() =>
    {
        IndicatorTheme.Forced = IndicatorPalette.Light;
        IndicatorTheme.ForcedAnimations = false; // states apply at once, so the checks need little slack
        var presenter = new IndicatorPresenter(Dispatcher.CurrentDispatcher, style);
        try
        {
            // The same calls, in the same order, that the controller makes for one dictation.
            presenter.ShowListening(IntPtr.Zero);
            presenter.SetLevel(0.2f);
            Ui.Pump(300);
            Assert.True(Shown(presenter).Main, "listening: the indicator should be on screen");
            Assert.False(style == IndicatorStyle.Glow && Shown(presenter).Words, "listening: the glow needs no words");

            presenter.ShowStatus("Transcribing…");
            Ui.Pump(300);
            Assert.True(Shown(presenter).Main, "transcribing: the indicator should stay on screen");
            Assert.False(style == IndicatorStyle.Glow && Shown(presenter).Words, "transcribing: still no words");

            presenter.Flash("Inserted 3 words · in clipboard", true, 700);
            Ui.Pump(1800);
            Assert.False(Shown(presenter).Main || Shown(presenter).Words, "inserted: should confirm and then hide itself");

            // A problem gets words, in a pill, on either style.
            presenter.Flash("No speech detected", false, 700);
            Ui.Pump(400);
            GlassPillWindow words = WordsWindow(presenter);
            Assert.True(words.IsVisible, "a problem should be shown in words");
            Assert.Equal("Didn't catch that", words.NoticeText.Text);
            Ui.Capture(words, $"presenter-problem-{style.ToString().ToLowerInvariant()}", White);

            presenter.ShowStatus("Opening microphone…");
            Ui.Pump(300);
            Assert.Equal("Opening microphone", WordsWindow(presenter).NoticeText.Text);

            presenter.Hide();
            Ui.Pump(200);
            Assert.False(Shown(presenter).Main || Shown(presenter).Words, "Hide: should hide at once");

            // Switching style in Settings swaps the indicator and it still works.
            IndicatorStyle other = style == IndicatorStyle.Pill ? IndicatorStyle.Glow : IndicatorStyle.Pill;
            presenter.UseStyle(other);
            presenter.ShowListening(IntPtr.Zero);
            Ui.Pump(300);
            Assert.True(Shown(presenter).Main, "after switching style: the new indicator should be on screen");
            presenter.Hide();
            Ui.Pump(100);
        }
        finally
        {
            presenter.Dispose();
            IndicatorTheme.Forced = null;
            IndicatorTheme.ForcedAnimations = null;
        }
    });

    [Fact]
    public void SettingsWindow_Opens_ShowsTheSavedIndicator_AndPreviewsIt() => Ui.Run(() =>
    {
        IndicatorTheme.ForcedAnimations = false;
        var settings = new AppSettings { Indicator = "glow" };
        var window = new SettingsWindow(settings);
        try
        {
            window.Show();
            Ui.Pump(400);
            Assert.True(window.IndicatorGlowRadio.IsChecked == true, "the saved style should be selected");
            Assert.True(window.TrailingSpaceBox.IsChecked == true, "new settings should load their defaults");
            Ui.Capture(window, "settings-window", Color.FromRgb(0xFA, 0xFA, 0xFA));

            // The Preview button plays a pretend dictation; it must not throw or steal the window.
            window.IndicatorPillRadio.IsChecked = true;
            window.IndicatorPreviewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Ui.Pump(600);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
            IndicatorTheme.ForcedAnimations = null;
        }
    });

    [Fact]
    public void Clipboard_RoundTrips_WithoutTheUiThread() => Ui.Run(() =>
    {
        var target = new WindowsTextTarget();
        (bool hadText, string? previous) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
        try
        {
            long before = target.ClipboardSequence;
            string text = "WinDictate smoke " + Guid.NewGuid().ToString("N")[..8];
            Assert.True(target.SetClipboardTextAsync(text).GetAwaiter().GetResult(), "writing to the clipboard");
            Assert.NotEqual(before, target.ClipboardSequence);

            (bool ok, string? read) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
            Assert.True(ok, "reading the clipboard");
            Assert.Equal(text, read);

            Assert.True(target.ClearClipboardAsync().GetAwaiter().GetResult(), "clearing the clipboard");
            (ok, read) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
            Assert.True(ok);
            Assert.Null(read);
        }
        finally
        {
            if (hadText && previous != null) target.SetClipboardTextAsync(previous).GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void TypingAndPasting_ReachTheFocusedTextBox() => Ui.Run(() =>
    {
        const string typed = "Typed by WinDictate: £5,000 (not the current account). ";
        const string pasted = "Pasted by WinDictate.";

        var box = new TextBox { Width = 560, Height = 110, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        var window = new Window
        {
            Title = "WinDictate smoke target",
            Content = box,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var target = new WindowsTextTarget();
        (bool hadText, string? previous) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
        try
        {
            window.Show();
            if (!Ui.TakeForeground(window))
            {
                // Not a failure of the app: this desktop session will not hand a test window the keyboard.
                _output.WriteLine("NOT CHECKED: could not give the test window the keyboard focus, so typing and pasting were not exercised.");
                Ui.Note("typing-and-pasting", "NOT CHECKED: the test window could not take the keyboard focus on this machine");
                return;
            }
            box.Focus();
            Keyboard.Focus(box);
            Ui.Pump(200);

            Assert.Equal(SendResult.Sent, target.TypeText(typed));
            Ui.Pump(700);
            Assert.Equal(typed, box.Text);

            Assert.True(target.SetClipboardTextAsync(pasted).GetAwaiter().GetResult(), "writing to the clipboard");
            Assert.Equal(SendResult.Sent, target.SendPaste());
            Ui.Pump(700);
            Assert.Equal(typed + pasted, box.Text);

            Ui.Capture(window, "typing-and-pasting", White);
            Ui.Note("typing-and-pasting", "checked: typed and pasted text both arrived in the focused text box");
        }
        finally
        {
            window.Close();
            if (hadText && previous != null) target.SetClipboardTextAsync(previous).GetAwaiter().GetResult();
        }
    });

    /// <summary>
    /// The rule that matters most now that dictations queue: text is inserted only
    /// where the dictation was started. Uses the app's real focus capture
    /// (GetGUIThreadInfo) and its real inserter against two real windows.
    /// </summary>
    [Fact]
    public void ADictationStartedInOneWindow_IsNotInsertedIntoAnother() => Ui.Run(() =>
    {
        const string stays = "Stays where it was dictated. ";
        const string strays = "Must not land in the other window. ";

        (Window Window, TextBox Box) Make(string title, double left)
        {
            var box = new TextBox { Width = 420, Height = 90, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
            var window = new Window
            {
                Title = title,
                Content = box,
                SizeToContent = SizeToContent.WidthAndHeight,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = left,
                Top = 80
            };
            return (window, box);
        }

        var first = Make("WinDictate smoke: dictated here", 40);
        var second = Make("WinDictate smoke: clicked here afterwards", 520);
        var target = new WindowsTextTarget();
        var inserter = new TextInserter(target);
        var options = new InsertOptions(TypeMode: false, KeepInClipboard: true, ClipboardRestoreDelayMs: 300);
        (bool hadText, string? previous) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
        try
        {
            first.Window.Show();
            second.Window.Show();
            if (!Ui.TakeForeground(first.Window))
            {
                _output.WriteLine("NOT CHECKED: could not give the test window the keyboard focus, so the focus rule was not exercised.");
                Ui.Note("insert-only-where-started", "NOT CHECKED: the test window could not take the keyboard focus on this machine");
                return;
            }
            first.Box.Focus();
            Keyboard.Focus(first.Box);
            Ui.Pump(200);

            // The hotkey goes down here: this is where the dictation belongs.
            InsertionTarget started = target.CurrentTarget();
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(first.Window).Handle, started.Window);

            // Focus has not moved: the text is inserted.
            InsertResult inPlace = inserter.InsertAsync(stays, options, started).GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.Inserted, inPlace.Outcome);
            Assert.Equal(stays, first.Box.Text);

            // The user clicks into another window before the next text is ready.
            if (!Ui.TakeForeground(second.Window))
            {
                Ui.Note("insert-only-where-started", "PARTLY CHECKED: insertion in place worked; the second window could not take the keyboard focus");
                return;
            }
            second.Box.Focus();
            Keyboard.Focus(second.Box);
            Ui.Pump(200);

            InsertResult moved = inserter.InsertAsync(strays, options, started).GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.CopiedOnly, moved.Outcome);
            Assert.Equal(RecoveryReason.TargetChanged, moved.Reason);
            Assert.Equal("", second.Box.Text);          // nothing typed into the wrong window
            Assert.Equal(stays, first.Box.Text);         // and nothing more into the first

            (bool ok, string? onClipboard) = target.GetClipboardTextAsync().GetAwaiter().GetResult();
            Assert.True(ok);
            Assert.Equal(strays, onClipboard);           // the words are safe on the clipboard instead

            Ui.Capture(second.Window, "insert-only-where-started", White);
            Ui.Note("insert-only-where-started", "checked: text went in where it was dictated, and went to the clipboard, not the other window, after focus moved");
        }
        finally
        {
            first.Window.Close();
            second.Window.Close();
            if (hadText && previous != null) target.SetClipboardTextAsync(previous).GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void KeyboardHook_Installs_IgnoresSyntheticKeys_AndStopsPromptly()
    {
        int seen = 0;
        var hook = new KeyboardHook();
        hook.KeyEvent += (_, _) => Interlocked.Increment(ref seen);
        try
        {
            hook.Start();     // throws if Windows refuses the hook
            hook.Reinstall(); // as after unlock or resume
            Thread.Sleep(300);

            // The hook is passive and must ignore synthetic input (its own Ctrl+V included).
            Win32.keybd_event(Win32.VK_F24, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(Win32.VK_F24, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(300);
            Assert.Equal(0, Volatile.Read(ref seen));
        }
        finally
        {
            var stopping = Stopwatch.StartNew();
            hook.Dispose();
            Assert.True(stopping.ElapsedMilliseconds < 2500, $"the hook thread took {stopping.ElapsedMilliseconds} ms to stop");
        }
    }

    /// <summary>Which of the presenter's windows are on screen: the indicator itself, and the pill that carries words.</summary>
    private static (bool Main, bool Words) Shown(IndicatorPresenter presenter) => presenter.Indicator switch
    {
        GlassPillWindow pill => (pill.IsVisible, pill.IsVisible),
        EdgeGlowIndicator glow => (glow.Glow.IsVisible, glow.Words.IsVisible),
        _ => (false, false)
    };

    private static GlassPillWindow WordsWindow(IndicatorPresenter presenter) => presenter.Indicator switch
    {
        GlassPillWindow pill => pill,
        EdgeGlowIndicator glow => glow.Words,
        _ => throw new InvalidOperationException("Unknown indicator type")
    };
}
