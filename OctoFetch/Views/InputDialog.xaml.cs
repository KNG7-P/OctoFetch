using System.Windows;
using System.Windows.Input;

namespace OctoFetch.Views
{
    public partial class InputDialog : Window
    {
        public string Answer => InputBox.Text.Trim();

        public InputDialog(string title, string prompt, string initial = "")
        {
            InitializeComponent();
            TitleText.Text = title;
            PromptText.Text = prompt;
            InputBox.Text = initial;
            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}
