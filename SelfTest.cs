using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

namespace LumaIPTV;
internal static class SelfTest
{
    public static async Task<string> Run()
    {
        var passed = new List<string>();
        void Check(bool result, string name) { if (!result) throw new Exception("FAIL: " + name); passed.Add("PASS: " + name); }
        var credentials = new Login("https://example.test:8080/", "a+b &/ü", "p?&#/=%");
        var handler = new FixtureHandler();
        using var api = new XtreamClient(credentials,handler);
        Check(await api.Authenticate(default) == "1900000000", "Xtream authentication and subscription status");
        Check(handler.Last!.Query.Contains("username=a%2Bb%20%26%2F%C3%BC") && handler.Last.Query.Contains("password=p%3F%26%23%2F%3D%25"), "Reserved and Unicode credentials escaped in requests");
        foreach (var kind in new[] { "live", "movie", "series" })
        {
            var (cats, items) = await api.Library(kind,default);
            Check(cats.Count == 1 && items.Count == 1 && items[0].Kind == kind && items[0].Id == "42", kind + " library parsing with numeric IDs");
        }
        Check(api.StreamUrl(new("42","Test","7","live"),"ts") == "https://example.test:8080/live/a%2Bb%20%26%2F%C3%BC/p%3F%26%23%2F%3D%25/42.ts", "Live URL and credential escaping");
        Check(api.StreamUrl(new("9","Episode","1","episode","mkv"),"ts").EndsWith("/9.mkv"), "Series episode URL and container");
        Check(api.StreamUrl(new("9","Film","1","movie","../bad"),"ts").EndsWith("/9.mp4"), "Invalid container falls back to mp4");
        Check(XtreamClient.NormalizeServer("https://example.test/base/player_api.php") == "https://example.test/base", "Provider subpath and API endpoint normalization");
        bool invalid = false; try { XtreamClient.NormalizeServer("file:///C:/test"); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Reject unsupported server protocols");
        var episodes = await api.Episodes("42",default);
        Check(episodes.Count == 2 && episodes[0].CategoryId == "1" && episodes[1].CategoryId == "2", "Episodes sorted by season");
        var guide = await api.Guide("42",default);
        Check(guide.Count == 1 && guide[0].Title == "Evening news" && guide[0].Description == "Plain description", "Base64 and plain program guide text");
        handler.Reject = true; bool rejected = false;
        try { await api.Authenticate(default); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected,"Rejected credentials do not authenticate"); handler.Reject = false;
        handler.Malformed = true; bool malformed = false;
        try { await api.Authenticate(default); } catch (InvalidOperationException ex) { malformed = !ex.Message.Contains(credentials.Password); }
        Check(malformed,"Malformed provider response handled without revealing credentials"); handler.Malformed = false;
        using var cts = new CancellationTokenSource(); cts.Cancel(); bool cancelled = false;
        try { await api.Library("live",cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled,"Cancelled provider requests stop loading");
        var bytes = Encoding.UTF8.GetBytes("test-only secret");
        var encrypted = ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
        Check(!encrypted.SequenceEqual(bytes) && ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser).SequenceEqual(bytes),"Windows account encryption round trip");
        Check(SettingsStore.AccountKey(credentials) != SettingsStore.AccountKey(credentials with { Username = "other" }),"Favorites isolated by account");
        Core.Initialize();
        using var vlc = new LibVLC("--quiet","--aout=dummy","--vout=dummy");
        using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
        var wav = Path.Combine(Path.GetTempPath(),"luma-test-" + Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                const int sampleRate = 22050, count = sampleRate * 4;
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                for (int i = 0; i < count; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / sampleRate) * 1000));
            }
            using var media = new Media(vlc,wav,FromType.FromPath);
            Check(player.Play(media),"Bundled VLC opens local media");
            for (int i = 0; i < 30 && !player.IsPlaying; i++) await Task.Delay(100);
            Check(player.IsPlaying,"VLC decodes and starts playback");
            player.SetPause(true); await Task.Delay(150); Check(player.State == VLCState.Paused,"Pause playback");
            player.SetPause(false); player.Position = 0.5f; await Task.Delay(250); Check(player.Time >= 1500,"Resume and seek playback");
            await Task.Run(() => player.Stop()); Check(player.State == VLCState.Stopped,"Stop playback");
        }
        finally { File.Delete(wav); }
        return string.Join(Environment.NewLine,passed) + $"\n\n{passed.Count} checks passed.\n";
    }
    sealed class FixtureHandler : HttpMessageHandler
    {
        public Uri? Last; public bool Reject, Malformed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Last = request.RequestUri;
            var query = Last!.Query;
            string json;
            if (Malformed) json = "<html>not an API</html>";
            else if (query.Contains("_categories")) json = "[{\"category_id\":7,\"category_name\":\"News\"}]";
            else if (query.Contains("get_series_info")) json = "{\"episodes\":{\"2\":[{\"id\":12,\"title\":\"Second\",\"episode_num\":1}],\"1\":[{\"id\":11,\"title\":\"First\",\"episode_num\":1}]}}";
            else if (query.Contains("get_short_epg")) json = "{\"epg_listings\":[{\"start_timestamp\":1800000000,\"title\":\"RXZlbmluZyBuZXdz\",\"description\":\"Plain description\"}]}";
            else if (query.Contains("action=")) json = "[{\"stream_id\":42,\"series_id\":42,\"name\":\"News One\",\"category_id\":7}]";
            else json = Reject ? "{\"user_info\":{\"auth\":0}}" : "{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":\"1900000000\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
