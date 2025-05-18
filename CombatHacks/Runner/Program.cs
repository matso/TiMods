using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CombatHacks;

namespace Runner
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(new Formation.Pusher(new int[] { 0, 0, 9, 0, 0 }).Calc().ToString());
            Console.WriteLine(new Formation.Pusher(new int[] { 9, 0, 0, 0, 0 }).Calc().ToString());
            Console.WriteLine(new Formation.Pusher(new int[] { 0, 0, 0, 0, 9 }).Calc().ToString());
            Console.ReadLine();
        }
    }

}
