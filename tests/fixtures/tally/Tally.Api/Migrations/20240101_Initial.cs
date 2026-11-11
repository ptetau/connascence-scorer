namespace Tally.Api.Migrations
{
    // Migrations are excluded from analysis. These literals must not be reported.
    public class Initial
    {
        public void Up(System.Collections.Generic.List<string> sql)
        {
            sql.Add("INSERT INTO statuses VALUES (3, 'Sent')");
            var status = 3;
            if (status == 3) sql.Add("-- seed");
            if (status == 3) sql.Add("-- seed again");
        }
    }
}
