using System.Globalization;
using Quicker.Kernel.Results;
using Quicker.Workflow.Contracts;

namespace Quicker.Workflow.Application;

/// <summary>
/// The safe expression grammar of ADR-0020: field access, literals, comparisons, arithmetic, <c>in</c>, <c>and</c>,
/// <c>or</c>, <c>not</c> and a fixed set of functions. Parsed and type-checked against the subject's field catalogue;
/// evaluated over the subject's values with no code execution.
///
/// <code>
/// expr    := or
/// or      := and ("or" and)*
/// and     := not ("and" not)*
/// not     := "not" not | cmp
/// cmp     := add (("==" | "=" | "!=" | "&lt;&gt;" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=") add | ["not"] "in" (add | "(" args ")"))?
/// add     := mul (("+" | "-") mul)*
/// mul     := unary (("*" | "/") unary)*
/// unary   := "-" unary | primary
/// primary := number | string | "true" | "false" | "null" | ident ["(" args ")"] | "(" expr ")" | "[" args "]"
/// </code>
/// </summary>
public static class Expressions
{
    /// <summary>The functions a rule may call: name → (argument types, result type).</summary>
    private static readonly IReadOnlyDictionary<string, (string[] Args, string Result)> Functions = new Dictionary<string, (string[], string)>(StringComparer.Ordinal)
    {
        ["amount_in"] = ([WorkflowFieldTypes.Text], WorkflowFieldTypes.Number),
        ["days_since"] = ([WorkflowFieldTypes.Date], WorkflowFieldTypes.Number),
        ["days_until"] = ([WorkflowFieldTypes.Date], WorkflowFieldTypes.Number),
        ["today"] = ([], WorkflowFieldTypes.Date),
        ["lower"] = ([WorkflowFieldTypes.Text], WorkflowFieldTypes.Text),
        ["upper"] = ([WorkflowFieldTypes.Text], WorkflowFieldTypes.Text),
        ["contains"] = ([WorkflowFieldTypes.Text, WorkflowFieldTypes.Text], WorkflowFieldTypes.Boolean),
        ["starts_with"] = ([WorkflowFieldTypes.Text, WorkflowFieldTypes.Text], WorkflowFieldTypes.Boolean),
        ["len"] = ([WorkflowFieldTypes.List], WorkflowFieldTypes.Number),
        ["abs"] = ([WorkflowFieldTypes.Number], WorkflowFieldTypes.Number),
        ["round"] = ([WorkflowFieldTypes.Number], WorkflowFieldTypes.Number),
        ["is_empty"] = ([WorkflowFieldTypes.Text], WorkflowFieldTypes.Boolean),
    };

    public static IReadOnlyList<string> FunctionNames => Functions.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>The type of a field the catalogue does not list, when the rule reasons over a block's own values (typed when the block is raised).</summary>
    private const string Any = "any";

    /// <summary>Parses and type-checks; the result type must be boolean for a condition. With <paramref name="openFields"/> unknown fields are allowed and typed when the values arrive (block rules).</summary>
    public static Result<Compiled> Compile(string source, IReadOnlyDictionary<string, string> fieldTypes, bool openFields = false)
    {
        ArgumentNullException.ThrowIfNull(fieldTypes);
        if (string.IsNullOrWhiteSpace(source))
        {
            return Error.Validation("workflow.condition_required", "A rule needs a condition.");
        }

        try
        {
            var tokens = Lexer.Tokenize(source);
            var parser = new Parser(tokens, source);
            var node = parser.ParseExpression();
            parser.ExpectEnd();
            var checker = new Checker(fieldTypes, openFields);
            var type = checker.Check(node);
            if (type is not (WorkflowFieldTypes.Boolean or Any))
            {
                return Error.Validation("workflow.condition_not_boolean", $"A condition must be true or false; this one is {type}.").WithWhy(("type", type));
            }

            return new Compiled(source, node, checker.FieldsUsed.Order(StringComparer.Ordinal).ToList(), checker.CurrenciesUsed.Order(StringComparer.Ordinal).ToList());
        }
        catch (ExpressionException ex)
        {
            return Error.Validation("workflow.condition_invalid", ex.Message).WithWhy(("position", ex.Position), ("expression", source));
        }
    }

    /// <summary>Evaluates a compiled condition over values; conversions for <c>amount_in</c> come from <paramref name="context"/>.</summary>
    public static Result<bool> Evaluate(Compiled compiled, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var value = new Evaluator(context).Eval(compiled.Root);
            return value is true;
        }
        catch (ExpressionException ex)
        {
            return Error.Validation("workflow.condition_failed", ex.Message).WithWhy(("expression", compiled.Source));
        }
    }

    public sealed record Compiled(string Source, Node Root, IReadOnlyList<string> Fields, IReadOnlyList<string> Currencies);

    /// <summary>Values by field name, the rate from the subject's currency to each currency a rule converts into, and today's date.</summary>
    public sealed record EvaluationContext(IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, decimal> RatesTo, DateOnly Today);

    // ------------------------------------------------------------------ syntax tree

    public abstract record Node(int Position);

    public sealed record Literal(int Position, object? Value) : Node(Position);

    public sealed record Field(int Position, string Name) : Node(Position);

    public sealed record Call(int Position, string Name, IReadOnlyList<Node> Args) : Node(Position);

    public sealed record Unary(int Position, string Op, Node Operand) : Node(Position);

    public sealed record Binary(int Position, string Op, Node Left, Node Right) : Node(Position);

    public sealed record ListNode(int Position, IReadOnlyList<Node> Items) : Node(Position);

    public sealed record InNode(int Position, Node Item, Node List, bool Negated) : Node(Position);

    private sealed class ExpressionException(int position, string message) : Exception(message)
    {
        public int Position { get; } = position;
    }

    // ------------------------------------------------------------------ lexer

    private enum TokenKind { Number, String, Ident, Op, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static class Lexer
    {
        /// <summary>Longest first; <c>=</c> and <c>&lt;&gt;</c> are accepted as the spellings of <c>==</c> and <c>!=</c> people write in rules.</summary>
        private static readonly string[] Operators = ["==", "!=", "<=", ">=", "<>", "<", ">", "=", "+", "-", "*", "/", "(", ")", "[", "]", ","];

        public static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Number, s[start..i], start));
                    continue;
                }

                if (c is '\'' or '"')
                {
                    var start = i;
                    i++;
                    var text = new System.Text.StringBuilder();
                    while (i < s.Length && s[i] != c)
                    {
                        text.Append(s[i]);
                        i++;
                    }

                    if (i >= s.Length)
                    {
                        throw new ExpressionException(start, "An unterminated string starts here.");
                    }

                    i++;
                    tokens.Add(new Token(TokenKind.String, text.ToString(), start));
                    continue;
                }

                if (char.IsAsciiLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Ident, s[start..i], start));
                    continue;
                }

                var op = Operators.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                if (op is null)
                {
                    throw new ExpressionException(i, $"Unexpected character '{c}'.");
                }

                tokens.Add(new Token(TokenKind.Op, op switch { "=" => "==", "<>" => "!=", _ => op }, i));
                i += op.Length;
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, s.Length));
            return tokens;
        }
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(List<Token> tokens, string source)
    {
        private int _index;

        private Token Current => tokens[_index];

        public Node ParseExpression() => ParseOr();

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
            {
                throw new ExpressionException(Current.Position, $"Unexpected '{Current.Text}' after the end of the expression.");
            }
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsKeyword("or"))
            {
                var pos = Current.Position;
                _index++;
                left = new Binary(pos, "or", left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (IsKeyword("and"))
            {
                var pos = Current.Position;
                _index++;
                left = new Binary(pos, "and", left, ParseNot());
            }

            return left;
        }

        private Node ParseNot()
        {
            if (IsKeyword("not"))
            {
                var pos = Current.Position;
                _index++;
                return new Unary(pos, "not", ParseNot());
            }

            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            if (Current.Kind == TokenKind.Op && Current.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = Current;
                _index++;
                return new Binary(op.Position, op.Text, left, ParseAdditive());
            }

            if (IsKeyword("not") && tokens[_index + 1].Kind == TokenKind.Ident && tokens[_index + 1].Text == "in")
            {
                var pos = Current.Position;
                _index += 2;
                return new InNode(pos, left, ParseInOperand(), Negated: true);
            }

            if (IsKeyword("in"))
            {
                var pos = Current.Position;
                _index++;
                return new InNode(pos, left, ParseInOperand(), Negated: false);
            }

            return left;
        }

        /// <summary>After <c>in</c>: a list in brackets, a list field, or the SQL-style tuple <c>('a', 'b')</c>.</summary>
        private Node ParseInOperand()
        {
            if (Current.Kind == TokenKind.Op && Current.Text == "(")
            {
                var pos = Current.Position;
                _index++;
                return new ListNode(pos, ParseArguments(")"));
            }

            return ParseAdditive();
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Current.Kind == TokenKind.Op && Current.Text is "+" or "-")
            {
                var op = Current;
                _index++;
                left = new Binary(op.Position, op.Text, left, ParseMultiplicative());
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Current.Kind == TokenKind.Op && Current.Text is "*" or "/")
            {
                var op = Current;
                _index++;
                left = new Binary(op.Position, op.Text, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Kind == TokenKind.Op && Current.Text == "-")
            {
                var pos = Current.Position;
                _index++;
                return new Unary(pos, "-", ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    _index++;
                    if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new ExpressionException(token.Position, $"'{token.Text}' is not a number.");
                    }

                    return new Literal(token.Position, number);
                case TokenKind.String:
                    _index++;
                    return new Literal(token.Position, token.Text);
                case TokenKind.Ident:
                    _index++;
                    switch (token.Text)
                    {
                        case "true":
                            return new Literal(token.Position, true);
                        case "false":
                            return new Literal(token.Position, false);
                        case "null":
                            return new Literal(token.Position, null);
                        case "and" or "or" or "not" or "in":
                            throw new ExpressionException(token.Position, $"'{token.Text}' cannot start a value.");
                    }

                    if (Current.Kind == TokenKind.Op && Current.Text == "(")
                    {
                        _index++;
                        var args = ParseArguments(")");
                        return new Call(token.Position, token.Text, args);
                    }

                    return new Field(token.Position, token.Text);
                case TokenKind.Op when token.Text == "(":
                    _index++;
                    var inner = ParseExpression();
                    Expect(")");
                    return inner;
                case TokenKind.Op when token.Text == "[":
                    _index++;
                    return new ListNode(token.Position, ParseArguments("]"));
                default:
                    throw new ExpressionException(token.Position, token.Kind == TokenKind.End ? "The expression ends too early." : $"Unexpected '{token.Text}'.");
            }
        }

        private List<Node> ParseArguments(string closer)
        {
            var args = new List<Node>();
            if (Current.Kind == TokenKind.Op && Current.Text == closer)
            {
                _index++;
                return args;
            }

            while (true)
            {
                args.Add(ParseExpression());
                if (Current.Kind == TokenKind.Op && Current.Text == ",")
                {
                    _index++;
                    continue;
                }

                Expect(closer);
                return args;
            }
        }

        private void Expect(string op)
        {
            if (Current.Kind != TokenKind.Op || Current.Text != op)
            {
                throw new ExpressionException(Current.Position, $"Expected '{op}' here{(Current.Kind == TokenKind.End ? " (the expression ends too early)" : $", found '{Current.Text}'")}.");
            }

            _index++;
        }

        private bool IsKeyword(string word) => Current.Kind == TokenKind.Ident && Current.Text == word;

        public override string ToString() => source;
    }

    // ------------------------------------------------------------------ type checker

    private sealed class Checker(IReadOnlyDictionary<string, string> fieldTypes, bool openFields)
    {
        public HashSet<string> FieldsUsed { get; } = new(StringComparer.Ordinal);

        public HashSet<string> CurrenciesUsed { get; } = new(StringComparer.Ordinal);

        public string Check(Node node)
        {
            switch (node)
            {
                case Literal literal:
                    return literal.Value switch
                    {
                        decimal => WorkflowFieldTypes.Number,
                        string => WorkflowFieldTypes.Text,
                        bool => WorkflowFieldTypes.Boolean,
                        _ => "null",
                    };
                case Field field:
                    if (!fieldTypes.TryGetValue(field.Name, out var type))
                    {
                        if (!openFields)
                        {
                            throw new ExpressionException(field.Position, $"Unknown field '{field.Name}'. Known fields: {string.Join(", ", fieldTypes.Keys.Order(StringComparer.Ordinal))}.");
                        }

                        type = Any;
                    }

                    FieldsUsed.Add(field.Name);
                    return type;
                case ListNode list:
                    foreach (var item in list.Items)
                    {
                        Check(item);
                    }

                    return WorkflowFieldTypes.List;
                case Unary unary:
                    {
                        var operand = Check(unary.Operand);
                        if (unary.Op == "not")
                        {
                            return Require(unary, operand, WorkflowFieldTypes.Boolean, "not");
                        }

                        return Require(unary, operand, WorkflowFieldTypes.Number, "-");
                    }

                case InNode inNode:
                    {
                        Check(inNode.Item);
                        var list = Check(inNode.List);
                        if (list is not (WorkflowFieldTypes.List or Any))
                        {
                            throw new ExpressionException(inNode.Position, "'in' expects a list on its right, like [\"A\", \"B\"].");
                        }

                        return WorkflowFieldTypes.Boolean;
                    }

                case Binary binary:
                    {
                        var left = Check(binary.Left);
                        var right = Check(binary.Right);
                        switch (binary.Op)
                        {
                            case "and" or "or":
                                Require(binary, left, WorkflowFieldTypes.Boolean, binary.Op);
                                Require(binary, right, WorkflowFieldTypes.Boolean, binary.Op);
                                return WorkflowFieldTypes.Boolean;
                            case "==" or "!=":
                                if (left != right && left is not (Any or "null") && right is not (Any or "null"))
                                {
                                    throw new ExpressionException(binary.Position, $"Cannot compare {left} with {right}.");
                                }

                                return WorkflowFieldTypes.Boolean;
                            case "<" or "<=" or ">" or ">=":
                                if (left is Any || right is Any)
                                {
                                    var known = left is Any ? right : left;
                                    if (known is not (WorkflowFieldTypes.Number or WorkflowFieldTypes.Date or WorkflowFieldTypes.Text or Any))
                                    {
                                        throw new ExpressionException(binary.Position, $"'{binary.Op}' compares two numbers, two dates or two texts; here {left} and {right}.");
                                    }

                                    return WorkflowFieldTypes.Boolean;
                                }

                                if (left != right || left is not (WorkflowFieldTypes.Number or WorkflowFieldTypes.Date or WorkflowFieldTypes.Text))
                                {
                                    throw new ExpressionException(binary.Position, $"'{binary.Op}' compares two numbers, two dates or two texts; here {left} and {right}.");
                                }

                                return WorkflowFieldTypes.Boolean;
                            case "+":
                                if (left == WorkflowFieldTypes.Text && right == WorkflowFieldTypes.Text)
                                {
                                    return WorkflowFieldTypes.Text;
                                }

                                if (left is Any || right is Any)
                                {
                                    return left == WorkflowFieldTypes.Text || right == WorkflowFieldTypes.Text ? WorkflowFieldTypes.Text : WorkflowFieldTypes.Number;
                                }

                                Require(binary, left, WorkflowFieldTypes.Number, "+");
                                Require(binary, right, WorkflowFieldTypes.Number, "+");
                                return WorkflowFieldTypes.Number;
                            default:
                                Require(binary, left, WorkflowFieldTypes.Number, binary.Op);
                                Require(binary, right, WorkflowFieldTypes.Number, binary.Op);
                                return WorkflowFieldTypes.Number;
                        }
                    }

                case Call call:
                    {
                        if (!Functions.TryGetValue(call.Name, out var signature))
                        {
                            throw new ExpressionException(call.Position, $"Unknown function '{call.Name}'. Functions: {string.Join(", ", FunctionNames)}.");
                        }

                        if (call.Args.Count != signature.Args.Length)
                        {
                            throw new ExpressionException(call.Position, $"'{call.Name}' takes {signature.Args.Length} argument(s).");
                        }

                        for (var i = 0; i < call.Args.Count; i++)
                        {
                            var actual = Check(call.Args[i]);
                            if (actual != signature.Args[i] && actual != Any && !(signature.Args[i] == WorkflowFieldTypes.List && actual == WorkflowFieldTypes.Text))
                            {
                                throw new ExpressionException(call.Args[i].Position, $"Argument {i + 1} of '{call.Name}' must be {signature.Args[i]}, not {actual}.");
                            }
                        }

                        if (call.Name == "amount_in")
                        {
                            if (!openFields && (!fieldTypes.ContainsKey("amount") || !fieldTypes.ContainsKey("currency")))
                            {
                                throw new ExpressionException(call.Position, "'amount_in' needs the subject to carry 'amount' and 'currency' fields.");
                            }

                            FieldsUsed.Add("amount");
                            FieldsUsed.Add("currency");
                            if (call.Args[0] is Literal { Value: string currency })
                            {
                                CurrenciesUsed.Add(currency.ToUpperInvariant());
                            }
                            else
                            {
                                throw new ExpressionException(call.Args[0].Position, "'amount_in' takes a currency code in quotes, like amount_in('USD').");
                            }
                        }

                        return signature.Result;
                    }

                default:
                    throw new ExpressionException(node.Position, "Unsupported expression.");
            }
        }

        private static string Require(Node node, string actual, string expected, string op)
        {
            if (actual != expected && actual != Any)
            {
                throw new ExpressionException(node.Position, $"'{op}' expects {expected}, not {actual}.");
            }

            return expected;
        }
    }

    // ------------------------------------------------------------------ evaluator

    private sealed class Evaluator(EvaluationContext context)
    {
        public object? Eval(Node node)
        {
            switch (node)
            {
                case Literal literal:
                    return literal.Value;
                case Field field:
                    return context.Values.TryGetValue(field.Name, out var value) ? Normalize(value) : null;
                case ListNode list:
                    return list.Items.Select(Eval).ToList();
                case Unary unary:
                    {
                        var operand = Eval(unary.Operand);
                        return unary.Op == "not" ? operand is not true : operand is decimal d ? -d : null;
                    }

                case InNode inNode:
                    {
                        var item = Eval(inNode.Item);
                        var list = Eval(inNode.List) as List<object?> ?? [];
                        var found = list.Any(candidate => AreEqual(item, candidate));
                        return inNode.Negated ? !found : found;
                    }

                case Binary binary:
                    {
                        if (binary.Op == "and")
                        {
                            return Eval(binary.Left) is true && Eval(binary.Right) is true;
                        }

                        if (binary.Op == "or")
                        {
                            return Eval(binary.Left) is true || Eval(binary.Right) is true;
                        }

                        var left = Eval(binary.Left);
                        var right = Eval(binary.Right);
                        switch (binary.Op)
                        {
                            case "==":
                                return AreEqual(left, right);
                            case "!=":
                                return !AreEqual(left, right);
                            case "<" or "<=" or ">" or ">=":
                                {
                                    var comparison = Compare(left, right);
                                    if (comparison is null)
                                    {
                                        return false;
                                    }

                                    return binary.Op switch
                                    {
                                        "<" => comparison < 0,
                                        "<=" => comparison <= 0,
                                        ">" => comparison > 0,
                                        _ => comparison >= 0,
                                    };
                                }

                            case "+" when left is string ls && right is string rs:
                                return ls + rs;
                            default:
                                {
                                    if (left is not decimal l || right is not decimal r)
                                    {
                                        return null;
                                    }

                                    return binary.Op switch
                                    {
                                        "+" => l + r,
                                        "-" => l - r,
                                        "*" => l * r,
                                        "/" => r == 0m ? throw new ExpressionException(binary.Position, "Division by zero.") : l / r,
                                        _ => null,
                                    };
                                }
                        }
                    }

                case Call call:
                    return Invoke(call);
                default:
                    return null;
            }
        }

        private object? Invoke(Call call)
        {
            var args = call.Args.Select(Eval).ToList();
            switch (call.Name)
            {
                case "amount_in":
                    {
                        var currency = (args[0] as string ?? string.Empty).ToUpperInvariant();
                        if (context.Values.TryGetValue("amount", out var amountValue) && Normalize(amountValue) is decimal amount)
                        {
                            var subjectCurrency = (context.Values.TryGetValue("currency", out var c) ? c as string : null)?.ToUpperInvariant();
                            if (subjectCurrency == currency)
                            {
                                return amount;
                            }

                            if (context.RatesTo.TryGetValue(currency, out var rate))
                            {
                                return amount * rate;
                            }

                            throw new ExpressionException(call.Position, $"No exchange rate from {subjectCurrency ?? "?"} to {currency} for the document's date.");
                        }

                        return null;
                    }

                case "days_since":
                    return args[0] is DateOnly since ? (decimal)context.Today.DayNumber - since.DayNumber : null;
                case "days_until":
                    return args[0] is DateOnly until ? (decimal)until.DayNumber - context.Today.DayNumber : null;
                case "today":
                    return context.Today;
                case "lower":
                    return (args[0] as string)?.ToLowerInvariant();
                case "upper":
                    return (args[0] as string)?.ToUpperInvariant();
                case "contains":
                    return args[0] is string haystack && args[1] is string needle && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
                case "starts_with":
                    return args[0] is string text && args[1] is string prefix && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                case "len":
                    return args[0] switch { List<object?> list => (decimal)list.Count, string s => (decimal)s.Length, _ => 0m };
                case "abs":
                    return args[0] is decimal a ? Math.Abs(a) : null;
                case "round":
                    return args[0] is decimal r ? decimal.Truncate(r + (r >= 0 ? 0.5m : -0.5m)) : null;
                case "is_empty":
                    return args[0] is not string s2 || s2.Length == 0;
                default:
                    throw new ExpressionException(call.Position, $"Unknown function '{call.Name}'.");
            }
        }

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            decimal d => d,
            int i => (decimal)i,
            long l => (decimal)l,
            string s => s,
            bool b => b,
            DateOnly date => date,
            DateTime dt => DateOnly.FromDateTime(dt),
            DateTimeOffset dto => DateOnly.FromDateTime(dto.UtcDateTime),
            IEnumerable<string> strings => strings.Cast<object?>().ToList(),
            IEnumerable<object?> objects => objects.Select(Normalize).ToList(),
            _ => value.ToString(),
        };

        private static bool AreEqual(object? a, object? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            if (a is string sa && b is string sb)
            {
                return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
            }

            return Compare(a, b) == 0 || a.Equals(b);
        }

        private static int? Compare(object? a, object? b) => (a, b) switch
        {
            (decimal x, decimal y) => x.CompareTo(y),
            (DateOnly x, DateOnly y) => x.CompareTo(y),
            (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            (bool x, bool y) => x.CompareTo(y),
            _ => null,
        };
    }
}
