using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LumaIPTV;
public record Login(string Server, string Username, string Password);
public record Category(string Id, string Name) { public override string ToString() => Name; }
public record Channel(string Id, string Name, string CategoryId, string Kind, string Extension = "mp4", string Detail = "")
{
    public string Monogram => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => s[0])).ToUpperInvariant();
    public string Badge => Kind switch { "live" => "LIVE", "movie" => "FILM", "episode" => "EPISODE", _ => "SERIES" };
    public string Subtitle => string.IsNullOrEmpty(Detail) ? Kind switch { "live" => "Live television", "movie" => "On demand · Film", "series" => "Explore episodes", _ => "On demand · Episode" } : Detail;
    public string Key => Kind + ":" + Id;
}
public record GuideItem(string Time, string Title, string Description);

public sealed class XtreamClient : IDisposable
{
    readonly HttpClient http;
    readonly Login login;
    public XtreamClient(Login credentials, HttpMessageHandler? handler = null)
    {
        login = credentials with { Server = NormalizeServer(credentials.Server) };
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(35);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LumaIPTV/1.0");
    }
    public static string NormalizeServer(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter a server URL such as https://provider.example:8080, without login parameters.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/player_api.php", StringComparison.OrdinalIgnoreCase)) path = path[..^15];
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }
    public static string S(JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    static string Escape(string value) => Uri.EscapeDataString(value);
    public async Task<JsonNode> Request(string action = "", string? key = null, string? value = null, CancellationToken ct = default)
    {
        var url = $"{login.Server}/player_api.php?username={Escape(login.Username)}&password={Escape(login.Password)}";
        if (action.Length > 0) url += "&action=" + Escape(action);
        if (key != null) url += "&" + Escape(key) + "=" + Escape(value ?? "");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The provider returned HTTP {(int)response.StatusCode}. Check the server address and subscription.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct) ?? throw new InvalidOperationException("The provider returned an empty response.");
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Cannot reach the provider. Check the server URL and your internet connection."); }
        catch (JsonException) { throw new InvalidOperationException("This server did not return a valid Xtream API response. Check the server URL."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("The provider took too long to respond. Please try again."); }
    }
    public async Task<string> Authenticate(CancellationToken ct)
    {
        var response = await Request(ct: ct);
        var user = response["user_info"];
        if (S(user, "auth") != "1" || !S(user, "status", "Active").Equals("Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Login was rejected or the subscription is inactive. Check your username and password.");
        return S(user, "exp_date");
    }
    public async Task<(List<Category>, List<Channel>)> Library(string kind, CancellationToken ct)
    {
        var prefix = kind == "live" ? "live" : kind == "movie" ? "vod" : "series";
        var categoriesTask = Request("get_" + prefix + "_categories", ct: ct);
        var streamsTask = Request(kind == "series" ? "get_series" : "get_" + prefix + "_streams", ct: ct);
        await Task.WhenAll(categoriesTask, streamsTask);
        if (categoriesTask.Result is not JsonArray cats || streamsTask.Result is not JsonArray streams)
            throw new InvalidOperationException("The provider returned an unexpected library response.");
        return (cats.Where(c => c != null).Select(c => new Category(S(c,"category_id"),S(c,"category_name","Other"))).ToList(),
            streams.Where(c => c != null).Select(c => new Channel(S(c,kind == "series" ? "series_id" : "stream_id"), S(c,"name","Untitled"), S(c,"category_id"),kind,S(c,"container_extension","mp4"))).ToList());
    }
    public async Task<List<Channel>> Episodes(string id, CancellationToken ct)
    {
        var response = await Request("get_series_info", "series_id", id, ct);
        var episodes = response["episodes"] as JsonObject;
        if (episodes == null) return new();
        return episodes.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue)
            .SelectMany(k => (k.Value as JsonArray ?? new JsonArray()).Where(e => e != null).Select(e =>
                new Channel(S(e,"id"),S(e,"title","Episode"),k.Key,"episode", S(e,"container_extension","mp4"), $"Season {k.Key} · Episode {S(e,"episode_num")}"))).ToList();
    }
    public async Task<List<GuideItem>> Guide(string id, CancellationToken ct)
    {
        var response = await Request("get_short_epg", "stream_id", id, ct);
        return (response["epg_listings"] as JsonArray ?? new JsonArray()).Where(e => e != null).Take(5).Select(e => {
            var time = S(e,"start");
            if (long.TryParse(S(e,"start_timestamp"), out var stamp))
                try { time = DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("ddd HH:mm"); } catch (ArgumentOutOfRangeException) { }
            return new GuideItem(time, Decode(S(e,"title")), Decode(S(e,"description")));
        }).ToList();
    }
    public static string Decode(string text)
    {
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text));
            return decoded.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') ? text : decoded;
        }
        catch { return text; }
    }
    public string StreamUrl(Channel channel, string liveFormat)
    {
        if (!new[] { "live", "movie", "episode" }.Contains(channel.Kind)) throw new ArgumentException("Select an episode to play.");
        var ext = channel.Kind == "live" ? liveFormat : channel.Extension;
        if (ext.Length == 0 || ext.Length > 10 || !ext.All(char.IsLetterOrDigit)) ext = "mp4";
        return $"{login.Server}/{(channel.Kind == "episode" ? "series" : channel.Kind)}/{Escape(login.Username)}/{Escape(login.Password)}/{Escape(channel.Id)}.{ext}";
    }
    public void Dispose() => http.Dispose();
}

public static class SettingsStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LumaIPTV");
    static string LoginPath => Path.Combine(Root,"account.dat");
    public static void Save(Login login)
    {
        Directory.CreateDirectory(Root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(login);
        try { File.WriteAllBytes(LoginPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static Login? Load()
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(LoginPath), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<Login>(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        } catch { return null; }
    }
    public static void Forget() { if (File.Exists(LoginPath)) File.Delete(LoginPath); }
    public static string AccountKey(Login login) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(XtreamClient.NormalizeServer(login.Server) + "\n" + login.Username)))[..24];
    public static HashSet<string> Favorites(string account)
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(Path.Combine(Root,account + ".json"))) ?? new(); } catch { return new(); }
    }
    public static void SaveFavorites(string account, HashSet<string> favorites)
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, account + ".json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(favorites));
        File.Move(file + ".tmp", file, true);
    }
}
