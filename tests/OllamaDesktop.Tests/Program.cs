using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using OllamaDesktop.Core;
namespace OllamaDesktop.Tests;
internal static class Program
{
    private static UpdateManifest ValidUpdate() => new() { ProductId="OllamaDesktopAssistant",Version="0.2.0",Channel="stable",MinimumVersion="0.1.32",InstallerUrl=UpdateFeed.DownloadPrefix+"v0.2.0/Setup.exe",InstallerSha256=new string('A',64),InstallerBytes=500000,InventoryUrl=UpdateFeed.DownloadPrefix+"v0.2.0/installed-files.json",InventorySha256=new string('B',64),Notes="Test" };
    private static readonly List<object> Results = [];
    private static int _failures;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "OllamaDesktop-Tests-" + Guid.NewGuid().ToString("N"));
    private static readonly AppPaths Paths = new(Root);
    private static readonly string Chat = Guid.NewGuid().ToString("N");
    private static string Id() => Guid.NewGuid().ToString("N");
    public static async Task<int> Main(string[] args)
    {
        Environment.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH",AppContext.BaseDirectory);
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("These acceptance tests require Windows x64, including native PDFium and DPAPI."); return 2; }
        await Test("update-semver-beta-before-stable", () => { True(ReleaseVersion.Parse("0.2.0-beta.2").CompareTo(ReleaseVersion.Parse("0.2.0-beta.10")) < 0); True(ReleaseVersion.Parse("0.2.0-beta.10").CompareTo(ReleaseVersion.Parse("0.2.0")) < 0); });
        await Test("update-semver-reject-invalid", () => { foreach(var v in new[]{"../1.0.0","01.0.0","1.0","1.2.3-rc.1","v1.2.3/evil"}) { bool rejected=false; try{ReleaseVersion.Parse(v);}catch{rejected=true;} True(rejected); } });
        await Test("update-signature-and-tamper", () => {
            using var rsa=System.Security.Cryptography.RSA.Create(2048);
            var data=Encoding.UTF8.GetBytes(JsonDefaults.Write(ValidUpdate()));
            var sig=rsa.SignData(data,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            Equal("0.2.0",UpdateFeed.Verify(data,sig,rsa.ToXmlString(false)).Version);
            data[10]^=1; bool rejected=false; try{UpdateFeed.Verify(data,sig,rsa.ToXmlString(false));}catch(AppException e){rejected=e.Code=="UPDATE_SIGNATURE";} True(rejected);
        });
        await Test("update-wrong-signing-key", () => {
            using var a=System.Security.Cryptography.RSA.Create(2048); using var b=System.Security.Cryptography.RSA.Create(2048);
            var data=Encoding.UTF8.GetBytes(JsonDefaults.Write(ValidUpdate())); var sig=a.SignData(data,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            bool rejected=false;try{UpdateFeed.Verify(data,sig,b.ToXmlString(false));}catch(AppException){rejected=true;}True(rejected);
        });
        await Test("update-stable-policy", () => UpdateFeed.Validate(ValidUpdate(),"0.1.32","stable"));
        await Test("update-beta-includes-stable", () => UpdateFeed.Validate(ValidUpdate(),"0.1.32","beta"));
        await Test("update-stable-rejects-beta", () => { var m=ValidUpdate();m.Channel="beta";m.Version="0.2.0-beta.1"; bool rejected=false;try{UpdateFeed.Validate(m,"0.1.32","stable");}catch(AppException){rejected=true;}True(rejected); });
        await Test("update-reject-downgrade-or-same", () => {foreach(var current in new[]{"0.2.0","0.3.0"}){bool rejected=false;try{UpdateFeed.Validate(ValidUpdate(),current,"stable");}catch(AppException){rejected=true;}True(rejected);}});
        await Test("update-reject-unsupported-base", () => {var m=ValidUpdate();m.MinimumVersion="0.2.0";bool rejected=false;try{UpdateFeed.Validate(m,"0.1.32","stable");}catch(AppException){rejected=true;}True(rejected);});
        await Test("update-reject-untrusted-downloads", () => {foreach(var url in new[]{"http://github.com/AlexFenixQ/OllamaDesktopAssistant/releases/download/v0.2.0/Setup.exe","https://evil.example/Setup.exe","https://github.com/other/repo/releases/download/v0.2.0/Setup.exe",UpdateFeed.DownloadPrefix+"v0.2.0/../Setup.exe",UpdateFeed.DownloadPrefix+"v0.2.0/Setup.exe?token=x"}){bool rejected=false;try{UpdateFeed.ValidateUrl(url,"0.2.0");}catch(AppException){rejected=true;}True(rejected);}});
        await Test("update-reject-hash-and-size", () => {foreach(var m in new[]{ValidUpdate(),ValidUpdate()}){if(m.InstallerSha256.Length==64)m.InstallerSha256="invalid";bool rejected=false;try{UpdateFeed.Validate(m,"0.1.32","stable");}catch(AppException){rejected=true;}True(rejected);}var tooLarge=ValidUpdate();tooLarge.InstallerBytes=300_000_001;bool failed=false;try{UpdateFeed.Validate(tooLarge,"0.1.32","stable");}catch(AppException){failed=true;}True(failed);});
        await Test("update-feed-channel-and-draft-selection", () => {
            object Release(string tag,bool beta,bool draft=false)=>new{tag_name=tag,prerelease=beta,draft,assets=new[]{new{name="update.json",browser_download_url=UpdateFeed.DownloadPrefix+tag+"/update.json"}}};
            var json=JsonSerializer.Serialize(new[]{Release("v0.2.0",false),Release("v0.3.0-beta.1",true),Release("v0.4.0",false,true)});
            Equal("v0.2.0",UpdateFeed.Candidates(json,"0.1.32","stable").First().Tag);Equal("v0.3.0-beta.1",UpdateFeed.Candidates(json,"0.1.32","beta").First().Tag);
        });
        await Test("update-options-backward-compatible", () => {var settings=JsonDefaults.Read<AppSettings>("{\"textModel\":\"gpt-oss:120b\",\"theme\":\"Light\"}");SettingsStore.Validate(settings);Equal("stable",settings.UpdateChannel);True(settings.AutomaticUpdateChecks);Equal("Light",settings.Theme);});
        await Test("project-rename-retains-chat-context", () => {var store=new ChatStore(Paths);store.Initialize();var p=new WorkspaceProject{Name="Before",Instructions="instruction",MemoryNotes="memory"};store.SaveProject(p);var c=new Conversation{ProjectId=p.Id};store.SaveConversation(c);p.Name="After";store.SaveProject(p);Equal("After",store.GetProject(p.Id)!.Name);Equal("memory",store.GetProject(p.Id)!.MemoryNotes);Equal(p.Id,store.List().First(x=>x.Id==c.Id).ProjectId);});
        await Test("auto-tools-available", () =>
        {
            var tools = ToolCatalog.Auto(); Equal(5, tools.Count);
            foreach (var tool in tools)
            {
                using var json = JsonDocument.Parse(JsonDefaults.Write(tool));
                True(json.RootElement.GetProperty("function").GetProperty("parameters").ValueKind == JsonValueKind.Object);
            }
            Equal(OutputKind.Word, ToolCatalog.Kind("create_word")!.Value);
            Equal(OutputKind.Excel, ToolCatalog.Kind("create_workbook")!.Value);
            Equal(OutputKind.PowerPoint, ToolCatalog.Kind("create_presentation")!.Value);
            Equal(OutputKind.Zip, ToolCatalog.Kind("create_archive")!.Value);
        });
        await Test("auto-file-intent", () => { True(FileRequestIntent.IsExplicitCreation("Создай Word Excel и ZIP")); True(!FileRequestIntent.IsExplicitCreation("Прочитай этот архив")); });
        await Test("intent-recognizes-four-formats", () =>
        {
            var formats = FileRequestIntent.RequestedKinds("Создай Word, Excel и PowerPoint, упакуй результат в ZIP");
            Equal(4, formats.Count);
            True(formats.Contains(OutputKind.Word) && formats.Contains(OutputKind.Excel) &&
                 formats.Contains(OutputKind.PowerPoint) && formats.Contains(OutputKind.Zip));
            Equal(0, FileRequestIntent.RequestedKinds("Прочитай word.xlsx и zip").Count);
        });
        await Test("auto-tools-without-early-archive", () =>
        {
            var functions = ToolCatalog.Auto(includeArchive: false);
            Equal(4, functions.Count);
            True(!JsonDefaults.Write(functions).Contains("create_archive"));
        });
        await Test("tool-chunks-as-string-fragments", () =>
        {
            var collector = new StreamingToolCollector();
            collector.Accept(JsonSerializer.SerializeToElement(new[] { new { index = 0, function = new { name = "create_word", arguments = "{\"title\":" } } }));
            collector.Accept(JsonSerializer.SerializeToElement(new[] { new { index = 0, function = new { arguments = "\"Отчёт\"}" } } }));
            var result = collector.Complete();
            Equal(1, result.Count); Equal("create_word", result[0].Name);
            Equal("Отчёт", result[0].Arguments.GetProperty("title").GetString());
        });
        await Test("tool-chunks-cumulative-string", () =>
        {
            var collector = new StreamingToolCollector();
            collector.Accept(JsonSerializer.SerializeToElement(new[] { new { index = 0, function = new { name = "calculate", arguments = "{\"expression\":" } } }));
            collector.Accept(JsonSerializer.SerializeToElement(new[] { new { index = 0, function = new { name = "calculate", arguments = "{\"expression\":\"2+2\"}" } } }));
            Equal("2+2", collector.Complete().Single().Arguments.GetProperty("expression").GetString());
        });
        await Test("tool-chunks-object-with-two-commands", () =>
        {
            var collector = new StreamingToolCollector();
            var a = JsonSerializer.SerializeToElement(new object[]
            {
                new { index = 0, function = new { name = "calculate", arguments = new { expression = "2+2" } } },
                new { index = 1, function = new { name = "calculate", arguments = new { expression = "3+3" } } }
            });
            collector.Accept(a); collector.Accept(a);
            Equal(2, collector.Complete().Count);
        });
        await Test("tool-chunks-malformed-arguments-rejected", () =>
        {
            var collector = new StreamingToolCollector();
            collector.Accept(JsonSerializer.SerializeToElement(new[] { new { index = 0, function = new { name = "calculate", arguments = "{not json" } } }));
            try { collector.Complete(); }
            catch (AppException error) when (error.Code == "TOOL_ARGS_JSON") { return; }
            throw new Exception("Expected TOOL_ARGS_JSON");
        });
        await TestAsync("engine-keeps-office-on-later-cloud-error", async () =>
        {
            var word = JsonSerializer.Deserialize<JsonElement>("""{"title":"Отчёт","sections":[{"heading":"Данные","paragraphs":["42"],"imageIds":[]}]}""");
            var first = JsonDefaults.Write(new { message = new { content = "", tool_calls = new object[] { new { function = new { name = "create_word", arguments = word } } } }, done = true }) + "\n";
            var turn = 0;
            using var client = new OllamaClient(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(++turn == 1 ? first : "{BROKEN}\n", Encoding.UTF8, "application/x-ndjson") })));
            var engine = new ConversationEngine(Paths, client);
            var result = await engine.GenerateAsync(Chat, Id(), "Создай Word-отчёт", [], [],
                new AppSettings { CloudConsent = true }, OutputKind.Chat, "TEST_ONLY_FAKE_KEY",
                new Progress<WorkProgress>(_ => { }), CancellationToken.None);
            True(!result.Complete); Equal(1, result.Artifacts.Count);
            True(File.Exists(Paths.Resolve(result.Artifacts[0].RelativePath)));
            Equal(".docx", result.Artifacts[0].Format);
        });
        await TestAsync("engine-generates-three-office-and-verified-zip", async () =>
        {
            var word = JsonSerializer.Deserialize<JsonElement>("""{"title":"Отчёт","sections":[{"heading":"Результат","paragraphs":["Проверено"],"imageIds":[]}]}""");
            var excel = JsonSerializer.Deserialize<JsonElement>("""{"title":"Таблица","sheets":[{"name":"Продажи","columns":["Товар","Сумма"],"rows":[["Бета",100],["Итого",{"formula":"SUM(B2:B2)"}]]}]}""");
            var pptx = JsonSerializer.Deserialize<JsonElement>("""{"title":"Презентация","slides":[{"title":"Итоги","bullets":["Результат проверен"]}]}""");
            var first = JsonDefaults.Write(new { message = new { content = "", tool_calls = new object[]
            {
                new { function = new { name = "create_word", arguments = word } },
                new { function = new { name = "create_workbook", arguments = excel } },
                new { function = new { name = "create_presentation", arguments = pptx } }
            } }, done = true }) + "\n";
            var next = """{"message":{"content":"Готово"},"done":true}""" + "\n";
            var requests = 0;
            using var client = new OllamaClient(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(++requests == 1 ? first : next, Encoding.UTF8, "application/x-ndjson") })));
            var engine = new ConversationEngine(Paths, client);
            var result = await engine.GenerateAsync(Chat, Id(), "Создай Word, Excel, PowerPoint и упакуй в ZIP", [], [],
                new AppSettings { CloudConsent = true }, OutputKind.Chat, "TEST_ONLY_FAKE_KEY",
                new Progress<WorkProgress>(_ => { }), CancellationToken.None);
            True(result.Complete); Equal(4, result.Artifacts.Count);
            foreach (var artifact in result.Artifacts) True(File.Exists(Paths.Resolve(artifact.RelativePath)));
            using var archive = ZipFile.OpenRead(Paths.Resolve(result.Artifacts.Single(a => a.Format == ".zip").RelativePath));
            Equal(3, archive.Entries.Count);
            True(archive.Entries.Any(x => x.Name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)));
            True(archive.Entries.Any(x => x.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)));
            True(archive.Entries.Any(x => x.Name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)));
        });

        await TestAsync("zip-import-unknown-files-reported", async () =>
        {
            var archive = MakeZip(("docs/readme.txt", "Привет"), ("setup/unknown.exe", "binary"));
            var stage = Path.Combine(Root, Id());
            var result = await ArchiveImport.ExpandDetailedAsync(archive, stage, 50, CancellationToken.None);
            Equal(1, result.Files.Count); Equal("readme.txt", result.Files[0].DisplayName);
            Equal(1, result.Skipped.Count);
        });
        await Test("zip-bundle-generated-office", () =>
        {
            var docx = Create(OutputKind.Word, """{"title":"Документ","sections":[{"heading":"Тест","paragraphs":["Работает"],"imageIds":[]}]}""");
            var bundle = ArchiveService.BundleGenerated(Paths, Chat, Id(), [docx], CancellationToken.None);
            using var zip = ZipFile.OpenRead(Paths.Resolve(bundle.RelativePath));
            Equal(1, zip.Entries.Count); Equal(docx.Name, zip.Entries[0].Name);
            using var input = zip.Entries[0].Open(); using var output = new MemoryStream(); input.CopyTo(output);
            True(output.Length == docx.Bytes);
        });
        await Test("arithmetic-precedence", () => Equal(14m, Arithmetic.Evaluate("2+3*4")));
        await Test("arithmetic-decimal", () => Equal(0.3m, Arithmetic.Evaluate("0.1+0.2")));
        await Test("arithmetic-functions", () => Equal(6m, Arithmetic.Evaluate("SUM(1,2,ABS(-3))")));
        await Test("arithmetic-round", () => Equal(2.35m, Arithmetic.Evaluate("ROUND(2.345,2)")));
        await Test("arithmetic-zero-division", () => Reject(() => Arithmetic.Evaluate("1/0")));
        await Test("arithmetic-no-code", () => Reject(() => Arithmetic.Evaluate("System.IO.File.Delete(1)")));
        await Test("arithmetic-depth", () => Reject(() => Arithmetic.Evaluate(new string('(', 100) + "1" + new string(')', 100))));
        await Test("path-traversal", () => Reject(() => Paths.Resolve("../outside.txt")));
        await Test("path-absolute", () => Reject(() => Paths.Resolve(@"C:\outside.txt")));
        await Test("path-invalid-id", () => Reject(() => AppPaths.Id("../chat")));
        await Test("safe-name", () => True(!AppPaths.SafeName("a/b:c?d").Contains('/')));
        await Test("unsupported-legacy", () => Reject(() => FileGuard.Extension("test.doc")));
        await Test("unsupported-macro", () => Reject(() => FileGuard.Extension("test.xlsm")));
        await Test("csv-quoted-multiline", () => { var rows = CsvReader.Parse("a;b\r\n\"x;z\";\"two\nlines\"\r\n"); Equal(2, rows.Count); Equal("x;z", rows[1][0]); Equal("two\nlines", rows[1][1]); });
        await Test("csv-unclosed-quote", () => Reject(() => CsvReader.Parse("a,b\n\"bad")));
        await Test("context-split-no-loss", () => { var text = new string('x', 20_001); Equal(text, string.Concat(ContextPlanner.Split([new SourceSegment("a", text)]).Select(s => s.Text))); });
        await Test("context-relevance", () => { var selected = ContextPlanner.Retrieve([new("1", "other"), new("2", "Нужная таблица")], "таблица", 30); True(selected.Any(s => s.Reference == "2")); });
        await Test("context-history-limit", () => { var messages = Enumerable.Range(0, 20).Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = new string('x', 2000) }).ToArray(); var selected = ContextPlanner.History(messages, out var omitted); True(omitted); True(selected.Sum(x => x.Content.Length) <= Limits.HistoryCharacters); True(selected[0].Role == "user"); });
        await Test("tool-schemas-parse", () => { foreach (var kind in Enum.GetValues<OutputKind>()) using (JsonDocument.Parse(JsonDefaults.Write(ToolCatalog.For(kind)))) { } });
        await Test("spec-unknown-property", () => Reject(() => JsonDefaults.Read<WordSpec>("{\"title\":\"A\",\"sections\":[],\"shell\":\"cmd\"}")));
        await Test("spec-empty-word", () => Reject(() => SpecValidation.Word(new WordSpec { Title = "A" }, new HashSet<string>())));
        await Test("spec-forbidden-image-path", () => Reject(() => SpecValidation.Word(new WordSpec { Title = "A", Sections = [new WordSection { ImageIds = ["C:/secret.png"] }] }, new HashSet<string>())));
        await Test("formula-safe", () => Equal("SUM(B2:B3)", SpecValidation.Formula("=SUM(B2:B3)", 4, 3)));
        await Test("formula-external", () => Reject(() => SpecValidation.Formula("'[book.xlsx]Sheet'!A1", 10, 5)));
        await Test("formula-outside-sheet", () => Reject(() => SpecValidation.Formula("Z999", 10, 5)));
        await Test("formula-webservice", () => Reject(() => SpecValidation.Formula("WEBSERVICE(1)", 10, 5)));
        await Test("zip-path-traversal", () => { var file = MakeZip(("../bad.xml", "x")); Reject(() => FileGuard.CheckOffice(file, ".docx")); });
        await Test("zip-macro-reject", () => { var file = MakeZip(("word/vbaProject.bin", "x")); Reject(() => FileGuard.CheckOffice(file, ".docx")); });
        await Test("zip-xml-dtd-reject", () => { var file = MakeZip(("[Content_Types].xml", "<!DOCTYPE x SYSTEM 'file:///not-allowed'><x/>"), ("word/document.xml", "x")); Reject(() => FileGuard.CheckOffice(file, ".docx")); });
        await TestAsync("zip-import-multiple", async () => { var archive = MakeZip(("reports/a.txt", "hello"), ("таблица/b.csv", "x;y")); var stage=Path.Combine(Root,Id()); var extracted=await ArchiveImport.ExpandAsync(archive,stage,50,CancellationToken.None); Equal(2,extracted.Count); True(File.ReadAllText(extracted[0])=="hello"); });
        await TestAsync("zip-import-traversal-rejected", async () => { var archive=MakeZip(("../secret.txt","x")); var stage=Path.Combine(Root,Id()); await RejectCodeAsync("ZIP_PATH", async () => { await ArchiveImport.ExpandAsync(archive,stage,50,CancellationToken.None); }); });
        await TestAsync("zip-import-over-limit-rejected", async () => { var archive=MakeZip(("a.txt","a"),("b.txt","b")); var stage=Path.Combine(Root,Id()); await RejectCodeAsync("ATTACHMENT_COUNT", async () => { await ArchiveImport.ExpandAsync(archive,stage,1,CancellationToken.None); }); });
        await Test("zip-create-text-file", () => { using var doc=JsonDocument.Parse("""{"title":"Проверка","files":[{"name":"summary.txt","content":"готово"}],"attachmentIds":[]}"""); var artifact=ArchiveService.Create(Paths,Chat,Id(),new ToolCall("create_archive",doc.RootElement.Clone()),[],CancellationToken.None); using var zip=ZipFile.OpenRead(Paths.Resolve(artifact.RelativePath)); using var reader=new StreamReader(zip.GetEntry("summary.txt")!.Open()); Equal("готово",reader.ReadToEnd()); });
        await Test("zip-create-traversal-rejected", () => { using var doc=JsonDocument.Parse("""{"title":"Bad","files":[{"name":"../danger.txt","content":"x"}],"attachmentIds":[]}"""); Reject(()=>ArchiveService.Create(Paths,Chat,Id(),new ToolCall("create_archive",doc.RootElement.Clone()),[],CancellationToken.None)); });
        await Test("image-signature-reject", () => Reject(() => FileGuard.ImageFormat(Encoding.UTF8.GetBytes("not-an-image"))));
        await Test("image-extension-mismatch", () => Reject(() => FileGuard.CheckImageExtension(".jpg", "PNG")));
        await Test("text-ingestion-cyrillic", () => { var a = ImportBytes(Encoding.UTF8.GetBytes("Первый абзац\nВторой абзац"), ".txt"); True(a.Segments.Any(s => s.Text.Contains("Второй"))); });
        await Test("png-native-ingestion", () => { var file = Path.Combine(Root, Id() + ".png"); PngWriter.WriteBgra(file, 2, 2, 8, Enumerable.Repeat((byte)255, 16).ToArray()); var a = ImportBytes(File.ReadAllBytes(file), ".png"); Equal(1, a.Visuals.Count); True(File.Exists(Paths.Resolve(a.Visuals[0].RelativePath))); });
        await Test("svg-safe-native-rasterization", () => {var a=ImportBytes(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='24'><rect width='32' height='24' fill='red'/></svg>"),".svg");Equal(1,a.Visuals.Count);});
        await Test("svg-external-http-case-and-base-rejected",()=>{foreach(var x in new[]{"<svg><image href='https://example.com/a.png'/></svg>","<svg><image HREF='file:///private.png'/></svg>","<svg xml:base='https://example.com/'><use href='#id'/></svg>"})Reject(()=>ImageFormats.ValidateSvg(Encoding.UTF8.GetBytes(x)));});
        await Test("svg-external-local-file-rejected",()=>Reject(()=>ImageFormats.ValidateSvg(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///C:/private.png'/></svg>"))));
        await Test("svg-dtd-and-script-rejected",()=>{foreach(var x in new[]{"<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///private'>]><svg>&x;</svg>","<svg><script>alert(1)</script></svg>"})Reject(()=>ImageFormats.ValidateSvg(Encoding.UTF8.GetBytes(x)));});
        await Test("svg-encoded-external-css-rejected",()=>{foreach(var x in new[]{"<svg><style>rect{fill:u&#114;l(file:///private.png)}</style></svg>","<svg><style>rect{fill:u\\72l(file:///private.png)}</style></svg>"})Reject(()=>ImageFormats.ValidateSvg(Encoding.UTF8.GetBytes(x)));});
        await Test("pdf-text-and-native-render", () => { var a = ImportBytes(MakePdf(), ".pdf"); True(a.Segments.Any(s => s.Text.Contains("Hello PDF"))); Equal(1, a.Visuals.Count); });
        await Test("generate-docx-and-extract", () =>
        {
            var artifact = Create(OutputKind.Word, """{"title":"Проверка Word","sections":[{"heading":"Раздел","paragraphs":["Тестовый абзац"],"imageIds":[],"table":{"columns":["Имя","Значение"],"rows":[["А","42"]]}}]}""");
            var a = ImportBytes(File.ReadAllBytes(Paths.Resolve(artifact.RelativePath)), ".docx"); True(a.Segments.Any(s => s.Text.Contains("Тестовый абзац"))); True(a.Segments.Any(s => s.Text.Contains("42")));
        });
        await Test("generate-xlsx-and-cached-calculation", () =>
        {
            var artifact = Create(OutputKind.Excel, """{"title":"Проверка Excel","sheets":[{"name":"Данные","columns":["Имя","Сумма"],"rows":[["А",10],["Б",20],["Итого",{"formula":"SUM(B2:B3)"}]]}]}""");
            using (var workbook = new XLWorkbook(Paths.Resolve(artifact.RelativePath))) Equal(30d, workbook.Worksheet(1).Cell("B4").GetDouble());
            var a = ImportBytes(File.ReadAllBytes(Paths.Resolve(artifact.RelativePath)), ".xlsx"); True(a.Segments.Any(s => s.Text.Contains("SUM(B2:B3)")));
        });
        await Test("generate-pptx-and-extract", () =>
        {
            var artifact = Create(OutputKind.PowerPoint, """{"title":"Презентация","slides":[{"title":"Первый слайд","bullets":["Основной результат","Второй тезис"]},{"title":"Итоги","bullets":["Проверить документ"]}]}""");
            var a = ImportBytes(File.ReadAllBytes(Paths.Resolve(artifact.RelativePath)), ".pptx"); True(a.Segments.Any(s => s.Text.Contains("Основной результат")));
        });
        await Test("artifact-refuses-overwrite", () => { var request = Id(); var argsJson = """{"title":"NoOverwrite","sections":[{"heading":"A","paragraphs":["B"],"imageIds":[]}]}"""; Create(OutputKind.Word, argsJson, request); Reject(() => Create(OutputKind.Word, argsJson, request)); });
        await Test("artifact-wrong-tool-denied", () => Reject(() => new ArtifactService(Paths).Create(Chat, Id(), OutputKind.Chat, new ToolCall("run_shell", JsonSerializer.SerializeToElement(new { command = "test" })), [], CancellationToken.None)));
        await Test("artifact-cancelled-no-file", () => { var request = Id(); using var token = new CancellationTokenSource(); token.Cancel(); Reject(() => new ArtifactService(Paths).Create(Chat, request, OutputKind.Word, new ToolCall("create_word", JsonDocument.Parse("""{"title":"Cancelled","sections":[{"heading":"A","paragraphs":["B"],"imageIds":[]}]}""").RootElement), [], token.Token)); True(!Directory.EnumerateFiles(Paths.Resolve("Generated/" + Chat)).Any(p => Path.GetFileName(p).Contains(request[..8]))); });
        await Test("sqlite-persistence-search-cascade", () =>
        {
            var store = new ChatStore(Paths); store.Initialize(); var chat = new Conversation { Title = "Тест" }; store.SaveConversation(chat);
            store.SaveMessage(new ChatMessage { ChatId = chat.Id, Role = "user", Content = "Проверка сохранения" });
            var reopened = new ChatStore(Paths); Equal(1, reopened.LoadMessages(chat.Id).Count); True(reopened.List("СОХРАНЕНИЯ").Any(c => c.Id == chat.Id));
            store.SaveMessage(new ChatMessage { ChatId = chat.Id, Role = "assistant", Content = "partial", Status = "streaming" }); reopened.Initialize(); Equal("interrupted", reopened.LoadMessages(chat.Id)[1].Status);
            var sentinel = Paths.Resolve("Generated/unrelated.txt"); File.WriteAllText(sentinel, "keep"); reopened.DeleteConversation(chat.Id); Equal(0, reopened.LoadMessages(chat.Id).Count); True(File.Exists(sentinel));
        });
        await Test("regenerate-last-answer-without-duplicate-user-message", () =>
        {
            var previous = new ChatMessage { ChatId = Chat, Role = "assistant", Content = "Earlier answer" };
            var question = new ChatMessage { ChatId = Chat, Role = "user", Content = "Создай Word и Excel" };
            var reply = new ChatMessage { ChatId = Chat, Role = "assistant", Content = "Partial", Status = "partial" };
            var plan = RegenerationPlanner.Select([previous, question, reply]);
            True(plan is not null);
            Equal(question.Id, plan!.User.Id); Equal(reply.Id, plan.Previous.Id);
            Equal(1, plan.History.Count); Equal(previous.Id, plan.History[0].Id);
            True(RegenerationPlanner.Select([previous, question]) is null);
        });
        await Test("projects-keep-original-chat-db-schema-one", () =>
        {
            var local = new AppPaths(Path.Combine(Root,"project-compat-"+Id()));
            var store = new ChatStore(local); store.Initialize();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+local.Database);
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
            command.CommandText = "PRAGMA table_info(chats)";
            using var columns = command.ExecuteReader(); var foundProjectColumn = false;
            while(columns.Read()) if (columns.GetString(1) == "project_id") foundProjectColumn = true;
            True(!foundProjectColumn);
        });
        await Test("projects-assign-chat-and-preserve-instructions", () =>
        {
            var local = new AppPaths(Path.Combine(Root,"project-store-"+Id()));
            var store = new ChatStore(local); store.Initialize();
            var project = new WorkspaceProject { Name = "Торговля и склад", Instructions = "Отвечай по-русски", MemoryNotes = "Временный склад W001" };
            store.SaveProject(project);
            var chat = new Conversation { Title = "Остатки", ProjectId = project.Id };
            store.SaveConversation(chat);
            var reopened = new ChatStore(local); reopened.Initialize();
            Equal(project.Id, reopened.List().Single().ProjectId);
            Equal("Временный склад W001", reopened.GetProject(project.Id)!.MemoryNotes);
            True(File.Exists(local.Resolve("Projects/projects.sqlite")));
        });
        await Test("project-memory-length-is-bounded", () =>
        {
            var local = new AppPaths(Path.Combine(Root,"project-limit-"+Id()));
            var store = new ChatStore(local); store.Initialize();
            Reject(() => store.SaveProject(new WorkspaceProject { Name="Test", MemoryNotes=new string('X',4001) }));
        });
        await Test("deleting-project-keeps-chat-history", () =>
        {
            var local = new AppPaths(Path.Combine(Root,"project-delete-"+Id()));
            var store = new ChatStore(local); store.Initialize();
            var project = new WorkspaceProject { Name = "Проект" }; store.SaveProject(project);
            var chat = new Conversation { ProjectId = project.Id }; store.SaveConversation(chat);
            store.SaveMessage(new ChatMessage { ChatId = chat.Id, Role="user", Content="Данные" });
            store.DeleteProject(project.Id);
            True(store.GetProject(project.Id) is null);
            True(store.List().Single().ProjectId is null);
            Equal("Данные",store.LoadMessages(chat.Id).Single().Content);
        });
        await Test("tabular-profile-1c-sales-exact-totals", () =>
        {
            var profile = new TabularProfile("Реализация");
            profile.AddRow(["Документ", "Дата", "Склад", "Номенклатура", "Количество", "НДС, ₽", "Итого, ₽"]);
            profile.AddRow(["РН-1", "2026-01-01", "W001", "N000001", "2", "200.50", "1200.50"]);
            profile.AddRow(["РН-2", "2026-01-02", "W002", "N000001", "3", "400.00", "2400.00"]);
            profile.AddRow(["РН-3", "2026-01-03", "W001", "N000002", "1", "0.00", "800.00"]);
            var text = profile.Report();
            True(text.Contains("Обработано локально: 3 строк"));
            True(text.Contains("4400.50") && text.Contains("600.50"));
            True(text.Contains("N000001 = 3600.50") && text.Contains("W001 = 2000.50"));
        });
        await Test("tabular-profile-stock-available-value", () =>
        {
            var profile = new TabularProfile("Остатки");
            profile.AddRow(["Склад", "Номенклатура", "Остаток", "Резерв", "Себестоимость, ₽"]);
            profile.AddRow(["W01", "N1", "120", "25", "3.50"]);
            profile.AddRow(["W02", "N2", "100", "60", "2.00"]);
            var text = profile.Report();
            True(text.Contains("135") && text.Contains("412.50"));
            True(TabularProfile.TryNumber("1 250,50", out var money) && money == 1250.50m);
        });
        await Test("csv-stream-quoted-multiline", () =>
        {
            using var reader = new StringReader("name;note;total\nN1;\"с переносом\nстроки\";12.5\nN2;\"a;\"\"b\"\"\";2\n");
            var result = CsvStreaming.Read(reader,';').ToList();
            Equal(3, result.Count); Equal("с переносом\nстроки", result[1][1]);
            Equal("a;\"b\"",result[2][1]);
        });
        await Test("csv-large-aggregates-all-rows", () =>
        {
            var data = new StringBuilder("Документ;Склад;КодНоменклатуры;Итого;НДС\n");
            for (int i=0;i<2500;i++) data.Append("R").Append(i).Append(";W01;N000001;10.25;2.00\n");
            var a = ImportBytes(Encoding.UTF8.GetBytes(data.ToString()),".csv");
            var summary = string.Join("\n", a.Segments.Select(segment=>segment.Text));
            True(summary.Contains("2500 строк") && summary.Contains("25625.00") && summary.Contains("5000.00"));
            True(a.Warnings.Any(w=>w.Contains("все 2500 строк")));
        });
        await Test("xlsx-large-aggregates-without-cloud-summary", () =>
        {
            using var workbook = new XLWorkbook(); var sheet = workbook.AddWorksheet("Реализация");
            sheet.Cell(1,1).Value="Склад"; sheet.Cell(1,2).Value="Номенклатура";
            sheet.Cell(1,3).Value="Итого, ₽"; sheet.Cell(1,4).Value="НДС, ₽";
            for(int i=2; i<=251; i++) { sheet.Cell(i,1).Value="W01";sheet.Cell(i,2).Value="N1";sheet.Cell(i,3).Value=100;sheet.Cell(i,4).Value=20; }
            using var stream = new MemoryStream(); workbook.SaveAs(stream);
            var a = ImportBytes(stream.ToArray(),".xlsx");
            var summary = string.Join("\n", a.Segments.Select(segment=>segment.Text));
            True(summary.Contains("250 строк") && summary.Contains("25000.00") && summary.Contains("5000.00"));
            True(a.Warnings.Any(w=>w.Contains("250 строк")));
        });
        await Test("csv-1c-120000-rows-profile", () =>
        {
            DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "test_data", "ODA_1C_120000_Synthetic_Sales.csv"))) folder = folder.Parent;
            True(folder is not null);
            var source = Path.Combine(folder!.FullName, "test_data", "ODA_1C_120000_Synthetic_Sales.csv");
            var report = ImportBytes(File.ReadAllBytes(source), ".csv");
            True(report.Segments.Any(x => x.Text.Contains("Обработано локально: 120000 строк")));
            True(report.Segments.Sum(x => x.Text.Length) < Limits.ContextCharacters);
        });
        await Test("dpapi-roundtrip-not-plaintext", () => { var settings = new SettingsStore(Paths); const string fake = "TEST_ONLY_NOT_A_REAL_API_KEY"; settings.StoreKey(fake); Equal(fake, settings.ReadKey()); True(!Encoding.UTF8.GetString(File.ReadAllBytes(Paths.KeyFile)).Contains(fake)); settings.DeleteKey(); True(!settings.HasKey); });
        await Test("diagnostics-excludes-secrets", () => { var settings = new SettingsStore(Paths); settings.StoreKey("TEST_ONLY_NOT_A_REAL_API_KEY"); Diagnostics.Record(Paths, "TEST", new Exception("PRIVATE_SAMPLE")); var file = Path.Combine(Root, "diagnostics.zip"); Diagnostics.Export(Paths, file); using var zip = ZipFile.OpenRead(file); foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd(); True(!text.Contains("PRIVATE_SAMPLE")); True(!text.Contains("TEST_ONLY_NOT_A_REAL_API_KEY")); True(!entry.FullName.EndsWith(".db")); } settings.DeleteKey(); });
        await TestAsync("http-stream-text", async () => { using var client = Fake(HttpStatusCode.OK, "{\"message\":{\"content\":\"Привет\"},\"done\":false}\n{\"message\":{\"content\":\"!\"},\"done\":true}\n"); var result = await Call(client); Equal("Привет!", result.Content); });
        await TestAsync("http-401", async () => { using var client = Fake(HttpStatusCode.Unauthorized, "PRIVATE_BODY"); await RejectCodeAsync("AUTH_401", () => Call(client)); });
        await TestAsync("http-429", async () => { using var client = Fake((HttpStatusCode)429, "PRIVATE_BODY"); await RejectCodeAsync("LIMIT_429", () => Call(client)); });
        await TestAsync("http-incomplete-stream", async () => { using var client = Fake(HttpStatusCode.OK, "{\"message\":{\"content\":\"partial\"}}\n"); await RejectCodeAsync("STREAM_INCOMPLETE", () => Call(client)); });
        await TestAsync("http-malformed-stream", async () => { using var client = Fake(HttpStatusCode.OK, "{BROKEN}\n"); await RejectCodeAsync("STREAM_JSON", () => Call(client)); });
        await TestAsync("http-tool-object", async () => { using var client = Fake(HttpStatusCode.OK, """{"message":{"content":"","tool_calls":[{"function":{"name":"calculate","arguments":{"expression":"2+2"}}}]},"done":true}""" + "\n"); var result = await Call(client); Equal("calculate", result.Tools.Single().Name); Equal("2+2", result.Tools[0].Arguments.GetProperty("expression").GetString()); });
        await TestAsync("http-cancellation", async () => { using var client = new OllamaClient(new Stub(async (_, token) => { await Task.Delay(30_000, token); return new HttpResponseMessage(HttpStatusCode.OK); })); using var cancel = new CancellationTokenSource(50); await RejectAsync(() => client.ChatAsync("fake", "test", [], null, null, null, cancel.Token)); });

        await Test("spec-null-word-section", () => RejectSpec(() => SpecValidation.Word(JsonDefaults.Read<WordSpec>("""{"title":"A","sections":[null]}"""), new HashSet<string>())));
        await Test("spec-null-word-row", () => RejectSpec(() => SpecValidation.Word(JsonDefaults.Read<WordSpec>("""{"title":"A","sections":[{"table":{"columns":["A"],"rows":[null]}}]}"""), new HashSet<string>())));
        await Test("spec-null-workbook-sheet", () => RejectSpec(() => SpecValidation.Workbook(JsonDefaults.Read<WorkbookSpec>("""{"title":"A","sheets":[null]}"""))));
        await Test("spec-null-workbook-row", () => RejectSpec(() => SpecValidation.Workbook(JsonDefaults.Read<WorkbookSpec>("""{"title":"A","sheets":[{"name":"A","columns":["A"],"rows":[null]}]}"""))));
        await Test("spec-numeric-formula", () => RejectSpec(() => SpecValidation.Workbook(JsonDefaults.Read<WorkbookSpec>("""{"title":"A","sheets":[{"name":"A","columns":["A"],"rows":[[{"formula":12}]]}]}"""))));
        await Test("spec-null-presentation-slide", () => RejectSpec(() => SpecValidation.Presentation(JsonDefaults.Read<PresentationSpec>("""{"title":"A","slides":[null]}"""), new HashSet<string>())));
        await Test("zip-missing-title-is-validation-error", () => RejectArchive("""{"files":[]}""", "ZIP_SPEC"));
        await Test("zip-invalid-id-array-is-validation-error", () => RejectArchive("""{"title":"A","files":[],"attachmentIds":[7]}""", "ZIP_SPEC"));
        await Test("zip-missing-entry-content-is-validation-error", () => RejectArchive("""{"title":"A","files":[{"name":"a.txt"}]}""", "ZIP_SPEC"));
        await Test("zip-reserved-windows-name-rejected", () => RejectArchive("""{"title":"A","files":[{"name":"CON.txt","content":"a"}]}""", "ZIP_PATH"));
        await Test("profile-column-200-is-not-dropped", () =>
        {
            var p = new TabularProfile("Wide"); var header = Enumerable.Range(1,200).Select(i => "C"+i).ToArray(); header[199]="Сумма";
            p.AddRow(header); var values=Enumerable.Repeat("",200).ToArray(); values[199]="42"; p.AddRow(values);
            True(p.Report().Contains("42.00"));
        });
        await Test("profile-too-many-columns-rejected", () =>
        {
            try { new TabularProfile("Wide").AddRow(Enumerable.Repeat("A",257).ToArray()); }
            catch(AppException e) when(e.Code=="TABLE_COLUMNS") { return; }
            throw new Exception("Expected TABLE_COLUMNS");
        });
        await Test("profile-quantity-only-top", () => { var p=new TabularProfile("Stock"); p.AddRow(["Товар","Количество"]); p.AddRow(["Бета","12"]); True(p.Report().Contains("ТОП номенклатуры по количеству")); });
        await Test("csv-delimiter-ignores-quoted-header", () =>
        {
            const string text="\"A,B,C,D\";Сумма\r\nТовар;42\r\n";
            Equal(';',CsvStreaming.DetectDelimiter(text)); Equal(2,CsvReader.Parse(text)[0].Count);
        });
        await Test("csv-delimiter-multiline-header", () => { const string text="\"A\nB,C,D\";Сумма\nТовар;42\n"; Equal(';',CsvStreaming.DetectDelimiter(text)); Equal(2,CsvReader.Parse(text)[0].Count); });
        await Test("xlsx-column-257-rejected-without-truncation", () =>
        {
            var file=Path.Combine(Root,Id()+".xlsx"); using(var book=new XLWorkbook()) { var sheet=book.AddWorksheet("Wide"); sheet.Cell(1,257).Value="Сумма"; sheet.Cell(2,257).Value=42; book.SaveAs(file); }
            try { ImportBytes(File.ReadAllBytes(file),".xlsx"); }
            catch(AppException e) when(e.Code=="XLSX_COLUMNS") { return; }
            throw new Exception("Expected XLSX_COLUMNS");
        });
        await Test("sqlite-old-message-does-not-reorder-chat", () =>
        {
            var store=new ChatStore(Paths); store.Initialize(); var newer=DateTimeOffset.UtcNow; var c=new Conversation { Updated=newer }; store.SaveConversation(c);
            store.SaveMessage(new ChatMessage { ChatId=c.Id, Created=newer.AddDays(-1), Content="Old" });
            Equal(newer,store.List().Single(x=>x.Id==c.Id).Updated);
        });
        await Test("archive-replay-survives-serialization", () =>
        {
            var id=Id(); var folder=Paths.AttachmentDirectory(Chat,id); Directory.CreateDirectory(folder); var file=Path.Combine(folder,"original.zip"); File.Copy(MakeZip(("a.txt","42")),file);
            var a=JsonDefaults.Read<Attachment>(JsonDefaults.Write(new Attachment { Id=id, Name="Данные.zip", RelativeOriginal=Path.GetRelativePath(Paths.Root,file), Sha256=AppPaths.Hash(file) }));
            True(ArchiveReplay.IsArchive(a)); Equal(file,ArchiveReplay.Resolve(Paths,Chat,a));
            Reject(()=>ArchiveReplay.Resolve(Paths,Id(),a)); File.AppendAllText(file,"tamper"); Reject(()=>ArchiveReplay.Resolve(Paths,Chat,a));
        });
        await TestAsync("archive-cancel-before-extraction", async () =>
        {
            using var c=new CancellationTokenSource(); c.Cancel(); var stage=Path.Combine(Root,Id());
            try { await ArchiveImport.ExpandDetailedAsync(MakeZip(("a.txt","42")),stage,50,c.Token); }
            catch(OperationCanceledException) { True(!Directory.Exists(stage)); return; }
            throw new Exception("Expected cancellation");
        });
        await Test("stream-two-unindexed-same-name-tools", () =>
        {
            var collector=new StreamingToolCollector();
            foreach(var expression in new[]{"2+2","3+3"}) collector.Accept(JsonSerializer.SerializeToElement(new[]{new{function=new{name="calculate",arguments=new{expression}}}}));
            Equal(2,collector.Complete().Count);
        });
        await Test("draft-word-table-preserved", () =>
        {
            var text="```json\n"+"""{"title":"A","sections":[{"heading":"B","paragraphs":[],"table":{"columns":["A"],"rows":[["42"]]}}]}"""+"\n```";
            var call=DraftToolRecovery.FromAssistantText(text,new HashSet<OutputKind>{OutputKind.Word}).Single();
            Equal("42",call.Arguments.GetProperty("sections")[0].GetProperty("table").GetProperty("rows")[0][0].GetString());
        });

        await TestAsync("vision-keeps-history-and-project-context", async () =>
        {
            var image=Paths.Resolve("Staging/"+Id()+".png"); PngWriter.WriteBgra(image,1,1,4,[255,255,255,255]);
            var visual=new VisualSource("v1","test image",Path.GetRelativePath(Paths.Root,image));
            var requests=0;
            using var client=new OllamaClient(new Stub(async (request,token) =>
            {
                requests++;
                if(request.Method==HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("""{"models":[{"name":"gemma4:31b","capabilities":["vision"]}]}""") };
                var body=await request.Content!.ReadAsStringAsync(token); using var doc=JsonDocument.Parse(body);
                var vision=doc.RootElement.GetProperty("model").GetString()=="gemma4:31b";
                if(!vision) { True(body.Contains("PRIOR_CONTEXT_42")); True(body.Contains("PROJECT_CONTEXT_42")); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(JsonDefaults.Write(new {message=new {content=vision ? "image description" : "contextual answer"},done=true})+"\n") };
            }));
            var result=await new ConversationEngine(Paths,client).GenerateAsync(Chat,Id(),"What is in the image?",[new ChatMessage {Content="PRIOR_CONTEXT_42"}],[new Attachment {Visuals=[visual]}],new AppSettings {CloudConsent=true},OutputKind.Chat,"FAKE_TEST_KEY",new Progress<WorkProgress>(_=>{}),CancellationToken.None,"PROJECT_CONTEXT_42");
            Equal("contextual answer",result.Content); Equal(3,requests);
        });
        await TestAsync("vision-three-images-are-synthesized-together", async () =>
        {
            var visuals=new List<VisualSource>();
            for(var i=0;i<3;i++) {var image=Paths.Resolve("Staging/"+Id()+".png"); PngWriter.WriteBgra(image,1,1,4,[255,255,255,255]);visuals.Add(new VisualSource("v"+i,"image"+i,Path.GetRelativePath(Paths.Root,image)));}
            var batches=0;
            using var client=new OllamaClient(new Stub(async (request,token) =>
            {
                if(request.Method==HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("""{"models":[{"name":"gemma4:31b","capabilities":["vision"]}]}""")};
                var body=await request.Content!.ReadAsStringAsync(token);using var doc=JsonDocument.Parse(body);
                var vision=doc.RootElement.GetProperty("model").GetString()=="gemma4:31b";
                if(!vision) {True(body.Contains("BATCH_1"));True(body.Contains("BATCH_2"));}
                return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(JsonDefaults.Write(new {message=new {content=vision ? "BATCH_"+(++batches) : "combined answer"},done=true})+"\n")};
            }));
            var result=await new ConversationEngine(Paths,client).GenerateAsync(Chat,Id(),"Compare the images",[],[new Attachment {Visuals=visuals}],new AppSettings {CloudConsent=true},OutputKind.Chat,"FAKE_TEST_KEY",new Progress<WorkProgress>(_=>{}),CancellationToken.None);
            Equal("combined answer",result.Content); Equal(2,batches);
        });

        await Test("media-six-hour-timeline-no-gaps",()=>{var chunks=MediaTime.Chunks(21600).ToArray();Equal(72,chunks.Length);Equal(21600d,chunks.Sum(x=>x.Duration));for(var i=1;i<chunks.Length;i++)Equal(chunks[i-1].Start+chunks[i-1].Duration,chunks[i].Start);});
        await Test("media-duration-invalid-rejected",()=>{foreach(var n in new[]{0d,-1,21600.001,double.NaN,double.PositiveInfinity})Reject(()=>MediaTime.Chunks(n).ToList());});
        await Test("media-last-part-not-dropped",()=>{var c=MediaTime.Chunks(601.25).ToArray();Equal(3,c.Length);Equal(1.25,c[^1].Duration);Equal(600d,c[^1].Start);});
        await Test("media-frames-span-whole-recording",()=>{var f=MediaTime.UniformFrames(21600);Equal(24,f.Count);True(f.First()<500);True(f.Last()>21000);True(f.All(t=>t>=0&&t<21600));});
        await Test("media-playlist-and-executable-masquerades-rejected",()=>{foreach(var text in new[]{"#EXTM3U\nhttps://example.com/a.mp3","MZ fake executable","<html>remote source</html>"}){var f=Paths.Resolve("Staging/"+Id()+".mp4");File.WriteAllText(f,text);Reject(()=>MediaTools.CheckHeader(f));}});
        await Test("media-limits-independent",()=>{Equal(512L*1024*1024,Limits.FileBytes);Equal(2L*1024*1024*1024,Limits.MediaBytes);True(FileGuard.IsMedia("Conference.MP4"));True(FileGuard.IsMedia("audio.FLAC"));True(!FileGuard.IsMedia("fake.mp4.exe"));});
        await Test("media-old-settings-and-attachments-compatible",()=>{var settings=JsonDefaults.Read<AppSettings>("{}");SettingsStore.Validate(settings);Equal("small",settings.SpeechModel);var a=JsonDefaults.Read<Attachment>("{\"name\":\"old.txt\"}");True(a.Media is null);True(!a.CanResume);});
        await Test("media-report-word-is-real-validated-document",()=>{var a=MediaArtifacts.Report(Paths,Chat,"# Итоги\nБюджет: 125000 рублей.\nОтветственный: не указан.","Проверка покрытия",CancellationToken.None);True(File.Exists(Paths.Resolve(a.RelativePath)));Equal(a.Sha256,AppPaths.Hash(Paths.Resolve(a.RelativePath)));using var d=WordprocessingDocument.Open(Paths.Resolve(a.RelativePath),false);True(d.MainDocumentPart!.Document!.InnerText.Contains("125000"));});
        await TestAsync("media-incomplete-report-rejected-before-cloud",async()=>{using var client=Fake(HttpStatusCode.OK,"{}");await RejectCodeAsync("MEDIA_INCOMPLETE",()=>new MediaReportEngine(Paths,client).GenerateAsync(Chat,"Report",[new Attachment{Media=new MediaMetadata{DurationSeconds=600}}],new AppSettings{CloudConsent=true},"FAKE_TEST_KEY",new Progress<WorkProgress>(),CancellationToken.None));});
        await TestAsync("media-report-covers-first-and-last-blocks",async()=>{
            var seenFirst=false;var seenLast=false;var summaries=0;var finals=0;
            using var client=new OllamaClient(new Stub(async(request,token)=>{
                var raw=await request.Content!.ReadAsStringAsync(token);using var doc=JsonDocument.Parse(raw);var system=doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                string answer;
                if(system.Contains("Cover EVERY")){seenFirst|=raw.Contains("FIRST_EVIDENCE");seenLast|=raw.Contains("LAST_EVIDENCE");answer="SUMMARY_"+(++summaries);}
                else{finals++;True(raw.Contains("SUMMARY_1"));True(raw.Contains("SUMMARY_"+summaries));answer="# Итоги\nПолная сводка теста.";}
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Write(new{message=new{content=answer},done=true})+"\n")};
            }));
            var a=new Attachment{Name="synthetic.wav",Media=new MediaMetadata{DurationSeconds=600,AudioComplete=true,FramesComplete=true},Segments=[new SourceSegment("00:00:00","FIRST_EVIDENCE "+new string('x',19000)),new SourceSegment("00:09:59",new string('y',19000)+" LAST_EVIDENCE")]};
            var result=await new MediaReportEngine(Paths,client).GenerateAsync(Chat,"Report",[a],new AppSettings{CloudConsent=true},"FAKE_TEST_KEY",new Progress<WorkProgress>(),CancellationToken.None);
            True(seenFirst&&seenLast);True(summaries>=3);Equal(1,finals);Equal(1,result.Artifacts.Count);
        });


        await Test("image-multipage-tiff-nine-pages-kept",()=>{using var pages=new ImageMagick.MagickImageCollection();for(var i=0;i<9;i++)pages.Add(new ImageMagick.MagickImage(new ImageMagick.MagickColor((byte)(i*20),30,90),32,24));using var ms=new MemoryStream();pages.Write(ms,ImageMagick.MagickFormat.Tiff);var a=ImportBytes(ms.ToArray(),".tiff");Equal(9,a.Visuals.Count);});
        await Test("image-psd-composite-warning",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Red,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Psd),".psd");Equal(1,a.Visuals.Count);True(a.Warnings.Any(x=>x.Contains("сведённое")));});
        await Test("image-raw-and-modern-extensions-admitted",()=>{foreach(var ext in new[]{".heic",".avif",".jxl",".dng",".nef",".cr3",".psd",".gif",".jxr"})Equal(ext,FileGuard.Extension("sample"+ext));});
        await TestAsync("native-codec-matrix-includes-real-avi-audio",async()=>{var decoders=await MediaTools.RunAsync("ffmpeg",["-hide_banner","-decoders"],TimeSpan.FromSeconds(20),CancellationToken.None);foreach(var codec in new[]{"pcm_mulaw","pcm_alaw","msvideo1","cinepak","msmpeg4v3","dvvideo","ffv1","huffyuv","ac3","eac3","libaom-av1"})True(decoders.Contains(codec,StringComparison.OrdinalIgnoreCase));});
        await TestAsync("avi-msvideo1-mulaw-decodes-audio-and-frames",async()=>{
            var photo=Paths.Resolve("Staging/"+Id()+".jpg");using(var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue,64,48))image.Write(photo);
            var wav=Paths.Resolve("Staging/"+Id()+".wav");using(var b=new BinaryWriter(File.Create(wav))){b.Write(Encoding.ASCII.GetBytes("RIFF"));b.Write(36+64000);b.Write(Encoding.ASCII.GetBytes("WAVEfmt "));b.Write(16);b.Write((short)1);b.Write((short)1);b.Write(16000);b.Write(32000);b.Write((short)2);b.Write((short)16);b.Write(Encoding.ASCII.GetBytes("data"));b.Write(64000);for(var i=0;i<32000;i++)b.Write((short)(Math.Sin(i*0.1)*5000));}
            var avi=Paths.Resolve("Staging/"+Id()+".avi");await MediaTools.RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-loop","1","-framerate","2","-i",photo,"-i",wav,"-t","2","-c:v","msvideo1","-pix_fmt","rgb555le","-c:a","pcm_mulaw",avi],TimeSpan.FromSeconds(30),CancellationToken.None);
            var probe=await MediaTools.ProbeAsync(avi,CancellationToken.None);True(probe.Audio&&probe.Video);var sound=Paths.Resolve("Staging/"+Id()+".wav");await MediaTools.AudioChunkAsync(avi,sound,0,2,CancellationToken.None);True(new FileInfo(sound).Length>60000);var frame=Paths.Resolve("Staging/"+Id()+".jpg");await MediaTools.FrameAsync(avi,frame,0.5,CancellationToken.None);True(File.Exists(frame));
        });
        await Test("image-native-roundtrip-gif",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Gif),".gif");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-avif",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Avif),".avif");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-heic-hevc-decoding",()=>{var bytes=File.ReadAllBytes(FindFixture("Formats/RGB_8__128x128.heif"));var a=ImportBytes(bytes,".heif");Equal(1,a.Visuals.Count);});
        await Test("image-synthetic-dng-bayer-decoding",()=>{var a=ImportBytes(File.ReadAllBytes(FindFixture("Formats/synthetic-bayer.dng")),".dng");Equal(1,a.Visuals.Count);True(a.Warnings.Any(x=>x.Contains("RAW/HDR")));});
        await Test("image-native-roundtrip-jxl",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Jxl),".jxl");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-jp2",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Jp2),".jp2");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-ico",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Ico),".ico");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-dds",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Dds),".dds");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-pcx",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Pcx),".pcx");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-tga",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Tga),".tga");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-exr",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Exr),".exr");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-hdr",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Hdr),".hdr");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-ppm",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Ppm),".ppm");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-pam",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Pam),".pam");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});
        await Test("image-native-roundtrip-pfm",()=>{using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue,64,48);var a=ImportBytes(image.ToByteArray(ImageMagick.MagickFormat.Pfm),".pfm");True(a.Visuals.Count>0);True(a.Visuals.All(v=>File.Exists(Paths.Resolve(v.RelativePath))));});


        await TestAsync("av1-software-decoder-reads-real-avif-bitstream",async()=>{var file=Paths.Resolve("Staging/"+Id()+".avif");using(var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Green,64,48))image.Write(file);await MediaTools.RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-protocol_whitelist","file","-i",file,"-frames:v","1","-f","null","-"],TimeSpan.FromSeconds(30),CancellationToken.None);});
        await Test("gif-animation-keeps-whole-timeline",()=>{using var frames=new ImageMagick.MagickImageCollection();foreach(var color in new[]{ImageMagick.MagickColors.Red,ImageMagick.MagickColors.Green,ImageMagick.MagickColors.Blue}){var image=new ImageMagick.MagickImage(color,64,48){AnimationDelay=100};frames.Add(image);}using var stream=new MemoryStream();frames.Write(stream,ImageMagick.MagickFormat.Gif);var a=ImportBytes(stream.ToArray(),".gif");True(a.Visuals.Count>=3&&a.Visuals.Count<=24);True(a.Visuals.Any(x=>x.Reference.Contains("кадр 3 из 3")));True(a.Warnings.Any(x=>x.Contains("равномерно")));});


        await TestAsync("audio-cover-art-is-not-a-video-track",async()=>{var cover=Paths.Resolve("Staging/"+Id()+".jpg");using(var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Red,32,24))image.Write(cover);var file=Paths.Resolve("Staging/"+Id()+".m4a");await MediaTools.RunAsync("ffmpeg",["-hide_banner","-v","error","-nostdin","-n","-i",FindFixture("Media/conference-video.mp4"),"-i",cover,"-map","0:a:0","-map","1:v:0","-t","2","-c:a","aac","-c:v","copy","-disposition:v:0","attached_pic","-f","mp4",file],TimeSpan.FromSeconds(30),CancellationToken.None);var probe=await MediaTools.ProbeAsync(file,CancellationToken.None);True(probe.Audio&&!probe.Video);});


        await Test("avi-unknown-service-track-does-not-extend-duration",()=>{var p=MediaTools.ParseProbe("""{"format":{"duration":"2296.422588"},"streams":[{"codec_type":"video","codec_name":"hevc","duration":"1799.978040","width":1920,"height":1080,"nb_frames":"45000"},{"codec_type":"audio","codec_name":"pcm_mulaw"},{"codec_type":"audio","duration":"2296.422588"}]}""");Equal(1799.978040,p.Duration);True(p.Audio&&p.Video);Equal(1,p.UnknownTracks);Equal(2,p.AudioTracks);});

        var report = args.Length == 2 && args[0] == "--report" ? args[1] : Path.Combine(Environment.CurrentDirectory, "tests.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonDefaults.Write(new { version = BuildInfo.Version, utc = DateTimeOffset.UtcNow, platform = "Windows x64", count = Results.Count, failed = _failures, cloudRequests = 0, tests = Results }));
        Console.WriteLine($"{Results.Count - _failures}/{Results.Count} passed. Live cloud and visual/user acceptance are not included.");
        try { Directory.Delete(Root, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    private static string FindFixture(string relative){DirectoryInfo? folder=new DirectoryInfo(AppContext.BaseDirectory);while(folder is not null&&!File.Exists(Path.Combine(folder.FullName,"test_data",relative)))folder=folder.Parent;return Path.Combine(folder?.FullName??throw new Exception("Fixture missing"),"test_data",relative);}
    private static void RejectSpec(Action action)
    {
        try { action(); } catch(AppException e) when(e.Code=="DOCUMENT_SPEC") { return; }
        throw new Exception("Expected DOCUMENT_SPEC");
    }
    private static void RejectArchive(string json, string code)
    {
        using var document=JsonDocument.Parse(json);
        try { ArchiveService.Create(Paths,Chat,Id(),new ToolCall("create_archive",document.RootElement.Clone()),[],CancellationToken.None); }
        catch(AppException e) when(e.Code==code) { return; }
        throw new Exception("Expected "+code);
    }
    private static Task Test(string name, Action test) => TestAsync(name, () => { test(); return Task.CompletedTask; });
    private static async Task TestAsync(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failures++; var code = e is AppException known ? known.Code : e.GetType().Name; Results.Add(new { name, passed = false, code }); Console.Error.WriteLine("FAIL " + name + " [" + code + "]"); }
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Assertion failed"); }
    private static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected rejection"); }
    private static async Task RejectCodeAsync(string code, Func<Task> action)
    {
        try { await action(); }
        catch (AppException e) when (e.Code == code) { return; }
        throw new Exception("Expected specific rejection: " + code);
    }
    private static Artifact Create(OutputKind kind, string json, string? id = null)
    {
        using var doc = JsonDocument.Parse(json); return new ArtifactService(Paths).Create(Chat, id ?? Id(), kind, new ToolCall(ToolCatalog.Name(kind), doc.RootElement.Clone()), [], CancellationToken.None);
    }
    private static Attachment ImportBytes(byte[] bytes, string extension)
    {
        var id = Id(); var folder = Paths.AttachmentDirectory(Chat, id); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "original" + extension), bytes);
        return new ExtractionEngine(Paths).Extract(new ExtractionJob(Chat, id, "Тест" + extension, extension));
    }
    private static string MakeZip(params (string Name, string Value)[] entries)
    {
        var file = Path.Combine(Root, Id() + ".zip"); using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var item in entries) { var entry = zip.CreateEntry(item.Name); using var writer = new StreamWriter(entry.Open()); writer.Write(item.Value); } return file;
    }
    private static byte[] MakePdf()
    {
        var content = "BT /F1 18 Tf 72 720 Td (Hello PDF) Tj ET\n";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream" };
        var builder = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString())); builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString()); builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10") + " 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(builder.ToString());
    }
    private static OllamaClient Fake(HttpStatusCode code, string body) => new(new Stub((request, token) =>
    {
        True(request.RequestUri!.Host == "ollama.com");
        True(request.Headers.Authorization?.Scheme == "Bearer");
        return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") });
    }));
    private static Task<ProviderResult> Call(OllamaClient client) => client.ChatAsync("TEST_ONLY_FAKE_KEY", "gpt-oss:120b", [new ApiMessage { Content = "test" }], null, null, null, CancellationToken.None);
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
}
