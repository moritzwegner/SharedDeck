using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using SharedDeck.Core;
using SharedDeck.Core.Update;

namespace SharedDeck.App;

/// <summary>Update offer: checks GitHub on start, downloads on request and lets the setup install and restart.</summary>
public partial class MainWindow
{
    private static readonly string UpdateDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharedDeck", "updates");

    // SHAREDDECK_UPDATE_SOURCE: a local server in place of the GitHub API, for testing updates end to end
    private readonly UpdateService updates = new(UpdateService.CreateHttpClient(),
        typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0),
        apiBase: Environment.GetEnvironmentVariable("SHAREDDECK_UPDATE_SOURCE") ?? "https://api.github.com");

    private AvailableUpdate? availableUpdate;
    private CancellationTokenSource? downloadCancel;

    private void StartUpdateCheck()
    {
        RemoveOldDownloads();
        Loaded += async (_, _) =>
        {
            // after start-up, so the window is usable right away
            await Task.Delay(TimeSpan.FromSeconds(3));
            await CheckForUpdatesAsync(silent: true);
        };
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            availableUpdate = await updates.CheckAsync();
            if (availableUpdate == null)
            {
                AddLog($"SharedDeck {AppVersion} ist aktuell.");
                if (!silent)
                {
                    MessageBox.Show(this, $"SharedDeck {AppVersion} ist die neueste Version.", "Updates");
                }
                return;
            }
            AddLog($"Update verfügbar: SharedDeck {availableUpdate.Version}");
            ShowUpdateOffer(availableUpdate);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException)
        {
            AddLog($"Update-Prüfung: {ex.Message}");
            if (!silent)
            {
                MessageBox.Show(this, ex.Message, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>The aircraft is part of the download only when the installed one is older.</summary>
    private bool UpdateNeedsAircraft(AvailableUpdate update) => installation.AircraftRevision < update.AircraftRevision;

    private void ShowUpdateOffer(AvailableUpdate update)
    {
        var withAircraft = UpdateNeedsAircraft(update);
        UpdateTitle.Text = $"SharedDeck {update.Version} ist verfügbar (installiert: {AppVersion})";
        UpdateText.Text = (withAircraft
                              ? $"Mit neuem SharedDeck-A32NX – Download {FormatSize(update.DownloadSize(true))}. MSFS muss dafür geschlossen sein."
                              : $"Download {FormatSize(update.DownloadSize(false))}, das Flugzeug bleibt unverändert.") +
                          " Die App startet nach dem Update von selbst neu." +
                          (update.Notes.Length > 0 ? "\n\n" + ShortNotes(update.Notes) : "");
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateNowButton.IsEnabled = true;
        UpdateLaterButton.Content = "Später";
        UpdateCard.Visibility = Visibility.Visible;
    }

    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        if (downloadCancel != null)
        {
            downloadCancel.Cancel();
            return;
        }
        UpdateCard.Visibility = Visibility.Collapsed;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        var update = availableUpdate;
        if (update == null)
        {
            return;
        }
        if (session.Status.Role != SessionRole.None)
        {
            MessageBox.Show(this, "Bitte zuerst „Trennen“ – während eines gemeinsamen Flugs wird nicht aktualisiert.", "Update");
            return;
        }
        var withAircraft = UpdateNeedsAircraft(update);
        if (withAircraft && Process.GetProcessesByName("FlightSimulator2024").Length > 0)
        {
            MessageBox.Show(this, "Dieses Update enthält ein neues Flugzeug. Bitte MSFS 2024 schließen und dann erneut „Jetzt aktualisieren“ klicken.",
                "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateNowButton.IsEnabled = false;
        UpdateLaterButton.Content = "Abbrechen";
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        downloadCancel = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            UpdateProgress.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            UpdateProgressText.Text = $"{FormatSize(p.Done)} von {FormatSize(p.Total)}";
        });
        try
        {
            AddLog($"Lade SharedDeck {update.Version} herunter{(withAircraft ? " (mit Flugzeug)" : "")} …");
            var downloaded = await updates.DownloadAsync(update, withAircraft, Path.Combine(UpdateDirectory, update.Version.ToString()),
                progress, downloadCancel.Token);

            AddLog("Download vollständig und geprüft. Starte die Installation …");
            var setupLog = Path.Combine(LogDirectory, $"setup-{DateTime.Now:yyyy-MM-dd}.log");
            Process.Start(UpdateService.SetupStartInfo(downloaded, Environment.ProcessId, installation.CommunityFolder, setupLog));
            // the setup waits for this process to end before it replaces the app
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AddLog("Update abgebrochen. Bereits geladene Dateien werden beim nächsten Versuch weiterverwendet.");
            ShowUpdateOffer(update);
        }
        catch (Exception ex)
        {
            AddLog($"Update fehlgeschlagen: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Update fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowUpdateOffer(update);
        }
        finally
        {
            downloadCancel?.Dispose();
            downloadCancel = null;
        }
    }

    /// <summary>Downloads of other versions are no longer needed once a newer version runs.</summary>
    private static void RemoveOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdateDirectory))
            {
                return;
            }
            foreach (var folder in Directory.GetDirectories(UpdateDirectory))
            {
                if (Version.TryParse(Path.GetFileName(folder), out var version) && version <= new Version(AppVersion))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (IOException)
        {
            // cleaning up is optional
        }
    }

    private static string ShortNotes(string notes)
    {
        var text = string.Join("\n", notes.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', '>', ' ').Replace("**", ""))
            .Where(l => l.Length > 0 && !l.StartsWith('|'))
            .Take(6));
        return text.Length > 600 ? text[..600] + " …" : text;
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : $"{bytes / 1024.0 / 1024:0} MB";
}
