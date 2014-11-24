using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fsync
{
    class FakeConsole
    {
        public Action<string> DoWriteLine { get; set; }
        public void WriteLine(object obj)
        {
            OnWriteLine(obj + "\r\n");
        }
        public void WriteLine(string s, params object[] args)
        {
            if (args.IsNullOrEmpty())
                OnWriteLine(s);
            else
                OnWriteLine(String.Format(s, args));
        }

        protected virtual void OnWriteLine(string s)
        {
            if (DoWriteLine != null)
                DoWriteLine(s);
        }
    }


    class MultiConsole : FakeConsole
    {
        public void Add(FakeConsole console)
        {
            console.DoWriteLine += OnWriteLine;
        }
    }
}
