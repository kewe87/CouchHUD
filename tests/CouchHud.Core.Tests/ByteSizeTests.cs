using CouchHud.Core.Config;
using CouchHud.Core.Localization;
using CouchHud.Core.Saves;

namespace CouchHud.Core.Tests;

public class ByteSizeTests
{
    [Fact]
    public void Sizes_are_decimal_like_windows_update_and_steam()
    {
        Assert.Equal("0 B", ByteSize.Format(0, "en"));
        Assert.Equal("0 B", ByteSize.Format(-5, "en"));
        Assert.Equal("512 B", ByteSize.Format(512, "en"));
        Assert.Equal("1.5 KB", ByteSize.Format(1500, "en"));
        Assert.Equal("5 MB", ByteSize.Format(5_000_000, "en"));
        // 5 MiB is 5.2 MB, not "5 MB".
        Assert.Equal("5.2 MB", ByteSize.Format(5 * 1024 * 1024, "en"));
        Assert.Equal("52 GB", ByteSize.Format(52_428_800_000, "en"));
        Assert.Equal("1,5 GB", ByteSize.Format(1_500_000_000, "de"));
        // Never "1000 KB": that is 1 MB.
        Assert.Equal("1 MB", ByteSize.Format(999_990, "en"));
        Assert.Equal("3 TB", ByteSize.Format(3_000_000_000_000, "en"));
    }

    [Fact]
    public void A_size_over_a_limit_never_reads_as_the_limit()
    {
        Assert.Equal("1000 MB", ByteSize.CeilingMb(1_000_000_000));
        Assert.Equal("1001 MB", ByteSize.CeilingMb(1_000_000_001));
    }

    [Fact]
    public void Save_limits_are_decimal_megabytes()
    {
        var c = new AppConfig();
        Assert.Equal(1000, c.AutoBackupMaxGameMb);
        Assert.Equal(10000, c.AutoBackupMaxTotalMb);
        var limits = AutoBackupLimits.From(c);
        Assert.Equal(1_000_000_000L, limits.MaxGameBytes);
        Assert.Equal(10_000_000_000L, limits.MaxTotalBytes);
        Assert.Equal("10 GB", ByteSize.Format(limits.MaxTotalBytes, "de"));
    }

    [Fact]
    public void Binary_limits_of_an_older_file_move_up_once_to_the_next_offered_limit()
    {
        // Written before 0.5: 1024 meant 1024 MiB = 1073.7 MB; as decimal MB a game of 1.03 GB would now be refused.
        var old = ConfigStore.Parse("""{ "autoBackupMaxGameMb": 1024, "autoBackupMaxTotalMb": 10240 }""")!;
        Assert.Equal(2000, old.AutoBackupMaxGameMb);
        Assert.Equal(20000, old.AutoBackupMaxTotalMb);
        Assert.True(old.SaveLimitsDecimal);
        Assert.True(AutoBackupLimits.From(old).MaxGameBytes >= 1024L * 1024 * 1024);
        // Every old preset lands on an offered one, so the picker shows it chosen.
        foreach (var mib in new[] { 100, 256, 512, 1024, 2048, 4096 })
            Assert.Contains(SaveGameService.FromBinaryMb(mib, SaveGameService.GamePresetsMb, SaveGameService.MinGameMb, SaveGameService.MaxGameMb), SaveGameService.GamePresetsMb);
        foreach (var mib in new[] { 1024, 5120, 10240, 20480, 51200 })
            Assert.Contains(SaveGameService.FromBinaryMb(mib, SaveGameService.TotalPresetsMb, SaveGameService.MinTotalMb, SaveGameService.MaxTotalMb), SaveGameService.TotalPresetsMb);
        // Never smaller than before; above every preset the whole MB above it, within the bounds.
        Assert.Equal(5369, SaveGameService.FromBinaryMb(5120, [], 10, 20_000));
        Assert.Equal(20_971, SaveGameService.FromBinaryMb(19_999, SaveGameService.GamePresetsMb, SaveGameService.MinGameMb, SaveGameService.MaxGameMb));
        // The old upper bounds (20 GiB per game, 1 TiB in total) fit the new ones: no old value shrinks, not even the largest.
        foreach (var mib in new[] { 10, 4096, 20 * 1024 })
            Assert.True((long)SaveGameService.FromBinaryMb(mib, SaveGameService.GamePresetsMb, SaveGameService.MinGameMb, SaveGameService.MaxGameMb) * ByteSize.Mega >= mib * 1024L * 1024);
        foreach (var mib in new[] { 100, 51200, 1024 * 1024 })
            Assert.True((long)SaveGameService.FromBinaryMb(mib, SaveGameService.TotalPresetsMb, SaveGameService.MinTotalMb, SaveGameService.MaxTotalMb) * ByteSize.Mega >= mib * 1024L * 1024);

        // Once: the file saved with the marker is read as it is (no second step up), and so is a file from this version.
        var saved = System.Text.Json.JsonSerializer.Serialize(old, ConfigStore.JsonOptions);
        var again = ConfigStore.Parse(saved)!;
        Assert.Equal(2000, again.AutoBackupMaxGameMb);
        Assert.Equal(20000, again.AutoBackupMaxTotalMb);
        var fresh = ConfigStore.Parse(System.Text.Json.JsonSerializer.Serialize(new AppConfig(), ConfigStore.JsonOptions))!;
        Assert.Equal(1000, fresh.AutoBackupMaxGameMb);
        Assert.Equal(10000, fresh.AutoBackupMaxTotalMb);
        // A decimal limit chosen in this version stays.
        var chosen = ConfigStore.Parse("""{ "saveLimitsDecimal": true, "autoBackupMaxGameMb": 250 }""")!;
        Assert.Equal(250, chosen.AutoBackupMaxGameMb);
        // Reading the old file twice without saving gives the same result.
        Assert.Equal(2000, ConfigStore.Parse("""{ "autoBackupMaxGameMb": 1024 }""")!.AutoBackupMaxGameMb);
    }

    [Fact]
    public void The_page_offers_the_same_limits_as_the_host()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        var js = File.ReadAllText(Path.Combine(root, "ui", "core", "size.js"));
        Assert.Contains($"export const GAME_LIMITS_MB = [{string.Join(", ", SaveGameService.GamePresetsMb)}];", js);
        Assert.Contains($"export const TOTAL_LIMITS_MB = [{string.Join(", ", SaveGameService.TotalPresetsMb)}];", js);
    }
}
