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

            var data = LoadUsers();
            if (data.Users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("A registration request for this email already exists.");

            data.Users.Add(new UserEntry
            {
                Id        = Guid.NewGuid().ToString("N"),
                Email     = email,
                Status    = "pending",
                MustChangePassword = false,
                CreatedAt = DateTime.UtcNow
            });
            SaveUsers(data);
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

    public class RegisterRequest { public string Email { get; set; } }
}
