using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharedDeck.Core.Update;

namespace SharedDeck.Tests;

/// <summary>Answers like the GitHub API and the release download URLs.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public readonly Dictionary<string, byte[]> Files = new();
    public HttpStatusCode ReleasesStatus = HttpStatusCode.OK;
    public readonly JsonArray Releases = new();
    public readonly List<string> Requested = new();

    public void AddRelease(string tag, int aircraftRevision, Dictionary<string, byte[]> files, bool withManifest = true, bool draft = false,
        Func<string, byte[], string>? sha = null)
    {
        var assets = new JsonArray();
        foreach (var (name, content) in files)
        {
            var url = $"https://github.example/{tag}/{name}";
            Files[url] = content;
            assets.Add(new JsonObject { ["name"] = name, ["browser_download_url"] = url });
        }
        if (withManifest)
        {
            var manifest = new JsonObject
            {
                ["version"] = tag.TrimStart('v'),
                ["aircraftRevision"] = aircraftRevision,
                ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Key, ["size"] = f.Value.Length,
                    ["sha256"] = sha?.Invoke(f.Key, f.Value) ?? Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant(),
                }).ToArray()),
            };
            var url = $"https://github.example/{tag}/{UpdateService.ManifestName}";
            Files[url] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            assets.Add(new JsonObject { ["name"] = UpdateService.ManifestName, ["browser_download_url"] = url });
        }
        Releases.Add(new JsonObject
        {
            ["tag_name"] = tag, ["draft"] = draft, ["body"] = $"Notes {tag}", ["html_url"] = $"https://github.example/{tag}",
            ["assets"] = assets,
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(ReleasesStatus) { Content = new StringContent(Releases.ToJsonString()) });
        }
        return Task.FromResult(Files.TryGetValue(url, out var content)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly FakeGitHub github = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "shareddeck-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private UpdateService Service(string current = "0.2.1") => new(new HttpClient(github), Version.Parse(current));

    private static Dictionary<string, byte[]> Files(string version) => new()
    {
        [UpdateService.SetupName] = Encoding.UTF8.GetBytes($"setup {version}"),
        ["SharedDeck-A32NX-part1of2.zip"] = Encoding.UTF8.GetBytes(new string('a', 5000)),
        ["SharedDeck-A32NX-part2of2.zip"] = Encoding.UTF8.GetBytes(new string('b', 3000)),
    };

    [Fact]
    public async Task FindsNewestReleaseWithManifest()
    {
        github.AddRelease("v0.2.0", 2, Files("0.2.0"));
        github.AddRelease("v0.3.1", 3, Files("0.3.1"), draft: true);
        github.AddRelease("v0.3.0", 3, Files("0.3.0"));
        github.AddRelease("v0.4.0", 3, Files("0.4.0"), withManifest: false);

        var update = await Service().CheckAsync();

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update!.Version);
        Assert.Equal(3, update.AircraftRevision);
        Assert.Equal(new[] { "SharedDeck-A32NX-part1of2.zip", "SharedDeck-A32NX-part2of2.zip" }, update.AircraftParts.Select(p => p.Name));
        Assert.Equal("Notes v0.3.0", update.Notes);
    }

    [Fact]
    public async Task NoUpdateWhenCurrent()
    {
        github.AddRelease("v0.2.1", 2, Files("0.2.1"));
        Assert.Null(await Service("0.2.1").CheckAsync());
    }

    [Fact]
    public async Task PrivateRepositoryIsExplained()
    {
        github.ReleasesStatus = HttpStatusCode.NotFound;
        var error = await Assert.ThrowsAsync<UpdateException>(() => Service().CheckAsync());
        Assert.Contains("öffentlich", error.Message);
    }

    [Fact]
    public async Task DownloadsOnlySetupWithoutAircraftAndVerifies()
    {
        github.AddRelease("v0.3.0", 3, Files("0.3.0"));
        var update = (await Service().CheckAsync())!;
        var reports = new List<(long Done, long Total)>();

        var downloaded = await Service().DownloadAsync(update, withAircraft: false, directory, new SynchronousProgress(reports.Add));

        Assert.Equal("setup 0.3.0", File.ReadAllText(downloaded.SetupPath));
        Assert.Null(downloaded.AircraftPath);
        Assert.False(File.Exists(Path.Combine(directory, "SharedDeck-A32NX-part1of2.zip")));
        Assert.Equal(update.Setup.Size, reports[^1].Done);
        Assert.Equal(update.DownloadSize(false), reports[^1].Total);
    }

    [Fact]
    public async Task DownloadsAircraftAndKeepsFilesAlreadyComplete()
    {
        github.AddRelease("v0.3.0", 3, Files("0.3.0"));
        var update = (await Service().CheckAsync())!;
        await Service().DownloadAsync(update, withAircraft: true, directory);
        github.Requested.Clear();

        var downloaded = await Service().DownloadAsync(update, withAircraft: true, directory);

        Assert.Equal(Path.Combine(directory, "SharedDeck-A32NX-part1of2.zip"), downloaded.AircraftPath);
        Assert.DoesNotContain(github.Requested, url => url.EndsWith(".zip", StringComparison.Ordinal) || url.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptDownloadIsRejected()
    {
        github.AddRelease("v0.3.0", 3, Files("0.3.0"), sha: (name, content) =>
            name == UpdateService.SetupName ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        var update = (await Service().CheckAsync())!;

        var error = await Assert.ThrowsAsync<UpdateException>(() => Service().DownloadAsync(update, false, directory));

        Assert.Contains("Prüfsumme", error.Message);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void SetupArgumentsForUpdate()
    {
        var withAircraft = UpdateService.SetupArguments(new DownloadedUpdate(new Version(0, 3, 0), "C:\\u\\Setup.exe", "C:\\u\\A-part1of2.zip"),
            1234, "D:\\Community", "C:\\log.txt");
        Assert.Equal(new[] { "/auto", "/restart", "/waitpid:1234", "/log:C:\\log.txt", "/aircraft:C:\\u\\A-part1of2.zip", "/community:D:\\Community" },
            withAircraft);

        var withoutAircraft = UpdateService.SetupArguments(new DownloadedUpdate(new Version(0, 3, 0), "C:\\u\\Setup.exe", null), 1, null, "l");
        Assert.Contains("/noaircraft", withoutAircraft);
    }

    [Theory]
    [InlineData("v0.2.1", "0.2.1")]
    [InlineData("0.10.0", "0.10.0")]
    [InlineData("release", null)]
    public void ParsesTags(string tag, string? expected) =>
        Assert.Equal(expected == null ? null : Version.Parse(expected), UpdateService.ParseVersion(tag));

    private sealed class SynchronousProgress(Action<(long Done, long Total)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => report(value);
    }
}
