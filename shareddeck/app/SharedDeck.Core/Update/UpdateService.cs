using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharedDeck.Core.Update;

/// <summary>A downloadable file of a release, with the checksum from the release manifest.</summary>
public sealed record ReleaseFile(string Name, long Size, string Sha256, string DownloadUrl);

/// <summary>A newer release than the running app.</summary>
public sealed record AvailableUpdate(
    Version Version,
    string Notes,
    string PageUrl,
    int AircraftRevision,
    ReleaseFile Setup,
    IReadOnlyList<ReleaseFile> AircraftParts)
{
    /// <summary>Bytes to download; the aircraft only when the installed one is older.</summary>
    public long DownloadSize(bool withAircraft) => Setup.Size + (withAircraft ? AircraftParts.Sum(p => p.Size) : 0);
}

/// <summary>The files of an update on disk, ready for the setup.</summary>
public sealed record DownloadedUpdate(Version Version, string SetupPath, string? AircraftPath);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds new SharedDeck releases on GitHub and downloads them. Every release carries a manifest
/// (<see cref="ManifestName"/>) with the version, the aircraft revision and the SHA-256 of each file.
/// </summary>
public sealed class UpdateService
{
    public const string DefaultRepository = "moritzwegner/SharedDeck";
    public const string ManifestName = "shareddeck-release.json";
    public const string SetupName = "SharedDeck-Setup.exe";

    private readonly HttpClient http;
    private readonly string repository;
    private readonly Version currentVersion;
    private readonly string apiBase;

    /// <param name="apiBase">GitHub API; a local server for testing the update end to end</param>
    public UpdateService(HttpClient http, Version currentVersion, string repository = DefaultRepository,
        string apiBase = "https://api.github.com")
    {
        this.http = http;
        this.currentVersion = currentVersion;
        this.repository = repository;
        this.apiBase = apiBase.TrimEnd('/');
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SharedDeck", "1.0"));
        return client;
    }

    /// <returns>the newest release above the running version, or null if the app is up to date</returns>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken token = default)
    {
        JsonArray releases;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/repos/{repository}/releases?per_page=30"))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new UpdateException("GitHub ist nicht erreichbar (Internetverbindung?).", e);
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new UpdateException($"Die Releases von {repository} sind nicht öffentlich erreichbar. " +
                                              "Updates funktionieren nur, wenn das GitHub-Repo öffentlich ist.");
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new UpdateException("GitHub hat die Anfrage vorübergehend begrenzt. Später erneut versuchen.");
                }
                response.EnsureSuccessStatusCode();
                releases = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonArray ?? new JsonArray();
            }
        }

        var candidates = releases
            .OfType<JsonObject>()
            .Where(r => r["draft"]?.GetValue<bool>() != true)
            .Select(r => (Release: r, Version: ParseVersion(r["tag_name"]?.GetValue<string>())))
            .Where(c => c.Version != null && c.Version > currentVersion)
            .OrderByDescending(c => c.Version);

        foreach (var (release, version) in candidates)
        {
            var assets = (release["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .ToDictionary(a => a["name"]!.GetValue<string>(), a => a["browser_download_url"]!.GetValue<string>());
            if (!assets.TryGetValue(ManifestName, out var manifestUrl))
            {
                // releases before the updater have no manifest
                continue;
            }
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await http.GetStringAsync(manifestUrl, token))
                           ?? throw new UpdateException($"Das Manifest von {release["tag_name"]} ist ungültig.");

            ReleaseFile File(ManifestFile file) => assets.TryGetValue(file.Name, out var url)
                ? new ReleaseFile(file.Name, file.Size, file.Sha256, url)
                : throw new UpdateException($"Im Release {release["tag_name"]} fehlt die Datei {file.Name}.");

            var setup = manifest.Files.FirstOrDefault(f => f.Name == SetupName)
                        ?? throw new UpdateException($"Im Release {release["tag_name"]} fehlt das Setup.");
            var aircraft = manifest.Files.Where(f => f.Name.StartsWith("SharedDeck-A32NX", StringComparison.Ordinal))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(File).ToList();
            return new AvailableUpdate(version!, release["body"]?.GetValue<string>() ?? "",
                release["html_url"]?.GetValue<string>() ?? "", manifest.AircraftRevision, File(setup), aircraft);
        }
        return null;
    }

    /// <summary>
    /// Downloads the update into <paramref name="directory"/>. Files that are already there with the right
    /// checksum are kept, so an interrupted download continues where it stopped.
    /// </summary>
    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update, bool withAircraft, string directory,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var files = new List<ReleaseFile> { update.Setup };
        if (withAircraft)
        {
            files.AddRange(update.AircraftParts);
        }
        var total = files.Sum(f => f.Size);
        long done = 0;

        foreach (var file in files)
        {
            var target = Path.Combine(directory, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size && await Sha256Async(target, token) == file.Sha256)
            {
                done += file.Size;
                progress?.Report((done, total));
                continue;
            }

            var partial = target + ".download";
            var before = done;
            using (var response = await http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(partial);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            if (await Sha256Async(partial, token) != file.Sha256)
            {
                File.Delete(partial);
                throw new UpdateException($"{file.Name} ist beim Herunterladen beschädigt worden (Prüfsumme falsch). Bitte erneut versuchen.");
            }
            File.Move(partial, target, overwrite: true);
            done = before + file.Size;
        }

        return new DownloadedUpdate(update.Version, Path.Combine(directory, update.Setup.Name),
            withAircraft && update.AircraftParts.Count > 0 ? Path.Combine(directory, update.AircraftParts[0].Name) : null);
    }

    /// <summary>Starts the downloaded setup; it waits for this app to exit, installs and starts the new version.</summary>
    public static ProcessStartInfo SetupStartInfo(DownloadedUpdate update, int appProcessId, string? communityFolder, string logFile)
    {
        var info = new ProcessStartInfo(update.SetupPath) { UseShellExecute = false };
        foreach (var argument in SetupArguments(update, appProcessId, communityFolder, logFile))
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    public static IReadOnlyList<string> SetupArguments(DownloadedUpdate update, int appProcessId, string? communityFolder, string logFile)
    {
        var arguments = new List<string> { "/auto", "/restart", $"/waitpid:{appProcessId}", $"/log:{logFile}" };
        arguments.Add(update.AircraftPath != null ? $"/aircraft:{update.AircraftPath}" : "/noaircraft");
        if (communityFolder != null)
        {
            arguments.Add($"/community:{communityFolder}");
        }
        return arguments;
    }

    public static Version? ParseVersion(string? tag)
    {
        if (tag == null)
        {
            return null;
        }
        return Version.TryParse(tag.TrimStart('v', 'V'), out var version) ? version : null;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private sealed class ReleaseManifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("aircraftRevision")] public int AircraftRevision { get; set; }
        [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }
}
