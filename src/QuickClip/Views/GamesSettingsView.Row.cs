using System.Collections.Immutable;
using System.ComponentModel;
using QuickClip.Discovery;

namespace QuickClip.Views;

public partial class GamesSettingsView
{
    internal sealed class GameRow(CatalogEntry entry) : INotifyPropertyChanged
    {
        private const string DisplayIcon = "M1,2 H15 V11 H1 Z M8,11 V14 M5,14 H11";
        private bool _expanded;
        private ImmutableArray<string> _blocked = [];
        private bool _offline;
        private string[] _unavailable = [];

        public CatalogEntry Entry { get; private set; } = entry;
        public bool IsManual => Entry.Source == CatalogSource.Manual;
        internal bool CheckingExecutables { get; set; }
        public bool IsExcluded => Entry.Rules.Inclusion == CatalogInclusion.Exclude;
        public bool IsAdvancedExpanded { get; set; }
        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                OnChanged(nameof(IsExpanded));
                OnChanged(nameof(DetailsAction));
                OnChanged(nameof(Chevron));
            }
        }
        public string Chevron => IsExpanded ? "\uE70E" : "\uE70D";
        public string DetailsAction => (IsExpanded ? "Hide details for " : "Show details for ") + Entry.DisplayName;
        public string CaptureIcon => IsExcluded ? DisplayIcon + " M1,15 L15,1" : DisplayIcon;
        public string CaptureAction => (IsExcluded ? "Enable" : "Disable") + $" automatic targeting for {Entry.DisplayName}";
        public string Inheritance => "Uses global Recording and Audio settings.";
        public string Diagnostics => $"{Entry.Id} · Catalog association confidence: {Entry.Confidence}";

        private string Source => Entry.Source == CatalogSource.Folder ? "Approved folder" : Entry.Source.ToString();
        private string Eligibility => IsExcluded ? "Ignored · Disabled" : Entry.Rules.Classification switch
        {
            AppClassification.Application => "Utility",
            AppClassification.Companion => "Companion app",
            AppClassification.Launcher => "Launcher",
            AppClassification.Game => "Game",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(CatalogPaths.IsDiscordProcess) => "Companion app",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Companion) => "Companion app",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Launcher) => "Launcher",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Application) => "Utility",
            _ => Entry.Source == CatalogSource.Manual ? "Game · enabled" : "Game candidate",
        };
        private string? Availability => _offline ? "Source unavailable or scan incomplete · games remembered" :
            Entry.Executables.IsEmpty ? "No executable found · check the source and rescan" :
            _unavailable.Length > 0 ? "Executable unavailable · check the drive or edit executables" :
            Entry.Source == CatalogSource.Epic && Entry.Rules.Classification == AppClassification.Automatic
                ? "Game window unverified · check executable and detection rules" : null;
        public string Summary => Availability is { } issue ? $"{Source} · {Eligibility} · {issue}" : $"{Source} · {Eligibility}";

        public sealed record ClassificationChoice(AppClassification Value, string Label);
        private static readonly ClassificationChoice[] ClassificationOptions =
        [
            new(AppClassification.Automatic, "Use automatic detection"),
            new(AppClassification.Game, "Treat as a game"),
            new(AppClassification.Application, "Utility"),
            new(AppClassification.Launcher, "Launcher"),
            new(AppClassification.Companion, "Companion app"),
        ];
        public ClassificationChoice[] Classifications => ClassificationOptions;
        public sealed record ExecutableChoice(string Path, string Label);
        public IEnumerable<ExecutableChoice> Executables => Entry.Executables.Select(path => new ExecutableChoice(path,
            path + ExecutableRole(path) +
            (_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) ? " · unavailable" : "")));

        private string ExecutableRole(string path)
        {
            if (IsExcluded || _blocked.Contains(path, StringComparer.OrdinalIgnoreCase)) return " · ignored";
            if (CatalogPaths.IsDiscordProcess(path)) return CatalogPaths.IsDiscordUpdater(path) ? " · ignored" : " · companion";
            if (Entry.Rules.LauncherExecutables.Contains(path, StringComparer.OrdinalIgnoreCase)) return " · launcher";
            return Entry.Rules.Classification == AppClassification.Automatic ? CatalogPaths.DefaultRole(path) switch
            {
                CatalogMatchKind.Companion => " · companion",
                CatalogMatchKind.Launcher => " · launcher",
                CatalogMatchKind.Application => " · utility",
                _ => "",
            } : "";
        }

        public bool Matches(string query) => Matches(Entry, query);
        public static bool Matches(CatalogEntry entry, string query) =>
            entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Executables.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase));

        public void Update(CatalogEntry value, bool offline, ImmutableArray<string> blocked)
        {
            bool pathsChanged = !Entry.Executables.SequenceEqual(value.Executables);
            if (!pathsChanged && Entry.Rules == value.Rules && Entry.Name == value.Name && _offline == offline && _blocked == blocked) return;
            _blocked = blocked;
            Entry = value;
            _offline = offline;
            if (pathsChanged) _unavailable = [];
            OnChanged(string.Empty);
        }

        public void SetUnavailableExecutables(string[] paths)
        {
            if (_unavailable.SequenceEqual(paths)) return;
            _unavailable = paths;
            OnChanged(nameof(Summary));
            OnChanged(nameof(Executables));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));
    }
}
