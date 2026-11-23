using System.Diagnostics;
using System.IO;
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

    /// <summary>
    /// The Settings window in its Windows 11 look, light and dark: it opens, shows
    /// the saved choices, shows the pilot's fixed privacy behaviour without any
    /// control to change it, fits a small laptop screen, and opens Advanced.
    /// </summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void SettingsWindow_ShowsTheSavedChoices_AndThePilotsFixedPrivacy(string theme) => Ui.Run(() =>
    {
        bool dark = theme == "dark";
        IndicatorTheme.ForcedAnimations = false;
        var settings = new AppSettings { Indicator = "glow", Hotkey = "Ctrl+Shift" };
        var window = new SettingsWindow(settings, ModelCheck.Ok) { ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light };
        try
        {
            window.Show();
            Ui.Pump(700);

            // What Windows 11 puts behind the window (its glass) cannot be captured here, so the
            // pictures use the plain colour Windows falls back to when transparency is off.
            Color fallback = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
            Color behind = window.Background is System.Windows.Media.SolidColorBrush own && own.Color.A == 0xFF ? own.Color : fallback;

            Assert.True(window.IndicatorGlowRadio.IsChecked == true, "the saved indicator should be selected");
            Assert.True(window.TrailingSpaceBox.IsChecked == true, "new settings should load their defaults");
            Assert.Equal(new[] { "Ctrl", "Shift" }, window.ShortcutKeys.Items.Cast<string>().ToArray());
            Assert.Equal("Ready. Everything stays on this PC.", window.StatusText.Text);
            Assert.Equal(Visibility.Collapsed, window.AdvancedPanel.Visibility);

            // Nothing on the page can switch on history, cleanup, a model download or start-up with Windows.
            string everything = string.Join(" | ", LogicalText(window));
            foreach (string absent in new[] { "AI cleanup", "API key", "Start with Windows", "Speech quality", "Save a history", "Keep the text on the clipboard" })
                Assert.DoesNotContain(absent, everything);
            foreach (string shown in new[] { "PRIVACY (FIXED IN THIS PILOT)", "Not used", "Restored", "small.en" })
                Assert.Contains(shown, everything);

            // A 14-inch laptop at 150% scaling leaves about 650 units of height: the everyday page must fit without scrolling.
            double before = window.ActualHeight;
            Ui.Note($"settings-{theme}", $"everyday page: {window.ActualWidth:F0} x {before:F0} units");
            Assert.True(before <= 648, $"the everyday page is {before:F0} units tall; it must fit a small laptop screen (648)");
            Assert.Equal(0, window.Scroller.ScrollableHeight, 1.0);
            Assert.True(Ui.Capture(window, $"settings-{theme}", behind, plain: true) > 5000, "the window should draw its content");

            window.AdvancedToggle.IsChecked = true;
            Ui.Pump(500);
            Assert.Equal(Visibility.Visible, window.AdvancedPanel.Visibility);
            Assert.True(window.ActualHeight <= SystemParameters.WorkArea.Height, "the window must fit on the screen");
            Assert.True(window.Top + window.ActualHeight <= SystemParameters.WorkArea.Bottom + 1, "the window must not hang off the bottom of the screen");
            Ui.Capture(window, $"settings-{theme}-advanced", behind, plain: true);

            // The Preview button plays a pretend dictation; it must not throw or steal the window.
            window.IndicatorPillRadio.IsChecked = true;
            window.IndicatorPreviewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Ui.Pump(600);
            Assert.True(window.IsVisible);

            // The microphone test must cope with whatever this machine has, including no microphone at all.
            window.MicTestButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Ui.Pump(900);
            Ui.Note($"settings-{theme}", $"microphone test after 0.9 s: button \"{window.MicTestButton.Content}\", line \"{window.MicRow.Tag}\"");
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
            Ui.Pump(300); // lets a microphone opened by the test close again
            IndicatorTheme.ForcedAnimations = null;
        }
    });

    /// <summary>The first-run notice, word for word, and About with the build details, light and dark.</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void PilotNotice_AndAbout_ShowWhatTheBriefRequires(string theme) => Ui.Run(() =>
    {
        bool dark = theme == "dark";
        Color behind = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        var notice = new PilotNoticeWindow("Ctrl+Alt", firstRun: true) { ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light };
        var about = new AboutWindow(ModelCheck.Ok, "Ctrl+Alt") { ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light };
        try
        {
            notice.Show();
            Ui.Pump(500);
            Assert.Equal(PilotNotice.Text, notice.NoticeText.Text);
            Assert.Equal("I understand", notice.AcceptButton.Content);
            Assert.Equal(Visibility.Visible, notice.ExitButton.Visibility);
            Assert.True(Ui.Capture(notice, $"pilot-notice-{theme}", behind, plain: true) > 3000, "the notice should draw");

            about.Show();
            Ui.Pump(500);
            string text = string.Join(" | ", LogicalText(about));
            Assert.Contains(BuildInfo.Version, text);
            Assert.Contains(PilotModel.FileName, text);
            Assert.Contains(PilotModel.ShortHash, text);
            Assert.Contains("checksum verified", text);
            Assert.Contains("Disabled: not available in this build", text); // transcript history
            Assert.Contains("Disabled: not included in this build", text);  // external AI cleanup
            Assert.Contains("On this PC only", text);
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")), "the licence ships beside the program");
            Ui.Capture(about, $"about-{theme}", behind, plain: true);
        }
        finally
        {
            about.Close();
            notice.Close();
        }
    });

    /// <summary>Every piece of text in a window (labels, buttons, row headers and explanations).</summary>
    private static IEnumerable<string> LogicalText(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            switch (child)
            {
                case string s when s.Length > 0: yield return s; break;
                case TextBlock t when t.Text.Length > 0: yield return t.Text; break;
                case HeaderedContentControl h:
                    if (h.Header is string header) yield return header;
                    if (h.Tag is string tag) yield return tag;
                    break;
                case ContentControl c when c.Content is string content: yield return content; break;
            }
            if (child is DependencyObject node)
                foreach (string inner in LogicalText(node)) yield return inner;
        }
    }

    /// <summary>
    /// The compiled program, checked the way the source is checked on every build:
    /// it references no networking library and no model downloader.
    /// </summary>
    [Fact]
    public void TheProgram_ReferencesNoNetworkingCode()
    {
        System.Reflection.Assembly app = typeof(App).Assembly;
        string[] references = app.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        foreach (string forbidden in new[] { "System.Net.Http", "System.Net.Sockets", "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets.Client", "System.Net.Mail" })
            Assert.DoesNotContain(forbidden, references);

        using var stream = File.OpenRead(app.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var typeNames = metadata.TypeReferences
            .Select(h => metadata.GetTypeReference(h))
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name))
            .ToArray();
        foreach (string forbidden in new[] { "HttpClient", "WebClient", "Socket", "WhisperGgmlDownloader", "HttpRequestMessage" })
            Assert.DoesNotContain(typeNames, n => n.EndsWith("." + forbidden, StringComparison.Ordinal));
        Ui.Note("no-network-references", $"checked: WinDictate.dll references {references.Length} assemblies and {typeNames.Length} types, none of them for networking or downloading");
    }

    /// <summary>This runner can run the speech engine: the checks the app makes at start-up pass here.</summary>
    [Fact]
    public void SystemChecks_PassOnThisRunner()
    {
        Assert.True(SystemCheck.CpuSupported(), "the runner's processor should have AVX2 and FMA");
        Assert.Empty(SystemCheck.MissingRuntimeLibraries());
        Ui.Note("system-check", "checked: AVX/AVX2/FMA present; Visual C++ runtime libraries found: " + string.Join(", ", SystemCheck.RequiredRuntimeLibraries));
    }

    /// <summary>
    /// With a picture on the real Windows clipboard, a dictation in paste mode is
    /// typed instead and the picture is still there afterwards.
    /// </summary>
    [Fact]
    public void APictureOnTheClipboard_SurvivesADictation() => Ui.Run(() =>
    {
        const string dictated = "Typed so the picture survives. ";
        var box = new TextBox { Width = 420, Height = 80, FontSize = 16, Margin = new Thickness(12) };
        var window = new Window
        {
            Title = "WinDictate smoke: picture on the clipboard",
            Content = box,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var target = new WindowsTextTarget();
        try
        {
            window.Show();
            if (!Ui.TakeForeground(window))
            {
                Ui.Note("picture-survives", "NOT CHECKED: the test window could not take the keyboard focus on this machine");
                return;
            }
            box.Focus();
            Keyboard.Focus(box);
            Ui.Pump(200);

            var picture = new System.Windows.Media.Imaging.WriteableBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            System.Windows.Clipboard.SetImage(picture);
            Assert.True(System.Windows.Clipboard.ContainsImage(), "the test picture should be on the clipboard");

            var inserter = new TextInserter(target);
            InsertResult result = inserter.InsertAsync(dictated, new InsertOptions(TypeMode: false, ClipboardRestoreDelayMs: 300), target.CurrentTarget())
                .GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.Inserted, result.Outcome);
            Assert.True(result.TypedToSpareClipboard, "typed rather than pasted, to leave the picture alone");
            Assert.Equal(dictated, box.Text);
            Assert.True(System.Windows.Clipboard.ContainsImage(), "the picture must still be on the clipboard");
            Ui.Note("picture-survives", "checked: with a picture on the clipboard the dictation was typed and the picture was still there afterwards");
        }
        finally
        {
            window.Close();
            try { System.Windows.Clipboard.Clear(); } catch { }
        }
    });

    /// <summary>The pill names the application the text will go into (never the window title).</summary>
    [Fact]
    public void ThePill_NamesTheTargetApplication() => Ui.Run(() =>
    {
        IndicatorTheme.Forced = IndicatorPalette.Light;
        IndicatorTheme.ForcedAnimations = false;
        var owner = new Window { Title = "A confidential window title", Width = 300, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var pill = new GlassPillWindow();
        try
        {
            owner.Show();
            Ui.Pump(200);
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            pill.ShowListening(handle);
            Ui.Speak(pill.SetLevel, 400);
            string shown = pill.TargetShown;
            Ui.Note("target-name", $"the pill named the test host as \"{shown}\"");
            Assert.False(string.IsNullOrEmpty(shown), "the pill should name the application");
            Assert.DoesNotContain("confidential", shown, StringComparison.OrdinalIgnoreCase);
            Ui.Capture(pill, "pill-listening-with-target", Color.FromRgb(0xF3, 0xF3, 0xF3));
        }
        finally
        {
            pill.Dispose();
            owner.Close();
            IndicatorTheme.Forced = null;
            IndicatorTheme.ForcedAnimations = null;
        }
    });

    [Fact]
    public void Clipboard_RoundTrips_WithoutTheUiThread() => Ui.Run(() =>
    {
        var target = new WindowsTextTarget();
        ClipboardSnapshot saved = target.ReadClipboardAsync().GetAwaiter().GetResult();
        (bool hadText, string? previous) = (saved.Readable && saved.Text != null, saved.Text);
        try
        {
            long before = target.ClipboardSequence;
            string text = "WinDictate smoke " + Guid.NewGuid().ToString("N")[..8];
            Assert.True(target.SetClipboardTextAsync(text).GetAwaiter().GetResult(), "writing to the clipboard");
            Assert.NotEqual(before, target.ClipboardSequence);

            ClipboardSnapshot read = target.ReadClipboardAsync().GetAwaiter().GetResult();
            Assert.True(read.Readable, "reading the clipboard");
            Assert.Equal(text, read.Text);
            Assert.True(read.OnlyText, "WinDictate's own text (with its privacy markers) counts as plain text");

            // Marked so that Windows clipboard history and cloud clipboard leave it out.
            string[] formats = ClipboardAccess.TryGetFormatsAsync().GetAwaiter().GetResult();
            Assert.Contains(ClipboardAccess.ExcludeFromMonitoring, formats);
            Assert.Contains(ClipboardAccess.CanIncludeInHistory, formats);
            Assert.Contains(ClipboardAccess.CanUploadToCloud, formats);
            Ui.Note("clipboard-privacy", "checked: dictated text on the clipboard carries " + string.Join(", ", formats));

            Assert.True(target.ClearClipboardAsync().GetAwaiter().GetResult(), "clearing the clipboard");
            read = target.ReadClipboardAsync().GetAwaiter().GetResult();
            Assert.True(read.Readable);
            Assert.Null(read.Text);
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
        ClipboardSnapshot saved = target.ReadClipboardAsync().GetAwaiter().GetResult();
        (bool hadText, string? previous) = (saved.Readable && saved.Text != null, saved.Text);
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
        var options = new InsertOptions(TypeMode: false, ClipboardRestoreDelayMs: 300);
        ClipboardSnapshot saved = target.ReadClipboardAsync().GetAwaiter().GetResult();
        (bool hadText, string? previous) = (saved.Readable && saved.Text != null, saved.Text);
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

            ClipboardSnapshot onClipboard = target.ReadClipboardAsync().GetAwaiter().GetResult();
            Assert.True(onClipboard.Readable);
            Assert.Equal(strays, onClipboard.Text);      // the words are safe on the clipboard instead

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

    /// <summary>
    /// A dictation that could not be inserted waits on the clipboard. Checks, with
    /// the real Windows clipboard and its change counter, that the next dictation
    /// is typed and leaves that text alone; and that pasting returns to normal once
    /// the user copies something else, or has had their time.
    /// </summary>
    [Fact]
    public void TextWaitingOnTheClipboard_IsLeftAlone_UntilTheUserCopiesOrTimePasses() => Ui.Run(() =>
    {
        const string waiting = "Waiting on the clipboard. ";
        const string typedNext = "Typed, so the waiting text stays. ";
        const string pastedAfterCopy = "Pasted again after the user copied. ";
        const string pastedAfterTime = "Pasted again after the time was up. ";

        var box = new TextBox { Width = 560, Height = 130, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        var window = new Window
        {
            Title = "WinDictate smoke: clipboard protection",
            Content = box,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var target = new WindowsTextTarget();
        var options = new InsertOptions(TypeMode: false, ClipboardRestoreDelayMs: 300);
        ClipboardSnapshot saved = target.ReadClipboardAsync().GetAwaiter().GetResult();
        (bool hadText, string? previous) = (saved.Readable && saved.Text != null, saved.Text);

        string? OnClipboard()
        {
            ClipboardSnapshot now = target.ReadClipboardAsync().GetAwaiter().GetResult();
            Assert.True(now.Readable, "reading the clipboard");
            return now.Text;
        }

        try
        {
            window.Show();
            if (!Ui.TakeForeground(window))
            {
                _output.WriteLine("NOT CHECKED: could not give the test window the keyboard focus, so the clipboard protection was not exercised.");
                Ui.Note("clipboard-protection", "NOT CHECKED: the test window could not take the keyboard focus on this machine");
                return;
            }
            box.Focus();
            Keyboard.Focus(box);
            Ui.Pump(200);
            InsertionTarget here = target.CurrentTarget();

            // 1. While the text waits, the next dictation is typed and the clipboard is untouched.
            var inserter = new TextInserter(target);
            Assert.Equal(InsertOutcome.CopiedOnly, inserter.KeepForRecoveryAsync(waiting).GetAwaiter().GetResult().Outcome);
            inserter.RecoveryAnnounced();
            Ui.Pump(400); // time for anything else on this desktop to react to the clipboard change
            InsertResult next = inserter.InsertAsync(typedNext, options, here).GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.Inserted, next.Outcome);
            Assert.True(next.Typed, "typed rather than pasted while text waits on the clipboard");
            Assert.True(next.EarlierTextKept);
            Assert.Equal(typedNext, box.Text);
            Assert.Equal(waiting, OnClipboard());

            // 2. The user copies something else: pasting is back to normal.
            Assert.True(target.SetClipboardTextAsync("copied by the user").GetAwaiter().GetResult(), "writing to the clipboard");
            InsertResult afterCopy = inserter.InsertAsync(pastedAfterCopy, options, here).GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.Inserted, afterCopy.Outcome);
            Assert.False(afterCopy.Typed, "pasted as usual once the user has copied something else");
            Assert.Equal(typedNext + pastedAfterCopy, box.Text);
            Assert.Equal("copied by the user", OnClipboard()); // and the user's copy is put back

            // 3. Nothing is copied, but the time to paste runs out: back to normal as well.
            var brief = new TextInserter(target, protectMs: 500);
            Assert.Equal(InsertOutcome.CopiedOnly, brief.KeepForRecoveryAsync(waiting).GetAwaiter().GetResult().Outcome);
            brief.RecoveryAnnounced();
            Ui.Pump(900);
            InsertResult afterTime = brief.InsertAsync(pastedAfterTime, options, here).GetAwaiter().GetResult();
            Ui.Pump(700);
            Assert.Equal(InsertOutcome.Inserted, afterTime.Outcome);
            Assert.False(afterTime.Typed, "pasted as usual once the time to paste the waiting text is up");
            Assert.Equal(typedNext + pastedAfterCopy + pastedAfterTime, box.Text);
            Assert.Equal(waiting, OnClipboard()); // the waiting text is put back after the paste...

            // 4. ...until its time is up: then it is removed and the user's copy comes back.
            var expiring = new TextInserter(target, protectMs: 0, clearMs: 500);
            Assert.True(target.SetClipboardTextAsync("copied by the user").GetAwaiter().GetResult());
            Assert.Equal(InsertOutcome.CopiedOnly, expiring.KeepForRecoveryAsync(waiting).GetAwaiter().GetResult().Outcome);
            expiring.RecoveryAnnounced();
            Assert.Equal(waiting, OnClipboard());
            Ui.Pump(800);
            Assert.True(expiring.ClearExpiredRecoveryAsync().GetAwaiter().GetResult(), "removing the waiting text");
            Assert.Equal("copied by the user", OnClipboard());

            Ui.Capture(window, "clipboard-protection", White);
            Ui.Note("clipboard-protection", "checked: waiting text was left alone by the next dictation, pasting returned to normal after a copy and after the time limit, the user's copy was put back each time, and the waiting text was removed when its time was up");
        }
        finally
        {
            window.Close();
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
