using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using SharedDeck.Core.Installation;

namespace SharedDeck.Setup;

public partial class MainWindow : Window
{
    private readonly bool uninstallMode;
    private string installedApp = "";

    public MainWindow()
    {
        InitializeComponent();
        uninstallMode = Environment.GetCommandLineArgs().Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

        if (uninstallMode)
        {
            Title = "SharedDeck entfernen";
            Form.Visibility = Visibility.Collapsed;
            InstallButton.Content = "Entfernen";
            Log("SharedDeck-App, Verknüpfungen und das SharedDeck-Modul werden entfernt. Das Flugzeug bleibt installiert.");
            return;
        }

        InstallDirBox.Text = Installer.DefaultInstallDirectory;
        var community = Installer.FindCommunityFolder();
        CommunityBox.Text = community ?? "";
        CommunityHint.Text = community != null
            ? "Automatisch gefunden."
            : "Nicht automatisch gefunden. Bitte den Community-Ordner von MSFS 2024 auswählen (Ordner „Community“ im Paketordner).";

        var besideSetup = Path.Combine(AppContext.BaseDirectory, "SharedDeck-A32NX.zip");
        var besideProcess = Environment.ProcessPath is { } process
            ? Path.Combine(Path.GetDirectoryName(process)!, "SharedDeck-A32NX.zip")
            : besideSetup;
        AircraftBox.Text = File.Exists(besideProcess) ? besideProcess : "";
        if (Installer.IsInstalled())
        {
            Log("SharedDeck ist bereits installiert – die Installation aktualisiert es.");
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
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        Form.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        try
        {
            if (uninstallMode)
            {
                await Task.Run(() => Installer.Uninstall(progress));
                return;
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
            };
            await Task.Run(() =>
            {
                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                    ?? throw new InvalidOperationException("Dieses Setup enthält keine Programmdateien (Entwickler-Build).");
                Installer.Install(payload, options, progress);
            });
            installedApp = Path.Combine(options.InstallDirectory, Installer.AppExecutable);
            StartButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log($"FEHLER: {ex.Message}");
            InstallButton.IsEnabled = true;
            Form.IsEnabled = true;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
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
    }
}
