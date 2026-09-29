using System;
using System.Collections.Generic;
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
        private const string Guild = "1400000000000000001", Map = "http://104.224.55.78:3000";
        private static readonly List<string> Here = new List<string> { Map };
        private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static readonly RSA Service = RSA.Create(2048);
        private static string KeyOf(RSA rsa)
        {
            var p = rsa.ExportParameters(false);
            return Convert.ToBase64String(p.Modulus) + "." + Convert.ToBase64String(p.Exponent);
        }
        private static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Sign(RSA rsa, string body) =>
            body + "." + B64u(rsa.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        private static string Body(bool owner = false, string guild = Guild, string aud = Map, long? exp = null) =>
            B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id = "123456789012345678", name = "Rurik \"the Red\"", avatar = "abc", roles = new[] { "111", "222" },
                owner, perms = "0", guild, aud, exp = exp ?? Now + 86400
            })));
        private static Auth.Session Check(string token) => Auth.Verify(token, KeyOf(Service), Guild, Here, Now);

        [Fact]
        public void ASessionTheServiceSignedIsBelieved()
        {
            var s = Check(Sign(Service, Body()));
            Assert.Equal("Rurik \"the Red\"", s.Name);
            Assert.Equal(new[] { "111", "222" }, s.Roles);
            Assert.False(s.Owner);
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
        public void ASessionForAnotherDiscordServerIsRefused() =>
            Assert.Null(Check(Sign(Service, Body(guild: "1400000000000000002"))));

        [Fact]
        public void ASessionForAnotherMapIsRefused()
        {
            Assert.Null(Check(Sign(Service, Body(aud: "http://104.224.55.78:3001"))));
            // the same session is good at public_url when the map is reached there
            var both = Auth.Origins("10.0.0.5:3000", false, "https://map.example.com/");
            Assert.NotNull(Auth.Verify(Sign(Service, Body(aud: "https://map.example.com")), KeyOf(Service), Guild, both, Now));
        }

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
        public void TheServiceAndItsKeyCannotBeSetFromTheSite()
        {
            Assert.NotNull(Settings.Set("auth_public_key", KeyOf(RSA.Create(2048)), "someone", out _, out _));
            Assert.NotNull(Settings.Set("auth_url", "https://evil.example", "someone", out _, out _));
        }

        [Theory]
        [InlineData(Map + "/index.html#at=1,2", true)]
        [InlineData("http://104.224.55.78:3000", true)]
        [InlineData("https://map.example.com/players.html", true)]              // public_url
        [InlineData("http://evil.example/", false)]
        [InlineData("http://104.224.55.78:3000@evil.example/", false)]
        [InlineData("http://104.224.55.78:3001/", false)]
        [InlineData("https://104.224.55.78:3000/", false)]
        [InlineData("//evil.example/", false)]
        [InlineData("javascript:alert(1)", false)]
        public void ASignInReturnsOnlyToThisMap(string to, bool ok)
        {
            var origins = Auth.Origins("104.224.55.78:3000", false, "https://map.example.com");
            string url = Auth.LoginUrl("", to, Guild, origins);
            Assert.Equal(ok, url != null);
            if (ok) Assert.StartsWith(Auth.Broker + "/auth/login?to=" + Uri.EscapeDataString(to) + "&guild=" + Guild + "&aud=", url);
        }
    }
}
