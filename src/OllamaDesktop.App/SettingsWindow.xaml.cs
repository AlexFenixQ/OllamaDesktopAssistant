using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OllamaDesktop.App.Infrastructure;
using OllamaDesktop.Core;
namespace OllamaDesktop.App;
public partial class SettingsWindow : Window
{
    private readonly Services _services;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    public SettingsWindow(Window owner, Services services)
    {
        Owner = owner; _services = services; _settings = services.Settings.Load(); InitializeComponent();
        TextModel.Text = _settings.TextModel; VisionModel.Text = _settings.VisionModel; Consent.IsChecked = _settings.CloudConsent;
        SpeechModelChoice.SelectedIndex = _settings.SpeechModel == "medium" ? 1 : 0;
        ThemeChoice.SelectedIndex = _settings.Theme == "Light" ? 1 : 0;
        RefreshKeyHint();
    }
    private void RefreshKeyHint() => KeyHint.Text = _services.Settings.HasKey ? "Ключ уже сохранён. Оставьте поле пустым, чтобы не менять его." : "Вставьте личный ключ. Не отправляйте его в чат и не вводите в терминале.";
    private string GetKey() => KeyBox.Password.Length == 0 ? _services.Settings.ReadKey() : KeyBox.Password.Trim();
    private void SetBusy(bool busy) { _busy = busy; SaveButton.IsEnabled = TestButton.IsEnabled = CatalogButton.IsEnabled = !busy; }
    private async void OnCatalog(object sender, RoutedEventArgs e)
    {
        SetBusy(true); StatusText.Text = "Загрузка облачного каталога…";
        try
        {
            var text = TextModel.Text; var vision = VisionModel.Text;
            var models = await _services.Client.ModelsAsync(null, _lifetime.Token);
            TextModel.ItemsSource = models.Select(m => m.Name).OrderBy(n => n).ToArray();
            VisionModel.ItemsSource = models.Where(m => m.IsVision).Select(m => m.Name).OrderBy(n => n).ToArray();
            TextModel.Text = text; VisionModel.Text = vision;
            StatusText.Text = "Каталог получен. Доступ аккаунта проверяется отдельной кнопкой.";
        }
        catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); }
        finally { SetBusy(false); }
    }
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (Consent.IsChecked != true) { StatusText.Text = "Подтвердите передачу данных в облако."; return; }
        SetBusy(true); StatusText.Text = "Проверка доступа к текстовой модели…";
        try { await _services.Client.TestAsync(GetKey(), TextModel.Text.Trim(), _lifetime.Token); StatusText.Text = "Текстовая модель ответила. Доступ визуальной модели проверяется при работе с изображением. Нажмите «Сохранить»."; }
        catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); }
        finally { SetBusy(false); }
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.TextModel = TextModel.Text.Trim(); _settings.VisionModel = VisionModel.Text.Trim(); _settings.CloudConsent = Consent.IsChecked == true;
            _settings.SpeechModel = SpeechModelChoice.SelectedIndex == 1 ? "medium" : "small";
            _settings.Theme = ThemeChoice.SelectedIndex == 1 ? "Light" : "Dark"; SettingsStore.Validate(_settings);
            if (KeyBox.Password.Length > 0) _services.Settings.StoreKey(KeyBox.Password);
            _services.Settings.Save(_settings); KeyBox.Clear(); DialogResult = true;
        }
        catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); }
    }
    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        if (_busy || new DialogWindow(this, "Удалить ключ?", "Доступ к облаку будет отключён до ввода нового ключа. Переписка останется.").ShowDialog() != true) return;
        try { _services.Settings.DeleteKey(); KeyBox.Clear(); RefreshKeyHint(); } catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); }
    }
    private void OnKeys(object sender, RoutedEventArgs e) => OpenWeb("https://ollama.com/settings/keys");
    private void OnPricing(object sender, RoutedEventArgs e) => OpenWeb("https://ollama.com/pricing");
    private void OpenWeb(string url) { try { SafeShell.Web(url); } catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); } }
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            if (new DialogWindow(this, "Экспорт диагностики", "Архив включает версию приложения, тип ОС и коды ошибок. Он не включает API-ключ, тексты переписки, вложения и полные пути. Выберите, куда его сохранить.").ShowDialog() != true) return;
            var dialog = new SaveFileDialog { FileName = "OllamaDesktop-Diagnostics.zip", Filter = "ZIP|*.zip", OverwritePrompt = true };
            if (dialog.ShowDialog(this) == true)
            {
                if (Path.GetFullPath(dialog.FileName).StartsWith(_services.Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new AppException("DIAGNOSTICS_LOCATION", "Выберите папку вне служебного хранилища приложения.");
                Diagnostics.Export(_services.Paths, dialog.FileName); StatusText.Text = "Диагностика сохранена.";
            }
        }
        catch (Exception error) { StatusText.Text = FriendlyError.Describe(error); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { _lifetime.Cancel(); KeyBox.Clear(); }
}
