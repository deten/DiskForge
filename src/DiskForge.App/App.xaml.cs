using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiskForge.App.ViewModels;
using DiskForge.Engine;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DiskForge.App;

public partial class App : Application
{
    private IServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DiskForge", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "diskforge-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("DiskForge starting (elevated={Elevated})", Elevation.IsElevated());

        DispatcherUnhandledException += OnUnhandledException;

        var sc = new ServiceCollection();
        sc.AddSingleton<SystemInspector>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        System.Windows.MessageBox.Show(e.Exception.Message, "DiskForge — unexpected error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("DiskForge exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
