// File: Memory2/Query/SqlTokenizer.cs
// .NET Standard 2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Memory2.Query.Sql
{
    internal enum TokKind
    {
        Eof = 0,
        Ident,
        Number,
        String,
        Comma,
        LParen,
        RParen,

        Eq,
        Neq,
        Lt,
        Lte,
        Gt,
        Gte,

        And,
        Or,
        Not,
        Between,
        In,
        Is,
        Null,
        True,
        False,

        Select,
        From,
        Where,
        Order,
        By,
        Limit,
        Offset,
        As
    }

    internal struct Tok
    {
        public TokKind Kind;
        public string Text;
        public object Value;

        public Tok(TokKind k, string text, object value = null)
        {
            Kind = k;
            Text = text ?? "";
            Value = value;
        }

        public override string ToString() => $"{Kind}:{Text}";
    }

    internal sealed class SqlTokenizer
    {
        private readonly string _s;
        private int _p;

        public SqlTokenizer(string s)
        {
            _s = s ?? "";
            _p = 0;
        }

        public List<Tok> Tokenize()
        {
            var toks = new List<Tok>(64);
            Tok t;
            do
            {
                t = Next();
                toks.Add(t);
            } while (t.Kind != TokKind.Eof);
            return toks;
        }

        private Tok Next()
        {
            SkipWs();
            if (_p >= _s.Length) return new Tok(TokKind.Eof, "");

            char c = _s[_p];

            // punctuation
            if (c == ',') { _p++; return new Tok(TokKind.Comma, ","); }
            if (c == '(') { _p++; return new Tok(TokKind.LParen, "("); }
            if (c == ')') { _p++; return new Tok(TokKind.RParen, ")"); }

            // operators
            if (c == '=')
            {
                _p++;
                return new Tok(TokKind.Eq, "=");
            }
            if (c == '!' && Peek('='))
            {
                _p += 2;
                return new Tok(TokKind.Neq, "!=");
            }
            if (c == '<')
            {
                if (Peek('=')) { _p += 2; return new Tok(TokKind.Lte, "<="); }
                if (Peek('>')) { _p += 2; return new Tok(TokKind.Neq, "<>"); }
                _p++;
                return new Tok(TokKind.Lt, "<");
            }
            if (c == '>')
            {
                if (Peek('=')) { _p += 2; return new Tok(TokKind.Gte, ">="); }
                _p++;
                return new Tok(TokKind.Gt, ">");
            }

            // string literal: '...'
            if (c == '\'')
            {
                _p++; // skip opening
                var sb = new StringBuilder();
                while (_p < _s.Length)
                {
                    char ch = _s[_p++];
                    if (ch == '\'')
                    {
                        // doubled quote -> escaped
                        if (_p < _s.Length && _s[_p] == '\'')
                        {
                            _p++;
                            sb.Append('\'');
                            continue;
                        }
                        break;
                    }
                    sb.Append(ch);
                }
                string str = sb.ToString();
                return new Tok(TokKind.String, str, str);
            }

            // number: [-]?\d+
            if (c == '-' || char.IsDigit(c))
            {
                int start = _p;
                if (c == '-') _p++;
                int digStart = _p;
                while (_p < _s.Length && char.IsDigit(_s[_p])) _p++;
                if (_p == digStart)
                {
                    // lone '-', treat as ident-ish
                    _p = start + 1;
                    return new Tok(TokKind.Ident, "-");
                }
                string raw = _s.Substring(start, _p - start);
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
                    return new Tok(TokKind.Number, raw, lv);
                return new Tok(TokKind.Number, raw, 0L);
            }

            // identifier / keyword
            if (IsIdentStart(c))
            {
                int start = _p++;
                while (_p < _s.Length && IsIdentPart(_s[_p])) _p++;
                string ident = _s.Substring(start, _p - start);

                var k = Keyword(ident);
                if (k != TokKind.Ident)
                {
                    // canonical values for booleans/null
                    if (k == TokKind.True) return new Tok(k, ident, true);
                    if (k == TokKind.False) return new Tok(k, ident, false);
                    if (k == TokKind.Null) return new Tok(k, ident, null);
                    return new Tok(k, ident);
                }
                return new Tok(TokKind.Ident, ident, ident);
            }

            // unknown char -> consume as ident to avoid infinite loops
            _p++;
            return new Tok(TokKind.Ident, c.ToString(), c.ToString());
        }

        private bool Peek(char next)
        {
            return (_p + 1 < _s.Length) && _s[_p + 1] == next;
        }

        private void SkipWs()
        {
            while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '@';

        private static TokKind Keyword(string s)
        {
            if (s == null) return TokKind.Ident;
            switch (s.ToUpperInvariant())
            {
                case "AND": return TokKind.And;
                case "OR": return TokKind.Or;
                case "NOT": return TokKind.Not;
                case "BETWEEN": return TokKind.Between;
                case "IN": return TokKind.In;
                case "IS": return TokKind.Is;
                case "NULL": return TokKind.Null;
                case "TRUE": return TokKind.True;
                case "FALSE": return TokKind.False;

                case "SELECT": return TokKind.Select;
                case "FROM": return TokKind.From;
                case "WHERE": return TokKind.Where;
                case "ORDER": return TokKind.Order;
                case "BY": return TokKind.By;
                case "LIMIT": return TokKind.Limit;
                case "OFFSET": return TokKind.Offset;
                case "AS": return TokKind.As;

                default: return TokKind.Ident;
            }
        }
    }
}
