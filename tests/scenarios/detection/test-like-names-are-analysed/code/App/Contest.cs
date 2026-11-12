namespace App
{
    public class Contest
    {
        public int Status;
        public bool IsOpen() => Status == 7;
    }
}
