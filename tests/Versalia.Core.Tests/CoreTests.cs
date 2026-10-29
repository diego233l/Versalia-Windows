using Versalia.Core;
using Xunit;

namespace Versalia.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParsesPlainLrcAndMarksEstimated()
    {
        var lyrics = LrcParser.Parse("[00:01.00]Hello world\n[00:03.00]Second line");
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal(1000, lyrics.Lines[0].StartMs);
        Assert.Equal(2, lyrics.Lines[0].Words.Count);
        Assert.True(lyrics.IsEstimated);
    }

    [Fact]
    public void ParsesEnhancedLrcWithRealWordTimes()
    {
        var lyrics = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:10.50>world <00:11.20>");
        var line = Assert.Single(lyrics.Lines);
        Assert.Equal(new LyricWord("Hello", 10000, 10500), line.Words[0]);
        Assert.Equal(new LyricWord("world", 10500, 11200), line.Words[1]);
        Assert.False(line.IsEstimated);
    }

    [Fact]
    public void MultipleTimestampsRepeatTheLine()
    {
        var lyrics = LrcParser.Parse("[00:10.00][00:50.00]Chorus");
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal(50000, lyrics.Lines[1].StartMs);
    }

    [Fact]
    public void AppliesOffsetTag()
    {
        var lyrics = LrcParser.Parse("[offset:500]\n[00:02.00]Hi");
        Assert.Equal(1500, lyrics.Lines[0].StartMs);
    }
}

public class YrcParserTests
{
    [Fact]
    public void JoinsSyllablesIntoWords()
    {
        var lyrics = YrcParser.Parse("[1000,2000](1000,500,0)Hel(1500,500,0)lo (2000,1000,0)world");
        var line = Assert.Single(lyrics.Lines);
        Assert.Equal("Hello", line.Words[0].Text);
        Assert.Equal(1000, line.Words[0].StartMs);
        Assert.Equal(2000, line.Words[0].EndMs);
        Assert.Equal("world", line.Words[1].Text);
        Assert.False(line.IsEstimated);
    }
}

public class ClockTests
{
    [Fact]
    public void ExtrapolatesWhilePlaying()
    {
        var clock = new InterpolatedClock();
        clock.Reset(5000, 10, true);
        Assert.Equal(6500, clock.PositionMs(11.5), 3);
    }

    [Fact]
    public void LargeDifferenceIsSeek()
    {
        var clock = new InterpolatedClock();
        clock.Reset(0, 0, true);
        Assert.Equal(InterpolatedClock.Outcome.Seek, clock.Observe(30000, 1, true));
        Assert.Equal(30000, clock.PositionMs(1), 3);
    }

    [Fact]
    public void TinyDifferenceIsIgnored()
    {
        var clock = new InterpolatedClock();
        clock.Reset(0, 0, true);
        Assert.Equal(InterpolatedClock.Outcome.WithinNoise, clock.Observe(1010, 1, true));
    }
}

public class SyncTests
{
    private static Lyrics Sample() => new(new[]
    {
        new LyricLine(1000, 3000, "a b", new[] { new LyricWord("a", 1000, 2000), new LyricWord("b", 2000, 3000) }, false),
        new LyricLine(5000, 6000, "c", new[] { new LyricWord("c", 5000, 6000) }, false),
    }, "test");

    [Fact]
    public void FindsLineWordAndProgress()
    {
        var cursor = new SyncCursor(Sample());
        Assert.Equal(-1, cursor.Position(500).LineIndex);
        var p = cursor.Position(2500);
        Assert.Equal(0, p.LineIndex);
        Assert.Equal(1, p.WordIndex);
        Assert.Equal(0.5, p.WordProgress, 3);
        Assert.True(cursor.Position(4000).IsLineFinished);
        Assert.Equal(1, cursor.Position(5500).LineIndex);
        Assert.Equal(0, cursor.Position(1100).LineIndex); // seek hacia atrás
    }

    [Fact]
    public void WordStreamGroupsAndOrders()
    {
        var stream = new WordStream(Sample());
        Assert.Equal(3, stream.Entries.Count);
        Assert.Equal(-1, stream.LastStartedIndex(100));
        Assert.Equal(1, stream.LastStartedIndex(2500));
        Assert.Equal(2, stream.Entries[0].GroupSize);
    }

    [Fact]
    public void ConstellationIsDeterministicAndStable()
    {
        var a = ConstellationLayout.Points(7, 6);
        var b = ConstellationLayout.Points(7, 6);
        Assert.Equal(a, b);
        Assert.Equal(a.Take(3).ToList(), ConstellationLayout.Points(7, 3));
    }
}

public class MatcherTests
{
    [Theory]
    [InlineData("Song Title - Remastered 2011", "song title")]
    [InlineData("Song Title (feat. Someone)", "song title")]
    [InlineData("Canción Ñandú", "cancion nandu")]
    public void NormalizesTitles(string input, string expected) =>
        Assert.Equal(expected, TrackMatcher.NormalizeTitle(input));

    [Theory]
    [InlineData("We Don’t Talk Anymore (feat. Selena Gomez)", "We Don’t Talk Anymore")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Canción Ñandú", "Canción Ñandú")]
    public void SearchTitleKeepsAccentsButDropsExtras(string input, string expected) =>
        Assert.Equal(expected, TrackMatcher.SearchTitle(input));

    [Fact]
    public void DurationToleranceIsThreeSeconds()
    {
        var track = new TrackInfo("1", "Song", "Artist", "", 200_000);
        Assert.True(TrackMatcher.Matches(track, "Song", "Artist", 202_500));
        Assert.False(TrackMatcher.Matches(track, "Song", "Artist", 210_000));
    }
}

public class EstimatorTests
{
    [Fact]
    public void WordsCoverTheWholeLineWithoutGaps()
    {
        var words = WordTimingEstimator.Words("hello beautiful world", 1000, 4000);
        Assert.Equal(3, words.Count);
        Assert.Equal(1000, words[0].StartMs);
        Assert.Equal(4000, words[^1].EndMs);
        Assert.Equal(words[0].EndMs, words[1].StartMs);
    }
}

public class ProviderTests
{
    private sealed class FakeHttp : IHttp
    {
        public Func<Uri, (string, int)> Handler = _ => ("", 404);
        public Task<(string Body, int Status)> GetAsync(Uri url, IDictionary<string, string> headers, CancellationToken ct)
            => Task.FromResult(Handler(url));
    }

    [Fact]
    public async Task LrclibReturnsParsedLyrics()
    {
        var http = new FakeHttp
        {
            Handler = _ => ("{\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":200,\"syncedLyrics\":\"[00:01.00]Hi there\\n[00:03.00]Bye\"}", 200)
        };
        var provider = new LrclibProvider(http, retryDelays: Array.Empty<double>());
        var lyrics = await provider.FetchAsync(new TrackInfo("1", "Song", "Artist", "Album", 200_000));
        Assert.NotNull(lyrics);
        Assert.Equal(2, lyrics!.Lines.Count);
    }

    [Fact]
    public async Task CompositeThrowsWhenNothingFoundButSomethingFailed()
    {
        var failing = new LrclibProvider(new FakeHttp { Handler = _ => ("", 500) }, retryDelays: Array.Empty<double>());
        var composite = new CompositeLyricsProvider(new ILyricsProvider[] { failing });
        await Assert.ThrowsAsync<LyricsProviderException>(() =>
            composite.FetchAsync(new TrackInfo("1", "Song", "Artist", "", 1000)));
    }
}

public class PaletteTests
{
    [Fact]
    public void PicksTheColorfulHue()
    {
        var pixels = new byte[64 * 4];
        for (int i = 0; i < 64; i++) { pixels[i * 4] = 20; pixels[i * 4 + 1] = 20; pixels[i * 4 + 2] = 220; pixels[i * 4 + 3] = 255; } // BGRA rojo
        var palette = PaletteExtractor.FromBgra(pixels, 64);
        Assert.NotNull(palette);
        Assert.True(palette!.Highlight.R > 0.85);
        Assert.True(palette.Highlight.G < 0.5);
    }

    [Fact]
    public void GreyArtworkGivesNoPalette()
    {
        var pixels = new byte[16 * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 128;
        Assert.Null(PaletteExtractor.FromBgra(pixels, 16));
    }
}

public class KrcTests
{
    private const string Sample = "[ar:Artist]\n[1000,2000]<0,500,0>Hel<500,500,0>lo <1000,1000,0>world\n";

    [Fact]
    public void ParsesRelativeWordTimes()
    {
        var line = Assert.Single(KrcParser.Parse(Sample).Lines);
        Assert.Equal(new LyricWord("Hello", 1000, 2000), line.Words[0]);
        Assert.Equal(new LyricWord("world", 2000, 3000), line.Words[1]);
        Assert.False(line.IsEstimated);
    }

    [Fact]
    public void DecodesEncryptedPayload()
    {
        Assert.Equal(Sample, KrcParser.Decode(KrcParser.Encode(Sample)));
    }

    [Fact]
    public void YrcStillParsesAfterRefactor()
    {
        var line = Assert.Single(YrcParser.Parse("[0,900](0,300,0)a (300,600,0)b").Lines);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(300, line.Words[1].StartMs);
    }
}

public class TtmlTests
{
    private const string Syllable =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" timing=\"Word\"><body><div>" +
        "<p begin=\"1.000\" end=\"3.500\"><span begin=\"1.000\" end=\"1.300\">Hel</span><span begin=\"1.300\" end=\"1.600\">lo</span> " +
        "<span begin=\"1.700\" end=\"2.400\">world</span><span ttm:role=\"x-bg\"><span begin=\"2.5\" end=\"3\">(ooh)</span></span></p>" +
        "<p begin=\"1:04.250\" end=\"1:06.000\"><span begin=\"1:04.250\" end=\"1:05.000\">Second</span></p>" +
        "</div></body></tt>";

    [Fact]
    public void JoinsSyllablesAndSkipsBackgroundVocals()
    {
        var lyrics = TtmlParser.Parse(Syllable, "applemusic");
        Assert.Equal(2, lyrics.Lines.Count);
        var first = lyrics.Lines[0];
        Assert.False(first.IsEstimated);
        Assert.Equal("Hello world", first.Text);
        Assert.Equal(1000, first.Words[0].StartMs);
        Assert.Equal(1600, first.Words[0].EndMs);
        Assert.Equal(1700, first.Words[1].StartMs);
        Assert.Equal(64250, lyrics.Lines[1].StartMs);
    }

    [Fact]
    public void LineLevelTtmlIsEstimated()
    {
        var lyrics = TtmlParser.Parse("<tt><body><div><p begin=\"00:00:02.000\" end=\"00:00:04.000\">Just a line</p></div></body></tt>");
        Assert.Single(lyrics.Lines);
        Assert.True(lyrics.IsEstimated);
        Assert.Equal(3, lyrics.Lines[0].Words.Count);
        Assert.Equal(2000, lyrics.Lines[0].StartMs);
    }

    [Theory]
    [InlineData("12.5s", 12500)]
    [InlineData("1500ms", 1500)]
    [InlineData("01:02.250", 62250)]
    [InlineData("1:00:00.000", 3600000)]
    public void ParsesTimes(string text, int expected) => Assert.Equal(expected, TtmlParser.ParseTime(text));
}

public class CalibrationTests
{
    private static Lyrics Make() => LrcParser.Parse("[00:01.00]one\n[00:05.00]two\n[00:10.00]three\n[00:20.00]four");

    [Fact]
    public void PicksALineFarEnoughAhead()
    {
        Assert.Equal(2, SyncCalibration.TargetLine(Make(), 4500));
        Assert.Equal(-1, SyncCalibration.TargetLine(Make(), 19000));
    }

    [Fact]
    public void LateLyricsGetPositiveCorrection()
    {
        // La línea empieza en 10 s; al oírla (y pulsar) la letra solo iba por 7 s: hay que adelantarla ~3 s.
        Assert.Equal(3100, SyncCalibration.TapCorrection(10000, 7000));
        Assert.Equal(-1900, SyncCalibration.TapCorrection(10000, 12000));
    }

    [Fact]
    public void LearnedIsTheMedian()
    {
        Assert.Null(SyncCalibration.Learned(new[] { 500 }));
        Assert.Equal(600, SyncCalibration.Learned(new[] { 500, 700 }));
        Assert.Equal(700, SyncCalibration.Learned(new[] { 9000, 700, 650 }));
    }
}

public class AppleMusicTests
{
    private sealed class FakeHttp : IHttp
    {
        public Func<Uri, IDictionary<string, string>, (string, int)> Handler = (_, _) => ("", 404);
        public Task<(string Body, int Status)> GetAsync(Uri url, IDictionary<string, string> headers, CancellationToken ct)
            => Task.FromResult(Handler(url, headers));
    }

    private const string Jwt = "eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJ4In0.c2lnbmF0dXJl";

    [Fact]
    public async Task FindsSyllableLyricsWithBothTokens()
    {
        var seen = new List<string>();
        var http = new FakeHttp
        {
            Handler = (url, headers) =>
            {
                var u = url.ToString();
                seen.Add(u);
                if (u.EndsWith("/us/browse")) return ("<script type=\"module\" src=\"/assets/index-abc123.js\"></script>", 200);
                if (u.EndsWith("/assets/index-abc123.js")) return ($"var t=\"{Jwt}\";", 200);
                Assert.Equal("Bearer " + Jwt, headers["Authorization"]);
                Assert.Equal("user-token", headers["media-user-token"]);
                if (u.Contains("/v1/me/storefront")) return ("{\"data\":[{\"id\":\"es\"}]}", 200);
                if (u.Contains("/v1/catalog/es/search"))
                    return ("{\"results\":{\"songs\":{\"data\":[{\"id\":\"42\",\"attributes\":{\"name\":\"Song\",\"artistName\":\"Artist\",\"durationInMillis\":200000}}]}}}", 200);
                if (u.EndsWith("/v1/catalog/es/songs/42/syllable-lyrics"))
                    return ("{\"data\":[{\"attributes\":{\"ttml\":\"<tt><body><div><p begin=\\\"1\\\" end=\\\"2\\\"><span begin=\\\"1\\\" end=\\\"2\\\">Hi</span></p></div></body></tt>\"}}]}", 200);
                return ("", 404);
            }
        };
        var provider = new AppleMusicProvider(() => "user-token", http, web: "https://web.test", api: "https://api.test");
        var lyrics = await provider.FetchAsync(new TrackInfo("1", "Song", "Artist", "", 200_000));
        Assert.NotNull(lyrics);
        Assert.Equal("applemusic", lyrics!.Source);
        Assert.False(lyrics.IsEstimated);
        Assert.Equal("conectado", provider.Status);
    }

    [Fact]
    public async Task RejectedTokenReturnsNullWithoutThrowing()
    {
        var http = new FakeHttp
        {
            Handler = (url, _) => url.ToString().EndsWith("/us/browse") ? ($"x \"{Jwt}\"", 200) : ("", 403)
        };
        var provider = new AppleMusicProvider(() => "bad", http, web: "https://web.test", api: "https://api.test");
        Assert.Null(await provider.FetchAsync(new TrackInfo("1", "Song", "Artist", "", 200_000)));
        Assert.StartsWith("token rechazado", provider.Status);
    }

    [Fact]
    public async Task NoTokenMeansNoRequests()
    {
        int calls = 0;
        var http = new FakeHttp { Handler = (_, _) => { calls++; return ("", 500); } };
        var provider = new AppleMusicProvider(() => null, http);
        Assert.Null(await provider.FetchAsync(new TrackInfo("1", "Song", "Artist", "", 200_000)));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("media-user-token=AbC123;", "AbC123")]
    [InlineData("  \"AbC123\" ", "AbC123")]
    [InlineData("AbC123", "AbC123")]
    public void CleansPastedToken(string pasted, string expected) => Assert.Equal(expected, AppleMusicProvider.CleanToken(pasted));
}
