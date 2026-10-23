using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SharedDeck.Core;
using SharedDeck.Core.Installation;
using SharedDeck.Core.Transport;

namespace SharedDeck.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;

    private static readonly string ConfigDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharedDeck");
    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "shareddeck.json");
    public static readonly string LogDirectory = Path.Combine(ConfigDirectory, "logs");
    /// <summary>The version from the project file, e.g. "0.2.1".</summary>
    private static readonly string AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";

    private readonly SharedDeckConfig config;
    private readonly BridgeServer bridge;
    private readonly SessionController session;
    private InstallationStatus installation = new(null, false, false, false, "");

    public MainWindow()
    {
        InitializeComponent();
        Title = $"SharedDeck {AppVersion} – Shared Cockpit";

        Directory.CreateDirectory(ConfigDirectory);
        WriteLogFile($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} SharedDeck {AppVersion} gestartet =====");
        config = SharedDeckConfig.Load(ConfigPath);
        AddressBox.Text = config.LastHostAddress;
        StrictBox.IsChecked = config.Strict;

        bridge = new BridgeServer(config.BridgePort);
        session = new SessionController(config, bridge, () => new PeerLink());
        session.StatusChanged += () => Dispatcher.BeginInvoke(Render);
        session.LogMessage += line => Dispatcher.BeginInvoke(() => AddLog(line));
        session.AuditCompleted += differences => Dispatcher.BeginInvoke(() => ShowAudit(differences));

        try
        {
            bridge.Start();
            AddLog($"Warte auf den Simulator (Bridge-Port {config.BridgePort}) …");
        }
        catch (Exception e)
        {
            AddLog($"Bridge-Port {config.BridgePort} ist belegt – läuft SharedDeck schon? Bitte nur eine SharedDeck-App pro PC öffnen. ({e.Message})");
        }

        Closed += (_, _) =>
        {
            session.Stop();
            session.Dispose();
            bridge.Dispose();
        };
        CheckInstallation();
        Render();
        StartUpdateCheck();
    }

    /// <summary>Is the SharedDeck module and the SharedDeck A32NX in the simulator? Without them the bridge never connects.</summary>
    private void CheckInstallation()
    {
        try
        {
            installation = InstallationCheck.Run();
        }
        catch (Exception e)
        {
            AddLog($"Installationsprüfung fehlgeschlagen: {e.Message}");
            return;
        }
        AddLog($"Community-Ordner: {installation.CommunityFolder ?? "nicht gefunden"}");
        if (installation.IsComplete)
        {
            AddLog($"Installation in Ordnung: SharedDeck-Modul und SharedDeck-A32NX ({installation.AircraftVersion})");
        }
        foreach (var problem in installation.Problems)
        {
            AddLog($"INSTALLATION: {problem}");
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    /// <summary>Every log line also goes into a file per day, so it can be sent after a failed attempt.</summary>
    public static void WriteLogFile(string line)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, $"shareddeck-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
        catch (IOException)
        {
            // logging must never stop the app
        }
    }

    private void OnHost(object sender, RoutedEventArgs e)
    {
        config.Strict = StrictBox.IsChecked == true;
        config.Save(ConfigPath);
        try
        {
            session.StartHost();
        }
        catch (Exception ex)
        {
            AddLog($"Host konnte nicht gestartet werden: {ex.Message}");
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0)
        {
            return;
        }
        config.LastHostAddress = address;
        config.Save(ConfigPath);
        await session.ConnectAsync(address);
    }

    private void OnStop(object sender, RoutedEventArgs e) => session.Stop();

    private void OnResume(object sender, RoutedEventArgs e) => session.Resume();

    private void OnAudit(object sender, RoutedEventArgs e) => session.RunAudit();

    private void OnAdopt(object sender, RoutedEventArgs e)
    {
        var names = session.ContestedVariables;
        if (names.Count == 0)
        {
            AddLog("Keine lokal beschriebenen Variablen gemeldet.");
            return;
        }
        var answer = MessageBox.Show(this,
            "Diese Variablen dürfen künftig auf beiden PCs verschieden sein:\n\n" + string.Join("\n", names.Take(30)) +
            (names.Count > 30 ? $"\n… und {names.Count - 30} weitere" : "") +
            "\n\nNur freigeben, was wirklich pro Pilot lokal ist (z. B. Anzeigen, Kamera). Übernehmen?",
            "Lokale Variablen freigeben", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        foreach (var name in names.Where(n => !config.LocalAllow.Contains(n)))
        {
            config.LocalAllow.Add(name);
        }
        config.Save(ConfigPath);
        AddLog($"{names.Count} Variablen freigegeben. Wirksam ab der nächsten Verbindung.");
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });

    private void Render()
    {
        var status = session.Status;

        (string title, Color color) = status.State switch
        {
            SessionState.Idle => ("Nicht verbunden", Color.FromRgb(0x5F, 0x6B, 0x7A)),
            SessionState.WaitingForCoPilot => ("Host bereit – warte auf Co-Pilot", Color.FromRgb(0x25, 0x63, 0xEB)),
            SessionState.Connecting => ("Verbinde …", Color.FromRgb(0x25, 0x63, 0xEB)),
            SessionState.Synchronizing => ("Synchronisiere …", Color.FromRgb(0xB4, 0x53, 0x09)),
            SessionState.Live => (status.Role == SessionRole.Host ? "Synchron – du bist Host" : "Synchron – du bist Co-Pilot", Color.FromRgb(0x15, 0x80, 0x3D)),
            SessionState.Halted => ("NOT-HALT – beide Simulatoren pausiert", Color.FromRgb(0xB9, 0x1C, 0x1C)),
            SessionState.LinkLost => ("Verbindung verloren – pausiert", Color.FromRgb(0xB9, 0x1C, 0x1C)),
            _ => (status.State.ToString(), Colors.Gray),
        };
        var reason = status.Reason;
        // without its simulator the host rejects every co-pilot, so it must not look "ready"
        if (!status.BridgeConnected && status.State is SessionState.WaitingForCoPilot or SessionState.Connecting)
        {
            title = status.Role == SessionRole.Host ? "Host gestartet – aber dein Simulator ist nicht verbunden" : "Dein Simulator ist nicht verbunden";
            color = Color.FromRgb(0xB4, 0x53, 0x09);
            reason = "Der Co-Pilot wird abgelehnt, bis die App mit deinem Simulator verbunden ist: MSFS starten und einen Flug " +
                     "mit dem SharedDeck-A32NX laden. Unter „Simulator“ muss „verbunden“ stehen.";
        }
        StateText.Text = title;
        ReasonText.Text = reason;
        Banner.Background = new SolidColorBrush(color);

        InstallText.Text = installation.CommunityFolder == null ? "Community-Ordner nicht gefunden"
            : installation.IsComplete ? $"in Ordnung ({installation.AircraftVersion})"
            : "unvollständig";
        InstallWarning.Visibility = installation.IsComplete ? Visibility.Collapsed : Visibility.Visible;
        InstallWarningText.Text = string.Join("\n", installation.Problems.Select(p => "• " + p));

        BridgeText.Text = status.BridgeConnected ? $"verbunden ({status.Aircraft})" : "nicht verbunden";
        PeerText.Text = status.PeerConnected ? "verbunden" : "–";
        RttText.Text = status.PeerConnected ? $"{status.RttMs:0} ms" : "–";
        FpsText.Text = $"{status.FramesPerSecond:0.0}";
        RateText.Text = $"{status.KilobytesPerSecondOut:0} / {status.KilobytesPerSecondIn:0} KB/s";
        LVarText.Text = status.LVarCount.ToString();
        VerifyText.Text = status.State is SessionState.Live or SessionState.Halted
            ? status.LastVerifyOk ? "identisch" : "Abweichung"
            : "–";
        InputsText.Text = status.InputsSent.ToString();

        LeversText.Text = status.LeverOwners.Length > 0 ? status.LeverOwners : "–";
        // weather can't be synchronized: shown below the real deviations as a hint
        MismatchList.ItemsSource = status.Mismatches.Concat(status.Hints).ToList();
        ContestedList.ItemsSource = status.Contested;

        var idle = status.Role == SessionRole.None;
        HostButton.IsEnabled = idle;
        ConnectButton.IsEnabled = idle || status.State == SessionState.LinkLost;
        StopButton.IsEnabled = !idle;
        ResumeButton.IsEnabled = status.State == SessionState.Halted;
        AuditButton.IsEnabled = status.Role == SessionRole.Client && status.PeerConnected;
        AdoptButton.IsEnabled = status.Role == SessionRole.Host && status.Contested.Count > 0;
    }

    private void ShowAudit(IReadOnlyList<AuditDifference> differences)
    {
        if (differences.Count == 0)
        {
            MessageBox.Show(this, "Alle geprüften Simulator-Variablen sind identisch.", "A:Var-Audit");
            return;
        }
        var lines = differences.Select(d => $"{d.Variable}: Host {d.Host?.ToString("G6") ?? "?"}, hier {d.Client?.ToString("G6") ?? "?"}");
        MessageBox.Show(this, string.Join("\n", lines.Take(40)) + (differences.Count > 40 ? $"\n… und {differences.Count - 40} weitere" : ""),
            $"A:Var-Audit: {differences.Count} Abweichungen");
    }

    private void AddLog(string line)
    {
        // lines of the session already start with the time
        if (!System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d\d:\d\d:\d\d "))
        {
            line = $"{DateTime.Now:HH:mm:ss} {line}";
        }
        WriteLogFile(line);
        LogList.Items.Add(line);
        while (LogList.Items.Count > MaxLogLines)
        {
            LogList.Items.RemoveAt(0);
        }
        LogList.ScrollIntoView(line);
    }
}
