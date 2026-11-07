using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;
namespace OllamaDesktop.Core;

public sealed class MediaExtraction(AppPaths paths)
{
    public static string CheckpointRelative(string chat,string id)=>$"Attachments/{AppPaths.Id(chat)}/{AppPaths.Id(id)}/media-checkpoint.json";
    public Attachment? ReadCheckpoint(string chat,string id) {
        var file=paths.Resolve(CheckpointRelative(chat,id));if(!File.Exists(file)||new FileInfo(file).Length>32_000_000)return null;
        var a=JsonDefaults.Read<Attachment>(File.ReadAllText(file));var prefix=$"Attachments/{AppPaths.Id(chat)}/{AppPaths.Id(id)}/";
        if(a.Id!=id||a.Media is null||!a.RelativeOriginal.Replace('\\','/').StartsWith(prefix,StringComparison.Ordinal)||a.Media.TranscriptRelative!=prefix+"transcript.txt")throw new AppException("MEDIA_CHECKPOINT","Контрольная точка записи повреждена.");
        return a;
    }
    public async Task<Attachment> ExtractAsync(ExtractionJob job,IProgress<string>? progress,CancellationToken ct) {
        var prefix=$"Attachments/{AppPaths.Id(job.ChatId)}/{AppPaths.Id(job.AttachmentId)}/";var relative=prefix+"original"+FileGuard.Extension("f"+job.Extension);var file=paths.Resolve(relative);
        FileGuard.CheckSize(file);var hash=AppPaths.Hash(file);var info=await MediaTools.ProbeAsync(file,ct);
        var a=ReadCheckpoint(job.ChatId,job.AttachmentId);
        if(a is not null && a.Sha256!=hash)throw new AppException("MEDIA_CHANGED","Медиазапись изменилась после начала распознавания.");
        a??=new Attachment{Id=job.AttachmentId,Name=Path.GetFileName(job.Name),RelativeOriginal=relative,Bytes=new FileInfo(file).Length,Sha256=hash,Media=new MediaMetadata{DurationSeconds=info.Duration,HasAudio=info.Audio,HasVideo=info.Video,SpeechModel=SpeechModelStore.Definition(job.SpeechModel).Id,TranscriptRelative=prefix+"transcript.txt",AudioComplete=!info.Audio,FramesComplete=!info.Video}};
        var media=a.Media!;
        void Save(){WriteTranscript(a);AppPaths.AtomicText(paths.Resolve(CheckpointRelative(job.ChatId,job.AttachmentId)),JsonDefaults.Write(a));}
        Save();
        if(info.Audio&&!media.AudioComplete) {
            progress?.Report("Загрузка речевой модели в память…");var model=await new SpeechModelStore(paths).VerifyAsync(media.SpeechModel,ct);
            RuntimeOptions.RuntimeLibraryOrder=[RuntimeLibrary.Cpu,RuntimeLibrary.CpuNoAvx];
            using var factory=WhisperFactory.FromPath(model);
            foreach(var chunk in MediaTime.Chunks(info.Duration)) {
                ct.ThrowIfCancellationRequested();if(chunk.Start+chunk.Duration<=media.ProcessedSeconds+0.02)continue;
                var wav=paths.Resolve(prefix+"chunk-"+Guid.NewGuid().ToString("N")+".wav");
                try {
                    progress?.Report($"Подготовка звука: {MediaTime.Format(chunk.Start)} / {MediaTime.Format(info.Duration)}…");await MediaTools.AudioChunkAsync(file,wav,chunk.Start,chunk.Duration,ct);
                    var lines=new List<TranscriptLine>();
                    // A container can outlast its audio stream. Empty trailing PCM must not
                    // be passed to Whisper (which rejects an empty sample buffer).
                    if(new FileInfo(wav).Length>128)
                    await using(var processor=factory.CreateBuilder().WithLanguage("auto").WithThreads(Math.Clamp(Environment.ProcessorCount/2,2,8)).WithNoSpeechThreshold(0.6f).WithProgressHandler(p=>progress?.Report($"Распознавание {MediaTime.Format(chunk.Start)}–{MediaTime.Format(chunk.Start+chunk.Duration)}: {p}%")).Build())
                    await using(var stream=File.OpenRead(wav))await foreach(var result in processor.ProcessAsync(stream,ct)) {
                        var text=result.Text.Trim();if(text.Length==0)continue;
                        var start=Math.Clamp(chunk.Start+result.Start.TotalSeconds,chunk.Start,chunk.Start+chunk.Duration);var end=Math.Clamp(chunk.Start+result.End.TotalSeconds,start,chunk.Start+chunk.Duration);
                        lines.Add(new(start,end,text));
                    }
                    media.Lines.AddRange(lines);media.Intervals.Add(new(chunk.Start,chunk.Start+chunk.Duration,lines.Count==0?"речь не распознана / тишина":"обработано"));
                    media.ProcessedSeconds=chunk.Start+chunk.Duration;media.AudioComplete=media.ProcessedSeconds>=info.Duration-0.02;
                    a.Segments=media.Lines.Select(l=>new SourceSegment($"{a.Name}, {MediaTime.Format(l.StartSeconds)}–{MediaTime.Format(l.EndSeconds)}",l.Text)).ToList();Save();
                } finally {if(File.Exists(wav))File.Delete(wav);}
            }
        }
        if(info.Video&&!media.FramesComplete) {
            progress?.Report("Поиск смен сцен по всей видеозаписи…");var scenes=await MediaTools.ScenesAsync(file,info.Duration,progress,ct);
            var times=MediaTime.UniformFrames(info.Duration).Concat(scenes).OrderBy(x=>x).DistinctBy(t=>(int)(t*2)).ToList();
            foreach(var time in times) {
                ct.ThrowIfCancellationRequested();var reference=$"{a.Name}, кадр около {MediaTime.Format(time)}";
                if(a.Visuals.Any(v=>v.Reference==reference))continue;
                var rel=prefix+"frame-"+Guid.NewGuid().ToString("N")+".jpg";progress?.Report($"Подготовка видеокадра: {MediaTime.Format(time)}…");await MediaTools.FrameAsync(file,paths.Resolve(rel),time,ct);
                a.Visuals.Add(new VisualSource(Guid.NewGuid().ToString("N"),reference,rel));Save();
            }
            media.FramesComplete=true;
        }
        a.Warnings=a.Warnings.Where(w=>!w.StartsWith("Медиазапись:")).ToList();
        a.Warnings.Add(info.Audio?"Медиазапись: обработана вся доступная звуковая дорожка. Распознавание автоматическое: возможны ошибки, неразборчивые места и ошибки таймкодов. Надёжное разделение по голосам не выполняется.":"Медиазапись: звуковая дорожка отсутствует; расшифровка речи невозможна.");
        if(info.Video)a.Warnings.Add($"Медиазапись: подготовлено {a.Visuals.Count} кадров по всей временной шкале и сменам сцен. Это выборочный визуальный анализ; краткие события между кадрами могут быть пропущены.");
        media.TranscriptArtifact=MediaArtifacts.Transcript(paths,job.ChatId,a);
        Save();return a;
    }
    private void WriteTranscript(Attachment a) {
        var m=a.Media!;var text=new StringBuilder();text.AppendLine("Расшифровка: "+a.Name);text.AppendLine($"Длительность: {MediaTime.Format(m.DurationSeconds)}. Обработано: {MediaTime.Format(m.ProcessedSeconds)}.");text.AppendLine("Автоматическое распознавание Whisper "+m.SpeechModel+". Имена говорящих не определяются. Отсутствие текста не гарантирует отсутствие речи.");text.AppendLine();
        var entries=m.Lines.Select(l=>(Start:l.StartSeconds,End:l.EndSeconds,Text:l.Text)).Concat(m.Intervals.Where(i=>i.State!="обработано").Select(i=>(Start:i.StartSeconds,End:i.EndSeconds,Text:"["+i.State+"]")));
        foreach(var l in entries.OrderBy(l=>l.Start))text.AppendLine($"[{MediaTime.Format(l.Start)}–{MediaTime.Format(l.End)}] {l.Text}");
        if(!m.HasAudio)text.AppendLine("Звуковая дорожка отсутствует.");
        AppPaths.AtomicText(paths.Resolve(m.TranscriptRelative),text.ToString());
    }
}
