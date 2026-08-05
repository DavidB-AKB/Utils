namespace Utils.Tests;

public class PaginationCursorTests
{
    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var encoded = PaginationCursor.Encode("2026-08-05T00:00:00.0000000Z", "some-id");

        var decoded = PaginationCursor.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal("2026-08-05T00:00:00.0000000Z", decoded!.Value.SortKey);
        Assert.Equal("some-id", decoded.Value.Id);
    }

    [Fact]
    public void Encode_IsOpaque_NotPlainBase64OfAReadableSeparator()
    {
        // Not a strict requirement, just documents intent - the cursor shouldn't be trivially
        // human-editable by splitting on a common character like a comma or pipe.
        var encoded = PaginationCursor.Encode("sort-value", "id-value");

        Assert.DoesNotContain("sort-value,id-value", encoded);
    }

    [Fact]
    public void Decode_Null_ReturnsNull()
    {
        Assert.Null(PaginationCursor.Decode(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_EmptyOrWhitespace_ReturnsNull(string cursor)
    {
        Assert.Null(PaginationCursor.Decode(cursor));
    }

    [Fact]
    public void Decode_NotValidBase64_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(PaginationCursor.Decode("not-valid-base64!!!"));
    }

    [Fact]
    public void Decode_ValidBase64ButMissingSeparator_ReturnsNull()
    {
        var malformed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-separator-here"));

        Assert.Null(PaginationCursor.Decode(malformed));
    }

    [Fact]
    public void Encode_SortKeyContainingTheSeparatorCharacter_StillRoundTrips()
    {
        // Decode anchors off the LAST separator specifically so a sortKey that happens to contain
        // one (caller/user-controlled data - a role name, a permission key) doesn't corrupt the id.
        var sortKeyWithSeparator = "weird" + '\u001F' + "value";

        var encoded = PaginationCursor.Encode(sortKeyWithSeparator, "real-id");
        var decoded = PaginationCursor.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(sortKeyWithSeparator, decoded!.Value.SortKey);
        Assert.Equal("real-id", decoded.Value.Id);
    }
}

public class PaginationDefaultsTests
{
    [Fact]
    public void ClampLimit_Null_ReturnsDefault()
    {
        Assert.Equal(PaginationDefaults.DefaultLimit, PaginationDefaults.ClampLimit(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ClampLimit_NonPositive_ReturnsDefault(int requested)
    {
        Assert.Equal(PaginationDefaults.DefaultLimit, PaginationDefaults.ClampLimit(requested));
    }

    [Fact]
    public void ClampLimit_WithinRange_ReturnsRequestedValue()
    {
        Assert.Equal(5, PaginationDefaults.ClampLimit(5));
    }

    [Fact]
    public void ClampLimit_AboveMax_ReturnsMax()
    {
        Assert.Equal(PaginationDefaults.MaxLimit, PaginationDefaults.ClampLimit(PaginationDefaults.MaxLimit + 1000));
    }

    [Fact]
    public void ClampLimit_ExactlyMax_ReturnsMax()
    {
        Assert.Equal(PaginationDefaults.MaxLimit, PaginationDefaults.ClampLimit(PaginationDefaults.MaxLimit));
    }
}
