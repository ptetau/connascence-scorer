namespace Web
{
    public class Login
    {
        public void SignIn(string user) { Core.Session.CurrentUser = user; }
        public int Hits(string page) => Core.Session.Counters[page];
        public void Count(string page) { Core.Session.Counters[page] = Hits(page) + 1; }
        public string Name() => Core.Session.Product;
    }
}
