using Documentation.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Http;
using System.Xml;
using Formatting = Newtonsoft.Json.Formatting;

namespace Documentation.Controllers
{
    public class AdminController : ApiController
    {
        private static readonly string EndpointsFile = "~/Models/endpoints.json";
        private static readonly string UsersFile      = "~/Models/users.json";

        private class AdminSession
        {
            public DateTime Expiry       { get; set; }
            public bool     IsSuperAdmin { get; set; }
            public string   Email        { get; set; }
            public string   UserId       { get; set; }
            public string   Role         { get; set; } // "SuperAdmin","Executive","ProjectManager","Developer"
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
                Sessions[token] = new AdminSession { Expiry = DateTime.UtcNow.AddHours(8), IsSuperAdmin = true, Role = "SuperAdmin" };
                return Ok(new { token, mustChangePassword = false, isSuperAdmin = true, role = "SuperAdmin", email = (string)null });
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
                    var userRole  = user.Role ?? "ProjectManager";
                    var isSuperOrExec = userRole == "Executive";
                    var token = GenerateToken();
                    Sessions[token] = new AdminSession
                    {
                        Expiry       = DateTime.UtcNow.AddHours(8),
                        IsSuperAdmin = isSuperOrExec,
                        Email        = user.Email,
                        UserId       = user.Id,
                        Role         = userRole
                    };
                    return Ok(new { token, mustChangePassword = user.MustChangePassword, isSuperAdmin = isSuperOrExec, role = userRole, email = user.Email });
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

            // Resolve manager IDs to {id,email} pairs from users.json. We only
            // surface users who are still active Project Managers; stale IDs
            // (deleted users, role-changed users) are silently filtered out.
            var users = LoadUsers()?.Users ?? new List<UserEntry>();
            var pmById = users
                .Where(u => string.Equals(u.Role, "ProjectManager", StringComparison.Ordinal))
                .ToDictionary(u => u.Id, u => u, StringComparer.Ordinal);

            return Ok(list.Select(e => new {
                id              = e.Id,
                name            = e.Name,
                baseUrl         = e.BaseUrl,
                encodedKey      = e.EncodedKey,
                hiddenServices  = e.HiddenServices ?? new List<string>(),
                projectManagers = (e.ProjectManagerIds ?? new List<string>())
                    .Where(uid => pmById.ContainsKey(uid))
                    .Select(uid => new { id = pmById[uid].Id, email = pmById[uid].Email })
                    .ToList()
            }));
        }

        [HttpPost]
        [Route("api/admin/endpoints/{id}/toggle-manager")]
        public IHttpActionResult ToggleEndpointManager(string id)
        {
            AdminSession session;
            if (!TryGetSession(out session)) return Unauthorized();
            if (session.Role != "ProjectManager" || string.IsNullOrEmpty(session.UserId))
                return BadRequest("Only Project Managers can claim responsibility for an endpoint.");

            var config = LoadConfig();
            if (config?.Endpoints == null) return NotFound();
            var entry = config.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return NotFound();

            if (entry.ProjectManagerIds == null) entry.ProjectManagerIds = new List<string>();

            bool nowResponsible;
            if (entry.ProjectManagerIds.Contains(session.UserId))
            {
                entry.ProjectManagerIds.Remove(session.UserId);
                nowResponsible = false;
            }
            else
            {
                entry.ProjectManagerIds.Add(session.UserId);
                nowResponsible = true;
            }

            SaveConfig(config);
            LogInternal("ToggleManager", "Endpoint", entry.Id, entry.Name,
                "user=" + (session.Email ?? "?") + ", responsible=" + nowResponsible);
            return Ok(new { id = entry.Id, responsible = nowResponsible });
        }

        [HttpPost]
        [Route("api/admin/endpoints/{id}/assign-manager")]
        public IHttpActionResult AssignEndpointManager(string id, [FromBody] AssignEndpointManagerRequest req)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.UserId))
                return BadRequest("userId is required.");

            var config = LoadConfig();
            if (config?.Endpoints == null) return NotFound();
            var entry = config.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return NotFound();

            // Only allow assignment when the endpoint has nobody yet.
            if (entry.ProjectManagerIds != null && entry.ProjectManagerIds.Count > 0)
                return BadRequest("Endpoint is already assigned to a project manager.");

            var users = LoadUsers();
            var target = users?.Users.FirstOrDefault(u => string.Equals(u.Id, req.UserId, StringComparison.Ordinal));
            if (target == null) return NotFound();
            if (!string.Equals(target.Status, "active", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Target user is not active.");
            if (!string.Equals(target.Role, "ProjectManager", StringComparison.Ordinal))
                return BadRequest("Target user is not a Project Manager.");

            if (entry.ProjectManagerIds == null) entry.ProjectManagerIds = new List<string>();
            entry.ProjectManagerIds.Add(target.Id);
            SaveConfig(config);
            LogInternal("AssignManager", "Endpoint", entry.Id, entry.Name,
                "assigned=" + target.Email);
            return Ok(new { id = entry.Id, projectManagerEmail = target.Email });
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

            LogInternal("Create", "Endpoint", entry.Id, entry.Name, "baseUrl=" + entry.BaseUrl);
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
            var doomed  = list.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            var removed = list.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();

            config.Endpoints = list.ToArray();
            SaveConfig(config);
            LogInternal("Delete", "Endpoint", id, doomed?.Name);
            return Ok();
        }

        [HttpPut]
        [Route("api/admin/endpoints/{id}/visibility")]
        public IHttpActionResult UpdateEndpointVisibility(string id, [FromBody] EndpointVisibilityRequest req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var config = LoadConfig();
            if (config?.Endpoints == null) return NotFound();
            var entry = config.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return NotFound();
            entry.HiddenServices = req?.HiddenServices ?? new List<string>();
            SaveConfig(config);
            LogInternal("Update", "Endpoint", entry.Id, entry.Name,
                "hiddenServices=" + entry.HiddenServices.Count);
            return Ok(new { id = entry.Id, hiddenServices = entry.HiddenServices });
        }

        // ── User Management (super-admin only) ────────────────────────────────────

        // Sentinel id for the synthetic super-admin row prepended to the users list.
        // It never matches any real users.json entry, so the existing approve/role/
        // delete endpoints all naturally 404 when they receive it — no extra guards
        // needed there. A `system: true` flag in the response signals to the admin
        // panel UI that this row must not show Remove/role-change controls.
        private const string SuperAdminSyntheticId = "system-superadmin";

        [HttpGet]
        [Route("api/admin/users")]
        public IHttpActionResult GetAdminUsers()
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers() ?? new UsersData();

            var rows = new List<object>();
            // Always-present row for the cyberhub super-admin (which lives only in
            // web.config and never in users.json). This is what makes "Active Users"
            // never empty when no registered user exists yet — and lets the logged-in
            // admin actually see themselves in the list.
            var adminUsername = ConfigurationManager.AppSettings["AdminUsername"] ?? "cyberhub";
            rows.Add(new
            {
                id                 = SuperAdminSyntheticId,
                email              = adminUsername,
                status             = "active",
                role               = "SuperAdmin",
                mustChangePassword = false,
                createdAt          = (DateTime?)null,
                approvedAt         = (DateTime?)null,
                system             = true
            });

            rows.AddRange(data.Users.Select(u => (object)new
            {
                id                 = u.Id,
                email              = u.Email,
                status             = u.Status,
                role               = u.Role ?? "ProjectManager",
                mustChangePassword = u.MustChangePassword,
                createdAt          = (DateTime?)u.CreatedAt,
                approvedAt         = u.ApprovedAt,
                system             = false
            }));

            return Ok(rows);
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
            var emailResult = SendCredentialEmail(user.Email, tempPassword);
            LogInternal("Approve", "User", user.Id, user.Email,
                "role=" + (user.Role ?? "ProjectManager") + ",emailSent=" + emailResult.Sent);
            return Ok(new { email = user.Email, tempPassword, emailSent = emailResult.Sent, emailError = emailResult.Error });
        }

        // SuperAdmin-only direct user creation. Skips the public registration
        // queue: the user is provisioned in "active" status with a generated
        // temporary password and must-change-password flag set, and an email
        // is fired off (when SMTP is configured). The email allow-list rule
        // mirrors UserController.Register exactly so the two paths can't drift.
        [HttpPost]
        [Route("api/admin/users")]
        public IHttpActionResult CreateUser([FromBody] CreateAdminUserRequest req)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.Email))
                return BadRequest("Email is required.");

            var email = req.Email.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(email, @"^[^@\s]+@cyberlogic\.gr$"))
                return BadRequest("Only @cyberlogic.gr email addresses are allowed.");

            var validRoles = new[] { "Executive", "ProjectManager", "Developer" };
            var role = string.IsNullOrWhiteSpace(req.Role) ? "ProjectManager" : req.Role.Trim();
            if (Array.IndexOf(validRoles, role) < 0)
                return BadRequest("Invalid role. Must be Executive, ProjectManager, or Developer.");

            var data = LoadUsers() ?? new UsersData();
            if (data.Users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("A user with this email already exists.");

            var tempPassword = GeneratePassword();
            var newUser = new UserEntry
            {
                Id                 = Guid.NewGuid().ToString("N"),
                Email              = email,
                Role               = role,
                Status             = "active",
                PasswordHash       = CreatePbkdf2Hash(tempPassword),
                MustChangePassword = true,
                CreatedAt          = DateTime.UtcNow,
                ApprovedAt         = DateTime.UtcNow
            };
            data.Users.Add(newUser);
            SaveUsers(data);

            var emailResult = SendCredentialEmail(email, tempPassword);
            LogInternal("Create", "User", newUser.Id, newUser.Email,
                "role=" + role + ",direct=true,emailSent=" + emailResult.Sent);
            return Ok(new
            {
                id           = newUser.Id,
                email        = newUser.Email,
                role         = newUser.Role,
                tempPassword,
                emailSent    = emailResult.Sent,
                emailError   = emailResult.Error
            });
        }

        // Re-issues a temporary password for an existing user and forces a
        // password change on next login. Used by the SuperAdmin to recover a
        // user who lost their password — never exposes the old hash.
        [HttpPost]
        [Route("api/admin/users/{id}/reset-password")]
        public IHttpActionResult ResetUserPassword(string id)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers();
            if (data == null) return NotFound();
            var user = data.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (user == null) return NotFound();
            if (!string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Only active users can have their password reset. Approve the registration first.");

            var tempPassword = GeneratePassword();
            user.PasswordHash       = CreatePbkdf2Hash(tempPassword);
            user.MustChangePassword = true;
            SaveUsers(data);

            var emailResult = SendCredentialEmail(user.Email, tempPassword);
            LogInternal("ResetPassword", "User", user.Id, user.Email, "emailSent=" + emailResult.Sent);
            return Ok(new
            {
                email      = user.Email,
                tempPassword,
                emailSent  = emailResult.Sent,
                emailError = emailResult.Error
            });
        }

        // Wraps Services.EmailService so the three credential-issuing paths
        // (Approve / Create / ResetPassword) share one call site. The portal
        // URL in the body is read from web.config so emails always link back
        // to the right deployment.
        private class EmailOutcome { public bool Sent; public string Error; }
        private EmailOutcome SendCredentialEmail(string email, string tempPassword)
        {
            var portalUrl = ConfigurationManager.AppSettings["AdminPortalUrl"] ?? string.Empty;
            var body      = Documentation.Services.EmailService.BuildCredentialBody(email, tempPassword, portalUrl);
            string err;
            var ok = Documentation.Services.EmailService.TrySend(
                email, "Your Cyberlogic API documentation account", body, out err);
            return new EmailOutcome { Sent = ok, Error = err };
        }

        [HttpDelete]
        [Route("api/admin/users/{id}")]
        public IHttpActionResult DeleteUser(string id)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var data = LoadUsers();
            if (data == null) return NotFound();
            var doomed = data.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            var removed = data.Users.RemoveAll(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (removed == 0) return NotFound();
            SaveUsers(data);
            // Strip the deleted user from any endpoint's project-manager list.
            RemoveUserFromEndpointManagers(id);
            LogInternal("Delete", "User", id, doomed?.Email, "role=" + (doomed?.Role ?? "?"));
            return Ok();
        }

        private void RemoveUserFromEndpointManagers(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            var config = LoadConfig();
            if (config?.Endpoints == null) return;
            bool changed = false;
            foreach (var ep in config.Endpoints)
            {
                if (ep.ProjectManagerIds != null && ep.ProjectManagerIds.Remove(userId))
                    changed = true;
            }
            if (changed) SaveConfig(config);
        }

        [HttpPut]
        [Route("api/admin/users/{id}/role")]
        public IHttpActionResult UpdateUserRole(string id, [FromBody] UpdateUserRoleRequest req)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.Role))
                return BadRequest("role is required.");

            var validRoles = new[] { "Executive", "ProjectManager", "Developer" };
            var role = req.Role.Trim();
            if (Array.IndexOf(validRoles, role) < 0)
                return BadRequest("Invalid role. Must be Executive, ProjectManager, or Developer.");

            var data = LoadUsers();
            if (data == null) return NotFound();
            var user = data.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal));
            if (user == null) return NotFound();

            var oldRole = user.Role;
            user.Role = role;
            SaveUsers(data);
            LogInternal("ChangeRole", "User", user.Id, user.Email, "from=" + (oldRole ?? "?") + ", to=" + role);

            // If the user is no longer a Project Manager, drop them from every
            // endpoint's responsibility list — only PMs can hold that role.
            if (role != "ProjectManager")
                RemoveUserFromEndpointManagers(user.Id);

            // Live-update any active sessions for this user so the change takes
            // effect on the next API call without requiring a re-login.
            var isSuperOrExec = role == "Executive";
            foreach (var kv in Sessions)
            {
                var s = kv.Value;
                if (s != null && string.Equals(s.UserId, user.Id, StringComparison.Ordinal))
                {
                    s.Role         = role;
                    s.IsSuperAdmin = isSuperOrExec;
                }
            }

            return Ok(new { id = user.Id, role = user.Role });
        }

        // ── Logs ─────────────────────────────────────────────────────────────────

        // Each log is rotated daily into ~/Logs/<category>/YYYY-MM-DD.json so that
        // entries persist across sessions, app-pool recycles and re-publishes.
        // Clients pick a date; queries default to today (UTC) when omitted.

        [HttpGet]
        [Route("api/admin/logs/internal/dates")]
        public IHttpActionResult GetInternalLogDates()
        {
            if (!IsSuperAdmin()) return Unauthorized();
            return Ok(LogStore.ListInternalDates());
        }

        [HttpGet]
        [Route("api/admin/logs/internal")]
        public IHttpActionResult GetInternalLog(string date = null)
        {
            if (!IsSuperAdmin()) return Unauthorized();
            var d = NormalizeDate(date);
            var data = LogStore.LoadInternalForDate(d);
            var entries = data.Entries
                .OrderByDescending(e => e.Timestamp)
                .Select(e => new
                {
                    timestamp  = e.Timestamp,
                    userEmail  = e.UserEmail,
                    userRole   = e.UserRole,
                    action     = e.Action,
                    category   = e.Category,
                    targetId   = e.TargetId,
                    targetName = e.TargetName,
                    details    = e.Details
                });
            return Ok(new { date = d, entries });
        }

        [HttpGet]
        [Route("api/admin/logs/internal/csv")]
        public HttpResponseMessage GetInternalLogCsv(string date = null)
        {
            if (!IsSuperAdmin())
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);

            var d = NormalizeDate(date);
            var data = LogStore.LoadInternalForDate(d);
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,User,Role,Action,Category,TargetId,TargetName,Details");
            foreach (var e in data.Entries.OrderByDescending(x => x.Timestamp))
            {
                sb.Append(CsvCell(e.Timestamp.ToString("u"))).Append(',');
                sb.Append(CsvCell(e.UserEmail)).Append(',');
                sb.Append(CsvCell(e.UserRole)).Append(',');
                sb.Append(CsvCell(e.Action)).Append(',');
                sb.Append(CsvCell(e.Category)).Append(',');
                sb.Append(CsvCell(e.TargetId)).Append(',');
                sb.Append(CsvCell(e.TargetName)).Append(',');
                sb.Append(CsvCell(e.Details));
                sb.AppendLine();
            }
            return CsvResponse(sb.ToString(), "internal-log-" + d + ".csv");
        }

        [HttpGet]
        [Route("api/admin/logs/documentation/dates")]
        public IHttpActionResult GetDocumentationLogDates()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(LogStore.ListDocumentationDates());
        }

        [HttpGet]
        [Route("api/admin/logs/documentation")]
        public IHttpActionResult GetDocumentationLog(string date = null)
        {
            // Visible to any authenticated registered user.
            if (!IsAuthenticated()) return Unauthorized();
            var d = NormalizeDate(date);
            var data = LogStore.LoadDocumentationForDate(d);
            var entries = data.Entries
                .OrderByDescending(e => e.Timestamp)
                .Select(e => new
                {
                    timestamp    = e.Timestamp,
                    endpointName = e.EndpointName,
                    userName     = e.UserName,
                    serviceHref  = e.ServiceHref,
                    serviceName  = e.ServiceName
                });
            return Ok(new { date = d, entries });
        }

        [HttpGet]
        [Route("api/admin/logs/documentation/csv")]
        public HttpResponseMessage GetDocumentationLogCsv(string date = null)
        {
            if (!IsAuthenticated())
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);

            var d = NormalizeDate(date);
            var data = LogStore.LoadDocumentationForDate(d);
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,EndpointName,UserName,ServiceHref,ServiceName");
            foreach (var e in data.Entries.OrderByDescending(x => x.Timestamp))
            {
                sb.Append(CsvCell(e.Timestamp.ToString("u"))).Append(',');
                sb.Append(CsvCell(e.EndpointName)).Append(',');
                sb.Append(CsvCell(e.UserName)).Append(',');
                sb.Append(CsvCell(e.ServiceHref)).Append(',');
                sb.Append(CsvCell(e.ServiceName));
                sb.AppendLine();
            }
            return CsvResponse(sb.ToString(), "documentation-log-" + d + ".csv");
        }

        private static string NormalizeDate(string raw)
        {
            DateTime parsed;
            if (!string.IsNullOrWhiteSpace(raw) &&
                DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out parsed))
            {
                return parsed.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
            return DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string CsvCell(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var needsQuote = raw.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            var s = raw.Replace("\"", "\"\"");
            return needsQuote ? "\"" + s + "\"" : s;
        }

        private static HttpResponseMessage CsvResponse(string body, string filename)
        {
            // Prepend BOM so Excel treats it as UTF-8 with non-ASCII chars intact.
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(Encoding.UTF8.GetBytes(body))
                .ToArray();
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
            resp.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = filename };
            return resp;
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

        private bool CanApproveSection()
        {
            AdminSession s;
            return TryGetSession(out s) && (s.IsSuperAdmin || s.Role == "Executive");
        }

        private bool CanApproveService()
        {
            AdminSession s;
            return TryGetSession(out s) && (s.IsSuperAdmin || s.Role == "Executive" || s.Role == "Developer");
        }

        private string CurrentRole()
        {
            AdminSession s;
            return TryGetSession(out s) ? (s.Role ?? "ProjectManager") : null;
        }

        // ── Internal log helper ─────────────────────────────────────────────────

        private void LogInternal(string action, string category, string targetId, string targetName, string details = null)
        {
            AdminSession s;
            TryGetSession(out s);
            LogStore.AppendInternal(new InternalLogEntry
            {
                Timestamp  = DateTime.UtcNow,
                UserEmail  = s != null ? (s.Email ?? "cyberhub") : "system",
                UserRole   = s != null ? (s.Role  ?? "Unknown")  : "system",
                Action     = action,
                Category   = category,
                TargetId   = targetId,
                TargetName = targetName,
                Details    = details
            });
        }

        private string ContentStatusForCurrentUser()
        {
            var role = CurrentRole();
            return (role == "SuperAdmin" || role == "Executive") ? "approved" : "pending";
        }

        private string ServiceStatusForCurrentUser()
        {
            var role = CurrentRole();
            return (role == "SuperAdmin" || role == "Executive" || role == "Developer") ? "approved" : "pending";
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

        private static readonly string ContentFile         = "~/Models/content.json";
        private static readonly string FieldDescFile       = "~/Models/fieldDescriptions.json";
        private static readonly string PendingFieldDescFile = "~/Models/pendingFieldDescriptions.json";
        private static readonly string ServicesFile  = "~/Models/webservicesUpdated.json";
        private static readonly string XsdBase       = "~/Models/XSDs/XSDs/";
        private static readonly string FieldAssignmentsFile = "~/Models/serviceFieldAssignments.json";

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

            // Empty string from the editor's "Shared (all endpoints)" option must
            // serialize to null in storage so the JsonProperty NullValueHandling
            // keeps the file clean and the Index.html filter handles both as shared.
            section.EndpointId = NormalizeEndpointId(section.EndpointId);
            if (!string.IsNullOrEmpty(section.EndpointId) && !IsKnownEndpointId(section.EndpointId))
                return BadRequest("Unknown endpointId.");

            if (string.IsNullOrWhiteSpace(section.Id))
                section.Id = Regex.Replace(section.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            section.Status = ContentStatusForCurrentUser();
            section.PendingChanges = null; // brand-new sections never carry a parked edit
            var data = LoadContent() ?? new ContentData();
            if (data.Sections.Any(s => string.Equals(s.Id, section.Id, StringComparison.OrdinalIgnoreCase)))
                section.Id += "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            data.Sections.Add(section);
            SaveContent(data);
            LogInternal("Create", "ContentSection", section.Id, section.Title,
                "status=" + section.Status + ",endpointId=" + (section.EndpointId ?? "shared"));
            return Ok(section);
        }

        // Normalises the wire-format value: empty/whitespace → null (shared), else trimmed.
        private static string NormalizeEndpointId(string raw)
        {
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        // Cheap existence check against endpoints.json. Used to refuse sections that
        // claim to belong to a non-existent tenant — a user who edits the wire payload
        // by hand (or a stale client) shouldn't be able to orphan content.
        private bool IsKnownEndpointId(string endpointId)
        {
            if (string.IsNullOrWhiteSpace(endpointId)) return false;
            var config = LoadConfig();
            if (config?.Endpoints == null) return false;
            return config.Endpoints.Any(e => string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase));
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

            var canAutoApprove = ContentStatusForCurrentUser() == "approved";
            // A section is "approved with content live on the docs page" when its
            // status is approved AND it is not still a brand-new pending entry.
            // For approved sections, a non-approver's edit must NOT overwrite
            // the live fields — it parks in PendingChanges instead.
            var sectionIsLive = string.Equals(sec.Status, "approved", StringComparison.OrdinalIgnoreCase);

            // Validate any new endpointId before we mutate anything.
            var proposedEndpointId = NormalizeEndpointId(update.EndpointId);
            if (!string.IsNullOrEmpty(proposedEndpointId) && !IsKnownEndpointId(proposedEndpointId))
                return BadRequest("Unknown endpointId.");

            if (canAutoApprove)
            {
                // Executive / SuperAdmin: write through directly and clear any parked edit.
                if (!string.IsNullOrWhiteSpace(update.Title)) sec.Title = update.Title;
                if (update.Items != null)                     sec.Items = update.Items;
                if (update.Placement != null)                 sec.Placement = update.Placement;
                sec.Headless       = update.Headless;
                sec.EndpointId     = proposedEndpointId;
                sec.PendingChanges = null;
                sec.Status         = "approved";
                SaveContent(data);
                LogInternal("Update", "ContentSection", sec.Id, sec.Title,
                    "status=approved,endpointId=" + (sec.EndpointId ?? "shared"));
                return Ok(sec);
            }

            if (sectionIsLive)
            {
                // Non-approver editing a live (approved) section: park the change.
                AdminSession s; TryGetSession(out s);
                sec.PendingChanges = new PendingSectionChanges
                {
                    Title      = string.IsNullOrWhiteSpace(update.Title) ? sec.Title : update.Title,
                    Items      = update.Items ?? sec.Items,
                    Placement  = update.Placement, // null is a legitimate value (top placement)
                    Headless   = update.Headless,
                    EndpointId = proposedEndpointId,
                    EditedAt   = DateTime.UtcNow,
                    EditedBy   = s?.Email ?? "(unknown)"
                };
                // Status stays "approved" so Index.html keeps showing the live version.
                SaveContent(data);
                LogInternal("Update", "ContentSection", sec.Id, sec.Title, "status=pending-edit");
                return Ok(sec);
            }

            // Non-approver editing their own brand-new pending section: keep
            // updating it in place — no live version to protect yet.
            if (!string.IsNullOrWhiteSpace(update.Title)) sec.Title = update.Title;
            if (update.Items != null)                     sec.Items = update.Items;
            if (update.Placement != null)                 sec.Placement = update.Placement;
            sec.Headless       = update.Headless;
            sec.EndpointId     = proposedEndpointId;
            sec.PendingChanges = null;
            sec.Status         = "pending";
            SaveContent(data);
            LogInternal("Update", "ContentSection", sec.Id, sec.Title,
                "status=pending,endpointId=" + (sec.EndpointId ?? "shared"));
            return Ok(sec);
        }

        [HttpPost]
        [Route("api/admin/content/sections/{id}/approve")]
        public IHttpActionResult ApproveSection(string id)
        {
            if (!CanApproveSection()) return Unauthorized();
            var data = LoadContent();
            if (data == null) return NotFound();
            var sec = data.Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (sec == null) return NotFound();

            // Two flavours of approval:
            //   • New pending section          → just flip status to approved.
            //   • Live section with a parked edit → commit PendingChanges into
            //     the live fields, clear the parking lot.
            if (sec.PendingChanges != null)
            {
                var p = sec.PendingChanges;
                sec.Title      = p.Title;
                sec.Items      = p.Items ?? new List<ContentItem>();
                sec.Placement  = p.Placement;
                sec.Headless   = p.Headless;
                sec.EndpointId = NormalizeEndpointId(p.EndpointId);
                sec.PendingChanges = null;
            }
            sec.Status = "approved";
            SaveContent(data);
            LogInternal("Approve", "ContentSection", sec.Id, sec.Title);
            return Ok(sec);
        }

        // Discards a pending edit / pending new-section without touching live content.
        // Replaces the previous behaviour of "Reject = DELETE", which was destructive
        // when an Executive rejected a Project Manager's edit to an approved section.
        [HttpPost]
        [Route("api/admin/content/sections/{id}/reject")]
        public IHttpActionResult RejectSection(string id)
        {
            if (!CanApproveSection()) return Unauthorized();
            var data = LoadContent();
            if (data == null) return NotFound();
            var sec = data.Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (sec == null) return NotFound();

            if (sec.PendingChanges != null)
            {
                // Live section with a parked edit → drop the edit, keep approved fields.
                sec.PendingChanges = null;
                sec.Status         = "approved";
                SaveContent(data);
                LogInternal("Reject", "ContentSection", sec.Id, sec.Title, "scope=pendingChanges");
                return Ok(new { id = sec.Id, scope = "pendingChanges", deleted = false });
            }

            if (string.Equals(sec.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                // Brand-new pending section → there is no approved version to keep, drop it.
                data.Sections.Remove(sec);
                SaveContent(data);
                LogInternal("Reject", "ContentSection", sec.Id, sec.Title, "scope=newSection,deleted=true");
                return Ok(new { id = sec.Id, scope = "newSection", deleted = true });
            }

            // Approved section with no pending edit → nothing to reject.
            return BadRequest("Section has no pending changes to reject.");
        }

        [HttpDelete]
        [Route("api/admin/content/sections/{id}")]
        public IHttpActionResult DeleteSection(string id)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var data = LoadContent();
            if (data == null) return NotFound();
            var doomed = data.Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            var removed = data.Sections.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();
            SaveContent(data);
            LogInternal("Delete", "ContentSection", id, doomed?.Title);
            return Ok();
        }

        // Reorders the sections array. The body's orderedIds list defines the
        // new sequence; any IDs missing from the list are appended to the end
        // in their original order. Index.html renders sections in array order,
        // so this also drives the on-screen order on the docs page.
        [HttpPost]
        [Route("api/admin/content/sections/reorder")]
        public IHttpActionResult ReorderSections([FromBody] ReorderSectionsRequest req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (req == null || req.OrderedIds == null) return BadRequest("orderedIds is required.");

            var data = LoadContent();
            if (data == null) return NotFound();

            var byId = data.Sections.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
            var rebuilt = new List<ContentSection>(data.Sections.Count);
            foreach (var sid in req.OrderedIds)
            {
                ContentSection s;
                if (sid != null && byId.TryGetValue(sid, out s))
                {
                    rebuilt.Add(s);
                    byId.Remove(sid);
                }
            }
            // Append leftovers (sections the client didn't mention) in original order
            // so a stale client can't accidentally drop sections.
            foreach (var s in data.Sections)
                if (byId.ContainsKey(s.Id)) rebuilt.Add(s);

            data.Sections = rebuilt;
            SaveContent(data);
            LogInternal("Reorder", "ContentSection", null, null, "count=" + rebuilt.Count);
            return Ok(new { count = rebuilt.Count });
        }

        // ── Field Descriptions ────────────────────────────────────────────────────

        [HttpGet]
        [Route("api/admin/content/fields")]
        public IHttpActionResult GetFields()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(new {
                approved = LoadFields() ?? new Dictionary<string, string>(),
                pending  = LoadPendingFields() ?? new Dictionary<string, string>()
            });
        }

        [HttpPost]
        [Route("api/admin/content/fields")]
        public IHttpActionResult CreateField([FromBody] FieldEntry req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return BadRequest("name is required.");
            var trimmedName = req.Name.Trim();
            if (ContentStatusForCurrentUser() == "approved")
            {
                var fields = LoadFields() ?? new Dictionary<string, string>();
                fields[trimmedName] = req.Description ?? string.Empty;
                SaveFields(fields);
                LogInternal("Create", "FieldDescription", trimmedName, trimmedName, "status=approved");
                return Ok(new { status = "approved" });
            }
            else
            {
                var pending = LoadPendingFields() ?? new Dictionary<string, string>();
                pending[trimmedName] = req.Description ?? string.Empty;
                SavePendingFields(pending);
                LogInternal("Create", "FieldDescription", trimmedName, trimmedName, "status=pending");
                return Ok(new { status = "pending" });
            }
        }

        [HttpPut]
        [Route("api/admin/content/fields/{name}")]
        public IHttpActionResult UpdateField(string name, [FromBody] FieldEntry req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var fields = LoadFields();
            if (fields != null && fields.ContainsKey(name))
            {
                if (ContentStatusForCurrentUser() == "approved")
                {
                    fields[name] = req?.Description ?? string.Empty;
                    SaveFields(fields);
                    LogInternal("Update", "FieldDescription", name, name, "status=approved");
                    return Ok(new { status = "approved" });
                }
                else
                {
                    var pending = LoadPendingFields() ?? new Dictionary<string, string>();
                    pending[name] = req?.Description ?? string.Empty;
                    SavePendingFields(pending);
                    LogInternal("Update", "FieldDescription", name, name, "status=pending");
                    return Ok(new { status = "pending" });
                }
            }
            var pf = LoadPendingFields();
            if (pf != null && pf.ContainsKey(name))
            {
                pf[name] = req?.Description ?? string.Empty;
                SavePendingFields(pf);
                LogInternal("Update", "FieldDescription", name, name, "status=pending");
                return Ok(new { status = "pending" });
            }
            return NotFound();
        }

        [HttpPost]
        [Route("api/admin/content/fields/{name}/approve")]
        public IHttpActionResult ApproveField(string name)
        {
            if (!CanApproveSection()) return Unauthorized();
            var pending = LoadPendingFields();
            if (pending == null || !pending.ContainsKey(name)) return NotFound();
            var desc = pending[name];
            pending.Remove(name);
            SavePendingFields(pending);
            var fields = LoadFields() ?? new Dictionary<string, string>();
            fields[name] = desc;
            SaveFields(fields);
            LogInternal("Approve", "FieldDescription", name, name);
            return Ok();
        }

        [HttpDelete]
        [Route("api/admin/content/fields/{name}")]
        public IHttpActionResult DeleteField(string name)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var fields = LoadFields();
            bool removed = false;
            if (fields != null && fields.ContainsKey(name)) { fields.Remove(name); SaveFields(fields); removed = true; }
            var pending = LoadPendingFields();
            if (pending != null && pending.ContainsKey(name)) { pending.Remove(name); SavePendingFields(pending); removed = true; }
            if (!removed) return NotFound();
            LogInternal("Delete", "FieldDescription", name, name);
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
                    LogInternal("ToggleVisibility", "Service", svc.Href, svc.Service, "hidden=" + svc.Hidden);
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
                Hidden      = false,
                Status      = ServiceStatusForCurrentUser()
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
            LogInternal("Create", "Service", newService.Href, newService.Service, "category=" + req.CategoryName + ", status=" + newService.Status);
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
            string serviceName = null;
            foreach (var cat in catalog.ServiceCategory.ToList())
            {
                var doomed = cat.Services.FirstOrDefault(s => string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
                if (doomed != null) serviceName = doomed.Service;
                var rem = cat.Services.RemoveAll(s => string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
                if (rem > 0) found = true;
                if (cat.Services.Count == 0) catalog.ServiceCategory.Remove(cat);
            }
            if (!found) return NotFound();
            SaveServiceCatalog(catalog);
            LogInternal("Delete", "Service", href, serviceName);
            return Ok();
        }

        [HttpPost]
        [Route("api/admin/services/{href}/approve")]
        public IHttpActionResult ApproveService(string href)
        {
            if (!CanApproveService()) return Unauthorized();
            var catalog = LoadServiceCatalog();
            if (catalog == null) return NotFound();
            foreach (var cat in catalog.ServiceCategory)
            {
                var svc = cat.Services.FirstOrDefault(s => string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
                if (svc != null)
                {
                    svc.Status = "approved";
                    SaveServiceCatalog(catalog);
                    LogInternal("Approve", "Service", svc.Href, svc.Service);
                    return Ok(new { href = svc.Href, status = svc.Status });
                }
            }
            return NotFound();
        }

        // ── Service field assignments ──────────────────────────────────────────────
        // Lets an admin place a glossary field (e.g. an "Ungrouped" entry like
        // "CheckIn From" / "To") onto a service's request. The assignment is tracked
        // in serviceFieldAssignments.json AND patched into the service's request XSD,
        // so the existing XSD-driven form + request-builder in Index.html pick it up
        // automatically (an attribute-bearing element renders as <CheckIn From=".." To=".." />).

        [HttpGet]
        [Route("api/admin/field-assignments")]
        public IHttpActionResult GetFieldAssignments()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(LoadFieldAssignments());
        }

        [HttpPost]
        [Route("api/admin/field-assignments")]
        public IHttpActionResult UpsertFieldAssignment([FromBody] ServiceFieldAssignmentRequest req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (req == null ||
                string.IsNullOrWhiteSpace(req.Field) ||
                string.IsNullOrWhiteSpace(req.Href) ||
                string.IsNullOrWhiteSpace(req.Element))
                return BadRequest("field, href, and element are required.");

            var element = req.Element.Trim();
            var attrs   = (req.Attributes ?? new List<AttrSpec>())
                          .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name)).ToList();
            if (!IsXmlName(element))
                return BadRequest("element must be a valid XML name (letters, digits, '_', '-', no spaces).");
            foreach (var a in attrs)
                if (!IsXmlName(a.Name.Trim()))
                    return BadRequest("attribute name '" + a.Name + "' is not a valid XML name.");
            var dupAttr = attrs.GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dupAttr != null)
                return BadRequest("Duplicate attribute name '" + dupAttr.Key + "'.");

            var svc = FindService(req.Href);
            if (svc == null) return NotFound();
            if (string.IsNullOrWhiteSpace(svc.XsdRequest))
                return BadRequest("This service has no request XSD to extend.");

            var all = LoadFieldAssignments();
            List<ServiceFieldAssignment> list;
            if (!all.TryGetValue(req.Href, out list)) { list = new List<ServiceFieldAssignment>(); all[req.Href] = list; }
            var old = CloneAssignments(list);

            var field = req.Field.Trim();
            // A glossary field owns exactly one element per service; re-assigning
            // replaces all of that field's rows (one row per attribute, or a single
            // row for a plain element).
            list.RemoveAll(a => string.Equals(a.Field, field, StringComparison.OrdinalIgnoreCase));
            if (attrs.Count > 0)
            {
                foreach (var a in attrs)
                    list.Add(new ServiceFieldAssignment
                    {
                        Field = field, Element = element, Attr = a.Name.Trim(),
                        Type = NormalizeXsdType(a.Type), Required = req.Required
                    });
            }
            else
            {
                list.Add(new ServiceFieldAssignment
                {
                    Field = field, Element = element, Attr = null,
                    Type = NormalizeXsdType(req.Type), Required = req.Required
                });
            }

            // Guard: an element can't be both simple and attribute-based.
            var conflict = ValidateElementConsistency(list, element);
            if (conflict != null) return BadRequest(conflict);

            try { PatchServiceXsd(svc.XsdRequest, old, list); }
            catch (Exception ex) { return InternalServerError(ex); }

            SaveFieldAssignments(all);
            LogInternal("AssignField", "Service", svc.Href, svc.Service,
                "field=" + field + ", element=" + element +
                (attrs.Count > 0 ? ", attrs=" + string.Join("/", attrs.Select(a => a.Name.Trim())) : "") +
                ", required=" + req.Required);
            return Ok(new { href = svc.Href, assignments = list });
        }

        [HttpDelete]
        [Route("api/admin/field-assignments")]
        public IHttpActionResult DeleteFieldAssignment(string href, string field)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(field))
                return BadRequest("href and field are required.");

            var all = LoadFieldAssignments();
            List<ServiceFieldAssignment> list;
            if (!all.TryGetValue(href, out list) ||
                !list.Any(a => string.Equals(a.Field, field, StringComparison.OrdinalIgnoreCase)))
                return NotFound();

            var svc = FindService(href);
            var old = CloneAssignments(list);
            list.RemoveAll(a => string.Equals(a.Field, field, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) all.Remove(href);

            if (svc != null && !string.IsNullOrWhiteSpace(svc.XsdRequest))
            {
                try { PatchServiceXsd(svc.XsdRequest, old, list); }
                catch (Exception ex) { return InternalServerError(ex); }
            }

            SaveFieldAssignments(all);
            LogInternal("UnassignField", "Service", href, svc != null ? svc.Service : href, "field=" + field);
            return Ok(new { href, assignments = list });
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

        private Dictionary<string, string> LoadPendingFields()
        {
            var path = HttpContext.Current.Server.MapPath(PendingFieldDescFile);
            if (!File.Exists(path)) return new Dictionary<string, string>();
            try { return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>(); }
            catch { return new Dictionary<string, string>(); }
        }

        private void SavePendingFields(Dictionary<string, string> fields)
        {
            var path = HttpContext.Current.Server.MapPath(PendingFieldDescFile);
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

        // ── Field-assignment helpers ────────────────────────────────────────────────

        private Dictionary<string, List<ServiceFieldAssignment>> LoadFieldAssignments()
        {
            var path = HttpContext.Current.Server.MapPath(FieldAssignmentsFile);
            if (!File.Exists(path)) return new Dictionary<string, List<ServiceFieldAssignment>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, List<ServiceFieldAssignment>>>(File.ReadAllText(path))
                       ?? new Dictionary<string, List<ServiceFieldAssignment>>(StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, List<ServiceFieldAssignment>>(StringComparer.OrdinalIgnoreCase); }
        }

        private void SaveFieldAssignments(Dictionary<string, List<ServiceFieldAssignment>> data)
        {
            var path = HttpContext.Current.Server.MapPath(FieldAssignmentsFile);
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private ServiceEntryModel FindService(string href)
        {
            var catalog = LoadServiceCatalog();
            if (catalog == null) return null;
            return catalog.ServiceCategory
                .SelectMany(c => c.Services)
                .FirstOrDefault(s => string.Equals(s.Href, href, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ServiceFieldAssignment> CloneAssignments(List<ServiceFieldAssignment> src)
        {
            return src.Select(a => new ServiceFieldAssignment
            {
                Field = a.Field, Element = a.Element, Attr = a.Attr, Type = a.Type, Required = a.Required
            }).ToList();
        }

        private static bool IsXmlName(string s)
        {
            return !string.IsNullOrEmpty(s) && Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_-]*$");
        }

        private static string NormalizeXsdType(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "date":     return "date";
                case "int":
                case "integer":  return "int";
                case "decimal":
                case "number":   return "decimal";
                case "boolean":
                case "bool":     return "boolean";
                default:         return "string";
            }
        }

        // An element is either simple (no attr on any of its assignments) or
        // attribute-based (every assignment carries an attr). Mixing is invalid,
        // and a simple element can only be produced by a single field.
        private static string ValidateElementConsistency(List<ServiceFieldAssignment> list, string element)
        {
            // XML element names are case-sensitive — compare with Ordinal.
            var group = list.Where(a => string.Equals(a.Element, element, StringComparison.Ordinal)).ToList();
            var withAttr = group.Count(a => !string.IsNullOrEmpty(a.Attr));
            if (withAttr > 0 && withAttr != group.Count)
                return "Element '" + element + "' mixes attribute-based and simple mappings. Use an attribute name on all of them, or none.";
            if (withAttr == 0 && group.Count > 1)
                return "Element '" + element + "' is mapped by multiple fields without an attribute name. Give each an attribute name to combine them into one element.";
            return null;
        }

        private string ResolveXsdFullPath(string xsdRelPath)
        {
            var root = HttpContext.Current.Server.MapPath(XsdBase);
            var direct = Path.Combine(root, xsdRelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(direct)) return direct;
            // Fall back to a filename search anywhere under the XSD tree.
            var name = Path.GetFileName(xsdRelPath);
            return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
        }

        // Rebuilds the assignment-managed portion of a request XSD: strips every
        // element/complexType previously produced from `oldList`, then (re)adds the
        // elements/types for `newList`. Original hand-authored XSD nodes are never
        // touched because removal is keyed only by names this system created.
        private void PatchServiceXsd(string xsdRelPath, List<ServiceFieldAssignment> oldList, List<ServiceFieldAssignment> newList)
        {
            var fullPath = ResolveXsdFullPath(xsdRelPath);
            if (fullPath == null) throw new FileNotFoundException("Request XSD not found: " + xsdRelPath);

            const string XS = "http://www.w3.org/2001/XMLSchema";
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.Load(fullPath);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("xs", XS);

            var schema   = doc.SelectSingleNode("/xs:schema", ns);
            var rootElem = doc.SelectSingleNode("/xs:schema/xs:element[1]", ns);
            var sequence = rootElem?.SelectSingleNode("xs:complexType/xs:sequence", ns);
            if (schema == null || sequence == null)
                throw new InvalidOperationException("Unexpected request XSD shape (missing root element/sequence).");

            // Names this system manages = every element name (and its "<name>Type")
            // referenced by the union of old + new assignments.
            var managedElements = oldList.Concat(newList)
                .Select(a => a.Element).Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.Ordinal).ToList();

            foreach (var el in managedElements)
            {
                foreach (XmlNode n in sequence.SelectNodes("xs:element[@name='" + el + "']", ns).Cast<XmlNode>().ToList())
                    sequence.RemoveChild(n);
                foreach (XmlNode n in schema.SelectNodes("xs:complexType[@name='" + el + "Type']", ns).Cast<XmlNode>().ToList())
                    schema.RemoveChild(n);
            }

            // (Re)add nodes for the desired assignments, grouped by element name.
            var descriptions = LoadFields() ?? new Dictionary<string, string>();
            var pendingDesc  = LoadPendingFields() ?? new Dictionary<string, string>();
            foreach (var g in newList.GroupBy(a => a.Element, StringComparer.Ordinal))
            {
                var groupList = g.ToList();
                var attrBased = groupList.Any(a => !string.IsNullOrEmpty(a.Attr));

                var elem = doc.CreateElement("xs", "element", XS);
                elem.SetAttribute("name", g.Key);

                if (attrBased)
                {
                    var typeName = g.Key + "Type";
                    elem.SetAttribute("type", typeName);
                    elem.SetAttribute("minOccurs", groupList.Any(a => a.Required) ? "1" : "0");
                    elem.SetAttribute("maxOccurs", "1");

                    var ct = doc.CreateElement("xs", "complexType", XS);
                    ct.SetAttribute("name", typeName);
                    var seenAttrs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var a in groupList)
                    {
                        if (string.IsNullOrEmpty(a.Attr) || !seenAttrs.Add(a.Attr)) continue; // skip dups
                        var at = doc.CreateElement("xs", "attribute", XS);
                        at.SetAttribute("name", a.Attr);
                        at.SetAttribute("type", "xs:" + a.Type);
                        if (a.Required) at.SetAttribute("use", "required");
                        SetDocAttr(doc, at, DescFor(a.Field, descriptions, pendingDesc));
                        ct.AppendChild(at);
                    }
                    sequence.AppendChild(elem);
                    schema.AppendChild(ct);
                }
                else
                {
                    var a = groupList[0];
                    elem.SetAttribute("type", "xs:" + a.Type);
                    elem.SetAttribute("minOccurs", a.Required ? "1" : "0");
                    elem.SetAttribute("maxOccurs", "1");
                    SetDocAttr(doc, elem, DescFor(a.Field, descriptions, pendingDesc));
                    sequence.AppendChild(elem);
                }
            }

            doc.Save(fullPath);
        }

        private static string DescFor(string field, Dictionary<string, string> approved, Dictionary<string, string> pending)
        {
            string d;
            if (approved != null && approved.TryGetValue(field, out d) && !string.IsNullOrWhiteSpace(d)) return d;
            if (pending  != null && pending.TryGetValue(field, out d)  && !string.IsNullOrWhiteSpace(d)) return d;
            return field;
        }

        // Mirrors the project's XSD documentation convention: a namespace-declaration
        // attribute `xmlns:doc="..."` whose value carries the human description
        // (WebApiDocService reads node.Attributes["xmlns:doc"]).
        private static void SetDocAttr(XmlDocument doc, XmlElement el, string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return;
            try
            {
                var attr = doc.CreateAttribute("xmlns", "doc", "http://www.w3.org/2000/xmlns/");
                attr.Value = description;
                el.Attributes.Append(attr);
            }
            catch { /* description is cosmetic — never let it break the schema patch */ }
        }
    }

    // ── Request / response models ─────────────────────────────────────────────────

    public class AdminLoginRequest             { public string Username    { get; set; } public string Password    { get; set; } }
    public class CreateEndpointRequest         { public string Name        { get; set; } public string BaseUrl     { get; set; } }
    public class ChangePasswordRequest         { public string NewPassword { get; set; } }
    public class UpdateUserRoleRequest         { public string Role        { get; set; } }
    public class AssignEndpointManagerRequest  { public string UserId      { get; set; } }
    public class CreateAdminUserRequest        { public string Email       { get; set; } public string Role { get; set; } }
    public class RecordDocAccessRequest
    {
        public string EndpointId   { get; set; }
        public string EndpointName { get; set; }
        public string UserName     { get; set; }
        public string ServiceHref  { get; set; }
        public string ServiceName  { get; set; }
    }

    public class ContentData
    {
        [JsonProperty("sections")]
        public List<ContentSection> Sections { get; set; } = new List<ContentSection>();
    }

    public class ContentSection
    {
        [JsonProperty("id")]             public string Id        { get; set; }
        [JsonProperty("title")]          public string Title     { get; set; }
        [JsonProperty("items")]          public List<ContentItem> Items { get; set; } = new List<ContentItem>();
        [JsonProperty("status")]         public string Status    { get; set; } = "approved";
        // Placement values: null/"" or "top" → top of page (Getting Started).
        // "before:<href>" / "after:<href>"           → outside the service block (legacy).
        // "before-params:<href>" / "after-params:<href>" → inside the service block, around Request Parameters.
        [JsonProperty("placement")]      public string Placement { get; set; }
        // When true, the docs page renders this section's items inline (no <h2> heading,
        // no collapsible wrapper). Lets a maintainer drop a single paragraph or code block
        // before/after Request Parameters without forcing a heading on top of it.
        [JsonProperty("headless", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Headless { get; set; }
        // Per-endpoint scoping. null/empty → shared across every endpoint
        // (legacy behaviour; the seed Introduction/Getting Started entries
        // stay visible to all tenants). Non-null → the section is only visible
        // to users authenticated into the matching endpoint.
        [JsonProperty("endpointId", NullValueHandling = NullValueHandling.Ignore)]
        public string EndpointId { get; set; }
        // When a Project Manager edits an *already-approved* section, the proposed
        // changes are parked here while the approved fields above stay untouched —
        // so Index.html keeps showing the approved version until an Executive
        // either approves (commits these into the section) or rejects (clears them).
        // Null when there is no pending edit.
        [JsonProperty("pendingChanges", NullValueHandling = NullValueHandling.Ignore)]
        public PendingSectionChanges PendingChanges { get; set; }
    }

    public class PendingSectionChanges
    {
        [JsonProperty("title")]      public string Title      { get; set; }
        [JsonProperty("items")]      public List<ContentItem> Items { get; set; } = new List<ContentItem>();
        [JsonProperty("placement")]  public string Placement  { get; set; }
        [JsonProperty("headless")]   public bool   Headless   { get; set; }
        // Allow the editor to also re-scope a section between endpoints / shared.
        [JsonProperty("endpointId", NullValueHandling = NullValueHandling.Ignore)]
        public string EndpointId { get; set; }
        [JsonProperty("editedAt")]   public DateTime EditedAt { get; set; }
        [JsonProperty("editedBy")]   public string EditedBy   { get; set; } // user email at time of edit
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
        [JsonProperty("status")]      public string Status      { get; set; } = "approved";
    }

    // One glossary field placed onto a service's request.
    //   Attr == null/empty  → a plain element:  <Element>value</Element>
    //   Attr set            → an attribute of a shared element, so sibling
    //                          assignments merge into <Element A="" B="" />.
    public class ServiceFieldAssignment
    {
        [JsonProperty("field")]    public string Field    { get; set; }
        [JsonProperty("element")]  public string Element  { get; set; }
        [JsonProperty("attr", NullValueHandling = NullValueHandling.Ignore)]
        public string Attr { get; set; }
        [JsonProperty("type")]     public string Type     { get; set; } = "string";
        [JsonProperty("required")] public bool   Required { get; set; }
    }

    public class ServiceFieldAssignmentRequest
    {
        public string Field    { get; set; }
        public string Href     { get; set; }
        public string Element  { get; set; }
        // Simple element only: the element's own value type. Ignored when Attributes is non-empty.
        public string Type     { get; set; }
        public bool   Required { get; set; }
        // When non-empty, the element carries these attributes → <Element A="" B="" />.
        // When empty/null, it is a plain element → <Element>value</Element>.
        public List<AttrSpec> Attributes { get; set; }
    }

    public class AttrSpec
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public class EndpointVisibilityRequest { public List<string> HiddenServices { get; set; } }

    public class ReorderSectionsRequest
    {
        [JsonProperty("orderedIds")]
        public List<string> OrderedIds { get; set; }
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
