using System.Globalization;
using System.Numerics;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Persistence;

namespace Quicker.Numbering.Application;

/// <summary>
/// For a code being typed on a new or edited record: whether it is already taken in its scope (case-insensitively, the
/// record itself excepted), and the next free code, continuing the most common pattern of the existing codes
/// (CUS-0041 after CUS-0040, WH30 after WH10 and WH20) or, for an account, its siblings under the parent.
/// </summary>
public sealed record CodeSuggestion(string Kind, string? Code, bool Taken, string Next, bool AutoFill);

/// <summary>Suggests the next code of a coded record and checks a code for duplicates (A-165).</summary>
public sealed class CodeSuggestionService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal)
{
    public const int MaxCodeLength = 64;
    private const int MaxProbes = 200;

    public async Task<Result<CodeSuggestion>> SuggestAsync(string kind, string? code, string? scope, string? parent, Guid? exceptId, CancellationToken cancellationToken)
    {
        var type = CodeCatalogue.Find(kind);
        if (type is null)
        {
            return Error.NotFound("code_kind", kind);
        }

        if (principal.Required.ScopesFor(type.ReadPermission) is null)
        {
            return Error.Forbidden("auth.forbidden", $"Reading {kind} codes needs {type.ReadPermission}.").WithWhy(("permission", type.ReadPermission));
        }

        var scopeValue = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
        if (type.ScopeColumn is not null && scopeValue is null)
        {
            return Error.Validation("codes.scope_required", $"A {kind} code is unique within its {type.ScopeColumn}; say which.").WithWhy(("scope", type.ScopeColumn));
        }

        var typed = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (typed is { Length: > MaxCodeLength })
        {
            return Error.Validation("codes.too_long", $"A code has at most {MaxCodeLength} characters.");
        }

        var parentCode = string.IsNullOrWhiteSpace(parent) ? null : parent.Trim();
        var parameters = new DynamicParameters();
        parameters.Add("scope", scopeValue);
        parameters.Add("except", exceptId);
        var scopeClause = type.ScopeColumn is null ? "TRUE" : $"{type.ScopeColumn}::text = @scope";
        var uow = unitOfWork.Current;

        async Task<bool> TakenAsync(string candidate)
        {
            var p = new DynamicParameters(parameters);
            p.Add("code", candidate);
            return await uow.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                $"SELECT EXISTS (SELECT 1 FROM {type.Table} WHERE {scopeClause} AND lower(code) = lower(@code) AND (@except::uuid IS NULL OR id <> @except))",
                p, uow.Transaction, cancellationToken: cancellationToken));
        }

        var (prefix, number, width, step) = type.Kind == CodeCatalogue.Account
            ? await NextAccountAsync(type, parameters, scopeClause, parentCode, cancellationToken)
            : await NextInPatternAsync(type, parameters, scopeClause, cancellationToken);
        var next = Format(prefix, number, width);
        for (var probe = 0; probe < MaxProbes && await TakenAsync(next); probe++)
        {
            number += step;
            next = Format(prefix, number, width);
        }

        var taken = typed is not null && await TakenAsync(typed);
        return new CodeSuggestion(type.Kind, typed, taken, next, type.AutoFill);
    }

    private static string Format(string prefix, BigInteger number, int width) =>
        prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    /// <summary>The most common "letters then digits" pattern among the records (or the kind's default) and the number after its highest.</summary>
    private async Task<(string Prefix, BigInteger Number, int Width, int Step)> NextInPatternAsync(CodedRecordType type, DynamicParameters parameters, string scopeClause, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var where = type.PatternFilter is null ? scopeClause : $"{scopeClause} AND {type.PatternFilter}";
        var pattern = await uow.Connection.QueryFirstOrDefaultAsync<(string Prefix, long Count)?>(new CommandDefinition(
            $"""
            SELECT regexp_replace(code, '[0-9]+$', '') AS prefix, count(*) AS n
            FROM {type.Table}
            WHERE {where} AND code ~ '[0-9]$'
            GROUP BY 1
            ORDER BY n DESC, prefix
            LIMIT 1
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        if (pattern is null)
        {
            return (type.DefaultPrefix, BigInteger.One, type.DefaultWidth, 1);
        }

        var prefix = pattern.Value.Prefix;
        var p = new DynamicParameters(parameters);
        p.Add("prefix", prefix);
        var numbers = (await uow.Connection.QueryAsync<string>(new CommandDefinition(
            $"""
            SELECT substring(code from '[0-9]+$')
            FROM {type.Table}
            WHERE {where} AND code ~ '[0-9]$' AND regexp_replace(code, '[0-9]+$', '') = @prefix
            """, p, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        return Continue(prefix, numbers);
    }

    /// <summary>
    /// An account continues its siblings under the parent (1130 after 1110 and 1120); the first child of a parent takes
    /// the parent's first trailing zero (1110 under 1100, 6111 under 6110), or the parent with a 1 after it.
    /// </summary>
    private async Task<(string Prefix, BigInteger Number, int Width, int Step)> NextAccountAsync(CodedRecordType type, DynamicParameters parameters, string scopeClause, string? parentCode, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var p = new DynamicParameters(parameters);
        p.Add("parent", parentCode);
        var siblings = (await uow.Connection.QueryAsync<string>(new CommandDefinition(
            $"""
            SELECT code FROM {type.Table}
            WHERE {scopeClause}
              AND parent_id IS NOT DISTINCT FROM (SELECT id FROM {type.Table} WHERE {scopeClause} AND code = @parent)
              AND code ~ '^[0-9]+$'
            """, p, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        if (siblings.Count > 0)
        {
            // Siblings of the most common length set the width (a stray longer code does not).
            var length = siblings.GroupBy(static s => s.Length).OrderByDescending(static g => g.Count()).ThenByDescending(static g => g.Key).First().Key;
            return Continue(string.Empty, siblings.Where(s => s.Length == length).ToList(), stepFromOne: true);
        }

        if (parentCode is null || !parentCode.All(char.IsAsciiDigit))
        {
            return (parentCode ?? string.Empty, BigInteger.One, 1, 1);
        }

        var lastNonZero = parentCode.TrimEnd('0').Length;
        var first = lastNonZero < parentCode.Length
            ? parentCode[..lastNonZero] + "1" + parentCode[(lastNonZero + 1)..]
            : parentCode + "1";
        return (string.Empty, BigInteger.Parse(first, CultureInfo.InvariantCulture), first.Length, 1);
    }

    /// <summary>
    /// The number after the highest, padded as wide as the widest. Round numbers continue in their step: the largest
    /// power of ten that divides them all (WH10, WH20 then WH30; 100, 200 then 300). It takes two codes to show a step,
    /// except among accounts, where one child 1110 is already a step of ten.
    /// </summary>
    private static (string Prefix, BigInteger Number, int Width, int Step) Continue(string prefix, IReadOnlyCollection<string> digits, bool stepFromOne = false)
    {
        var values = digits.Select(static d => BigInteger.Parse(d, CultureInfo.InvariantCulture)).ToList();
        var max = values.Max();
        var width = digits.Max(static d => d.Length);
        var step = 1;
        if (values.Count >= 2 || stepFromOne)
        {
            while (step * 10 <= max && values.All(v => v % (step * 10) == 0))
            {
                step *= 10;
            }
        }

        return (prefix, (max / step + 1) * step, width, step);
    }
}
