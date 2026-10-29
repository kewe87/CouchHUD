using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>Something a plugin's code uses that needs a capability it may not have declared.</summary>
public sealed record LintFinding(string File, string Capability, string Detail);

/// <summary>
/// A look at a plugin package before it is installed: which files it brings and what its code references (System.Reflection.Metadata, nothing
/// is loaded or run). It finds what an honest plugin forgot to declare and turns away the obvious; it is NOT a sandbox. Plugins run inside
/// CouchHUD with its rights, and code that hides what it calls (reflection by name, code built at run time, a native library) gets past any
/// such check; that is why run-time code ("dynamicCode") is barred for third-party developers and every third-party package is pinned to its
/// developer's key, and why the warning before installing says so.
/// </summary>
public static class PluginLint
{
    /// <summary>Never in a plugin package, whatever it declares: programs and scripts Windows would run.</summary>
    public static readonly IReadOnlySet<string> RefusedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".psm1", ".sys", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".com",
        ".lnk", ".url", ".hta", ".cpl", ".reg", ".inf", ".application", ".appref-ms", ".jar"
    };

    /// <summary>What a third-party package may contain at all (a native .dll only with the "native" capability).</summary>
    public static readonly IReadOnlySet<string> ThirdPartyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".json", ".png", ".md", ".txt", ".pdb"
    };

    public const long MaxScannedBytes = 64L * 1024 * 1024;

    /// <summary>Types whose use needs a capability: namespace + type name → capability.</summary>
    private static readonly (string Namespace, string? Type, string Capability)[] TypeRules =
    [
        ("System.Diagnostics", "Process", PluginCapabilities.Process),
        ("System.Diagnostics", "ProcessStartInfo", PluginCapabilities.Process),
        ("Microsoft.Win32", "Registry", PluginCapabilities.Registry),
        ("Microsoft.Win32", "RegistryKey", PluginCapabilities.Registry),
        ("System.Net.Sockets", null, Network),
        ("System.Net.Http", "HttpClient", Network),
        ("System.Net.Http", "HttpClientHandler", Network),
        ("System.Net.Http", "SocketsHttpHandler", Network),
        ("System.Net.Http", "HttpMessageInvoker", Network),
        ("System.Net", "WebClient", Network),
        ("System.Net", "WebRequest", Network),
        ("System.Net", "HttpWebRequest", Network),
        ("System.Net", "HttpListener", Network),
        ("System.Net.WebSockets", "ClientWebSocket", Network),
        ("System.Net.Quic", null, Network),
        ("System.Reflection.Emit", null, PluginCapabilities.DynamicCode),
        ("System.Runtime.Loader", "AssemblyLoadContext", PluginCapabilities.DynamicCode),
        ("System.Runtime.InteropServices", "NativeLibrary", PluginCapabilities.Native),
        ("System.Linq.Expressions", "LambdaExpression", PluginCapabilities.DynamicCode),
        ("Microsoft.VisualBasic", "Interaction", PluginCapabilities.Process),
        // Reaching past the SDK by reflection: run-time code for a third party (see Reflection).
        ("System.Linq.Expressions", null, Reflection),
        ("Microsoft.CSharp.RuntimeBinder", null, Reflection),
        ("Microsoft.VisualBasic.CompilerServices", "NewLateBinding", Reflection),
        ("Microsoft.VisualBasic.CompilerServices", "LateBinding", Reflection),
        ("System.Runtime.CompilerServices", "UnsafeAccessorAttribute", Reflection),
        ("System.Runtime.CompilerServices", "UnsafeAccessorTypeAttribute", Reflection),
        ("System.Runtime.CompilerServices", "Unsafe", Reflection),
        ("System.Reflection", "DispatchProxy", Reflection),
        // Page actions need SDK 4: code that uses them must say so in plugin.json (SdkActions).
        ("CouchHud.Sdk", "PluginPageAction", SdkActions),
        ("CouchHud.Sdk", "PluginPageButton", SdkActions),
        // The user's DPAPI: every plugin runs as the same Windows user, so a plugin that calls it can open anything CouchHUD or another
        // plugin protected for that user. Only CouchHUD's own plugins (the host does the encrypting for the others via SetSecret).
        ("System.Security.Cryptography", "ProtectedData", Dpapi),
        ("System.Security.Cryptography", "DataProtectionScope", Dpapi),
    ];

    /// <summary>Single members: type → member names → capability.</summary>
    private static readonly (string Namespace, string Type, string[] Members, string Capability)[] MemberRules =
    [
        ("System.Reflection", "Assembly", ["Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom", "LoadWithPartialName", "GetType"], PluginCapabilities.DynamicCode),
        ("System.Reflection", "MethodBase", ["Invoke"], PluginCapabilities.DynamicCode),
        ("System.Reflection", "MethodInfo", ["Invoke", "CreateDelegate"], PluginCapabilities.DynamicCode),
        ("System.Reflection", "ConstructorInfo", ["Invoke"], PluginCapabilities.DynamicCode),
        ("System", "Type", ["GetType", "GetTypeFromProgID", "GetTypeFromCLSID", "InvokeMember"], PluginCapabilities.DynamicCode),
        ("System", "Activator", ["CreateInstance", "CreateInstanceFrom", "CreateComInstanceFrom"], PluginCapabilities.DynamicCode),
        ("System", "AppDomain", ["Load", "ExecuteAssembly", "ExecuteAssemblyByName", "CreateInstanceAndUnwrap", "CreateInstanceFromAndUnwrap"], PluginCapabilities.DynamicCode),
        ("System.Runtime.InteropServices", "Marshal", ["GetDelegateForFunctionPointer", "GetFunctionPointerForDelegate"], PluginCapabilities.Native),
        ("System.Linq.Expressions", "Expression`1", ["Compile"], PluginCapabilities.DynamicCode),
        ("System.Reflection", "FieldInfo", ["GetValue", "SetValue", "GetValueDirect", "SetValueDirect", "GetFieldFromHandle"], Reflection),
        ("System.Reflection", "PropertyInfo", ["GetValue", "SetValue", "GetGetMethod", "GetSetMethod", "get_GetMethod", "get_SetMethod"], Reflection),
        ("System.Reflection", "MethodBase", ["GetMethodFromHandle"], Reflection),
        ("System.Reflection", "Assembly", ["GetTypes", "get_DefinedTypes", "get_ExportedTypes", "GetExportedTypes", "GetForwardedTypes", "get_Modules", "GetModules", "GetLoadedModules", "GetModule",
            "GetCallingAssembly", "GetEntryAssembly", "GetExecutingAssembly"], Reflection),
        ("System", "AppDomain", ["GetAssemblies"], Reflection),
        ("System.Reflection", "Module", ["GetTypes", "GetType", "FindTypes", "GetField", "GetFields", "GetMethod", "GetMethods", "ResolveField", "ResolveMethod", "ResolveMember", "ResolveType"], Reflection),
        ("System.Reflection", "TypeInfo", ["get_DeclaredFields", "get_DeclaredMethods", "get_DeclaredProperties", "get_DeclaredMembers", "get_DeclaredConstructors", "get_DeclaredNestedTypes", "GetDeclaredField", "GetDeclaredMethod", "GetDeclaredMethods", "GetDeclaredProperty", "GetDeclaredNestedType"], Reflection),
        ("System.Reflection", "TypeExtensions", ["GetField", "GetFields", "GetProperty", "GetProperties", "GetMethod", "GetMethods", "GetMember", "GetMembers", "GetConstructor", "GetConstructors"], Reflection),
        ("System.Reflection", "RuntimeReflectionExtensions", ["GetRuntimeField", "GetRuntimeFields", "GetRuntimeProperty", "GetRuntimeProperties", "GetRuntimeMethod", "GetRuntimeMethods", "GetMethodInfo"], Reflection),
        ("System", "Delegate", ["CreateDelegate"], Reflection),
        ("System", "Type", ["GetField", "GetFields", "GetProperty", "GetProperties", "GetMethod", "GetMethods", "GetMember", "GetMembers", "GetConstructor", "GetConstructors", "GetNestedType", "GetNestedTypes", "GetDefaultMembers", "FindMembers", "get_TypeInitializer"], Reflection),
        ("Microsoft.VisualBasic", "Interaction", ["CallByName", "CreateObject", "GetObject"], Reflection),
        ("CouchHud.Sdk", "PluginPage", ["set_Actions"], SdkActions),
        ("CouchHud.Sdk", "PluginSetting", ["set_Page"], SdkActions),
    ];

    /// <summary>
    /// Stands for "uses page actions (SDK 4)". A finding counts when the SDK the package declares is below <see cref="PluginPageAction.MinSdk"/>:
    /// the host ignores the actions of such a plugin, so it is told at install time instead of finding nothing in the footer.
    /// </summary>
    public const string SdkActions = "SDK 4 (page actions)";

    /// <summary>Stands for "any network capability" (loopback or internet).</summary>
    private const string Network = "network";

    /// <summary>
    /// Reflection into code that is not the plugin's own (the host's types reached through any object the SDK hands out, private fields, code
    /// built from expression trees, the dynamic binder, unsafe accessors). Ordinary .NET for CouchHUD's own plugins and for a local plugin the
    /// parents approved; for a third-party developer it counts as run-time code (<see cref="PluginCapabilities.DynamicCode"/>), which a third
    /// party can never declare. Still not a sandbox: code that hides what it calls gets past any such list.
    /// </summary>
    private const string Reflection = "reflection";

    /// <summary>The Windows user's data protection (ProtectedData): refused for third-party developers, free for CouchHUD's own plugins.</summary>
    private const string Dpapi = "dpapi";

    /// <summary>
    /// Checks a package before it is installed. Throws a <see cref="CatalogException"/> listing what is wrong: a refused file type, a file a
    /// third-party package may not contain, unreadable code, or code that needs a capability the package did not declare.
    /// </summary>
    /// <param name="legacy">A package without a statement from before SDK 3 (catalog without statement): only the file types are checked.</param>
    public static void CheckZip(string zipPath, PluginOrigin origin, IReadOnlyCollection<string> capabilities, bool legacy = false, int sdk = int.MaxValue)
    {
        // Nobody may write to the file while it is read.
        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckZip(file, origin, capabilities, legacy, sdk);
    }

    /// <summary>The same from an open stream, so the caller can check and unpack one and the same handle.</summary>
    public static void CheckZip(Stream zipStream, PluginOrigin origin, IReadOnlyCollection<string> capabilities, bool legacy = false, int sdk = int.MaxValue)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var findings = new List<LintFinding>();
        var problems = new List<string>();
        var refs = new List<(string File, string Assembly)>();
        var defs = new List<(string File, string Assembly)>();
        long scanned = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (CheckFileType(name, origin) is { } typeProblem)
            {
                problems.Add(typeProblem);
                continue;
            }
            if (legacy || !Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            scanned += entry.Length;
            if (entry.Length > MaxScannedBytes || scanned > MaxScannedBytes)
            {
                problems.Add(Strings.Get("plugin.lint.tooLarge", name));
                continue;
            }
            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            stream.CopyTo(buffer);
            ScanInto(buffer.ToArray(), name, findings, problems, refs, defs);
        }
        CheckReferences(refs, defs, origin, problems);
        Throw(Evaluate(findings, origin, capabilities, sdk), problems);
    }

    /// <summary>The same for an unpacked folder (a package installed by hand and approved in expert mode).</summary>
    public static void CheckFolder(string dir, PluginOrigin origin, IReadOnlyCollection<string> capabilities, int sdk = int.MaxValue)
    {
        var findings = new List<LintFinding>();
        var problems = new List<string>();
        var refs = new List<(string File, string Assembly)>();
        var defs = new List<(string File, string Assembly)>();
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (CheckFileType(name, origin) is { } typeProblem)
            {
                problems.Add(typeProblem);
                continue;
            }
            if (!Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length > MaxScannedBytes)
            {
                problems.Add(Strings.Get("plugin.lint.tooLarge", name));
                continue;
            }
            ScanInto(File.ReadAllBytes(file), name, findings, problems, refs, defs);
        }
        CheckReferences(refs, defs, origin, problems);
        Throw(Evaluate(findings, origin, capabilities, sdk), problems);
    }

    private static void Throw(IReadOnlyList<LintFinding> undeclared, List<string> problems)
    {
        foreach (var group in undeclared.GroupBy(f => f.Capability))
            problems.Add(Strings.Get("plugin.lint.undeclared", group.Key, string.Join(", ", group.Select(f => f.Detail).Distinct().Take(3))));
        if (problems.Count > 0) throw new CatalogException(string.Join(" · ", problems.Distinct().Take(6)));
    }

    /// <summary>
    /// The assemblies a package's code may reference. Never CouchHUD's own besides the SDK (whatever the origin: the loader refuses them as
    /// well). For a third-party developer only the SDK, the .NET base library and the package's own assemblies: nothing of the host.
    /// </summary>
    public static string? CheckReference(string assembly, PluginOrigin origin, IReadOnlySet<string> own)
    {
        if (PluginManager.HostAssemblies.Contains(assembly)) return "plugin.lint.hostAssembly";
        if (origin != PluginOrigin.ThirdParty) return null;
        if (assembly.Equals("CouchHud.Sdk", StringComparison.OrdinalIgnoreCase) || IsBaseLibrary(assembly) || own.Contains(assembly)) return null;
        return "plugin.lint.assemblyRef";
    }

    private static bool IsBaseLibrary(string assembly) =>
        // Not Microsoft.VisualBasic(.Core): its late binder and Interaction.Shell reach past every rule above.
        assembly is "System" or "mscorlib" or "netstandard" or "Microsoft.CSharp"
        || assembly.StartsWith("System.", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    private static void CheckReferences(List<(string File, string Assembly)> refs, List<(string File, string Assembly)> defs, PluginOrigin origin, List<string> problems)
    {
        // The package's own assemblies by the names they define (what the loader resolves), not by their file names: a file renamed to what
        // it references does not make that assembly the package's own. An assembly that calls itself like one of CouchHUD's is refused.
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, assembly) in defs)
        {
            if (PluginManager.HostAssemblies.Contains(assembly)) problems.Add(Strings.Get("plugin.lint.hostAssembly", file, assembly));
            else own.Add(assembly);
        }
        foreach (var (file, assembly) in refs)
        {
            if (CheckReference(assembly, origin, own) is { } key) problems.Add(Strings.Get(key, file, assembly));
        }
    }

    /// <summary>Null when the file may be in a package of this origin, else the reason.</summary>
    public static string? CheckFileType(string name, PluginOrigin origin)
    {
        var ext = Path.GetExtension(name);
        if (RefusedExtensions.Contains(ext)) return Strings.Get("plugin.lint.refusedFile", name);
        if (origin == PluginOrigin.ThirdParty && !ThirdPartyExtensions.Contains(ext)) return Strings.Get("plugin.lint.refusedFile", name);
        return null;
    }

    /// <summary>The findings that the declared capabilities do not cover (any network capability covers "network"). Run-time code is never covered for a third party.</summary>
    public static IReadOnlyList<LintFinding> Evaluate(IEnumerable<LintFinding> findings, PluginOrigin origin, IReadOnlyCollection<string> capabilities, int sdk = int.MaxValue) =>
        findings.Where(f => f.Capability switch
        {
            Network => !capabilities.Contains(PluginCapabilities.NetworkLoopback) && !capabilities.Contains(PluginCapabilities.NetworkLan) && !capabilities.Contains(PluginCapabilities.NetworkInternet),
            Reflection => origin == PluginOrigin.ThirdParty,
            Dpapi => origin == PluginOrigin.ThirdParty,
            SdkActions => sdk < PluginPageAction.MinSdk,
            PluginCapabilities.DynamicCode when origin == PluginOrigin.ThirdParty => true,
            var cap => !capabilities.Contains(cap)
        }).Select(f => f.Capability == Reflection ? f with { Capability = PluginCapabilities.DynamicCode } : f).ToList();

    /// <summary>What one file's code references. A file that is not a readable .NET assembly is native code.</summary>
    public static IReadOnlyList<LintFinding> Scan(byte[] image, string name)
    {
        var findings = new List<LintFinding>();
        var problems = new List<string>();
        ScanInto(image, name, findings, problems, [], null);
        if (problems.Count > 0) findings.Add(new LintFinding(name, PluginCapabilities.Native, "unreadable"));
        return findings;
    }

    /// <summary>The names of the assemblies a .NET file references (empty for anything else).</summary>
    public static IReadOnlyList<string> References(byte[] image)
    {
        var refs = new List<(string File, string Assembly)>();
        ScanInto(image, "", [], [], refs, null);
        return refs.Select(r => r.Assembly).ToList();
    }

    private static void ScanInto(byte[] image, string name, List<LintFinding> findings, List<string> problems, List<(string File, string Assembly)> refs,
        List<(string File, string Assembly)>? defs)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(image));
            if (!pe.HasMetadata)
            {
                // A native library: CouchHUD cannot see what it does at all.
                findings.Add(new LintFinding(name, PluginCapabilities.Native, Path.GetFileName(name)));
                return;
            }
            var md = pe.GetMetadataReader();
            if (md.IsAssembly) defs?.Add((name, md.GetString(md.GetAssemblyDefinition().Name)));
            foreach (var handle in md.AssemblyReferences) refs.Add((name, md.GetString(md.GetAssemblyReference(handle).Name)));
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    var import = method.GetImport();
                    var module = import.Module.IsNil ? "?" : md.GetString(md.GetModuleReference(import.Module).Name);
                    findings.Add(new LintFinding(name, PluginCapabilities.Native, $"{module}!{md.GetString(method.Name)}"));
                }
            }
            if (System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetTableRowCount(md, System.Reflection.Metadata.Ecma335.TableIndex.ModuleRef) > 0) findings.Add(new LintFinding(name, PluginCapabilities.Native, "moduleref"));

            foreach (var handle in md.TypeReferences)
            {
                var (ns, type) = FullName(md, handle);
                foreach (var rule in TypeRules)
                {
                    if (ns == rule.Namespace && (rule.Type is null || type == rule.Type))
                        findings.Add(new LintFinding(name, rule.Capability, $"{ns}.{type}"));
                }
            }
            foreach (var handle in md.MemberReferences)
            {
                var member = md.GetMemberReference(handle);
                // A member of a generic type (List<T>.Add, Expression<T>.Compile) hangs on a type specification: counted as its generic type.
                if (ParentType(md, member.Parent) is not { } parent) continue;
                var (ns, type) = FullName(md, parent);
                var memberName = md.GetString(member.Name);
                foreach (var rule in MemberRules)
                {
                    if (ns == rule.Namespace && type == rule.Type && rule.Members.Contains(memberName))
                        findings.Add(new LintFinding(name, rule.Capability, $"{type}.{memberName}"));
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            problems.Add(Strings.Get("plugin.lint.unreadable", name));
        }
    }

    /// <summary>The type reference a member reference hangs on: directly, or the generic type of a type specification (GENERICINST CLASS/VALUETYPE).</summary>
    private static TypeReferenceHandle? ParentType(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference) return (TypeReferenceHandle)parent;
        if (parent.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        var code = blob.ReadByte();
        if (code == 0x15) code = blob.ReadByte(); // GENERICINST
        if (code is not (0x11 or 0x12)) return null; // VALUETYPE, CLASS
        var handle = blob.ReadTypeHandle();
        return handle.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)handle : null;
    }

    /// <summary>Namespace and name of a type reference; a nested type counts with the namespace of its outermost type.</summary>
    private static (string Namespace, string Type) FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        var depth = 0;
        while (type.ResolutionScope.Kind == HandleKind.TypeReference && depth++ < 16)
        {
            type = md.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
        }
        var outer = md.GetString(type.Name);
        return (md.GetString(type.Namespace), depth > 0 ? outer : name);
    }
}
