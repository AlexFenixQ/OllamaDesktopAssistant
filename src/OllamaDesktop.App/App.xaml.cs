using System.Runtime.InteropServices;
using System.Windows;
using OllamaDesktop.Core;
namespace OllamaDesktop.App;
public partial class App : Application
{
    public static bool SmokeTest { get; set; }
    public static bool Preview { get; set; }
    private Mutex? _mutex;
    private bool _ownsMutex;
    private Services? _services;
    private string? _smokeRoot;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,args)=>{if(ReferenceEquals(sender,args.OriginalSource))WindowBounds.Fit((Window)sender);}));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) ||
            (RuntimeInformation.OSArchitecture != Architecture.X64 &&
             !(RuntimeInformation.OSArchitecture == Architecture.Arm64 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))))
        { MessageBox.Show("Требуется Windows 10/11: x64 либо Windows 11 ARM64 с эмуляцией x64.", "Ollama Desktop"); Shutdown(2); return; }
        if (!SmokeTest && !Preview)
        {
            _mutex = new Mutex(true, @"Local\OllamaDesktopAssistant", out _ownsMutex);
            if (!_ownsMutex) { MessageBox.Show("Приложение уже запущено. Откройте его на панели задач.", "Ollama Desktop"); Shutdown(); return; }
        }
        var paths = (SmokeTest || Preview) ? new AppPaths(_smokeRoot = Path.Combine(Path.GetTempPath(), "OllamaDesktop-Smoke-" + Guid.NewGuid().ToString("N"))) : AppPaths.Default();
        _services = new Services(paths);
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.Record(paths, "UNHANDLED_UI", args.Exception);
            args.Handled = true;
            MessageBox.Show("Приложение закрывается после ошибки. " + FriendlyError.Describe(args.Exception), "Ollama Desktop");
            Shutdown(1);
        };
        if(Preview) {
            _services.Store.Initialize();
            var project=new WorkspaceProject { Name="Рабочие документы",Instructions="Отвечай по-русски. Указывай источники." }; _services.Store.SaveProject(project);
            var general=new Conversation { Title="Добро пожаловать" }; _services.Store.SaveConversation(general);
            var chat=new Conversation { Title="Отчёт и презентация",ProjectId=project.Id }; _services.Store.SaveConversation(chat);
            _services.Store.SaveMessage(new ChatMessage { ChatId=chat.Id,Role="user",Content="Помоги подготовить итоговый отчёт и презентацию по проекту.",Status="complete" });
            _services.Store.SaveMessage(new ChatMessage { ChatId=chat.Id,Role="assistant",Content="## Давайте начнём\n\nПрикрепите документы или архив — я помогу разобрать материалы и подготовить результат.\n\n• **Word** — итоговый отчёт\n• **Excel** — таблицы и расчёты\n• **PowerPoint** — презентация\n\nИстория и файлы сохраняются на вашем компьютере. Инструкции проекта учитываются в каждом его чате.",Status="complete" });
        }
        var settings = _services.Settings.Load(); Theme.Apply(settings.Theme);
        MainWindow = new MainWindow(_services, settings); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        if (_smokeRoot is not null) { try { Directory.Delete(_smokeRoot, true); } catch { } }
        base.OnExit(e);
    }
}
