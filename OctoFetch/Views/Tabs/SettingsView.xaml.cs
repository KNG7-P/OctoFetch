using System.Windows.Controls;

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
    }
}
