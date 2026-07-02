using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Documentation.Models
{
    public class UsersData
    {
        [JsonProperty("users")]
        public List<UserEntry> Users { get; set; } = new List<UserEntry>();
    }

    public class UserEntry
    {
        [JsonProperty("id")]                 public string    Id                 { get; set; }
        [JsonProperty("email")]              public string    Email              { get; set; }
        [JsonProperty("passwordHash")]       public string    PasswordHash       { get; set; }
        [JsonProperty("status")]             public string    Status             { get; set; }
        [JsonProperty("role")]               public string    Role               { get; set; } = "ProjectManager";
        [JsonProperty("mustChangePassword")] public bool      MustChangePassword { get; set; }
        [JsonProperty("createdAt")]          public DateTime  CreatedAt          { get; set; }
        [JsonProperty("approvedAt")]         public DateTime? ApprovedAt         { get; set; }
    }
}
