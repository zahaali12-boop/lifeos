using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Quicker.Analyzers;

/// <summary>
/// Enforces the kernel rules that keep the books right (ADR-0005, ADR-0011):
/// QK0001 no floating-point types in domain code, QK0002 no ad-hoc rounding, QK0003 no ambient clock.
/// Projects opt out per rule with <c>&lt;NoWarn&gt;</c> only where a documented exception exists.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainSafetyAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Quicker.Domain";

    public static readonly DiagnosticDescriptor NoFloatingPoint = new(
        "QK0001",
        "Floating-point type in domain code",
        "'{0}' is a floating-point type; use decimal (Money, Quantity) for anything that can reach the ledger",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Money and quantities are exact decimals, never floating point (ADR-0005).");

    public static readonly DiagnosticDescriptor NoAdHocRounding = new(
        "QK0002",
        "Rounding outside RoundingPolicy",
        "Rounding must go through Quicker.Kernel RoundingPolicy, not '{0}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Rounding rules are explicit and centralized (ADR-0005).");

    public static readonly DiagnosticDescriptor NoAmbientClock = new(
        "QK0003",
        "Ambient clock used",
        "'{0}' reads the ambient clock; inject IClock so time is controllable in tests",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Time is controlled through IClock (ADR-0011).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(NoFloatingPoint, NoAdHocRounding, NoAmbientClock);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzePredefinedType, SyntaxKind.PredefinedType);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzePredefinedType(SyntaxNodeAnalysisContext context)
    {
        var node = (PredefinedTypeSyntax)context.Node;
        if (node.Keyword.IsKind(SyntaxKind.DoubleKeyword) || node.Keyword.IsKind(SyntaxKind.FloatKeyword))
        {
            context.ReportDiagnostic(Diagnostic.Create(NoFloatingPoint, node.GetLocation(), node.Keyword.Text));
        }
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        var name = access.Name.Identifier.Text;

        if (name is "Round" or "Floor" or "Ceiling" or "Truncate")
        {
            var symbol = context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol;
            var container = symbol?.ContainingType?.ToDisplayString();
            if (container is "System.Math" or "System.Decimal" or "System.MathF")
            {
                if (IsInsideRoundingPolicy(access))
                {
                    return;
                }

                context.ReportDiagnostic(Diagnostic.Create(NoAdHocRounding, access.GetLocation(), $"{container}.{name}"));
            }

            return;
        }

        if (name is "Now" or "UtcNow" or "Today")
        {
            var symbol = context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol;
            var container = symbol?.ContainingType?.ToDisplayString();
            if (container is "System.DateTime" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeProvider")
            {
                if (IsInsideSystemClock(access))
                {
                    return;
                }

                context.ReportDiagnostic(Diagnostic.Create(NoAmbientClock, access.GetLocation(), $"{container}.{name}"));
            }
        }
    }

    private static bool IsInsideRoundingPolicy(SyntaxNode node) => IsInsideType(node, "RoundingPolicy");

    private static bool IsInsideSystemClock(SyntaxNode node) => IsInsideType(node, "SystemClock");

    private static bool IsInsideType(SyntaxNode node, string typeName)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is TypeDeclarationSyntax type && string.Equals(type.Identifier.Text, typeName, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
