using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The ways code reaches past the SDK without naming a forbidden type: one case per pattern. Each is in <see cref="ReflectionBait"/> (never
/// called) so this assembly's metadata holds it; a third-party package that uses it is refused as run-time code, a first-party one is not.
/// The lint is not a sandbox; this pins what it does look for.
/// </summary>
public sealed class PluginLintPatternTests
{
    private static readonly IReadOnlyList<LintFinding> Findings = PluginLint.Scan(File.ReadAllBytes(typeof(PluginLintPatternTests).Assembly.Location), "Tests.dll");

    [Theory]
    [InlineData("System.Linq.Expressions.Expression")]       // Expression.Lambda(...) (a type of System.Linq.Expressions)
    [InlineData("Expression`1.Compile")]                     // the member of a generic type hangs on a type specification
    [InlineData("FieldInfo.GetValue")]
    [InlineData("FieldInfo.SetValue")]
    [InlineData("PropertyInfo.GetValue")]
    [InlineData("PropertyInfo.SetValue")]
    [InlineData("Assembly.GetTypes")]
    [InlineData("Assembly.get_DefinedTypes")]
    [InlineData("Assembly.get_ExportedTypes")]
    [InlineData("Delegate.CreateDelegate")]
    [InlineData("AppDomain.GetAssemblies")]
    [InlineData("Assembly.GetCallingAssembly")]
    [InlineData("Assembly.GetEntryAssembly")]
    [InlineData("Assembly.GetExecutingAssembly")]
    [InlineData("Type.GetField")]
    [InlineData("Type.GetProperty")]
    [InlineData("Type.GetMethod")]
    [InlineData("Type.GetMember")]
    [InlineData("System.Runtime.CompilerServices.UnsafeAccessorAttribute")]
    [InlineData("Microsoft.CSharp.RuntimeBinder.Binder")]    // dynamic
    public void A_pattern_that_reaches_past_the_SDK_is_refused_for_a_third_party_as_run_time_code(string detail)
    {
        Assert.Contains(Findings, f => f.Detail == detail);
        // Even a declared dynamicCode does not cover it for a third party; the same code is ordinary for CouchHUD's own plugin.
        var all = new[] { PluginCapabilities.DynamicCode, PluginCapabilities.Process, PluginCapabilities.Native };
        var refused = PluginLint.Evaluate(Findings.Where(f => f.Detail == detail), PluginOrigin.ThirdParty, all);
        Assert.NotEmpty(refused);
        Assert.All(refused, f => Assert.True(f.Capability is PluginCapabilities.DynamicCode or PluginCapabilities.Process));
        Assert.Empty(PluginLint.Evaluate(Findings.Where(f => f.Detail == detail), PluginOrigin.FirstParty, all));
    }

    [Fact]
    public void Shell_of_the_Visual_Basic_library_needs_the_process_capability_and_its_assembly_is_no_base_library_for_a_third_party()
    {
        var shell = Findings.Where(f => f.Detail == "Microsoft.VisualBasic.Interaction").ToList();
        Assert.NotEmpty(shell);
        Assert.All(shell, f => Assert.Equal(PluginCapabilities.Process, f.Capability));
        Assert.Empty(PluginLint.Evaluate(shell, PluginOrigin.FirstParty, [PluginCapabilities.Process]));
        Assert.NotEmpty(PluginLint.Evaluate(shell, PluginOrigin.ThirdParty, []));
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("plugin.lint.assemblyRef", PluginLint.CheckReference("Microsoft.VisualBasic.Core", PluginOrigin.ThirdParty, own));
        Assert.Equal("plugin.lint.assemblyRef", PluginLint.CheckReference("Microsoft.VisualBasic", PluginOrigin.ThirdParty, own));
        Assert.Null(PluginLint.CheckReference("Microsoft.VisualBasic.Core", PluginOrigin.FirstParty, own));
    }

    [Fact]
    public void The_host_is_reached_through_an_object_the_SDK_hands_out_only_by_reflection_that_is_found()
    {
        // context.GetType().Assembly.GetTypes() and FieldInfo.SetValue need no reference to Core at all.
        var reach = new[] { "Assembly.GetTypes", "FieldInfo.SetValue", "Type.GetField" };
        foreach (var detail in reach) Assert.Contains(PluginLint.Evaluate(Findings, PluginOrigin.ThirdParty, [PluginCapabilities.Pages]), f => f.Detail == detail);
    }
}

/// <summary>Never called: gives the patterns above something to find in this assembly's metadata.</summary>
internal static class ReflectionBait
{
#pragma warning disable CA1416, CS8602, IL2026, IL2075, IL2070, IL3050, CA2200
    internal static object?[] Uses(object context, FieldInfo field, PropertyInfo property, Type type, dynamic anything)
    {
        Expression<Func<int>> expression = Expression.Lambda<Func<int>>(Expression.Constant(1));
        var assembly = context.GetType().Assembly;
        field.SetValue(context, 1);
        property.SetValue(context, 1);
        return
        [
            expression.Compile(),
            Expression.Lambda(Expression.Constant(1)).Compile(),
            field.GetValue(context),
            property.GetValue(context),
            assembly.GetTypes(),
            assembly.DefinedTypes,
            assembly.ExportedTypes,
            Delegate.CreateDelegate(typeof(Action), context, "x"),
            AppDomain.CurrentDomain.GetAssemblies(),
            Assembly.GetCallingAssembly(),
            Assembly.GetEntryAssembly(),
            Assembly.GetExecutingAssembly(),
            type.GetField("x"),
            type.GetProperty("x"),
            type.GetMethod("x"),
            type.GetMember("x"),
            anything.Whatever,
            Microsoft.VisualBasic.Interaction.Shell("cmd")
        ];
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "X")]
    internal static extern void Reach(object target);
#pragma warning restore CA1416, CS8602, IL2026, IL2075, IL2070, IL3050, CA2200
}
