using System;
using System.Windows;
using System.Windows.Controls;
using OctoFetch.Models;

namespace OctoFetch.Views.Dialogs
{
    public partial class DownloadSettingsDialog : Window
    {
        private readonly AppSettings _settings;

        public bool DestinationChanged { get; private set; }

        public DownloadSettingsDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            CmbConcurrency.SelectedItem = _settings.MaxConcurrentDownloads;
            TxtFolder.Text = _settings.DownloadFolderPath;
            SelectDestinationByTag(_settings.DefaultUploadDestination.ToString());

            ChkReleaseEnabled.IsChecked = _settings.IsReleaseUploaderEnabled;
            TxtReleaseTag.Text = _settings.ReleaseDefaultTag;
        }

        private void SelectDestinationByTag(string tag)
        {
            foreach (var item in CmbDestination.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.Ordinal))
                {
                    CmbDestination.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void CmbDestination_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Oct-Download output folder",
                Multiselect = false,
            };
            if (!string.IsNullOrWhiteSpace(TxtFolder.Text))
                dlg.InitialDirectory = TxtFolder.Text;

            if (dlg.ShowDialog(this) == true)
            {
                TxtFolder.Text = dlg.FolderName;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (CmbConcurrency.SelectedItem is int n && n >= 1 && n <= 5)
                _settings.MaxConcurrentDownloads = n;

            var folder = TxtFolder.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(folder))
                _settings.DownloadFolderPath = folder!;

            if (CmbDestination.SelectedItem is ComboBoxItem cbi
                && Enum.TryParse<UploadDestination>(cbi.Tag?.ToString(), out var parsed)
                && parsed != _settings.DefaultUploadDestination)
            {
                _settings.DefaultUploadDestination = parsed;
                DestinationChanged = true;
            }

            // GitHub Releases card.
            _settings.IsReleaseUploaderEnabled = ChkReleaseEnabled.IsChecked == true;
            var tag = TxtReleaseTag.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(tag))
                _settings.ReleaseDefaultTag = tag!;

            DialogResult = true;
            Close();
        }
    }
}
