namespace App
{
    public class FakeClock
    {
        public int Status;
        public bool Ticks() => Status == 7;
    }
}
