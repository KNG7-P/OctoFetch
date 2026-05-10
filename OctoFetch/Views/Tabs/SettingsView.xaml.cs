using System.Windows;
using System.Windows.Controls;
using OctoFetch.ViewModels;

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

        private void BtnBrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Oct-Download output folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (!string.IsNullOrWhiteSpace(vm.Settings.DownloadFolderPath))
                dlg.SelectedPath = vm.Settings.DownloadFolderPath;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                vm.Settings.DownloadFolderPath = dlg.SelectedPath;
                vm.SaveSettingsSilently();
            }
        }
    }
}
