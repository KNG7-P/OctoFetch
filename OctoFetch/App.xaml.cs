using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OctoFetch.Models;
using OctoFetch.Services;
using OctoFetch.ViewModels;

namespace OctoFetch
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; } = null!;

        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OctoFetch", "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);

            try
            {
                var services = new ServiceCollection();
                ConfigureServices(services);
                Services = services.BuildServiceProvider();

                var mainWindow = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainViewModel>(),
                };
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                LogCrash("Startup", ex);
                MessageBox.Show(
                    "OctoFetch could not start.\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Details written to:\n{CrashLogPath}",
                    "OctoFetch — Startup error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(b =>
            {
                b.AddDebug();
                b.SetMinimumLevel(LogLevel.Information);
            });

            services.AddSingleton<IAppLogger, AppLogger>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IExtractorService, ExtractorService>();
            services.AddSingleton<IUsageStatsService, UsageStatsService>();
            services.AddSingleton(sp => sp.GetRequiredService<ISettingsService>().Load());

            services.AddSingleton<IToastService, ToastService>();

            services.AddSingleton<IGitHubService>(sp =>
            {
                var logger = sp.GetRequiredService<IAppLogger>();
                var settings = sp.GetRequiredService<AppSettings>();
                return new GitHubService(
                    logger,
                    () => settings.AllowInsecureSsl,
                    () => settings.PollIntervalSeconds,
                    () => settings.PollMaxAttempts,
                    () => string.IsNullOrWhiteSpace(settings.ChunkSize) ? "90M" : settings.ChunkSize);
            });

            services.AddSingleton<MainViewModel>();
        }

        // -- Global exception handlers ---------------------------------------
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandled", e.Exception);
            MessageBox.Show(
                $"An unexpected error occurred:\n\n" +
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                $"OctoFetch will keep running. Details saved to:\n{CrashLogPath}",
                "OctoFetch — Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash("AppDomainUnhandled", ex);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                var entry =
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {source} ====\n" +
                    $"{ex}\n\n";
                File.AppendAllText(CrashLogPath, entry);
            }
            catch
            {
            }
        }
    }
}
