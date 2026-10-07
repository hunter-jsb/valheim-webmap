using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using BepInEx.Configuration;
using Xunit;
using Mod = WebMap.WebMap;

namespace WebMap.Tests
{
    // The write routes over real HTTP, on a loopback port: who may write, and as whom.
    // Configured as an operator does it: the BepInEx config, and announce.token beside the DLL.
    public class GateTests : WithDir
    {
        const string Token = "proxy-secret", Pin = "{\"op\":\"add\",\"x\":1,\"z\":2}";
        readonly string tokenFile = Path.Combine(AppContext.BaseDirectory, "announce.token");
        readonly (int port, string guild, string url, string key) was =
            (WebMapConfig.SERVER_PORT, WebMapConfig.DISCORD_GUILD, WebMapConfig.PUBLIC_URL, WebMapConfig.AUTH_PUBLIC_KEY);
        readonly MapDataServer server;
        readonly HttpClient http = new HttpClient();

        public GateTests()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            string cfg = Path.Combine(Dir, "webmap.cfg");
            File.WriteAllText(cfg, $"[Server]\nserver_port = {port}\ndiscord_guild = {AuthTests.Guild}\npublic_url = {AuthTests.Map}\n"
                                 + $"auth_public_key = {AuthTests.KeyOf(AuthTests.Service)}\n");
            WebMapConfig.ReadConfigFile(new ConfigFile(cfg, false));
            File.WriteAllText(tokenFile, Token);
            Settings.ResetForTests(Dir);
            Mod.worldDataPath = Dir;
            Mod.mapDataServer = server = new MapDataServer(IPAddress.Loopback);
            server.ListenAsync();
            http.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        }

        public override void Dispose()
        {
            server.Stop();
            File.Delete(tokenFile);
            (WebMapConfig.SERVER_PORT, WebMapConfig.DISCORD_GUILD, WebMapConfig.PUBLIC_URL, WebMapConfig.AUTH_PUBLIC_KEY) = was;
            Mod.mapDataServer = null; Mod.worldDataPath = null;
            base.Dispose();
        }

        int Send(HttpMethod method, string path, string body, params (string, string)[] headers)
        {
            var req = new HttpRequestMessage(method, path);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            return (int)http.Send(req).StatusCode;
        }
        int Post(string path, string body, params (string, string)[] h) => Send(HttpMethod.Post, path, body, h);
        int Get(string path, params (string, string)[] h) => Send(HttpMethod.Get, path, null, h);
        static (string, string) Bearer(bool owner = false, string guild = AuthTests.Guild, string aud = AuthTests.Map) =>
            ("Authorization", "Bearer " + AuthTests.Sign(AuthTests.Service, AuthTests.Body(owner, guild, aud)));
        string Owner() => server.pins.Last().Split(',')[3];

        [Fact]
        public void AWriteWithNoCredentialsIsRefused()
        {
            Assert.Equal(403, Post("/pins", Pin));
            Assert.Equal(403, Post("/names", "{\"id\":\"x\",\"name\":\"y\"}"));
            Assert.Equal(403, Post("/announce", "restart in 5"));
            Assert.Equal(403, Get("/settings"));
            Assert.Empty(server.pins);
        }

        [Fact]
        public void TheProxysTokenWritesAsTheUserItNames()
        {
            Assert.Equal(200, Post("/pins", Pin, ("X-Announce-Token", Token), ("X-User", "Gonk%20the%20B%C3%B8ld")));
            Assert.Equal("Gonk the Bøld", Owner());
            Assert.Equal(403, Get("/settings", ("X-Announce-Token", Token)));
            Assert.Equal(200, Get("/settings", ("X-Announce-Token", Token), ("X-Admin", "1")));
            Assert.Equal(202, Post("/announce", "restart in 5", ("X-Announce-Token", Token)));
        }

        [Fact]
        public void XUserAndXAdminWithoutTheTokenAreIgnored()
        {
            Assert.Equal(403, Post("/pins", Pin, ("X-User", "Mallory"), ("X-Admin", "1")));
            Assert.Equal(403, Post("/pins", Pin, ("X-Announce-Token", "proxy-secreT"), ("X-User", "Mallory")));
            Assert.Equal(200, Post("/pins", Pin, Bearer(), ("X-User", "Mallory")));
            Assert.Equal("Rurik \"the Red\"", Owner());
            Assert.Equal(403, Get("/settings", Bearer(), ("X-Admin", "1")));
        }

        [Fact]
        public void ASessionWritesAsAMemberAndReachesTheSettingsOnlyAsAnAdmin()
        {
            Assert.Equal(200, Post("/pins", Pin, Bearer()));
            Assert.Equal(403, Get("/settings", Bearer()));
            Assert.Equal(200, Get("/settings", Bearer(owner: true)));
            Assert.Equal(403, Post("/announce", "restart in 5", Bearer(owner: true)));     // the proxy's token alone
        }

        [Fact]
        public void TheSpawnWatchAnswersAnAdminAndNoMember()
        {
            Assert.Equal(403, Get("/spawns"));
            Assert.Equal(403, Get("/spawns", Bearer()));
            Assert.Equal(403, Get("/spawns", ("X-Announce-Token", Token)));
            Assert.Equal(200, Get("/spawns", Bearer(owner: true)));
            Assert.Equal(200, Get("/spawns", ("X-Announce-Token", Token), ("X-Admin", "1")));
        }

        [Fact]
        public void ASessionForAnotherDiscordServerOrAnotherMapIsRefusedAtTheRoute()
        {
            Assert.Equal(403, Post("/pins", Pin, Bearer(guild: "1400000000000000002")));
            Assert.Equal(403, Post("/pins", Pin, Bearer(aud: "http://203.0.113.9:3000")));
            Assert.Equal(403, Get("/settings", Bearer(owner: true, aud: "http://203.0.113.9:3000")));
            Assert.Empty(server.pins);
        }

        // a busy world's JSON layers run to hundreds of KB: seconds on a slow link as written
        [Fact]
        public void ABigJsonLayerGoesGzippedToAClientThatTakesIt()
        {
            Stats.Load(Dir);
            for (int i = 0; i < 40; i++) Stats.Join("Viking number " + i);
            var req = new HttpRequestMessage(HttpMethod.Get, "/stats/players");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            var r = http.Send(req);
            Assert.Contains("gzip", r.Content.Headers.ContentEncoding);
            using var text = new StreamReader(new System.IO.Compression.GZipStream(r.Content.ReadAsStream(), System.IO.Compression.CompressionMode.Decompress));
            Assert.Contains("Viking number 39", text.ReadToEnd());
        }

        // every open map polls /state: built and zipped once while nothing in it moves, and never a write behind
        [Fact]
        public void StateGoesGzippedAndShowsAPinTheMomentItIsPlaced()
        {
            for (int i = 0; i < 20; i++) server.pins.Add($"web,w{i},dot,Someone,{i}.00,0.00,a pin with a few words on it");
            string State()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "/state");
                req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                var r = http.Send(req);
                Assert.Contains("gzip", r.Content.Headers.ContentEncoding);
                using var text = new StreamReader(new System.IO.Compression.GZipStream(r.Content.ReadAsStream(), System.IO.Compression.CompressionMode.Decompress));
                return text.ReadToEnd();
            }
            Assert.DoesNotContain("Gonk", State());
            Assert.Equal(200, Post("/pins", Pin, ("X-Announce-Token", Token), ("X-User", "Gonk")));
            Assert.Contains("Gonk", State());
        }
    }
}
