using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.Helpers
{
    public static class StringExtensions
    {
        public static string SplitLast(this string s, string separator, bool ignoreEmpty = false)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }

            string[] array = s.Split(separator, ignoreEmpty);
            if (array.Length != 0)
            {
                return array[array.Length - 1];
            }

            return string.Empty;
        }

        public static string SplitBeforeLast(this string s, string separator)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }

            int num = s.LastIndexOf(separator, StringComparison.Ordinal);
            if (num <= 0)
            {
                return s;
            }

            return s.Substring(0, num);
        }

        public static string[] Split(this string s, string separator, bool ignoreEmpty = false)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            string[] array = s.Split(new string[1] { separator }, StringSplitOptions.None);
            if (!ignoreEmpty)
            {
                return array;
            }

            List<string> list = new List<string>();
            string[] array2 = array;
            foreach (string text in array2)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    list.Add(text);
                }
            }

            return list.ToArray();
        }
    }
}
