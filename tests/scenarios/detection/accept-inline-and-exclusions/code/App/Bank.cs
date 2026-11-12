namespace App
{
    public class Bank
    {
        public string Amount(string line)
        {
            var parts = line.Split('|');
            // connascence: accept - column order fixed by the bank's file spec
            var amount = parts[3];
            var payee = parts[5];
            return payee + amount;
        }

        public void Wait()
        {
            System.Threading.Thread.Sleep(250); // connascence: accept - bank rate limit is documented as 4 calls a second
        }
    }
}
