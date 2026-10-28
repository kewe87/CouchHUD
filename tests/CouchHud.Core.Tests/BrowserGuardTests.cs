using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class BrowserGuardTests
{
    [Fact]
    public void DownloadsNeverGetTheBrowsersOwnUi()
    {
        Assert.Equal((true, true), BrowserGuard.Download(allowed: false));
        Assert.Equal((true, false), BrowserGuard.Download(allowed: true));
    }

    [Theory]
    [InlineData("print")]
    [InlineData("showOpenFilePicker")]
    [InlineData("showSaveFilePicker")]
    [InlineData("showDirectoryPicker")]
    [InlineData("type === 'file'")]
    [InlineData("showPicker")]
    [InlineData("type === 'color'")]
    [InlineData("'EyeDropper'")]
    [InlineData("'PaymentRequest'")]
    [InlineData("nav('share'")]
    [InlineData("nav('credentials'")]
    [InlineData("PublicKeyCredential")]
    [InlineData("'requestPictureInPicture', { value: refuse")]
    [InlineData("lock('documentPictureInPicture', undefined)")]
    [InlineData("'disablePictureInPicture', { get: () => true")]
    [InlineData("'pictureInPictureEnabled', { get: () => false")]
    public void ScriptCoversEveryWayToAWindowsDialog(string needle) =>
        Assert.Contains(needle, BrowserGuard.Script);

    [Fact]
    public void PictureInPictureIsOffOnTheCommandLine()
    {
        var args = BrowserGuard.BrowserArguments;
        Assert.Contains("--disable-blink-features=PictureInPictureAPI,DocumentPictureInPictureAPI", args);
        Assert.Contains("--disable-features=DocumentPictureInPictureAPI,MediaSessionEnterPictureInPicture", args);
        // Chromium takes only the last of a repeated switch: each one exactly once.
        Assert.Equal(1, args.Split("--disable-features=").Length - 1);
        Assert.Equal(1, args.Split("--disable-blink-features=").Length - 1);
    }

    [Fact]
    public void EveryNeutralizedApiIsInTheScript()
    {
        foreach (var api in BrowserGuard.NeutralizedApis.Where(a => !a.Contains('['))) Assert.Contains(api, BrowserGuard.Script);
    }
}
