using System.Collections.Generic;
namespace Web
{
    public class User { public string Role { get; set; } }
    public class Admin
    {
        readonly IDictionary<string, string> _config;
        public Admin(IDictionary<string, string> config) { _config = config; }
        public bool CanDelete(User u) => u.Role == "admin";
        public string SmtpHost() => _config["Smtp:Host"];
    }
}
