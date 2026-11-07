using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OllamaDesktop.Core;

public sealed record MediaProbe(double Duration,bool Audio,bool Video,int Width,int Height);
public static class MediaTools
{
    private const string Formats="mov,matroska,avi,asf,mpegts,mpegvideo,wav,w64,mp3,aac,flac,ogg,aiff";
    public static string Executable(string name) {
        if(name is not ("ffmpeg" or "ffprobe"))throw new ArgumentException(nameof(name));
        var path=Path.Combine(AppContext.BaseDirectory,"MediaTools",name+".exe");
        if(!File.Exists(path))throw new AppException("MEDIA_TOOLS_MISSING","Не найден встроенный обработчик аудио и видео. Установите полный выпуск приложения.");
        AppPaths.AssertNotReparse(path);return path;
    }
    public static void CheckHeader(string path) {
        using var stream=File.OpenRead(path);Span<byte> b=stackalloc byte[64];var n=stream.Read(b);b=b[..n];
        static bool Prefix(ReadOnlySpan<byte> data,string text)=>data.Length>=text.Length && Encoding.ASCII.GetString(data[..text.Length])==text;
        // Reject playlists, text manifests, arbitrary images and executable masquerades before probing.
        var valid=(n>=12 && ((Prefix(b,"RIFF") && (Encoding.ASCII.GetString(b.Slice(8,4)) is "WAVE" or "AVI "))||Encoding.ASCII.GetString(b.Slice(4,4))=="ftyp"))||Prefix(b,"ID3")||Prefix(b,"fLaC")||Prefix(b,"OggS")||Prefix(b,"FORM")||
            (n>=4 && b[0]==0x1a && b[1]==0x45 && b[2]==0xdf && b[3]==0xa3)||
            (n>=4 && b[0]==0x30 && b[1]==0x26 && b[2]==0xb2 && b[3]==0x75)||
            (n>=2 && b[0]==0xff && (b[1]&0xe0)==0xe0)||
            (n>=4 && b[0]==0 && b[1]==0 && b[2]==1 && (b[3] is 0xba or 0xb3))||
            (n>=1 && b[0]==0x47 && Path.GetExtension(path).Equals(".ts",StringComparison.OrdinalIgnoreCase));
        if(!valid)throw new AppException("MEDIA_SIGNATURE","Содержимое не похоже на поддерживаемую медиазапись. Плейлисты, ссылки и сетевые потоки не открываются.");
    }
    public static async Task<string> RunAsync(string name,IEnumerable<string> arguments,TimeSpan timeout,CancellationToken ct,Action<string>? errorLine=null) {
        var start=new ProcessStartInfo(Executable(name)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in arguments)start.ArgumentList.Add(arg);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(timeout);
        using var p=new Process{StartInfo=start};if(!p.Start())throw new AppException("MEDIA_START","Не удалось запустить обработчик записи.");
        using var guard=new NativeJobGuard(p,1536);
        try {
            var output=BoundedText.ReadAsync(p.StandardOutput.BaseStream,1_000_000,limit.Token);
            var errors=Task.Run(async()=>{var tail=new StringBuilder();while(await p.StandardError.ReadLineAsync(limit.Token) is {} line){if(line.Length>8192)throw new AppException("MEDIA_LOG","Необычно большой ответ декодера.");errorLine?.Invoke(line);if(tail.Length<16000)tail.AppendLine(line);}return tail.ToString();},limit.Token);
            await p.WaitForExitAsync(limit.Token);var text=await output;var error=await errors;
            if(p.ExitCode!=0)throw new AppException("MEDIA_DECODE","Запись не декодируется: повреждена, защищена или содержит неподдерживаемый кодек. Исходный файл сохранён.");return text;
        } catch(OperationCanceledException) when(!ct.IsCancellationRequested) {throw new AppException("MEDIA_TIMEOUT","Превышено время обработки части записи. Уже подготовленные данные сохранены.");}
        finally{if(!p.HasExited){try{p.Kill(true);await p.WaitForExitAsync(CancellationToken.None);}catch(InvalidOperationException){}}}
    }
    public static async Task<MediaProbe> ProbeAsync(string path,CancellationToken ct) {
        CheckHeader(path);
        var json=await RunAsync("ffprobe",["-hide_banner","-v","error","-protocol_whitelist","file","-format_whitelist",Formats,"-probesize","8388608","-analyzeduration","10000000","-show_entries","format=duration:stream=codec_type,duration,width,height","-of","json",path],TimeSpan.FromSeconds(45),ct);
        using var doc=JsonDocument.Parse(json);var duration=0d;bool audio=false,video=false;int width=0,height=0;
        static double Number(JsonElement x,string key)=>x.TryGetProperty(key,out var value) && double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:0;
        if(doc.RootElement.TryGetProperty("format",out var f))duration=Number(f,"duration");
        foreach(var s in doc.RootElement.GetProperty("streams").EnumerateArray()){var type=s.GetProperty("codec_type").GetString();audio|=type=="audio";video|=type=="video";duration=Math.Max(duration,Number(s,"duration"));if(type=="video"){width=Math.Max(width,(int)Number(s,"width"));height=Math.Max(height,(int)Number(s,"height"));}}
        if(!audio&&!video)throw new AppException("MEDIA_EMPTY","В файле нет доступных звуковых или видеодорожек.");
        _=MediaTime.Chunks(duration).First();if((long)width*height>40_000_000)throw new AppException("MEDIA_PIXELS","Кадр превышает 40 миллионов пикселей.");
        return new(duration,audio,video,width,height);
    }
    private static string N(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    public static Task<string> AudioChunkAsync(string input,string output,double start,double duration,CancellationToken ct)=>RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-protocol_whitelist","file","-format_whitelist",Formats,"-ss",N(start),"-i",input,"-t",N(duration),"-map","0:a:0","-vn","-sn","-dn","-ac","1","-ar","16000","-c:a","pcm_s16le","-f","wav",output],TimeSpan.FromMinutes(10),ct);
    public static Task<string> FrameAsync(string input,string output,double time,CancellationToken ct)=>RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-protocol_whitelist","file","-format_whitelist",Formats,"-ss",N(time),"-i",input,"-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf","scale=1280:1280:force_original_aspect_ratio=decrease","-threads","2",output],TimeSpan.FromMinutes(3),ct);
    public static async Task<List<double>> ScenesAsync(string input,double duration,IProgress<string>? progress,CancellationToken ct) {
        var slots=new double?[40];var last=DateTime.MinValue;
        await RunAsync("ffmpeg",["-hide_banner","-v","info","-nostdin","-protocol_whitelist","file","-format_whitelist",Formats,"-i",input,"-map","0:v:0","-an","-sn","-dn","-vf",@"scale=320:-2,select=gt(scene\,0.18),showinfo","-threads","2","-f","null","-"],TimeSpan.FromHours(3),ct,line=>{
            var m=Regex.Match(line,@"pts_time:([0-9.]+)");if(!m.Success||!double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var time)||time<0||time>duration)return;
            var slot=Math.Clamp((int)(time/duration*slots.Length),0,slots.Length-1);slots[slot]??=time;
            if(DateTime.UtcNow-last>TimeSpan.FromSeconds(1)){progress?.Report("Поиск смен сцен: "+MediaTime.Format(time)+" / "+MediaTime.Format(duration));last=DateTime.UtcNow;}
        });return slots.Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
    }
}
