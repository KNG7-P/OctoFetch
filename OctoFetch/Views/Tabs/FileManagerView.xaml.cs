using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using OctoFetch.Models;
using OctoFetch.ViewModels;

namespace OctoFetch.Views.Tabs
{
    public partial class FileManagerView : UserControl
    {
        public FileManagerView()
        {
            InitializeComponent();
        }
        public System.Collections.Generic.IEnumerable<CloudItem> GetSelectedItems()
            => ListViewFilesGithub.SelectedItems.OfType<CloudItem>();

        private void ListViewFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FileManagerViewModel vm
                && ListViewFilesGithub.SelectedItem is CloudItem item)
            {
                vm.OpenFolder(item);
            }
        }
    }
}
