using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WebMap
{
    // Sign-in through the project's sign-in service: it signs members of the map's Discord
    // server in with its own Discord application and hands the page a session it signed
    // with a private key. The mod holds only the public half, so an operator makes no
    // Discord application and nothing secret lives on the game server.
    internal static class Auth
    {
        internal const string Broker = "https://valheim-proxy.hunterjsb.workers.dev";
        // RSA-2048, "<modulus>.<exponent>" in base64
        internal const string BrokerKey =
            "vXDqIj4PivgOvGg/uNIisLfE2tMp1iF/TtLHn6Y2VwLmygV2LoO/G1P7hUsuRMjJKz+fWPZir8m255XlDdE1Xs1Lzj+a74fenDx6C3Db02Y2qgCHDik2vOKblbEsu1jkHc53PqERO7SO1vcKeimHugtCulJKWhKj1EfjACNVLNunu3pfaXC4PwZ+Y+w7Bww6g1XwCgS59echk38CxyiF+GqXEEHoMI+3yJQ08GVOMaHedRX3cZC4n3aR7M0e7K4rI/XMuHhxuRqSJS8AUj+jKGd2vAG2C/nSyRVgQ/+dGmkYS7iyoMU2A50fxGTYtwLh4akMLJ3IVS9eg6tT8v4WYw==.AQAB";

        internal sealed class Session
        {
            public string Id, Name, Avatar, Perms;
            public bool Owner;
            public List<string> Roles = new List<string>();
        }

        private static readonly Regex TokenShape = new Regex(@"^([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]+)$");
        private static readonly Regex Quoted = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");

        // "<modulus>.<exponent>", base64 either way; null unless a key of 2048 bits or more
        internal static RSAParameters? Key(string text)
        {
            var parts = (text ?? "").Trim().Split('.');
            if (parts.Length != 2) return null;
            byte[] n = B64(parts[0]), e = B64(parts[1]);
            if (n == null || e == null || e.Length == 0) return null;
            int z = 0;
            while (z < n.Length - 1 && n[z] == 0) z++;          // a sign byte some encoders keep
            if (n.Length - z < 256) return null;
            var m = new byte[n.Length - z];
            Array.Copy(n, z, m, 0, m.Length);
            return new RSAParameters { Modulus = m, Exponent = e };
        }

        // The session a token carries, or null when it is forged, expired, for another
        // Discord server or for another map, or issued before signedOutBefore (a sign-out of
        // everyone; a session without its issue time is older than that rule and refused too).
        // origins: the addresses this map answers at.
        internal static Session Verify(string token, string key, string guild, ICollection<string> origins, long now, long signedOutBefore = 0)
        {
            var shape = TokenShape.Match(token ?? "");
            var k = Key(key);
            if (!shape.Success || k == null || string.IsNullOrEmpty(guild)) return null;
            string body = shape.Groups[1].Value, json;
            try
            {
                byte[] sig = B64(shape.Groups[2].Value);
                if (sig == null) return null;
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(k.Value);
                    if (!rsa.VerifyData(Encoding.ASCII.GetBytes(body), CryptoConfig.MapNameToOID("SHA256"), sig)) return null;
                }
                json = Encoding.UTF8.GetString(B64(body));
            }
            catch { return null; }

            if (!(Body.Num(json, "exp") > now)) return null;
            if (!(Body.Num(json, "iat") >= signedOutBefore)) return null;
            if (Body.Str(json, "guild") != guild) return null;
            string aud = Origin(Body.Str(json, "aud"));
            if (aud == null || !origins.Contains(aud)) return null;
            var s = new Session
            {
                Id = Body.Str(json, "id"),
                Name = Body.Str(json, "name") ?? "",
                Avatar = Body.Str(json, "avatar"),
                Perms = Body.Str(json, "perms"),
                Owner = Body.Bool(json, "owner"),
            };
            if (string.IsNullOrEmpty(s.Id)) return null;
            foreach (Match r in Quoted.Matches(Body.Arr(json, "roles") ?? "")) s.Roles.Add(r.Groups[1].Value);
            return s;
        }

        // The server's owner, Administrator (0x8) in their permissions there, or the role
        // the operator named. The bitfield is a decimal string that will outgrow 64 bits;
        // 16 divides 10,000, so its last four digits fix the low four bits at any length.
        internal static bool IsAdmin(bool owner, string perms, IList<string> roles, string adminRole)
        {
            if (owner) return true;
            if (!string.IsNullOrEmpty(perms) && Regex.IsMatch(perms, @"^\d+$")
                && (int.Parse(perms.Length > 4 ? perms.Substring(perms.Length - 4) : perms, CultureInfo.InvariantCulture) & 8) != 0) return true;
            return !string.IsNullOrEmpty(adminRole) && roles != null && roles.Contains(adminRole);
        }

        // scheme://host[:port] of an absolute http(s) address, as a browser writes an origin;
        // null otherwise. One with a user part is refused: http://map@elsewhere is elsewhere.
        internal static string Origin(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri u)) return null;
            if ((u.Scheme != "http" && u.Scheme != "https") || u.UserInfo.Length > 0 || u.Host.Length == 0) return null;
            return u.Scheme + "://" + u.Host.ToLowerInvariant() + (u.IsDefaultPort ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture));
        }

        // The addresses this map answers at: public_url, and the server's public address at
        // the mod's port. Host is anyone's to write, and a session's aud is only as good as
        // the list it is checked against, so the address a visitor asked at counts only on
        // this machine or a LAN -- a private peer asking at a private address. Behind a proxy
        // or a Docker port every peer can look private; the address asked at cannot.
        internal static List<string> Origins(string host, IPAddress peer, string publicUrl, string publicIp, int port)
        {
            var list = new List<string>();
            void Add(string o) { if (o != null && !list.Contains(o)) list.Add(o); }
            Add(Origin(publicUrl));
            if (IPAddress.TryParse(publicIp ?? "", out IPAddress ip))
                Add(Origin("http://" + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString()) + ":" + port.ToString(CultureInfo.InvariantCulture)));
            string asked = string.IsNullOrEmpty(host) ? null : Origin("http://" + host);
            if (asked != null && Private(peer) && Uri.TryCreate(asked, UriKind.Absolute, out Uri u) && Local(u.Host)) Add(asked);
            return list;
        }

        // loopback, 10/8, 172.16/12, 192.168/16, fc00::/7
        internal static bool Private(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
            return a.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xFE) == 0xFC;
        }
        private static bool Local(string host) =>
            host == "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out IPAddress a) && Private(a);

        // Equal, in a time that depends only on the secret's length: how fast a wrong guess
        // is refused says nothing about how much of it was right.
        internal static bool Same(string secret, string given)
        {
            if (string.IsNullOrEmpty(secret) || given == null) return false;
            int diff = secret.Length ^ given.Length;
            for (int i = 0; i < secret.Length; i++) diff |= secret[i] ^ (given.Length > 0 ? given[i % given.Length] : 0);
            return diff == 0;
        }

        // Where a sign-in starts: the service, told which server gates this map and which
        // map the session is for. to must be on this map, so the route is no open redirect.
        internal static string LoginUrl(string broker, string to, string guild, ICollection<string> origins)
        {
            string aud = Origin(to);
            if (aud == null || to.Length > 2000 || !origins.Contains(aud) || string.IsNullOrEmpty(guild)) return null;
            return (string.IsNullOrEmpty(broker) ? Broker : broker).TrimEnd('/') + "/auth/login?to=" + Uri.EscapeDataString(to)
                + "&guild=" + Uri.EscapeDataString(guild) + "&aud=" + Uri.EscapeDataString(aud);
        }

        // base64, url-safe or not, padded or not; null when it is neither
        private static byte[] B64(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
        }
    }
}
