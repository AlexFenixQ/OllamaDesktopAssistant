using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OllamaDesktop.App.Infrastructure;
using OllamaDesktop.App.ViewModels;
using OllamaDesktop.Core;
namespace OllamaDesktop.App;
public partial class MainWindow : Window, IUserDialogs
{
    private readonly Services _services;
    private readonly MainViewModel _vm;
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval=TimeSpan.FromHours(6) };
    private readonly CancellationTokenSource _updateLifetime = new();
    private UpdateManifest? _availableUpdate;
    private bool _checkingUpdates;
    private UpdatesWindow? _updatesWindow;
    private bool _nearBottom = true, _closing, _closedApproved;
    private int _navigationIndex = -1;
    public MainWindow(Services services, AppSettings settings)
    {
        _services = services; InitializeComponent(); Width=Math.Min(1450,SystemParameters.WorkArea.Width-32); Height=Math.Min(900,SystemParameters.WorkArea.Height-32); _vm = new MainViewModel(services, this, settings); DataContext = _vm;
        _vm.Messages.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Reset) { _nearBottom = true; _navigationIndex = -1; }
            else if (change.Action == NotifyCollectionChangedAction.Add &&
                     change.NewItems?.OfType<MessageViewModel>().Any(x => x.IsUser) == true)
            { _nearBottom = true; _navigationIndex = -1; }
        };
        _vm.ScrollRequested += () =>
        {
            if (_nearBottom && _vm.Messages.Count > 0)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_nearBottom && _vm.Messages.Count > 0) MessageList.ScrollIntoView(_vm.Messages[^1]);
                }), DispatcherPriority.Background);
        };
        Closed += (_, _) => { _vm.Shutdown(); _updateTimer.Stop(); _updateLifetime.Cancel(); _updates.Dispose(); };
        SizeChanged += (_,_) => { if(ActualWidth<1150 && FilesPanel.Visibility==Visibility.Visible) SetSidebar(false); };
        _updateTimer.Tick += async (_,_) => await CheckUpdatesQuietlyAsync();
        if (settings.SidebarCollapsed) SetSidebar(false);
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync();
        if (App.SmokeTest)
        {
            try
            {
                if (_vm.HasError) throw new InvalidOperationException("SMOKE_INITIALIZE");
                var md = new Controls.MarkdownView { Markdown = "# Проверка\n\n**Текст** и `код`.\n\n```csharp\nint x = 1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |" };
                md.Measure(new Size(640, 800)); UpdateLayout();
                var checks = await SmokeChecks.RunAsync(this, _services);
                AppPaths.AtomicText(Path.Combine(AppContext.BaseDirectory, "smoke-test.json"), JsonDefaults.Write(new { version = BuildInfo.Version, passed = true, kind = "WPF startup, SQLite, Markdown, project/ZIP/worker regressions", checks, utc = DateTimeOffset.UtcNow }));
                Application.Current.Shutdown(0);
            }
            catch (Exception error) {
                AppPaths.AtomicText(Path.Combine(AppContext.BaseDirectory, "smoke-test.json"), JsonDefaults.Write(new { version = BuildInfo.Version, passed = false, code = error.Message, exceptionType = error.GetType().Name }));
                Application.Current.Shutdown(3);
            }
            return;
        }
        if (!App.Preview && (!_services.Settings.HasKey || !_services.Settings.Load().CloudConsent)) _vm.OpenSettings();
        PromptBox.Focus();
        if(!App.Preview) { _updateTimer.Start(); _ = CheckUpdatesQuietlyAsync(); }
    }
    public string[] PickFiles()
    {
        var dialog = new OpenFileDialog { Title = "Выберите вложения", Multiselect = true, CheckFileExists = true, Filter = "Все файлы|*.*|Видео и аудио|"+string.Join(";",FileGuard.MediaExtensions.Select(e=>"*"+e))+"|Изображения|"+string.Join(";",ImageFormats.Extensions.Select(e=>"*"+e))+"|Документы и ZIP|*.docx;*.xlsx;*.pptx;*.pdf;*.txt;*.csv;*.zip" };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }
    public string? PasteImage()
    {
        if (!Clipboard.ContainsImage()) { MessageBox.Show(this, "В буфере обмена нет изображения.", "Вставка изображения"); return null; }
        var image = Clipboard.GetImage(); if (image is null) return null;
        if ((long)image.PixelWidth * image.PixelHeight > (long)Limits.ImagePixels) throw new AppException("PIXEL_LIMIT", "Изображение превышает 40 миллионов пикселей.");
        var path = _services.Paths.Resolve("Staging/" + Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = new FileStream(path, FileMode.CreateNew)) encoder.Save(stream);
        return path;
    }
    public bool Confirm(string title, string message) => new DialogWindow(this, title, message).ShowDialog() == true;
    public string? Prompt(string title, string value, int maxLength = 100, bool multiline = false) { var window = new DialogWindow(this, title, value, true, maxLength, multiline); return window.ShowDialog() == true ? window.Value : null; }
    public bool EditSettings() => new SettingsWindow(this, _services).ShowDialog() == true;
    private async void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) {
            e.Handled = true; if (_vm.SendCommand.CanExecute(null)) await _vm.SendCommand.ExecuteAsync(null); return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Enter && _vm.SendCommand.CanExecute(null)) { e.Handled = true; await _vm.SendCommand.ExecuteAsync(null); }
        else if (e.Key == Key.V && !_vm.Busy)
        {
            try { if (Clipboard.ContainsImage()) { e.Handled = true; await _vm.PasteAsync(); } } catch (Exception error) { _vm.Fail(error); }
        }
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !_vm.Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        try { if (!_vm.Busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await _vm.AddFilesAsync(paths); } catch (Exception error) { _vm.Fail(error); } e.Handled = true;
    }
    private void OnDiagnosticsClick(object sender, RoutedEventArgs e) => new DiagnosticsWindow(this, _services).ShowDialog();
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { if (((FrameworkElement)sender).DataContext is MessageViewModel message && message.Content.Length > 0) Clipboard.SetText(message.Content); } catch (Exception error) { _vm.Fail(error); }
    }
    private async void OnRegenerateMessage(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MessageViewModel message || !message.IsLastAssistant || _vm.Busy)
            return;
        if (!Confirm("Пересоздать ответ?", "Будет выполнен новый запрос к облачной модели. Старый ответ и созданные файлы сохранятся, запрос может расходовать лимит аккаунта."))
            return;
        await _vm.RegenerateCommand.ExecuteAsync(null);
    }
    private async void OnOpenArtifact(object sender, RoutedEventArgs e)
    {
        try { if (((FrameworkElement)sender).DataContext is Artifact artifact) SafeShell.OpenFile(await Task.Run(() => SafeShell.CheckArtifact(_services.Paths, artifact))); }
        catch (Exception error) { _vm.Fail(new AppException("OPEN_FILE", "Файл не удалось открыть. Проверьте его наличие и программу для просмотра; воспользуйтесь «Сохранить как».", error)); }
    }
    private async void OnSaveArtifact(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((FrameworkElement)sender).DataContext is not Artifact artifact) return;
            var source = await Task.Run(() => SafeShell.CheckArtifact(_services.Paths, artifact));
            var dialog = new SaveFileDialog { FileName = artifact.Name, DefaultExt = Path.GetExtension(source), Filter = "Документ|*" + Path.GetExtension(source), AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            var target = Path.GetFullPath(dialog.FileName);
            if (target.StartsWith(_services.Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new AppException("SAVE_LOCATION", "Выберите папку вне служебного хранилища приложения, например «Документы».");
            await Task.Run(() => File.Copy(source, target, true));
            _vm.ClearErrorCommand.Execute(null);
            _vm.Notify("Копия сохранена.");
        }
        catch (Exception error) { _vm.Fail(error); }
    }
    private void OnArtifactFolder(object sender, RoutedEventArgs e)
    {
        try { if (((FrameworkElement)sender).DataContext is Artifact artifact) SafeShell.Folder(_services.Paths.Resolve(artifact.RelativePath)); } catch (Exception error) { _vm.Fail(error); }
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Extent changes during streamed Markdown reflow must not disable follow-bottom.
        // Only an actual scroll movement changes the user's follow preference.
        if (e.OriginalSource is ScrollViewer viewer && e.ExtentHeightChange == 0 && Math.Abs(e.VerticalChange) > 0.5)
            _nearBottom = viewer.ScrollableHeight - viewer.VerticalOffset < 72;
        ScrollLatestButton.Visibility=_nearBottom || _vm.Messages.Count==0?Visibility.Collapsed:Visibility.Visible;
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = FindVisual<T>(VisualTreeHelper.GetChild(parent, i));
            if (child is not null) return child;
        }
        return null;
    }
    private int FirstVisibleMessage()
    {
        for (var i = 0; i < _vm.Messages.Count; i++)
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            var position = item.TranslatePoint(new Point(0, 0), MessageList);
            if (position.Y + item.ActualHeight > 10) return i;
        }
        return Math.Clamp(_navigationIndex, 0, Math.Max(0, _vm.Messages.Count - 1));
    }
    private void GoToMessage(int index)
    {
        if (_vm.Messages.Count == 0) return;
        index = Math.Clamp(index, 0, _vm.Messages.Count - 1);
        _navigationIndex = index;
        _nearBottom = index == _vm.Messages.Count - 1;
        MessageList.ScrollIntoView(_vm.Messages[index]);
        if (index == 0) FindVisual<ScrollViewer>(MessageList)?.ScrollToTop();
        if (_nearBottom) FindVisual<ScrollViewer>(MessageList)?.ScrollToEnd();
    }
    private void OnScrollTopClick(object sender, RoutedEventArgs e) => GoToMessage(0);
    private void OnScrollLatestClick(object sender, RoutedEventArgs e) => GoToMessage(_vm.Messages.Count - 1);
    private void OnPreviousMessageClick(object sender, RoutedEventArgs e) => GoToMessage((_navigationIndex >= 0 ? _navigationIndex : FirstVisibleMessage()) - 1);
    private void OnNextMessageClick(object sender, RoutedEventArgs e) => GoToMessage((_navigationIndex >= 0 ? _navigationIndex : FirstVisibleMessage()) + 1);
    private void OnFocusChat(object sender, RoutedEventArgs e) => PromptBox.Focus();
    private void OnFocusHistory(object sender, RoutedEventArgs e) { SetSidebar(true); HistorySearch.Focus(); }
    private void OnFocusAttachments(object sender, RoutedEventArgs e) { SetFiles(true); AttachmentList.Focus(); }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Preserve Ctrl+Home/End behavior while the user is editing text.
        if (PromptBox.IsKeyboardFocusWithin) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Home) { GoToMessage(0); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.End) { GoToMessage(_vm.Messages.Count - 1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Up) { OnPreviousMessageClick(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Down) { OnNextMessageClick(sender, e); e.Handled = true; }
    }
    private async void OnResumeMediaClick(object sender,RoutedEventArgs e) { if(((FrameworkElement)sender).DataContext is Attachment attachment) await _vm.ResumeMediaAsync(attachment); }
    private async void OnTranscriptClick(object sender,RoutedEventArgs e) {
        try { if(((FrameworkElement)sender).DataContext is Attachment {Media: {} media} && media.TranscriptArtifact is {} artifact) SafeShell.OpenFile(await Task.Run(()=>SafeShell.CheckArtifact(_services.Paths,artifact))); }
        catch(Exception error){_vm.Fail(error);}
    }
    private void SetSidebar(bool show) { RestoreSidebarButton.Visibility=show?Visibility.Collapsed:Visibility.Visible; SidebarColumn.Width=new GridLength(show?270:0); SidebarPanel.Visibility=show?Visibility.Visible:Visibility.Collapsed; }
    private void SetFiles(bool show) { FilesColumn.Width=new GridLength(show?Math.Min(310,Math.Max(250,ActualWidth-730)):0); FilesPanel.Visibility=show?Visibility.Visible:Visibility.Collapsed; }
    private void OnToggleSidebar(object sender,RoutedEventArgs e) {
        var show=SidebarPanel.Visibility!=Visibility.Visible; SetSidebar(show);
        if(show && ActualWidth<1150) SetFiles(false);
        try { var s=_services.Settings.Load(); s.SidebarCollapsed=!show; _services.Settings.Save(s); } catch(Exception error) { _vm.Fail(error); }
    }
    private void OnToggleFiles(object sender,RoutedEventArgs e) { var show=FilesPanel.Visibility!=Visibility.Visible; if(show && ActualWidth<1150) SetSidebar(false); SetFiles(show); }
    private async void OnSidebarChat(object sender,RoutedEventArgs e) { if(((FrameworkElement)sender).DataContext is Conversation chat) await _vm.NavigateChatAsync(chat); }
    private async void OnRecentChatSelected(object sender,SelectionChangedEventArgs e) { if(ConversationList.SelectedItem is Conversation chat) await _vm.NavigateChatAsync(chat); }
    private void OnProjectSelect(object sender,RoutedEventArgs e) { if(((FrameworkElement)sender).DataContext is ProjectGroup group) _vm.ActiveProject=group.Project; }
    private void OnAllChats(object sender,RoutedEventArgs e) { _vm.ActiveProject=_vm.Projects.FirstOrDefault(); }
    private async void OnProjectRename(object sender,RoutedEventArgs e) { if(((FrameworkElement)sender).DataContext is ProjectGroup group) await _vm.RenameProjectAsync(group.Project); }
    private async void OnProjectDelete(object sender,RoutedEventArgs e) { if(((FrameworkElement)sender).DataContext is ProjectGroup group) await _vm.DeleteProjectAsync(group.Project); }
    private void OnProjectMenu(object sender,RoutedEventArgs e) {
        if(((FrameworkElement)sender).DataContext is not ProjectGroup group || _vm.Busy) return;
        var menu=new ContextMenu();
        var rename=new MenuItem { Header="Переименовать проект" }; rename.Click+=async(_,_)=>await _vm.RenameProjectAsync(group.Project);
        var delete=new MenuItem { Header="Удалить проект, сохранить чаты…" }; delete.Click+=async(_,_)=>await _vm.DeleteProjectAsync(group.Project);
        menu.Items.Add(rename);menu.Items.Add(delete);menu.PlacementTarget=(UIElement)sender;menu.IsOpen=true;
    }
    private void OnChatMenu(object sender,RoutedEventArgs e) {
        var menu=new ContextMenu();
        menu.Items.Add(new MenuItem { Header="Переименовать чат",Command=_vm.RenameCommand });
        var move=new MenuItem { Header="Переместить в проект",IsEnabled=_vm.NotBusy };
        var general=new MenuItem { Header="Без проекта" }; general.Click+=async(_,_)=>await _vm.MoveCurrentChatAsync(null); move.Items.Add(general);
        foreach(var p in _vm.Projects.Where(p=>p.Id.Length>0)) { var item=new MenuItem { Header=p.Name }; item.Click+=async(_,_)=>await _vm.MoveCurrentChatAsync(p.Id); move.Items.Add(item); }
        menu.Items.Add(move); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header="Удалить чат…",Command=_vm.DeleteCommand });
        menu.PlacementTarget=(UIElement)sender; menu.IsOpen=true;
    }
    private void OnAboutClick(object sender,RoutedEventArgs e) => new InfoWindow(this,"О программе","Ollama Desktop " + BuildInfo.Version + "\n\nЧаты, проекты и документы через Ollama Cloud. Речь распознаётся локально Whisper.\n\nДля отчёта текст и выбранные кадры отправляются в Ollama Cloud только по вашему запросу.\n\nhttps://github.com/"+UpdateFeed.Repository).ShowDialog();
    private void OnUpdatesClick(object sender,RoutedEventArgs e) {
        if(_updatesWindow is not null) { _updatesWindow.Activate(); return; }
        _updatesWindow=new UpdatesWindow(this,_services.Settings,_updates,()=>_vm.Busy,async()=> { await _vm.PersistDraftAsync(); _closedApproved=true; },_availableUpdate);
        try { _updatesWindow.ShowDialog(); } finally { _updatesWindow=null; if(!App.Preview) _ = CheckUpdatesQuietlyAsync(); }
    }
    private async Task CheckUpdatesQuietlyAsync() {
        if(_checkingUpdates || App.SmokeTest || App.Preview || _updatesWindow is not null) return;
        _checkingUpdates=true;
        try { if(!_services.Settings.Load().AutomaticUpdateChecks) return; _availableUpdate=await _updates.CheckAsync(_services.Settings.Load().UpdateChannel,_updateLifetime.Token); UpdateNotice.Visibility=_availableUpdate is null?Visibility.Collapsed:Visibility.Visible; }
        catch(OperationCanceledException) { }
        catch(Exception error) { Diagnostics.Record(_services.Paths,"UPDATE_CHECK_UNAVAILABLE",error); }
        finally { _checkingUpdates=false; }
    }
    private async void OnClosing(object? sender,CancelEventArgs e) {
        if(_closedApproved) return;
        e.Cancel=true;
        if(_closing) return;
        if(_vm.Busy && !Confirm("Закрыть приложение?","Текущая операция будет остановлена. Уже отправленные запросы могли расходовать лимит облака.")) return;
        _closing=true;
        try { if(_vm.Busy) { _vm.Cancel(); while(_vm.Busy) await Task.Delay(150); } await _vm.PersistDraftAsync(); _closedApproved=true; Close(); }
        catch(Exception error) { _vm.Fail(error); _closing=false; }
    }
}
