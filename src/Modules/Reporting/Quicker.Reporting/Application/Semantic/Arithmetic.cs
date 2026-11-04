using System.Globalization;
using Quicker.Kernel.Results;

namespace Quicker.Reporting.Application.Semantic;

/// <summary>
/// Calculated fields: arithmetic over the measures of a result row (+ − × ÷, parentheses, numbers, measure keys).
/// Evaluated after aggregation; a division by zero or a missing operand gives an empty cell, never an error.
/// </summary>
public static class Arithmetic
{
    public abstract record Node;

    public sealed record Number(decimal Value) : Node;

    public sealed record Ref(string Key) : Node;

    public sealed record Binary(char Op, Node Left, Node Right) : Node;

    public sealed record Negate(Node Operand) : Node;

    public static Result<Node> Parse(string expression, IReadOnlySet<string> knownKeys)
    {
        ArgumentNullException.ThrowIfNull(knownKeys);
        try
        {
            var parser = new Parser(expression ?? string.Empty, knownKeys);
            var node = parser.ParseExpression();
            parser.ExpectEnd();
            return node;
        }
        catch (FormatException ex)
        {
            return Error.Validation("report.calculated_invalid", ex.Message).WithWhy(("expression", expression ?? string.Empty));
        }
    }

    public static decimal? Evaluate(Node node, IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(row);
        switch (node)
        {
            case Number n:
                return n.Value;
            case Ref r:
                return row.TryGetValue(r.Key, out var value) ? ToDecimal(value) : null;
            case Negate neg:
                return -Evaluate(neg.Operand, row);
            case Binary b:
                var left = Evaluate(b.Left, row);
                var right = Evaluate(b.Right, row);
                if (left is null || right is null)
                {
                    return null;
                }

                return b.Op switch
                {
                    '+' => left + right,
                    '-' => left - right,
                    '*' => left * right,
                    '/' => right == 0m ? null : left / right,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        _ => decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    private sealed class Parser(string source, IReadOnlySet<string> keys)
    {
        private int _position;

        public Node ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipSpace();
                if (_position < source.Length && source[_position] is '+' or '-')
                {
                    var op = source[_position++];
                    left = new Binary(op, left, ParseTerm());
                    continue;
                }

                return left;
            }
        }

        private Node ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpace();
                if (_position < source.Length && source[_position] is '*' or '/')
                {
                    var op = source[_position++];
                    left = new Binary(op, left, ParseUnary());
                    continue;
                }

                return left;
            }
        }

        private Node ParseUnary()
        {
            SkipSpace();
            if (_position < source.Length && source[_position] == '-')
            {
                _position++;
                return new Negate(ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_position >= source.Length)
            {
                throw new FormatException("The expression ends where a value was expected.");
            }

            var c = source[_position];
            if (c == '(')
            {
                _position++;
                var inner = ParseExpression();
                SkipSpace();
                if (_position >= source.Length || source[_position] != ')')
                {
                    throw new FormatException("A closing parenthesis is missing.");
                }

                _position++;
                return inner;
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                var start = _position;
                while (_position < source.Length && (char.IsAsciiDigit(source[_position]) || source[_position] == '.'))
                {
                    _position++;
                }

                return new Number(decimal.Parse(source[start.._position], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = _position;
                while (_position < source.Length && (char.IsAsciiLetterOrDigit(source[_position]) || source[_position] == '_'))
                {
                    _position++;
                }

                var key = source[start.._position];
                if (!keys.Contains(key))
                {
                    throw new FormatException($"'{key}' is not a measure of the query; a calculated field names the measures it combines.");
                }

                return new Ref(key);
            }

            throw new FormatException($"Unexpected '{c}' at position {_position + 1}.");
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position < source.Length)
            {
                throw new FormatException($"Unexpected '{source[_position]}' at position {_position + 1}.");
            }
        }

        private void SkipSpace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
            {
                _position++;
            }
        }
    }
}
