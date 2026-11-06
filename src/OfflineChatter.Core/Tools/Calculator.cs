using System;
using System.Globalization;
using System.Numerics;

namespace OfflineChatter.Core.Tools;

/// <summary>
/// Калькулятор для модели: точная рациональная арифметика (BigInteger-дроби) для + − × ÷ и целых степеней,
/// с переходом на double для функций (sqrt, sin, ln…). Маленькие модели плохо считают — этот инструмент считает точно.
/// </summary>
public static class Calculator
{
    public readonly struct Number
    {
        public Number(BigInteger num, BigInteger den)
        {
            if (den.IsZero) throw new DivideByZeroException();
            if (den.Sign < 0) { num = -num; den = -den; }
            var g = BigInteger.GreatestCommonDivisor(num, den);
            if (!g.IsZero && !g.IsOne) { num /= g; den /= g; }
            Num = num;
            Den = den;
            Approx = null;
        }

        public Number(double value)
        {
            Num = BigInteger.Zero;
            Den = BigInteger.One;
            Approx = value;
        }

        public BigInteger Num { get; }
        public BigInteger Den { get; }
        /// <summary>Не null — число неточное (результат функции).</summary>
        public double? Approx { get; }
        public bool IsExact => Approx == null;
        public double Value => Approx ?? (double)Num / (double)Den;

        public static Number Int(BigInteger v) => new(v, BigInteger.One);

        public override string ToString()
        {
            if (!IsExact) return Value.ToString("G15", CultureInfo.InvariantCulture);
            if (Den.IsOne) return Num.ToString(CultureInfo.InvariantCulture);
            var dec = Value.ToString("G15", CultureInfo.InvariantCulture);
            return $"{Num}/{Den} ≈ {dec}";
        }
    }

    public static Number Evaluate(string expression) => new Parser(expression).Parse();

    private sealed class Parser
    {
        private readonly string _s;
        private int _p;

        // «2,5» (десятичная запятая) → 2.5, но «log(8, 2)» — разделитель аргументов → «;»
        public Parser(string s) => _s = System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                s.Replace("×", "*").Replace("·", "*").Replace("÷", "/").Replace("−", "-").Replace("**", "^"),
                @"(?<=\d),(?=\d)", "."),
            ",", ";");

        public Number Parse()
        {
            var v = Expr();
            Skip();
            if (_p < _s.Length) throw new FormatException($"Unexpected '{_s[_p]}' at {_p}");
            return v;
        }

        private void Skip()
        {
            while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
        }

        private bool Eat(char c)
        {
            Skip();
            if (_p < _s.Length && _s[_p] == c) { _p++; return true; }
            return false;
        }

        private Number Expr()
        {
            var v = Term();
            while (true)
            {
                if (Eat('+')) v = Add(v, Term());
                else if (Eat('-')) v = Sub(v, Term());
                else return v;
            }
        }

        private Number Term()
        {
            var v = Power();
            while (true)
            {
                if (Eat('*')) v = Mul(v, Power());
                else if (Eat('/')) v = Div(v, Power());
                else if (Eat('%')) v = Mod(v, Power());
                else return v;
            }
        }

        private Number Power()
        {
            var b = Unary();
            if (Eat('^')) return Pow(b, Power()); // правоассоциативно
            return b;
        }

        private Number Unary()
        {
            if (Eat('-')) return Sub(Number.Int(0), Unary());
            if (Eat('+')) return Unary();
            return Postfix(Primary());
        }

        private Number Postfix(Number v)
        {
            while (Eat('!'))
            {
                if (!v.IsExact || !v.Den.IsOne || v.Num.Sign < 0 || v.Num > 1000) throw new ArgumentException("factorial needs an integer 0..1000");
                var r = BigInteger.One;
                for (var i = 2; i <= (int)v.Num; i++) r *= i;
                v = Number.Int(r);
            }
            return v;
        }

        private Number Primary()
        {
            Skip();
            if (Eat('('))
            {
                var v = Expr();
                if (!Eat(')')) throw new FormatException("Missing ')'");
                return v;
            }
            if (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.'))
            {
                var start = _p;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
                if (_p < _s.Length && (_s[_p] == 'e' || _s[_p] == 'E') && _p + 1 < _s.Length && (char.IsDigit(_s[_p + 1]) || _s[_p + 1] is '-' or '+'))
                {
                    _p += 2;
                    while (_p < _s.Length && char.IsDigit(_s[_p])) _p++;
                    return new Number(double.Parse(_s.Substring(start, _p - start), CultureInfo.InvariantCulture));
                }
                return FromDecimal(_s.Substring(start, _p - start));
            }
            if (_p < _s.Length && char.IsLetter(_s[_p]))
            {
                var start = _p;
                while (_p < _s.Length && char.IsLetterOrDigit(_s[_p])) _p++;
                var name = _s.Substring(start, _p - start).ToLowerInvariant();
                switch (name)
                {
                    case "pi": return new Number(Math.PI);
                    case "e": return new Number(Math.E);
                }
                if (!Eat('(')) throw new FormatException("Unknown name: " + name);
                var a = Expr();
                Number? b = null;
                if (Eat(',') || Eat(';')) b = Expr();
                if (!Eat(')')) throw new FormatException("Missing ')'");
                return Function(name, a, b);
            }
            throw new FormatException("Unexpected end of expression");
        }

        private static Number FromDecimal(string s)
        {
            var dot = s.IndexOf('.');
            if (dot < 0) return Number.Int(BigInteger.Parse(s, CultureInfo.InvariantCulture));
            var digits = s.Replace(".", string.Empty);
            if (digits.Length == 0) throw new FormatException("Bad number");
            var den = BigInteger.Pow(10, s.Length - dot - 1);
            return new Number(BigInteger.Parse(digits, CultureInfo.InvariantCulture), den);
        }

        private static Number Function(string name, Number a, Number? b)
        {
            var x = a.Value;
            return name switch
            {
                "sqrt" => ExactRoot(a) ?? new Number(Math.Sqrt(x)),
                "abs" => a.IsExact ? new Number(BigInteger.Abs(a.Num), a.Den) : new Number(Math.Abs(x)),
                "sin" => new Number(Math.Sin(x)),
                "cos" => new Number(Math.Cos(x)),
                "tan" => new Number(Math.Tan(x)),
                "asin" => new Number(Math.Asin(x)),
                "acos" => new Number(Math.Acos(x)),
                "atan" => new Number(Math.Atan(x)),
                "ln" => new Number(Math.Log(x)),
                "log" or "log10" => b is { } bb ? new Number(Math.Log(x) / Math.Log(bb.Value)) : new Number(Math.Log10(x)),
                "log2" => new Number(Math.Log(x, 2)),
                "exp" => new Number(Math.Exp(x)),
                "floor" => Number.Int(new BigInteger(Math.Floor(x))),
                "ceil" => Number.Int(new BigInteger(Math.Ceiling(x))),
                "round" => Number.Int(new BigInteger(Math.Round(x, MidpointRounding.AwayFromZero))),
                "deg" => new Number(x * 180 / Math.PI),
                "rad" => new Number(x * Math.PI / 180),
                "gcd" when b is { } g && a.IsExact && g.IsExact && a.Den.IsOne && g.Den.IsOne => Number.Int(BigInteger.GreatestCommonDivisor(a.Num, g.Num)),
                _ => throw new FormatException("Unknown function: " + name),
            };
        }

        private static Number? ExactRoot(Number a)
        {
            if (!a.IsExact || a.Num.Sign < 0) return null;
            var n = ISqrt(a.Num);
            var d = ISqrt(a.Den);
            return n * n == a.Num && d * d == a.Den ? new Number(n, d) : null;
        }

        private static BigInteger ISqrt(BigInteger v)
        {
            if (v < 2) return v;
            var x = new BigInteger(Math.Sqrt((double)v));
            while (x * x > v) x--;
            while ((x + 1) * (x + 1) <= v) x++;
            return x;
        }

        private static Number Add(Number a, Number b) => a.IsExact && b.IsExact ? new Number(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den) : new Number(a.Value + b.Value);
        private static Number Sub(Number a, Number b) => a.IsExact && b.IsExact ? new Number(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den) : new Number(a.Value - b.Value);
        private static Number Mul(Number a, Number b) => a.IsExact && b.IsExact ? new Number(a.Num * b.Num, a.Den * b.Den) : new Number(a.Value * b.Value);

        private static Number Div(Number a, Number b)
        {
            if (b.IsExact && b.Num.IsZero || !b.IsExact && b.Value == 0) throw new DivideByZeroException("Division by zero");
            return a.IsExact && b.IsExact ? new Number(a.Num * b.Den, a.Den * b.Num) : new Number(a.Value / b.Value);
        }

        private static Number Mod(Number a, Number b)
        {
            if (a.IsExact && b.IsExact && a.Den.IsOne && b.Den.IsOne && !b.Num.IsZero) return Number.Int(BigInteger.Remainder(a.Num, b.Num));
            return new Number(a.Value % b.Value);
        }

        private static Number Pow(Number a, Number b)
        {
            if (a.IsExact && b.IsExact && b.Den.IsOne && BigInteger.Abs(b.Num) <= 10000)
            {
                var e = (int)b.Num;
                var n = BigInteger.Pow(a.Num, Math.Abs(e));
                var d = BigInteger.Pow(a.Den, Math.Abs(e));
                return e >= 0 ? new Number(n, d) : new Number(d, n);
            }
            return new Number(Math.Pow(a.Value, b.Value));
        }
    }
}
