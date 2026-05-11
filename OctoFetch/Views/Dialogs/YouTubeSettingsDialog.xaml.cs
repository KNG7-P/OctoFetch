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
            TxtApiKey.Text = _settings.YouTubeApiKey;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtApiKey.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(key))
                _settings.YouTubeApiKey = key!;

            DialogResult = true;
            Close();
        }
    }
}
