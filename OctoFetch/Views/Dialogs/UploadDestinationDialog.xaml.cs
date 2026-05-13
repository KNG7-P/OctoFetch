using System.Windows;
using OctoFetch.Models;

namespace OctoFetch.Views.Dialogs
{
    public partial class UploadDestinationDialog : Window
    {
        public UploadDestination? SelectedDestination { get; private set; }

        public UploadDestinationDialog(
            string? subtitle = null,
            bool driveAvailable = true,
            string? driveAccountLabel = null,
            bool releaseAvailable = true,
            string? releaseDefaultTag = null)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(subtitle))
                TxtSubtitle.Text = subtitle;
            if (driveAvailable && !string.IsNullOrWhiteSpace(driveAccountLabel))
                TxtDriveStatus.Text = $"Connected as {driveAccountLabel}";
            else if (!driveAvailable)
                TxtDriveStatus.Text = "Not connected — click to connect";

            if (releaseAvailable && !string.IsNullOrWhiteSpace(releaseDefaultTag))
                TxtReleaseStatus.Text = $"Default tag: {releaseDefaultTag} • auto-split > 1.95 GiB";
            else if (!releaseAvailable)
                TxtReleaseStatus.Text = "Disabled in Settings → GitHub Releases";
        }

        private void BtnGitHub_Click(object sender, RoutedEventArgs e)
        {
            SelectedDestination = UploadDestination.GitHub;
            DialogResult = true;
            Close();
        }

        private void BtnDrive_Click(object sender, RoutedEventArgs e)
        {
            SelectedDestination = UploadDestination.GoogleDrive;
            DialogResult = true;
            Close();
        }

        private void BtnRelease_Click(object sender, RoutedEventArgs e)
        {
            SelectedDestination = UploadDestination.GitHubRelease;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedDestination = null;
            DialogResult = false;
            Close();
        }
    }
}
