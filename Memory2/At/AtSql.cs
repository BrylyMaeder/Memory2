// File: Memory2/At/AtSql.cs
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Memory2.At
{
    public static class AtSql
    {
        public static IReadOnlyList<(string Axis, object Value)> ParseAtClause(string sql)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));

            string s = sql.Trim();
            if (s.StartsWith("AT ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(3).Trim();
            else
                throw new FormatException("AT SQL must start with: AT <Axis> = <literal> [AND ...]");

            if (s.Length == 0) throw new FormatException("AT clause is empty.");

            var parts = SplitAndParts(s);
            var terms = new List<(string Axis, object Value)>(parts.Count);

            for (int i = 0; i < parts.Count; i++)
                terms.Add(ParseEq(parts[i]));

            if (terms.Count == 0) throw new FormatException("AT requires at least one term.");
            return terms;
        }

        private static List<string> SplitAndParts(string s)
        {
            var parts = new List<string>();
            int i = 0;
            int start = 0;
            bool inStr = false;

            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\'') inStr = !inStr;

                if (!inStr && i + 3 <= s.Length)
                {
                    if ((s[i] == 'A' || s[i] == 'a') &&
                        (s[i + 1] == 'N' || s[i + 1] == 'n') &&
                        (s[i + 2] == 'D' || s[i + 2] == 'd'))
                    {
                        bool leftOk = (i == 0) || char.IsWhiteSpace(s[i - 1]);
                        bool rightOk = (i + 3 >= s.Length) || (i + 3 < s.Length && char.IsWhiteSpace(s[i + 3]));
                        if (leftOk && rightOk)
                        {
                            parts.Add(s.Substring(start, i - start).Trim());
                            i += 3;
                            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                            start = i;
                            continue;
                        }
                    }
                }
                i++;
            }

            if (start < s.Length) parts.Add(s.Substring(start).Trim());
            parts.RemoveAll(p => p.Length == 0);
            return parts;
        }

        private static (string Axis, object Value) ParseEq(string part)
        {
            string p = (part ?? "").Trim();
            if (p.Length == 0) throw new FormatException("Empty AT term.");

            int idx = p.IndexOf('=');
            if (idx < 0) throw new FormatException("AT terms must be: <Axis> = <literal>");

            string axis = p.Substring(0, idx).Trim();
            string rhs = p.Substring(idx + 1).Trim();

            if (axis.Length == 0) throw new FormatException("AT axis is empty.");
            if (rhs.Length == 0) throw new FormatException("AT literal is empty.");

            object lit = ParseLiteral(rhs);
            return (axis, lit);
        }

        private static object ParseLiteral(string raw)
        {
            if (raw.Length >= 2 && raw[0] == '\'' && raw[raw.Length - 1] == '\'')
                return raw.Substring(1, raw.Length - 2);

            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

            long v;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;

            return raw;
        }
    }
}
