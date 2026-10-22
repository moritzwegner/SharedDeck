using System.Windows;

namespace SharedDeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // crashes end up in the log file, so they can be sent along
        DispatcherUnhandledException += (_, args) => SharedDeck.App.MainWindow.WriteLogFile($"{DateTime.Now:HH:mm:ss} ABSTURZ: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            SharedDeck.App.MainWindow.WriteLogFile($"{DateTime.Now:HH:mm:ss} ABSTURZ: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            SharedDeck.App.MainWindow.WriteLogFile($"{DateTime.Now:HH:mm:ss} FEHLER im Hintergrund: {args.Exception}");
        base.OnStartup(e);
    }
}
