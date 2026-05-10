using System.Windows.Controls;
using System.Windows.Input;

namespace OctoFetch.Views.Tabs
{
    public partial class YouTubeView : UserControl
    {
        public YouTubeView()
        {
            InitializeComponent();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is ViewModels.YouTubeViewModel vm && vm.SearchCommand.CanExecute(null))
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
