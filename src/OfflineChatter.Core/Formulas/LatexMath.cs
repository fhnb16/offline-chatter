using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;

namespace OfflineChatter.Core.Formulas;

/// <summary>Узел дерева формулы (единый AST для экспорта: MathML, OMML, исходный LaTeX).</summary>
public abstract class MathNode { }

public sealed class MRow : MathNode
{
    public List<MathNode> Items { get; } = new();
}

/// <summary>Идентификатор (буквы курсивом), число или оператор.</summary>
public sealed class MToken : MathNode
{
    public MToken(string text, char kind) { Text = text; Kind = kind; }
    public string Text { get; }
    /// <summary>'i' — идентификатор, 'n' — число, 'o' — оператор, 'f' — имя функции (прямой шрифт).</summary>
    public char Kind { get; }
}

public sealed class MText : MathNode
{
    public MText(string text) => Text = text;
    public string Text { get; }
}

public sealed class MFrac : MathNode
{
    public MFrac(MathNode num, MathNode den) { Num = num; Den = den; }
    public MathNode Num { get; }
    public MathNode Den { get; }
}

public sealed class MSqrt : MathNode
{
    public MSqrt(MathNode body, MathNode? index) { Body = body; Index = index; }
    public MathNode Body { get; }
    public MathNode? Index { get; }
}

/// <summary>Верхний и/или нижний индекс. У «больших» операторов (∑, ∫) — пределы.</summary>
public sealed class MScripts : MathNode
{
    public MScripts(MathNode @base, MathNode? sub, MathNode? sup) { Base = @base; Sub = sub; Sup = sup; }
    public MathNode Base { get; }
    public MathNode? Sub { get; }
    public MathNode? Sup { get; }
    public bool IsBigOperator => Base is MToken { Kind: 'o' } t && LatexMath.BigOperators.Contains(t.Text);
}

public sealed class MFenced : MathNode
{
    public MFenced(string open, string close, MathNode body) { Open = open; Close = close; Body = body; }
    public string Open { get; }
    public string Close { get; }
    public MathNode Body { get; }
}

public sealed class MMatrix : MathNode
{
    public List<List<MathNode>> Rows { get; } = new();
    public string Open { get; set; } = string.Empty;
    public string Close { get; set; } = string.Empty;
}

/// <summary>Надстрочный знак: \hat, \bar, \vec, \dot, \tilde, \overline.</summary>
public sealed class MAccent : MathNode
{
    public MAccent(string accent, MathNode body) { Accent = accent; Body = body; }
    public string Accent { get; }
    public MathNode Body { get; }
}

public sealed class MStyle : MathNode
{
    public MStyle(string variant, MathNode body) { Variant = variant; Body = body; }
    /// <summary>MathML mathvariant: bold, normal, double-struck, script, fraktur, italic.</summary>
    public string Variant { get; }
    public MathNode Body { get; }
}

/// <summary>
/// Разбор подмножества LaTeX, которое реально выдают LLM: дроби, корни, индексы, греческие буквы, операторы,
/// \left…\right, матрицы и cases, \text, стили шрифта, акценты. Неизвестные команды сохраняются как текст.
/// </summary>
public static class LatexMath
{
    internal static readonly HashSet<string> BigOperators = new() { "∑", "∏", "∐", "∫", "∬", "∭", "∮", "⋃", "⋂", "⨁", "⨂" };

    private static readonly Dictionary<string, string> Symbols = new()
    {
        // греческие
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ",
        ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
        ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        // прочие буквы-символы
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["hbar"] = "ℏ", ["ell"] = "ℓ", ["emptyset"] = "∅", ["varnothing"] = "∅",
        ["aleph"] = "ℵ", ["Re"] = "ℜ", ["Im"] = "ℑ", ["wp"] = "℘",
    };

    private static readonly Dictionary<string, string> Operators = new()
    {
        ["cdot"] = "·", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘", ["bullet"] = "•",
        ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼",
        ["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫", ["prec"] = "≺", ["succ"] = "≻",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔", ["implies"] = "⟹", ["iff"] = "⟺", ["mapsto"] = "↦", ["uparrow"] = "↑", ["downarrow"] = "↓",
        ["longrightarrow"] = "⟶", ["Longrightarrow"] = "⟹",
        ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇",
        ["cup"] = "∪", ["cap"] = "∩", ["setminus"] = "∖", ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄", ["neg"] = "¬", ["lnot"] = "¬",
        ["land"] = "∧", ["wedge"] = "∧", ["lor"] = "∨", ["vee"] = "∨", ["oplus"] = "⊕", ["otimes"] = "⊗", ["perp"] = "⊥", ["parallel"] = "∥",
        ["mid"] = "∣", ["angle"] = "∠", ["triangle"] = "△", ["therefore"] = "∴", ["because"] = "∵", ["prime"] = "′", ["degree"] = "°",
        ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱",
        ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉", ["vert"] = "|", ["Vert"] = "‖",
        ["{"] = "{", ["}"] = "}", ["|"] = "‖", ["#"] = "#", ["%"] = "%", ["&"] = "&", ["_"] = "_", ["$"] = "$",
    };

    private static readonly HashSet<string> Functions = new()
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth", "log", "ln", "lg", "exp",
        "lim", "liminf", "limsup", "max", "min", "sup", "inf", "det", "dim", "ker", "deg", "gcd", "arg", "Pr", "mod", "bmod",
    };

    private static readonly Dictionary<string, string> Accents = new()
    {
        ["hat"] = "^", ["widehat"] = "^", ["bar"] = "¯", ["overline"] = "¯", ["vec"] = "→", ["overrightarrow"] = "→", ["dot"] = "˙",
        ["ddot"] = "¨", ["tilde"] = "~", ["widetilde"] = "~", ["check"] = "ˇ", ["breve"] = "˘", ["acute"] = "´", ["grave"] = "`",
    };

    private static readonly Dictionary<string, string> Styles = new()
    {
        ["mathbf"] = "bold", ["boldsymbol"] = "bold", ["bm"] = "bold", ["mathrm"] = "normal", ["operatorname"] = "normal", ["mathit"] = "italic",
        ["mathbb"] = "double-struck", ["mathcal"] = "script", ["mathscr"] = "script", ["mathfrak"] = "fraktur", ["mathsf"] = "sans-serif",
        ["mathtt"] = "monospace", ["textbf"] = "bold",
    };

    public static MathNode Parse(string latex) => new Parser(latex ?? string.Empty).ParseRow(null);

    private sealed class Parser
    {
        private readonly string _s;
        private int _p;

        public Parser(string s) => _s = s;

        private bool End => _p >= _s.Length;
        private char Cur => _s[_p];

        private void SkipSpaces()
        {
            while (!End && char.IsWhiteSpace(Cur)) _p++;
        }

        /// <summary>Строка до одного из стоп-токенов: '}', '&', '\\', \right, \end.</summary>
        public MRow ParseRow(string? stopAt)
        {
            var row = new MRow();
            while (true)
            {
                SkipSpaces();
                if (End) break;
                if (Cur == '}' && stopAt == "}") { _p++; break; }
                if (Cur == '}') { _p++; continue; } // лишняя скобка — пропускаем
                if (stopAt == "cell" && (Cur == '&' || Peek("\\\\") || PeekCommand("end"))) break;
                if (stopAt == "right" && PeekCommand("right")) break;
                var atom = ParseAtom(row);
                if (atom == null) continue;
                row.Items.Add(ParseScripts(atom));
            }
            return row;
        }

        private bool Peek(string t) => string.CompareOrdinal(_s, _p, t, 0, t.Length) == 0;

        private bool PeekCommand(string name) => Peek("\\" + name) && (_p + name.Length + 1 >= _s.Length || !char.IsLetter(_s[_p + name.Length + 1]));

        private MathNode ParseScripts(MathNode atom)
        {
            MathNode? sub = null, sup = null;
            while (true)
            {
                SkipSpaces();
                if (End) break;
                if (Cur == '^' && sup == null) { _p++; sup = ParseArgument(); }
                else if (Cur == '_' && sub == null) { _p++; sub = ParseArgument(); }
                else if (Cur == '\'')
                {
                    _p++;
                    sup = sup == null ? new MToken("′", 'o') : Concat(sup, new MToken("′", 'o'));
                }
                else break;
            }
            return sub == null && sup == null ? atom : new MScripts(atom, sub, sup);
        }

        private static MathNode Concat(MathNode a, MathNode b)
        {
            var r = new MRow();
            r.Items.Add(a);
            r.Items.Add(b);
            return r;
        }

        /// <summary>Аргумент команды: {группа} или один токен.</summary>
        private MathNode ParseArgument()
        {
            SkipSpaces();
            if (End) return new MRow();
            if (Cur == '{')
            {
                _p++;
                return Simplify(ParseRow("}"));
            }
            // Аргумент без скобок — ровно один символ: \frac12 = 1/2, x^23 = x²·3
            if (char.IsDigit(Cur))
            {
                var d = Cur.ToString();
                _p++;
                return new MToken(d, 'n');
            }
            return ParseAtom(null) ?? new MRow();
        }

        private string ReadGroupText()
        {
            SkipSpaces();
            if (End || Cur != '{') return End ? string.Empty : _s[_p++].ToString();
            _p++;
            var depth = 1;
            var start = _p;
            while (!End)
            {
                if (Cur == '{') depth++;
                else if (Cur == '}' && --depth == 0) break;
                _p++;
            }
            var text = _s.Substring(start, _p - start);
            if (!End) _p++;
            return text;
        }

        private static MathNode Simplify(MRow r) => r.Items.Count == 1 ? r.Items[0] : r;

        private MathNode? ParseAtom(MRow? context)
        {
            var c = Cur;
            if (c == '{')
            {
                _p++;
                return Simplify(ParseRow("}"));
            }
            if (char.IsDigit(c) || (c == '.' && _p + 1 < _s.Length && char.IsDigit(_s[_p + 1])))
            {
                var start = _p;
                while (!End && (char.IsDigit(Cur) || Cur == '.')) _p++;
                return new MToken(_s.Substring(start, _p - start), 'n');
            }
            if (char.IsLetter(c))
            {
                _p++;
                return new MToken(c.ToString(), 'i');
            }
            if (c == '\\') return ParseCommand();
            if (c == '&' || c == '^' || c == '_')
            {
                _p++;
                return null;
            }
            _p++;
            return new MToken(c.ToString(), 'o');
        }

        private MathNode? ParseCommand()
        {
            _p++; // '\'
            if (End) return null;
            if (!char.IsLetter(Cur))
            {
                var ch = Cur.ToString();
                _p++;
                return ch switch
                {
                    "," or ":" or ";" or ">" => new MText(" "),
                    "!" => null,
                    " " => new MText(" "),
                    "\\" => null,
                    _ => Operators.TryGetValue(ch, out var symOp) ? new MToken(symOp, 'o') : new MToken(ch, 'o'),
                };
            }
            var start = _p;
            while (!End && char.IsLetter(Cur)) _p++;
            var name = _s.Substring(start, _p - start);

            switch (name)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                    return new MFrac(ParseArgument(), ParseArgument());
                case "binom":
                {
                    var n = ParseArgument();
                    var k = ParseArgument();
                    var m = new MMatrix { Open = "(", Close = ")" };
                    m.Rows.Add(new List<MathNode> { n });
                    m.Rows.Add(new List<MathNode> { k });
                    return m;
                }
                case "sqrt":
                {
                    MathNode? index = null;
                    SkipSpaces();
                    if (!End && Cur == '[')
                    {
                        _p++;
                        var start2 = _p;
                        while (!End && Cur != ']') _p++;
                        index = Parse(_s.Substring(start2, _p - start2));
                        if (!End) _p++;
                    }
                    return new MSqrt(ParseArgument(), index);
                }
                case "text":
                case "textrm":
                case "textit":
                case "mbox":
                    return new MText(ReadGroupText());
                case "left":
                {
                    var open = ReadDelimiter();
                    var body = ParseRow("right");
                    var close = string.Empty;
                    if (PeekCommand("right"))
                    {
                        _p += 6;
                        close = ReadDelimiter();
                    }
                    return new MFenced(open, close, Simplify(body));
                }
                case "right":
                    ReadDelimiter();
                    return null;
                case "big":
                case "Big":
                case "bigg":
                case "Bigg":
                case "bigl":
                case "bigr":
                case "Bigl":
                case "Bigr":
                    return new MToken(ReadDelimiter(), 'o');
                case "begin":
                    return ParseEnvironment(ReadGroupText());
                case "end":
                    ReadGroupText();
                    return null;
                case "quad":
                case "qquad":
                    return new MText(name == "quad" ? "  " : "    ");
                case "displaystyle":
                case "textstyle":
                case "limits":
                case "nolimits":
                    return null;
                case "not":
                {
                    var next = ParseArgument();
                    return next is MToken { Text: "=" } ? new MToken("≠", 'o') : next is MToken { Text: "∈" } ? new MToken("∉", 'o') : next;
                }
            }

            if (Symbols.TryGetValue(name, out var sym)) return new MToken(sym, 'i');
            if (Operators.TryGetValue(name, out var op)) return new MToken(op, 'o');
            if (Functions.Contains(name)) return new MToken(name == "bmod" ? "mod" : name, 'f');
            if (Accents.TryGetValue(name, out var acc)) return new MAccent(acc, ParseArgument());
            if (Styles.TryGetValue(name, out var variant))
            {
                if (name == "operatorname") return new MToken(ReadGroupText(), 'f');
                return new MStyle(variant, ParseArgument());
            }
            // Неизвестная команда — сохраняем имя, чтобы смысл не пропал
            return new MText("\\" + name);
        }

        private string ReadDelimiter()
        {
            SkipSpaces();
            if (End) return string.Empty;
            if (Cur == '\\')
            {
                _p++;
                if (End) return string.Empty;
                if (!char.IsLetter(Cur))
                {
                    var ch = Cur;
                    _p++;
                    return ch == '|' ? "‖" : ch.ToString();
                }
                var start = _p;
                while (!End && char.IsLetter(Cur)) _p++;
                var name = _s.Substring(start, _p - start);
                return Operators.TryGetValue(name, out var op) ? op : string.Empty;
            }
            var c = Cur;
            _p++;
            return c == '.' ? string.Empty : c.ToString();
        }

        private MathNode ParseEnvironment(string env)
        {
            var m = new MMatrix();
            switch (env.TrimEnd('*'))
            {
                case "pmatrix": m.Open = "("; m.Close = ")"; break;
                case "bmatrix": m.Open = "["; m.Close = "]"; break;
                case "Bmatrix": m.Open = "{"; m.Close = "}"; break;
                case "vmatrix": m.Open = "|"; m.Close = "|"; break;
                case "Vmatrix": m.Open = "‖"; m.Close = "‖"; break;
                case "cases": m.Open = "{"; break;
                case "array":
                    ReadGroupText(); // спецификация колонок
                    break;
            }
            var row = new List<MathNode>();
            while (!End)
            {
                var cell = ParseRow("cell");
                row.Add(Simplify(cell));
                SkipSpaces();
                if (End) break;
                if (Cur == '&')
                {
                    _p++;
                    continue;
                }
                if (Peek("\\\\"))
                {
                    _p += 2;
                    m.Rows.Add(row);
                    row = new List<MathNode>();
                    continue;
                }
                if (PeekCommand("end"))
                {
                    _p += 4;
                    ReadGroupText();
                    break;
                }
            }
            if (row.Count > 0 && !(row.Count == 1 && row[0] is MRow { Items.Count: 0 })) m.Rows.Add(row);
            return m;
        }
    }

    // ======================= MathML =======================

    public static string ToMathML(string latex, bool display)
    {
        var sb = new StringBuilder();
        sb.Append("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"").Append(display ? " display=\"block\"" : string.Empty).Append("><semantics><mrow>");
        WriteMathML(Parse(latex), sb);
        sb.Append("</mrow><annotation encoding=\"application/x-tex\">").Append(SecurityElement.Escape(latex)).Append("</annotation></semantics></math>");
        return sb.ToString();
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static void WriteMathML(MathNode n, StringBuilder sb)
    {
        switch (n)
        {
            case MRow r:
                sb.Append("<mrow>");
                foreach (var i in r.Items) WriteMathML(i, sb);
                sb.Append("</mrow>");
                break;
            case MToken t:
                switch (t.Kind)
                {
                    case 'n': sb.Append("<mn>").Append(X(t.Text)).Append("</mn>"); break;
                    case 'i': sb.Append("<mi>").Append(X(t.Text)).Append("</mi>"); break;
                    case 'f': sb.Append("<mi mathvariant=\"normal\">").Append(X(t.Text)).Append("</mi><mo>&#x2061;</mo>"); break;
                    default:
                        var large = BigOperators.Contains(t.Text);
                        sb.Append(large ? "<mo largeop=\"true\">" : "<mo>").Append(X(t.Text)).Append("</mo>");
                        break;
                }
                break;
            case MText tx:
                sb.Append("<mtext>").Append(X(tx.Text)).Append("</mtext>");
                break;
            case MFrac f:
                sb.Append("<mfrac>");
                WriteMathML(f.Num, sb);
                WriteMathML(f.Den, sb);
                sb.Append("</mfrac>");
                break;
            case MSqrt s when s.Index != null:
                sb.Append("<mroot>");
                WriteMathML(s.Body, sb);
                WriteMathML(s.Index, sb);
                sb.Append("</mroot>");
                break;
            case MSqrt s:
                sb.Append("<msqrt>");
                WriteMathML(s.Body, sb);
                sb.Append("</msqrt>");
                break;
            case MScripts sc:
            {
                var big = sc.IsBigOperator;
                var tag = sc.Sub != null && sc.Sup != null ? (big ? "munderover" : "msubsup") : sc.Sub != null ? (big ? "munder" : "msub") : (big ? "mover" : "msup");
                sb.Append('<').Append(tag).Append('>');
                WriteMathML(sc.Base, sb);
                if (sc.Sub != null) WriteMathML(sc.Sub, sb);
                if (sc.Sup != null) WriteMathML(sc.Sup, sb);
                sb.Append("</").Append(tag).Append('>');
                break;
            }
            case MFenced fe:
                sb.Append("<mrow>");
                if (fe.Open.Length > 0) sb.Append("<mo fence=\"true\">").Append(X(fe.Open)).Append("</mo>");
                WriteMathML(fe.Body, sb);
                if (fe.Close.Length > 0) sb.Append("<mo fence=\"true\">").Append(X(fe.Close)).Append("</mo>");
                sb.Append("</mrow>");
                break;
            case MMatrix m:
                sb.Append("<mrow>");
                if (m.Open.Length > 0) sb.Append("<mo fence=\"true\">").Append(X(m.Open)).Append("</mo>");
                sb.Append(m.Open == "{" && m.Close.Length == 0 ? "<mtable columnalign=\"left left\">" : "<mtable>");
                foreach (var row in m.Rows)
                {
                    sb.Append("<mtr>");
                    foreach (var cell in row)
                    {
                        sb.Append("<mtd>");
                        WriteMathML(cell, sb);
                        sb.Append("</mtd>");
                    }
                    sb.Append("</mtr>");
                }
                sb.Append("</mtable>");
                if (m.Close.Length > 0) sb.Append("<mo fence=\"true\">").Append(X(m.Close)).Append("</mo>");
                sb.Append("</mrow>");
                break;
            case MAccent a:
                sb.Append("<mover accent=\"true\">");
                WriteMathML(a.Body, sb);
                sb.Append("<mo>").Append(X(a.Accent)).Append("</mo></mover>");
                break;
            case MStyle st:
                sb.Append("<mstyle mathvariant=\"").Append(st.Variant).Append("\">");
                WriteMathML(st.Body, sb);
                sb.Append("</mstyle>");
                break;
        }
    }

    // ======================= OMML (Office Math) =======================

    public const string OmmlNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>OMML для вставки в document.xml (префикс m: должен быть объявлен в корне документа).</summary>
    public static string ToOmml(string latex, bool display)
    {
        var sb = new StringBuilder();
        if (display) sb.Append("<m:oMathPara>");
        sb.Append("<m:oMath>");
        WriteOmmlRow(Flatten(Parse(latex)), sb);
        sb.Append("</m:oMath>");
        if (display) sb.Append("</m:oMathPara>");
        return sb.ToString();
    }

    private static List<MathNode> Flatten(MathNode n) => n is MRow r ? r.Items : new List<MathNode> { n };

    private static void WriteOmmlRow(List<MathNode> items, StringBuilder sb)
    {
        for (var i = 0; i < items.Count; i++)
        {
            // Большой оператор с пределами «забирает» следующий элемент как операнд (m:nary)
            if (items[i] is MScripts { IsBigOperator: true } sc)
            {
                var operand = i + 1 < items.Count ? items[++i] : new MRow();
                WriteNary(((MToken)sc.Base).Text, sc.Sub, sc.Sup, operand, sb);
                continue;
            }
            if (items[i] is MToken { Kind: 'o' } bt && BigOperators.Contains(bt.Text))
            {
                var operand = i + 1 < items.Count ? items[++i] : new MRow();
                WriteNary(bt.Text, null, null, operand, sb);
                continue;
            }
            WriteOmml(items[i], sb);
        }
    }

    private static void WriteNary(string chr, MathNode? sub, MathNode? sup, MathNode operand, StringBuilder sb)
    {
        sb.Append("<m:nary><m:naryPr><m:chr m:val=\"").Append(X(chr)).Append("\"/>");
        if (chr is "∫" or "∬" or "∭" or "∮") sb.Append("<m:limLoc m:val=\"subSup\"/>");
        if (sub == null) sb.Append("<m:subHide m:val=\"1\"/>");
        if (sup == null) sb.Append("<m:supHide m:val=\"1\"/>");
        sb.Append("</m:naryPr><m:sub>");
        if (sub != null) WriteOmmlRow(Flatten(sub), sb);
        sb.Append("</m:sub><m:sup>");
        if (sup != null) WriteOmmlRow(Flatten(sup), sb);
        sb.Append("</m:sup><m:e>");
        WriteOmmlRow(Flatten(operand), sb);
        sb.Append("</m:e></m:nary>");
    }

    private static void Run(StringBuilder sb, string text, bool plain = false, string? sty = null)
    {
        sb.Append("<m:r>");
        if (plain || sty != null) sb.Append("<m:rPr>").Append(plain ? "<m:sty m:val=\"p\"/>" : $"<m:sty m:val=\"{sty}\"/>").Append("</m:rPr>");
        sb.Append("<m:t xml:space=\"preserve\">").Append(X(text)).Append("</m:t></m:r>");
    }

    private static void E(MathNode n, StringBuilder sb, string tag = "m:e")
    {
        sb.Append('<').Append(tag).Append('>');
        WriteOmmlRow(Flatten(n), sb);
        sb.Append("</").Append(tag).Append('>');
    }

    private static void WriteOmml(MathNode n, StringBuilder sb)
    {
        switch (n)
        {
            case MRow r:
                WriteOmmlRow(r.Items, sb);
                break;
            case MToken t:
                Run(sb, t.Text, plain: t.Kind is 'n' or 'f' or 'o');
                break;
            case MText tx:
                Run(sb, tx.Text, plain: true);
                break;
            case MFrac f:
                sb.Append("<m:f>");
                E(f.Num, sb, "m:num");
                E(f.Den, sb, "m:den");
                sb.Append("</m:f>");
                break;
            case MSqrt s:
                sb.Append("<m:rad>");
                if (s.Index == null) sb.Append("<m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/>");
                else E(s.Index, sb, "m:deg");
                E(s.Body, sb);
                sb.Append("</m:rad>");
                break;
            case MScripts sc when sc.Sub != null && sc.Sup != null:
                sb.Append("<m:sSubSup>");
                E(sc.Base, sb);
                E(sc.Sub, sb, "m:sub");
                E(sc.Sup, sb, "m:sup");
                sb.Append("</m:sSubSup>");
                break;
            case MScripts sc when sc.Sub != null:
                sb.Append(sc.Base is MToken { Kind: 'f' } ? "<m:limLow>" : "<m:sSub>");
                E(sc.Base, sb);
                E(sc.Sub, sb, sc.Base is MToken { Kind: 'f' } ? "m:lim" : "m:sub");
                sb.Append(sc.Base is MToken { Kind: 'f' } ? "</m:limLow>" : "</m:sSub>");
                break;
            case MScripts sc:
                sb.Append("<m:sSup>");
                E(sc.Base, sb);
                E(sc.Sup!, sb, "m:sup");
                sb.Append("</m:sSup>");
                break;
            case MFenced fe:
                sb.Append("<m:d><m:dPr><m:begChr m:val=\"").Append(X(fe.Open)).Append("\"/><m:endChr m:val=\"").Append(X(fe.Close)).Append("\"/></m:dPr>");
                E(fe.Body, sb);
                sb.Append("</m:d>");
                break;
            case MMatrix m:
            {
                var fenced = m.Open.Length > 0 || m.Close.Length > 0;
                if (fenced) sb.Append("<m:d><m:dPr><m:begChr m:val=\"").Append(X(m.Open)).Append("\"/><m:endChr m:val=\"").Append(X(m.Close)).Append("\"/></m:dPr><m:e>");
                var cols = m.Rows.Count == 0 ? 1 : m.Rows.Max(r => r.Count);
                sb.Append("<m:m><m:mPr><m:mcs><m:mc><m:mcPr><m:count m:val=\"").Append(cols).Append("\"/><m:mcJc m:val=\"")
                  .Append(m.Open == "{" && m.Close.Length == 0 ? "left" : "center").Append("\"/></m:mcPr></m:mc></m:mcs></m:mPr>");
                foreach (var row in m.Rows)
                {
                    sb.Append("<m:mr>");
                    for (var c = 0; c < cols; c++) E(c < row.Count ? row[c] : new MRow(), sb);
                    sb.Append("</m:mr>");
                }
                sb.Append("</m:m>");
                if (fenced) sb.Append("</m:e></m:d>");
                break;
            }
            case MAccent a:
                sb.Append("<m:acc><m:accPr><m:chr m:val=\"").Append(X(a.Accent switch { "^" => "̂", "¯" => "̅", "→" => "⃗", "˙" => "̇", "¨" => "̈", "~" => "̃", _ => a.Accent }))
                  .Append("\"/></m:accPr>");
                E(a.Body, sb);
                sb.Append("</m:acc>");
                break;
            case MStyle st:
            {
                // Простая передача стиля: жирный (b), прямой (p); остальное — как есть
                var inner = new StringBuilder();
                WriteOmmlRow(Flatten(st.Body), inner);
                var s = inner.ToString();
                if (st.Variant == "bold") s = s.Replace("<m:r><m:t", "<m:r><m:rPr><m:sty m:val=\"b\"/></m:rPr><m:t");
                else if (st.Variant == "normal") s = s.Replace("<m:r><m:t", "<m:r><m:rPr><m:sty m:val=\"p\"/></m:rPr><m:t");
                else if (st.Variant == "double-struck") s = s.Replace("<m:r><m:t", "<m:r><m:rPr><m:scr m:val=\"double-struck\"/></m:rPr><m:t");
                sb.Append(s);
                break;
            }
        }
    }

    /// <summary>Плоский «читаемый» текст формулы (для RTF без картинок и простого текста).</summary>
    public static string ToPlainText(string latex)
    {
        var sb = new StringBuilder();
        void W(MathNode n)
        {
            switch (n)
            {
                case MRow r: foreach (var i in r.Items) W(i); break;
                case MToken t: sb.Append(t.Text); break;
                case MText tx: sb.Append(tx.Text); break;
                case MFrac f: sb.Append('('); W(f.Num); sb.Append(")/("); W(f.Den); sb.Append(')'); break;
                case MSqrt s: sb.Append("√("); W(s.Body); sb.Append(')'); break;
                case MScripts sc:
                    W(sc.Base);
                    if (sc.Sub != null) { sb.Append('_'); W(sc.Sub); }
                    if (sc.Sup != null) { sb.Append('^'); W(sc.Sup); }
                    break;
                case MFenced fe: sb.Append(fe.Open); W(fe.Body); sb.Append(fe.Close); break;
                case MMatrix m:
                    sb.Append(m.Open);
                    sb.Append(string.Join("; ", m.Rows.Select(row => string.Join(", ", row.Select(c => { var inner = new StringBuilder(); var save = sb.Length; W(c); var s = sb.ToString(save, sb.Length - save); sb.Length = save; return s; })))));
                    sb.Append(m.Close);
                    break;
                case MAccent a: W(a.Body); sb.Append(a.Accent); break;
                case MStyle st: W(st.Body); break;
            }
        }
        W(Parse(latex));
        return sb.ToString();
    }
}
