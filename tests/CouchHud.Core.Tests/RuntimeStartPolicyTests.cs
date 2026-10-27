using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class RuntimeStartPolicyTests
{
    private const string Folder = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.80";

    private static RuntimeStartState State(bool strict = false, string? folder = Folder, int failures = 0, bool newer = false,
        bool setAside = false, bool loader = false) => new(strict, folder, failures, newer, setAside, loader);

    [Fact]
    public void The_machine_wide_runtime_comes_first()
    {
        Assert.Equal(RuntimeStartAction.Machine, RuntimeStartPolicy.Next(State()));
        Assert.Equal(RuntimeStartAction.Machine, RuntimeStartPolicy.Next(State(strict: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_machine_wide_runtime_the_loader_chooses_whatever_the_lock(bool strict)
    {
        Assert.Equal(RuntimeStartAction.Loader, RuntimeStartPolicy.Next(State(strict, folder: null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_failure_is_tried_again_on_a_fresh_choice(bool strict)
    {
        Assert.Equal(RuntimeStartAction.Machine, RuntimeStartPolicy.Next(State(strict, failures: 1)));
    }

    [Fact]
    public void A_locked_couch_never_falls_back_to_the_loader()
    {
        Assert.Equal(RuntimeStartAction.FailClosed, RuntimeStartPolicy.Next(State(strict: true, failures: 2)));
        Assert.Equal(RuntimeStartAction.FailClosed, RuntimeStartPolicy.Next(State(strict: true, failures: 3, setAside: true)));
    }

    [Fact]
    public void Without_a_lock_the_loader_is_tried_once_then_the_error_goes_up()
    {
        Assert.Equal(RuntimeStartAction.Loader, RuntimeStartPolicy.Next(State(failures: 2)));
        Assert.Equal(RuntimeStartAction.Fail, RuntimeStartPolicy.Next(State(failures: 2, loader: true)));
    }

    [Fact]
    public void A_failing_loader_ends_the_start()
    {
        Assert.Equal(RuntimeStartAction.Fail, RuntimeStartPolicy.Next(State(folder: null, loader: true)));
        Assert.Equal(RuntimeStartAction.Fail, RuntimeStartPolicy.Next(State(strict: true, folder: null, loader: true)));
    }

    [Fact]
    public void A_profile_from_a_newer_runtime_is_put_aside_once_then_tried_again()
    {
        Assert.Equal(RuntimeStartAction.SetAsideProfile, RuntimeStartPolicy.Next(State(strict: true, failures: 1, newer: true)));
        Assert.Equal(RuntimeStartAction.Machine, RuntimeStartPolicy.Next(State(strict: true, failures: 1, setAside: true)));
        Assert.Equal(RuntimeStartAction.Machine, RuntimeStartPolicy.Next(State(strict: true, failures: 2, setAside: true)));
        // Never twice, even when the marker still says newer.
        Assert.Equal(RuntimeStartAction.FailClosed, RuntimeStartPolicy.Next(State(strict: true, failures: 3, newer: true, setAside: true)));
    }

    [Fact]
    public void Without_a_machine_wide_folder_after_a_failure_nothing_is_put_aside()
    {
        Assert.Equal(RuntimeStartAction.FailClosed, RuntimeStartPolicy.Next(State(strict: true, folder: null, failures: 1, newer: true)));
        Assert.Equal(RuntimeStartAction.Loader, RuntimeStartPolicy.Next(State(folder: null, failures: 1, newer: true)));
    }

    [Theory]
    [InlineData("999.0.0.0", true)]
    [InlineData("131.0.2903.70\n", true)]
    [InlineData("130.0.2849.80", false)]
    [InlineData("129.0.2792.89", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_profile_marker_is_compared_with_the_runtime_folder(string? lastVersion, bool newer)
    {
        Assert.Equal(newer, RuntimeStartPolicy.ProfileNewer(lastVersion, Folder));
        Assert.Equal(newer, RuntimeStartPolicy.ProfileNewer(lastVersion, Folder + "\\"));
    }

    [Fact]
    public void A_folder_without_a_version_never_counts_as_older()
    {
        Assert.False(RuntimeStartPolicy.ProfileNewer("999.0.0.0", @"C:\Program Files\Something"));
        Assert.False(RuntimeStartPolicy.ProfileNewer("999.0.0.0", null));
    }

    private const string Profile = @"C:\Users\kid\AppData\Local\CouchHud\WebView2";

    private static string Aside(string stamp) => $"{Profile}.newer-runtime-{stamp}";

    [Fact]
    public void The_search_pattern_names_the_profile_folder_only()
    {
        Assert.Equal("WebView2.newer-runtime-*", RuntimeStartPolicy.SetAsidePattern(Profile + "\\"));
    }

    [Fact]
    public void Below_the_limit_nothing_is_removed()
    {
        Assert.Empty(RuntimeStartPolicy.SetAsideToRemove(Profile, []));
        Assert.Empty(RuntimeStartPolicy.SetAsideToRemove(Profile, [Aside("20261001-120000")], kept: 2));
    }

    [Fact]
    public void The_oldest_go_so_that_at_most_the_limit_remains_with_the_new_one()
    {
        var siblings = new[] { Aside("20261027-093005"), Aside("20260101-000000"), Aside("20261001-120000") + "\\" };
        Assert.Equal([Aside("20260101-000000"), Aside("20261001-120000")], RuntimeStartPolicy.SetAsideToRemove(Profile, siblings, kept: 2));
        Assert.Equal([Aside("20260101-000000")], RuntimeStartPolicy.SetAsideToRemove(Profile, siblings, kept: 3));
    }

    [Theory]
    [InlineData(@"C:\Users\kid\AppData\Local\CouchHud\WebView2.newer-runtime-20260101-000000-copy")]
    [InlineData(@"C:\Users\kid\AppData\Local\CouchHud\WebView2.newer-runtime-2026010100000x")]
    [InlineData(@"C:\Users\kid\AppData\Local\CouchHud\WebView2.newer-runtime-")]
    [InlineData(@"C:\Users\kid\AppData\Local\CouchHud\Browser.newer-runtime-20260101-000000")]
    [InlineData(@"C:\Users\kid\AppData\Local\CouchHud\WebView2")]
    [InlineData(@"D:\elsewhere\WebView2.newer-runtime-20260101-000000")]
    public void Only_exact_names_next_to_the_profile_are_ever_removed(string foreign)
    {
        Assert.Empty(RuntimeStartPolicy.SetAsideToRemove(Profile, [foreign, foreign + "x", foreign], kept: 1));
    }

    [Fact]
    public void A_profile_is_renamed_next_to_itself()
    {
        Assert.Equal(@"C:\Users\kid\AppData\Local\CouchHud\WebView2.newer-runtime-20261027-093005",
            RuntimeStartPolicy.SetAsideName(@"C:\Users\kid\AppData\Local\CouchHud\WebView2\", new DateTime(2026, 10, 27, 9, 30, 5)));
    }
}
