// File: Memory2/Query/SqlParser.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;

namespace Memory2.Query.Sql
{
    internal sealed class SqlParser
    {
        private readonly List<Tok> _toks;
        private int _p;

        public SqlParser(List<Tok> toks)
        {
            _toks = toks ?? new List<Tok>();
            _p = 0;
        }

        public SqlExpr ParseWhereOrWholeStatement(out int? limit)
        {
            limit = null;

            // Allow:
            //   WHERE <expr>
            //   SELECT ... FROM ... WHERE <expr> [LIMIT n]
            //   <expr> (treated as where)
            SkipSelectFromJunk();

            if (Match(TokKind.Where))
            {
                var e = ParseExpr();
                limit = ParseOptionalLimitTail();
                Expect(TokKind.Eof);
                return e;
            }

            // maybe starts directly with expression
            var expr = ParseExpr();
            limit = ParseOptionalLimitTail();
            Expect(TokKind.Eof);
            return expr;
        }

        private void SkipSelectFromJunk()
        {
            // Very tolerant: if starts with SELECT, skip tokens until WHERE or Eof.
            if (!PeekIs(TokKind.Select)) return;

            while (!PeekIs(TokKind.Eof) && !PeekIs(TokKind.Where))
            {
                _p++;
            }
        }

        private int? ParseOptionalLimitTail()
        {
            // Skip ORDER BY ...
            if (Match(TokKind.Order))
            {
                Match(TokKind.By);
                while (!PeekIs(TokKind.Eof) && !PeekIs(TokKind.Limit) && !PeekIs(TokKind.Offset))
                    _p++;
            }

            if (Match(TokKind.Limit))
            {
                var nTok = ExpectAny(TokKind.Number);
                long lv = (nTok.Value is long l) ? l : 0L;
                if (lv < 0) lv = 0;
                if (lv > int.MaxValue) lv = int.MaxValue;
                return (int)lv;
            }
            return null;
        }

        // Grammar:
        // expr := orExpr
        // orExpr := andExpr (OR andExpr)*
        // andExpr := unary (AND unary)*
        // unary := NOT unary | primary
        // primary := '(' expr ')' | predicate
        // predicate :=
        //   ident IS [NOT] NULL
        //   ident BETWEEN lit AND lit
        //   ident IN '(' lit (',' lit)* ')'
        //   ident op lit
        private SqlExpr ParseExpr() => ParseOr();

        private SqlExpr ParseOr()
        {
            var left = ParseAnd();
            while (Match(TokKind.Or))
            {
                var right = ParseAnd();
                left = new SqlBinary("OR", left, right);
            }
            return left;
        }

        private SqlExpr ParseAnd()
        {
            var left = ParseUnary();
            while (Match(TokKind.And))
            {
                var right = ParseUnary();
                left = new SqlBinary("AND", left, right);
            }
            return left;
        }

        private SqlExpr ParseUnary()
        {
            if (Match(TokKind.Not))
                return new SqlNot(ParseUnary());
            return ParsePrimary();
        }

        private SqlExpr ParsePrimary()
        {
            if (Match(TokKind.LParen))
            {
                var e = ParseExpr();
                Expect(TokKind.RParen);
                return e;
            }
            return ParsePredicate();
        }

        private SqlExpr ParsePredicate()
        {
            var idTok = ExpectAny(TokKind.Ident);
            string ident = idTok.Text;

            // IS NULL / IS NOT NULL
            if (Match(TokKind.Is))
            {
                bool not = Match(TokKind.Not);
                Expect(TokKind.Null);
                return new SqlIsNull(ident, not);
            }

            // BETWEEN
            if (Match(TokKind.Between))
            {
                object lo = ParseLiteral();
                Expect(TokKind.And);
                object hi = ParseLiteral();
                return new SqlBetween(ident, lo, hi);
            }

            // IN (...)
            if (Match(TokKind.In))
            {
                Expect(TokKind.LParen);
                var vals = new List<object>();
                if (!PeekIs(TokKind.RParen))
                {
                    vals.Add(ParseLiteral());
                    while (Match(TokKind.Comma))
                        vals.Add(ParseLiteral());
                }
                Expect(TokKind.RParen);
                return new SqlInList(ident, vals.ToArray());
            }

            // Compare op
            string op;
            if (Match(TokKind.Eq)) op = "=";
            else if (Match(TokKind.Neq)) op = "!=";
            else if (Match(TokKind.Lte)) op = "<=";
            else if (Match(TokKind.Gte)) op = ">=";
            else if (Match(TokKind.Lt)) op = "<";
            else if (Match(TokKind.Gt)) op = ">";
            else throw new FormatException("Expected operator after identifier: " + ident);

            object lit = ParseLiteral();
            return new SqlCompare(ident, op, lit);
        }

        private object ParseLiteral()
        {
            if (Match(TokKind.Null)) return null;
            if (Match(TokKind.True)) return true;
            if (Match(TokKind.False)) return false;

            if (PeekIs(TokKind.String))
            {
                var t = _toks[_p++];
                return (string)t.Value;
            }
            if (PeekIs(TokKind.Number))
            {
                var t = _toks[_p++];
                return (t.Value is long l) ? l : 0L;
            }
            if (PeekIs(TokKind.Ident))
            {
                // allow bare identifiers as string literals
                var t = _toks[_p++];
                return t.Text;
            }

            throw new FormatException("Expected literal.");
        }

        private bool PeekIs(TokKind k) => _p < _toks.Count && _toks[_p].Kind == k;

        private bool Match(TokKind k)
        {
            if (PeekIs(k))
            {
                _p++;
                return true;
            }
            return false;
        }

        private void Expect(TokKind k)
        {
            if (!Match(k))
                throw new FormatException("Expected token: " + k);
        }

        private Tok ExpectAny(params TokKind[] kinds)
        {
            if (_p >= _toks.Count) throw new FormatException("Unexpected end.");
            var t = _toks[_p];
            for (int i = 0; i < kinds.Length; i++)
            {
                if (t.Kind == kinds[i])
                {
                    _p++;
                    return t;
                }
            }
            throw new FormatException("Unexpected token: " + t.Kind);
        }
    }
}
