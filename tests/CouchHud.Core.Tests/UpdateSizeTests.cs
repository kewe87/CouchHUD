using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

/// <summary>Sizes of Windows updates: never the full package as if it were the download.</summary>
public class UpdateSizeTests
{
    [Fact]
    public void Min_and_max_close_together_show_the_larger()
    {
        Assert.Equal(60_000_000, UpdateSize.Shown(50_000_000L, 60_000_000L, false));
        Assert.Equal(75_000_000, UpdateSize.Shown(50_000_000L, 75_000_000L, false));   // exactly 1.5
        Assert.Equal(10, UpdateSize.Shown(10L, 10L, false));
    }

    [Fact]
    public void Min_far_below_max_shows_nothing()
    {
        // A Defender definition or a cumulative update: a few MB of express download, gigabytes of full package.
        Assert.Null(UpdateSize.Shown(8_000_000L, 2_300_000_000L, false));
        Assert.Null(UpdateSize.Shown(50_000_000L, 75_000_001L, false));
    }

    [Fact]
    public void Min_unknown_or_zero_shows_nothing()
    {
        Assert.Null(UpdateSize.Shown(null, 2_300_000_000L, false));
        Assert.Null(UpdateSize.Shown(0L, 2_300_000_000L, false));
        Assert.Null(UpdateSize.Shown(-1L, 100L, false));
        Assert.Null(UpdateSize.Shown(100L, null, false));
        Assert.Null(UpdateSize.Shown((object?)null, null, false));
    }

    [Fact]
    public void Already_downloaded_shows_nothing()
    {
        Assert.Null(UpdateSize.Shown(50_000_000L, 50_000_000L, true));
        Assert.Null(UpdateSize.Shown((object)50_000_000m, 50_000_000m, true));
    }

    [Fact]
    public void Swapped_values_are_read_the_right_way_round()
    {
        Assert.Equal(60, UpdateSize.Shown(60L, 50L, false));
    }

    [Fact]
    public void Com_values_of_any_numeric_type_are_read_and_foreign_ones_refused()
    {
        // The agent reports DECIMAL.
        Assert.Equal(1234, UpdateSize.Bytes(1234m));
        Assert.Equal(1235, UpdateSize.Bytes(1234.2m));
        Assert.Equal(1234, UpdateSize.Bytes(1234.0));
        Assert.Equal(1234, UpdateSize.Bytes(1234));
        Assert.Equal(1234, UpdateSize.Bytes(1234UL));
        Assert.Null(UpdateSize.Bytes(null));
        Assert.Null(UpdateSize.Bytes("1234"));
        Assert.Null(UpdateSize.Bytes(true));
        Assert.Null(UpdateSize.Bytes(new object()));
        Assert.Null(UpdateSize.Bytes(double.NaN));
        Assert.Null(UpdateSize.Bytes(double.PositiveInfinity));
        Assert.Null(UpdateSize.Bytes(double.MaxValue));
        Assert.Null(UpdateSize.Bytes(decimal.MaxValue));
        Assert.Null(UpdateSize.Bytes(0m));
        Assert.Null(UpdateSize.Bytes(-5m));
        Assert.Null(UpdateSize.Bytes(UpdateSize.MaxPlausibleBytes + 1m));
        Assert.Equal(UpdateSize.MaxPlausibleBytes, UpdateSize.Bytes((decimal)UpdateSize.MaxPlausibleBytes));
        // Overflow-safe comparison at the upper end.
        Assert.Equal(UpdateSize.MaxPlausibleBytes, UpdateSize.Shown(UpdateSize.MaxPlausibleBytes, UpdateSize.MaxPlausibleBytes, false));
        Assert.Null(UpdateSize.Shown(long.MaxValue, long.MaxValue, false));
    }

    [Fact]
    public void Download_counters_are_kept_within_their_total()
    {
        Assert.Equal((5L, 20L), UpdateSize.Transfer(5m, 20m));
        Assert.Equal((20L, 20L), UpdateSize.Transfer(50m, 20m));
        Assert.Equal((0L, 20L), UpdateSize.Transfer(null, 20m));
        Assert.Equal((0L, 20L), UpdateSize.Transfer(double.NaN, 20m));
        Assert.Equal((0L, 0L), UpdateSize.Transfer(5m, 0m));
        Assert.Equal((0L, 0L), UpdateSize.Transfer(5m, null));
        Assert.Equal((0L, 0L), UpdateSize.Transfer(5m, -1m));
        Assert.Equal((0L, 0L), UpdateSize.Transfer(5L, long.MaxValue));
    }
}
