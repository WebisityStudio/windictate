using System.Diagnostics;
using System.IO;
using System.Windows;
using WinDictate.Core;

namespace WinDictate.UI;

/// <summary>
/// Product, version, build date, source commit, the speech model and what this
/// build deliberately leaves out. The licence texts open from the program
/// folder (no network). Only one is open at a time.
/// </summary>
public partial class AboutWindow : Window
{
    private static AboutWindow? _open;
    private readonly string _hotkey;

    public AboutWindow(ModelCheck? modelState, string hotkey = "Ctrl+Alt")
    {
        InitializeComponent();
        _hotkey = hotkey;
        ProductText.Text = BuildInfo.Product;
        VersionValue.Text = BuildInfo.Version;
        BuiltValue.Text = BuildInfo.BuildDateUtc.Length > 0 ? BuildInfo.BuildDateUtc : "Developer build (not a release)";
        CommitValue.Text = BuildInfo.Commit.Length > 0 ? BuildInfo.Commit : "not recorded";
        CommitValue.ToolTip = CommitValue.Text;
        ModelValue.Text = DescribeModel(modelState);

        NoticeButton.Click += (_, _) => PilotNoticeWindow.ShowForReading(_hotkey, this);
        LicenceButton.Click += (_, _) => OpenBesideProgram("LICENSE.txt");
        NoticesButton.Click += (_, _) => OpenBesideProgram("THIRD-PARTY-NOTICES.txt");
        CopyButton.Click += (_, _) => CopyDetails();
        CloseButton.Click += (_, _) => Close();
        SourceInitialized += (_, _) => WindowBackdrop.TryUseGlass(this);
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }

    /// <summary>Opens About, or brings the open one to the front.</summary>
    public static void ShowSingle(ModelCheck? modelState, string hotkey = "Ctrl+Alt")
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new AboutWindow(modelState, hotkey);
        _open.Show();
        _open.Activate();
    }

    internal static string DescribeModel(ModelCheck? state)
    {
        string file = $"{PilotModel.FileName} (SHA-256 {PilotModel.ShortHash}…)";
        return state switch
        {
            ModelCheck.Ok => file + ", checksum verified",
            null => file + ", being checked",
            _ => file + ", failed its check: " + PilotModel.Explain(state.Value)
        };
    }

    private string Details() => string.Join(Environment.NewLine,
        $"{BuildInfo.Product} {BuildInfo.Version}",
        $"Built: {BuiltValue.Text}",
        $"Source commit: {CommitValue.Text}",
        $"Speech model: {ModelValue.Text}",
        "Speech recognition: on this PC only",
        "Transcript history: disabled",
        "External AI cleanup: not included",
        $"Windows: {Environment.OSVersion.VersionString}");

    private void CopyDetails()
    {
        try { System.Windows.Clipboard.SetText(Details()); }
        catch (Exception ex) { Logger.Warn($"Could not copy the About details: {ex.GetType().Name}"); }
    }

    private static void OpenBesideProgram(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                Logger.Warn($"{fileName} is missing from the program folder");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not open {fileName}: {ex.GetType().Name}");
        }
    }
}
