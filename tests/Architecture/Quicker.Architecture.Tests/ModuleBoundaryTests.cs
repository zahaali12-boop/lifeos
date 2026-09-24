using System.Reflection;
using System.Xml.Linq;

namespace Quicker.Architecture.Tests;

/// <summary>
/// The walls between modules (ARCHITECTURE §6, ADR-0001): a module references only the kernel, the building blocks
/// and other modules' <c>.Contracts</c>; contracts reference only the kernel and nothing of persistence; the kernel and the
/// building blocks know no module but the caller's identity contract; production code never references test support. Checked twice: on the project files
/// (what a change declares) and on the compiled assemblies (what the code actually uses). Dependencies point from process
/// to core to platform modules, and no new pair of modules comes to depend on each other; the few that do on purpose are
/// named with their reason.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly string Root = FindRoot();

    private static readonly string[] PersistenceAssemblies = ["Quicker.Persistence", "Microsoft.EntityFrameworkCore", "Npgsql", "Dapper"];

    /// <summary>
    /// The one module contract the building blocks may use: who is calling (the principal, its grants and scopes), which
    /// the request pipeline, the permission filters and the outbox's actor need. Anything else of a module stays out.
    /// </summary>
    private const string CallerContract = "Quicker.Identity.Contracts";

    /// <summary>One production project: its name, its layer and module, and the projects it references.</summary>
    private sealed record Project(string Name, string Layer, string? Module, IReadOnlyList<string> References);

    [Fact]
    public void Every_production_project_is_classified()
    {
        var projects = Projects();
        projects.Count.ShouldBeGreaterThan(30);
        projects.Where(static p => p.Layer == "unknown").Select(static p => p.Name).ShouldBeEmpty();
        projects.Count(static p => p.Layer == "contracts").ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Modules_reference_only_the_kernel_the_building_blocks_and_other_modules_contracts()
    {
        var projects = Projects().ToDictionary(static p => p.Name, StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var module in projects.Values.Where(static p => p.Layer == "module"))
        {
            foreach (var reference in module.References)
            {
                var target = projects.GetValueOrDefault(reference);
                var allowed = target is not null && (target.Layer is "kernel" or "building_block" or "contracts" || (target.Layer == "module" && target.Module == module.Module));
                if (!allowed)
                {
                    violations.Add($"{module.Name} → {reference} ({target?.Layer ?? "not a production project"})");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Contracts_reference_only_the_kernel()
    {
        var violations = Projects()
            .Where(static p => p.Layer == "contracts")
            .SelectMany(static p => p.References.Where(static r => r != "Quicker.Kernel").Select(r => $"{p.Name} → {r}"))
            .ToList();
        violations.ShouldBeEmpty();
    }

    [Fact]
    public void The_kernel_and_the_building_blocks_know_no_module_but_the_caller_and_production_code_no_test_support()
    {
        var projects = Projects().ToDictionary(static p => p.Name, StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var project in projects.Values)
        {
            foreach (var reference in project.References)
            {
                var target = projects.GetValueOrDefault(reference);
                if (project.Layer is "kernel" or "building_block" && target?.Layer is "module" or "contracts" && !(project.Layer == "building_block" && reference == CallerContract))
                {
                    violations.Add($"{project.Name} → {reference} (a module)");
                }

                if (project.Layer != "test_support" && project.Name != "Quicker.Testing" && (reference.EndsWith(".TestSupport", StringComparison.Ordinal) || reference == "Quicker.Testing"))
                {
                    violations.Add($"{project.Name} → {reference} (test support)");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Compiled_modules_use_only_what_their_projects_may_reference()
    {
        var projects = Projects().ToDictionary(static p => p.Name, StringComparer.Ordinal);
        var loaded = LoadedQuickerAssemblies();
        loaded.Keys.ShouldContain("Quicker.Purchasing");
        loaded.Keys.ShouldContain("Quicker.Accounting.Contracts");

        var violations = new List<string>();
        foreach (var (name, assembly) in loaded)
        {
            if (!projects.TryGetValue(name, out var project))
            {
                continue;
            }

            foreach (var used in assembly.GetReferencedAssemblies().Select(static a => a.Name!))
            {
                var target = projects.GetValueOrDefault(used);
                switch (project.Layer)
                {
                    case "module" when target is not null && !(target.Layer is "kernel" or "building_block" or "contracts" || (target.Layer == "module" && target.Module == project.Module)):
                        violations.Add($"{name} uses {used} ({target.Layer})");
                        break;
                    case "contracts" when (target is not null && target.Name != "Quicker.Kernel") || PersistenceAssemblies.Any(p => used.StartsWith(p, StringComparison.Ordinal)):
                        violations.Add($"{name} uses {used}");
                        break;
                    case "kernel" or "building_block" when target?.Layer is "module" or "contracts" && !(project.Layer == "building_block" && used == CallerContract):
                        violations.Add($"{name} uses {used} (a module)");
                        break;
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    /// <summary>The tiers of ARCHITECTURE §6: process modules depend on core modules, core on platform, never upwards.</summary>
    private static readonly Dictionary<string, int> Tiers = new(StringComparer.Ordinal)
    {
        ["Tenancy"] = 0,
        ["Identity"] = 0,
        ["Audit"] = 0,
        ["Numbering"] = 0,
        ["Collaboration"] = 0,
        ["Integration"] = 0,
        ["Integrity"] = 0,
        ["Organization"] = 1,
        ["Accounting"] = 1,
        ["Partners"] = 1,
        ["Items"] = 1,
        ["Inventory"] = 1,
        ["Purchasing"] = 2,
        ["Payables"] = 2,
        ["Banking"] = 2,
        ["Workflow"] = 2,
    };

    /// <summary>Dependencies against the tiers that exist on purpose; a new one fails, and one that disappears must be removed here.</summary>
    private static readonly Dictionary<string, string> UpwardDependencies = new(StringComparer.Ordinal)
    {
        ["Inventory → Workflow"] = "stock adjustments are approved through the workflow engine (ADR-0020), drawn with the process modules but serving core documents too",
        ["Numbering → Organization"] = "number series are per company and fiscal period, so numbering reads the company directory and the fiscal calendar",
    };

    /// <summary>Modules that use each other's contracts on purpose (no assembly cycle: contracts reference only the kernel).</summary>
    private static readonly Dictionary<string, string> MutualDependencies = new(StringComparer.Ordinal)
    {
        ["Audit ↔ Identity"] = "identity's changes are audited, and the audit trail names and filters by the caller",
        ["Inventory ↔ Items"] = "stock reads the item master, and the item master's per-warehouse settings check the warehouse",
    };

    [Fact]
    public void Dependencies_point_from_process_to_core_to_platform()
    {
        var modules = ModuleDependencies();
        modules.Keys.Where(m => !Tiers.ContainsKey(m)).ShouldBeEmpty("every module has a tier in ARCHITECTURE §6");
        var upward = modules.SelectMany(static m => m.Value.Where(t => Tiers[t] > Tiers[m.Key]).Select(t => $"{m.Key} → {t}")).Order(StringComparer.Ordinal).ToList();
        upward.ShouldBe(UpwardDependencies.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void No_new_pair_of_modules_depends_on_each_other()
    {
        var modules = ModuleDependencies();
        var mutual = modules.SelectMany(m => m.Value.Where(t => string.CompareOrdinal(m.Key, t) < 0 && modules.GetValueOrDefault(t)?.Contains(m.Key) == true).Select(t => $"{m.Key} ↔ {t}")).Order(StringComparer.Ordinal).ToList();
        mutual.ShouldBe(MutualDependencies.Keys.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Which other modules' contracts each module's implementation references.</summary>
    private static Dictionary<string, HashSet<string>> ModuleDependencies()
    {
        var projects = Projects();
        var owners = projects.Where(static p => p.Layer == "contracts").ToDictionary(static p => p.Name, static p => p.Module!, StringComparer.Ordinal);
        return projects.Where(static p => p.Layer == "module").ToDictionary(
            static p => p.Module!,
            p => p.References.Select(r => owners.GetValueOrDefault(r)).OfType<string>().Where(m => m != p.Module).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static List<Project> Projects()
    {
        var src = Path.Combine(Root, "src");
        return Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Classify(src, f))
            .Where(static p => p is not null)
            .Select(static p => p!)
            .ToList();
    }

    private static Project? Classify(string src, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (name.EndsWith(".Tests", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
        var references = XDocument.Load(file).Descendants("ProjectReference")
            .Select(static r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();
        var (layer, module) = parts[0] switch
        {
            "Kernel" => ("kernel", null),
            "BuildingBlocks" => ("building_block", null),
            "Host" => ("host", null),
            "Modules" when name.EndsWith(".TestSupport", StringComparison.Ordinal) => ("test_support", parts[1]),
            "Modules" when name.EndsWith(".Contracts", StringComparison.Ordinal) => ("contracts", parts[1]),
            "Modules" when name == $"Quicker.{parts[1]}" => ("module", parts[1]),
            _ => ("unknown", (string?)null),
        };
        return new Project(name, layer, module, references);
    }

    /// <summary>Every Quicker assembly the API host loads (it references every module).</summary>
    private static Dictionary<string, Assembly> LoadedQuickerAssemblies()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(Program).Assembly]);
        while (pending.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies().Where(static r => r.Name!.StartsWith("Quicker.", StringComparison.Ordinal)))
            {
                if (!found.ContainsKey(reference.Name!))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return found;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quicker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Quicker.sln) was not found above the test's output directory.");
    }
}
