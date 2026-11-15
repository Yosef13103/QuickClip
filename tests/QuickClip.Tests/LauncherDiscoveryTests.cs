using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickClip.Discovery;
using Scan = QuickClip.Discovery.GameCatalogService.Scan;

namespace QuickClip.Tests;

public sealed class LauncherDiscoveryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("quickclip-launchers-");

    [Theory]
    [InlineData("Ea", "Battlefield 2042", null, "battlefield 2042", "Game.exe")]
    [InlineData("Ubisoft", "123", null, "123", "Game.exe")]
    [InlineData("BattleNet", "Overwatch", @"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --lang=enUS --uid=prometheus --uninstall", "prometheus", @"_retail_\Overwatch.exe")]
    [InlineData("Riot", "Riot Game valorant.live", null, "valorant", @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")]
    [InlineData("Rockstar", "Grand Theft Auto V", @"""C:\Rockstar\Launcher.exe"" -uninstall=gta5", "gta5", "GTA5.exe")]
    public async Task Registry_metadata_produces_verified_games_and_retains_unverified_cache(
        string sourceName, string key, string? command, string id, string renderer)
    {
        var source = Enum.Parse<CatalogSource>(sourceName);
        string first = _root.CreateSubdirectory("Game").FullName;
        string executable = WriteExecutable(first, renderer);
        File.WriteAllBytes(Path.Combine(first, "Update.exe"), [1]);
        var record = Assert.IsType<LauncherInstall>(LocalLaunchers.ParseRegistration(source, new(key, "Fixture game", first, command)));
        string cached = _root.CreateSubdirectory("Cached").FullName;
        string stale = WriteExecutable(cached, "Cached.exe");
        LauncherInstall?[] registrations = [record, new("old", "Cached game", cached)];
        GameCatalogSettings state = new() { AutoScan = false, EnabledLaunchers = [source] };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, localInstalls: _ => registrations));
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(CatalogMatchKind.Game, Match(state, executable));
        Assert.Equal(CatalogMatchKind.Game, Match(state, stale));

        registrations = [new(Issue: CatalogIssueKind.InvalidManifest), record, new("bad", "Not local", @"\\host\share\Game")];
        await coordinator.RequestAsync(manual: true);
        var row = Assert.Single(GameCatalogRuntime.Rows(state), row => !row.IsUnverified);
        Assert.Equal($"{sourceName.ToLowerInvariant()}:{id}", row.Id);
        Assert.Equal(CatalogMatchKind.Game, Match(state, executable));
        Assert.Equal(CatalogMatchKind.Inactive, Match(state, stale));
        Assert.Equal(CatalogMatchKind.Application, Match(state, Path.Combine(first, "Update.exe")));
        Assert.Equal(1, Assert.Single(state.SourceClaims!.Value).DiscoveredCount);
        Assert.Equal(1, state.DiscoveredCount);
    }

    [Theory]
    [InlineData("Xbox")]
    [InlineData("Heroic")]
    [InlineData("Origin")]
    [InlineData("Amazon")]
    [InlineData("Itch")]
    public async Task File_sources_feed_incremental_claims_with_bounded_metadata(string sourceName)
    {
        string install = _root.CreateSubdirectory(sourceName switch
        {
            "Itch" => @"itch\apps\Demo",
            "Xbox" => @"XboxGames\Demo\Content",
            _ => "Demo",
        }).FullName;
        string executable = WriteExecutable(install, "Demo.exe");
        string bad = _root.CreateSubdirectory("Bad").FullName;
        var source = Enum.Parse<CatalogSource>(sourceName);
        using var metadata = GameCatalogService.CreateScan(new());
        IEnumerable<LauncherInstall?> records = source switch
        {
            CatalogSource.Xbox => Xbox(metadata, install),
            CatalogSource.Heroic => Heroic(metadata, install),
            CatalogSource.Origin => Origin(metadata, install),
            CatalogSource.Amazon => Amazon(metadata, install),
            _ => Itch(metadata),
        };
        var request = new CatalogRequest { Launchers = [source], MaxEntries = 1 };
        using var scan = GameCatalogService.CreateScan(request, localInstalls: _ =>
            records.Append(new("bad", "Bad record", bad, @"..\Demo\Demo.exe")));
        CatalogResult result;
        int slices = 0;
        do
        {
            result = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
            slices++;
        } while (scan.HasMore);
        Assert.True(slices > 1);
        var scope = new CatalogScope(source);
        var state = CatalogClaims.Apply(new() { AutoScan = false, EnabledLaunchers = [source] }, scope, result, false);
        Assert.Equal(CatalogMatchKind.Game, Match(state, executable));
        Assert.DoesNotContain(executable, GameCatalogRuntime.Rows(state).Single(row => row.Id.EndsWith(":bad")).Executables);
    }

    [Fact]
    public void Xbox_markers_and_manifests_reject_traversal_and_external_entities()
    {
        Assert.Equal(new[] { "XboxGames" }, Scan.ParseGamingRoot(Marker(@"\XboxGames")));
        Assert.Null(Scan.ParseGamingRoot(Marker(@"..\Outside")));
        Assert.Equal(CatalogIssueKind.InvalidManifest, Scan.ParseXboxManifest(
            "<!DOCTYPE Game [<!ENTITY e SYSTEM 'file:///C:/fixture'>]><Game>&e;</Game>"u8.ToArray(), @"C:\Fixture").Issue);
        var package = Scan.ParseXboxManifest(
            """<Package xmlns="urn:test"><Identity Name="Fixture"/><Properties><DisplayName>Fixture name</DisplayName></Properties><Applications><Application Executable="Proxy.exe"/></Applications></Package>"""u8.ToArray(),
            @"C:\Fixture");
        Assert.Equal("Fixture", package.Id);
        Assert.Null(package.Renderer);
    }

    [Fact]
    public async Task Minecraft_placeholder_is_launcher_metadata_not_gameplay()
    {
        string install = _root.CreateSubdirectory("Minecraft").FullName;
        string stub = WriteExecutable(install, "MCPlaceholderStub.exe");
        var record = Scan.ParseXboxManifest(
            """<Game><Identity Name="MinecraftFixture"/><ShellVisuals DefaultDisplayName="Minecraft"/><ExecutableList><Executable Name="MCPlaceholderStub.exe" TargetDeviceFamily="PC"/></ExecutableList></Game>"""u8.ToArray(),
            install);
        Assert.Null(record.Renderer);
        Assert.Equal("MCPlaceholderStub.exe", record.Launcher);
        using var scan = GameCatalogService.CreateScan(new() { Launchers = [CatalogSource.Xbox] },
            localInstalls: _ => new LauncherInstall?[] { record });
        CatalogResult result;
        do { result = await scan.ContinueAsync(100, TimeSpan.FromSeconds(1), default); } while (scan.HasMore);
        var scope = new CatalogScope(CatalogSource.Xbox);
        var state = CatalogClaims.Apply(new() { AutoScan = false, EnabledLaunchers = [scope.Kind] }, scope, result, false);
        var row = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal("Minecraft", row.DisplayName);
        Assert.Equal(CatalogMatchKind.Launcher, Match(state, stub));
        Assert.Equal(CatalogMatchKind.Launcher, CatalogPaths.DefaultRole(@"C:\Minecraft\MinecraftLauncher.exe"));
        Assert.Equal(CatalogMatchKind.Unknown, CatalogPaths.DefaultRole(@"C:\Other\javaw.exe"));
    }

    [Fact]
    public void Launcher_registration_patterns_skip_client_and_unknown_products()
    {
        Assert.Null(LocalLaunchers.ParseRegistration(CatalogSource.Riot, new("Riot Client", "Riot Client", @"C:\Riot", null)));
        Assert.Null(LocalLaunchers.ParseRegistration(CatalogSource.BattleNet,
            new("Client", "Battle.net", @"C:\Battle.net", @"C:\Battle.net\Battle.net.exe --uid=battle.net --uninstall")));
        Assert.Null(LocalLaunchers.ParseRegistration(CatalogSource.Rockstar,
            new("Unknown", "Unknown", @"C:\Rockstar", @"C:\Rockstar\Launcher.exe -uninstall=unknown")));
        Assert.Null(Scan.ParseOrigin("id=Game%40steam&dipInstallPath=C%3A%5CGame"u8.ToArray()));
    }

    [Fact]
    public void Battle_net_agent_preserves_other_products_after_a_malformed_record()
    {
        byte[] Field(int number, byte[] value)
        {
            Assert.InRange(value.Length, 0, 127);
            return [(byte)(number * 8 + 2), (byte)value.Length, .. value];
        }
        byte[] Product(string id) => [.. Field(1, Encoding.UTF8.GetBytes(id)), .. Field(3, Field(1, """C:\Games\Fixture"""u8.ToArray()))];
        byte[] inventory = [.. Field(1, Product("battle.net")), .. Field(1, [0xff]),
            .. Field(1, Product("wow")), .. Field(1, Product("prometheus"))];
        var records = BattleNetAgentDatabase.Read(inventory).ToArray();
        Assert.Null(records[0]);
        Assert.Equal(CatalogIssueKind.InvalidManifest, records[1]!.Issue);
        Assert.Equal(@"_retail_\Wow.exe", records[2]!.Renderer);
        Assert.Equal(@"C:\Games\Fixture", records[3]!.Directory);
    }

    [Fact]
    public void Disabling_a_provider_keeps_its_explicit_denial_over_a_manual_entry()
    {
        string install = _root.CreateSubdirectory("Game").FullName;
        string exe = WriteExecutable(install, "Game.exe");
        var scope = new CatalogScope(CatalogSource.Ea);
        GameCatalogSettings state = new()
        {
            EnabledLaunchers = [scope.Kind],
            Request = new()
            {
                ManualEntries = [new(Guid.NewGuid(), "Manual", [exe])],
                Overrides = [new("ea:game", new(Inclusion: CatalogInclusion.Exclude))],
            },
        };
        var entry = new CatalogEntry("ea:game", "Game", scope.Kind, [install], [exe], new());
        state = CatalogClaims.Apply(state, scope, new([entry], [], 1, false, default), false);
        Assert.Equal(CatalogMatchKind.Ignored, Match(state, exe));
        state = CatalogClaims.Reconfigure(state with { EnabledLaunchers = [] });
        Assert.Equal(CatalogMatchKind.Ignored, Match(state, exe));
        state = state with { Request = state.Request with { Overrides = [] } };
        Assert.Equal(CatalogMatchKind.Game, Match(state, exe));
    }

    [Fact]
    public void Matching_Heroic_and_Epic_store_identity_keeps_one_row_and_both_sources()
    {
        string install = _root.CreateSubdirectory("Game").FullName;
        string exe = WriteExecutable(install, "Game.exe");
        GameCatalogSettings state = new() { DiscoverEpic = true, EnabledLaunchers = [CatalogSource.Heroic] };
        foreach (var (source, id) in new[] { (CatalogSource.Epic, "epic:abc"), (CatalogSource.Heroic, "heroic:epic:abc") })
        {
            var entry = new CatalogEntry(id, "Game", source, [install], [exe], new()) { RendererExecutables = [exe] };
            state = CatalogClaims.Apply(state, new(source), new([entry], [], 1, false, default), false);
        }
        var row = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal("epic:abc", row.Id);
        Assert.Equal(new[] { CatalogSource.Epic, CatalogSource.Heroic }, row.Provenance);
        Assert.Equal(CatalogMatchKind.Game, Match(state, exe));
    }

    private IEnumerable<LauncherInstall?> Xbox(Scan scanner, string install)
    {
        string xml = """<Game><Identity Name="Fixture.Game"/><ShellVisuals DefaultDisplayName="Fixture"/><ExecutableList><Executable Name="Demo.exe" TargetDeviceFamily="PC"/></ExecutableList></Game>""";
        File.WriteAllText(Path.Combine(install, "MicrosoftGame.config"), xml);
        WriteFixture(".GamingRoot", Marker(@"\XboxGames"));
        return scanner.ReadXbox([_root.FullName]);
    }

    private IEnumerable<LauncherInstall?> Heroic(Scan scanner, string install)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            installed = new[] { new { appName = "123", install_path = install, platform = "windows", is_dlc = false } },
        });
        WriteFixture(@"heroic\gog_store\installed.json", Encoding.UTF8.GetBytes(json));
        WriteFixture(@"heroic\legendaryConfig\legendary\installed.json", """{"bad":[],"invalid":2}"""u8.ToArray());
        return scanner.ReadHeroic(_root.FullName);
    }

    private IEnumerable<LauncherInstall?> Origin(Scan scanner, string install)
    {
        WriteFixture(@"Origin\LocalContent\install.mfst", Encoding.UTF8.GetBytes($"id=Origin.123&dipInstallPath={Uri.EscapeDataString(install)}"));
        WriteFixture(@"Origin\LocalContent\bad.mfst", "bad"u8.ToArray());
        return scanner.ReadOrigin(_root.FullName);
    }

    private IEnumerable<LauncherInstall?> Amazon(Scan scanner, string install)
    {
        const string relative = @"Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite";
        string database = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE DbSet (Id TEXT, ProductTitle TEXT, InstallDirectory TEXT, Installed INTEGER);
                INSERT INTO DbSet VALUES ('123','Fixture',$path,1),('invalid','Malformed',NULL,1),('owned','Owned',$path,0);
                """;
            command.Parameters.AddWithValue("$path", install);
            command.ExecuteNonQuery();
        }
        return scanner.ReadAmazon(_root.FullName);
    }

    private IEnumerable<LauncherInstall?> Itch(Scan scanner)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write("""{"game":{"id":123,"title":"Demo"},"files":["Demo.exe"]}"""u8);
        WriteFixture(@"itch\apps\Demo\.itch\receipt.json.gz", output.ToArray());
        return scanner.ReadItch(_root.FullName);
    }

    private static byte[] Marker(string folder) => [.. "RGBX"u8, 1, 0, 0, 0, .. Encoding.Unicode.GetBytes(folder + "\0")];

    private void WriteFixture(string relative, byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string WriteExecutable(string install, string relative)
    {
        string path = Path.Combine(install, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private static CatalogMatchKind Match(GameCatalogSettings state, string path) =>
        new GameCatalogIndex(new(GameCatalogRuntime.Rows(state), [], 0, false, default), CatalogClaims.BlockedExecutables(state)).Match(path, true).Kind;

    public void Dispose() => _root.Delete(recursive: true);
}
