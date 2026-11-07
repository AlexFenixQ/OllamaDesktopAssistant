using System.Windows;
using System.Windows.Controls;
namespace OllamaDesktop.App;
public sealed class InfoWindow : Window
{
    public InfoWindow(Window owner,string title,string text){Style=(Style)Application.Current.FindResource(typeof(Window));Owner=owner;Title=title;Width=540;Height=350;MinWidth=380;MinHeight=240;WindowStartupLocation=WindowStartupLocation.CenterOwner;var panel=new DockPanel{Margin=new Thickness(24)};Content=panel;var close=new Button{Content="Закрыть",IsCancel=true,HorizontalAlignment=HorizontalAlignment.Right,MinWidth=110};DockPanel.SetDock(close,Dock.Bottom);close.Click+=(_,_)=>Close();panel.Children.Add(close);panel.Children.Add(new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=15}});}
}
