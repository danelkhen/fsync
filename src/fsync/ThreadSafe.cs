using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fsync
{
    class ThreadSafe
    {
        public static ThreadSafe<T> Create<T>(T value)
        {
            return new ThreadSafe<T>(value);
        }
    }
    class ThreadSafe<T>
    {
        private T Value;
        public ThreadSafe(T value)
        {
            Value = value;
        }
        object Entrance = new object();
        public void Do(Action<T> action)
        {
            lock (Entrance)
            {
                action(Value);
            }
        }
        public R Get<R>(Func<T, R> func)
        {
            lock (Entrance)
            {
                return func(Value);
            }
        }
    }
}
