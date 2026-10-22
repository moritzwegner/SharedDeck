using System.IO;
using System.Reflection;
using System.Windows;
using SharedDeck.Core.Installation;

namespace SharedDeck.Setup;

public partial class App : Application
{
    /// <summary>
    /// Without arguments the setup window opens. Unattended installation:
    /// SharedDeck-Setup.exe /silent [/dir:&lt;folder&gt;] [/community:&lt;folder&gt;] [/aircraft:&lt;zip&gt;] [/noregister] [/log:&lt;file&gt;]
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
        {
            new MainWindow().Show();
            return;
        }

        string? Arg(string name) => e.Args
            .Where(a => a.StartsWith($"/{name}:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a[(name.Length + 2)..])
            .FirstOrDefault();

        var logFile = Arg("log");
        var lines = new List<string>();
        var log = new SynchronousProgress(line => lines.Add(line));
        var exitCode = 0;
        try
        {
            var community = Arg("community") ?? Installer.FindCommunityFolder();
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                ?? throw new InvalidOperationException("Dieses Setup enthält keine Programmdateien.");
            Installer.Install(payload, new InstallOptions
            {
                InstallDirectory = Arg("dir") ?? Installer.DefaultInstallDirectory,
                CommunityFolder = community,
                AircraftZip = Arg("aircraft"),
                RegisterWithWindows = !e.Args.Any(a => a.Equals("/noregister", StringComparison.OrdinalIgnoreCase)),
            }, log);
        }
        catch (Exception ex)
        {
            lines.Add($"FEHLER: {ex.Message}");
            exitCode = 1;
        }
        if (logFile != null)
        {
            File.WriteAllLines(logFile, lines);
        }
        Shutdown(exitCode);
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
