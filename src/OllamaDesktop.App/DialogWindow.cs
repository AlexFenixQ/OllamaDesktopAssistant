using System.Windows;
using System.Windows.Controls;
namespace OllamaDesktop.App;
public sealed class DialogWindow : Window
{
    private readonly TextBox? _input;
    public string Value => _input?.Text ?? "";
    public DialogWindow(Window owner, string title, string message, bool input = false, int maxLength = 100, bool multiline = false)
    {
        Style=(Style)Application.Current.FindResource(typeof(Window)); Owner = owner; Title = title; Width = 620; Height = input ? (multiline ? 480 : 230) : 420; MinWidth = 420; MinHeight = 200; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(20) }; Content = panel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 95 }; buttons.Children.Add(cancel);
        var ok = new Button { Content = input ? "Сохранить" : "Продолжить", IsDefault = true, MinWidth = 110 }; ok.Click += (_, _) => DialogResult = true; buttons.Children.Add(ok);
        if (input) { _input = new TextBox { Text = message, MaxLength = Math.Clamp(maxLength, 1, 4000), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, VerticalAlignment = multiline ? VerticalAlignment.Stretch : VerticalAlignment.Top, Margin = new Thickness(0, 14, 0, 20) }; panel.Children.Add(_input); Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); }; }
        else panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 15) } });
    }
}
