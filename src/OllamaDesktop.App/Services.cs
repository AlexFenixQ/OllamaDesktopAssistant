using OllamaDesktop.Core;
using OllamaDesktop.App.Infrastructure;

namespace OllamaDesktop.App;

public sealed class Services : IDisposable
{
    public AppPaths Paths { get; }
    public ChatStore Store { get; }
    public SettingsStore Settings { get; }
    public OllamaClient Client { get; } = new();
    public ImportService Import { get; }
    public ConversationEngine Engine { get; }
    public Services(AppPaths paths)
    {
        Paths = paths; Store = new ChatStore(paths); Settings = new SettingsStore(paths); Import = new ImportService(paths); Engine = new ConversationEngine(paths, Client);
    }
    public void Dispose() => Client.Dispose();
}
