using CouchHud.Core.Guard;
using CouchHud.Core.Notifications;

namespace CouchHud.Core.Tests;

/// <summary>
/// Everything the setup assistant writes, checked against the couch lock: it must not become a way around it. The look and sound
/// choices are harmless on purpose (a guest may change them anyway); everything else needs the code.
/// </summary>
public class SetupWizardLockTests
{
    [Theory]
    [InlineData("setupWizardDone")]
    [InlineData("language")]
    [InlineData("integrationMode")]
    [InlineData("runStartupApps")]
    [InlineData("guideTapInGame")]
    [InlineData("autoLogonDeclined")]
    [InlineData("suppressCompetitors")]
    public void Settings_the_assistant_writes_need_the_code(string key) =>
        Assert.DoesNotContain(key, LockPolicy.HarmlessSettings);

    [Theory]
    [InlineData("audioMode")]
    [InlineData("pointer")]
    [InlineData("acceptedTerms")]
    public void Look_sound_and_the_notice_stay_open_to_a_guest_as_before(string key) =>
        Assert.Contains(key, LockPolicy.HarmlessSettings);

    [Theory]
    [InlineData("setLauncherMode")]
    [InlineData("setSetting")]
    [InlineData("setIntegrationMode")]
    [InlineData("setShell")]
    [InlineData("setAutoLogon")]
    [InlineData("enableAutoLogon")]
    [InlineData("applyStartupSuggestions")]
    [InlineData("setupServiceTask")]
    public void Bridge_calls_the_assistant_makes_are_refused_while_locked(string method) =>
        Assert.DoesNotContain(method, LockPolicy.OpenWhileLocked);

    [Theory]
    [InlineData("getLaunchers")]
    [InlineData("getSettings")]
    [InlineData("getSecurity")]
    [InlineData("setSkin")]
    public void Reads_and_the_skin_stay_open_while_locked(string method) =>
        Assert.Contains(method, LockPolicy.OpenWhileLocked);

    [Fact]
    public void A_notification_is_no_security_notice_unless_the_host_says_so()
    {
        // Windows toasts and plugins use the plain constructor: they cannot get through a quiet page.
        var plain = new Notification("1", "windows", "WhatsApp", "Tim", "Hi", 0);
        Assert.False(plain.Security);
        Assert.True((plain with { Security = true }).Security);
    }
}
