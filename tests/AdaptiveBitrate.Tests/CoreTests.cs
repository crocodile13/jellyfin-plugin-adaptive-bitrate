using System;
using System.Collections.Generic;
using System.Linq;
using AdaptiveBitrate.Core;
using Xunit;

namespace AdaptiveBitrate.Tests;

public class LadderTests
{
    private static readonly SourceVideo Hd = new(1920, 1080, 12_000_000, 23.976);

    [Fact]
    public void RungsGoDownByTheStepUntilTheMinimumWithShrinkingImages()
    {
        var r = Ladder.Rungs(8_000_000, Hd, new LadderOptions());
        Assert.Equal(new[] { 4_800_000, 2_880_000, 1_728_000, 1_036_800 }, r.Select(x => x.VideoBitrate));
        Assert.True(r.Zip(r.Skip(1)).All(p => p.Second.MaxHeight < p.First.MaxHeight), string.Join(" ", r));
        Assert.All(r, x => Assert.True(x.MaxHeight % 2 == 0 && x.MaxHeight <= 1080));
        // quatre marches au plus, rien sous le minimum
        Assert.Equal(2, Ladder.Rungs(2_000_000, Hd, new LadderOptions()).Count);
        Assert.Empty(Ladder.Rungs(1_000_000, Hd, new LadderOptions()));
    }

    [Fact]
    public void ImageHeightFollowsTheUsualLaddersAndNeverExceedsTheSource()
    {
        Assert.InRange(Ladder.HeightFor(5_800_000, new SourceVideo(3840, 2160, 0, 30)), 1076, 1084);
        Assert.InRange(Ladder.HeightFor(2_700_000, new SourceVideo(3840, 2160, 0, 30)), 690, 750);
        Assert.InRange(Ladder.HeightFor(700_000, new SourceVideo(3840, 2160, 0, 30)), 340, 380);
        Assert.Equal(480, Ladder.HeightFor(20_000_000, new SourceVideo(640, 480, 0, 25)));
        // 60 images/s : le même débit donne une image plus petite
        Assert.True(Ladder.HeightFor(3_000_000, new SourceVideo(1920, 1080, 0, 60)) < Ladder.HeightFor(3_000_000, new SourceVideo(1920, 1080, 0, 30)));
    }

    [Fact]
    public void WithoutARequestedBitrateTheFileBitrateIsTheTopAndAboveRungsStayUnderTheFile()
    {
        Assert.Equal(7_200_000, Ladder.Rungs(null, Hd, new LadderOptions())[0].VideoBitrate);   // copie : le fichier
        Assert.Empty(Ladder.Rungs(null, new SourceVideo(0, 0, 0, 0), new LadderOptions()));
        var o = new LadderOptions { RungsAbove = 2 };
        var r = Ladder.Rungs(5_000_000, Hd, o);
        Assert.Equal(new[] { 8_333_333 }, r.TakeWhile(x => x.VideoBitrate > 5_000_000).Select(x => x.VideoBitrate));   // 13,9 Mb/s dépasserait le fichier
        Assert.Empty(Ladder.Rungs(5_000_000, Hd, new LadderOptions { RungsAbove = 2, AboveCap = 6_000_000, MaxRungs = 0 }));
    }

    [Fact]
    public void OutOfRangeSettingsAreBounded()
    {
        var s = new LadderOptions { MaxRungs = 99, Step = double.NaN, MinBitrate = -5, RungsAbove = 9, AboveCap = -1 }.Sanitized();
        Assert.Equal((8, 0.6, 100_000, 3, 0), (s.MaxRungs, s.Step, s.MinBitrate, s.RungsAbove, s.AboveCap));
    }
}

public class MasterPlaylistTests
{
    private const string Jellyfin = "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Français\",DEFAULT=YES,AUTOSELECT=YES,FORCED=NO,LANGUAGE=\"fre\",URI=\"x/subtitles.m3u8\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=8320000,AVERAGE-BANDWIDTH=8320000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1920x1080,FRAME-RATE=23.976,SUBTITLES=\"subs\"\n"
        + "main.m3u8?DeviceId=d&VideoBitrate=8000000&AudioStreamIndex=2\n"
        + "#EXT-X-IMAGE-STREAM-INF:BANDWIDTH=1,RESOLUTION=320x180,CODECS=\"jpeg\",URI=\"Trickplay/320/tiles.m3u8\"\n";

    private static Variant Rung(int bitrate, string res)
        => new($"#EXT-X-STREAM-INF:BANDWIDTH={bitrate + 320000},AVERAGE-BANDWIDTH={bitrate + 320000},VIDEO-RANGE=SDR,CODECS=\"avc1.64001f,mp4a.40.2\",RESOLUTION={res},FRAME-RATE=23.976,SUBTITLES=\"subs\"",
            $"main.m3u8?DeviceId=d&VideoBitrate={bitrate}&AudioStreamIndex=2&MaxHeight=720");

    [Fact]
    public void AddedQualitiesComeAfterTheOriginalOneAndBeforeTheTrickplayImages()
    {
        var merged = MasterPlaylist.Merge(Jellyfin, new[] { Rung(4_800_000, "1280x720"), Rung(2_880_000, "854x480") });
        var v = MasterPlaylist.Variants(merged);
        Assert.Equal(new long[] { 8_320_000, 5_120_000, 3_200_000 }, v.Select(x => x.Bandwidth));
        var lines = merged.Split('\n');
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.StartsWith("#EXT-X-MEDIA:TYPE=SUBTITLES", lines[1]);   // sous-titres : une fois, en tête
        Assert.StartsWith("#EXT-X-IMAGE-STREAM-INF", lines.Last(l => l.Length > 0));
        Assert.Equal(1, lines.Count(l => l.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARepeatedAddressIsAddedOnceAndAnUnknownPlaylistIsLeftAlone()
    {
        var r = Rung(4_800_000, "1280x720");
        var merged = MasterPlaylist.Merge(Jellyfin, new[] { r, r, new Variant("#EXT-X-STREAM-INF:BANDWIDTH=1", "main.m3u8?DeviceId=d&VideoBitrate=8000000&AudioStreamIndex=2") });
        Assert.Equal(2, MasterPlaylist.Variants(merged).Count);
        Assert.Equal("not a playlist", MasterPlaylist.Merge("not a playlist", new[] { r }));
        Assert.Equal(Jellyfin, MasterPlaylist.Merge(Jellyfin, Array.Empty<Variant>()));
    }

    [Fact]
    public void AttributesAreReadWithOrWithoutQuotes()
    {
        var info = "#EXT-X-STREAM-INF:BANDWIDTH=8320000,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1920x1080";
        Assert.Equal("avc1.640028,mp4a.40.2", MasterPlaylist.Attribute(info, "CODECS"));
        Assert.Equal("1920x1080", MasterPlaylist.Attribute(info, "RESOLUTION"));
        Assert.Null(MasterPlaylist.Attribute(info, "FRAME-RATE"));
        Assert.Null(MasterPlaylist.Attribute("#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=5", "BANDWIDTH"));
    }
}

public class PolicyTests
{
    private static readonly PlaybackRequest Remote = new(false, false, "Jellyfin Web", true);

    [Fact]
    public void TheLadderSkipsLocalPlayersLiveStreamsExcludedAppsAndAccountsThatCannotTranscode()
    {
        var none = Array.Empty<string>();
        Assert.Null(Policy.Skip(true, Scope.Remote, none, Remote));
        Assert.NotNull(Policy.Skip(false, Scope.All, none, Remote));
        Assert.NotNull(Policy.Skip(true, Scope.Remote, none, Remote with { IsLocal = true }));
        Assert.Null(Policy.Skip(true, Scope.All, none, Remote with { IsLocal = true }));
        Assert.NotNull(Policy.Skip(true, Scope.All, none, Remote with { IsLiveStream = true }));
        Assert.NotNull(Policy.Skip(true, Scope.All, none, Remote with { CanTranscode = false }));
        Assert.NotNull(Policy.Skip(true, Scope.All, Policy.ParseClients("roku, kodi"), Remote with { Client = "Kodi Jellyfin" }));
        Assert.Null(Policy.Skip(true, Scope.All, Policy.ParseClients(" ,\n"), Remote));
    }
}

public class SwitchLogTests
{
    [Fact]
    public void StepsAreKeptPerPlaybackWithoutRepeatsAndCountedUpAndDown()
    {
        var log = new SwitchLog();
        var t = new DateTime(2026, 11, 27, 20, 0, 0, DateTimeKind.Utc);
        log.Started("s1", "Film", "Jellyfin Web", 5);
        log.Requested("s1", 0, t);
        log.Requested("s1", 0, t.AddSeconds(5));          // même marche : ignorée
        log.Requested("s1", 2_880_000, t.AddSeconds(30));
        log.Requested("s1", 1_728_000, t.AddSeconds(40));
        log.Requested("s1", 4_800_000, t.AddSeconds(90));
        log.Requested("inconnue", 1, t);                   // lecture jamais commencée : ignorée
        var s = log.Recent().Single();
        Assert.Equal(4, s.Steps.Count);
        Assert.Equal((2, 1), (s.Down, s.Up));
    }

    [Fact]
    public void MemoryIsBounded()
    {
        var log = new SwitchLog();
        for (int i = 0; i < 100; i++)
        {
            log.Started("s" + i, "t", "c", 3);
        }

        for (int i = 0; i < 500; i++)
        {
            log.Requested("s99", i + 1, DateTime.UtcNow);
        }

        Assert.Equal(SwitchLog.MaxSessions, log.Recent().Count);
        Assert.Equal("s99", log.Recent()[0].Session);
        Assert.Equal(SwitchLog.MaxSteps, log.Recent()[0].Steps.Count);
    }
}
