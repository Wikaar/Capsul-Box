using System.Text;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioNowPlayingTests
{
    private static byte[] Block(string text, Encoding enc)
    {
        var body = enc.GetBytes(text);
        var padded = new byte[(body.Length + 15) / 16 * 16];
        body.CopyTo(padded, 0);
        return padded;
    }

    [Fact]
    public void ParsesStreamTitleInUtf8AndWindows1251()
    {
        Assert.Equal("Кино - Группа крови", RadioNowPlaying.ParseStreamTitle(Block("StreamTitle='Кино - Группа крови';StreamUrl='';", Encoding.UTF8)));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("Натали - Улыбочка", RadioNowPlaying.ParseStreamTitle(Block("StreamTitle='Натали - Улыбочка';", Encoding.GetEncoding(1251))));
        Assert.Equal("Rock'n'Roll Star", RadioNowPlaying.ParseStreamTitle(Block("StreamTitle='Rock'n'Roll Star';", Encoding.UTF8)));
        Assert.Null(RadioNowPlaying.ParseStreamTitle(Block("StreamUrl='x';", Encoding.UTF8)));
    }

    [Fact]
    public void CleansJunkAndStationNames()
    {
        Assert.Equal("Coldplay – Yellow", TrackText.Clean("  Coldplay  -  Yellow ", "NDR 2"));
        Assert.Null(TrackText.Clean(" - ", "NDR 2"));
        Assert.Null(TrackText.Clean("Unknown", "NDR 2"));
        Assert.Null(TrackText.Clean("ndr 2", "NDR 2"));
        Assert.Null(TrackText.Clean("https://ndr.de", "NDR 2"));
        Assert.Equal(("Adele", "Hello"), TrackText.Split("Adele – Hello"));
    }

    [Fact]
    public void ComparesTracksIgnoringCaseAndPunctuation()
    {
        Assert.True(TrackText.Same("ЗВОНКИЙ / ЕВА ПОЛЬНА - HASTA LA VISTA", "Звонкий, Ева Польна – Hasta La Vista"));
        Assert.False(TrackText.Same("НАТАЛИ - УЛЫБОЧКА", "Кино – Группа крови"));
        Assert.True(TrackText.SameArtist("Ёлка", "елка"));
    }

    [Fact]
    public void MatchesAmgStationsByStreamThenLongestName()
    {
        var list = AmgRadio.Parse("""
            {"stations":{
              "retro":{"name":"Ретро Хит","logo":"https://volna.top/logoradio/retro.webp","track":"НАТАЛИ - УЛЫБОЧКА","cover":"https://i.amgradio.ru/cover/x.webp"},
              "remixfm":{"name":"Remix FM","logo":"https://volna.top/logoradio/remixfm.webp","track":"","cover":""},
              "remixfm_beloreck":{"name":"Remix FM Белорецк","logo":"https://volna.top/logoradio/remixfm_beloreck.webp","track":"","cover":""}
            }}
            """);
        Assert.Equal(3, list.Count);
        var retro = new DeckAction { Id = "r", Kind = "radio", Label = "РЕТРО ХИТ - RETRO HIT", Target = "http://retro.volna.top/Retro" };
        Assert.True(AmgRadio.LooksLikeAmg(retro));
        Assert.Equal("retro", AmgRadio.Match(retro, list)?.Key);
        var beloreck = new DeckAction { Id = "b", Kind = "radio", Label = "Remix FM Белорецк", Target = "https://s.example.com/a" };
        Assert.Equal("remixfm_beloreck", AmgRadio.Match(beloreck, list)?.Key);
        var other = new DeckAction { Id = "n", Kind = "radio", Label = "NDR 2", Target = "https://icecast.ndr.de/ndr/ndr2/hamburg/mp3/128/stream.mp3" };
        Assert.False(AmgRadio.LooksLikeAmg(other));
        Assert.Null(AmgRadio.Match(other, list));
    }

    [Fact]
    public void DetectsImageTypes()
    {
        Assert.Equal("image/jpeg", RadioNowPlaying.ImageMime([0xFF, 0xD8, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("image/webp", RadioNowPlaying.ImageMime(Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")));
        Assert.Null(RadioNowPlaying.ImageMime(Encoding.ASCII.GetBytes("<html>hello</html>")));
    }
}
