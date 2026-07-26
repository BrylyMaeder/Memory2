// File: Memory2/Query/SqlAst.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Memory2.Query.Sql
{
    internal abstract class SqlExpr
    {
        public abstract void ToString(StringBuilder sb);

        public override string ToString()
        {
            var sb = new StringBuilder();
            ToString(sb);
            return sb.ToString();
        }
    }

    internal sealed class SqlBinary : SqlExpr
    {
        public readonly string Op; // "AND" / "OR"
        public readonly SqlExpr Left;
        public readonly SqlExpr Right;

        public SqlBinary(string op, SqlExpr left, SqlExpr right)
        {
            Op = op ?? "";
            Left = left;
            Right = right;
        }

        public override void ToString(StringBuilder sb)
        {
            sb.Append("(");
            Left?.ToString(sb);
            sb.Append(" ").Append(Op).Append(" ");
            Right?.ToString(sb);
            sb.Append(")");
        }
    }

    internal sealed class SqlNot : SqlExpr
    {
        public readonly SqlExpr Inner;
        public SqlNot(SqlExpr inner) { Inner = inner; }

        public override void ToString(StringBuilder sb)
        {
            sb.Append("(NOT ");
            Inner?.ToString(sb);
            sb.Append(")");
        }
    }

    internal sealed class SqlCompare : SqlExpr
    {
        public readonly string Ident;
        public readonly string Op; // = != < <= > >=
        public readonly object Value;

        public SqlCompare(string ident, string op, object value)
        {
            Ident = ident ?? "";
            Op = op ?? "";
            Value = value;
        }

        public override void ToString(StringBuilder sb)
        {
            sb.Append(Ident).Append(" ").Append(Op).Append(" ");
            AppendLit(sb, Value);
        }

        internal static void AppendLit(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("NULL"); return; }
            if (v is bool b) { sb.Append(b ? "TRUE" : "FALSE"); return; }
            if (v is string s)
            {
                sb.Append("'").Append(s.Replace("'", "''")).Append("'");
                return;
            }
            if (v is long || v is int || v is short || v is byte || v is ulong || v is uint || v is ushort)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            sb.Append("'").Append(Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("'", "''")).Append("'");
        }
    }

    internal sealed class SqlBetween : SqlExpr
    {
        public readonly string Ident;
        public readonly object Lo;
        public readonly object Hi;

        public SqlBetween(string ident, object lo, object hi)
        {
            Ident = ident ?? "";
            Lo = lo;
            Hi = hi;
        }

        public override void ToString(StringBuilder sb)
        {
            sb.Append(Ident).Append(" BETWEEN ");
            SqlCompare.AppendLit(sb, Lo);
            sb.Append(" AND ");
            SqlCompare.AppendLit(sb, Hi);
        }
    }

    internal sealed class SqlInList : SqlExpr
    {
        public readonly string Ident;
        public readonly object[] Values;

        public SqlInList(string ident, object[] values)
        {
            Ident = ident ?? "";
            Values = values ?? Array.Empty<object>();
        }

        public override void ToString(StringBuilder sb)
        {
            sb.Append(Ident).Append(" IN (");
            for (int i = 0; i < Values.Length; i++)
            {
                if (i != 0) sb.Append(", ");
                SqlCompare.AppendLit(sb, Values[i]);
            }
            sb.Append(")");
        }
    }

    internal sealed class SqlIsNull : SqlExpr
    {
        public readonly string Ident;
        public readonly bool Negated; // IS NOT NULL

        public SqlIsNull(string ident, bool negated)
        {
            Ident = ident ?? "";
            Negated = negated;
        }

        public override void ToString(StringBuilder sb)
        {
            sb.Append(Ident).Append(Negated ? " IS NOT NULL" : " IS NULL");
        }
    }
}
