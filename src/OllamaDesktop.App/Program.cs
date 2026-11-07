using System.IO;
using System.Text;
using System.Windows;
using OllamaDesktop.Core;

namespace OllamaDesktop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--extract") return Extract();
        try
        {
            App.Preview = args.Length == 1 && args[0] == "--preview";
            App.SmokeTest = args.Length == 1 && args[0] == "--smoke-test";
            var app = new App(); app.InitializeComponent(); return app.Run();
        }
        catch (Exception e)
        {
            MessageBox.Show(FriendlyError.Describe(e), "Ollama Desktop", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
        }
    }
    private static int Extract()
    {
        WorkerResult result;
        try
        {
            using var input = Console.OpenStandardInput();
            var text = BoundedText.ReadAsync(input, 16_000, CancellationToken.None).GetAwaiter().GetResult();
            var job = JsonDefaults.Read<ExtractionJob>(text);
            var root = Environment.GetEnvironmentVariable("ODA_WORKER_DATA_ROOT");
            var paths = string.IsNullOrWhiteSpace(root) ? AppPaths.Default() : new AppPaths(root);
            if (FileGuard.IsMedia("file"+job.Extension)) {
                var progress=new Progress<string>(stage=>{ lock(Console.Error) { Console.Error.WriteLine(JsonDefaults.Write(new { stage })); Console.Error.Flush(); } });
                result=new WorkerResult { Attachment=new MediaExtraction(paths).ExtractAsync(job,progress,CancellationToken.None).GetAwaiter().GetResult() };
            } else result = new WorkerResult { Attachment = new ExtractionEngine(paths).Extract(job) };
        }
        catch (Exception e)
        {
            result = new WorkerResult { ErrorCode = e is AppException known ? known.Code : "FILE_PROCESSING", ErrorMessage = e is AppException ? e.Message : "Файл повреждён, защищён паролем, слишком сложен или не поддерживается. Оригинал не изменён." };
        }
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)); output.Write(JsonDefaults.Write(result)); output.Flush();
        return result.Attachment is null ? 2 : 0;
    }
}
