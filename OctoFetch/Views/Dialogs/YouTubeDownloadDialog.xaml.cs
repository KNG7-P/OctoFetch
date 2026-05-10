using System.Windows;
using System.Windows.Controls;
using OctoFetch.Models;

namespace OctoFetch.Views.Dialogs
{
    public partial class YouTubeDownloadDialog : Window
    {
        public YouTubeDownloadOptions? Result { get; private set; }

        public YouTubeDownloadDialog(string videoTitle)
        {
            InitializeComponent();
            TxtVideoTitle.Text = videoTitle;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var qualityText = (CmbQuality.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1080 (Full HD)";
            var audioBitrateText = (CmbAudioBitrate.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "192";

            var quality = qualityText.Split(' ')[0];

            Result = new YouTubeDownloadOptions
            {
                Format = "mp4",
                Quality = quality,
                AudioBitrate = audioBitrateText.Split(' ')[0],
            };

            DialogResult = true;
            Close();
        }
    }
}
