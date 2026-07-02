using Documentation.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Http;

namespace Documentation.Controllers
{
    public class UserController : ApiController
    {
        private static readonly string UsersFile = "~/Models/users.json";

        [HttpPost]
        [Route("api/users/register")]
        public IHttpActionResult Register([FromBody] RegisterRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Email))
                return BadRequest("Email is required.");

            var email = req.Email.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(email, @"^[^@\s]+@cyberlogic\.gr$"))
                return BadRequest("Only @cyberlogic.gr email addresses are allowed to register.");

            var validRoles = new[] { "Executive", "ProjectManager", "Developer" };
            var role = string.IsNullOrWhiteSpace(req.Role) ? "ProjectManager" : req.Role.Trim();
            if (Array.IndexOf(validRoles, role) < 0)
                return BadRequest("Invalid role. Must be Executive, ProjectManager, or Developer.");

            var data = LoadUsers();
            if (data.Users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("A registration request for this email already exists.");

            var newUser = new UserEntry
            {
                Id        = Guid.NewGuid().ToString("N"),
                Email     = email,
                Role      = role,
                Status    = "pending",
                MustChangePassword = false,
                CreatedAt = DateTime.UtcNow
            };
            data.Users.Add(newUser);
            SaveUsers(data);

            // Internal log — registration is a "Register" action attributed to the user themselves.
            LogStore.AppendInternal(new InternalLogEntry
            {
                Timestamp  = DateTime.UtcNow,
                UserEmail  = newUser.Email,
                UserRole   = newUser.Role,
                Action     = "Register",
                Category   = "User",
                TargetId   = newUser.Id,
                TargetName = newUser.Email,
                Details    = "status=pending"
            });
            return Ok(new { message = "Registration submitted. Pending admin approval." });
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
    }

    public class RegisterRequest { public string Email { get; set; } public string Role { get; set; } }
}
