using System.Windows;
using System.Windows.Input;
using OctoFetch.ViewModels;

namespace OctoFetch
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not MainViewModel vm) return;

            vm.NodeManagement.NewTokenProvider = () => SettingsHost.GetNewToken();
            vm.NodeManagement.ClearNewTokenInput = () => SettingsHost.ClearNewToken();
            vm.FileManager.SelectedItemsProvider = () => FileManagerHost.GetSelectedItems();

            // Shared YouTube quality picker used by both the YouTube tab and the
            // Dashboard input (when the user pastes a YouTube link there).
            Models.YouTubeDownloadOptions? ShowYtDialog(string title)
            {
                var dlg = new Views.Dialogs.YouTubeDownloadDialog(title) { Owner = this };
                return dlg.ShowDialog() == true ? dlg.Result : null;
            }

            vm.YouTube.ShowDownloadDialog = ShowYtDialog;
            vm.Dashboard.ShowYouTubeDialog = ShowYtDialog;

            vm.FileManager.PromptForNewFolderName = currentName =>
            {
                var dlg = new Views.InputDialog(
                    title: "Rename folder",
                    prompt: "Enter the new folder name:",
                    initial: currentName)
                { Owner = this };
                return dlg.ShowDialog() == true ? dlg.Answer : null;
            };
        }

        // -- Window chrome (chrome-only behavior, never business logic) ------
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Application.Current.Shutdown();

        // -- Drag-and-drop ----------------------------------------------------
        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasAcceptableData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var text = ExtractDroppedText(e.Data);
            if (string.IsNullOrWhiteSpace(text)) return;

            vm.Dashboard.AcceptDroppedText(text);
            if (!vm.IsDashboardActive)
            {
                vm.IsDashboardActive = true;
                vm.IsFileManagerActive = false;
                vm.IsExtractorActive = false;
                vm.IsStatsActive = false;
                vm.IsSettingsActive = false;
                vm.IsYouTubeActive = false;
                vm.IsDownloaderActive = false;
            }
        }

        private static bool HasAcceptableData(IDataObject data)
        {
            return data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent("UniformResourceLocator")
                || data.GetDataPresent("UniformResourceLocatorW")
                || data.GetDataPresent(DataFormats.FileDrop);
        }

        private static string? ExtractDroppedText(IDataObject data)
        {
            if (data.GetDataPresent("UniformResourceLocatorW"))
            {
                var bytes = data.GetData("UniformResourceLocatorW") as byte[];
                if (bytes != null) return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            if (data.GetDataPresent("UniformResourceLocator"))
            {
                var bytes = data.GetData("UniformResourceLocator") as byte[];
                if (bytes != null) return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
                return data.GetData(DataFormats.UnicodeText) as string;
            if (data.GetDataPresent(DataFormats.Text))
                return data.GetData(DataFormats.Text) as string;
            if (data.GetDataPresent(DataFormats.FileDrop)
                && data.GetData(DataFormats.FileDrop) is string[] files
                && files.Length > 0)
            {
                return files[0];
            }
            return null;
        }
    }
}
