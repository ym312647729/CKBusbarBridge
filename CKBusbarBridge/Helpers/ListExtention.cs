using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.Helpers
{
    public static class ListExtention
    {
        public static void ForEach<T>(this IEnumerable<T> list, Action<T> function)
        {
            foreach (T item in list)
            {
                function(item);
            }
        }
    }
}
