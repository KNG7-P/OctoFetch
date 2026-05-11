using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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

            DialogResult = true;
            Close();
        }
    }
}
