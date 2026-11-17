using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QuickClip.Discovery;

internal sealed record LauncherInstall(string? Id = null, string? Name = null, string? Directory = null,
    string? Renderer = null, string? Launcher = null, CatalogIssueKind? Issue = null);

internal static class LocalLaunchers
{
    internal static bool IsSupported(CatalogSource source) => source is CatalogSource.Xbox or CatalogSource.Ea or CatalogSource.Riot
        or CatalogSource.Ubisoft or CatalogSource.BattleNet or CatalogSource.Rockstar
        or CatalogSource.Heroic or CatalogSource.Origin or CatalogSource.Amazon or CatalogSource.Itch;

    internal static string Name(CatalogSource source) => source switch
    {
        CatalogSource.Gog => "GOG",
        CatalogSource.Xbox => "Xbox PC / Game Pass",
        CatalogSource.Ea => "EA App",
        CatalogSource.Riot => "Riot Games",
        CatalogSource.Ubisoft => "Ubisoft Connect",
        CatalogSource.BattleNet => "Battle.net",
        CatalogSource.Rockstar => "Rockstar Games",
        CatalogSource.Amazon => "Amazon Games",
        CatalogSource.Heroic => "Heroic",
        CatalogSource.Origin => "Legacy Origin",
        CatalogSource.Itch => "itch.io",
        _ => source.ToString(),
    };

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UbisoftKey = @"Software\Ubisoft\Launcher\Installs";
    private const string EaGamesKey = @"Software\EA Games";
    private static readonly Regex BattleNetCommand = new(
        @"(?:^|[\\/])(?:Battle\.net\.exe|Battle\.net[\\/]Agent[\\/]Blizzard Uninstaller\.exe)""?\s+(?:.*?\s)?--uid=(?<id>[a-z0-9_]+)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RockstarCommand = new(
        @"(?:^|[\\/])(?:Launcher|uninstall)\.exe""?\s+.*?\buninstall=(?<id>[a-z0-9_]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal sealed record Registration(string Key, string? Name, string? Directory, string? Command);

    internal static LauncherInstall? ParseRegistration(CatalogSource source, Registration item)
    {
        if (source == CatalogSource.Ubisoft)
            return item.Key.Length > 0 && item.Key.All(char.IsAsciiDigit) ? new(item.Key, item.Name, item.Directory) : null;
        if (source == CatalogSource.Ea)
            return item.Directory == null ? null : new(item.Key, item.Key, item.Directory);

        if (source == CatalogSource.Riot)
        {
            return item.Key.ToLowerInvariant() switch
            {
                "riot game league_of_legends.live" => new("league_of_legends", "League of Legends", item.Directory,
                    @"Game\League of Legends.exe", "LeagueClient.exe"),
                "riot game league_of_legends.pbe" => new("league_of_legends.pbe", "League of Legends PBE", item.Directory,
                    @"Game\League of Legends.exe", "LeagueClient.exe"),
                "riot game valorant.live" => new("valorant", "VALORANT", item.Directory,
                    @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "VALORANT.exe"),
                "riot game bacon.live" => new("bacon", "Legends of Runeterra", item.Directory, "LoR.exe"),
                "riot game lion.live" => new("lion", "2XKO", item.Directory, Launcher: "Lion.exe"),
                _ => null,
            };
        }

        if (item.Command is not { Length: > 0 and <= 4096 } command) return null;
        if (source == CatalogSource.BattleNet)
        {
            string id = BattleNetCommand.Match(command).Groups["id"].Value.ToLowerInvariant();
            return BattleNetProduct(id, item.Name, item.Directory);
        }

        string title = RockstarCommand.Match(command).Groups["id"].Value.ToLowerInvariant();
        return title switch
        {
            "gta5" => new(title, item.Name, item.Directory, "GTA5.exe", "PlayGTAV.exe"),
            "gta5_gen9" => new(title, item.Name, item.Directory, "GTA5_Enhanced.exe", "GTA5_Enhanced_BE.exe"),
            "rdr" => new(title, item.Name, item.Directory, "RDR.exe"),
            "rdr2" => new(title, item.Name, item.Directory, "RDR2.exe", "PlayRDR2.exe"),
            "lanoire" => new(title, item.Name, item.Directory, "LANoire.exe"),
            "mp3" => new(title, item.Name, item.Directory, "MaxPayne3.exe"),
            "lanoirevr" => new(title, item.Name, item.Directory, "LANoireVR.exe"),
            "gtasa" => new(title, item.Name, item.Directory, "gta_sa.exe"),
            "gta3" => new(title, item.Name, item.Directory, "gta3.exe"),
            "gtavc" => new(title, item.Name, item.Directory, "gta-vc.exe"),
            "bully" => new(title, item.Name, item.Directory, "Bully.exe"),
            "gta4" => new(title, item.Name, item.Directory, "GTAIV.exe"),
            "gta3unreal" => new(title, item.Name, item.Directory, @"Gameface\Binaries\Win64\LibertyCity.exe"),
            "gtavcunreal" => new(title, item.Name, item.Directory, @"Gameface\Binaries\Win64\ViceCity.exe"),
            "gtasaunreal" => new(title, item.Name, item.Directory, @"Gameface\Binaries\Win64\SanAndreas.exe"),
            _ => null,
        };
    }

    internal static LauncherInstall? BattleNetProduct(string id, string? name, string? directory)
    {
        // Playnite's default PC products. Exact IDs exclude components and most test patchlines.
        if (id is not ("wow" or "wow_classic" or "wow_classic_era" or "diablo3" or "s2" or "s1" or "hs_beta"
            or "heroes" or "prometheus" or "viper" or "odin" or "w3" or "lazarus" or "zeus" or "wlby"
            or "osi" or "rtro" or "fore" or "anbs" or "auks" or "fen" or "d1" or "w1r" or "w2r"
            or "w1" or "w2" or "gryphon" or "aris" or "scorpio" or "arkansas" or "libra" or "pinta" or "aqua")) return null;
        return id switch
        {
            "wow" => new(id, name ?? "World of Warcraft", directory, @"_retail_\Wow.exe", "World of Warcraft Launcher.exe"),
            "wow_classic" => new(id, name ?? "World of Warcraft Classic", directory, @"_classic_\WowClassic.exe"),
            "wow_classic_era" => new(id, name ?? "World of Warcraft Classic Era", directory, @"_classic_era_\WowClassic.exe"),
            "prometheus" => new(id, name ?? "Overwatch", directory, @"_retail_\Overwatch.exe", "Overwatch Launcher.exe"),
            _ => new(id, name, directory),
        };
    }

    internal static IEnumerable<LauncherInstall?> ReadRegistry(CatalogSource source)
    {
        RegistryHive[] hives = source switch
        {
            CatalogSource.Riot => [RegistryHive.CurrentUser],
            CatalogSource.Ubisoft or CatalogSource.Ea => [RegistryHive.LocalMachine],
            _ => [RegistryHive.LocalMachine, RegistryHive.CurrentUser],
        };
        string root = source switch { CatalogSource.Ubisoft => UbisoftKey, CatalogSource.Ea => EaGamesKey, _ => UninstallKey };
        bool present = false;
        foreach (var hive in hives)
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var key = OpenRegistry(hive, view, root, out bool denied);
            if (denied)
            {
                yield return new(Issue: CatalogIssueKind.Unavailable);
                continue;
            }
            if (key == null) continue;
            if (source is CatalogSource.Ubisoft or CatalogSource.Ea) present = true;
            string[]? names = ReadNames(key);
            if (names == null)
            {
                yield return new(Issue: CatalogIssueKind.Unavailable);
                continue;
            }
            foreach (string name in names)
            {
                var install = ReadRegistration(source, key, name, out bool client);
                present |= client || install is { Issue: null };
                yield return install;
            }
        }
        if (!present) yield return new(Issue: CatalogIssueKind.SourceMissing);
    }

    private static RegistryKey? OpenRegistry(RegistryHive hive, RegistryView view, string path, out bool denied)
    {
        denied = false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ReadFailed(ex)) { denied = true; return null; }
    }

    private static string[]? ReadNames(RegistryKey key)
    {
        try { return key.SubKeyCount <= 20_000 ? key.GetSubKeyNames() : null; }
        catch (Exception ex) when (ReadFailed(ex)) { return null; }
    }

    private static LauncherInstall? ReadRegistration(CatalogSource source, RegistryKey parent, string name, out bool client)
    {
        client = false;
        if (source == CatalogSource.Riot && !name.StartsWith("Riot ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var key = parent.OpenSubKey(name, writable: false);
            if (key == null) return null;
            string? Read(string field) => key.GetValue(field, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string? displayName = Read("DisplayName");
            client = source switch
            {
                CatalogSource.Riot => name.Equals("Riot Client", StringComparison.OrdinalIgnoreCase),
                CatalogSource.BattleNet => displayName is "Battle.net" or "Battle.net App",
                CatalogSource.Rockstar => displayName == "Rockstar Games Launcher",
                _ => false,
            };
            string field = source switch { CatalogSource.Ubisoft => "InstallDir", CatalogSource.Ea => "Install Dir", _ => "InstallLocation" };
            return ParseRegistration(source, new(name, displayName, Read(field),
                source is CatalogSource.BattleNet or CatalogSource.Rockstar ? Read("UninstallString") : null));
        }
        catch (Exception ex) when (ReadFailed(ex)) { return new(Issue: CatalogIssueKind.Unavailable); }
    }

    private static bool ReadFailed(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
