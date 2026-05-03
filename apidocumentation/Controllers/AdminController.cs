using Documentation.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Http;

namespace Documentation.Controllers
{
    public class AdminController : ApiController
    {
        private static readonly string EndpointsFile = "~/Models/endpoints.json";
        private static readonly ConcurrentDictionary<string, DateTime> Sessions =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private const int Iterations = 10000;
        private const int HashBytes  = 32;

        [HttpPost]
        [Route("api/admin/login")]
        public IHttpActionResult Login([FromBody] AdminLoginRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest("username and password are required.");

            var expectedUser = ConfigurationManager.AppSettings["AdminUsername"] ?? string.Empty;
            var expectedHash = ConfigurationManager.AppSettings["AdminPasswordHash"] ?? string.Empty;

            if (!string.Equals(req.Username.Trim(), expectedUser, StringComparison.Ordinal))
                return Unauthorized();

            if (!VerifyPbkdf2(req.Password, expectedHash))
                return Unauthorized();

            var token = GenerateToken();
            Sessions[token] = DateTime.UtcNow.AddHours(8);
            return Ok(new { token });
        }

        [HttpPost]
        [Route("api/admin/logout")]
        public IHttpActionResult Logout()
        {
            IEnumerable<string> vals;
            if (Request.Headers.TryGetValues("X-Admin-Token", out vals))
            {
                var tok = vals.FirstOrDefault();
                DateTime ignored;
                if (tok != null) Sessions.TryRemove(tok, out ignored);
            }
            return Ok();
        }

        [HttpGet]
        [Route("api/admin/endpoints")]
        public IHttpActionResult GetEndpoints()
        {
            if (!IsAuthenticated()) return Unauthorized();
            var config = LoadConfig();
            var list = config?.Endpoints ?? new EndpointEntry[0];
            return Ok(list.Select(e => new { e.Id, e.Name, e.BaseUrl, e.EncodedKey }));
        }

        [HttpPost]
        [Route("api/admin/endpoints")]
        public IHttpActionResult CreateEndpoint([FromBody] CreateEndpointRequest req)
        {
            if (!IsAuthenticated()) return Unauthorized();

            if (req == null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.BaseUrl))
                return BadRequest("name and baseUrl are required.");

            var baseUrl = req.BaseUrl.Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return BadRequest("baseUrl must be a valid http/https URL.");

            var aesKey   = ConfigurationManager.AppSettings["ApiCipherKey"];
            var plainKey = GeneratePlainKey();
            var keyHash  = CreatePbkdf2Hash(plainKey);
            var encoded  = EncryptToken(plainKey, aesKey);

            var entry = new EndpointEntry
            {
                Id         = Guid.NewGuid().ToString("N"),
                Name       = req.Name.Trim(),
                BaseUrl    = baseUrl,
                KeyHash    = keyHash,
                EncodedKey = encoded
            };

            var config = LoadConfig() ?? new EndpointsConfig { Endpoints = new EndpointEntry[0] };
            var list = (config.Endpoints ?? new EndpointEntry[0]).ToList();
            list.Add(entry);
            config.Endpoints = list.ToArray();
            SaveConfig(config);

            return Ok(new { entry.Id, entry.Name, entry.BaseUrl, entry.EncodedKey });
        }

        [HttpDelete]
        [Route("api/admin/endpoints/{id}")]
        public IHttpActionResult DeleteEndpoint(string id)
        {
            if (!IsAuthenticated()) return Unauthorized();

            var config = LoadConfig();
            if (config?.Endpoints == null) return NotFound();

            var list    = config.Endpoints.ToList();
            var removed = list.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();

            config.Endpoints = list.ToArray();
            SaveConfig(config);
            return Ok();
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private bool IsAuthenticated()
        {
            IEnumerable<string> vals;
            if (!Request.Headers.TryGetValues("X-Admin-Token", out vals)) return false;
            var token = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(token)) return false;
            DateTime expiry;
            if (!Sessions.TryGetValue(token, out expiry)) return false;
            if (DateTime.UtcNow > expiry) { DateTime ignored; Sessions.TryRemove(token, out ignored); return false; }
            return true;
        }

        private static string GenerateToken()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Convert.ToBase64String(b);
        }

        private static string GeneratePlainKey()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return new string(b.Select(x => chars[x % chars.Length]).ToArray());
        }

        private static string CreatePbkdf2Hash(string plain)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            using (var pb = new Rfc2898DeriveBytes(plain, salt, Iterations))
            {
                var hash = pb.GetBytes(HashBytes);
                return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
            }
        }

        private static bool VerifyPbkdf2(string input, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            var parts = stored.Split(':');
            if (parts.Length != 2) return false;
            byte[] salt, expected;
            try { salt = Convert.FromBase64String(parts[0]); expected = Convert.FromBase64String(parts[1]); }
            catch { return false; }
            using (var pb = new Rfc2898DeriveBytes(input, salt, Iterations))
            {
                var computed = pb.GetBytes(HashBytes);
                int diff = 0;
                for (int i = 0; i < computed.Length; i++) diff |= computed[i] ^ expected[i];
                return diff == 0;
            }
        }

        private static string EncryptToken(string plain, string aesKeyB64)
        {
            var kb = Convert.FromBase64String(aesKeyB64);
            using (var aes = Aes.Create())
            {
                aes.Key = kb; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                var enc = aes.CreateEncryptor();
                var pb  = Encoding.UTF8.GetBytes(plain);
                var cb  = enc.TransformFinalBlock(pb, 0, pb.Length);
                return Convert.ToBase64String(aes.IV.Concat(cb).ToArray());
            }
        }

        private EndpointsConfig LoadConfig()
        {
            var path = HttpContext.Current.Server.MapPath(EndpointsFile);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<EndpointsConfig>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private void SaveConfig(EndpointsConfig config)
        {
            var path = HttpContext.Current.Server.MapPath(EndpointsFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }

    public class AdminLoginRequest    { public string Username { get; set; } public string Password { get; set; } }
    public class CreateEndpointRequest { public string Name { get; set; } public string BaseUrl { get; set; } }
}
