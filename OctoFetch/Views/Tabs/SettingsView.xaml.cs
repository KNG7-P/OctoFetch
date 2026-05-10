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

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Oct-Download output folder",
                Multiselect = false,
            };
            if (!string.IsNullOrWhiteSpace(vm.Settings.DownloadFolderPath))
                dlg.InitialDirectory = vm.Settings.DownloadFolderPath;

            if (dlg.ShowDialog() == true)
            {
                vm.Settings.DownloadFolderPath = dlg.FolderName;
                vm.SaveSettingsSilently();
            }
        }
    }
}
