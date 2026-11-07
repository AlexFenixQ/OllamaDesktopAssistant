using System.Text.Json;
var dir = args.FirstOrDefault(a=>a.StartsWith("/DIR="))?.Substring(5).Trim('"');
if(dir is null) return 0; // Fake app restart: exit immediately, never read production data.
if(!Path.GetFullPath(dir).Contains("ODA-Updater-Test-")) return 9;
var stage=AppContext.BaseDirectory;
File.WriteAllText(Path.Combine(dir,"file-0.txt"),"partially replaced");
if(File.Exists(Path.Combine(stage,"fail.flag"))) return 42;
using var inventory=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"installed-files.json")));
foreach(var entry in inventory.RootElement.GetProperty("files").EnumerateArray()) {
    var rel=entry.GetProperty("path").GetString()!;
    var dest=Path.Combine(dir,rel);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    File.Copy(Path.Combine(stage,"payload",rel),dest,true);
}
File.Copy(Path.Combine(stage,"installed-files.json"),Path.Combine(dir,"installed-files.json"),true);
return 0;
