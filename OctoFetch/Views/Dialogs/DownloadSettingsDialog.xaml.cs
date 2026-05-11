using System.Windows;
using OctoFetch.Models;

namespace OctoFetch.Views.Dialogs
{
    public partial class DownloadSettingsDialog : Window
    {
        private readonly AppSettings _settings;

        public DownloadSettingsDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            // Seed controls from the live settings instance.
            CmbConcurrency.SelectedItem = _settings.MaxConcurrentDownloads;
            TxtFolder.Text = _settings.DownloadFolderPath;
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
                // Update the textbox immediately so the user sees the change
                // before clicking Save (previously the live binding silently
                // failed because AppSettings didn't raise PropertyChanged).
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

            DialogResult = true;
            Close();
        }
    }
}
