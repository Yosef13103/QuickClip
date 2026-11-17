using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace QuickClip.Discovery;

internal sealed record GogRegistryEntry(string? Key = null, string? Name = null, string? Path = null,
    string? Parent = null, CatalogIssueKind? Issue = null);

/// <summary>Reads the installed-product registration used by GOG's Windows installers.</summary>
internal static class GogRegistry
{
    private const string ProductsKey = @"Software\GOG.com\Games";
    internal const string SourceName = "GOG registry";

    public static IEnumerable<GogRegistryEntry> Read(CancellationToken token)
    {
        // GOG's documented-by-implementation install registration uses the 32-bit HKLM view.
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var products = hive.OpenSubKey(ProductsKey, writable: false);
        if (products == null)
        {
            yield return new(Issue: CatalogIssueKind.SourceMissing);
            yield break;
        }
        string[] keys = products.GetSubKeyNames();
        foreach (string key in keys)
        {
            token.ThrowIfCancellationRequested();
            yield return ReadProduct(products, key);
        }
    }

    private static GogRegistryEntry ReadProduct(RegistryKey products, string name)
    {
        try
        {
            using var product = products.OpenSubKey(name, writable: false);
            if (product == null) return new(Issue: CatalogIssueKind.Unavailable);
            return new(name, product.GetValue("gameName") as string,
                product.GetValue("path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                Convert.ToString(product.GetValue("dependsOn"), CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(Issue: CatalogIssueKind.Unavailable);
        }
    }

    internal static bool TryProductId(string? text, out long id) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
