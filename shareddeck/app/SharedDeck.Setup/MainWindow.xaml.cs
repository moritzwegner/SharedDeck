using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using SharedDeck.Core.Installation;

namespace SharedDeck.Setup;

/// <summary>
/// Setup window. Arguments (all optional):
/// /uninstall, /auto (install right away, used by the in-app updater), /dir:&lt;folder&gt;, /community:&lt;folder&gt;,
/// /aircraft:&lt;zip&gt;, /noaircraft, /waitpid:&lt;process id&gt;, /restart, /noregister, /log:&lt;file&gt;
/// </summary>
public partial class MainWindow : Window
{
    private readonly bool uninstallMode;
    private readonly bool autoMode;
    private readonly bool restartApp;
    private readonly bool register;
    private readonly int waitForProcess;
    private readonly string? logFile;
    private string installedApp = "";

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        Title = $"SharedDeck {version} Setup";

        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        bool Flag(string name) => args.Any(a => a.Equals($"/{name}", StringComparison.OrdinalIgnoreCase));
        string? Value(string name) => args
            .Where(a => a.StartsWith($"/{name}:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a[(name.Length + 2)..])
            .FirstOrDefault();

        uninstallMode = Flag("uninstall");
        autoMode = Flag("auto");
        restartApp = Flag("restart");
        register = !Flag("noregister");
        waitForProcess = int.TryParse(Value("waitpid"), out var pid) ? pid : 0;
        logFile = Value("log");

        if (uninstallMode)
        {
            Title = "SharedDeck entfernen";
            Heading.Text = "SharedDeck entfernen";
            Form.Visibility = Visibility.Collapsed;
            InstallButton.Content = "Entfernen";
            Log("SharedDeck-App, Verknüpfungen und das SharedDeck-Modul werden entfernt. Das Flugzeug bleibt installiert.");
            return;
        }

        InstallDirBox.Text = Value("dir") ?? Installer.InstalledDirectory() ?? Installer.DefaultInstallDirectory;
        var community = Value("community") ?? Installer.FindCommunityFolder();
        CommunityBox.Text = community ?? "";
        CommunityHint.Text = community != null
            ? "Automatisch gefunden."
            : "Nicht automatisch gefunden. Bitte den Community-Ordner von MSFS 2024 auswählen (Ordner „Community“ im Paketordner).";

        // the aircraft next to the setup: one ZIP or the parts of a split download
        var setupDirectory = Environment.ProcessPath is { } process ? Path.GetDirectoryName(process)! : AppContext.BaseDirectory;
        AircraftBox.Text = Flag("noaircraft")
            ? ""
            : Value("aircraft") ?? Directory.GetFiles(setupDirectory, "SharedDeck-A32NX-part1of*.zip")
                .Concat(Directory.GetFiles(setupDirectory, "SharedDeck-A32NX.zip"))
                .FirstOrDefault() ?? "";
        if (Installer.IsInstalled())
        {
            Log("SharedDeck ist bereits installiert – die Installation aktualisiert es.");
        }

        if (autoMode)
        {
            Heading.Text = $"SharedDeck wird auf Version {version} aktualisiert";
            Form.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => OnInstall(this, new RoutedEventArgs());
        }
    }

    private void OnBrowseInstall(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Programmordner wählen", InitialDirectory = InstallDirBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            InstallDirBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseCommunity(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Community-Ordner von MSFS 2024 wählen" };
        if (dialog.ShowDialog(this) == true)
        {
            CommunityBox.Text = dialog.FolderName;
            CommunityHint.Text = "";
        }
    }

    private void OnBrowseAircraft(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "SharedDeck-A32NX (ZIP) wählen", Filter = "ZIP-Datei (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) == true)
        {
            AircraftBox.Text = dialog.FileName;
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<string>(Log);
        var percent = new Progress<double>(fraction =>
        {
            Progress.Value = fraction * 100;
            ProgressText.Text = $"{fraction * 100:0} %";
        });
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        // the form makes room for log and progress
        Form.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = "0 %";
        ProgressCaption.Text = uninstallMode ? "Entferne …" : "Installiere …";
        ProgressPanel.Visibility = Visibility.Visible;
        try
        {
            if (uninstallMode)
            {
                Progress.IsIndeterminate = true;
                ProgressText.Text = "";
                await Task.Run(() => Installer.Uninstall(progress));
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                ProgressCaption.Text = "Entfernt";
                return;
            }

            if (waitForProcess > 0)
            {
                Log("Warte, bis die SharedDeck-App beendet ist …");
                await WaitForExit(waitForProcess);
            }

            var community = CommunityBox.Text.Trim();
            if (community.Length > 0 && !Directory.Exists(community))
            {
                throw new DirectoryNotFoundException($"Den Community-Ordner „{community}“ gibt es nicht.");
            }
            if (community.Length == 0 && (ModuleBox.IsChecked == true || AircraftBox.Text.Trim().Length > 0))
            {
                throw new InvalidOperationException("Bitte den Community-Ordner von MSFS 2024 auswählen.");
            }
            if (Process.GetProcessesByName("FlightSimulator2024").Length > 0)
            {
                Log("Hinweis: MSFS 2024 läuft. Die neuen Pakete werden erst nach einem Neustart des Simulators geladen.");
            }

            var options = new InstallOptions
            {
                InstallDirectory = InstallDirBox.Text.Trim(),
                CommunityFolder = community.Length > 0 ? community : null,
                InstallModule = ModuleBox.IsChecked == true,
                AircraftZip = AircraftBox.Text.Trim().Length > 0 ? AircraftBox.Text.Trim() : null,
                DesktopShortcut = DesktopBox.IsChecked == true,
                RegisterWithWindows = register,
            };
            await Task.Run(() =>
            {
                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                    ?? throw new InvalidOperationException("Dieses Setup enthält keine Programmdateien (Entwickler-Build).");
                Installer.Install(payload, options, progress, percent);
            });
            installedApp = Path.Combine(options.InstallDirectory, Installer.AppExecutable);
            ProgressCaption.Text = "Fertig";

            if (restartApp)
            {
                Log("Starte SharedDeck neu …");
                await Task.Delay(1500);
                OnStartApp(this, new RoutedEventArgs());
                return;
            }
            StartButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log($"FEHLER: {ex.Message}");
            ProgressCaption.Text = "Abgebrochen";
            InstallButton.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            Form.Visibility = uninstallMode ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            // the bar stays visible: 100 % when done, or where an error stopped the installation
            CloseButton.IsEnabled = true;
        }
    }

    private static async Task WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (OperationCanceledException)
        {
            // continue anyway, the copy reports a locked file if it is still running
        }
    }

    private void OnStartApp(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(installedApp) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installedApp) });
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Log(string line)
    {
        LogList.Items.Add(line);
        LogList.ScrollIntoView(line);
        if (logFile != null)
        {
            try
            {
                File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // the log file is a help, never a reason to fail
            }
        }
    }
}
