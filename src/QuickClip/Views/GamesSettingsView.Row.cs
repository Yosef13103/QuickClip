using System.Collections.Immutable;
using System.ComponentModel;
using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class GamesSettingsView
{
    internal enum DetectionIssue { None, SourceUnavailable, MissingExecutable, OwnershipConflict, UnassociatedExecutable, MinecraftJava }

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
                Match(path).Kind is CatalogMatchKind.Ignored or CatalogMatchKind.Application or CatalogMatchKind.Companion) => BlockedSummary(),
            _ => DetectionReady ? "Ready to record" : "Needs checking",
        };
        private string BlockedSummary()
        {
            var reasons = Entry.Executables.Select(path => ExecutableRole(path)).Distinct(StringComparer.Ordinal).ToArray();
            // Do not imply that a retained game-level denial is a per-file block.
            return reasons is [var reason] && reason.StartsWith(" · Blocked (", StringComparison.Ordinal)
                ? reason[3..] : "Blocked · multiple rules";
        }
        private string? Availability => _offline ? "Source unavailable or scan incomplete · games remembered" :
            Entry.Executables.IsEmpty ? "No executable found · check the source and rescan" :
            _unavailable.Length > 0 ? "Executable unavailable · check the drive or edit executables" : null;
        public bool ShowAdvanced => !_basic;
        internal DetectionIssue Issue { get; private set; }
        internal bool IssueIgnored { get; private set; }
        public bool HasIssue => Issue != DetectionIssue.None;
        public bool IssueIsBlocking => Issue == DetectionIssue.MissingExecutable || Issue == DetectionIssue.OwnershipConflict && !DetectionReady;
        public string DismissIssueLabel => IssueIgnored ? "Unignore issue" : "Ignore issue";
        public string IssueRepairLabel => Issue switch
        {
            DetectionIssue.SourceUnavailable => "Rescan source",
            DetectionIssue.MinecraftJava => "Fix detection…",
            DetectionIssue.MissingExecutable => "Locate game executable…",
            DetectionIssue.OwnershipConflict => "Choose recording owner…",
            _ => "Register gameplay executable…",
        };
        public string IssueReason => Issue switch
        {
            DetectionIssue.SourceUnavailable => "QuickClip could not verify this game source. Reconnect it and scan again.",
            DetectionIssue.MissingExecutable => "QuickClip could not find a usable executable for this game.",
            DetectionIssue.OwnershipConflict => "Multiple game entries claim the same executable.",
            DetectionIssue.UnassociatedExecutable => "The source found an executable but could not associate it with a verified game installation.",
            DetectionIssue.MinecraftJava => "Minecraft's bundled Java runtime was not found. Locate the gameplay executable.",
            _ => "",
        };
        public string IssueTechnicalDetail => string.Join(", ", Entry.Executables.Select(path =>
            $"{Path.GetFileName(path)} · {Match(path).Kind}"));
        internal IEnumerable<string> IssueKeys() => Entry.AliasIds.Prepend(Entry.Id).Select(id => $"{Issue}:{id}");

        internal void EvaluateIssue(IEnumerable<string> ignored, bool scanning)
        {
            var issue = ClassifyIssue(scanning);
            bool wasIgnored = IssueIgnored;
            bool changed = Issue != issue;
            Issue = issue;
            IssueIgnored = HasIssue && IssueKeys().Intersect(ignored, StringComparer.Ordinal).Any();
            if (changed || wasIgnored != IssueIgnored) OnChanged(string.Empty);
        }

        private DetectionIssue ClassifyIssue(bool scanning)
        {
            if (IsExcluded || Entry.Rules.Classification is AppClassification.Application or AppClassification.Companion or
                AppClassification.Launcher) return DetectionIssue.None;

            var matches = Entry.Executables.Select(Match).ToArray();
            if (matches.Any(match => match.Kind == CatalogMatchKind.Ambiguous)) return DetectionIssue.OwnershipConflict;
            if (DetectionReady || matches.Any(match => GameDetector.HasCatalogEligibility(match) && match.Entry?.Id != Entry.Id)) return DetectionIssue.None;
            if (MinecraftProxy && matches.Any(match => match.Kind == CatalogMatchKind.Launcher))
                return DetectionIssue.MinecraftJava;
            if (matches.Length > 0 && matches.All(match => match.Kind is CatalogMatchKind.Launcher or CatalogMatchKind.Ignored or
                CatalogMatchKind.Application or CatalogMatchKind.Companion)) return DetectionIssue.None;
            if (matches.Any(match => match.Kind == CatalogMatchKind.Candidate)) return DetectionIssue.UnassociatedExecutable;
            if (matches.Length > 0 && _unavailable.Length >= matches.Length) return DetectionIssue.MissingExecutable;
            if (scanning) return Issue is DetectionIssue.SourceUnavailable or DetectionIssue.MissingExecutable
                ? Issue : DetectionIssue.None;
            if (_offline || matches.Any(match => match.Kind == CatalogMatchKind.Inactive)) return DetectionIssue.SourceUnavailable;
            if (matches.Length == 0) return DetectionIssue.MissingExecutable;
            return DetectionIssue.None;
        }

        private bool MinecraftProxy => Entry.Executables.Any(path =>
            Path.GetFileName(path).Equals("MCPlaceholderStub.exe", StringComparison.OrdinalIgnoreCase));
        internal bool DetectionReady => !IsExcluded && Entry.Executables.Any(path =>
        {
            var match = Match(path);
            return match.Entry?.Id == Entry.Id && !_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                GameDetector.HasCatalogEligibility(match);
        });
        public string Summary => HasIssue ? IssueReason : _basic ? Eligibility :
            Availability is { } issue ? $"{Source} · {Eligibility} · {issue}" : $"{Source} · {Eligibility}";

        internal void UseBasicPresentation(bool basic)
        {
            if (_basic == basic) return;
            _basic = basic;
            OnChanged(nameof(ShowAdvanced));
            OnChanged(nameof(Summary));
        }

        public sealed record ExecutableChoice(string Path, string Label, bool ExplicitlyBlocked)
        {
            public string BlockAction => ExplicitlyBlocked ? "Unblock selected executable" : "Block selected executable";
        }
        public IEnumerable<ExecutableChoice> Executables => Entry.Executables.Select(path => new ExecutableChoice(path,
            path + ExecutableRole(path) +
            (_unavailable.Contains(path, StringComparer.OrdinalIgnoreCase) ? " · unavailable" : ""),
            _blocked.Contains(path, StringComparer.OrdinalIgnoreCase)));

        private string ExecutableRole(string path)
        {
            var match = Match(path);
            return match.Kind switch
            {
                CatalogMatchKind.Launcher => " · Launcher",
                CatalogMatchKind.Companion => " · Blocked (companion application)",
                CatalogMatchKind.Application => " · Blocked (non-game application)",
                CatalogMatchKind.Ignored when _blocked.Contains(path, StringComparer.OrdinalIgnoreCase) => " · Blocked (explicit executable block)",
                CatalogMatchKind.Ignored when IsExcluded || match.Entry?.Rules.Inclusion == CatalogInclusion.Exclude => " · Blocked (game disabled)",
                CatalogMatchKind.Ignored when CatalogPaths.DefaultRole(path) == CatalogMatchKind.Ignored => " · Blocked (known non-game executable)",
                CatalogMatchKind.Ignored => " · Blocked (another game exclusion or retained denial)",
                CatalogMatchKind.Ambiguous => " · claimed by multiple catalog entries",
                CatalogMatchKind.Inactive => " · Source not checked yet",
                _ when _offline => " · source offline or scan incomplete",
                _ when GameDetector.HasCatalogEligibility(match) && match.Entry?.Id != Entry.Id => $" · Records as {match.Entry?.DisplayName}",
                _ when GameDetector.HasCatalogEligibility(match) => Entry.Rules.Activation == CatalogActivation.ForegroundOnly
                    ? " · Ready in foreground" : " · Ready to record",
                _ => " · Not ready to record",
            };
        }

        public string IssueImpact => Issue == DetectionIssue.OwnershipConflict
            ? "Shared files cannot start automatic recording until ownership is resolved. Other eligible files still work."
            : "Automatic recording is unavailable for this entry.";

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
