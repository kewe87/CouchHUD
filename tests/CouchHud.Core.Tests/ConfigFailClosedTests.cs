using CouchHud.Core.Config;

namespace CouchHud.Core.Tests;

public class ConfigFailClosedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-config-").FullName;
    private string File_ => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);

    private const string LockedConfig = """{"couchLock":"code","passcodeHash":"hash-of-the-parents","integrationMode":"full","parental":{"enabled":true}}""";

    [Fact]
    public void A_broken_file_starts_locked_and_is_not_overwritten_with_defaults()
    {
        File.WriteAllText(File_, "{ \"couchLock\": \"code\", \"passcodeHash\": ");
        var damaged = File.ReadAllText(File_);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        Assert.Equal("windows", store.Current.CouchLock);
        // Changing something while it is broken keeps the file as it is, and the stand-in in memory as well.
        Assert.False(store.Update(c => c.Sounds = false));
        Assert.False(store.Update(c => c.Skin = "deepsea"));
        Assert.Equal(damaged, File.ReadAllText(File_));
        Assert.True(store.Current.Sounds);
        Assert.NotEqual("deepsea", store.Current.Skin);
        // A copy of the damaged file is kept next to it, as before.
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void A_repaired_file_is_taken_up_again_with_its_lock_and_rules()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new ConfigStore(_dir, 1, Quick);
        Assert.Equal(ConfigFault.Invalid, store.Fault);

        File.WriteAllText(File_, LockedConfig);
        store.Update(c => c.Sounds = false);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
        Assert.True(store.Current.Parental.Enabled);
        Assert.False(store.Current.Sounds);
        // And now it saves again, with the rules still in it.
        Assert.Contains("hash-of-the-parents", File.ReadAllText(File_));
    }

    [Fact]
    public void A_file_that_was_deleted_while_broken_stays_locked_until_the_folder_goes_as_a_whole()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new ConfigStore(_dir, 1, Quick);

        // Deleting the broken file is no way to an open couch: its copy shows that CouchHUD was set up here.
        File.Delete(File_);
        store.Update(c => c.Sounds = false);

        Assert.Equal(ConfigFault.Missing, store.Fault);
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.True(store.Current.Parental.Enabled);
        Assert.False(File.Exists(File_));
        Assert.False(new ConfigStore(_dir, 1, Quick).Fault is null);

        // Starting over on purpose: everything of the earlier install gone.
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
        store.Update(c => c.Sounds = false);
        Assert.Null(store.Fault);
        Assert.Equal("off", store.Current.CouchLock);
        Assert.True(File.Exists(File_));
    }

    [Theory]
    [InlineData("playtime.json")]
    [InlineData("playtime-kid1.json")]
    [InlineData("playtime.json.bak")]
    [InlineData("lockout-profile-kid1.json")]
    [InlineData("parental.key")]
    [InlineData("config.json.broken-20261020-123005")]
    [InlineData("config.json.recovery")]
    public void Settings_gone_after_an_earlier_install_are_no_first_start(string trace)
    {
        File.WriteAllText(Path.Combine(_dir, trace), "{}");

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Missing, store.Fault);
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.True(store.Current.Parental.Enabled);
        // No setup assistant, no notice on the stand-in: it could reset the lock and the parental controls.
        Assert.True(store.Current.SetupWizardDone > 0);
        Assert.True(store.Current.AcceptedTerms > 0);
        // No defaults written over the gap, and nothing saved while it lasts.
        Assert.False(File.Exists(File_));
        Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.CouchLock = "off"));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void A_refused_change_leaves_the_stand_in_locked_in_memory()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new ConfigStore(_dir, 1, Quick);
        Assert.Equal(ConfigFault.Invalid, store.Fault);

        // A caller that does not look at the answer (Update, not UpdateOrThrow) must not have opened the couch for this run.
        Assert.False(store.Update(c =>
        {
            c.CouchLock = "off";
            c.Parental.Enabled = false;
            c.PasscodeHash = "child";
        }));
        Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.CouchLock = "off"));
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.True(store.Current.Parental.Enabled);
        Assert.Equal("", store.Current.PasscodeHash);
        Assert.Equal("{ broken", File.ReadAllText(File_));

        // Only the recovery fields go on (into the stand-in and the side file).
        store.Update(c =>
        {
            c.ShellRemovedReason = "watcher";
            c.PendingShellRestore = true;
            c.CouchLock = "off";
        });
        Assert.Equal("watcher", store.Current.ShellRemovedReason);
        Assert.True(store.Current.PendingShellRestore);
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.True(File.Exists(File_ + ".recovery"));
    }

    [Fact]
    public void A_file_in_place_of_the_data_folder_counts_as_an_earlier_install()
    {
        var notAFolder = Path.Combine(_dir, "CouchHud");
        File.WriteAllText(notAFolder, "");
        Assert.Equal(notAFolder, ConfigStore.EarlierInstallTraces(notAFolder));
        Assert.Null(ConfigStore.EarlierInstallTraces(Path.Combine(_dir, "missing")));
        Assert.Null(ConfigStore.EarlierInstallTraces(_dir));
    }

    [JunctionFact]
    public void A_data_folder_that_is_a_junction_counts_as_an_earlier_install()
    {
        // An empty folder elsewhere, linked in as the data folder: listing it through the link would find nothing.
        var target = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        var link = Path.Combine(_dir, "CouchHud");
        Links.Junction(link, target);
        try
        {
            Assert.Equal(link, ConfigStore.EarlierInstallTraces(link));
            var store = new ConfigStore(link, 1, Quick);
            Assert.Equal(ConfigFault.Missing, store.Fault);
            Assert.Equal("windows", store.Current.CouchLock);
            // Also when its target is gone.
            foreach (var f in Directory.GetFiles(target)) File.Delete(f);
            Directory.Delete(target);
            Assert.Equal(link, ConfigStore.EarlierInstallTraces(link));
        }
        finally
        {
            // The link itself, not what it points to.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Settings_put_back_after_an_earlier_install_are_taken_up_again()
    {
        File.WriteAllText(Path.Combine(_dir, "playtime.json"), "{}");
        var store = new ConfigStore(_dir, 1, Quick);
        Assert.Equal(ConfigFault.Missing, store.Fault);

        File.WriteAllText(File_, LockedConfig);
        store.Update(c => c.Sounds = false);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
    }

    [Fact]
    public void A_good_copy_still_wins_over_the_traces()
    {
        File.WriteAllText(Path.Combine(_dir, "playtime.json"), "{}");
        File.WriteAllText(File_ + ".bak", LockedConfig);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.True(store.RestoredFromBackup);
        Assert.Equal("code", store.Current.CouchLock);
    }

    [Fact]
    public void A_broken_file_without_a_copy_shows_no_setup_assistant()
    {
        File.WriteAllText(File_, "{ broken");

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        Assert.True(store.Current.SetupWizardDone > 0);
        Assert.True(store.Current.AcceptedTerms > 0);
    }

    [Fact]
    public void A_file_that_is_held_by_another_program_is_not_replaced_by_defaults()
    {
        File.WriteAllText(File_, LockedConfig);
        ConfigStore store;
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new ConfigStore(_dir, 3, Quick);
            Assert.Equal(ConfigFault.Unreadable, store.Fault);
            Assert.Equal("windows", store.Current.CouchLock);
            // Nothing is written while it is unreadable (it could not be anyway, but a swap would have worked).
            store.Update(c => c.Sounds = false);
        }

        Assert.Equal(LockedConfig, File.ReadAllText(File_));
        // Once the other program lets go, the real file comes back.
        store.Update(c => c.Sounds = false);
        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
    }

    [Fact]
    public async Task A_file_that_is_held_for_a_moment_is_read_on_a_later_try()
    {
        File.WriteAllText(File_, LockedConfig);
        var hold = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            hold.Dispose();
        });

        // Generous: a busy CI runner can start the release task late. The test only needs "a later try reads it".
        var store = new ConfigStore(_dir, 400, TimeSpan.FromMilliseconds(25));
        await release;

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
    }

    [Fact]
    public void No_file_is_a_normal_first_start()
    {
        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.Equal("off", store.Current.CouchLock);
        Assert.True(File.Exists(File_));
    }

    [Fact]
    public void Saving_goes_through_a_temp_file_of_its_own_per_process()
    {
        var store = new ConfigStore(_dir, 1, Quick);

        store.Update(c => c.Sounds = false);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(File.Exists(File_ + ".tmp"));
        Assert.Contains($".{Environment.ProcessId}.tmp", $"{File_}.{Environment.ProcessId}.tmp");
        Assert.False(new ConfigStore(_dir, 1, Quick).Current.Sounds);
    }

    [Fact]
    public void A_valid_file_loads_unchanged()
    {
        File.WriteAllText(File_, LockedConfig);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("full", store.Current.IntegrationMode);
    }
}
