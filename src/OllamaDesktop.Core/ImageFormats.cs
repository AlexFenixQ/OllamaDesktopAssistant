using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
namespace OllamaDesktop.Core;

public static class ImageFormats
{
    static ImageFormats()=>Environment.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH",AppContext.BaseDirectory);
    public static readonly string[] Extensions=".jxr .wdp .hdp .jpg .jpeg .jpe .jfif .png .apng .webp .bmp .dib .tif .tiff .gif .heic .heif .hif .avif .avifs .jxl .jp2 .j2k .j2c .jpc .jpf .jpx .jpm .pgx .jng .mng .ico .cur .icns .psd .psb .dds .pcx .dcx .tga .vda .icb .vst .exr .hdr .rgbe .pic .dpx .cin .pnm .pbm .pgm .ppm .pam .pfm .svg .svgz .wmf .emf .xbm .xpm .fits .fit .fts .dng .nef .nrw .arw .cr2 .cr3 .orf .raf .rw2 .rwl .pef .3fr .iiq .sr2 .srf .srw .x3f .raw .dcr .kdc .erf .mrw .mos .mef".Split(' ');
    private static readonly HashSet<string> Raw=".dng .nef .nrw .arw .cr2 .cr3 .orf .raf .rw2 .rwl .pef .3fr .iiq .sr2 .srf .srw .x3f .raw .dcr .kdc .erf .mrw .mos .mef".Split(' ').ToHashSet();
    private static readonly HashSet<string> Allowed="PNG PNG8 PNG24 PNG32 PNG48 PNG64 JPEG JPG JPE WEBP BMP BMP2 BMP3 DIB TIFF TIFF64 GIF HEIC HEIF AVIF JXL JP2 J2K JPC JPM JPX PGX JNG MNG ICO CUR ICON ICNS PSD PSB DDS PCX DCX TGA VDA ICB VST EXR HDR RGBE PIC DPX CIN PNM PBM PGM PPM PAM PFM SVG MSVG WMF EMF XBM XPM FITS FIT FTS DNG NEF NRW ARW CR2 CR3 ORF RAF RW2 RWL PEF 3FR IIQ SR2 SRF SRW X3F RAW DCR KDC ERF MRW MOS MEF".Split(' ').ToHashSet();
    public static bool IsRaw(string? extension)=>extension is not null&&Raw.Contains(extension);
    public static byte[] Prepare(byte[] bytes,string? extension)
    {
        if(extension==".svgz"){
            using var input=new GZipStream(new MemoryStream(bytes),CompressionMode.Decompress);using var output=new MemoryStream();var buffer=new byte[8192];int n;
            while((n=input.Read(buffer))>0){if(output.Length+n>8_000_000)throw new AppException("SVG_SIZE","SVG превышает допустимый размер после распаковки.");output.Write(buffer,0,n);}bytes=output.ToArray();
        }
        if(extension is ".svg" or ".svgz")ValidateSvg(bytes);
        return bytes;
    }
    public static void ValidateSvg(byte[] bytes)
    {
        if(bytes.Length>8_000_000)throw new AppException("SVG_SIZE","SVG превышает 8 миллионов байт.");
        using var input=new MemoryStream(bytes);using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8_000_000});
        var doc=XDocument.Load(reader);
        if(doc.Root?.Name.LocalName!="svg")throw new AppException("SVG_STRUCTURE","Не найдено содержимое SVG.");
        foreach(var node in doc.DescendantNodes())if(node is XProcessingInstruction)throw new AppException("SVG_RESOURCE","Внешние инструкции SVG не разрешены.");
        foreach(var e in doc.Descendants()){
            var name=e.Name.LocalName.ToLowerInvariant();
            if(name is "script" or "foreignobject")throw new AppException("SVG_ACTIVE","Активное содержимое SVG не выполняется.");
            if(name=="style"&&e.Value.Contains('\\')||e.Attributes().Any(a=>a.Value.Contains('\\')&&(a.Name.LocalName.Equals("style",StringComparison.OrdinalIgnoreCase)||a.Value.Contains('('))))throw new AppException("SVG_RESOURCE","Экранированные CSS-ресурсы SVG не разрешены.");
            foreach(var a in e.Attributes()){
                var attribute=a.Name.LocalName.ToLowerInvariant();
                if(attribute=="base"&&a.Name.Namespace==XNamespace.Xml||attribute is "href" or "src"&&!Regex.IsMatch(a.Value.Trim(),@"^#[A-Za-z_][A-Za-z0-9_.-]*$"))throw new AppException("SVG_RESOURCE","SVG с внешними или встроенными ресурсами не открывается автоматически. Исходник сохранён.");
            }
        }
        var text=Regex.Replace(doc.ToString(),@"/\*.*?\*/","",RegexOptions.Singleline);
        if(text.Contains("@import",StringComparison.OrdinalIgnoreCase))throw new AppException("SVG_RESOURCE","Внешние стили SVG запрещены.");
        foreach(Match m in Regex.Matches(text,@"url\s*\((.*?)\)",RegexOptions.IgnoreCase|RegexOptions.Singleline))if(!m.Groups[1].Value.Trim(' ','\t','\r','\n','\"','\'').StartsWith('#'))throw new AppException("SVG_RESOURCE","Внешние ресурсы SVG запрещены.");
    }
    public static string Detect(ReadOnlySpan<byte> bytes)
    {
        try{
            var head=Encoding.UTF8.GetString(bytes[..Math.Min(4096,bytes.Length)]).TrimStart('\ufeff',' ','\t','\r','\n');
            if(head.StartsWith('<')||bytes.Length>1&&(bytes[0]==0xff&&bytes[1]==0xfe||bytes[0]==0xfe&&bytes[1]==0xff)){ValidateSvg(bytes.ToArray());return "MSVG";}
            if(head.StartsWith("push graphic-context",StringComparison.OrdinalIgnoreCase))throw new AppException("IMAGE_FORMAT","Прямые команды MVG не принимаются как изображение.");
            var info=new MagickImageInfo(bytes.ToArray());var format=info.Format.ToString().ToUpperInvariant();if(!Allowed.Contains(format))throw new AppException("IMAGE_FORMAT","Декодер распознал формат, который не разрешён для безопасного визуального анализа.");return format;
        }
        catch(MagickException e){throw new AppException("IMAGE_FORMAT","Не удалось распознать изображение: формат повреждён, требует внешнего обработчика или недоступен в текущем декодере.",e);}
    }
    public static string DetectFile(byte[] bytes,string extension)
    {
        if(extension is ".ico" or ".cur"){
            if(bytes.Length<6||bytes[0]!=0||bytes[1]!=0||bytes[3]!=0||bytes[2] is not (1 or 2)||bytes[4]+bytes[5]*256==0)throw new AppException("IMAGE_SIGNATURE","Заголовок ICO/CUR повреждён.");
            return "ICO";
        }
        if(extension is ".tga" or ".vda" or ".icb" or ".vst"){
            if(bytes.Length<18||bytes[1]>1||bytes[2] is not (1 or 2 or 3 or 9 or 10 or 11)||bytes[12]+bytes[13]*256==0||bytes[14]+bytes[15]*256==0)throw new AppException("IMAGE_SIGNATURE","Заголовок TGA повреждён.");
            return "TGA";
        }
        return FileGuard.ImageFormat(bytes);
    }
    public static MagickFormat NativeFormat(string format,string? extension)
    {
        if(extension is ".svg" or ".svgz")return MagickFormat.Msvg;
        if(IsRaw(extension))return MagickFormat.Dng;
        if(Enum.TryParse<MagickFormat>(format,true,out var value))return value;
        throw new AppException("IMAGE_FORMAT","Не найден встроенный декодер изображения.");
    }
    public static bool ExtensionMatches(string extension,string format)
    {
        if(!Extensions.Contains(extension))return false;
        if(IsRaw(extension))return format=="TIFF"||Allowed.Contains(format);
        return extension switch{
            ".jpg" or ".jpeg" or ".jpe" or ".jfif"=>format is "JPEG" or "JPG" or "JPE",
            ".png" or ".apng"=>format.StartsWith("PNG"),".tif" or ".tiff"=>format.StartsWith("TIFF"),
            ".heic" or ".heif" or ".hif" or ".avif" or ".avifs"=>format is "HEIC" or "HEIF" or "AVIF",
            ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpf" or ".jpx" or ".jpm"=>format is "JP2" or "J2K" or "JPC" or "JPX" or "JPM",
            ".ico" or ".cur"=>format is "ICO" or "ICON" or "CUR", ".psd" or ".psb"=>format is "PSD" or "PSB",
            ".svg" or ".svgz"=>format is "SVG" or "MSVG", ".pnm" or ".pbm" or ".pgm" or ".ppm" or ".pam" or ".pfm"=>format is "PNM" or "PBM" or "PGM" or "PPM" or "PAM" or "PFM",
            ".tga" or ".vda" or ".icb" or ".vst"=>format is "TGA" or "VDA" or "ICB" or "VST",
            ".hdr" or ".rgbe"=>format is "HDR" or "RGBE", ".fits" or ".fit" or ".fts"=>format is "FITS" or "FIT" or "FTS",
            _=>extension.TrimStart('.').Equals(format,StringComparison.OrdinalIgnoreCase)
        };
    }
    public static bool IsAnimation(string format,byte[] bytes)=>format=="GIF"||format.StartsWith("PNG")&&bytes.AsSpan().IndexOf("acTL"u8)>=0||format=="WEBP"&&bytes.AsSpan().IndexOf("ANIM"u8)>=0;
}
