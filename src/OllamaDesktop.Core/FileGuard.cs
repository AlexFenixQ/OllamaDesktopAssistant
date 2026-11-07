using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OllamaDesktop.Core;

public static class FileGuard
{
    public static readonly string[] MediaExtensions = [".wav", ".wave", ".w64", ".rf64", ".mp3", ".mp2", ".mpa", ".m4a", ".m4b", ".m4p", ".aac", ".adts", ".flac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc", ".au", ".snd", ".caf", ".amr", ".awb", ".ac3", ".eac3", ".dts", ".mka", ".ape", ".wv", ".tta", ".tak", ".alac", ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".mpg", ".mpeg", ".mpe", ".vob", ".vro", ".ts", ".mts", ".m2ts", ".m2t", ".mxf", ".flv", ".f4v", ".3gp", ".3g2", ".ogv", ".rm", ".rmvb", ".nut", ".nsv", ".h264", ".264", ".h265", ".265", ".hevc", ".av1", ".ivf", ".y4m", ".dv", ".m2v", ".m1v", ".divx", ".xvid", ".bik", ".smk", ".fli", ".flc", ".swf", ".mjpeg", ".mjpg", ".dat"];
    public static bool IsMedia(string file) => MediaExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());
    public static readonly string[] Extensions = [..MediaExtensions,..ImageFormats.Extensions,".docx",".xlsx",".pptx",".pdf",".txt",".csv",".zip"];
    public static string Extension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new AppException("FORMAT_UNSUPPORTED", "Этот формат пока не поддерживается. Доступны DOCX, XLSX, PPTX, PDF, TXT, CSV, JPEG, PNG, WebP, BMP TIFF, ZIP, аудио и видео. Старые DOC/XLS/PPT и файлы с макросами не открываются.");
        return extension;
    }
    public static void CheckSize(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0 || info.Length > (IsMedia(file) ? Limits.MediaBytes : Limits.FileBytes)) throw new AppException("FILE_SIZE", IsMedia(file) ? "Медиазапись должна быть непустой и не больше 2 ГиБ." : "Файл должен быть непустым и не больше 512 МиБ.");
    }
    public static List<string> CheckOffice(string file, string extension)
    {
        using var zip = ZipFile.OpenRead(file); var total = 0L; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var warnings = new List<string>();
        if (zip.Entries.Count > Limits.ZipEntries) throw new AppException("ZIP_ENTRIES", "В документе слишком много внутренних файлов.");
        var externalCount = 0; var embedded = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == "..") || !seen.Add(name))
                throw new AppException("ZIP_PATH", "Небезопасная структура документа.");
            total = checked(total + entry.Length);
            if (total > Limits.ExpandedBytes || entry.Length > 40 * 1024 * 1024 || (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000))
                throw new AppException("ZIP_EXPANSION", "Документ превышает ограничения распаковки.");
            if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) throw new AppException("MACROS", "Документы с макросами не поддерживаются.");
            if (name.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase)) embedded = true;
            if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open(); var relationships = SafeXml(stream);
                externalCount += relationships.Descendants().Count(x => (string?)x.Attribute("TargetMode") == "External");
            }
        }
        var types = zip.GetEntry("[Content_Types].xml") ?? throw new AppException("OFFICE_STRUCTURE", "Не найдено описание формата Office.");
        using (var stream = types.Open())
        {
            var xml = SafeXml(stream).ToString();
            if (xml.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)) throw new AppException("MACROS", "Файлы с макросами не поддерживаются.");
        }
        var required = extension switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", ".pptx" => "ppt/presentation.xml", _ => "" };
        if (zip.GetEntry(required) is null) throw new AppException("FORMAT_MISMATCH", "Расширение не соответствует содержимому документа.");
        if (externalCount > 0) warnings.Add($"Внешних ссылок: {externalCount}. Их содержимое не загружалось.");
        if (embedded) warnings.Add("Вложенные OLE-объекты не запускались и не извлекались.");
        return warnings;
    }
    private static XDocument SafeXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 40_000_000 });
        return XDocument.Load(reader);
    }
    public static string ImageFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "PNG";
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return "JPEG";
        if (data.Length >= 12 && Encoding.ASCII.GetString(data[..4]) == "RIFF" && Encoding.ASCII.GetString(data.Slice(8, 4)) == "WEBP") return "WEBP";
        if (data.Length >= 2 && data[0] == 66 && data[1] == 77) return "BMP";
        if (data.Length >= 4 && (data[..4].SequenceEqual(new byte[] { 73, 73, 42, 0 }) || data[..4].SequenceEqual(new byte[] { 77, 77, 0, 42 }))) return "TIFF";
        return ImageFormats.Detect(data);
    }
    public static void CheckImageExtension(string extension, string format)
    {
        if (!ImageFormats.ExtensionMatches(extension,format)) throw new AppException("IMAGE_MISMATCH", "Расширение изображения не соответствует распознанному содержимому.");
    }
}
