using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using OctoFetch.Models;

namespace OctoFetch.Views.Dialogs
{
    public partial class YouTubeSettingsDialog : Window
    {
        private readonly AppSettings _settings;

        public YouTubeSettingsDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            // Multi-key list -- newline-separated in the textbox.
            var keys = _settings.YouTubeApiKeys ?? new List<string>();
            // Fold the legacy single key in for backwards compat in case migration hasn't run yet.
            if (!string.IsNullOrWhiteSpace(_settings.YouTubeApiKey) &&
                !keys.Contains(_settings.YouTubeApiKey))
            {
                keys = new List<string>(keys) { _settings.YouTubeApiKey };
            }
            TxtApiKeys.Text = string.Join(Environment.NewLine, keys);

            // Engine selection -- default to yt-hub if anything else is stored.
            var engine = string.IsNullOrWhiteSpace(_settings.YouTubeEngine)
                ? "yt-hub"
                : _settings.YouTubeEngine;
            if (string.Equals(engine, "yt-dlp", StringComparison.OrdinalIgnoreCase))
                RbEngineDlp.IsChecked = true;
            else
                RbEngineHub.IsChecked = true;

            TxtCookies.Text = _settings.YouTubeCookiesPath ?? string.Empty;
        }

        private void BtnBrowseCookies_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select cookies.txt (Netscape format)",
                Filter = "Cookies (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                TxtCookies.Text = dlg.FileName;
            }
        }

        private void BtnClearCookies_Click(object sender, RoutedEventArgs e)
        {
            TxtCookies.Text = string.Empty;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var keys = (TxtApiKeys.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            _settings.YouTubeApiKeys = keys;
            // Keep the legacy slot in sync so older flows still see *something* if they read it.
            _settings.YouTubeApiKey = keys.FirstOrDefault() ?? string.Empty;

            _settings.YouTubeEngine = (RbEngineDlp.IsChecked == true) ? "yt-dlp" : "yt-hub";
            _settings.YouTubeCookiesPath = (TxtCookies.Text ?? string.Empty).Trim();

            DialogResult = true;
            Close();
        }
    }
}
