using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fsync
{

    static class Extensions
    {
        public static T Get<T>(this Dictionary<string, string> dic, string key, T defaultValue = default(T))
        {
            var value = dic.TryGetValue(key);
            if (value.IsNullOrEmpty())
                return defaultValue;
            return (T)Convert.ChangeType(value, typeof(T));
        }
    }
}
