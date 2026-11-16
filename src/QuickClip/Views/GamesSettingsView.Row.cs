using System.Collections.Immutable;
using System.ComponentModel;
using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class GamesSettingsView
{
    internal sealed class GameRow(CatalogEntry entry) : INotifyPropertyChanged
    {
        private const string DisplayIcon = "M1,2 H15 V11 H1 Z M8,11 V14 M5,14 H11";
        private bool _expanded;
        private bool _basic;
        private ImmutableArray<string> _blocked = [];
        private bool _offline;
        private GameCatalogIndex? _catalog;
        private string[] _unavailable = [];

        public CatalogEntry Entry { get; private set; } = entry;
        public bool IsManual => Entry.Source == CatalogSource.Manual;
        internal bool CheckingExecutables { get; set; }
        public bool IsExcluded => Entry.Rules.Inclusion == CatalogInclusion.Exclude;
        public bool CaptureEnabled => !IsExcluded;
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
        public string Diagnostics => $"{Entry.Id}";

        private string Source => string.Join(" / ",
            (Entry.Provenance.IsEmpty ? [Entry.Source] : Entry.Provenance)
                .Select(source => source == CatalogSource.Folder ? "Game folder" : LocalLaunchers.Name(source)));
        private string Eligibility => IsExcluded ? "Disabled" : Entry.Rules.Classification switch
        {
            AppClassification.Application => "Utility",
            AppClassification.Companion => "Not a game",
            AppClassification.Launcher => "Launcher",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(CatalogPaths.IsDiscordProcess) => "Not a game",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Companion) => "Not a game",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Launcher) =>
                Entry.Source == CatalogSource.Xbox || Entry.Provenance.Contains(CatalogSource.Xbox)
                    ? "Game found · needs gameplay executable" : "Launcher",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Application) => "Utility",
            _ => Entry.Executables.Any(path => GameDetector.HasCatalogEligibility(Match(path)))
                ? "Game · Enabled" : "Game · Needs checking",
        };
        private string? Availability => _offline ? "Source unavailable or scan incomplete · games remembered" :
            Entry.Executables.IsEmpty ? "No executable found · check the source and rescan" :
            _unavailable.Length > 0 ? "Executable unavailable · check the drive or edit executables" : null;
        public bool ShowAdvanced => !_basic;
        private bool MinecraftProxy => Entry.Executables.Any(path =>
            Path.GetFileName(path).Equals("MCPlaceholderStub.exe", StringComparison.OrdinalIgnoreCase));
        internal bool DetectionReady => !IsExcluded && Entry.Executables.Any(path =>
        {
            var match = Match(path);
            return match.Entry?.Id == Entry.Id && GameDetector.HasCatalogEligibility(match);
        });
        public string Summary => !IsExcluded && MinecraftProxy && !DetectionReady
            ? "Minecraft launcher found. For Java Edition, start manual recording on the allowed display unless you have a dedicated game executable."
            : _basic ? IsExcluded ? "Recording for this game is off" : DetectionReady ? "Ready for automatic detection" :
            _offline ? "Game needs checking" : "Automatic detection needs setup" :
            Availability is { } issue ? $"{Source} · {Eligibility} · {issue}" : $"{Source} · {Eligibility}";

        internal void UseBasicPresentation(bool basic)
        {
            if (_basic == basic) return;
            _basic = basic;
            OnChanged(nameof(ShowAdvanced));
            OnChanged(nameof(Summary));
        }

        public sealed record ClassificationChoice(AppClassification Value, string Label);
        private static readonly ClassificationChoice[] ClassificationOptions =
        [
            new(AppClassification.Automatic, "Use automatic detection"),
            new(AppClassification.Game, "Treat as a game"),
            new(AppClassification.Application, "Utility"),
            new(AppClassification.Launcher, "Launcher"),
            new(AppClassification.Companion, "Not a game"),
        ];
        public ClassificationChoice[] Classifications => ClassificationOptions;
        public sealed record ExecutableChoice(string Path, string Label, bool CanUse);
        public IEnumerable<ExecutableChoice> Executables => Entry.Executables.Select(path => new ExecutableChoice(path,
            path + ExecutableRole(path) +
            (_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) ? " · unavailable" : ""),
            !_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) && RulesAfterApproval([path]) != null));

        private string ExecutableRole(string path)
        {
            var match = Match(path);
            return match.Kind switch
            {
                CatalogMatchKind.Companion => " · Not a game",
                CatalogMatchKind.Launcher => " · Launcher",
                CatalogMatchKind.Application => " · Not a game",
                CatalogMatchKind.Ignored => " · Blocked",
                CatalogMatchKind.Ambiguous => " · claimed by multiple catalog entries",
                CatalogMatchKind.Inactive => " · Source not checked yet",
                _ when _offline => " · source offline or scan incomplete",
                _ when GameDetector.HasCatalogEligibility(match) => Entry.Rules.Activation == CatalogActivation.ForegroundOnly
                    ? " · Ready in foreground" : " · Ready to record",
                _ => " · Not ready to record",
            };
        }

        private CatalogMatch Match(string path) => GameDetector.ApplyProcessRole(path,
            (_catalog ??= new(new([Entry], [], 0, false, default), _blocked)).Match(path, true));

        internal CatalogRules? RulesAfterApproval(IReadOnlyList<string> paths)
        {
            if (_offline || Entry.Source == CatalogSource.Manual ||
                Entry.Rules.Classification != AppClassification.Automatic) return null;

            var rules = Entry.Rules;
            var approved = rules.ApprovedExecutables.ToBuilder();
            foreach (var path in paths)
            {
                var match = Match(path);
                if (match.Entry?.Id != Entry.Id || match.Kind is not (CatalogMatchKind.Candidate or CatalogMatchKind.Game))
                    return null;
                if (match.Kind == CatalogMatchKind.Candidate && !approved.Contains(path, StringComparer.OrdinalIgnoreCase))
                    approved.Add(path);
            }
            return approved.Count == rules.ApprovedExecutables.Length ? null : rules with { ApprovedExecutables = approved.ToImmutable() };
        }

        public bool Matches(string query) => Matches(Entry, query);
        public static bool Matches(CatalogEntry entry, string query) =>
            entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Executables.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase));

        public void Update(CatalogEntry value, bool offline, ImmutableArray<string> blocked, GameCatalogIndex catalog)
        {
            bool pathsChanged = !Entry.Executables.SequenceEqual(value.Executables);
            _catalog = catalog;
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
