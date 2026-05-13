using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.Views.Dialogs
{
    public partial class MitmSettingsDialog : Window
    {
        private readonly IMitmService _mitm;
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly Action _persistSettings;

        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
        private static readonly SolidColorBrush GreyBrush = new(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly SolidColorBrush BlueBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6));

        public MitmSettingsDialog(
            IMitmService mitm,
            AppSettings settings,
            IAppLogger logger,
            Action persistSettings)
        {
            InitializeComponent();
            _mitm = mitm;
            _settings = settings;
            _logger = logger;
            _persistSettings = persistSettings;

            TogEnable.IsChecked = _settings.IsMitmEnabled;
            TxtConfig.Text = _mitm.ReadConfig();
            UpdateFooter();

            _mitm.StateChanged += OnMitmStateChanged;
        }

        private void OnMitmStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateFooter));
        }

        protected override void OnClosed(EventArgs e)
        {
            _mitm.StateChanged -= OnMitmStateChanged;
            base.OnClosed(e);
        }

        private void UpdateFooter()
        {
            if (_mitm.IsRunning)
            {
                LblEngine.Text = $"Engine: running on {_mitm.ProxyAddress}";
                EllEngine.Fill = GreenBrush;
            }
            else
            {
                LblEngine.Text = "Engine: stopped";
                EllEngine.Fill = GreyBrush;
            }

            if (_mitm.IsCertificateInstalled)
            {
                LblCert.Text = "Cert: installed";
                EllCert.Fill = GreenBrush;
            }
            else
            {
                LblCert.Text = _mitm.BinariesPresent ? "Cert: not generated" : "Cert: binaries missing";
                EllCert.Fill = _mitm.BinariesPresent ? GreyBrush : RedBrush;
            }

            UpdateErrorBanner();
        }

        private void UpdateErrorBanner()
        {
            var err = _mitm.LastStartError;
            if (_settings.IsMitmEnabled && !_mitm.IsRunning && !string.IsNullOrWhiteSpace(err))
            {
                LblStartError.Text = err;
                ErrorBanner.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorBanner.Visibility = Visibility.Collapsed;
            }
        }
        private bool _suppressToggleHandler;

        private async void TogEnable_Toggled(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_suppressToggleHandler) return;
            bool nowEnabled = TogEnable.IsChecked == true;
            if (_settings.IsMitmEnabled == nowEnabled) return;

            if (!nowEnabled)
            {
                int active = _mitm.DependentOperationsCount;
                if (active > 0)
                {
                    var confirmation = MessageBox.Show(
                        $"{active} download(s) are currently using the MITM proxy.\n\n" +
                        "Turning MITM off now will likely interrupt them with a connection reset.\n\n" +
                        "Disable MITM anyway?",
                        "MITM is in use",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirmation != MessageBoxResult.Yes)
                    {
                        _suppressToggleHandler = true;
                        try { TogEnable.IsChecked = true; }
                        finally { _suppressToggleHandler = false; }
                        return;
                    }
                }
            }

            _settings.IsMitmEnabled = nowEnabled;
            _persistSettings();
            _logger.Log(LogChannel.Mitm,
                nowEnabled
                    ? "MITM engine enabled — starting xray-core…"
                    : "MITM engine disabled — stopping xray-core and clearing any system proxy…");

            BtnCheck.IsEnabled = false;
            try
            {
                await _mitm.ApplyEnabledStateAsync().ConfigureAwait(true);
            }
            finally
            {
                BtnCheck.IsEnabled = true;
                UpdateFooter();
            }
        }

        private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            BtnCheck.IsEnabled = false;
            HealthPanel.Visibility = Visibility.Visible;
            LblYouTube.Text = "checking…";
            LblDrive.Text = "checking…";
            LblPing.Text = "checking…";
            EllYouTube.Fill = GreyBrush;
            EllDrive.Fill = GreyBrush;
            EllPing.Fill = GreyBrush;

            bool previouslyEnabled = _settings.IsMitmEnabled;
            if (!previouslyEnabled)
            {
                _settings.IsMitmEnabled = true;
                await _mitm.StartEngineAsync().ConfigureAwait(true);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var result = await _mitm.CheckHealthAsync(cts.Token).ConfigureAwait(true);

                LblYouTube.Text = result.YouTubeOk
                    ? $"OK · {result.YouTubeLatencyMs} ms"
                    : $"failed{(string.IsNullOrEmpty(result.YouTubeError) ? "" : $" — {Trim(result.YouTubeError)}")}";
                EllYouTube.Fill = result.YouTubeOk ? GreenBrush : RedBrush;

                LblDrive.Text = result.DriveOk
                    ? $"OK · {result.DriveLatencyMs} ms"
                    : $"failed{(string.IsNullOrEmpty(result.DriveError) ? "" : $" — {Trim(result.DriveError)}")}";
                EllDrive.Fill = result.DriveOk ? GreenBrush : RedBrush;

                LblPing.Text = result.TcpPingOk
                    ? $"OK · {result.TcpPingMs} ms"
                    : $"failed{(string.IsNullOrEmpty(result.TcpPingError) ? "" : $" — {Trim(result.TcpPingError!)}")}";
                EllPing.Fill = result.TcpPingOk ? GreenBrush : RedBrush;
            }
            catch (Exception ex)
            {
                LblYouTube.Text = $"Probe error: {ex.Message}";
                EllYouTube.Fill = RedBrush;
            }
            finally
            {
                if (!previouslyEnabled)
                {
                    _settings.IsMitmEnabled = false;
                    try { await _mitm.StopEngineAsync().ConfigureAwait(true); }
                    catch {  }
                }
                BtnCheck.IsEnabled = true;
                UpdateFooter();
            }
        }

        private static string Trim(string s) =>
            s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        // ----- Tab pill handlers -----
        private void TabSettings_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (TabConfig != null) TabConfig.IsChecked = false;
            if (PaneSettings != null) PaneSettings.Visibility = Visibility.Visible;
            if (PaneConfig != null) PaneConfig.Visibility = Visibility.Collapsed;
        }

        private void TabConfig_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (TabSettings != null) TabSettings.IsChecked = false;
            if (PaneSettings != null) PaneSettings.Visibility = Visibility.Collapsed;
            if (PaneConfig != null) PaneConfig.Visibility = Visibility.Visible;
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch {  }
            }
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_mitm.IsRunning)
            {
                MessageBox.Show(this,
                    "Stop or wait for any active MITM session before removing the certificate.",
                    "MITM engine", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(this,
                "Remove the local MITM root CA from Windows and delete mycert.crt/.key?",
                "Uninstall certificate", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            BtnUninstall.IsEnabled = false;
            try
            {
                await _mitm.UninstallCertificateAsync().ConfigureAwait(true);
            }
            finally
            {
                BtnUninstall.IsEnabled = true;
                UpdateFooter();
            }
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(this,
                "Replace the current config text with the version bundled in the app?",
                "Restore default", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _mitm.RestoreDefaultConfig();
            TxtConfig.Text = _mitm.ReadConfig();
            LblConfigError.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_mitm.WriteConfig(TxtConfig.Text, out var err))
            {
                LblConfigError.Visibility = Visibility.Collapsed;
                MessageBox.Show(this,
                    "Config saved. Restart the engine (e.g. by triggering a YouTube search) for changes to take effect.",
                    "MITM engine", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                LblConfigError.Text = $"Invalid JSON: {err}";
                LblConfigError.Visibility = Visibility.Visible;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
