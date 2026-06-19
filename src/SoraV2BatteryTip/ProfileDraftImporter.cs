using System.Text.Json;

namespace SoraV2BatteryTip;

internal sealed class ProfileDraftImporter
{
    private readonly AppPaths _paths;
    private readonly KnownDeviceProfileProvider _profileProvider;

    public ProfileDraftImporter(AppPaths paths, KnownDeviceProfileProvider profileProvider)
    {
        _paths = paths;
        _profileProvider = profileProvider;
    }

    public ProfileDraftImportResult ImportLatestVerifiedDrafts(int tolerance = 1)
    {
        _paths.Ensure();
        var latestRoot = FindLatestCandidateRoot();
        if (latestRoot == null)
            return new ProfileDraftImportResult();

        return ImportVerifiedDrafts(latestRoot, tolerance);
    }

    public ProfileDraftImportResult ImportVerifiedDrafts(string candidateRoot, int tolerance = 1)
    {
        return ImportVerifiedDrafts(candidateRoot, new[] { tolerance }, stableReadsRequired: 1);
    }

    public ProfileDraftImportResult ImportVerifiedDraftsProgressive(string candidateRoot)
    {
        return ImportVerifiedDrafts(candidateRoot, new[] { 1, 3, 5 }, stableReadsRequired: 3);
    }

    private ProfileDraftImportResult ImportVerifiedDrafts(string candidateRoot, int[] tolerances, int stableReadsRequired)
    {
        _paths.Ensure();
        if (string.IsNullOrWhiteSpace(candidateRoot) || !Directory.Exists(candidateRoot))
            return new ProfileDraftImportResult();

        var latestRoot = candidateRoot;
        var draftsDir = Path.Combine(latestRoot, "profile-drafts");
        if (!Directory.Exists(draftsDir))
            return new ProfileDraftImportResult { SourceDirectory = latestRoot, StableReadsRequired = stableReadsRequired };

        var officialBattery = ReadOfficialBattery(Path.Combine(latestRoot, "battery-candidates.json"));
        if (officialBattery == null)
            return new ProfileDraftImportResult { SourceDirectory = latestRoot, StableReadsRequired = stableReadsRequired };

        var rejectedDir = Path.Combine(latestRoot, "rejected-drafts");
        Directory.CreateDirectory(rejectedDir);

        var files = Directory.GetFiles(draftsDir, "*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            return new ProfileDraftImportResult
            {
                Total = 0,
                OfficialBatteryPercentage = officialBattery,
                SourceDirectory = latestRoot,
                StableReadsRequired = stableReadsRequired
            };
        }

        foreach (var tolerance in tolerances.Distinct().OrderBy(value => value))
        {
            var accepted = files
                .Select(file => new VerifiedDraft(file, VerifyDraft(file, officialBattery.Value, tolerance, stableReadsRequired)))
                .Where(item => item.Score.IsAccepted)
                .OrderBy(item => item.Score.Delta)
                .ThenBy(item => item.Score.Spread)
                .ThenByDescending(item => item.Score.Average)
                .ToArray();

            if (accepted.Length == 0)
                continue;

            var best = accepted[0];
            var target = NextAvailableTarget(Path.Combine(_paths.ProfilesDirectory, Path.GetFileName(best.File)));
            File.Copy(best.File, target, overwrite: false);

            foreach (var file in files.Where(file => !string.Equals(file, best.File, StringComparison.OrdinalIgnoreCase)))
            {
                var rejectTarget = NextAvailableTarget(Path.Combine(rejectedDir, Path.GetFileName(file)));
                File.Copy(file, rejectTarget, overwrite: false);
            }

            return new ProfileDraftImportResult
            {
                Imported = 1,
                Rejected = files.Length - 1,
                Total = files.Length,
                ToleranceUsed = tolerance,
                StableReadsRequired = stableReadsRequired,
                OfficialBatteryPercentage = officialBattery,
                SourceDirectory = latestRoot,
                ImportedFiles = new[] { Path.GetFileName(target) }
            };
        }

        foreach (var file in files)
        {
            var target = NextAvailableTarget(Path.Combine(rejectedDir, Path.GetFileName(file)));
            File.Copy(file, target, overwrite: false);
        }

        return new ProfileDraftImportResult
        {
            Imported = 0,
            Rejected = files.Length,
            Total = files.Length,
            ToleranceUsed = tolerances.DefaultIfEmpty(0).Max(),
            StableReadsRequired = stableReadsRequired,
            OfficialBatteryPercentage = officialBattery,
            SourceDirectory = latestRoot
        };
    }

    private DraftVerificationScore VerifyDraft(string file, int officialBattery, int tolerance, int stableReadsRequired)
    {
        var values = new List<int>();
        for (var index = 0; index < stableReadsRequired; index++)
        {
            var reading = _profileProvider.TryReadProfileFile(file);
            if (reading == null)
                return DraftVerificationScore.Rejected;

            values.Add(reading.BatteryPercentage);
            if (index + 1 < stableReadsRequired)
                Thread.Sleep(120);
        }

        var min = values.Min();
        var max = values.Max();
        var spread = max - min;
        var average = values.Average();
        var roundedAverage = (int)Math.Round(average, MidpointRounding.AwayFromZero);
        var delta = Math.Abs(roundedAverage - officialBattery);

        return new DraftVerificationScore
        {
            IsAccepted = spread <= 1 && delta <= tolerance,
            Delta = delta,
            Spread = spread,
            Average = average
        };
    }

    private string? FindLatestCandidateRoot()
    {
        if (!Directory.Exists(_paths.CandidatesDirectory))
            return null;

        return Directory.GetDirectories(_paths.CandidatesDirectory)
            .OrderByDescending(path => path)
            .FirstOrDefault(path => Directory.Exists(Path.Combine(path, "profile-drafts")));
    }

    private static int? ReadOfficialBattery(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("officialBatteryPercentage", out var value) && value.TryGetInt32(out var battery) && battery is >= 1 and <= 100)
                return battery;
        }
        catch { }

        return null;
    }

    private static string NextAvailableTarget(string target)
    {
        if (!File.Exists(target))
            return target;

        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(dir, $"{name}-{index}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private sealed record VerifiedDraft(string File, DraftVerificationScore Score);

    private sealed class DraftVerificationScore
    {
        public static readonly DraftVerificationScore Rejected = new();
        public bool IsAccepted { get; init; }
        public int Delta { get; init; } = int.MaxValue;
        public int Spread { get; init; } = int.MaxValue;
        public double Average { get; init; }
    }
}
