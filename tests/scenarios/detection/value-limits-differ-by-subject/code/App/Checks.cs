using System.Collections.Generic;
namespace App
{
    public class Checks
    {
        public bool NameOk(string name) => name.Length <= 64;
        public bool CartOk(List<int> items) => items.Count <= 64;
        public bool AgeOk(int age) => age >= 18;
    }
    public class Signup
    {
        public bool Adult(int age) => age >= 18;
    }
}
