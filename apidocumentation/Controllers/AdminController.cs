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
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Http;

namespace Documentation.Controllers
{
    public class AdminController : ApiController
    {
        private static readonly string EndpointsFile = "~/Models/endpoints.json";
        private static readonly string UsersFile      = "~/Models/users.json";

        private class AdminSession
        {
            public DateTime Expiry      { get; set; }
            public bool     IsSuperAdmin { get; set; }
            public string   Email       { get; set; }
            public string   UserId      { get; set; }
        }

        private static readonly ConcurrentDictionary<string, AdminSession> Sessions =
            new ConcurrentDictionary<string, AdminSession>(StringComparer.Ordinal);

        private const int Iterations = 10000;
        private const int HashBytes  = 32;

        // ── Auth ─────────────────────────────────────────────────────────────────

        [HttpPost]
        [Route("api/admin/login")]
        public IHttpActionResult Login([FromBody] AdminLoginRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest("username and password are required.");

            var expectedUser = ConfigurationManager.AppSettings["AdminUsername"] ?? string.Empty;
            var expectedHash = ConfigurationManager.AppSettings["AdminPasswordHash"] ?? string.Empty;

            // Check cyberhub super-admin credentials
            if (string.Equals(req.Username.Trim(), expectedUser, StringComparison.Ordinal) &&
                VerifyPbkdf2(req.Password, expectedHash))
            {
                var token = GenerateToken();
                Sessions[token] = new AdminSession { Expiry = DateTime.UtcNow.AddHours(8), IsSuperAdmin = true };
                return Ok(new { token, mustChangePassword = false, isSuperAdmin = true, email = (string)null });
            }

            // Check registered @cyberlogic.gr users
            var users = LoadUsers();
            if (users != null)
            {
                var user = users.Users.FirstOrDefault(u =>
                    string.Equals(u.Email, req.Username.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    u.Status == "active" &&
                    !string.IsNullOrEmpty(u.PasswordHash));

                if (user != null && VerifyPbkdf2(req.Password, user.PasswordHash))
                {
                    var token = GenerateToken();
                    Sessions[token] = new AdminSession
                    {
                        Expiry       = DateTime.UtcNow.AddHours(8),
                        IsSuperAdmin = false,
                        Email        = user.Email,
                        UserId       = user.Id
                    };
                    return Ok(new { token, mustChangePassword = user.MustChangePassword, isSuperAdmin = false, email = user.Email });
                }
            }

            return Unauthorized();
        }

        [HttpPost]
        [Route("api/admin/logout")]
        public IHttpActionResult Logout()
        {
            IEnumerable<string> vals;
            if (Request.Headers.TryGetValues("X-Admin-Token", out vals))
            {
                var tok = vals.FirstOrDefault();
                AdminSession ignored;
                if (tok != null) Sessions.TryRemove(tok, out ignored);
            }
            return Ok();
        }

        [HttpPost]
        [Route("api/admin/change-password")]
        public IHttpActionResult ChangePassword([FromBody] ChangePasswordRequest req)
        {
            AdminSession session;
            if (!TryGetSession(out session)) return Unauthorized();
            if (session.IsSuperAdmin) return BadRequest("Super-admin password is managed via server configuration.");
            if (string.IsNullOrEmpty(session.UserId)) return Unauthorized();

            if (req == null || string.IsNullOrWhiteSpace(req.NewPassword))
                return BadRequest("New password is required.");
            if (req.NewPassword.Length < 8)
                return BadRequest("Password must be at least 8 characters.");

            var data = LoadUsers();
            var user = data?.Users.FirstOrDefault(u => string.Equals(u.Id, session.UserId, StringComparison.Ordinal));
            if (user == null) return NotFound();

            user.PasswordHash      = CreatePbkdf2Hash(req.NewPassword);
            user.MustChangePassword = false;
            SaveUsers(data);
            return Ok(new { message = "Password changed successfully." });
        }

        // ── Endpoints ────────────────────────────────────────────────────────────

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

        // ── User Management (super-admin only) ────────────────────────────────────

        [HttpGet]
        [Route("api/admin/users")]
        public IHttpActionResult GetAdminUsers()
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers() ?? new UsersData();
            return Ok(data.Users.Select(u => new
            {
                id                = u.Id,
                email             = u.Email,
                status            = u.Status,
                mustChangePassword = u.MustChangePassword,
                createdAt         = u.CreatedAt,
                approvedAt        = u.ApprovedAt
            }));
        }

        [HttpPost]
        [Route("api/admin/users/{id}/approve")]
        public IHttpActionResult ApproveUser(string id)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers();
            if (data == null) return NotFound();
            var user = data.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (user == null) return NotFound();

            var tempPassword = GeneratePassword();
            user.PasswordHash       = CreatePbkdf2Hash(tempPassword);
            user.Status             = "active";
            user.MustChangePassword = true;
            user.ApprovedAt         = DateTime.UtcNow;
            SaveUsers(data);
            return Ok(new { email = user.Email, tempPassword });
        }

        [HttpDelete]
        [Route("api/admin/users/{id}")]
        public IHttpActionResult DeleteUser(string id)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers();
            if (data == null) return NotFound();
            var removed = data.Users.RemoveAll(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (removed == 0) return NotFound();
            SaveUsers(data);
            return Ok();
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private bool TryGetSession(out AdminSession session)
        {
            session = null;
            IEnumerable<string> vals;
            if (!Request.Headers.TryGetValues("X-Admin-Token", out vals)) return false;
            var token = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(token)) return false;
            AdminSession s;
            if (!Sessions.TryGetValue(token, out s)) return false;
            if (DateTime.UtcNow > s.Expiry) { Sessions.TryRemove(token, out s); return false; }
            session = s;
            return true;
        }

        private bool IsAuthenticated()
        {
            AdminSession s;
            return TryGetSession(out s);
        }

        private bool IsSuperAdmin()
        {
            AdminSession s;
            return TryGetSession(out s) && s.IsSuperAdmin;
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

        private static string GeneratePassword()
        {
            const string lower   = "abcdefghijkmnpqrstuvwxyz";
            const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string digits  = "23456789";
            const string special = "@#$!";
            var all = lower + upper + digits + special;
            var b = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var chars = new char[12];
            chars[0] = upper[b[0]   % upper.Length];
            chars[1] = lower[b[1]   % lower.Length];
            chars[2] = digits[b[2]  % digits.Length];
            chars[3] = special[b[3] % special.Length];
            for (int i = 4; i < 12; i++) chars[i] = all[b[i] % all.Length];
            var rb = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rb);
            for (int i = 11; i > 0; i--) { var j = rb[i] % (i + 1); var tmp = chars[i]; chars[i] = chars[j]; chars[j] = tmp; }
            return new string(chars);
        }

        internal static string CreatePbkdf2Hash(string plain)
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

        private UsersData LoadUsers()
        {
            var path = HttpContext.Current.Server.MapPath(UsersFile);
            if (!File.Exists(path)) return new UsersData();
            try { return JsonConvert.DeserializeObject<UsersData>(File.ReadAllText(path)) ?? new UsersData(); }
            catch { return new UsersData(); }
        }

        private void SaveUsers(UsersData data)
        {
            var path = HttpContext.Current.Server.MapPath(UsersFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        // ── Content Sections ─────────────────────────────────────────────────────

        private static readonly string ContentFile   = "~/Models/content.json";
        private static readonly string FieldDescFile = "~/Models/fieldDescriptions.json";
        private static readonly string ServicesFile  = "~/Models/webservicesUpdated.json";
        private static readonly string XsdBase       = "~/Models/XSDs/XSDs/";

        [HttpGet]
        [Route("api/admin/content/sections")]
        public IHttpActionResult GetSections()
        {
            if (!IsAuthenticated()) return Unauthorized();
            var data = LoadContent();
            return Ok(data?.Sections ?? new List<ContentSection>());
        }

        [HttpPost]
        [Route("api/admin/content/sections")]
        public IHttpActionResult CreateSection([FromBody] ContentSection section)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (section == null || string.IsNullOrWhiteSpace(section.Title))
                return BadRequest("title is required.");
            if (string.IsNullOrWhiteSpace(section.Id))
                section.Id = Regex.Replace(section.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            var data = LoadContent() ?? new ContentData();
            if (data.Sections.Any(s => string.Equals(s.Id, section.Id, StringComparison.OrdinalIgnoreCase)))
                section.Id += "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            data.Sections.Add(section);
            SaveContent(data);
            return Ok(section);
        }

        [HttpPut]
        [Route("api/admin/content/sections/{id}")]
        public IHttpActionResult UpdateSection(string id, [FromBody] ContentSection update)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (update == null) return BadRequest("body is required.");
            var data = LoadContent();
            if (data == null) return NotFound();
            var sec = data.Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (sec == null) return NotFound();
            if (!string.IsNullOrWhiteSpace(update.Title)) sec.Title = update.Title;
            if (update.Items != null) sec.Items = update.Items;
            SaveContent(data);
            return Ok(sec);
        }

        [HttpDelete]
        [Route("api/admin/content/sections/{id}")]
        public IHttpActionResult DeleteSection(string id)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var data = LoadContent();
            if (data == null) return NotFound();
            var removed = data.Sections.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();
            SaveContent(data);
            return Ok();
        }

        // ── Field Descriptions ────────────────────────────────────────────────────

        [HttpGet]
        [Route("api/admin/content/fields")]
        public IHttpActionResult GetFields()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(LoadFields() ?? new Dictionary<string, string>());
        }

        [HttpPost]
        [Route("api/admin/content/fields")]
        public IHttpActionResult CreateField([FromBody] FieldEntry req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return BadRequest("name is required.");
            var fields = LoadFields() ?? new Dictionary<string, string>();
            fields[req.Name.Trim()] = req.Description ?? string.Empty;
            SaveFields(fields);
            return Ok();
        }

        [HttpPut]
        [Route("api/admin/content/fields/{name}")]
        public IHttpActionResult UpdateField(string name, [FromBody] FieldEntry req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var fields = LoadFields();
            if (fields == null || !fields.ContainsKey(name)) return NotFound();
            fields[name] = req?.Description ?? string.Empty;
            SaveFields(fields);
            return Ok();
        }

        [HttpDelete]
        [Route("api/admin/content/fields/{name}")]
        public IHttpActionResult DeleteField(string name)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var fields = LoadFields();
            if (fields == null || !fields.ContainsKey(name)) return NotFound();
            fields.Remove(name);
            SaveFields(fields);
            return Ok();
        }

        // ── Services ──────────────────────────────────────────────────────────────

        [HttpGet]
        [Route("api/admin/services")]
        public IHttpActionResult GetAllServices()
        {
            if (!IsAuthenticated()) return Unauthorized();
            var catalog = LoadServiceCatalog();
            return Ok(catalog?.ServiceCategory ?? new List<ServiceCategoryModel>());
        }

        [HttpPost]
        [Route("api/admin/services/{href}/toggle-visibility")]
        public IHttpActionResult ToggleServiceVisibility(string href)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var catalog = LoadServiceCatalog();
            if (catalog == null) return NotFound();
            foreach (var cat in catalog.ServiceCategory)
            {
                var svc = cat.Services.FirstOrDefault(s =>
                    string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
                if (svc != null)
                {
                    svc.Hidden = !svc.Hidden;
                    SaveServiceCatalog(catalog);
                    return Ok(new { href = svc.Href, hidden = svc.Hidden });
                }
            }
            return NotFound();
        }

        [HttpPost]
        [Route("api/admin/services")]
        public IHttpActionResult CreateService([FromBody] CreateServiceRequest req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.ServiceName) ||
                string.IsNullOrWhiteSpace(req.CategoryName) || string.IsNullOrWhiteSpace(req.Href))
                return BadRequest("serviceName, categoryName, and href are required.");

            string xsdRequestPath = null, xsdSchemaPath = null;

            if (!string.IsNullOrWhiteSpace(req.XsdRequestContent))
            {
                var fname = SanitizeFilename(req.Href) + "Request.xsd";
                var relPath = "Requests/Custom/" + fname;
                var fullPath = Path.Combine(HttpContext.Current.Server.MapPath(XsdBase), "Requests\\Custom\\" + fname);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, req.XsdRequestContent, Encoding.UTF8);
                xsdRequestPath = relPath;
            }

            if (!string.IsNullOrWhiteSpace(req.XsdSchemaContent))
            {
                var fname = SanitizeFilename(req.Href) + "Response.xsd";
                var relPath = "Responses/Custom/" + fname;
                var fullPath = Path.Combine(HttpContext.Current.Server.MapPath(XsdBase), "Responses\\Custom\\" + fname);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, req.XsdSchemaContent, Encoding.UTF8);
                xsdSchemaPath = relPath;
            }

            var newService = new ServiceEntryModel
            {
                Service     = req.ServiceName.Trim(),
                Href        = req.Href.Trim(),
                Description = req.Description ?? string.Empty,
                Link        = req.Link ?? string.Empty,
                XsdRequest  = xsdRequestPath ?? string.Empty,
                XsdSchema   = xsdSchemaPath  ?? string.Empty,
                Hidden      = false
            };

            var catalog = LoadServiceCatalog() ?? new ServiceCatalogModel { ServiceCategory = new List<ServiceCategoryModel>() };
            var category = catalog.ServiceCategory.FirstOrDefault(c =>
                string.Equals(c.Name, req.CategoryName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                category = new ServiceCategoryModel { Name = req.CategoryName.Trim(), Services = new List<ServiceEntryModel>() };
                catalog.ServiceCategory.Add(category);
            }
            category.Services.Add(newService);
            SaveServiceCatalog(catalog);
            return Ok(newService);
        }

        [HttpDelete]
        [Route("api/admin/services/{href}")]
        public IHttpActionResult DeleteService(string href)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var catalog = LoadServiceCatalog();
            if (catalog == null) return NotFound();
            bool found = false;
            foreach (var cat in catalog.ServiceCategory.ToList())
            {
                var rem = cat.Services.RemoveAll(s => string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
                if (rem > 0) found = true;
                if (cat.Services.Count == 0) catalog.ServiceCategory.Remove(cat);
            }
            if (!found) return NotFound();
            SaveServiceCatalog(catalog);
            return Ok();
        }

        // ── Content helpers ───────────────────────────────────────────────────────

        private ContentData LoadContent()
        {
            var path = HttpContext.Current.Server.MapPath(ContentFile);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<ContentData>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private void SaveContent(ContentData data)
        {
            var path = HttpContext.Current.Server.MapPath(ContentFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private Dictionary<string, string> LoadFields()
        {
            var path = HttpContext.Current.Server.MapPath(FieldDescFile);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private void SaveFields(Dictionary<string, string> fields)
        {
            var path = HttpContext.Current.Server.MapPath(FieldDescFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(fields, Formatting.Indented));
        }

        private ServiceCatalogModel LoadServiceCatalog()
        {
            var path = HttpContext.Current.Server.MapPath(ServicesFile);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<ServiceCatalogModel>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private void SaveServiceCatalog(ServiceCatalogModel catalog)
        {
            var path = HttpContext.Current.Server.MapPath(ServicesFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(catalog, Formatting.Indented));
        }

        private static string SanitizeFilename(string name)
        {
            return Regex.Replace(name, @"[^a-zA-Z0-9_-]", "_");
        }
    }

    // ── Request / response models ─────────────────────────────────────────────────

    public class AdminLoginRequest      { public string Username    { get; set; } public string Password    { get; set; } }
    public class CreateEndpointRequest  { public string Name        { get; set; } public string BaseUrl     { get; set; } }
    public class ChangePasswordRequest  { public string NewPassword { get; set; } }

    public class ContentData
    {
        [JsonProperty("sections")]
        public List<ContentSection> Sections { get; set; } = new List<ContentSection>();
    }

    public class ContentSection
    {
        [JsonProperty("id")]    public string Id    { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("items")] public List<ContentItem> Items { get; set; } = new List<ContentItem>();
    }

    public class ContentItem
    {
        [JsonProperty("type")]     public string Type     { get; set; }
        [JsonProperty("text")]     public string Text     { get; set; }
        [JsonProperty("language")] public string Language { get; set; }
    }

    public class FieldEntry
    {
        public string Name        { get; set; }
        public string Description { get; set; }
    }

    public class ServiceCatalogModel
    {
        [JsonProperty("serviceCategory")]
        public List<ServiceCategoryModel> ServiceCategory { get; set; } = new List<ServiceCategoryModel>();
    }

    public class ServiceCategoryModel
    {
        [JsonProperty("name")]     public string Name { get; set; }
        [JsonProperty("services")] public List<ServiceEntryModel> Services { get; set; } = new List<ServiceEntryModel>();
    }

    public class ServiceEntryModel
    {
        [JsonProperty("service")]     public string Service     { get; set; }
        [JsonProperty("href")]        public string Href        { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("link")]        public string Link        { get; set; }
        [JsonProperty("xsdRequest")]  public string XsdRequest  { get; set; }
        [JsonProperty("xsdSchema")]   public string XsdSchema   { get; set; }
        [JsonProperty("hidden")]      public bool   Hidden      { get; set; }
    }

    public class CreateServiceRequest
    {
        public string CategoryName      { get; set; }
        public string ServiceName       { get; set; }
        public string Href              { get; set; }
        public string Description       { get; set; }
        public string Link              { get; set; }
        public string XsdRequestContent { get; set; }
        public string XsdSchemaContent  { get; set; }
    }
}
