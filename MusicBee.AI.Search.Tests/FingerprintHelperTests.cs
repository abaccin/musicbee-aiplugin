using FluentAssertions;
using MusicBee.AI.Search;

namespace MusicBee.AI.Search.Tests;

public class FingerprintHelperTests
{
    [Fact]
    public void Fingerprint_IsStableForSameInput()
    {
        var t = NewTrack();
        FingerprintHelper.ComputeFingerprint(t)
            .Should().Be(FingerprintHelper.ComputeFingerprint(t));
    }

    [Fact]
    public void Fingerprint_IsLength64Hex()
    {
        FingerprintHelper.ComputeFingerprint(NewTrack())
            .Should().MatchRegex("^[A-F0-9]{64}$");
    }

    [Fact]
    public void Fingerprint_TreatsNullAndEmptyAsEqual()
    {
        var n = new DbTrackRow { Path = "p" };
        var e = new DbTrackRow
        {
            Path = "p",
            Title = "",
            Artist = "",
            Album = "",
            Genre = "",
            Year = "",
            Comment = "",
            Rating = ""
        };
        FingerprintHelper.ComputeFingerprint(n)
            .Should().Be(FingerprintHelper.ComputeFingerprint(e));
    }

    [Fact]
    public void Fingerprint_ChangesWhenAnyFieldChanges()
    {
        var a = NewTrack();
        var b = NewTrack();
        b.Title = "Different";
        FingerprintHelper.ComputeFingerprint(a)
            .Should().NotBe(FingerprintHelper.ComputeFingerprint(b));
    }

    [Fact]
    public void Fingerprint_DoesNotIncludeRatingOrPath()
    {
        // Rating + Path are not part of the embeddable text and intentionally ignored
        // in the fingerprint, so a rating change should not force re-embedding.
        var a = NewTrack();
        var b = NewTrack();
        a.Rating = "5";
        b.Rating = "1";
        a.Path = "x";
        b.Path = "y";
        FingerprintHelper.ComputeFingerprint(a)
            .Should().Be(FingerprintHelper.ComputeFingerprint(b));
    }

    private static DbTrackRow NewTrack() => new()
    {
        Path = "C:\\Music\\test.mp3",
        Title = "Test Song",
        Artist = "Test Artist",
        Album = "Test Album",
        Genre = "Rock",
        Year = "2024",
        Comment = "Test Comment"
    };
}
