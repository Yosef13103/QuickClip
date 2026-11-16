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
        private string? _actionError;
        private GameCatalogIndex? _catalog;
        private string[] _unavailable = [];

        public CatalogEntry Entry { get; private set; } = entry;
        public bool IsManual => Entry.Source == CatalogSource.Manual;
        internal bool CheckingExecutables { get; set; }
        public bool IsExcluded => Entry.Rules.Inclusion == CatalogInclusion.Exclude;
        public bool CaptureEnabled => !IsExcluded;

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
        public string? ActionError
        {
            get => _actionError;
            set
            {
                if (_actionError == value) return;
                _actionError = value;
                OnChanged(nameof(ActionError));
            }
        }

        private string Source => string.Join(" / ",
            (Entry.Provenance.IsEmpty ? [Entry.Source] : Entry.Provenance)
                .Select(source => source == CatalogSource.Folder ? "Game folder" : LocalLaunchers.Name(source)));
        private string Eligibility => IsExcluded ? "Disabled" : Entry.Rules.Classification switch
        {
            AppClassification.Application or AppClassification.Companion => "Blocked",
            AppClassification.Launcher => "Launcher",

            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Launcher) =>
                Entry.Source == CatalogSource.Xbox || Entry.Provenance.Contains(CatalogSource.Xbox)
                    ? "Game found · needs gameplay executable" : "Launcher",
            _ when !Entry.Executables.IsEmpty && Entry.Executables.All(path =>
                Match(path).Kind is CatalogMatchKind.Ignored or CatalogMatchKind.Application or CatalogMatchKind.Companion) => "Blocked",
            _ => DetectionReady ? "Ready to record" : "Needs checking",
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
            : _basic ? Eligibility :
            Availability is { } issue ? $"{Source} · {Eligibility} · {issue}" : $"{Source} · {Eligibility}";

        internal void UseBasicPresentation(bool basic)
        {
            if (_basic == basic) return;
            _basic = basic;
            OnChanged(nameof(ShowAdvanced));
            OnChanged(nameof(Summary));
        }

        public sealed record ExecutableChoice(string Path, string Label);
        public IEnumerable<ExecutableChoice> Executables => Entry.Executables.Select(path => new ExecutableChoice(path,
            path + ExecutableRole(path) +
            (_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) ? " · unavailable" : "")));

        private string ExecutableRole(string path)
        {
            var match = Match(path);
            return match.Kind switch
            {
                CatalogMatchKind.Launcher => " · Launcher",
                CatalogMatchKind.Companion or CatalogMatchKind.Application or CatalogMatchKind.Ignored => " · Blocked",
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
