using System.Windows;
using System.Windows.Input;

namespace BrakeCalibrator.Views
{
    public partial class InputDialog : Window
    {
        public string Answer { get; private set; } = "";

        public InputDialog(string prompt, string title)
        {
            InitializeComponent();
            PromptText.Text = prompt;
            Title = title;
            InputBox.Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Answer = InputBox.Text;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnOk(sender, e);
            if (e.Key == Key.Escape) OnCancel(sender, e);
        }
    }
}
