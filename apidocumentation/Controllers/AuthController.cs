using Documentation.Models;
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Http;

namespace Documentation.Controllers
{
    public class AuthController : ApiController
    {
        private static readonly string EndpointsFile = "~/Models/endpoints.json";
        private const int Iterations = 10000;
        private const int HashBytes  = 32;

        [HttpPost]
        [Route("api/auth/resolve")]
        public IHttpActionResult Resolve([FromBody] ResolveRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ApiKey))
                return BadRequest("apiKey is required.");

            // Step 1 — AES-decrypt the opaque token the client submitted
            string plainKey;
            try
            {
                var aesKeyB64 = ConfigurationManager.AppSettings["ApiCipherKey"];
                plainKey = DecryptToken(req.ApiKey.Trim(), aesKeyB64);
            }
            catch
            {
                return Unauthorized(); // malformed / wrong key → same response as invalid
            }

            // Step 2 — PBKDF2-verify against stored hashes
            var path = HttpContext.Current.Server.MapPath(EndpointsFile);
            if (!File.Exists(path))
                return InternalServerError(new Exception("Endpoint configuration not found."));

            EndpointsConfig config;
            try { config = JsonConvert.DeserializeObject<EndpointsConfig>(File.ReadAllText(path)); }
            catch { return InternalServerError(new Exception("Endpoint configuration is malformed.")); }

            if (config?.Endpoints == null)
                return InternalServerError(new Exception("Endpoint configuration is empty."));

            var entry = config.Endpoints.FirstOrDefault(e => VerifyKey(plainKey, e.KeyHash));

            if (entry == null)
                return Unauthorized();

            return Ok(new {
                id             = entry.Id,
                name           = entry.Name,
                baseUrl        = entry.BaseUrl,
                hiddenServices = entry.HiddenServices ?? new System.Collections.Generic.List<string>()
            });
        }

        // AES-256-CBC decrypt; token = Base64(IV[16] + ciphertext)
        private static string DecryptToken(string token, string aesKeyB64)
        {
            var all     = Convert.FromBase64String(token);
            var iv      = all.Take(16).ToArray();
            var cipher  = all.Skip(16).ToArray();
            var keyBytes = Convert.FromBase64String(aesKeyB64);

            using (var aes = Aes.Create())
            {
                aes.Key     = keyBytes;
                aes.IV      = iv;
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var dec = aes.CreateDecryptor())
                using (var ms  = new MemoryStream(cipher))
                using (var cs  = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                using (var sr  = new StreamReader(cs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
        }

        private static bool VerifyKey(string plain, string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash)) return false;
            var parts = storedHash.Split(':');
            if (parts.Length != 2) return false;

            byte[] salt, expected;
            try
            {
                salt     = Convert.FromBase64String(parts[0]);
                expected = Convert.FromBase64String(parts[1]);
            }
            catch { return false; }

            using (var pbkdf2 = new Rfc2898DeriveBytes(plain, salt, Iterations))
            {
                var computed = pbkdf2.GetBytes(HashBytes);
                return CryptographicEquals(computed, expected);
            }
        }

        private static bool CryptographicEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // Public endpoint — returns current hiddenServices for an endpoint ID (no auth required)
        [HttpGet]
        [Route("api/endpoints/{id}/visibility")]
        public IHttpActionResult GetEndpointVisibility(string id)
        {
            var path = HttpContext.Current.Server.MapPath("~/Models/endpoints.json");
            if (!File.Exists(path))
                return Ok(new { hiddenServices = new System.Collections.Generic.List<string>() });

            EndpointsConfig config;
            try { config = JsonConvert.DeserializeObject<EndpointsConfig>(File.ReadAllText(path)); }
            catch { return Ok(new { hiddenServices = new System.Collections.Generic.List<string>() }); }

            var entry = config?.Endpoints?.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

            return Ok(new { hiddenServices = entry?.HiddenServices ?? new System.Collections.Generic.List<string>() });
        }
    }

    public class ResolveRequest { public string ApiKey { get; set; } }
}
