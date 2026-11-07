using System.Text;
using DocumentFormat.OpenXml.Packaging;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace OllamaDesktop.Core;

/// <summary>Run in a disposable bounded worker process, never on the UI thread.</summary>
public sealed class ExtractionEngine(AppPaths paths,Func<string,byte[]>? platformImageDecoder=null)
{
    private Attachment _result = new();
    private string _directory = "";
    private int _characters;
    private int _skippedVisuals;
    private readonly HashSet<string> _imageHashes = [];
    public Attachment Extract(ExtractionJob job)
    {
        AppPaths.Id(job.ChatId); AppPaths.Id(job.AttachmentId);
        var extension = FileGuard.Extension("file" + job.Extension);
        _directory = paths.AttachmentDirectory(job.ChatId, job.AttachmentId);
        Directory.CreateDirectory(_directory);
        var relative = $"Attachments/{job.ChatId}/{job.AttachmentId}/original{extension}";
        var file = paths.Resolve(relative); FileGuard.CheckSize(file);
        _result = new Attachment { Id = job.AttachmentId, Name = Path.GetFileName(job.Name), RelativeOriginal = relative, Bytes = new FileInfo(file).Length, Sha256 = AppPaths.Hash(file) };
        _characters = 0; _skippedVisuals = 0; _imageHashes.Clear();
        Environment.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MAGICK_TEMPORARY_PATH", paths.Resolve("Staging"));
        switch (extension)
        {
            case ".docx": _result.Warnings.AddRange(FileGuard.CheckOffice(file, extension)); ReadWord(file); break;
            case ".xlsx": _result.Warnings.AddRange(FileGuard.CheckOffice(file, extension)); ReadExcel(file); break;
            case ".pptx": _result.Warnings.AddRange(FileGuard.CheckOffice(file, extension)); ReadPresentation(file); break;
            case ".pdf": ReadPdf(file); break;
            case ".txt": case ".csv": ReadText(file, extension); break;
            default:
                if(extension is ".jxr" or ".wdp" or ".hdp"){
                    if(platformImageDecoder is null)throw new AppException("IMAGE_WINDOWS_CODEC","Для JPEG XR требуется встроенный Windows-декодер приложения.");
                    AddImage(platformImageDecoder(file),"JPEG XR, подготовленное изображение",true);
                    _result.Warnings.Add("JPEG XR подготовлен встроенным Windows WIC. HDR преобразован для обычного визуального анализа; оригинал сохранён.");break;
                }
                var bytes = ImageFormats.Prepare(File.ReadAllBytes(file),extension); var format = ImageFormats.DetectFile(bytes,extension); FileGuard.CheckImageExtension(extension, format);
                if(ImageFormats.IsAnimation(format,bytes)&&ReadAnimation(file))break;
                AddImage(bytes, "изображение", true,extension); break;
        }
        if (_skippedVisuals > 0) _result.Warnings.Add($"Не подготовлено изображений/страниц для визуального анализа: {_skippedVisuals}. Лимит — {Limits.VisualsPerFile} визуальных объектов на документ; текст извлечён отдельно.");
        if (_result.Segments.Count == 0 && _result.Visuals.Count == 0) throw new AppException("EMPTY_EXTRACTION", "Не удалось извлечь текст или поддерживаемые изображения.");
        _result.Warnings = _result.Warnings.Distinct().ToList();
        return _result;
    }
    private void Add(string reference, string? text)
    {
        text = text?.Replace('\0', ' ').Trim(); if (string.IsNullOrEmpty(text)) return;
        _characters = checked(_characters + text.Length);
        if (_characters > Limits.DocumentCharacters) throw new AppException("DOCUMENT_TEXT_LIMIT", "Документ содержит более 400 000 знаков. Разделите его на меньшие файлы; скрытого обрезания не было.");
        _result.Segments.Add(new SourceSegment(_result.Name + " · " + reference, text));
    }
    private void ReadWord(string file)
    {
        using var document = WordprocessingDocument.Open(file, false);
        var main = document.MainDocumentPart ?? throw new AppException("DOCX_MAIN", "В Word-файле отсутствует основной документ.");
        var body = main.Document?.Body ?? throw new AppException("DOCX_BODY", "В Word-файле отсутствует содержимое.");
        var paragraph = 0; var table = 0;
        foreach (var element in body.ChildElements)
        {
            if (element is W.Table t)
            {
                table++; var row = 0;
                foreach (var tr in t.Elements<W.TableRow>()) Add($"таблица {table}, строка {++row}", string.Join(" | ", tr.Elements<W.TableCell>().Select(c => c.InnerText)));
            }
            else if (element is W.Paragraph p) Add($"параграф {++paragraph}", p.InnerText);
            else foreach (var nested in element.Descendants<W.Paragraph>()) Add($"параграф {++paragraph}", nested.InnerText);
        }
        var index = 0;
        foreach (var header in main.HeaderParts) Add($"верхний колонтитул {++index}", header.Header?.InnerText);
        index = 0;
        foreach (var footer in main.FooterParts) Add($"нижний колонтитул {++index}", footer.Footer?.InnerText);
        if (main.FootnotesPart?.Footnotes is { } footnotes) Add("сноски", footnotes.InnerText);
        if (main.EndnotesPart?.Endnotes is { } endnotes) Add("концевые сноски", endnotes.InnerText);
        if (main.WordprocessingCommentsPart?.Comments is { } comments) Add("комментарии", comments.InnerText);
        ReadParts(main, "Word");
        _result.Warnings.Add("Номера страниц Word не определяются. Верстка, SmartArt и исправления не воспроизводятся; ссылки относятся к параграфам и таблицам.");
    }
    private void ReadExcel(string file)
    {
        using var document = SpreadsheetDocument.Open(file, false);
        var workbook = document.WorkbookPart ?? throw new AppException("XLSX_MAIN", "Не найдена книга Excel.");
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        if (strings.Sum(x => (long)x.Length) > Limits.ExpandedBytes) throw new AppException("XLSX_STRINGS", "Слишком большая таблица строк.");
        var sheets = workbook.Workbook?.Sheets?.Elements<S.Sheet>().ToArray() ?? [];
        if (sheets.Length > 100) throw new AppException("XLSX_SHEETS", "Поддерживается до 100 листов в одной книге.");
        long cells = 0;
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { } relationship) continue;
            var part = (WorksheetPart)workbook.GetPartById(relationship);
            var name = sheet.Name?.Value ?? "Лист";
            var rows = (part.Worksheet ?? throw new AppException("XLSX_STRUCTURE", "Не найдено содержимое листа.")).GetFirstChild<S.SheetData>()?.Elements<S.Row>() ?? [];
            var profile = new TabularProfile(name);
            var samples = new List<SourceSegment>();
            var sampleLength = 0; var number = 0;
            foreach (var row in rows)
            {
                number++;
                var values = new List<string>();
                var positional = new Dictionary<int, string>();
                foreach (var cell in row.Elements<S.Cell>())
                {
                    if (++cells > 2_000_000) throw new AppException("XLSX_CELLS", "Достигнут предел 2 млн ячеек. Для этого файла требуется пакетный импорт.");
                    var value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
                    if (cell.DataType?.Value == S.CellValues.SharedString && int.TryParse(value, out var si) && si >= 0 && si < strings.Length) value = strings[si];
                    if (cell.DataType?.Value == S.CellValues.Boolean) value = value == "1" ? "TRUE" : "FALSE";
                    if (cell.CellFormula is { } formula) value = "=" + (string.IsNullOrEmpty(formula.Text) ? "[общая/массивная формула]" : formula.Text) + " [кэш: " + (value.Length == 0 ? "отсутствует" : value) + "]";
                    var address = cell.CellReference?.Value ?? "";
                    var column = 0;
                    foreach (var ch in address) { if (!char.IsLetter(ch)) break; column = checked(column * 26 + char.ToUpperInvariant(ch) - 'A' + 1); }
                    if (column < 1 || column > 256) throw new AppException("XLSX_COLUMNS", "Ячейка вне поддерживаемых 256 столбцов или имеет неверный адрес. Разделите лист; скрытого пропуска столбцов не было.");
                    positional[column - 1] = value;
                    if (value.Length > 0) values.Add(address + " = " + value);
                }
                if (number == 1 && positional.Count == 0) continue;
                var length = positional.Count == 0 ? 0 : positional.Keys.Max() + 1;
                var ordered = new string[length];
                foreach (var item in positional) ordered[item.Key] = item.Value;
                for (var i=0; i<ordered.Length; i++) ordered[i] ??= "";
                profile.AddRow(ordered);
                // A small workbook retains original cell-level details. For large
                // sheets use exact local aggregates plus bounded representative rows.
                if (number <= 100 && sampleLength < 18_000)
                {
                    var raw = string.Join(" | ", values);
                    sampleLength += raw.Length;
                    if (sampleLength <= 18_000 && raw.Length > 0)
                        samples.Add(new SourceSegment(_result.Name + $" · лист «{name}», строка {row.RowIndex?.Value}", raw));
                }
            }
            Add($"лист «{name}», локальный аналитический профиль", profile.Report());
            if (profile.Rows <= 75 && sampleLength <= 18_000)
                foreach (var sample in samples) Add(sample.Reference.Replace(_result.Name + " · ", ""), sample.Text);
            else
                _result.Warnings.Add($"Лист «{name}»: локально агрегировано {profile.Rows} строк, индивидуальные строки не отправлены целиком в облако. Для адресной проверки используйте исходный XLSX.");
            ReadParts(part, $"лист «{name}»");
        }
        _result.Warnings.Add("Формулы загруженной книги не выполнялись; денежные суммы вычислены из доступных числовых значений. Даты Excel могут отображаться серийными числами. Для сравнения с контрольным листом учитывайте его сохранённые значения.");
    }
    private void ReadPresentation(string file)
    {
        using var document = PresentationDocument.Open(file, false);
        var main = document.PresentationPart ?? throw new AppException("PPTX_MAIN", "Не найдена презентация.");
        var ids = main.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToArray() ?? [];
        if (ids.Length > 150) throw new AppException("PPTX_SLIDES", "Поддерживается до 150 слайдов.");
        var number = 0;
        foreach (var id in ids)
        {
            number++; var part = (SlidePart)main.GetPartById(id.RelationshipId?.Value ?? "");
            Add($"слайд {number}", string.Join("\n", (part.Slide ?? throw new AppException("PPTX_STRUCTURE", "Не найдено содержимое слайда.")).Descendants<A.Paragraph>().Select(p => p.InnerText)));
            if (part.NotesSlidePart?.NotesSlide is { } notes) Add($"слайд {number}, заметки", string.Join("\n", notes.Descendants<A.Paragraph>().Select(p => p.InnerText)));
            ReadParts(part, $"слайд {number}");
        }
        _result.Warnings.Add("Извлечены текст, заметки и поддерживаемые встроенные изображения. Анимация, SmartArt и точное расположение объектов не воспроизводятся.");
    }
    private void ReadParts(OpenXmlPart root, string label)
    {
        var visited = new HashSet<Uri>(); var stack = new Stack<(OpenXmlPart Part, int Depth)>(); stack.Push((root, 0)); var images = 0; var charts = 0;
        while (stack.Count > 0)
        {
            var (part, depth) = stack.Pop(); if (!visited.Add(part.Uri) || depth > 12) continue;
            if (part is ImagePart image)
            {
                images++;
                if (_result.Visuals.Count >= Limits.VisualsPerFile) { _skippedVisuals++; continue; }
                try { using var stream = image.GetStream(FileMode.Open, FileAccess.Read); using var memory = new MemoryStream(); stream.CopyTo(memory); AddImage(memory.ToArray(), $"{label}, встроенное изображение {images}", false); }
                catch (Exception e) when (e is AppException or MagickException) { _result.Warnings.Add($"{label}: одно встроенное изображение пропущено (неподдерживаемый или слишком большой формат)."); }
            }
            else if (part is ChartPart chart) Add($"{label}, диаграмма {++charts} (данные)", chart.ChartSpace?.InnerText);
            // Do not traverse back through slides, masters or worksheets: only their directly owned assets.
            foreach (var child in part.Parts)
                if (child.OpenXmlPart is not (SlidePart or SlideMasterPart or SlideLayoutPart or WorkbookPart or WorksheetPart or NotesMasterPart))
                    stack.Push((child.OpenXmlPart, depth + 1));
        }
    }
    private void ReadPdf(string file)
    {
        using (var header = File.OpenRead(file))
        {
            var signature = new byte[1024]; var count = header.Read(signature);
            if (!Encoding.ASCII.GetString(signature, 0, count).Contains("%PDF-", StringComparison.Ordinal)) throw new AppException("PDF_SIGNATURE", "Файл не является PDF.");
        }
        using var document = PdfDocument.Open(file);
        if (document.NumberOfPages > Limits.PdfPages) throw new AppException("PDF_PAGES", $"В PDF больше {Limits.PdfPages} страниц. Разделите документ; скрытого обрезания не было.");
        using var renderer = new PdfiumRenderer(File.ReadAllBytes(file));
        if (renderer.PageCount != document.NumberOfPages) throw new AppException("PDF_PAGE_MISMATCH", "Обработчики PDF определили разное количество страниц. Файл требует проверки.");
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            var text = ContentOrderTextExtractor.GetText(document.GetPage(number));
            Add($"страница {number}", string.IsNullOrWhiteSpace(text) ? "[Текст не извлечён. Для этой страницы требуется визуальное распознавание.]" : text);
            if (number <= Limits.VisualsPerFile)
            {
                var path = Path.Combine(_directory, $"visual-{_result.Visuals.Count + 1:0000}.png"); renderer.Render(number - 1, path, Limits.MaxImageSide);
                AddVisual(path, $"страница {number} (визуальный вид)");
            }
            else _skippedVisuals++;
        }
        if (document.NumberOfPages > Limits.VisualsPerFile) _result.Warnings.Add($"Визуальный вид PDF доступен только для первых {Limits.VisualsPerFile} страниц. Более поздние сканы и диаграммы НЕ распознаны; текстовый слой прочитан на всех {document.NumberOfPages} страницах.");
        _result.Warnings.Add("Растровый вид ограничен 1800 пикселями по длинной стороне; мелкий текст может читаться неточно. PDF-формы и вложения не выполнялись.");
    }
    private void ReadText(string file, string extension)
    {
        var bytes = File.ReadAllBytes(file); string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(1251).GetString(bytes);
            _result.Warnings.Add("Кодировка определена как Windows-1251 после неудачного чтения UTF-8. Проверьте правильность текста.");
        }
        if (text.Any(c => c == '\0') || text.Count(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) > Math.Max(2, text.Length / 100))
            throw new AppException("TEXT_BINARY", "Файл содержит двоичные данные, а не обычный текст.");
        if (extension == ".csv" && text.Length > 35_000)
        {
            var profile = new TabularProfile("CSV");
            using var input = new StringReader(text);
            foreach (var record in CsvStreaming.Read(input, CsvStreaming.DetectDelimiter(text))) profile.AddRow(record);
            Add("CSV, анализ всех записей", profile.Report());
            _result.Warnings.Add($"Большая CSV: агрегированы все {profile.Rows} строк локально. Построчная детализация остаётся в исходном файле, в облако передан только профиль. Формулы в исходной CSV не исполнялись.");
        }
        else
        {
            if (text.Length > Limits.DocumentCharacters) throw new AppException("TEXT_LIMIT", "Текст слишком большой для текстового режима; для таблиц используйте CSV/XLSX.");
            if (extension == ".csv")
            {
                var rows = CsvReader.Parse(text); var index = 0;
                foreach (var row in rows) Add($"CSV, строка {++index}", string.Join(" | ", row.Select((value, col) => $"столбец {col + 1}: {value}")));
            }
            else
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (var index = 0; index < lines.Length; index += 60)
                    Add($"строки {index + 1}–{Math.Min(index + 60, lines.Length)}", string.Join("\n", lines.Skip(index).Take(60)));
            }
        }
    }
    private bool ReadAnimation(string file)
    {
        MediaProbe info;
        try{info=MediaTools.ProbeAsync(file,CancellationToken.None).GetAwaiter().GetResult();}
        catch(AppException e)when(e.Code is "MEDIA_DURATION" or "MEDIA_DECODE"){return false;}
        var count=info.Frames>0?(int)Math.Min(24,info.Frames):info.Duration<0.2?1:24;
        foreach(var time in MediaTime.UniformFrames(info.Duration,count)){
            var output=Path.Combine(_directory,$"animation-{_result.Visuals.Count+1:0000}.jpg");
            var index=info.Frames>0?Math.Min(info.Frames-1,(long)(time/info.Duration*info.Frames)):-1;
            if(index>=0)MediaTools.FrameIndexAsync(file,output,index,CancellationToken.None).GetAwaiter().GetResult();
            else MediaTools.FrameAsync(file,output,time,CancellationToken.None).GetAwaiter().GetResult();
            if(!File.Exists(output))throw new AppException("ANIMATION_FRAME","Декодер не создал кадр анимации. Оригинал сохранён.");
            AddVisual(output,index>=0?$"кадр {index+1} из {info.Frames}":"кадр около "+MediaTime.Format(time));
        }
        _result.Warnings.Add($"Анимация: подготовлено {_result.Visuals.Count} кадров равномерно по последовательности кадров; длительность {MediaTime.Format(info.Duration)}. Это выборочный визуальный анализ; события между кадрами могут быть пропущены. Оригинал сохранён.");
        return true;
    }
    private void AddImage(byte[] bytes, string reference, bool standalone,string? extension=null)
    {
        if (bytes.Length == 0 || bytes.Length > Limits.FileBytes) throw new AppException("IMAGE_BYTES", "Размер изображения превышает ограничение.");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!_imageHashes.Add(hash)) return;
        var format = extension is null?FileGuard.ImageFormat(bytes):ImageFormats.DetectFile(bytes,extension);
        var settings = new MagickReadSettings { Format = ImageFormats.NativeFormat(format,extension) };
        if(standalone&&extension is ".psd" or ".psb")settings.FrameCount=1;
        using var images = new MagickImageCollection(); images.Read(bytes, settings);
        if(images.Count==0)throw new AppException("IMAGE_EMPTY","В изображении нет доступных кадров.");
        var composite=standalone&&extension is ".psd" or ".psb";
        var icon=standalone&&extension is ".ico" or ".cur" or ".icns";
        if(!composite&&!icon&&images.Count>(standalone?48:Limits.ImagePages))throw new AppException("IMAGE_PAGES","Изображение содержит слишком много страниц: до 48 для отдельного файла. Для анимации используйте GIF/APNG или видео. Исходник сохранён.");
        if(images.Count>1&&format!="TIFF"&&!composite&&!icon){images.Coalesce();_result.Warnings.Add("Многостраничное изображение: подготовлены доступные кадры/страницы; их смысл и порядок зависят от формата контейнера.");}
        var selected=icon?images.OrderByDescending(i=>(ulong)i.Width*i.Height).Take(1):composite?images.Take(1):images;
        if(composite)_result.Warnings.Add("PSD/PSB: анализируется сведённое изображение. Редактирование и отдельный анализ слоёв не выполняются; исходник сохранён.");
        if(icon)_result.Warnings.Add("Из набора значков выбран вариант с наибольшим разрешением.");
        if(ImageFormats.IsRaw(extension)||format is "HDR" or "EXR" or "DPX" or "CIN")_result.Warnings.Add("RAW/HDR преобразован в обычное изображение для визуального анализа. Цвет и динамический диапазон могут отличаться от профессиональной проявки; исходник сохранён.");
        var frame = 0;
        foreach (var image in selected)
        {
            frame++;
            if (_result.Visuals.Count >= (standalone?48:Limits.VisualsPerFile)) { _skippedVisuals++; continue; }
            if ((ulong)image.Width * image.Height > Limits.ImagePixels || image.Width == 0 || image.Height == 0) throw new AppException("IMAGE_PIXELS", "Изображение превышает 40 мегапикселей или имеет неверный размер.");
            image.AutoOrient();
            if (image.Width > Limits.MaxImageSide || image.Height > Limits.MaxImageSide)
            {
                image.Resize(new MagickGeometry(Limits.MaxImageSide, Limits.MaxImageSide) { Greater = true });
                _result.Warnings.Add("Для облака изображение уменьшено до 1800 пикселей по длинной стороне. Оригинал сохранён.");
            }
            image.Strip(); image.Format = MagickFormat.Png;
            var output = Path.Combine(_directory, $"visual-{_result.Visuals.Count + 1:0000}.png"); image.Write(output);
            if (new FileInfo(output).Length > 8 * 1024 * 1024) throw new AppException("IMAGE_NORMALIZED_SIZE", "После нормализации изображение превышает 8 МиБ. Выберите меньшую область.");
            AddVisual(output, reference + (images.Count > 1 ? $", страница {frame}" : ""));
        }
    }
    private void AddVisual(string path, string reference)
    {
        _result.Visuals.Add(new VisualSource(_result.Id + "-v" + (_result.Visuals.Count + 1), _result.Name + " · " + reference, Path.GetRelativePath(paths.Root, path).Replace('\\', '/')));
    }
}
