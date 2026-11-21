using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public class SteamLibraryTests
{
    [Fact]
    public void LibraryFoldersAreReadFromTheVdf()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\" { \"892970\" \"123\" }\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibrary.LibraryPaths(vdf).ToArray());
    }

    [Fact]
    public void AManifestGivesTheGameAndToolsAreSkipped()
    {
        var g = SteamLibrary.ParseManifest("\"AppState\"\n{\n\t\"appid\"\t\t\"892970\"\n\t\"name\"\t\t\"Valheim\"\n\t\"LastPlayed\"\t\t\"1795290000\"\n\t\"SizeOnDisk\"\t\t\"1600000000\"\n}");
        Assert.Equal(new SteamGame("892970", "Valheim", 1795290000, 1600000000), g);
        Assert.Null(SteamLibrary.ParseManifest("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" }"));
        Assert.Null(SteamLibrary.ParseManifest("\"AppState\" { \"appid\" \"abc\" \"name\" \"Broken\" }"));
    }

    [Fact]
    public void GamesComeFromEveryLibraryNewestFirst()
    {
        var steam = Path.Combine(Path.GetTempPath(), "cb-steam-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "cb-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(other, "steamapps"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{other.Replace(@"\", @"\\")}\" }} }}");
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1.acf"), "\"appid\" \"1\" \"name\" \"Old\" \"LastPlayed\" \"100\"");
            File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_2.acf"), "\"appid\" \"2\" \"name\" \"New\" \"LastPlayed\" \"200\"");

            var games = SteamLibrary.Read(steam);
            Assert.Equal(["New", "Old"], games.Select(g => g.Name).ToArray());

            var lib = new SteamLibrary(NullLogger<SteamLibrary>.Instance);
            Assert.NotNull(lib.Launch("999")); // нет в библиотеке — не запускаем
        }
        finally
        {
            Directory.Delete(steam, true);
            Directory.Delete(other, true);
        }
    }
}
