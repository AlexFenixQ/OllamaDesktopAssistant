using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, int Beta = int.MaxValue) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string text)
    {
        var m = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-beta\.(0|[1-9]\d*))?$", RegexOptions.CultureInvariant);
        if (!m.Success || text.Length > 60) throw new AppException("UPDATE_VERSION", "Некорректная версия обновления.");
        return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : int.MaxValue);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (var pair in new[] { (Major,other.Major), (Minor,other.Minor), (Patch,other.Patch), (Beta,other.Beta) })
        { var c = pair.Item1.CompareTo(pair.Item2); if (c != 0) return c; }
        return 0;
    }
}
public sealed class UpdateManifest
{
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public string MinimumVersion { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string InstallerSha256 { get; set; } = "";
    public long InstallerBytes { get; set; }
    public string InventoryUrl { get; set; } = "";
    public string InventorySha256 { get; set; } = "";
    public string Notes { get; set; } = "";
}
public static class UpdateFeed
{
    public const string Repository = "AlexFenixQ/OllamaDesktopAssistant";
    public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    public static void Validate(UpdateManifest m, string current, string selectedChannel)
    {
        var target = ReleaseVersion.Parse(m.Version); var baseline = ReleaseVersion.Parse(current);
        if (m.ProductId != "OllamaDesktopAssistant" || m.Channel is not ("stable" or "beta") || selectedChannel is not ("stable" or "beta") ||
            (selectedChannel == "stable" && m.Channel != "stable") || (m.Channel == "stable") != (target.Beta == int.MaxValue) ||
            target.CompareTo(baseline) <= 0 || baseline.CompareTo(ReleaseVersion.Parse(m.MinimumVersion)) < 0 ||
            m.InstallerBytes is < 100000 or > 300_000_000 || m.Notes.Length > 8000)
            throw new AppException("UPDATE_POLICY", "Этот выпуск не подходит для выбранного канала или установленной версии.");
        foreach (var hash in new[] { m.InstallerSha256, m.InventorySha256 })
            if (!Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) throw new AppException("UPDATE_HASH", "Манифест обновления повреждён.");
        ValidateUrl(m.InstallerUrl, m.Version); ValidateUrl(m.InventoryUrl, m.Version);
    }
    public static void ValidateUrl(string url, string version)
    {
        var prefix = DownloadPrefix + "v" + version + "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !url.StartsWith(prefix, StringComparison.Ordinal) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(url[prefix.Length..], @"^[A-Za-z0-9][A-Za-z0-9._-]{0,180}$"))
            throw new AppException("UPDATE_SOURCE", "Источник обновления не разрешён.");
    }
    public static UpdateManifest Verify(byte[] json, byte[] signature, string publicKeyXml)
    {
        if (json.Length > 64000 || signature.Length > 1024) throw new AppException("UPDATE_SIZE", "Манифест обновления слишком большой.");
        using var rsa = RSA.Create(); rsa.FromXmlString(publicKeyXml);
        if (!rsa.VerifyData(json, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new AppException("UPDATE_SIGNATURE", "Подпись обновления не прошла проверку. Установка отменена.");
        return JsonDefaults.Read<UpdateManifest>(System.Text.Encoding.UTF8.GetString(json));
    }
    public static IEnumerable<(string Tag, string Url)> Candidates(string releasesJson, string current, string channel)
    {
        if (channel is not ("stable" or "beta")) throw new ArgumentException("channel");
        using var doc = JsonDocument.Parse(releasesJson);
        var results = new List<(ReleaseVersion Version,string Tag,string Url)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || (channel == "stable" && r.GetProperty("prerelease").GetBoolean())) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            ReleaseVersion version; try { version = ReleaseVersion.Parse(tag); } catch { continue; }
            if (version.CompareTo(ReleaseVersion.Parse(current)) <= 0 || (channel == "stable" && version.Beta != int.MaxValue)) continue;
            var asset = r.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "update.json");
            if (asset.ValueKind == JsonValueKind.Undefined) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            ValidateUrl(url, tag.TrimStart('v')); results.Add((version,tag,url));
        }
        return results.OrderByDescending(r => r.Version).Select(r => (r.Tag,r.Url)).ToArray();
    }
}
