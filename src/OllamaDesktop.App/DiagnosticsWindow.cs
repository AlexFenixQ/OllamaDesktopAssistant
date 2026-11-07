using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OllamaDesktop.Core;

namespace OllamaDesktop.App;

/// <summary>Explicit, privacy-minimized diagnostics export; no chat content or API key.</summary>
public sealed class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(Window owner, Services services)
    {
        Style=(Style)Application.Current.FindResource(typeof(Window)); Owner = owner; Title = "Диагностика · Ollama Desktop"; Width = 560; Height = 415;
        MinWidth = 470; MinHeight = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Диагностика приложения", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        root.Children.Add(new TextBlock { Text = "Версия: " + BuildInfo.Version + "\nWindows: " + Environment.OSVersion.Version +
            "\n.NET: " + Environment.Version + "\nБаза истории: " + (System.IO.File.Exists(services.Paths.Database) ? "есть" : "пока нет") +
            "\nКлюч доступа: " + (services.Settings.HasKey ? "сохранён локально" : "не добавлен"),
            Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = "Экспортирует: версии среды, коды и время ошибок, наличие хранилища. НЕ экспортирует: ключ Ollama, сообщения, вложения, имена файлов или пути пользователя. Архив можно проверить перед отправкой.",
            Margin = new Thickness(0, 0, 0, 20), TextWrapping = TextWrapping.Wrap });
        var button = new Button { Content = "Экспортировать безопасный ZIP…", Padding = new Thickness(15, 10, 15, 10) };
        var status = new TextBlock { Margin = new Thickness(4, 14, 4, 0), TextWrapping = TextWrapping.Wrap };
        button.Click += (_, _) =>
        {
            try
            {
                var dialog = new SaveFileDialog { FileName = "OllamaDesktop-Diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip",
                    Filter = "Архив ZIP|*.zip", OverwritePrompt = true, AddExtension = true };
                if (dialog.ShowDialog(this) != true) return;
                var destination = System.IO.Path.GetFullPath(dialog.FileName);
                var dataRoot = services.Paths.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (destination.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                    throw new AppException("DIAGNOSTICS_LOCATION", "Сохраняйте архив вне внутренней папки данных приложения.");
                Diagnostics.Export(services.Paths, destination);
                status.Text = "Архив создан. Проверьте содержимое перед отправкой разработчику.";
            }
            catch (Exception error) { status.Text = FriendlyError.Describe(error); }
        };
        root.Children.Add(button); root.Children.Add(status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
