using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Web;

/// <summary>
/// The list filter language (slice 1.9): <c>field op value [and field op value]*</c>. Operators: eq, ne, gt, ge, lt,
/// le, like (case-insensitive contains), in (v1, v2, …), isnull, notnull. Values: 'quoted' ('' escapes a quote),
/// numbers, true/false, dates (yyyy-MM-dd) and timestamps (ISO 8601). Fields are the ones an endpoint exposes;
/// <c>cf.key</c> reaches custom fields where the entity carries them.
/// </summary>
public sealed record FilterCondition(string Field, string Operator, IReadOnlyList<string> Values);

public static class FilterParser
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "eq", "ne", "gt", "ge", "lt", "le", "like", "in", "isnull", "notnull" };

    public static Result<IReadOnlyList<FilterCondition>> Parse(string? filter)
    {
        var conditions = new List<FilterCondition>();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return conditions;
        }

        var tokens = Tokenize(filter);
        if (tokens.IsFailure)
        {
            return tokens.Error!;
        }

        var list = tokens.Value;
        var i = 0;
        while (i < list.Count)
        {
            if (i + 1 >= list.Count)
            {
                return Syntax("a condition is 'field operator value'");
            }

            var field = list[i].Text;
            var op = list[i + 1].Text.ToLowerInvariant();
            if (list[i].Quoted || !IsField(field) || list[i + 1].Quoted || !Operators.Contains(op))
            {
                return Syntax($"unexpected '{list[i].Text} {list[i + 1].Text}'");
            }

            i += 2;
            var values = new List<string>();
            if (op is "isnull" or "notnull")
            {
            }
            else if (op == "in")
            {
                if (i >= list.Count || list[i].Text != "(")
                {
                    return Syntax("'in' takes a parenthesised list");
                }

                i++;
                while (i < list.Count && list[i].Text != ")")
                {
                    if (list[i].Text == ",")
                    {
                        i++;
                        continue;
                    }

                    values.Add(list[i].Text);
                    i++;
                }

                if (i >= list.Count)
                {
                    return Syntax("'in' list is not closed");
                }

                i++;
            }
            else
            {
                if (i >= list.Count || list[i].Text is "(" or ")" or ",")
                {
                    return Syntax($"'{op}' needs a value");
                }

                values.Add(list[i].Text);
                i++;
            }

            conditions.Add(new FilterCondition(field, op, values));
            if (i < list.Count)
            {
                if (!string.Equals(list[i].Text, "and", StringComparison.OrdinalIgnoreCase) || list[i].Quoted)
                {
                    return Syntax($"expected 'and' before '{list[i].Text}'");
                }

                i++;
            }
        }

        return conditions;
    }

    private static bool IsField(string text) => text.Length is > 0 and <= 80 && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');

    private static Error Syntax(string detail) => Error.Validation("filter.syntax", $"Cannot parse the filter: {detail}.");

    private sealed record Token(string Text, bool Quoted);

    private static Result<List<Token>> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c is '(' or ')' or ',')
            {
                tokens.Add(new Token(c.ToString(), false));
                i++;
            }
            else if (c == '\'')
            {
                var value = new System.Text.StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            value.Append('\'');
                            i += 2;
                            continue;
                        }

                        closed = true;
                        i++;
                        break;
                    }

                    value.Append(text[i]);
                    i++;
                }

                if (!closed)
                {
                    return Syntax("unterminated quoted value");
                }

                tokens.Add(new Token(value.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ','))
                {
                    i++;
                }

                tokens.Add(new Token(text[start..i], false));
            }
        }

        return tokens;
    }
}

/// <summary>The fields an endpoint lets clients filter on, each bound to a property of the queried entity.</summary>
public sealed class FilterSpec<T>
{
    private readonly Dictionary<string, LambdaExpression> _fields = new(StringComparer.OrdinalIgnoreCase);
    private LambdaExpression? _customFields;

    public FilterSpec<T> Field<TProp>(string name, Expression<Func<T, TProp>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _fields[name] = selector;
        return this;
    }

    /// <summary>Enables <c>cf.&lt;key&gt;</c> against a jsonb column of custom-field values (compared as text).</summary>
    public FilterSpec<T> CustomFields(Expression<Func<T, string>> jsonColumn)
    {
        _customFields = jsonColumn;
        return this;
    }

    public IReadOnlyCollection<string> Fields => _fields.Keys.Order(StringComparer.Ordinal).ToList();

    public Result<IQueryable<T>> Apply(IQueryable<T> query, string? filter)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parsed = FilterParser.Parse(filter);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        Expression? body = null;
        foreach (var condition in parsed.Value)
        {
            var clause = Clause(condition, parameter);
            if (clause.IsFailure)
            {
                return clause.Error!;
            }

            body = body is null ? clause.Value : Expression.AndAlso(body, clause.Value);
        }

        return Result<IQueryable<T>>.Success(body is null ? query : query.Where(Expression.Lambda<Func<T, bool>>(body, parameter)));
    }

    private Result<Expression> Clause(FilterCondition condition, ParameterExpression parameter)
    {
        Expression member;
        Type type;
        if (condition.Field.StartsWith("cf.", StringComparison.OrdinalIgnoreCase))
        {
            if (_customFields is null)
            {
                return Error.Validation("filter.field_unknown", "This list has no custom fields.").WithWhy(("field", condition.Field));
            }

            var key = condition.Field[3..];
            if (key.Length == 0 || !key.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return Error.Validation("filter.field_unknown", "Custom-field keys are lower-case names.").WithWhy(("field", condition.Field));
            }

            var column = Rebind(_customFields, parameter);
            member = Expression.Call(typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Text))!, column, Expression.Constant(key));
            type = typeof(string);
        }
        else if (_fields.TryGetValue(condition.Field, out var selector))
        {
            member = Rebind(selector, parameter);
            type = selector.ReturnType;
        }
        else
        {
            return Error.Validation("filter.field_unknown", $"'{condition.Field}' is not a filterable field.").WithWhy(("field", condition.Field), ("fields", Fields));
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        switch (condition.Operator)
        {
            case "isnull":
                return Expression.Equal(member, Expression.Constant(null, type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(object) : type));
            case "notnull":
                return Expression.NotEqual(member, Expression.Constant(null, type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(object) : type));
            case "like":
                if (underlying != typeof(string))
                {
                    return Error.Validation("filter.operator_invalid", "'like' applies to text fields only.").WithWhy(("field", condition.Field));
                }

                return Expression.Call(typeof(NpgsqlDbFunctionsExtensions), nameof(NpgsqlDbFunctionsExtensions.ILike), Type.EmptyTypes,
                    Expression.Property(null, typeof(EF), nameof(EF.Functions)), member, Expression.Constant("%" + Escape(condition.Values[0]) + "%"));
            case "in":
                {
                    var values = new List<object?>();
                    foreach (var raw in condition.Values)
                    {
                        var converted = Convert(raw, underlying, condition.Field);
                        if (converted.IsFailure)
                        {
                            return converted.Error!;
                        }

                        values.Add(converted.Value);
                    }

                    var typedList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
                    foreach (var value in values)
                    {
                        typedList.Add(value);
                    }

                    return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [type], Expression.Constant(typedList), member);
                }

            default:
                {
                    var converted = Convert(condition.Values[0], underlying, condition.Field);
                    if (converted.IsFailure)
                    {
                        return converted.Error!;
                    }

                    var constant = Expression.Constant(converted.Value, type);
                    if (underlying == typeof(string) && condition.Operator is not ("eq" or "ne"))
                    {
                        var compare = Expression.Call(member, typeof(string).GetMethod(nameof(string.CompareTo), [typeof(string)])!, constant);
                        return Comparison(condition.Operator, compare, Expression.Constant(0));
                    }

                    return Comparison(condition.Operator, member, constant);
                }
        }
    }

    private static Expression Comparison(string op, Expression left, Expression right) => op switch
    {
        "eq" => Expression.Equal(left, right),
        "ne" => Expression.NotEqual(left, right),
        "gt" => Expression.GreaterThan(left, right),
        "ge" => Expression.GreaterThanOrEqual(left, right),
        "lt" => Expression.LessThan(left, right),
        _ => Expression.LessThanOrEqual(left, right),
    };

    private static Expression Rebind(LambdaExpression selector, ParameterExpression parameter) => new ParameterReplacer(selector.Parameters[0], parameter).Visit(selector.Body);

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static Result<object?> Convert(string raw, Type type, string field)
    {
        try
        {
            if (type == typeof(string))
            {
                return raw;
            }

            if (type == typeof(bool))
            {
                return bool.Parse(raw);
            }

            if (type == typeof(Guid))
            {
                return Guid.Parse(raw);
            }

            if (type == typeof(DateOnly))
            {
                return DateOnly.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (type == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            }

            if (type.IsEnum)
            {
                return Enum.Parse(type, raw, ignoreCase: true);
            }

            return System.Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return Error.Validation("filter.value_invalid", $"'{raw}' is not a valid value for '{field}'.").WithWhy(("field", field), ("expected", type.Name));
        }
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
