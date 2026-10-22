using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SharedDeck.Core.Installation;

public sealed class InstallOptions
{
    public required string InstallDirectory { get; init; }
    /// <summary>MSFS 2024 Community folder; null skips everything that goes into the simulator.</summary>
    public string? CommunityFolder { get; init; }
    public bool InstallModule { get; init; } = true;
    /// <summary>ZIP with the SharedDeck A32NX packages (flybywire-aircraft-a320-neo...), optional.</summary>
    public string? AircraftZip { get; init; }
    public bool DesktopShortcut { get; init; } = true;
    /// <summary>Start menu, desktop and "Apps &amp; Features" entries (off in tests).</summary>
    public bool RegisterWithWindows { get; init; } = true;
}

/// <summary>
/// Installs SharedDeck from the payload embedded in the setup: the app, the SharedDeck module and
/// optionally the SharedDeck A32NX. Packages already in the Community folder are moved to a backup,
/// never deleted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string AppExecutable = "SharedDeck.App.exe";
    public const string UninstallerName = "SharedDeck-Uninstall.exe";
    public const string ModulePackage = "shareddeck-module";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SharedDeck";

    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SharedDeck");

    /// <summary>Finds the Community folder of MSFS 2024 (Microsoft Store or Steam) from UserCfg.opt.</summary>
    public static string? FindCommunityFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        };
        foreach (var userCfg in candidates.Where(File.Exists))
        {
            var packages = ReadInstalledPackagesPath(File.ReadAllText(userCfg));
            if (packages != null)
            {
                var community = Path.Combine(packages, "Community");
                if (Directory.Exists(community))
                {
                    return community;
                }
            }
        }
        return null;
    }

    public static string? ReadInstalledPackagesPath(string userCfg)
    {
        var match = Regex.Match(userCfg, "^\\s*InstalledPackagesPath\\s+\"([^\"]+)\"", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <param name="payload">ZIP with "app/..." (the app) and "module/shareddeck-module/..." (the module)</param>
    public static void Install(Stream payload, InstallOptions options, IProgress<string> log, CancellationToken token = default)
    {
        Directory.CreateDirectory(options.InstallDirectory);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);

        log.Report("Installiere die SharedDeck-App …");
        ExtractPrefix(archive, "app/", options.InstallDirectory, token);

        if (options.CommunityFolder != null && options.InstallModule)
        {
            log.Report("Installiere das SharedDeck-Modul in den Community-Ordner …");
            var target = Path.Combine(options.CommunityFolder, ModulePackage);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            ExtractPrefix(archive, $"module/{ModulePackage}/", target, token);
        }

        if (options.CommunityFolder != null && !string.IsNullOrEmpty(options.AircraftZip))
        {
            InstallAircraft(options.AircraftZip, options.CommunityFolder, log, token);
        }

        if (options.RegisterWithWindows)
        {
            Register(options, log);
        }
        log.Report("Fertig.");
    }

    /// <summary>
    /// All parts of a split aircraft archive ("Name-part1of3.zip" … "Name-part3of3.zip", each a complete ZIP),
    /// given any one of them; a normal ZIP is returned as is.
    /// </summary>
    public static IReadOnlyList<string> FindAircraftParts(string zipPath)
    {
        var match = Regex.Match(Path.GetFileName(zipPath), @"^(?<base>.+)-part(?<n>\d+)of(?<total>\d+)\.zip$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return [zipPath];
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        var total = int.Parse(match.Groups["total"].Value);
        var parts = Enumerable.Range(1, total)
            .Select(n => Path.Combine(directory, $"{match.Groups["base"].Value}-part{n}of{total}.zip"))
            .ToList();
        var missing = parts.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Es fehlen Teile des Flugzeugs: {string.Join(", ", missing)}. Alle Teile müssen im selben Ordner liegen.");
        }
        return parts;
    }

    /// <summary>Extracts the aircraft (all parts); packages of the same name are moved to a backup folder first.</summary>
    public static void InstallAircraft(string zipPath, string communityFolder, IProgress<string> log, CancellationToken token = default)
    {
        var zips = FindAircraftParts(zipPath).Select(ZipFile.OpenRead).ToList();
        try
        {
            InstallAircraft(zips, communityFolder, log, token);
        }
        finally
        {
            zips.ForEach(z => z.Dispose());
        }
    }

    private static void InstallAircraft(List<ZipArchive> zips, string communityFolder, IProgress<string> log, CancellationToken token)
    {
        var entries = zips.SelectMany(z => z.Entries).ToList();
        var packages = entries
            .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        if (packages.Count == 0 || packages.Any(p => !p.StartsWith("flybywire-aircraft-a320-neo", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Die ZIP-Datei enthält nicht das SharedDeck-A32NX-Paket (flybywire-aircraft-a320-neo).");
        }

        // a renamed folder inside Community would still be loaded, so the backup lives next to it
        var backup = Path.Combine(Path.GetDirectoryName(communityFolder.TrimEnd('\\', '/'))!, "SharedDeck-Backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var package in packages)
        {
            var existing = Path.Combine(communityFolder, package);
            if (Directory.Exists(existing))
            {
                Directory.CreateDirectory(backup);
                log.Report($"Sichere vorhandenes Paket {package} nach {backup} …");
                Directory.Move(existing, Path.Combine(backup, package));
            }
        }

        var total = entries.Count;
        var done = 0;
        log.Report($"Entpacke das Flugzeug ({entries.Sum(e => e.Length) / 1024 / 1024} MB aus {zips.Count} Datei(en)) …");
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            ExtractEntry(entry, communityFolder, entry.FullName);
            if (++done % 500 == 0)
            {
                log.Report($"Flugzeug: {done * 100 / total} %");
            }
        }
    }

    private static void ExtractPrefix(ZipArchive archive, string prefix, string targetDirectory, CancellationToken token)
    {
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            ExtractEntry(entry, targetDirectory, entry.FullName[prefix.Length..]);
        }
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string root, string relativePath)
    {
        // archives made by Windows PowerShell 5.1 use backslashes
        relativePath = relativePath.Replace('\\', '/');
        if (relativePath.Length == 0)
        {
            return;
        }
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Ungültiger Pfad im Archiv: {entry.FullName}");
        }
        if (relativePath.EndsWith('/'))
        {
            Directory.CreateDirectory(full);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        entry.ExtractToFile(full, overwrite: true);
    }

    private static void Register(InstallOptions options, IProgress<string> log)
    {
        log.Report("Lege Verknüpfungen und den Eintrag unter „Apps“ an …");
        var app = Path.Combine(options.InstallDirectory, AppExecutable);

        // the setup itself becomes the uninstaller
        var uninstaller = Path.Combine(options.InstallDirectory, UninstallerName);
        var self = Environment.ProcessPath;
        if (self != null && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, uninstaller, overwrite: true);
        }

        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SharedDeck.lnk");
        CreateShortcut(startMenu, app, options.InstallDirectory);
        if (options.DesktopShortcut)
        {
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SharedDeck.lnk"), app,
                options.InstallDirectory);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "SharedDeck – Shared Cockpit für MSFS 2024");
        key.SetValue("DisplayVersion", "0.1.1");
        key.SetValue("Publisher", "SharedDeck");
        key.SetValue("DisplayIcon", app);
        key.SetValue("InstallLocation", options.InstallDirectory);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (options.CommunityFolder != null && options.InstallModule)
        {
            key.SetValue("ModulePath", Path.Combine(options.CommunityFolder, ModulePackage));
        }
    }

    private static void CreateShortcut(string shortcutPath, string target, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell fehlt");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = "SharedDeck – gemeinsam im Cockpit fliegen";
        shortcut.Save();
    }

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key != null;
    }

    /// <summary>Removes app, shortcuts, the module and the Windows entry. The aircraft stays installed.</summary>
    public static void Uninstall(IProgress<string> log)
    {
        string? installDirectory;
        string? modulePath;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            installDirectory = key?.GetValue("InstallLocation") as string;
            modulePath = key?.GetValue("ModulePath") as string;
        }

        if (modulePath != null && Directory.Exists(modulePath))
        {
            log.Report("Entferne das SharedDeck-Modul …");
            Directory.Delete(modulePath, recursive: true);
        }
        foreach (var shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SharedDeck.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SharedDeck.lnk"),
                 })
        {
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (installDirectory != null && Directory.Exists(installDirectory))
        {
            log.Report("Entferne die App …");
            // the running uninstaller lives in this folder: delete it once this process has exited
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{installDirectory}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        log.Report("SharedDeck wurde entfernt. Das Flugzeug bleibt installiert.");
    }
}
