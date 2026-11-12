using System.Collections.Generic;
namespace Jobs
{
    public class Mailer
    {
        readonly IDictionary<string, string> _config;
        public Mailer(IDictionary<string, string> config) { _config = config; }
        public bool IsAdmin(Web.User u) => u.Role == "admin";
        public string Host() => _config["Smtp:Host"];
    }
}
