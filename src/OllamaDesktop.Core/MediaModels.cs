using System.Security.Cryptography;
using System.Net.Http;
namespace OllamaDesktop.Core;

public sealed record TranscriptLine(double StartSeconds,double EndSeconds,string Text);
public sealed record MediaInterval(double StartSeconds,double EndSeconds,string State);
public sealed class MediaMetadata
{
    public double DurationSeconds { get; set; }
    public double ProcessedSeconds { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
    public bool AudioComplete { get; set; }
    public bool FramesComplete { get; set; }
    public string SpeechModel { get; set; }="small";
    public string TranscriptRelative { get; set; }="";
    public Artifact? TranscriptArtifact { get; set; }
    public List<TranscriptLine> Lines { get; set; }=[];
    public List<MediaInterval> Intervals { get; set; }=[];
    public string Status => AudioComplete && FramesComplete?"готово":$"подготовлено {MediaTime.Format(ProcessedSeconds)} из {MediaTime.Format(DurationSeconds)}";
}
public static class MediaTime
{
    public static string Format(double seconds) { var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"; }
    public static IEnumerable<(double Start,double Duration)> Chunks(double duration,double size=300) {
        if(!double.IsFinite(duration)||duration<=0||duration>Limits.MediaSeconds||size<=0)throw new AppException("MEDIA_DURATION","Запись должна быть непустой и длиться не более 6 часов.");
        for(double start=0;start<duration;start+=size)yield return(start,Math.Min(size,duration-start));
    }
    public static List<double> UniformFrames(double duration,int count=24)=>Enumerable.Range(0,count).Select(i=>Math.Clamp(duration*(i+0.5)/count,0,Math.Max(0,duration-0.1))).Distinct().ToList();
}
public sealed record SpeechModelDefinition(string Id,string Url,string Sha256,long MaxBytes);
public sealed class SpeechModelStore
{
    public static readonly SpeechModelDefinition Small=new("small","https://huggingface.co/ggerganov/whisper.cpp/resolve/90a64d80ea254cf67575b41a5971f972c79f7b45/ggml-small.bin","1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B",500_000_000);
    public static readonly SpeechModelDefinition Medium=new("medium","https://huggingface.co/ggerganov/whisper.cpp/resolve/13ce9e800d8d6aeab537db9f6d1aa62d3e540c4c/ggml-medium.bin","6C14D5ADEE5F86394037B4E4E8B59F1673B6CEE10E3CF0B11BBDBEE79C156208",1_600_000_000);
    private static readonly SemaphoreSlim Gate=new(1,1);
    private readonly string _root;
    public SpeechModelStore(AppPaths paths) {
        var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OllamaDesktopAssistant","Data");
        _root=Environment.GetEnvironmentVariable("ODA_SPEECH_MODEL_ROOT") ?? (string.Equals(paths.Root,expected,StringComparison.OrdinalIgnoreCase)?Path.Combine(Path.GetDirectoryName(paths.Root)!,"Models","Speech"):paths.Resolve("Models/Speech"));
        _root=Path.GetFullPath(_root);for(var p=_root;p is not null;p=Path.GetDirectoryName(p))if(Directory.Exists(p))AppPaths.AssertNotReparse(p);
    }
    public string Root=>_root;
    public static SpeechModelDefinition Definition(string id)=>id switch{"small"=>Small,"medium"=>Medium,_=>throw new AppException("SPEECH_MODEL","Неизвестная речевая модель.")};
    public string ModelPath(string id)=>Path.Combine(_root,"ggml-"+Definition(id).Id+".bin");
    public async Task<string> VerifyAsync(string id,CancellationToken ct) {
        var definition=Definition(id);var path=ModelPath(id);
        if(!File.Exists(path))throw new AppException("SPEECH_MODEL_MISSING","Речевая модель ещё не загружена. Прикрепите запись для подготовки.");
        AppPaths.AssertNotReparse(path);using var stream=File.OpenRead(path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
        if(!hash.Equals(definition.Sha256,StringComparison.OrdinalIgnoreCase))throw new AppException("SPEECH_MODEL_HASH","Проверка речевой модели не пройдена. Повреждённый файл не используется.");return path;
    }
    public async Task EnsureAsync(string id,IProgress<string>? progress,CancellationToken ct) {
        await Gate.WaitAsync(ct);string? temp=null;
        try {
            var d=Definition(id);if(File.Exists(ModelPath(id))){progress?.Report("Проверка речевой модели…");await VerifyAsync(id,ct);return;}
            Directory.CreateDirectory(_root);var drive=new DriveInfo(Path.GetPathRoot(_root)!);if(drive.AvailableFreeSpace<d.MaxBytes+256L*1024*1024)throw new AppException("MODEL_DISK","Недостаточно места для речевой модели.");
            temp=Path.Combine(_root,"download-"+Guid.NewGuid().ToString("N")+".part");using var http=new HttpClient{Timeout=TimeSpan.FromHours(1)};http.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaDesktop/"+BuildInfo.Version);
            using var response=await http.GetAsync(d.Url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();var total=response.Content.Headers.ContentLength??0;if(total>d.MaxBytes)throw new AppException("MODEL_SIZE","Размер речевой модели не соответствует ожидаемому.");
            await using(var input=await response.Content.ReadAsStreamAsync(ct))await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true)) {
                var buffer=new byte[131072];long bytes=0;int read;var last=DateTime.MinValue;
                while((read=await input.ReadAsync(buffer,ct))>0){bytes+=read;if(bytes>d.MaxBytes)throw new AppException("MODEL_SIZE","Превышен допустимый размер модели.");await output.WriteAsync(buffer.AsMemory(0,read),ct);if(DateTime.UtcNow-last>TimeSpan.FromMilliseconds(500)){progress?.Report($"Загрузка речи Whisper {id}: {bytes/1048576:N0}"+(total>0?$" / {total/1048576:N0}":"")+" МиБ…");last=DateTime.UtcNow;}}
            }
            progress?.Report("Проверка SHA-256 речевой модели…");using(var stream=File.OpenRead(temp))if(!Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).Equals(d.Sha256,StringComparison.OrdinalIgnoreCase))throw new AppException("SPEECH_MODEL_HASH","Загруженная модель повреждена. Установка отменена.");
            File.Move(temp,ModelPath(id));temp=null;
        } finally {if(temp is not null && File.Exists(temp))File.Delete(temp);Gate.Release();}
    }
}
