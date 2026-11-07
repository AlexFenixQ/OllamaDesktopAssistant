using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OllamaDesktop.Core;

public sealed record MediaProbe(double Duration,bool Audio,bool Video,int Width,int Height,long Frames=0,int AudioTracks=0,int UnknownTracks=0);
public static class MediaTools
{
    private const string Formats="mov,matroska,webm,avi,asf,mpegts,mpegvideo,mpeg,vob,flv,rm,ogg,ogv,mxf,nut,nsv,h264,hevc,av1,ivf,yuv4mpegpipe,dv,mjpeg,bink,smacker,flic,swf,wav,w64,mp3,mp2,aac,flac,aiff,au,caf,amr,ac3,eac3,dts,ape,wv,tta,tak,loas,g722,g723_1,g726,g726le,ilbc,spdif,truehd,mlp,gif,apng,webp_pipe,webp";
    public static string Executable(string name) {
        if(name is not ("ffmpeg" or "ffprobe"))throw new ArgumentException(nameof(name));
        var path=Path.Combine(AppContext.BaseDirectory,"MediaTools",name+".exe");
        if(!File.Exists(path))throw new AppException("MEDIA_TOOLS_MISSING","Не найден встроенный обработчик аудио и видео. Установите полный выпуск приложения.");
        AppPaths.AssertNotReparse(path);return path;
    }
    public static void CheckHeader(string path) {
        using var stream=File.OpenRead(path);Span<byte> b=stackalloc byte[64];var n=stream.Read(b);b=b[..n];
        static bool Prefix(ReadOnlySpan<byte> data,string text)=>data.Length>=text.Length && Encoding.ASCII.GetString(data[..text.Length])==text;
        // A strict demuxer allowlist below decides the actual container. Reject
        // executable/document/playlist masquerades without excluding valid codecs.
        var prefix=Encoding.ASCII.GetString(b).TrimStart();
        if(n==0||Prefix(b,"MZ")||Prefix(b,"PK")||Prefix(b,"%PDF")||prefix.StartsWith("#EXTM3U",StringComparison.OrdinalIgnoreCase)||prefix.StartsWith("<",StringComparison.Ordinal)||prefix.StartsWith("[playlist]",StringComparison.OrdinalIgnoreCase))
            throw new AppException("MEDIA_SIGNATURE","Это не локальная медиазапись: исполняемые файлы, документы, плейлисты и сетевые источники не открываются.");
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
        var json=await RunAsync("ffprobe",["-hide_banner","-v","error","-protocol_whitelist","file","-format_whitelist",Formats,"-probesize","8388608","-analyzeduration","10000000","-show_entries","format=duration:stream=codec_type,codec_name,duration,width,height,nb_frames:stream_disposition=attached_pic","-of","json",path],TimeSpan.FromSeconds(45),ct);
        var info=ParseProbe(json);
        // Some AVI exporters label a service stream as audio and give it a bogus
        // duration. Decode the primary audio locally to null in this exceptional
        // case, so real audio beyond a video stream cannot be silently omitted.
        if(info.Audio&&info.UnknownTracks>0){
            var output=await RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-nostats","-progress","pipe:1","-protocol_whitelist","file,pipe","-format_whitelist",Formats,"-i",path,"-map","0:a:0","-vn","-sn","-dn","-c:a","pcm_s16le","-f","null","-"],TimeSpan.FromMinutes(10),ct);
            var measured=Regex.Matches(output,@"(?m)^out_time_us=(\d+)\r?$").Select(m=>double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)/1_000_000d).DefaultIfEmpty().Max();
            if(measured<=0)throw new AppException("MEDIA_AUDIO_DURATION","Не удалось подтвердить длительность основной звуковой дорожки.");
            info=info with{Duration=Math.Max(info.Duration,measured)};_=MediaTime.Chunks(info.Duration).First();
        }
        return info;
    }
    public static MediaProbe ParseProbe(string json) {
        using var doc=JsonDocument.Parse(json);var duration=0d;var containerDuration=0d;var unknownTracks=0;bool audio=false,video=false;int width=0,height=0;long frames=0;int audioTracks=0;
        static double Number(JsonElement x,string key)=>x.TryGetProperty(key,out var value) && double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:0;
        if(doc.RootElement.TryGetProperty("format",out var f))containerDuration=Number(f,"duration");
        foreach(var s in doc.RootElement.GetProperty("streams").EnumerateArray()){var type=s.GetProperty("codec_type").GetString();var cover=s.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty("attached_pic",out var attached)&&attached.GetInt32()==1;var known=s.TryGetProperty("codec_name",out var codec)&&!string.IsNullOrWhiteSpace(codec.GetString())&&codec.GetString()!="unknown";var actualVideo=type=="video"&&!cover&&known;var actualAudio=type=="audio"&&known;if(type is "audio" or "video"&&!known)unknownTracks++;audio|=actualAudio;video|=actualVideo;if(actualAudio||actualVideo)duration=Math.Max(duration,Number(s,"duration"));if(actualVideo){frames=Math.Max(frames,(long)Number(s,"nb_frames"));width=Math.Max(width,(int)Number(s,"width"));height=Math.Max(height,(int)Number(s,"height"));}}
        if(!audio&&!video)throw new AppException("MEDIA_EMPTY","В файле нет доступных звуковых или видеодорожек.");
        if(unknownTracks==0)duration=Math.Max(duration,containerDuration);
        else if(duration<=0)duration=containerDuration;
        _=MediaTime.Chunks(duration).First();if((long)width*height>40_000_000)throw new AppException("MEDIA_PIXELS","Кадр превышает 40 миллионов пикселей.");
        audioTracks=doc.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio");
        return new(duration,audio,video,width,height,frames,audioTracks,unknownTracks);
    }
    private static string N(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    private const string FrameScale="scale='min(1280,iw)':'min(1280,ih)':force_original_aspect_ratio=decrease";
    public static Task<string> FrameIndexAsync(string input,string output,long index,CancellationToken ct)=>RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-protocol_whitelist","file","-format_whitelist",Formats,"-i",input,"-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf",@"select=eq(n\,"+index.ToString(CultureInfo.InvariantCulture)+"),"+FrameScale,"-threads","2",output],TimeSpan.FromMinutes(3),ct);
    public static Task<string> AudioChunkAsync(string input,string output,double start,double duration,CancellationToken ct)=>RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-protocol_whitelist","file","-format_whitelist",Formats,"-ss",N(start),"-i",input,"-t",N(duration),"-map","0:a:0","-vn","-sn","-dn","-ac","1","-ar","16000","-c:a","pcm_s16le","-f","wav",output],TimeSpan.FromMinutes(10),ct);
    public static Task<string> FrameAsync(string input,string output,double time,CancellationToken ct)=>RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-protocol_whitelist","file","-format_whitelist",Formats,"-ss",N(time),"-i",input,"-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf",FrameScale,"-threads","2",output],TimeSpan.FromMinutes(3),ct);
    public static async Task<List<double>> ScenesAsync(string input,double duration,IProgress<string>? progress,CancellationToken ct) {
        var slots=new double?[40];var last=DateTime.MinValue;
        await RunAsync("ffmpeg",["-hide_banner","-v","info","-nostdin","-protocol_whitelist","file","-format_whitelist",Formats,"-i",input,"-map","0:v:0","-an","-sn","-dn","-vf",@"scale=320:-2,select=gt(scene\,0.18),showinfo","-threads","2","-f","null","-"],TimeSpan.FromHours(3),ct,line=>{
            var m=Regex.Match(line,@"pts_time:([0-9.]+)");if(!m.Success||!double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var time)||time<0||time>duration)return;
            var slot=Math.Clamp((int)(time/duration*slots.Length),0,slots.Length-1);slots[slot]??=time;
            if(DateTime.UtcNow-last>TimeSpan.FromSeconds(1)){progress?.Report("Поиск смен сцен: "+MediaTime.Format(time)+" / "+MediaTime.Format(duration));last=DateTime.UtcNow;}
        });return slots.Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
    }
}
