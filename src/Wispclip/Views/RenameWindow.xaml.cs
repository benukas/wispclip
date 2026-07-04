using System.Windows;
using Wispclip.Services;

namespace Wispclip.Views;

public partial class RenameWindow : Window
{
    public string? Result { get; private set; }

    public RenameWindow(string currentName)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Ui.EnableDarkTitleBar(this);
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
