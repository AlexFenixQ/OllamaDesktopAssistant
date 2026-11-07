using System.Windows;
using System.Windows.Media;

namespace OllamaDesktop.App;

public static class Theme
{
    public static void Apply(string theme)
    {
        var dark = theme != "Light";
        var colors = new Dictionary<string, string>
        {
            ["BackgroundBrush"] = dark ? "#0E141C" : "#F4F7FB", ["SidebarBrush"] = dark ? "#090F17" : "#E8F0FB",
            ["SurfaceBrush"] = dark ? "#172334" : "#FFFFFF", ["InputBrush"] = dark ? "#111C29" : "#FFFFFF",
            ["TextBrush"] = dark ? "#F3F7FF" : "#14273B", ["MutedBrush"] = dark ? "#B2C5D9" : "#526A86",
            ["BorderBrush"] = dark ? "#27384C" : "#CBD9E9", ["AccentBrush"] = dark ? "#0879FF" : "#145DCE",
            ["SelectionBrush"] = dark ? "#123E70" : "#DCEBFF", ["CodeBrush"] = dark ? "#0B141F" : "#EEF3F9",
            ["ErrorBrush"] = dark ? "#FFBAC0" : "#A52E3E", ["UserBrush"] = dark ? "#203247" : "#E1EDFF"
        };
        foreach (var (key, value) in colors) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
}
