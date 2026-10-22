using System.IO.Compression;
using System.Runtime.Versioning;
using SharedDeck.Core.Installation;

namespace SharedDeck.Tests;

[SupportedOSPlatform("windows")]
public sealed class InstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shareddeck-test-" + Guid.NewGuid().ToString("N"));
    private readonly Progress<string> log = new();

    public InstallerTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static MemoryStream Payload()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "app/", null);
            Write(zip, "app/SharedDeck.App.exe", "exe");
            Write(zip, "module/shareddeck-module/manifest.json", "{}");
            Write(zip, "module/shareddeck-module/modules/shareddeck-module.wasm", "wasm");
        }
        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive zip, string name, string? content)
    {
        var entry = zip.CreateEntry(name);
        if (content != null)
        {
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    [Fact]
    public void InstallsAppAndModule()
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Packages", "Community")).FullName;
        var install = Path.Combine(root, "Programs", "SharedDeck");
        // an old module is replaced completely
        Directory.CreateDirectory(Path.Combine(community, "shareddeck-module", "old"));

        Installer.Install(Payload(), new InstallOptions
        {
            InstallDirectory = install, CommunityFolder = community, RegisterWithWindows = false,
        }, log);

        Assert.Equal("exe", File.ReadAllText(Path.Combine(install, "SharedDeck.App.exe")));
        Assert.True(File.Exists(Path.Combine(community, "shareddeck-module", "modules", "shareddeck-module.wasm")));
        Assert.False(Directory.Exists(Path.Combine(community, "shareddeck-module", "old")));
    }

    [Fact]
    public void AircraftReplacesExistingPackageWithBackup()
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Packages", "Community")).FullName;
        var existing = Directory.CreateDirectory(Path.Combine(community, "flybywire-aircraft-a320-neo")).FullName;
        File.WriteAllText(Path.Combine(existing, "marker.txt"), "original");

        var aircraftZip = Path.Combine(root, "aircraft.zip");
        using (var zip = ZipFile.Open(aircraftZip, ZipArchiveMode.Create))
        {
            Write(zip, "flybywire-aircraft-a320-neo/layout.json", "{}");
            Write(zip, "flybywire-aircraft-a320-neo-lock-highlight/layout.json", "{}");
        }

        Installer.InstallAircraft(aircraftZip, community, log);

        Assert.True(File.Exists(Path.Combine(community, "flybywire-aircraft-a320-neo", "layout.json")));
        Assert.False(File.Exists(Path.Combine(community, "flybywire-aircraft-a320-neo", "marker.txt")));
        var backup = Directory.GetDirectories(Path.Combine(root, "Packages", "SharedDeck-Backup")).Single();
        Assert.Equal("original", File.ReadAllText(Path.Combine(backup, "flybywire-aircraft-a320-neo", "marker.txt")));
    }

    [Fact]
    public void AcceptsArchivesWithBackslashPaths()
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
        var aircraftZip = Path.Combine(root, "backslash.zip");
        using (var zip = ZipFile.Open(aircraftZip, ZipArchiveMode.Create))
        {
            // as written by Windows PowerShell 5.1 (Compress-Archive / .NET Framework)
            Write(zip, "flybywire-aircraft-a320-neo\\SimObjects\\", null);
            Write(zip, "flybywire-aircraft-a320-neo\\SimObjects\\aircraft.cfg", "cfg");
        }

        Installer.InstallAircraft(aircraftZip, community, log);

        Assert.Equal("cfg", File.ReadAllText(Path.Combine(community, "flybywire-aircraft-a320-neo", "SimObjects", "aircraft.cfg")));
    }

    [Fact]
    public void RejectsForeignAndMaliciousArchives()
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
        var foreign = Path.Combine(root, "foreign.zip");
        using (var zip = ZipFile.Open(foreign, ZipArchiveMode.Create))
        {
            Write(zip, "some-other-addon/layout.json", "{}");
        }
        Assert.Throws<InvalidDataException>(() => Installer.InstallAircraft(foreign, community, log));

        var malicious = Path.Combine(root, "malicious.zip");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            Write(zip, "flybywire-aircraft-a320-neo/../../evil.txt", "x");
        }
        Assert.Throws<InvalidDataException>(() => Installer.InstallAircraft(malicious, community, log));
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
    }

    [Theory]
    [InlineData("InstalledPackagesPath \"D:\\MSFS2024\\Packages\"\nOther 1", "D:\\MSFS2024\\Packages")]
    [InlineData("Version 1\r\n  InstalledPackagesPath \"C:\\Users\\x\\AppData\\Local\\Packages\"\r\n", "C:\\Users\\x\\AppData\\Local\\Packages")]
    [InlineData("nothing here", null)]
    public void ReadsPackagesPathFromUserCfg(string content, string? expected) =>
        Assert.Equal(expected, Installer.ReadInstalledPackagesPath(content));
}
