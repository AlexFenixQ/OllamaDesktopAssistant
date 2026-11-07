using System.Text.RegularExpressions;
namespace OllamaDesktop.Core;
public static class FileRequestIntent
{
    public static bool IsExplicitCreation(string text) => !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text,
        @"(?i)(созда(й|ть|йте|вай|дите|ния)|сдела(й|йте|ть|й мне)|сформиру(й|йте)|подготов(ь|ьте|ить)|собер(и|ите)|выгруз(и|ите)|сгенериру(й|йте)|упаку(й|йте)|заархивиру(й|йте)|экспортиру(й|йте)|запиш(и|ите)|create|generate|build|make|prepare|export|package|bundle)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static IReadOnlySet<OutputKind> RequestedKinds(string text)
    {
        var selected = new HashSet<OutputKind>();
        if (!IsExplicitCreation(text)) return selected;
        if (Regex.IsMatch(text, @"(?i)(\bword\b|\bdocx\b|\.docx\b|ворд|отч[её]т[^\n]{0,30}(документ|word|docx)|документ\s+(word|в\s*формате))")) selected.Add(OutputKind.Word);
        if (Regex.IsMatch(text, @"(?i)(\bexcel\b|\bxlsx\b|\.xlsx\b|эксел|таблиц[^\n]{0,24}(excel|формул))")) selected.Add(OutputKind.Excel);
        if (Regex.IsMatch(text, @"(?i)(\bpowerpoint\b|\bpptx\b|\.pptx\b|презентаци[юяий]|слайд[ыаов]*)")) selected.Add(OutputKind.PowerPoint);
        if (Regex.IsMatch(text, @"(?i)(\bzip\b|\.zip\b|архив|заархив|упаков)")) selected.Add(OutputKind.Zip);
        return selected;
    }

}
