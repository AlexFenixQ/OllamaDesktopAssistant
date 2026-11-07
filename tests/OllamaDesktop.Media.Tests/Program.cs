using System.IO;
using OllamaDesktop.Core;
using OllamaDesktop.App.Infrastructure;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args is ["--extract"])return OllamaDesktop.App.Program.Main(args);
        return Run(args).GetAwaiter().GetResult();
    }
    private static async Task<int> Run(string[] args)
    {
        if(args.Length!=3)throw new ArgumentException("input.mp4 isolated-data-root report.json");
        var paths=new AppPaths(args[1]);var chat=Guid.NewGuid().ToString("N");var importer=new ImportService(paths);
        using var stop=new CancellationTokenSource();
        try {
            var first=await importer.ImportAsync(chat,args[0],stop.Token,progress:new InlineProgress(s=>{if(s.StartsWith("Поиск смен сцен"))stop.Cancel();}));
            if(!stop.IsCancellationRequested||!first.CanResume||!first.Media!.AudioComplete||first.Media.Lines.Count==0)throw new Exception("Cancellation checkpoint failed");
            var text=File.ReadAllText(paths.Resolve(first.Media.TranscriptArtifact!.RelativePath));
            if(!text.Contains("125")||!text.Contains("Мария")||!text.Contains("Конференция завершена"))throw new Exception("Synthetic recording evidence missing");
            // Serialize/reload exactly as a persisted draft, then resume in a NEW worker.
            var saved=JsonDefaults.Read<Attachment>(JsonDefaults.Write(first));var repeatedAsr=false;
            var completed=await importer.ResumeAsync(chat,saved,new InlineProgress(s=>{if(s.StartsWith("Распознавание "))repeatedAsr=true;}),CancellationToken.None);
            if(completed.CanResume||completed.Visuals.Count<20||repeatedAsr||completed.Media!.Lines.Count!=first.Media.Lines.Count)throw new Exception("Resume repeated/dropped content");
            if(AppPaths.Hash(paths.Resolve(completed.RelativeOriginal))!=completed.Sha256)throw new Exception("Original changed");
            AppPaths.AtomicText(args[2],JsonDefaults.Write(new{version=BuildInfo.Version,passed=true,checks=new[]{"real worker and nested native job","full Russian ASR with final sentence","cancel after audio","partial TXT preserved","serialized checkpoint restored","resume in new process without repeated ASR","video frames across timeline","original SHA-256 preserved"},lines=completed.Media.Lines.Count,frames=completed.Visuals.Count}));
            return 0;
        }catch(Exception e){AppPaths.AtomicText(args[2],JsonDefaults.Write(new{version=BuildInfo.Version,passed=false,code=e is AppException a?a.Code:e.GetType().Name}));return 1;}
    }
    private sealed class InlineProgress(Action<string> callback):IProgress<string>{public void Report(string value)=>callback(value);}
}
