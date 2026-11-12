namespace App
{
    public class Worker
    {
        public void Run()
        {
            System.Threading.Thread.Sleep(500);
            Step();
            System.Threading.Thread.Sleep(500);
        }

        void Step() { }
    }
}
