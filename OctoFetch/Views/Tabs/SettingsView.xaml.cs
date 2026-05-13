using System.Windows;
using System.Windows.Controls;
using OctoFetch.ViewModels;
using OctoFetch.Views.Dialogs;

namespace OctoFetch.Views.Tabs
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        public string GetNewToken() => PwdNewToken.Password;

        public void ClearNewToken() => PwdNewToken.Clear();

        private void BtnOpenDownloadSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var dlg = new DownloadSettingsDialog(vm.Settings)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() == true)
            {
                vm.SaveSettingsSilently();

                vm.Downloader.UpdateConcurrency(vm.Settings.MaxConcurrentDownloads);

                if (dlg.DestinationChanged)
                    vm.Dashboard.ApplyDestinationFromSettings();
            }
        }

    }
}
