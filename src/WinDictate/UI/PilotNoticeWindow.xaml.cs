using System.Windows;
using WinDictate.Core;

namespace WinDictate.UI;

/// <summary>
/// Shows <see cref="PilotNotice.Text"/>. On first launch it asks for
/// acknowledgement (dictation stays off until then, and "Exit" quits); opened
/// again from About it is for reading only.
/// </summary>
public partial class PilotNoticeWindow : Window
{
    public PilotNoticeWindow(string hotkey, bool firstRun)
    {
        InitializeComponent();
        NoticeText.Text = PilotNotice.Text;
        HowToText.Text = PilotNotice.HowTo(hotkey);
        if (!firstRun)
        {
            ExitButton.Visibility = Visibility.Collapsed;
            AcceptButton.Content = "Close";
        }
        AcceptButton.Click += (_, _) =>
        {
            // DialogResult only exists for the modal first-run window.
            if (firstRun) DialogResult = true;
            else Close();
        };
        ExitButton.Click += (_, _) => DialogResult = false;
        SourceInitialized += (_, _) => WindowBackdrop.TryUseGlass(this);
    }

    /// <summary>First launch: true when the user acknowledged the notice.</summary>
    public static bool AskOnFirstRun(string hotkey)
    {
        var window = new PilotNoticeWindow(hotkey, firstRun: true);
        return window.ShowDialog() == true;
    }

    /// <summary>For reading again (from About).</summary>
    public static void ShowForReading(string hotkey, Window? owner)
    {
        var window = new PilotNoticeWindow(hotkey, firstRun: false);
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Show();
        window.Activate();
    }
}
