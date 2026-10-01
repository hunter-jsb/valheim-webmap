using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebMap;
using Xunit;

namespace WebMap.Tests
{
    // Sessions as the sign-in service signs them, from a key pair made here: the mod
    // must believe only its own server's members, on its own map, until they expire.
    public class AuthTests
    {
        internal const string Guild = "1400000000000000001", Map = "http://104.224.55.78:3000";
        private static readonly List<string> Here = new List<string> { Map };
        private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        internal static readonly RSA Service = RSA.Create(2048);
        internal static string KeyOf(RSA rsa)
        {
            var p = rsa.ExportParameters(false);
            return Convert.ToBase64String(p.Modulus) + "." + Convert.ToBase64String(p.Exponent);
        }
        private static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        internal static string Sign(RSA rsa, string body) =>
            body + "." + B64u(rsa.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        internal static string Body(bool owner = false, string guild = Guild, string aud = Map, long? exp = null, long? iat = -60)
        {
            var b = new Dictionary<string, object>
            {
                ["id"] = "123456789012345678", ["name"] = "Rurik \"the Red\"", ["avatar"] = "abc", ["roles"] = new[] { "111", "222" },
                ["owner"] = owner, ["perms"] = "0", ["guild"] = guild, ["aud"] = aud, ["exp"] = exp ?? Now + 86400,
            };
            if (iat != null) b["iat"] = iat < 0 ? Now + iat : iat;
            return B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(b)));
        }
        private static Auth.Session Check(string token, long signedOutBefore = 0) =>
            Auth.Verify(token, KeyOf(Service), Guild, Here, Now, signedOutBefore);

        [Fact]
        public void ASessionTheServiceSignedIsBelieved()
        {
            var s = Check(Sign(Service, Body()));
            Assert.Equal("Rurik \"the Red\"", s.Name);
            Assert.Equal(new[] { "111", "222" }, s.Roles);
            Assert.False(s.Owner);
        }

        // signed by WebCrypto exactly as the Worker signs, under a throwaway key
        [Fact]
        public void ASessionTheWorkerSignedVerifiesHere()
        {
            const string key = "nMpS+295wg8eAhs3lEq2oh2Yv/9eI9puxC+1fyrZfsKZqDhA8J8m8FPSBcQfhpCHdCtx5p5s4LjcCHoVRN9uj8fhl9zQKjTLYTomOKL+FP41ORBoxG9VFAPm0CsclZjs7dN/EpAU6I88/9LgltCDSZznRhhx/+ojK6ez/x5ev9muNtRSgc2WI/s6OpV/fX/XMvfHlfwCLj2cWENlp01nAo7wELkTnJCp8R3URQw4aKdt/2hIx8Ciq9JtkVyU7iSZhLMzd4O5vnm6Fu2yp9nXFKflpRp6xBsiLXfT2xmNQm7s/6QYeSPsT4EznmVuB9RqrI9D+FTcmxvW8KXyEiO1Qw==.AQAB";
            const string token = "eyJpZCI6IjQyIiwibmFtZSI6IkdvbmsgdGhlIELDuGxkIiwiYXZhdGFyIjoiYWJjIiwicm9sZXMiOlsiOTAwIl0sIm93bmVyIjpmYWxzZSwicGVybXMiOiIyMjUxNzk5ODEzNjg1MjQ3IiwiZ3VpbGQiOiIxMTExMTExMTExMTExMTExMTEiLCJhdWQiOiJodHRwOi8vbWFwLmV4YW1wbGU6MzAwMCIsImlhdCI6MTc5MDcyMDAwMCwiZXhwIjo0MTAyNDQ0ODAwfQ.OvJjjcS7LZR9JGf5WyqeLyQTSlQKY_BGBiMLuXm-vGzyvDzSOe50EJxpuv5MvWZ78Vw7wjTzMvP0agzoij7wegZlyNVBWGMYhufCIu_nO1b1QSStvLIrqS-WShrzg0HlGcdr9GbC3dAfhzWNUnr278tBVtS_TOD5qcmjLPBBaP1V2iGvjT_Chr-GesbampQNz0F7Ais17y_XAkoFjbPnThBxba9X_upu0l-hIVXxilu0-MZJwTxBJ7pj1hDQfqnz8eq9VNSz_xaoq95kKG5evaJdOPBlwsmei8VBkcteVcaWHIQ9RBANEExsfk2gM8yfcDpsRzz22KaGJyV9k0YYEg";
            var s = Auth.Verify(token, key, "111111111111111111", new List<string> { "http://map.example:3000" }, Now, 1790720000);
            Assert.NotNull(s);
            Assert.Equal("42", s.Id);
            Assert.Equal("Gonk the Bøld", s.Name);
            Assert.Equal(new[] { "900" }, s.Roles);
            Assert.Null(Auth.Verify(token, key, "111111111111111112", new List<string> { "http://map.example:3000" }, Now));
            Assert.Null(Auth.Verify(token, key, "111111111111111111", new List<string> { "http://map.example:3001" }, Now));
        }

        [Fact]
        public void ABodyAlteredAfterSigningIsRefused()
        {
            string token = Sign(Service, Body()), forged = Body(owner: true);
            Assert.Null(Check(forged + token.Substring(token.IndexOf('.'))));
        }

        [Fact]
        public void ASessionSignedWithAnotherKeyIsRefused() =>
            Assert.Null(Check(Sign(RSA.Create(2048), Body())));

        [Fact]
        public void AnExpiredSessionIsRefused() =>
            Assert.Null(Check(Sign(Service, Body(exp: Now - 1))));

        [Fact]
        public void SessionsFromBeforeASignOutOfEveryoneAreRefused()
        {
            Assert.Null(Check(Sign(Service, Body(iat: -60)), Now - 30));
            Assert.NotNull(Check(Sign(Service, Body(iat: -10)), Now - 30));
            Assert.Null(Check(Sign(Service, Body(iat: null))));              // from before sessions carried the time
        }

        [Fact]
        public void ASessionForAnotherDiscordServerIsRefused() =>
            Assert.Null(Check(Sign(Service, Body(guild: "1400000000000000002"))));

        [Fact]
        public void ASessionForAnotherMapIsRefused() =>
            Assert.Null(Check(Sign(Service, Body(aud: "http://104.224.55.78:3001"))));

        [Fact]
        public void TheMapsOwnAddressesArePublicUrlAndThePublicAddressAtItsPort() =>
            Assert.Equal(new[] { "https://map.example.com", "http://104.224.55.78:3000" },
                         Auth.Origins("evil.example:3000", IPAddress.Parse("203.0.113.9"), "https://map.example.com/x", "104.224.55.78", 3000));

        // Host is the caller's to write: it counts only from this machine or a LAN, asked at
        // a private address -- never from outside, nor through a proxy or a Docker gateway
        [Theory]
        [InlineData("203.0.113.9", "evil.example:3000", "")]
        [InlineData("172.17.0.1", "evil.example:3000", "")]
        [InlineData("203.0.113.9", "192.168.1.10:3000", "")]
        [InlineData("127.0.0.1", "127.0.0.1:3000", "http://127.0.0.1:3000")]
        [InlineData("192.168.1.20", "192.168.1.10:3000", "http://192.168.1.10:3000")]
        [InlineData("::ffff:10.0.0.7", "localhost:3000", "http://localhost:3000")]
        [InlineData("fd00::5", "[fd00::1]:3000", "http://[fd00::1]:3000")]
        public void TheAddressAskedAtCountsOnlyOnALan(string peer, string host, string asked) =>
            Assert.Equal(asked.Length == 0 ? new string[0] : new[] { asked }, Auth.Origins(host, IPAddress.Parse(peer), "", "", 3000));

        // just started, the game not yet knowing its public address: not yet, rather than off
        [Fact]
        public void SignInOnButNoAddressYetIsNotYetNotOff() =>
            Assert.Equal((503, 404, 404), (Auth.Closed(Guild, null), Auth.Closed("", null), Auth.Closed(Guild, "104.224.55.78")));

        [Theory]
        [InlineData(true, "0", "", true)]                                        // the server's owner
        [InlineData(false, "8", "", true)]                                       // Administrator
        [InlineData(false, "2147483639", "", false)]                             // every bit but 0x8
        [InlineData(false, "1180591620717411303432", "", true)]                  // 2^70 + 8: past 64 bits
        [InlineData(false, "1180591620717411303424", "", false)]
        [InlineData(false, "0", "222", true)]                                    // the role the operator named
        [InlineData(false, "0", "333", false)]
        public void AdminsAreTheOwnerAdministratorsAndTheNamedRole(bool owner, string perms, string role, bool admin) =>
            Assert.Equal(admin, Auth.IsAdmin(owner, perms, new[] { "111", "222" }, role));

        [Fact]
        public void TheAnnounceTokenMatchesOnlyItself()
        {
            Assert.True(Auth.Same("s3cret-token", "s3cret-token"));
            foreach (string guess in new[] { "", "s3cret-toke", "s3cret-token!", "S3cret-token", "s3cret-tokens3cret-token" })
                Assert.False(Auth.Same("s3cret-token", guess));
            Assert.False(Auth.Same(null, ""));
        }

        [Fact]
        public void TheServiceAndItsKeyCannotBeSetFromTheSite()
        {
            Assert.NotNull(Settings.Set("auth_public_key", KeyOf(RSA.Create(2048)), "someone", out _, out _));
            Assert.NotNull(Settings.Set("auth_url", "https://evil.example", "someone", out _, out _));
        }

        // a host that rewrites the BepInEx config on restart leaves only the site's own file
        [Fact]
        public void PublicUrlAndASignOutHoldFromTheSitesFileAlone()
        {
            string d = Directory.CreateTempSubdirectory("webmap-auth-").FullName;
            try
            {
                File.WriteAllText(Path.Combine(d, "settings.tsv"), "public_url\thttps://map.example.com\tadmin\t1\nsign_out_before\t1790000000\tadmin\t1\n");
                Settings.ResetForTests(d);
                Assert.Equal("https://map.example.com", WebMapConfig.PUBLIC_URL);
                Assert.Equal(1790000000L, WebMapConfig.SIGN_OUT_BEFORE);
                Assert.Null(Settings.Set("sign_out_before", "now", "admin", out _, out _));    // by the server's clock
                Assert.InRange(WebMapConfig.SIGN_OUT_BEFORE, Now, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            finally { WebMapConfig.PUBLIC_URL = ""; WebMapConfig.SIGN_OUT_BEFORE = 0; Directory.Delete(d, true); }
        }

        [Theory]
        [InlineData(Map + "/index.html#at=1,2", true)]
        [InlineData("http://104.224.55.78:3000", true)]
        [InlineData("https://map.example.com/players.html", true)]              // public_url
        [InlineData("http://evil.example:3000/", false)]                         // what Host said
        [InlineData("http://evil.example/", false)]
        [InlineData("http://104.224.55.78:3000@evil.example/", false)]
        [InlineData("http://104.224.55.78:3001/", false)]
        [InlineData("https://104.224.55.78:3000/", false)]
        [InlineData("//evil.example/", false)]
        [InlineData("javascript:alert(1)", false)]
        public void ASignInReturnsOnlyToThisMap(string to, bool ok)
        {
            var origins = Auth.Origins("evil.example:3000", IPAddress.Parse("203.0.113.9"), "https://map.example.com", "104.224.55.78", 3000);
            string url = Auth.LoginUrl("", to, Guild, origins);
            Assert.Equal(ok, url != null);
            if (ok) Assert.StartsWith(Auth.Broker + "/auth/login?to=" + Uri.EscapeDataString(to) + "&guild=" + Guild + "&aud=", url);
        }
    }
}
