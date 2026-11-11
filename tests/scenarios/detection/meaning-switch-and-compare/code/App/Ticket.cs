namespace App
{
    public class Ticket { public int State { get; set; } }
    public class Board
    {
        public string Column(Ticket t)
        {
            switch (t.State)
            {
                case 7: return "Blocked";
                case 8: return "Done";
                default: return "Open";
            }
        }
    }
    public class Alerts
    {
        public bool Escalate(Ticket t) => t.State == 7;
    }
    public class Reports
    {
        public int Weight(Ticket t) => t.State switch { 7 => 3, 8 => 0, _ => 1 };
    }
}
