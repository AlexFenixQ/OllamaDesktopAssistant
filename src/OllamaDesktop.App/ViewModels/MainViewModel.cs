using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaDesktop.Core;

namespace OllamaDesktop.App.ViewModels;

public interface IUserDialogs
{
    string[] PickFiles();
    string? PasteImage();
    bool Confirm(string title, string message);
    string? Prompt(string title, string value, int maxLength = 100, bool multiline = false);
    bool EditSettings();
}
public sealed class MessageViewModel(ChatMessage record) : ObservableObject
{
    public ChatMessage Record { get; } = record;
    public string Content => Record.Content;
    public string Heading => (Record.Role == "user" ? "Вы" : "Ассистент") + "  ·  " + Record.Created.ToLocalTime().ToString("HH:mm");
    public string State => Record.Status switch { "streaming" => "Формируется…", "partial" => "Завершено частично", "cancelled" => "Остановлено", "superseded" => "Предыдущий вариант", "interrupted" => "Прервано при закрытии", "failed" => "Не завершено", _ => "" };
    public bool IsUser => Record.Role == "user";
    public bool IsLastAssistant { get => _lastAssistant; set => SetProperty(ref _lastAssistant, value); }
    private bool _lastAssistant;
    public bool HasNote => !string.IsNullOrWhiteSpace(Record.Note);
    public string Note => Record.Note;
    public List<Artifact> Artifacts => Record.Artifacts;
    public string AttachmentLabel => Record.Attachments.Count == 0 ? "" : "Вложения: " + string.Join(", ", Record.Attachments.Select(x => x.Name));
    public void Refresh() { OnPropertyChanged(nameof(Content)); OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(Note)); OnPropertyChanged(nameof(HasNote)); OnPropertyChanged(nameof(Artifacts)); }
}
public sealed record ProjectGroup(WorkspaceProject Project, List<Conversation> Chats) { public string Name => Project.Name; }
public sealed class MainViewModel : ObservableObject
{
    private readonly Services _services;
    private readonly IUserDialogs _dialogs;
    private CancellationTokenSource? _operation, _search;
    private Conversation? _current, _selected;
    private WorkspaceProject? _project;
    private bool _busy, _ignoreSelection;
    private string _prompt = "", _query = "", _status = "Готово", _error = "";
    private AppSettings _settings;
    private List<Conversation> _allChats = [];
    public IEnumerable<ProjectGroup> SidebarProjects => Projects.Where(p => p.Id.Length > 0).Select(p => new ProjectGroup(p, _allChats.Where(c => c.ProjectId == p.Id).ToList()));
    public IEnumerable<Conversation> RecentChats => _allChats.Where(c => string.IsNullOrEmpty(c.ProjectId));
    public bool HasActiveProject => _project is { Id.Length: > 0 };
    public async Task NavigateChatAsync(Conversation chat) {
        if (Busy) return;
        _project = Projects.FirstOrDefault(p => p.Id == (chat.ProjectId ?? ""));
        OnPropertyChanged(nameof(ActiveProject)); OnPropertyChanged(nameof(ProjectLabel)); OnPropertyChanged(nameof(HasActiveProject));
        ReplaceList(_allChats); await SelectAsync(chat);
    }
    public async Task RenameProjectAsync(WorkspaceProject project) {
        if (Busy) return;
        var name = _dialogs.Prompt("Переименовать проект", project.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        Busy = true;
        try { var changed = new WorkspaceProject { Id = project.Id, Name = name, Instructions = project.Instructions, MemoryNotes = project.MemoryNotes }; await Task.Run(() => _services.Store.SaveProject(changed)); RefreshProjects(await Task.Run(_services.Store.Projects)); }
        catch(Exception e) { Fail(e); } finally { Busy = false; }
    }
    public async Task DeleteProjectAsync(WorkspaceProject project) {
        if (Busy || !_dialogs.Confirm("Удалить проект?", "Инструкции и ручная память проекта будут удалены. Все чаты, вложения и созданные файлы сохранятся в общей истории.")) return;
        Busy = true;
        try { await Task.Run(() => _services.Store.DeleteProject(project.Id)); RefreshProjects(await Task.Run(_services.Store.Projects)); ReplaceList(await Task.Run(() => _services.Store.List(Search))); }
        catch(Exception e) { Fail(e); } finally { Busy = false; }
        await ReloadProjectsAsync();
    }
    public async Task MoveCurrentChatAsync(string? projectId) {
        if (Busy || _current is null) return;
        Busy = true;
        try { _current.ProjectId = projectId; await Task.Run(() => _services.Store.SaveConversation(_current)); ReplaceList(await Task.Run(() => _services.Store.List(Search))); }
        catch(Exception e) { Fail(e); } finally { Busy = false; }
        await ReloadProjectsAsync();
    }
    public async Task PersistDraftAsync() {
        if (_current is not null) await Task.Run(() => _services.Store.SaveConversation(_current));
        // Draft is separate from history and never sent until the user clicks Send.
        AppPaths.AtomicText(_services.Paths.Resolve("draft.json"), JsonDefaults.Write(new { chatId = _current?.Id, text = Draft, attachments = Attachments.ToList() }));
    }
    private readonly DispatcherTimer _streamTimer;
    private readonly StringBuilder _buffer = new();
    private MessageViewModel? _streaming;
    private Task _checkpoint = Task.CompletedTask;
    private DateTimeOffset _lastCheckpoint = DateTimeOffset.MinValue;
    public ObservableCollection<Conversation> Conversations { get; } = [];
    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public WorkspaceProject? ActiveProject
    {
        get => _project;
        set { if (Busy) return; if (SetProperty(ref _project, value)) { OnPropertyChanged(nameof(ProjectLabel)); OnPropertyChanged(nameof(HasActiveProject)); UpdateCommands(); _ = ReloadProjectsAsync(); } }
    }
    public string ProjectLabel => _project is null || _project.Id.Length == 0 ? "Все чаты" : _project.Name;
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<Attachment> Attachments { get; } = [];
    public string AttachmentCounter => $"Вложенные файлы ({Attachments.Count})";
    public string AppVersion => BuildInfo.Version;
    public event Action? ScrollRequested;
    public string Title => _current?.Title ?? "Ollama Desktop";
    public string ModelLabel => _settings.TextModel + "  ·  Ollama Cloud";
    public string Draft { get => _prompt; set { if (SetProperty(ref _prompt, value)) SendCommand.NotifyCanExecuteChanged(); } }
    public string Search { get => _query; set { if (SetProperty(ref _query, value)) _ = SearchAsync(value); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(NotBusy)); UpdateCommands(); } } }
    public bool NotBusy => !Busy;
    public bool FullDocument { get => _settings.FullDocument; set { _settings.FullDocument = value; SaveOptions(); OnPropertyChanged(); } }
    public bool AnalyzeVisuals { get => _settings.AnalyzeVisuals; set { _settings.AnalyzeVisuals = value; SaveOptions(); OnPropertyChanged(); } }
    private void SaveOptions() { try { var fresh=_services.Settings.Load(); fresh.FullDocument=_settings.FullDocument; fresh.AnalyzeVisuals=_settings.AnalyzeVisuals; _services.Settings.Save(fresh); _settings=fresh; } catch (Exception e) { Fail(e); } }
    public Conversation? SelectedConversation
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && !_ignoreSelection && value is not null && !Busy && value.Id != _current?.Id) _ = SelectAsync(value); }
    }
    public IAsyncRelayCommand SendCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IAsyncRelayCommand NewCommand { get; }
    public IAsyncRelayCommand AttachCommand { get; }
    public IAsyncRelayCommand PasteCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RegenerateCommand { get; }
    public IAsyncRelayCommand CreateProjectCommand { get; }
    public IAsyncRelayCommand EditProjectCommand { get; }
    public IAsyncRelayCommand MoveToProjectCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand ClearErrorCommand { get; }
    public IRelayCommand<Attachment> RemoveAttachmentCommand { get; }
    public MainViewModel(Services services, IUserDialogs dialogs, AppSettings settings)
    {
        _services = services; _dialogs = dialogs; _settings = settings;
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AttachmentCounter));
        SendCommand = new AsyncRelayCommand(() => SendAsync(), () => !Busy && _current is not null && !string.IsNullOrWhiteSpace(Draft));
        StopCommand = new RelayCommand(Cancel, () => Busy);
        NewCommand = new AsyncRelayCommand(NewAsync, () => !Busy);
        AttachCommand = new AsyncRelayCommand(() => AddFilesAsync(_dialogs.PickFiles()), () => !Busy && _current is not null);
        PasteCommand = new AsyncRelayCommand(PasteAsync, () => !Busy && _current is not null);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => !Busy && _current is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !Busy && _current is not null);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !Busy && Messages.Any(x => x.Record.Role == "user"));
        RegenerateCommand = new AsyncRelayCommand(() => SendAsync(regenerate: true), () => !Busy && Messages.Any(x => x.IsLastAssistant));
        CreateProjectCommand = new AsyncRelayCommand(CreateProjectAsync, () => !Busy);
        EditProjectCommand = new AsyncRelayCommand(EditProjectAsync, () => !Busy && _project is { Id.Length: > 0 });
        MoveToProjectCommand = new AsyncRelayCommand(MoveToProjectAsync, () => !Busy && _current is not null);
        SettingsCommand = new RelayCommand(OpenSettings, () => !Busy);
        ClearErrorCommand = new RelayCommand(() => Error = "");
        RemoveAttachmentCommand = new RelayCommand<Attachment>(a => { if (a is not null) Attachments.Remove(a); }, _ => !Busy);
        _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) }; _streamTimer.Tick += (_, _) => Flush(); _streamTimer.Start();
    }
    private void UpdateCommands()
    {
        SendCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); NewCommand.NotifyCanExecuteChanged(); AttachCommand.NotifyCanExecuteChanged(); PasteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged(); RegenerateCommand.NotifyCanExecuteChanged(); CreateProjectCommand.NotifyCanExecuteChanged(); EditProjectCommand.NotifyCanExecuteChanged(); MoveToProjectCommand.NotifyCanExecuteChanged(); SettingsCommand.NotifyCanExecuteChanged(); RemoveAttachmentCommand.NotifyCanExecuteChanged();
    }
    public async Task InitializeAsync()
    {
        Busy = true; Status = "Открытие истории…";
        try
        {
            await Task.Run(_services.Store.Initialize);
            var chats = await Task.Run(() => _services.Store.List());
            if (chats.Count == 0) { var chat = new Conversation(); await Task.Run(() => _services.Store.SaveConversation(chat)); chats.Add(chat); }
            RefreshProjects(await Task.Run(_services.Store.Projects)); ReplaceList(chats); await LoadAsync(chats[0]);
            var draftPath = _services.Paths.Resolve("draft.json");
            if (File.Exists(draftPath) && new FileInfo(draftPath).Length < 4_000_000) {
              try {
                using var draft = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(draftPath));
                var id = draft.RootElement.GetProperty("chatId").GetString();
                var text = draft.RootElement.GetProperty("text").GetString() ?? "";
                var target = chats.FirstOrDefault(c => c.Id == id);
                if(target is not null) {
                    await LoadAsync(target); Draft = text[..Math.Min(text.Length,16000)];
                    if(draft.RootElement.TryGetProperty("attachments", out var array)) foreach(var item in array.EnumerateArray().Take(Limits.AttachmentCount)) {
                        var attachment = item.Deserialize<Attachment>(JsonDefaults.Options);
                        if(attachment is not null && File.Exists(_services.Paths.Resolve(attachment.RelativeOriginal))) Attachments.Add(attachment);
                    }
                }
              } catch(Exception error) { Diagnostics.Record(_services.Paths,"DRAFT_RESTORE",error); }
            }
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; Status = "Готово"; }
    }
    private void ReplaceList(List<Conversation> chats)
    {
        _allChats = chats.ToList(); OnPropertyChanged(nameof(SidebarProjects)); OnPropertyChanged(nameof(RecentChats));
        _ignoreSelection = true;
        try { Conversations.Clear(); foreach (var chat in chats.Where(c => _project is null || _project.Id.Length == 0 || c.ProjectId == _project.Id)) Conversations.Add(chat); SelectedConversation = chats.FirstOrDefault(c => c.Id == _current?.Id); }
        finally { _ignoreSelection = false; }
    }
    private async Task LoadAsync(Conversation chat)
    {
        var records = await Task.Run(() => _services.Store.LoadMessages(chat.Id));
        _current = chat; OnPropertyChanged(nameof(Title)); Messages.Clear(); Attachments.Clear();
        foreach (var record in records) Messages.Add(new MessageViewModel(record));
        UpdateLastAssistant();
        Draft = ""; Error = "";
        _ignoreSelection = true; SelectedConversation = Conversations.FirstOrDefault(c => c.Id == chat.Id); _ignoreSelection = false;
        ScrollRequested?.Invoke(); UpdateCommands();
    }
    private async Task SelectAsync(Conversation chat)
    {
        Busy = true;
        try { await LoadAsync(chat); }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task NewAsync()
    {
        Busy = true;
        try { var chat = new Conversation { ProjectId = _project is { Id.Length: > 0 } ? _project.Id : null }; await Task.Run(() => _services.Store.SaveConversation(chat)); Search = ""; ReplaceList(await Task.Run(() => _services.Store.List())); await LoadAsync(chat); }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task SearchAsync(string query)
    {
        _search?.Cancel(); _search?.Dispose(); _search = new CancellationTokenSource(); var token = _search.Token;
        try { await Task.Delay(300, token); var chats = await Task.Run(() => _services.Store.List(query), token); token.ThrowIfCancellationRequested(); ReplaceList(chats); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Fail(e); }
    }
    public void OpenSettings()
    {
        try
        {
            if (_dialogs.EditSettings()) { _settings = _services.Settings.Load(); Theme.Apply(_settings.Theme); OnPropertyChanged(nameof(ModelLabel)); OnPropertyChanged(nameof(FullDocument)); OnPropertyChanged(nameof(AnalyzeVisuals)); Error = ""; }
        }
        catch (Exception e) { Fail(e); }
    }
    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        if (Busy || _current is null) return;
        var selected = files.ToArray(); if (selected.Length == 0) return;
        if (selected.Length + Attachments.Count > Limits.AttachmentCount) { Error = "За один запрос можно добавить не более 50 файлов (включая содержимое архивов)."; return; }
        Busy = true; Error = ""; _operation = new CancellationTokenSource();
        try
        {
            foreach (var file in selected)
            {
                Status = "Чтение: " + Path.GetFileName(file);
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // Archive remains ONE staged attachment. Unpack only after Send.
                    var placeholder = await ArchiveReplay.StageAsync(_services.Paths, _current.Id, file, _operation.Token);
                    Attachments.Add(placeholder);
                }
                else
                {
                    var attachment = await _services.Import.ImportAsync(_current.Id, file, _operation.Token);
                    Attachments.Add(attachment);
                }
            }
            Status = "Подготовлено вложений: " + Attachments.Count + (Attachments.Any(ArchiveReplay.IsArchive) ? ". ZIP будет распакован после отправки." : ". Можно отправлять запрос.");
        }
        catch (OperationCanceledException) { Status = "Чтение остановлено."; }
        catch (Exception e) { Fail(e); Status = "Не все вложения подготовлены."; }
        finally { _operation.Dispose(); _operation = null; Busy = false; }
    }
    public async Task PasteAsync()
    {
        if (Busy) return; string? path = null;
        try { path = _dialogs.PasteImage(); if (path is not null) await AddFilesAsync([path]); }
        catch (Exception e) { Fail(e); }
        finally { if (path is not null) { try { File.Delete(path); } catch { } } }
    }
    private async Task SendAsync(bool regenerate = false)
    {
        if (_current is null || Busy || (!regenerate && string.IsNullOrWhiteSpace(Draft))) return;
        var replay = regenerate ? RegenerationPlanner.Select(Messages.Select(m => m.Record).ToList()) : null;
        var oldAssistant = replay is null ? null : Messages.FirstOrDefault(m => ReferenceEquals(m.Record, replay.Previous));
        var originalUser = replay is null ? null : Messages.FirstOrDefault(m => ReferenceEquals(m.Record, replay.User));
        if (regenerate && replay is null) return;
        if (!_services.Settings.HasKey || !_settings.CloudConsent) { OpenSettings(); if (!_services.Settings.HasKey || !_settings.CloudConsent) return; }
        var displayAttachments = regenerate ? originalUser!.Record.Attachments.ToList() : Attachments.ToList();
        var prompt = regenerate ? originalUser!.Record.Content : Draft.Trim();
        if (!regenerate) AppPaths.AtomicText(_services.Paths.Resolve("draft.json"), JsonDefaults.Write(new { chatId = _current.Id, text = "" }));
        var attachments = new List<Attachment>();
        // Archive processing begins only when the user sends the request.
        // The original ZIP remains visible as one item in the sent user message.
        if (displayAttachments.Any(ArchiveReplay.IsArchive))
        {
            Busy = true;
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Status = "Проверка архивов после отправки…";
            try
            {
                foreach (var pending in displayAttachments)
                {
                    _operation.Token.ThrowIfCancellationRequested();
                    if (!ArchiveReplay.IsArchive(pending)) { attachments.Add(pending); continue; }
                    var zipPath = await Task.Run(() => ArchiveReplay.Resolve(_services.Paths, _current.Id, pending), _operation.Token);
                    var staging = Path.Combine(Path.GetTempPath(), "ODA-archive-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var reserved = displayAttachments.Count(a => !ArchiveReplay.IsArchive(a)) - attachments.Count(a => displayAttachments.Contains(a));
                        var expanded = await ArchiveImport.ExpandDetailedAsync(zipPath, staging, Limits.AttachmentCount - attachments.Count - reserved, _operation.Token);
                        var firstImported = attachments.Count;
                        foreach (var extracted in expanded.Files)
                            attachments.Add(await _services.Import.ImportAsync(_current.Id, extracted.Path, _operation.Token, extracted.DisplayName));
                        Diagnostics.Record(_services.Paths, expanded.Skipped.Count > 0 ? "ZIP_EXPANDED_PARTIAL" : "ZIP_EXPANDED_OK");
                        if (expanded.Skipped.Count > 0)
                            attachments[firstImported].Warnings.Add("ZIP: не обработаны неподдерживаемые файлы: " + string.Join(", ", expanded.Skipped));
                    }
                    finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
                }
                if (attachments.Count > Limits.AttachmentCount) throw new AppException("ATTACHMENT_COUNT", "Слишком много файлов после распаковки ZIP.");
            }
            catch (OperationCanceledException) { Status = "Обработка архива остановлена. Сообщение не отправлено."; return; }
            catch (Exception e) { Fail(e); Status = "Архив не обработан. Сообщение не отправлено."; return; }
            finally { _operation.Dispose(); _operation = null; Busy = false; UpdateCommands(); }
        }
        else attachments.AddRange(displayAttachments);
        if (prompt.Length > Limits.UserCharacters) { Error = "Запрос превышает 16 000 знаков."; return; }
        var textSize = attachments.Sum(a => (long)a.Segments.Sum(s => s.Text.Length));
        var visualCount = AnalyzeVisuals ? attachments.Sum(a => a.Visuals.Count) : 0;
        if (textSize > Limits.TotalCharacters || visualCount > Limits.VisualsPerRequest) { Error = "Для одного запроса выберите не более 600 000 знаков текста и 50 подготовленных изображений. Уберите часть вложений или отключите визуальный анализ."; return; }
        if (attachments.Count > 0)
        {
            var explanation = $"Будут переданы в Ollama Cloud: запрос, ограниченная история этого чата, текст выбранных вложений ({textSize:N0} знаков) и {visualCount} подготовленных изображений.\n\n";
            if (textSize > Limits.ContextCharacters && FullDocument) explanation += "Для полного текста понадобятся промежуточные облачные запросы. Защитный предел — 64 запроса за операцию; они расходуют лимиты/баланс аккаунта.\n\n";
            if (!FullDocument && textSize > Limits.ContextCharacters) explanation += "Будет передана только часть текста, отобранная по запросу.\n\n";
            if (!AnalyzeVisuals && attachments.Any(a => a.Visuals.Count > 0)) explanation += "Визуальный анализ отключён. Содержимое изображений и сканов не распознается.\n\n";
            explanation += string.Join("\n", attachments.SelectMany(a => a.Warnings.Select(w => a.Name + ": " + w)));
            if (!_dialogs.Confirm("Отправить выбранные данные в облако?", explanation)) return;
        }
        Busy = true; Error = ""; _checkpoint = Task.CompletedTask; _operation = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var history = regenerate ? replay!.History : Messages.Select(m => m.Record).ToList(); var settings = JsonDefaults.Read<AppSettings>(JsonDefaults.Write(_settings));
        MessageViewModel? answer = null;
        try
        {
            if (!regenerate)
            {
                var user = new ChatMessage { ChatId = _current.Id, Role = "user", Content = prompt, Attachments = displayAttachments, Output = OutputKind.Chat };
                await Task.Run(() => _services.Store.SaveMessage(user)); Messages.Add(new MessageViewModel(user));
                Draft = ""; Attachments.Clear();
            }
            if (_current.Title == "Новый чат") { _current.Title = prompt.Split('\n')[0][..Math.Min(55, prompt.Split('\n')[0].Length)]; _current.Updated = DateTimeOffset.UtcNow; await Task.Run(() => _services.Store.SaveConversation(_current)); OnPropertyChanged(nameof(Title)); }
            answer = new MessageViewModel(new ChatMessage { ChatId = _current.Id, Role = "assistant", Status = "streaming" });
            await Task.Run(() => _services.Store.SaveMessage(answer.Record)); Messages.Add(answer); UpdateLastAssistant(); _streaming = answer; _buffer.Clear(); _lastCheckpoint = DateTimeOffset.UtcNow;
            ScrollRequested?.Invoke();
            var key = _services.Settings.ReadKey();
            var progress = new Progress<WorkProgress>(item =>
            {
                Status = item.Stage;
                if (_streaming is null) return;
                if (item.Delta is not null) _buffer.Append(item.Delta);
                if (item.CreatedArtifact is { } artifact && !_streaming.Record.Artifacts.Any(a => a.Sha256 == artifact.Sha256))
                {
                    _streaming.Record.Artifacts.Add(artifact);
                    _streaming.Refresh();
                    ScrollRequested?.Invoke();
                }
            });
            var project = await Task.Run(() => _services.Store.GetProject(_current.ProjectId));
            var projectContext = project is null ? "" :
                "Project: " + project.Name + "\nProject instructions: " + project.Instructions +
                "\nUser-maintained project notes: " + project.MemoryNotes;
            var result = await _services.Engine.GenerateAsync(_current.Id, answer.Record.Id, prompt, history, attachments, settings, OutputKind.Chat, key, progress, _operation.Token, projectContext);
            Flush(); _streaming = null;
            answer.Record.Content = result.Content;
            answer.Record.Artifacts = result.Artifacts;
            answer.Record.Note = result.Note;
            answer.Record.Status = result.Complete ? "complete" : "partial";
            if (regenerate && oldAssistant is not null)
            {
                oldAssistant.Record.Status = "superseded"; oldAssistant.Refresh();
                await Task.Run(() => _services.Store.SaveMessage(oldAssistant.Record));
            }
            answer.Refresh();
            Status = !result.Complete ? "Часть задачи не завершена — готовые файлы сохранены."
                : result.Artifacts.Count > 0 ? "Создано и проверено файлов: " + result.Artifacts.Count + "." : "Готово";
        }
        catch (OperationCanceledException)
        {
            Flush(); _streaming = null;
            if (answer is not null) { answer.Record.Status = "cancelled"; answer.Record.Note = "Обработка остановлена. Уже отправленные облачные запросы могли расходовать лимит. Незавершённые операции автоматически не повторяются."; if (answer.Record.Content.Length == 0) answer.Record.Content = answer.Record.Artifacts.Count > 0 ? "Операция остановлена; подтверждённые файлы сохранены ниже." : "Остановлено до получения ответа."; answer.Refresh(); }
            Status = "Остановлено";
        }
        catch (Exception e)
        {
            Flush(); _streaming = null; Fail(e);
            if (answer is not null) { answer.Record.Status = "failed"; answer.Record.Note = FriendlyError.Describe(e); if (answer.Record.Content.Length == 0) answer.Record.Content = answer.Record.Artifacts.Count > 0 ? "Произошёл сбой; подтверждённые файлы сохранены ниже." : "Ответ не завершён."; answer.Refresh(); }
            Status = "Операция не завершена";
        }
        finally
        {
            _streaming = null;
            try { await _checkpoint; } catch (Exception e) { Fail(e); }
            if (answer is not null) try { await Task.Run(() => _services.Store.SaveMessage(answer.Record)); } catch (Exception e) { Fail(e); Error = "Не удалось сохранить последнее сообщение. " + Error; }
            try { ReplaceList(await Task.Run(() => _services.Store.List(Search))); } catch (Exception e) { Fail(e); }
            _operation.Dispose(); _operation = null; Busy = false; UpdateLastAssistant(); ScrollRequested?.Invoke();
        }
    }
    private void Flush()
    {
        if (_streaming is null) return;
        if (_buffer.Length != _streaming.Record.Content.Length) { _streaming.Record.Content = _buffer.ToString(); _streaming.Refresh(); ScrollRequested?.Invoke(); }
        if (_checkpoint.IsFaulted) { _operation?.Cancel(); return; }
        if (_checkpoint.IsCompleted && DateTimeOffset.UtcNow - _lastCheckpoint > TimeSpan.FromSeconds(2))
        {
            var snapshot = JsonDefaults.Read<ChatMessage>(JsonDefaults.Write(_streaming.Record));
            _checkpoint = Task.Run(() => _services.Store.SaveMessage(snapshot)); _lastCheckpoint = DateTimeOffset.UtcNow;
        }
    }
    private void UpdateLastAssistant()
    {
        var last = Messages.LastOrDefault(m => m.Record.Role == "assistant" && m.Record.Status != "superseded");
        foreach (var message in Messages) message.IsLastAssistant = ReferenceEquals(message, last);
        RegenerateCommand.NotifyCanExecuteChanged();
    }
    private void RefreshProjects(List<WorkspaceProject> projects)
    {
        var previousId = _project?.Id ?? "";
        Projects.Clear(); Projects.Add(new WorkspaceProject { Id = "", Name = "Все чаты" });
        foreach (var project in projects) Projects.Add(project);
        _project = Projects.FirstOrDefault(p => p.Id == previousId) ?? Projects[0];
        OnPropertyChanged(nameof(ActiveProject)); OnPropertyChanged(nameof(ProjectLabel)); OnPropertyChanged(nameof(HasActiveProject)); OnPropertyChanged(nameof(SidebarProjects));
    }
    private async Task ReloadProjectsAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            ReplaceList(await Task.Run(() => _services.Store.List(Search)));
            var target = Conversations.FirstOrDefault();
            if (target is null && string.IsNullOrWhiteSpace(Search))
            {
                target = new Conversation { ProjectId = _project is { Id.Length: > 0 } ? _project.Id : null };
                await Task.Run(() => _services.Store.SaveConversation(target));
                ReplaceList(await Task.Run(() => _services.Store.List(Search)));
            }
            if (target is not null && (target.Id != _current?.Id || _current?.ProjectId != target.ProjectId))
                await LoadAsync(target);
            else if (target is null)
            {
                _current = null; Messages.Clear(); Draft = ""; Error = "";
                OnPropertyChanged(nameof(Title)); UpdateCommands();
            }
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task CreateProjectAsync()
    {
        var name = _dialogs.Prompt("Создать проект", "Новый проект")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        Busy = true;
        try
        {
            var project = new WorkspaceProject { Name = name };
            await Task.Run(() => _services.Store.SaveProject(project));
            RefreshProjects(await Task.Run(_services.Store.Projects));
            _project = Projects.First(p => p.Id == project.Id);
            OnPropertyChanged(nameof(ActiveProject)); OnPropertyChanged(nameof(ProjectLabel)); OnPropertyChanged(nameof(HasActiveProject));
            Status = "Проект создан. Новые чаты будут сохраняться в нём.";
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
        await ReloadProjectsAsync();
    }
    private async Task EditProjectAsync()
    {
        if (_project is not { Id.Length: > 0 } project) return;
        var instructions = _dialogs.Prompt("Инструкции проекта (до 4000 знаков)", project.Instructions, 4000, true);
        if (instructions is null) return;
        var memory = _dialogs.Prompt("Заметки памяти проекта (до 4000 знаков, передаются в облако)", project.MemoryNotes, 4000, true);
        if (memory is null) return;
        Busy = true;
        try
        {
            project.Instructions = instructions; project.MemoryNotes = memory;
            await Task.Run(() => _services.Store.SaveProject(project));
            RefreshProjects(await Task.Run(_services.Store.Projects));
            Status = "Инструкции и заметки проекта сохранены локально.";
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task MoveToProjectAsync()
    {
        if (_current is null) return;
        var requested = _dialogs.Prompt("Переместить текущий чат (пусто = без проекта). Доступно: " +
            string.Join(", ", Projects.Where(x => x.Id.Length > 0).Select(x => x.Name)), "")?.Trim();
        if (requested is null) return;
        var project = string.IsNullOrWhiteSpace(requested) ? null : Projects.FirstOrDefault(x =>
            x.Id.Length > 0 && string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (requested.Length > 0 && project is null)
        { Error = "Проект с таким названием не найден. Чат не перемещён."; return; }
        if (!_dialogs.Confirm("Переместить чат?", "Переместить открытый чат в " + (project?.Name ?? "общие чаты") + "?")) return;
        Busy = true;
        try
        {
            _current.ProjectId = project?.Id;
            await Task.Run(() => _services.Store.SaveConversation(_current));
            ReplaceList(await Task.Run(() => _services.Store.List(Search)));
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task RenameAsync()
    {
        if (_current is null) return; var title = _dialogs.Prompt("Название чата", _current.Title);
        if (string.IsNullOrWhiteSpace(title)) return;
        Busy = true;
        try { _current.Title = title.Trim()[..Math.Min(title.Trim().Length, 100)]; _current.Updated = DateTimeOffset.UtcNow; await Task.Run(() => _services.Store.SaveConversation(_current)); ReplaceList(await Task.Run(() => _services.Store.List(Search))); OnPropertyChanged(nameof(Title)); }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task DeleteAsync()
    {
        if (_current is null || !_dialogs.Confirm("Удалить чат?", "Чат, локальные копии его вложений и созданные в нём файлы будут удалены. Копии, сохранённые через «Сохранить как», останутся. Действие необратимо.")) return;
        Busy = true;
        try
        {
            var id = _current.Id; await Task.Run(() => _services.Store.DeleteConversation(id)); _current = null;
            var chats = await Task.Run(() => _services.Store.List());
            Search = ""; ReplaceList(chats);
            var next = Conversations.FirstOrDefault();
            if (next is null)
            {
                next = new Conversation { ProjectId = _project is { Id.Length: > 0 } ? _project.Id : null };
                await Task.Run(() => _services.Store.SaveConversation(next));
                ReplaceList(await Task.Run(() => _services.Store.List()));
            }
            await LoadAsync(next);
        }
        catch (Exception e) { Fail(e); }
        finally { Busy = false; }
    }
    private async Task RetryAsync()
    {
        var last = Messages.LastOrDefault(m => m.Record.Role == "user")?.Record; if (last is null) return;
        if (!_dialogs.Confirm("Повторить запрос?", "Будет выполнен новый облачный запрос с расходованием лимита. При создании документа появится новый файл; старый не перезаписывается.")) return;
        Draft = last.Content; Attachments.Clear(); foreach (var attachment in last.Attachments) Attachments.Add(attachment); 
        await SendAsync();
    }
    public void Cancel() => _operation?.Cancel();
    public void Shutdown() { Cancel(); _search?.Cancel(); _streamTimer.Stop(); }
    public void Fail(Exception exception)
    {
        Error = FriendlyError.Describe(exception);
        var code = exception is AppException known ? known.Code : exception is System.Text.Json.JsonException ? "APP_JSON" : "UI_ERROR";
        Diagnostics.Record(_services.Paths, code, exception);
    }
}
