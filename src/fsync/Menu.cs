using Corex.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fsync
{
    class Menu
    {
        public List<MenuItem> Items { get; set; }

        public void Help()
        {
            Items.ForEach(t => Console.WriteLine("{0}", t.Name));
        }
        public void Start()
        {
            Items.Add(new MenuItem { Name = "Help", Action = Help, Aliases={"?"} });
            while (true)
            {
                Console.Write(">");
                var s = System.Console.ReadLine();
                if (s.IsNullOrEmpty())
                    continue;
                if (s == "q")
                    return;
                var list = Items.Where(t => Matches(t, s)).ToList();
                if (list.Count == 0)
                    Console.WriteLine("No such command");
                else if (list.Count > 1)
                    Console.WriteLine("Did you mean: {0}", list.Select(t=>t.Name).StringJoin(" / "));
                else
                {
                    var mi = list[0];// ParseHelper.TryInt(s);
                    //var mi = Items[x.Value];
                    try
                    {
                        mi.Action();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }

        private bool Matches(MenuItem mi, string cmd)
        {
            if (mi.Name.EqualsIgnoreCase(cmd))
                return true;
            var alias = new String(mi.Name.Where(ch => Char.IsUpper(ch) || Char.IsDigit(ch)).ToArray());
            if (alias.EqualsIgnoreCase(cmd))
                return true;
            if (mi.Aliases != null && mi.Aliases.Where(t => t.EqualsIgnoreCase(cmd)).FirstOrDefault() != null)
                return true;
            return false;
        }

    }

    class MenuItem
    {
        public MenuItem()
        {
            Aliases = new List<string>();
        }
        public List<string> Aliases { get; set; }
        public string Name { get; set; }
        public Action Action { get; set; }
    }

}
