using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W=DocumentFormat.OpenXml.Wordprocessing;
namespace OllamaDesktop.Core;

public static class MediaArtifacts
{
    private static (string Relative,string Full) Target(AppPaths paths,string chat,string name,string extension) {
        var relative=$"Generated/{AppPaths.Id(chat)}/{Guid.NewGuid():N}/{AppPaths.SafeName(name)}{extension}";var full=paths.Resolve(relative);Directory.CreateDirectory(Path.GetDirectoryName(full)!);return(relative,full);
    }
    private static Artifact Record(string relative,string path,string note)=>new(){Name=Path.GetFileName(path),RelativePath=relative,Format=Path.GetExtension(path),Bytes=new FileInfo(path).Length,Sha256=AppPaths.Hash(path),Validation=note};
    public static Artifact Transcript(AppPaths paths,string chat,Attachment source) {
        var input=paths.Resolve(source.Media!.TranscriptRelative);var target=Target(paths,chat,"Расшифровка_"+Path.GetFileNameWithoutExtension(source.Name),".txt");File.Copy(input,target.Full,false);
        if(AppPaths.Hash(input)!=AppPaths.Hash(target.Full))throw new IOException("Transcript copy failed");
        return Record(target.Relative,target.Full,source.Media.AudioComplete?"Вся доступная звуковая дорожка обработана. Автоматическое распознавание может содержать ошибки; интервалы без текста отмечены.":"Частичная расшифровка: обработка остановлена. Продолжите распознавание для полного текста.");
    }
    public static Artifact Report(AppPaths paths,string chat,string markdown,string coverage,CancellationToken ct) {
        if(string.IsNullOrWhiteSpace(markdown)||markdown.Length>300_000)throw new AppException("MEDIA_REPORT_SIZE","Модель вернула пустой или слишком большой отчёт. Полная расшифровка сохранена отдельно.");
        var target=Target(paths,chat,"Отчёт_по_записи",".docx");
        static string Clean(string s)=>new(s.Where(c=>System.Xml.XmlConvert.IsXmlChar(c)).ToArray());
        try {
            using(var document=WordprocessingDocument.Create(target.Full,WordprocessingDocumentType.Document)){
                var part=document.AddMainDocumentPart();var body=new W.Body();part.Document=new W.Document(body);
                body.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines{After="240"}),new W.Run(new W.RunProperties(new W.Bold(),new W.FontSize{Val="32"}),new W.Text("Отчёт по записи"))));
                body.Append(new W.Paragraph(new W.Run(new W.RunProperties(new W.FontSize{Val="20"}),new W.Text(Clean(coverage)){Space=SpaceProcessingModeValues.Preserve})));
                foreach(var raw in markdown.Split('\n')){
                    ct.ThrowIfCancellationRequested();var line=raw.TrimEnd();if(line.Length==0){body.Append(new W.Paragraph());continue;}
                    var heading=line.StartsWith('#');var text=heading?line.TrimStart('#',' '):line.Replace("**","");
                    var run=new W.Run();if(heading)run.Append(new W.RunProperties(new W.Bold(),new W.FontSize{Val="26"}));run.Append(new W.Text(Clean(text)){Space=SpaceProcessingModeValues.Preserve});body.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines{After="120"}),run));
                }
                body.Append(new W.SectionProperties(new W.PageSize{Width=11906,Height=16838},new W.PageMargin{Top=1134,Bottom=1134,Left=1134,Right=1134}));part.Document.Save();
            }
            using(var read=WordprocessingDocument.Open(target.Full,false))if(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(read).Any())throw new AppException("MEDIA_REPORT_INVALID","Отчёт не прошёл проверку Word. Расшифровка сохранена.");
            return Record(target.Relative,target.Full,"Создан настоящий DOCX; структура Open XML проверена. "+coverage);
        }catch{if(File.Exists(target.Full))File.Delete(target.Full);throw;}
    }
}
