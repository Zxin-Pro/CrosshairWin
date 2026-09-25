using System.Windows;

namespace CrosshairWin.Dialogs;

/// <summary>
/// Minimal single-line text prompt, so the app does not have to reference
/// Microsoft.VisualBasic for InputBox.
/// </summary>
public partial class InputDialogWindow : Window
{
    /// <summary>The trimmed text the user entered, or null when cancelled.</summary>
    public string? EnteredText { get; private set; }

    public InputDialogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>Shows the prompt modally and returns the entered text, or null.</summary>
    public static string? Ask(Window owner, string title, string prompt, string? initial)
    {
        var window = new InputDialogWindow
        {
            Owner = owner,
            Title = title
        };

        window.PromptText.Text = prompt;
        window.ValueBox.Text = initial ?? string.Empty;

        return window.ShowDialog() == true ? window.EnteredText : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string text = ValueBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "名称不能为空。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EnteredText = text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        EnteredText = null;
        DialogResult = false;
    }
}
