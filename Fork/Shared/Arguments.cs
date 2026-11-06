using System.Collections.Generic;
using System.Text;

namespace WandEnhancer.Fork
{
    internal static class Arguments
    {
        public static string Join(IEnumerable<string> values)
        {
            var result = new List<string>();
            foreach (string value in values) result.Add(Quote(value));
            return string.Join(" ", result);
        }

        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
