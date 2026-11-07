using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using OllamaDesktop.App.Infrastructure;
using OllamaDesktop.App.ViewModels;
using OllamaDesktop.Core;

namespace OllamaDesktop.App;

internal static class SmokeChecks
{
    public static async Task<List<string>> RunAsync(Window owner, Services services)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var dialogs = new SmokeDialogs();
        var vm = new MainViewModel(services, dialogs, new AppSettings());
        try
        {
            await vm.InitializeAsync();
            Check(!vm.HasError && vm.Conversations.Count > 0, "WPF history initialization");
            dialogs.Answers.Enqueue("Smoke project");
            await vm.CreateProjectCommand.ExecuteAsync(null);
            Check(!vm.HasError && vm.ActiveProject?.Id.Length == 32 && vm.EditProjectCommand.CanExecute(null), "Project creation and edit command");
            dialogs.Answers.Enqueue(new string('A', 1000));
            dialogs.Answers.Enqueue(new string('B', 1000));
            await vm.EditProjectCommand.ExecuteAsync(null);
            Check(!vm.HasError && services.Store.GetProject(vm.ActiveProject!.Id)?.Instructions.Length == 1000, "Project notes exceed 100 characters");
            var dialog = new DialogWindow(owner, "Smoke", "text", true, 4000, true);
            var input = ((DockPanel)dialog.Content).Children.OfType<TextBox>().Single();
            Check(input.MaxLength == 4000 && input.AcceptsReturn, "Multiline project editor limit");
            vm.FullDocument = false; vm.AnalyzeVisuals = false;
            var saved = services.Settings.Load();
            Check(!saved.FullDocument && !saved.AnalyzeVisuals, "Analysis options survive reload");
            await vm.DeleteCommand.ExecuteAsync(null);
            Check(!vm.HasError && vm.Conversations.Count > 0 && vm.Conversations.All(c => c.ProjectId == vm.ActiveProject!.Id), "Delete respects active project");
            var text = services.Paths.Resolve("Staging/smoke.txt");
            await File.WriteAllTextAsync(text, "Smoke text");
            await vm.AddFilesAsync([text]);
            Check(!vm.HasError && vm.Attachments.Any(a => a.Segments.Any(s => s.Text.Contains("Smoke text"))), "Isolated import worker uses bound data root");
            var png = services.Paths.Resolve("Staging/smoke.png");
            PngWriter.WriteBgra(png, 2, 2, 8, Enumerable.Repeat((byte)255, 16).ToArray());
            await vm.AddFilesAsync([png]);
            Check(!vm.HasError && vm.Attachments.Any(a => a.Visuals.Count == 1), "Isolated native image worker");
            var zipPath = services.Paths.Resolve("Staging/smoke.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            { using var writer = new StreamWriter(archive.CreateEntry("smoke.txt").Open()); writer.Write("Smoke archive"); }
            await vm.AddFilesAsync([zipPath]);
            var attachment = vm.Attachments.Single(ArchiveReplay.IsArchive);
            Check(!vm.HasError && attachment.RelativeOriginal.Replace('\\', '/').StartsWith("Attachments/"), "ZIP staged under chat ownership");
            var reloaded = JsonDefaults.Read<Attachment>(JsonDefaults.Write(attachment));
            Check(File.Exists(ArchiveReplay.Resolve(services.Paths, vm.SelectedConversation!.Id, reloaded)), "ZIP replay after reload");
            var call = new ToolCall("create_archive", System.Text.Json.JsonSerializer.SerializeToElement(new { title = "Smoke", files = new[] { new { name = "smoke.txt", content = "42" } } }));
            var artifact = ArchiveService.Create(services.Paths, vm.SelectedConversation!.Id, Guid.NewGuid().ToString("N"), call, [], CancellationToken.None);
            Check(File.Exists(SafeShell.CheckArtifact(services.Paths, artifact)), "ZIP Open and Save As validation");
            Theme.Apply("Light"); owner.UpdateLayout(); Theme.Apply("Dark"); owner.UpdateLayout();
            Check(true, "Light and dark theme construction");
            Check(vm.SidebarProjects.Any(g=>g.Chats.Count>0), "Project sidebar contains its chats");
            var projectId=vm.ActiveProject!.Id; var project=services.Store.GetProject(projectId)!;
            dialogs.Answers.Enqueue("Renamed smoke project"); await vm.RenameProjectAsync(project);
            Check(services.Store.GetProject(projectId)?.Name=="Renamed smoke project", "Project rename preserves context");
            vm.Draft="Unsaved draft before update"; await vm.PersistDraftAsync();
            var restored=new MainViewModel(services,dialogs,services.Settings.Load());
            try { await restored.InitializeAsync(); Check(restored.Draft==vm.Draft && restored.Attachments.Count==vm.Attachments.Count, "Draft and staged attachments survive update restart"); } finally { restored.Shutdown(); }
            await vm.DeleteProjectAsync(services.Store.GetProject(projectId)!);
            Check(services.Store.GetProject(projectId) is null && services.Store.List().Any(c=>c.Id==vm.SelectedConversation?.Id), "Project UI delete keeps chats and files");
            Check(((ColumnDefinition)owner.FindName("FilesColumn")).Width.Value==0, "Files panel collapsed by default");
            return checks;
        }
        finally { vm.Shutdown(); }
    }
    private sealed class SmokeDialogs : IUserDialogs
    {
        public Queue<string> Answers { get; } = new();
        public string[] PickFiles() => [];
        public string? PasteImage() => null;
        public bool Confirm(string title, string message) => true;
        public string? Prompt(string title, string value, int maxLength = 100, bool multiline = false) => Answers.Dequeue();
        public bool EditSettings() => false;
    }
}
