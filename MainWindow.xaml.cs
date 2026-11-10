using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NoIncomingCallsModpackBuilder.Services;

namespace NoIncomingCallsModpackBuilder;

public partial class MainWindow : Window
{
    private readonly GameScriptService _gameScripts = new();
    private readonly OivPackageBuilder _oivBuilder = new();
    private GameScanResult? _scan;
    private bool _isBusy;
    private string? _exportFolder;
    private DateTime _buildMenuClosedAt = DateTime.MinValue;
    private DateTime _doneVisibleUntil = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        PrimaryScriptsList.ItemsSource = new[] { GameScriptService.DialogueHandlerScript };
        OptionalScriptsList.ItemsSource = GameScriptService.AmbientCallerScripts;
        RefreshScriptList();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _exportFolder = UserSettings.LoadLastExportFolder();
        var lastGameFolder = UserSettings.LoadLastGameFolder();
        if (string.IsNullOrWhiteSpace(lastGameFolder)) return;

        GameFolderText.Text = lastGameFolder;
        GameFolderText.ToolTip = lastGameFolder;
        if (Directory.Exists(lastGameFolder))
            await AnalyseFolderAsync(lastGameFolder, showError: false);
        else
            SetStatus("The last selected GTA V folder is no longer available.", "SubTextBrush");
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the GTA V installation folder"
        };
        if (dialog.ShowDialog(this) != true) return;

        UserSettings.SaveLastGameFolder(dialog.FolderName);
        await AnalyseFolderAsync(dialog.FolderName);
    }

    private async Task AnalyseFolderAsync(string gameFolder, bool showError = true)
    {
        SetBusy(true);
        _scan = null;
        BuildButton.IsEnabled = false;
        GameFolderText.Text = gameFolder;
        GameFolderText.ToolTip = gameFolder;
        EditionText.Text = "—";
        GameBuildText.Text = "—";
        ValidationText.Text = "Checking";
        ValidationText.Foreground = (Brush)FindResource("WarnBrush");
        ScriptCountText.Text = "Checking";
        SetStatus("Reading the selected original game files...", "SubTextBrush");

        try
        {
            var result = await Task.Run(() => _gameScripts.ReadAndValidate(gameFolder, SetProgress));
            _scan = result;
            EditionText.Text = result.Edition.ToString();
            GameBuildText.Text = result.GameVersion;
            ValidationText.Text = "Ready";
            ValidationText.Foreground = (Brush)FindResource("GoodBrush");
            RefreshScriptList();
            UpdateBuildAvailability();
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ValidationText.Text = "Stopped safely";
            ValidationText.Foreground = (Brush)FindResource("BadBrush");
            SetStatus("Validation stopped. See the message for details.", "BadBrush");
            if (showError)
            {
                MessageBox.Show(this, ex.Message, "No Incoming Calls Modpack Builder", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFilePath is not null && DateTime.UtcNow < _doneVisibleUntil && File.Exists(_lastOutputFilePath))
        {
            OpenBuiltFile(_lastOutputFilePath);
            return;
        }

        await BuildPackageAsync(BuildKind.Oiv);
    }

    private void BuildExtraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _scan is null) return;
        if ((DateTime.UtcNow - _buildMenuClosedAt).TotalMilliseconds < 200 && !BuildMenuPopup.IsOpen)
            return;
        BuildMenuPopup.IsOpen = true;
    }

    private async void BuildZipButton_Click(object sender, RoutedEventArgs e)
    {
        BuildMenuPopup.IsOpen = false;
        await BuildPackageAsync(BuildKind.Zip);
    }

    private async void BuildOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        BuildMenuPopup.IsOpen = false;
        await BuildPackageAsync(BuildKind.OriginalOiv);
    }

    private void SetExportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        BuildMenuPopup.IsOpen = false;
        if (_isBusy) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder for built modpacks",
            InitialDirectory = GetExportFolder()
        };
        if (dialog.ShowDialog(this) != true) return;

        _exportFolder = dialog.FolderName;
        UserSettings.SaveLastExportFolder(_exportFolder);
        SetStatus("Export folder saved.", "SubTextBrush");
    }

    private async Task BuildPackageAsync(BuildKind kind)
    {
        if (_isBusy || _scan is null) return;

        var scan = _scan;
        var edition = scan.Edition.ToString();
        var isZip = kind == BuildKind.Zip;
        var isOriginal = kind == BuildKind.OriginalOiv;
        // Read every UI-backed selection before the worker starts. WPF controls
        // are owned by the UI thread and must not be read inside Task.Run.
        var includeOptionalScripts = IncludeOptionalScripts;
        var includePrimaryScript = IncludePrimaryScripts;
        var selectedScripts = SelectedScriptNames();
        if (selectedScripts.Count == 0)
        {
            SetStatus("Select at least one script category to build a package.", "WarnBrush");
            return;
        }
        var extension = isZip ? ".zip" : ".oiv";
        var buildName = ToFileNamePart(scan.GameVersion);
        var suggestedFileName = isOriginal
            ? $"Original Phone Calls - {edition} {buildName}.oiv"
            : $"No Incoming Calls - {edition} {buildName}{extension}";
        string outputPath;

        if (HasSavedExportFolder)
        {
            outputPath = GetAvailableOutputPath(_exportFolder!, suggestedFileName);
        }
        else
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = isOriginal ? "Save the original-script OIV package" : "Save the generated mod package",
                Filter = isZip ? "ZIP archive (*.zip)|*.zip" : "OpenIV package (*.oiv)|*.oiv",
                DefaultExt = extension,
                AddExtension = true,
                InitialDirectory = GetExportFolder(),
                FileName = suggestedFileName
            };
            if (dialog.ShowDialog(this) != true) return;

            outputPath = dialog.FileName;
            _exportFolder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(_exportFolder))
                UserSettings.SaveLastExportFolder(_exportFolder);
        }

        SetBusy(true);
        SetStatus(isOriginal ? "Creating an OIV with the original scripts..." : "Creating the mod package...", "SubTextBrush");
        try
        {
            var result = await Task.Run(() =>
            {
                if (isOriginal)
                    return _oivBuilder.BuildOriginal(scan, selectedScripts, outputPath);

                var patches = _gameScripts.CreatePatches(scan, includeOptionalScripts, includePrimaryScript);
                return _oivBuilder.Build(scan, patches, outputPath);
            });
            _lastOutputFilePath = result.OutputPath;
            var scriptLabel = result.ScriptCount == 1 ? "script" : "scripts";
            SetStatus($"Done — {result.ScriptCount} {scriptLabel} packaged for GTA V {result.Edition}.", "GoodBrush");
            BuildDoneEffect(result.OutputPath);
        }
        catch (Exception ex)
        {
            SetStatus("The package was not created.", "BadBrush");
            MessageBox.Show(this, ex.Message, "No Incoming Calls Modpack Builder", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void OpenBuiltFile(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void SetProgress(string message)
    {
        Dispatcher.BeginInvoke(() => SetStatus(message, "SubTextBrush"));
    }

    private async void BuildDoneEffect(string path)
    {
        _lastOutputFilePath = path;
        _doneVisibleUntil = DateTime.UtcNow.AddSeconds(4.5);
        BuildButton.MinWidth = BuildButton.ActualWidth;
        BuildRoot.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x4E));
        BuildButton.Content = "Show in folder";
        BuildButton.Foreground = Brushes.White;
        BuildExtraButton.Foreground = Brushes.White;

        await Task.Delay(TimeSpan.FromSeconds(4.5));
        if (DateTime.UtcNow < _doneVisibleUntil) return;

        BuildRoot.Background = Brushes.White;
        BuildButton.Content = "Build Modpack";
        BuildButton.Foreground = Brushes.Black;
        BuildExtraButton.Foreground = Brushes.Black;
        BuildButton.MinWidth = 0;
        _lastOutputFilePath = null;
        StatusText.Text = string.Empty;
    }

    private void BuildMenuPopup_Closed(object? sender, EventArgs e)
    {
        _buildMenuClosedAt = DateTime.UtcNow;
    }

    private void BuildMenuPopup_Opened(object? sender, EventArgs e)
    {
        BuildFlyoutBorder.BeginAnimation(OpacityProperty, null);
        BuildFlyoutBorder.Opacity = 0;
        BuildFlyoutBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BrowseFolderButton.IsEnabled = !busy;
        UpdateBuildAvailability();
        if (busy) BuildMenuPopup.IsOpen = false;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void SetStatus(string message, string brushKey)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(brushKey);
    }

    private string? _lastOutputFilePath;

    private bool IncludePrimaryScripts => PrimaryScriptsOption.IsChecked == true;
    private bool IncludeOptionalScripts => OptionalScriptsOption.IsChecked == true;
    private bool HasSavedExportFolder => !string.IsNullOrWhiteSpace(_exportFolder) && Directory.Exists(_exportFolder);

    private void ScriptScopeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (OptionalScriptsList is null) return;
        RefreshScriptList();
    }

    private void RefreshScriptList()
    {
        PrimaryScriptsList.ItemsSource = new[] { GameScriptService.DialogueHandlerScript };
        OptionalScriptsList.ItemsSource = GameScriptService.AmbientCallerScripts;
        PrimaryScriptsList.Opacity = IncludePrimaryScripts ? 1 : 0.32;
        OptionalScriptsList.Visibility = IncludeOptionalScripts ? Visibility.Visible : Visibility.Collapsed;
        var selectedCount = (IncludePrimaryScripts ? 1 : 0) +
                            (IncludeOptionalScripts ? GameScriptService.AmbientCallerScripts.Length : 0);
        ScriptCountText.Text = _scan is null
            ? "Waiting"
            : $"{selectedCount} / {_scan.Scripts.Count} selected";
        UpdateBuildAvailability();
    }

    private IReadOnlyCollection<string> SelectedScriptNames()
    {
        var names = new List<string>();
        if (IncludePrimaryScripts) names.Add(GameScriptService.DialogueHandlerScript);
        if (IncludeOptionalScripts) names.AddRange(GameScriptService.AmbientCallerScripts);
        return names;
    }

    private void UpdateBuildAvailability()
    {
        var enabled = !_isBusy && _scan is not null && (IncludePrimaryScripts || IncludeOptionalScripts);
        BuildButton.IsEnabled = enabled;
        BuildExtraButton.IsEnabled = enabled;
    }

    private static string ToFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private string GetExportFolder()
        => !string.IsNullOrWhiteSpace(_exportFolder) && Directory.Exists(_exportFolder)
            ? _exportFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static string GetAvailableOutputPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 1; ; number++)
        {
            candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var mica = 2;
        DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int));
        var dark = 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private enum BuildKind
    {
        Oiv,
        Zip,
        OriginalOiv
    }
}
