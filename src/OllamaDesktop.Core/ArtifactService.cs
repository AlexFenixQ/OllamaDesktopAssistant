using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace OllamaDesktop.Core;

public sealed class ArtifactService(AppPaths paths)
{
    public Artifact Create(string chatId, string requestId, OutputKind kind, ToolCall call, IReadOnlyList<VisualSource> visuals, CancellationToken token)
    {
        if (call.Name != ToolCatalog.Name(kind) || kind == OutputKind.Chat) throw new AppException("TOOL_DENIED", "Этот инструмент не разрешён выбранным режимом.");
        var images = visuals.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var imageIds = images.Keys.ToHashSet(StringComparer.Ordinal);
        var extension = kind switch { OutputKind.Word => ".docx", OutputKind.Excel => ".xlsx", _ => ".pptx" };
        object specification; string title;
        try
        {
            switch (kind)
            {
                case OutputKind.Word: var word = JsonDefaults.Read<WordSpec>(call.Arguments.GetRawText()); SpecValidation.Word(word, imageIds); specification = word; title = word.Title; break;
                case OutputKind.Excel: var workbook = JsonDefaults.Read<WorkbookSpec>(call.Arguments.GetRawText()); SpecValidation.Workbook(workbook); specification = workbook; title = workbook.Title; break;
                default: var slides = JsonDefaults.Read<PresentationSpec>(call.Arguments.GetRawText()); SpecValidation.Presentation(slides, imageIds); specification = slides; title = slides.Title; break;
            }
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new AppException("TOOL_SPEC_JSON", "Модель передала некорректную структуру документа. Файл не был создан.", error);
        }
        var relativeDirectory = "Generated/" + AppPaths.Id(chatId); Directory.CreateDirectory(paths.Resolve(relativeDirectory));
        var relative = relativeDirectory + "/" + AppPaths.SafeName(title) + "-" + AppPaths.Id(requestId)[..8] + extension;
        var final = paths.Resolve(relative);
        if (File.Exists(final)) throw new AppException("ARTIFACT_EXISTS", "Файл для этого запроса уже существует. Повторная запись запрещена.");
        var temp = paths.Resolve(relativeDirectory + "/pending-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            token.ThrowIfCancellationRequested();
            if (specification is WordSpec word) WriteWord(temp, word, images);
            else if (specification is WorkbookSpec workbook) WriteWorkbook(temp, workbook);
            else WritePresentation(temp, (PresentationSpec)specification, images);
            Validate(temp, extension); token.ThrowIfCancellationRequested();
            File.Move(temp, final, false);
            return new Artifact { Name = Path.GetFileName(final), RelativePath = relative, Format = extension, Bytes = new FileInfo(final).Length, Sha256 = AppPaths.Hash(final), Validation = kind == OutputKind.Excel ? "Структура проверена; поддерживаемые формулы пересчитаны. Содержимое требует проверки человеком." : "Структура Open XML проверена. Визуальная проверка и проверка фактов пользователем обязательны." };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Validate(string path, string extension)
    {
        FileGuard.CheckSize(path); FileGuard.CheckOffice(path, extension);
        using OpenXmlPackage package = extension switch
        {
            ".docx" => WordprocessingDocument.Open(path, false), ".xlsx" => SpreadsheetDocument.Open(path, false), ".pptx" => PresentationDocument.Open(path, false),
            _ => throw new AppException("OUTPUT_FORMAT", "Неизвестный формат результата.")
        };
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(package).Take(5).ToList();
        if (errors.Count > 0) throw new AppException("OPENXML_VALIDATION", "Созданный документ не прошёл структурную проверку (" + errors[0].Id + "). Итоговый файл не опубликован.");
    }
    private static string Template(string name)
    {
        using var stream = typeof(ArtifactService).Assembly.GetManifestResourceStream("OllamaDesktop.Core.Templates." + name)
            ?? throw new AppException("TEMPLATE_MISSING", "В сборке отсутствует шаблон документа.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private void WriteWord(string file, WordSpec spec, IReadOnlyDictionary<string, VisualSource> images)
    {
        using var document = WordprocessingDocument.Create(file, WordprocessingDocumentType.Document);
        document.PackageProperties.Title = spec.Title; document.PackageProperties.Creator = BuildInfo.Product;
        var main = document.AddMainDocumentPart(); main.Document = new W.Document(); var body = main.Document.AppendChild(new W.Body());
        main.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(Template("word-styles.xml"));
        body.Append(Paragraph(spec.Title, "Title"));
        var drawingId = 1U;
        foreach (var section in spec.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading)) body.Append(Paragraph(section.Heading, "Heading1"));
            foreach (var text in section.Paragraphs) body.Append(Paragraph(text));
            if (section.Table is { } table) body.Append(WordTable(table));
            foreach (var imageId in section.ImageIds)
            {
                var path = paths.Resolve(images[imageId].RelativePath); var part = main.AddImagePart(ImagePartType.Png); using (var input = File.OpenRead(path)) part.FeedData(input);
                var (cx, cy) = ImageExtent(path, 5_600_000, 6_000_000); var relationship = main.GetIdOfPart(part);
                var xml = $"""
                    <w:drawing xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{drawingId++}" name="Image"/>
                      <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
                        <pic:nvPicPr><pic:cNvPr id="0" name="Image"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="{relationship}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                      </pic:pic></a:graphicData></a:graphic></wp:inline>
                    </w:drawing>
                    """;
                body.Append(new W.Paragraph(new W.Run(new W.Drawing(xml))));
            }
        }
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }), new W.Run(new W.Text("Страница ")), new W.SimpleField(new W.Run(new W.Text("1"))) { Instruction = " PAGE " }));
        body.Append(new W.SectionProperties(new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new W.PageSize { Width = 11906U, Height = 16838U }, new W.PageMargin { Top = 1080, Right = 1080U, Bottom = 1080, Left = 1080U, Header = 500U, Footer = 500U, Gutter = 0U }));
        main.Document.Save();
    }
    private static W.Paragraph Paragraph(string text, string style = "Normal")
    {
        var p = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }));
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++) { if (index > 0) p.Append(new W.Run(new W.Break())); p.Append(new W.Run(new W.Text(lines[index]) { Space = SpaceProcessingModeValues.Preserve })); }
        return p;
    }
    private static W.Table WordTable(DocumentTable spec)
    {
        var table = new W.Table();
        table.Append(new W.TableProperties(new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" }, new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" }, new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" }, new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "D8DEE9" })));
        table.Append(new W.TableGrid(spec.Columns.Select(_ => new W.GridColumn { Width = (9600 / spec.Columns.Count).ToString(CultureInfo.InvariantCulture) })));
        var rows = new List<List<string>> { spec.Columns }; rows.AddRange(spec.Rows);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = new W.TableRow(); if (i == 0) row.Append(new W.TableRowProperties(new W.TableHeader()));
            foreach (var value in rows[i])
            {
                var properties = new W.TableCellProperties(new W.TableCellWidth { Width = (9600 / spec.Columns.Count).ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa });
                if (i == 0) properties.Append(new W.Shading { Fill = "E8EFFA", Val = W.ShadingPatternValues.Clear });
                row.Append(new W.TableCell(properties, Paragraph(value)));
            }
            table.Append(row);
        }
        return table;
    }
    private static void WriteWorkbook(string file, WorkbookSpec spec)
    {
        using var workbook = new XLWorkbook(); workbook.Properties.Title = spec.Title; workbook.Properties.Author = BuildInfo.Product;
        foreach (var sheetSpec in spec.Sheets)
        {
            var sheet = workbook.AddWorksheet(sheetSpec.Name);
            for (var col = 0; col < sheetSpec.Columns.Count; col++) sheet.Cell(1, col + 1).Value = sheetSpec.Columns[col];
            for (var row = 0; row < sheetSpec.Rows.Count; row++)
                for (var col = 0; col < sheetSpec.Columns.Count; col++)
                {
                    var value = sheetSpec.Rows[row][col]; var cell = sheet.Cell(row + 2, col + 1);
                    switch (value.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.String: cell.Value = value.GetString() ?? ""; break;
                        case System.Text.Json.JsonValueKind.Number: cell.Value = value.GetDouble(); break;
                        case System.Text.Json.JsonValueKind.True: cell.Value = true; break;
                        case System.Text.Json.JsonValueKind.False: cell.Value = false; break;
                        case System.Text.Json.JsonValueKind.Object: cell.FormulaA1 = SpecValidation.Formula(value.GetProperty("formula").GetString()!, sheetSpec.Rows.Count + 1, sheetSpec.Columns.Count); break;
                    }
                }
            var range = sheet.Range(1, 1, sheetSpec.Rows.Count + 1, sheetSpec.Columns.Count);
            range.Style.Font.FontName = "Segoe UI"; range.Style.Font.FontSize = 11;
            range.Style.Alignment.WrapText = true; range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            range.CreateTable().Theme = XLTableTheme.TableStyleMedium2;
            sheet.SheetView.FreezeRows(1); sheet.Row(1).Height = 32;
            for (var col = 1; col <= sheetSpec.Columns.Count; col++) sheet.Column(col).Width = Math.Clamp(sheetSpec.Columns[col - 1].Length + 8, 16, 36);
            for (var row = 0; row < sheetSpec.Rows.Count; row++)
            {
                var max = sheetSpec.Rows[row].Max(c => c.ValueKind == System.Text.Json.JsonValueKind.String ? (c.GetString()?.Length ?? 0) : 12);
                sheet.Row(row + 2).Height = Math.Clamp(20 + (max / 24) * 15, 22, 120);
            }
        }
        try
        {
            workbook.RecalculateAllFormulas();
            foreach (var sheet in workbook.Worksheets)
                foreach (var cell in sheet.CellsUsed())
                    if (cell.HasFormula && cell.Value.Type == XLDataType.Error) throw new AppException("FORMULA_RESULT", "Формула дала ошибку. Книга не опубликована.");
            workbook.SaveAs(file, new SaveOptions { EvaluateFormulasBeforeSaving = true, ValidatePackage = true });
        }
        catch (AppException) { throw; }
        catch (Exception e) { throw new AppException("WORKBOOK_CALCULATION", "Не удалось проверить расчёты книги. Используйте поддерживаемые формулы без циклических ссылок.", e); }
    }
    private void WritePresentation(string file, PresentationSpec spec, IReadOnlyDictionary<string, VisualSource> images)
    {
        using var document = PresentationDocument.Create(file, PresentationDocumentType.Presentation);
        document.PackageProperties.Title = spec.Title; document.PackageProperties.Creator = BuildInfo.Product;
        var main = document.AddPresentationPart(); var master = main.AddNewPart<SlideMasterPart>("rIdMaster");
        master.SlideMaster = new P.SlideMaster(Template("presentation-master.xml"));
        var theme = master.AddNewPart<ThemePart>("rIdTheme"); theme.Theme = new A.Theme(Template("presentation-theme.xml"));
        var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout"); layout.SlideLayout = new P.SlideLayout(Template("presentation-layout.xml")); layout.AddPart(master, "rIdMaster");
        var slideList = new P.SlideIdList();
        main.Presentation = new P.Presentation(new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rIdMaster" }), slideList, new P.SlideSize { Cx = 12192000, Cy = 6858000 }, new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        for (var index = 0; index < spec.Slides.Count; index++)
        {
            var specSlide = spec.Slides[index]; var part = main.AddNewPart<SlidePart>("rIdSlide" + (index + 1)); part.AddPart(layout, "rIdLayout");
            var shapes = new StringBuilder();
            shapes.Append(TextShape(2, "Title", 650000, 400000, 10892000, 1050000, [specSlide.Title], 3000, true));
            var withImage = !string.IsNullOrWhiteSpace(specSlide.ImageId);
            shapes.Append(TextShape(3, "Body", 700000, 1650000, withImage ? 5400000 : 10700000, 4200000, specSlide.Bullets.Select(x => "• " + x).ToArray(), 2000, false));
            shapes.Append(TextShape(4, "Footer", 700000, 6280000, 10700000, 300000, [$"{index + 1} / {spec.Slides.Count}"], 1000, false));
            if (withImage)
            {
                var image = images[specSlide.ImageId!]; var path = paths.Resolve(image.RelativePath); var imagePart = part.AddImagePart(ImagePartType.Png, "rIdImage"); using (var stream = File.OpenRead(path)) imagePart.FeedData(stream);
                var (cx, cy) = ImageExtent(path, 4700000, 4200000); var x = 6500000 + (4700000 - cx) / 2; var y = 1650000 + (4200000 - cy) / 2;
                shapes.Append($"""
                    <p:pic><p:nvPicPr><p:cNvPr id="5" name="Image"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>
                      <p:blipFill><a:blip r:embed="rIdImage"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>
                      <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                    </p:pic>
                    """);
            }
            part.Slide = new P.Slide($"""
                <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>
                    <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                    {shapes}
                  </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
                </p:sld>
                """);
            slideList.Append(new P.SlideId { Id = (uint)(256 + index), RelationshipId = main.GetIdOfPart(part) }); part.Slide.Save();
        }
        main.Presentation.Save();
    }
    private static string TextShape(int id, string name, long x, long y, long cx, long cy, IReadOnlyList<string> paragraphs, int size, bool bold)
    {
        var body = new StringBuilder();
        foreach (var text in paragraphs)
            body.Append($"""
                <a:p><a:pPr><a:spcAft><a:spcPts val="1400"/></a:spcAft></a:pPr><a:r><a:rPr lang="ru-RU" sz="{size}" b="{(bold ? "1" : "0")}"><a:solidFill><a:srgbClr val="{(bold ? "17324D" : "29384A")}"/></a:solidFill><a:latin typeface="Segoe UI"/><a:cs typeface="Segoe UI"/></a:rPr><a:t>{SecurityElement.Escape(text)}</a:t></a:r><a:endParaRPr lang="ru-RU" sz="{size}"/></a:p>
                """);
        if (paragraphs.Count == 0) body.Append("<a:p><a:endParaRPr lang=\"ru-RU\"/></a:p>");
        return $"""
            <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>
              <p:txBody><a:bodyPr wrap="square" lIns="0" tIns="0" rIns="0" bIns="0"><a:normAutofit/></a:bodyPr><a:lstStyle/>{body}</p:txBody>
            </p:sp>
            """;
    }
    private static (long Width, long Height) ImageExtent(string path, long maxWidth, long maxHeight)
    {
        using var stream = File.OpenRead(path); var header = new byte[24]; stream.ReadExactly(header);
        if (FileGuard.ImageFormat(header) != "PNG") throw new AppException("IMAGE_FORMAT", "Ожидалось нормализованное PNG-изображение.");
        var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)); var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));
        if (width <= 0 || height <= 0) throw new AppException("IMAGE_DIMENSIONS", "Некорректный размер изображения.");
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        return ((long)(width * scale), (long)(height * scale));
    }
}
