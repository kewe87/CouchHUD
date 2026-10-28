using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

/// <summary>The browser's recent sites: hidden at the locked couch and with parental controls, until the parents' code shows them.</summary>
public sealed class BrowserRecentPrivacyTests
{
    [Theory]
    // locked, parental on, shown by parent => hidden, needs parent
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    // The locked couch hides them even after the parents' code (the couch code opens the couch first).
    [InlineData(true, true, true, true, false)]
    // A release left over without parental controls changes nothing.
    [InlineData(false, false, true, false, false)]
    public void Hidden_follows_lock_and_parental_controls(bool locked, bool parentalOn, bool shown, bool hidden, bool needsParent)
    {
        Assert.Equal(hidden, BrowserRecentPrivacy.Hidden(locked, parentalOn, shown));
        Assert.Equal(needsParent, BrowserRecentPrivacy.NeedsParent(locked, parentalOn, shown));
    }

    [Fact]
    public void The_parents_code_for_the_list_fits_nothing_else()
    {
        var now = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        var grant = new ParentGrant(() => now, TimeSpan.FromSeconds(90));
        grant.Give(BrowserRecentPrivacy.ParentAction);
        Assert.False(grant.Fits("launch", "game"));
        Assert.False(grant.Fits("profile"));
        Assert.True(grant.TryConsume(BrowserRecentPrivacy.ParentAction));
        Assert.False(grant.TryConsume(BrowserRecentPrivacy.ParentAction));
    }

    [Theory]
    // parental on, history on now, asked for => needs the parents' code
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void Only_switching_the_history_off_with_parental_controls_needs_the_parents(bool parentalOn, bool historyOn, bool next, bool needs) =>
        Assert.Equal(needs, BrowserRecentPrivacy.HistoryChangeNeedsParent(parentalOn, historyOn, next));

    [Fact]
    public void The_parents_code_for_history_off_fits_nothing_else()
    {
        var now = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        var grant = new ParentGrant(() => now, TimeSpan.FromSeconds(90));
        grant.Give(BrowserRecentPrivacy.HistoryOffAction);
        Assert.False(grant.Fits(BrowserRecentPrivacy.ParentAction));
        Assert.True(grant.TryConsume(BrowserRecentPrivacy.HistoryOffAction));
    }
}
