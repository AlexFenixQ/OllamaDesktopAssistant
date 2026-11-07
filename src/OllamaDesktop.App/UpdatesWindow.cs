using System.Windows;
using System.Windows.Controls;
using OllamaDesktop.App.Infrastructure;
using OllamaDesktop.Core;

namespace OllamaDesktop.App;

public sealed class UpdatesWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly UpdateService _updates;
    private readonly Func<bool> _busy;
    private readonly Func<Task> _save;
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(3,15,3,10) };
    private readonly TextBlock _notes = new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(3,8,3,10) };
    private readonly Button _check = new() { Content="Проверить обновления" };
    private readonly Button _install = new() { Content="Обновить и перезапустить",IsEnabled=false };
    private readonly Button _cancel = new() { Content="Отмена",IsEnabled=false };
    private readonly ComboBox _channel = new() { ItemsSource=new[]{"Стабильный","Бета"}, Margin=new Thickness(3,8,3,6) };
    private readonly CheckBox _automatic = new() { Content="Проверять при запуске и каждые 6 часов" };
    private UpdateManifest? _release;
    private bool _working;
    public UpdatesWindow(Window owner, SettingsStore settings, UpdateService updates, Func<bool> busy, Func<Task> save, UpdateManifest? available=null)
    {
        Style=(Style)Application.Current.FindResource(typeof(Window)); Owner=owner; Title="Обновления · Ollama Desktop"; Width=550; Height=480; MinWidth=450; MinHeight=400; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _settings=settings; _updates=updates; _busy=busy; _save=save;
        var stack=new StackPanel { Margin=new Thickness(24) }; Content=new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        stack.Children.Add(new TextBlock { Text="Обновления",FontSize=25,FontWeight=FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text="Установлена версия " + BuildInfo.Version,Margin=new Thickness(3,10,3,7) });
        stack.Children.Add(_channel); stack.Children.Add(_automatic); stack.Children.Add(_status); stack.Children.Add(_notes); stack.Children.Add(_check); stack.Children.Add(_install); stack.Children.Add(_cancel);
        stack.Children.Add(new TextBlock { Text="Перед установкой создаются проверенные копии приложения и данных. История, проекты и ключ остаются на компьютере.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,16,3,0),FontSize=12 });
        var s=settings.Load(); _channel.SelectedIndex=s.UpdateChannel=="beta"?1:0; _automatic.IsChecked=s.AutomaticUpdateChecks;
        _channel.SelectionChanged+=(_,_)=> { SaveOptions(); _release=null; _install.IsEnabled=false; _status.Text="Канал изменён. Проверьте наличие обновлений."; _notes.Text=""; };
        _automatic.Checked+=(_,_)=>SaveOptions(); _automatic.Unchecked+=(_,_)=>SaveOptions();
        _cancel.Click+=(_,_)=>_cts.Cancel();
        _check.Click+=async(_,_)=>await CheckAsync(); _install.Click+=async(_,_)=>await InstallAsync();
        Closing+=(_,e)=> { if(_working) e.Cancel=true; else _cts.Cancel(); }; Closed+=(_,_)=>_cts.Dispose();
        if(available is not null && (Channel=="beta" || available.Channel=="stable")) ShowRelease(available); else _status.Text="Выберите канал и проверьте наличие новой версии.";
    }
    private string Channel => _channel.SelectedIndex==1?"beta":"stable";
    private void SaveOptions() { try { var s=_settings.Load(); s.UpdateChannel=Channel; s.AutomaticUpdateChecks=_automatic.IsChecked==true; _settings.Save(s); } catch(Exception e) { _status.Text=FriendlyError.Describe(e); } }
    private void ShowRelease(UpdateManifest? release) { _release=release; _status.Text=release is null?"У вас актуальная версия выбранного канала.":"Доступна версия " + release.Version; _notes.Text=release?.Notes??""; _install.IsEnabled=release is not null; }
    private async Task CheckAsync()
    {
        SetWorking(true); _status.Text="Проверка GitHub Releases…";
        try { ShowRelease(await _updates.CheckAsync(Channel,_cts.Token)); }
        catch(Exception) { _status.Text="Не удалось проверить обновления. Проверьте интернет или повторите позже. Приложение продолжит работать."; _install.IsEnabled=false; }
        finally { SetWorking(false); if(_cts.IsCancellationRequested) Close(); }
    }
    private void SetWorking(bool value) { _working=value; _cancel.IsEnabled=value; _check.IsEnabled=!value; _channel.IsEnabled=!value; _automatic.IsEnabled=!value; _install.IsEnabled=!value && _release is not null; }
    private async Task InstallAsync()
    {
        if(_release is null) return;
        SetWorking(true);
        try
        {
            var stage=await _updates.PrepareAsync(_release,Channel,new Progress<string>(text=>_status.Text=text),_cts.Token);
            // Owner is modal: no new operation can start while an existing request finishes.
            while(_busy()) { _status.Text="Ожидание завершения текущей операции. Затем приложение обновится и перезапустится."; await Task.Delay(500,_cts.Token); }
            await _save(); UpdateService.StartWorker(stage); _working=false; Application.Current.Shutdown();
        }
        catch(Exception e) { _status.Text=e is AppException?FriendlyError.Describe(e):"Обновление не установлено. Текущая версия и данные сохранены. Повторите позже."; }
        finally { SetWorking(false); if(_cts.IsCancellationRequested) Close(); }
    }
}
