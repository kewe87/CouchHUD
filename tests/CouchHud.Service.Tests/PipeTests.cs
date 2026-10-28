using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;
using CouchHud.Service.Pipe;

namespace CouchHud.Service.Tests;

/// <summary>The pipe's security descriptor, and the native pipe under a random test name (no service, nothing elevated).</summary>
public class PipeTests
{
    private const int FileAppendData = 0x4;   // = FILE_CREATE_PIPE_INSTANCE on a pipe
    private const int FileWriteEa = 0x10;

    [Fact]
    public void The_pipe_sddl_parses_with_the_deny_ace_first()
    {
        var descriptor = new RawSecurityDescriptor(ServiceHardening.PipeSddl);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), descriptor.Owner);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));

        var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.Equal(4, aces.Count);
        Assert.Equal(AceQualifier.AccessDenied, aces[0].AceQualifier);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), aces[0].SecurityIdentifier);
        Assert.All(aces.Skip(1), a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
    }

    [Fact]
    public void Interactive_users_may_read_and_write_but_not_create_instances()
    {
        var descriptor = new RawSecurityDescriptor(ServiceHardening.PipeSddl);
        var interactive = descriptor.DiscretionaryAcl!.Cast<CommonAce>()
            .Single(a => a.SecurityIdentifier == new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        Assert.Equal(0x120183, interactive.AccessMask);
        Assert.Equal(0, interactive.AccessMask & FileAppendData);
        Assert.Equal(0, interactive.AccessMask & FileWriteEa);
        // Only SYSTEM, Administrators and INTERACTIVE are allowed anything.
        var allowed = descriptor.DiscretionaryAcl!.Cast<CommonAce>().Where(a => a.AceQualifier == AceQualifier.AccessAllowed).Select(a => a.SecurityIdentifier.Value).Order();
        Assert.Equal(["S-1-5-18", "S-1-5-32-544", "S-1-5-4"], allowed);
    }

    [Fact]
    public void The_sddl_carries_a_medium_no_write_up_no_read_up_label()
    {
        var descriptor = new RawSecurityDescriptor(ServiceHardening.PipeSddl);
        var label = Assert.Single(descriptor.SystemAcl!.Cast<GenericAce>());
        // SYSTEM_MANDATORY_LABEL_ACE (0x11) is not a type .NET models: check the binary form.
        var bytes = new byte[label.BinaryLength];
        label.GetBinaryForm(bytes, 0);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(3, BitConverter.ToInt32(bytes, 4));   // SYSTEM_MANDATORY_LABEL_NO_WRITE_UP | NO_READ_UP: low IL may not even read
        Assert.EndsWith("S-1-16-8192", new SecurityIdentifier(bytes, 8).Value);
    }

    [Fact]
    public void The_service_sddl_lets_users_start_but_not_stop()
    {
        var descriptor = new RawSecurityDescriptor(ServiceHardening.ServiceSddl);
        var interactive = descriptor.DiscretionaryAcl!.Cast<CommonAce>()
            .Single(a => a.SecurityIdentifier == new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        const int serviceStop = 0x20;   // SERVICE_STOP = WP
        const int serviceStart = 0x10;  // SERVICE_START = RP
        Assert.Equal(0, interactive.AccessMask & serviceStop);
        Assert.NotEqual(0, interactive.AccessMask & serviceStart);
    }

    // A descriptor the test process can apply: it cannot make SYSTEM the owner, and it must be able to connect itself.
    private static string TestSddl()
    {
        var me = WindowsIdentity.GetCurrent().User!.Value;
        return $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;{me})S:(ML;;NWNR;;;ME)";
    }

    private static string TestPipeName() => "CouchHud.Service.Test." + Guid.NewGuid().ToString("N");

    private static NamedPipeClientStream Client(string name) => new(".", name,
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes | PipeAccessRights.Synchronize,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);

    [Fact]
    public void The_service_sddl_and_the_test_sddl_carry_the_same_label()
    {
        var label = ServiceHardening.PipeSddl[ServiceHardening.PipeSddl.IndexOf("S:", StringComparison.Ordinal)..];
        Assert.Equal("S:(ML;;NWNR;;;ME)", label);
        Assert.EndsWith(label, TestSddl());
    }

    [Fact]
    public void A_name_that_exists_already_is_squatting()
    {
        var name = TestPipeName();
        // Someone else's pipe under the name: an ordinary .NET server with default security.
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message);
        var factory = new PipeFactory(name, TestSddl(), 4);
        Assert.Throws<PipeSquattedException>(() => factory.Create(first: true));
    }

    [Fact]
    public void The_first_instance_is_exclusive_and_further_ones_join_it()
    {
        var factory = new PipeFactory(TestPipeName(), TestSddl(), 4);
        using var first = factory.Create(first: true);
        Assert.Throws<PipeSquattedException>(() => factory.Create(first: true));
        using var second = factory.Create(first: false);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task The_identifier_reads_the_callers_token_not_the_request()
    {
        var name = TestPipeName();
        var factory = new PipeFactory(name, TestSddl(), 4);
        using var server = factory.Create(first: true);
        using var client = Client(name);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accept;

        var caller = await Task.Run(() => new PipeCallerIdentifier(new DenyLog(new CapturingSink(), StopwatchClock.Instance)).Identify(server.SafePipeHandle));
        Assert.NotNull(caller);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, caller.Sid);
        Assert.Equal(Environment.ProcessId, caller.ProcessId);
        Assert.Equal(caller.PipeSessionId, caller.TokenSessionId);
        Assert.True(caller.IntegrityLevel >= IntegrityLevels.Medium);
        Assert.False(caller.IsNetwork);
        Assert.False(caller.IsAppContainer);
        Assert.NotEqual(0, caller.LogonId);
        Assert.False(caller.IsRestricted);
        // After the identification the thread runs as itself again.
        Assert.Null(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    [Fact]
    public async Task A_client_with_a_restricted_token_is_seen_as_restricted_and_refused()
    {
        var name = TestPipeName();
        // Everyone may connect, so the restricting SID (Everyone) passes the second access check of a restricted token.
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var factory = new PipeFactory(name, $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;{me})(A;;0x120183;;;WD)S:(ML;;NWNR;;;ME)", 4);
        using var server = factory.Create(first: true);
        using var restricted = RestrictedToken.Create();
        var accept = server.WaitForConnectionAsync();
        // A restricted token cannot read assembly files (the framework folder grants nothing to the restricting SID): nothing may be
        // loaded or compiled while impersonating, or the load fails, and that failure sticks for the whole test process. Whether a load
        // was due depended on what the tests running in parallel had touched before: the flaky part. WindowsIdentity.RunImpersonated
        // runs managed code of its own under the token (execution context, AsyncLocal handler, the callback); OpenAs impersonates by
        // itself, with every piece compiled beforehand, and only the raw CreateFileW runs as the restricted token.
        RestrictedToken.Prepare();
        var (connected, error) = RestrictedToken.OpenAs(restricted, name);
        using (connected)
        {
            Assert.False(connected.IsInvalid, $"CreateFileW as the restricted token failed: Win32 {error}");
            await accept.WaitAsync(TimeSpan.FromSeconds(10));

            var sink = new CapturingSink();
            var caller = await Task.Run(() => new PipeCallerIdentifier(new DenyLog(sink, StopwatchClock.Instance)).Identify(server.SafePipeHandle));
            Assert.True(caller is not null, "identification failed: " + string.Join(" | ", sink.Lines));
            Assert.True(caller.IsRestricted);
            Assert.Equal("restricted token", Authorizer.CheckCaller(caller));
        }
        // The test thread runs as itself again.
        Assert.Null(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    private static class RestrictedToken
    {
        public static Microsoft.Win32.SafeHandles.SafeAccessTokenHandle Create()
        {
            const uint access = 0x0001 | 0x0002 | 0x0004 | 0x0008;   // ASSIGN_PRIMARY | DUPLICATE | IMPERSONATE | QUERY
            // The pseudo-handle, never Process.GetCurrentProcess().Handle: that raw handle belongs to a Process object nothing keeps
            // alive, and a GC from the tests running in parallel finalized it and closed the handle before OpenProcessToken ran
            // (ERROR_INVALID_HANDLE, the remaining flaky part).
            if (!OpenProcessToken(GetCurrentProcess(), access, out var token)) throw new System.ComponentModel.Win32Exception();
            using (token)
            {
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                var bytes = new byte[everyone.BinaryLength];
                everyone.GetBinaryForm(bytes, 0);
                var sid = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, sid, bytes.Length);
                    var restrict = new[] { new SidAndAttributes { Sid = sid, Attributes = 0 } };
                    if (!CreateRestrictedToken(token, 0, 0, null, 0, IntPtr.Zero, 1, restrict, out var result)) throw new System.ComponentModel.Win32Exception();
                    return result;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(sid);
                }
            }
        }

        /// <summary>Opens the client end with exactly the rights the DACL grants, at Identification level.</summary>
        public static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string pipeName) => OpenPath(@"\\.\pipe\" + pipeName);

        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenPath(string path) =>
            CreateFileW(path, 0x100183, 0, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x00100000 | 0x00010000 /*SQOS_PRESENT | IDENTIFICATION*/, IntPtr.Zero);

        /// <summary>
        /// Compiles everything <see cref="OpenAs"/> runs, outside any impersonation: the methods, and the interop stubs by one harmless
        /// call each (a pipe that does not exist; RevertToSelf while not impersonating changes nothing).
        /// </summary>
        public static void Prepare()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(RestrictedToken).GetMethod(nameof(OpenAs), any)!.MethodHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(RestrictedToken).GetMethod(nameof(OpenPath), any)!.MethodHandle);
            Open("CouchHud.Service.Test.none").Dispose();
            _ = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (!RevertToSelf()) throw new System.ComponentModel.Win32Exception();
        }

        /// <summary>
        /// The client end opened as <paramref name="token"/>: impersonate, CreateFileW, revert, on this thread with nothing else in
        /// between (call <see cref="Prepare"/> first). The handle, and the Win32 error for an invalid one.
        /// </summary>
        public static (Microsoft.Win32.SafeHandles.SafeFileHandle Handle, int Error) OpenAs(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, string pipeName)
        {
            var path = @"\\.\pipe\" + pipeName;
            if (!ImpersonateLoggedOnUser(token)) throw new System.ComponentModel.Win32Exception();
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            int error;
            try
            {
                handle = OpenPath(path);
                error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            }
            finally
            {
                // A test thread left running as the restricted token would break every later test on it.
                if (!RevertToSelf()) Environment.FailFast("RevertToSelf failed in the restricted-token pipe test");
            }
            return (handle, error);
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ImpersonateLoggedOnUser(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool RevertToSelf();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateRestrictedToken(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, uint flags, uint disableCount, SidAndAttributes[]? disable,
            uint deleteCount, IntPtr delete, uint restrictCount, SidAndAttributes[] restrict, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle result);
    }

    [Fact]
    public async Task A_ping_over_the_real_pipe_gets_an_answer()
    {
        var name = TestPipeName();
        var harness = new Harness();
        var factory = new PipeFactory(name, TestSddl(), 5);
        var server = new PipeServer(factory, new PipeCallerIdentifier(harness.DenyLog), harness.Connections, harness.Log, 4);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);

        using (var client = Client(name))
        {
            await client.ConnectAsync(5000);
            client.ReadMode = PipeTransmissionMode.Message;
            await FrameCodec.WriteAsync(client, ProtocolJson.SerializeRequest(Operation.Ping));
            var answer = await FrameCodec.ReadAsync(client, TimeSpan.FromSeconds(5));
            var response = ProtocolJson.DeserializeResponse(Harness.Strip(answer!))!;
            // On a desktop the test runs interactively (ok); a CI runner's logon may not be interactive (denied). Either way an answer.
            Assert.True(response.Ok || response.Code == ErrorCodes.Denied, response.Code);
        }

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void The_redactor_strips_secrets_and_control_characters()
    {
        var grant = Convert.ToBase64String(new byte[32]);
        var hash = "pbkdf2-sha256$100000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
        var text = RedactingEventSink.Redact($"grant {grant} hash {hash} password=hunter2 \"code\":\"abab\"\r\nE forged line");
        Assert.DoesNotContain(grant, text);
        Assert.DoesNotContain("100000$", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abab", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.True(RedactingEventSink.Redact(new string('x', 10_000)).Length <= RedactingEventSink.MaxLength + 1);
        Assert.Equal("", RedactingEventSink.Redact(null));
    }

    [Fact]
    public void The_redacting_sink_redacts_both_levels()
    {
        var inner = new CapturingSink();
        var sink = new RedactingEventSink(inner);
        sink.Info("password: hunter2");
        sink.Error("passwort=hunter2");
        Assert.DoesNotContain("hunter2", inner.All);
        Assert.Equal(2, inner.Lines.Count);
    }
}
