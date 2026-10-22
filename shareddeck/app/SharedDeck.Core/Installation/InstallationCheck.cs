using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace SharedDeck.Core.Installation;

/// <summary>What is installed in the simulator's Community folder.</summary>
public sealed record InstallationStatus(
    string? CommunityFolder,
    bool ModuleInstalled,
    bool AircraftInstalled,
    bool AircraftIsSharedDeck,
    string AircraftVersion)
{
    public bool IsComplete => ModuleInstalled && AircraftIsSharedDeck;

    /// <summary>Problems in plain words, empty if everything is in place.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();
            if (CommunityFolder == null)
            {
                problems.Add("Community-Ordner von MSFS 2024 nicht gefunden");
                return problems;
            }
            if (!ModuleInstalled)
            {
                problems.Add("SharedDeck-Modul fehlt im Community-Ordner");
            }
            if (!AircraftInstalled)
            {
                problems.Add("A32NX fehlt im Community-Ordner");
            }
            else if (!AircraftIsSharedDeck)
            {
                problems.Add($"Im Community-Ordner liegt der normale FlyByWire A32NX ({AircraftVersion}), nicht der SharedDeck-A32NX");
            }
            return problems;
        }
    }
}

[SupportedOSPlatform("windows")]
public static class InstallationCheck
{
    private const string AircraftPackage = "flybywire-aircraft-a320-neo";
    /// <summary>Only the SharedDeck build of the extras-host contains the bridge.</summary>
    private const string BridgeMarker = "SHAREDDECK_FROM_WASM";

    public static InstallationStatus Run() => Run(Installer.FindCommunityFolder());

    public static InstallationStatus Run(string? communityFolder)
    {
        if (communityFolder == null)
        {
            return new InstallationStatus(null, false, false, false, "");
        }

        var module = File.Exists(Path.Combine(communityFolder, Installer.ModulePackage, "modules", "shareddeck-module.wasm"));
        var aircraft = Path.Combine(communityFolder, AircraftPackage);
        var aircraftInstalled = Directory.Exists(aircraft);

        var version = "";
        var buildInfo = Path.Combine(aircraft, "a32nx_build_info.json");
        if (File.Exists(buildInfo))
        {
            try
            {
                version = JsonNode.Parse(File.ReadAllText(buildInfo))?["version"]?.GetValue<string>() ?? "";
            }
            catch (Exception)
            {
                version = "unbekannt";
            }
        }

        var extrasHost = Path.Combine(aircraft, "html_ui", "Pages", "VCockpit", "Instruments", "A32NX", "ExtrasHost", "index.js");
        var isSharedDeck = File.Exists(extrasHost) && File.ReadAllText(extrasHost).Contains(BridgeMarker, StringComparison.Ordinal);

        return new InstallationStatus(communityFolder, module, aircraftInstalled, isSharedDeck, version);
    }
}
