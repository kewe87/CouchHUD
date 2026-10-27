using System.Runtime.InteropServices;
using System.Security.Principal;
using CouchHud.Privileged;
using CouchHud.Service.Contracts;
using CouchHud.Service.Pipe;
using CouchHud.Service.Setup;

namespace CouchHud.Service.Tests;

/// <summary>Fixes from the P6 security review: handle inheritance (L2) and one form of update ids (L5).</summary>
public class ReviewFixTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeHandle handle, out uint flags);

    private const uint HandleFlagInherit = 0x1;

    [Fact]
    public void The_service_s_pipe_instances_are_not_inheritable()
    {
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var factory = new PipeFactory("CouchHud.Service.Test." + Guid.NewGuid().ToString("N"), $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;{me})S:(ML;;NWNR;;;ME)", 4);
        using var first = factory.Create(first: true);
        using var second = factory.Create(first: false);
        foreach (var pipe in new[] { first, second })
        {
            Assert.True(GetHandleInformation(pipe.SafePipeHandle, out var flags));
            Assert.Equal(0u, flags & HandleFlagInherit);
        }
    }

    [Fact]
    public void A_setup_is_started_without_inherited_handles_and_with_exactly_the_given_environment()
    {
        var temp = Path.Combine(Path.GetTempPath(), "p6e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        // An inheritable handle in this process: the child must not get it (bInheritHandles false). Not observable from the
        // child here; the start itself must work with it present, and the environment must be the built one.
        using var inheritable = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out, HandleInheritability.Inheritable);
        Environment.SetEnvironmentVariable("COR_PROFILER_P6_START_TEST", "{x}");
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // 9: a variable of this process leaked; 8: a user folder leaked; 7: TEMP as built; 1: no TEMP.
            var script = "/d /c \"if defined COR_PROFILER_P6_START_TEST (exit 9) else if defined USERPROFILE (exit 8) else if \"%TEMP%\"==\"" + temp + "\" (exit 7) else exit 1\"";
            using var process = new SystemSetupStarter().Start(cmd, script, temp, SetupEnvironment.Build(temp));
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(TimeSpan.FromSeconds(20)));
            Assert.Equal(7, process.ExitCode);

            // The image of a running process is read from the held handle (the setup check compares it with the staged path).
            using var running = new SystemSetupStarter().Start(cmd, "/d /c \"ping -n 3 127.0.0.1 >nul\"", temp, SetupEnvironment.Build(temp));
            Assert.Equal(cmd, Core.Updates.PathSafety.StripPrefix(running!.ImagePath!), ignoreCase: true);
            running.Kill();
            Assert.True(running.WaitForExit(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COR_PROFILER_P6_START_TEST", null);
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void The_environment_block_is_sorted_terminated_and_refuses_bad_names()
    {
        var block = SystemSetupStarter.EnvironmentBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" });
        Assert.Equal("A=1\0b=2\0", block);
        Assert.Throws<ArgumentException>(() => SystemSetupStarter.EnvironmentBlock(new Dictionary<string, string> { ["a=b"] = "1" }));
        Assert.Throws<ArgumentException>(() => SystemSetupStarter.EnvironmentBlock(new Dictionary<string, string> { ["a"] = "x\0y" }));
        Assert.Equal("\"C:\\s\\setup.exe\" /VERYSILENT", SystemSetupStarter.CommandLine(@"C:\s\setup.exe", "/VERYSILENT"));
        Assert.Throws<ArgumentException>(() => new SystemSetupStarter().Start("C:\\a\"b.exe", "", "C:\\", new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => new SystemSetupStarter().Start("setup.exe", "", "C:\\", new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("0B2A3F4E-1111-4C3D-9E8A-0000000000A1")]
    [InlineData(" 0b2a3f4e-1111-4c3d-9e8a-0000000000a1 ")]
    [InlineData("{0b2a3f4e-1111-4c3d-9e8a-0000000000a1}")]
    public void Update_ids_have_one_form_on_both_sides(string spelling)
    {
        const string canonical = "0b2a3f4e-1111-4c3d-9e8a-0000000000a1";
        Assert.Equal(canonical, FoundUpdate.NormalizeId(spelling));
        Assert.Equal(canonical, Updates.UpdateSearchCache.Normalize(spelling));
        var cache = new Updates.UpdateSearchCache();
        cache.Set([spelling]);
        Assert.True(cache.ContainsAll([canonical.ToUpperInvariant()]));
    }

    [Fact]
    public void Every_search_keeps_hidden_and_non_software_updates_out()
    {
        // The installation searches again with the same criteria (also when no ids are given).
        Assert.Equal("IsInstalled=0 and IsHidden=0 and Type='Software'", WindowsUpdateAgent.SearchCriteria);
    }
}
