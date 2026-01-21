namespace Bobani.Engine;

public record MoodRow(
    string MoodName,
    double CurrentMood,
    int StartingRankMorning
);

public record MoodSelectionResult(
    string SelectedMood,
    List<MoodRow> SortedMoodsLowToHigh,
    List<MoodRow> OutliersLow,
    List<MoodRow> TopOutlierCandidates,
    string SelectionMode, // "OUTLIER" or "IQR_RANDOM"
    Dictionary<string, object> Debug
);

public record ActivityRow(
    string ActivityName,
    double EnergyPoints,
    double NotHangoverPoints,
    double NotBoredomPoints,
    double NotLonelinessPoints,
    double DrunkPoints,
    double HighPoints,
    double SatiaityPoints,
    double EnlightenmentPoints,
    double? MuThreshold,
    double TimeVarianceLow,
    double TimeVarianceHigh,
    bool Defaulted,
    string ActivityType,
    bool DurationActivity,
    bool PastBedtime
);

public record ActivityTimeRule(
    string ActivityName,
    TimeSpan? TimeLowerBound, // null means can happen anytime
    TimeSpan? TimeUpperBound  // null means can happen anytime
);

public record ActivityFilterResult(
    string CurrentActivity,
    ActivityRow? CurrentActivityDetails,
    List<ActivityRow> ActivitiesAfterTimeRuleFilter,
    List<ActivityRow> ActivitiesAfterBedtimeFilter,
    List<ActivityRow> FinalAvailableActivities,
    DateTime CurrentTime,
    Dictionary<string, object> Debug
);

public class DecisionEngine
{
    private readonly Random _rng;

    public DecisionEngine(int seed)
    {
        _rng = new Random(seed);
    }

    // STEP 1 ONLY: mood selection
    public MoodSelectionResult SelectMood(List<MoodRow> moods)
    {
        // b) Sort moods by CurrentMood (lowest at top, highest at bottom)
        var sorted = moods
            .OrderBy(m => m.CurrentMood)
            .ToList();

        // c) Interquartile Range analysis on CurrentMood
        var values = sorted.Select(m => m.CurrentMood).ToList();
        var q1 = Quantile(values, 0.25);
        var q3 = Quantile(values, 0.75);
        var iqr = q3 - q1;

        // d) Low outliers only (below lower fence)
        // Lower fence = Q1 - 1.5*IQR (standard)
        var lowerFence = q1 - 1.5 * iqr;

        var outliersLow = sorted
            .Where(m => m.CurrentMood < lowerFence)
            .ToList();

        // If outliers exist:
        if (outliersLow.Count > 0)
        {
            // Pick top outlier based on StartingRankMorning (lowest rank number wins)
            var bestRank = outliersLow.Min(m => m.StartingRankMorning);
            var topCandidates = outliersLow
                .Where(m => m.StartingRankMorning == bestRank)
                .ToList();

            // Tie => random
            var chosen = topCandidates[_rng.Next(topCandidates.Count)];

            var debug = new Dictionary<string, object>
            {
                ["Step1"] = new {
                    SortedMoods = sorted.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    Q1 = q1, Q3 = q3, IQR = iqr,
                    LowerFence = lowerFence,
                    OutliersLow = outliersLow.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    TopOutlierCandidates = topCandidates.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    SelectedMood = chosen.MoodName
                }
            };

            return new MoodSelectionResult(
                SelectedMood: chosen.MoodName,
                SortedMoodsLowToHigh: sorted,
                OutliersLow: outliersLow,
                TopOutlierCandidates: topCandidates,
                SelectionMode: "OUTLIER",
                Debug: debug
            );
        }

        // If no outliers:
        // Pick one of any moods from interquartile range at random.
        // In practice: choose from "non-outliers" (i.e., all moods since outliersLow is empty).
        // We'll define "IQR range" as [Q1, Q3] inclusive.
        var inIqr = sorted
            .Where(m => m.CurrentMood >= q1 && m.CurrentMood <= q3)
            .ToList();

        // Edge case: if IQR slice is empty (e.g., tiny list or repeated values), fallback to all moods
        var pool = inIqr.Count > 0 ? inIqr : sorted;

        var chosenIqr = pool[_rng.Next(pool.Count)];

        var debugNoOutlier = new Dictionary<string, object>
        {
            ["Step1"] = new {
                SortedMoods = sorted.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                Q1 = q1, Q3 = q3, IQR = iqr,
                LowerFence = lowerFence,
                OutliersLow = Array.Empty<object>(),
                IqrPool = pool.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                SelectedMood = chosenIqr.MoodName
            }
        };

        return new MoodSelectionResult(
            SelectedMood: chosenIqr.MoodName,
            SortedMoodsLowToHigh: sorted,
            OutliersLow: new List<MoodRow>(),
            TopOutlierCandidates: new List<MoodRow>(),
            SelectionMode: "IQR_RANDOM",
            Debug: debugNoOutlier
        );
    }

    // Quantile for an already-sorted list (ascending). Our values list is sorted via moods sort.
    private static double Quantile(List<double> sortedAsc, double q)
    {
        if (sortedAsc.Count == 0) return 0;

        var pos = (sortedAsc.Count - 1) * q;
        var baseIndex = (int)Math.Floor(pos);
        var rest = pos - baseIndex;

        if (baseIndex + 1 >= sortedAsc.Count) return sortedAsc[baseIndex];
        return sortedAsc[baseIndex] + rest * (sortedAsc[baseIndex + 1] - sortedAsc[baseIndex]);
    }

    /// <summary>
    /// Step 2 - Activity Filtering:
    /// Filters available activities based on time rules and bedtime restrictions.
    /// </summary>
    public ActivityFilterResult FilterActivities(
        string currentActivity,
        List<ActivityRow> allActivities,
        List<ActivityTimeRule> timeRules,
        DateTime currentTime)
    {
        // Default current activity if not set
        if (string.IsNullOrWhiteSpace(currentActivity))
        {
            currentActivity = "Moving In";
        }

        // Find current activity details
        var currentActivityDetails = allActivities.FirstOrDefault(a => 
            a.ActivityName.Equals(currentActivity, StringComparison.OrdinalIgnoreCase));

        // Step 1: Filter by time rules
        var activitiesAfterTimeRule = allActivities.Where(activity =>
        {
            var rule = timeRules.FirstOrDefault(r => 
                r.ActivityName.Equals(activity.ActivityName, StringComparison.OrdinalIgnoreCase));
            
            // If no rule, activity can happen anytime Bobani is awake
            if (rule == null || (rule.TimeLowerBound == null && rule.TimeUpperBound == null))
            {
                return true;
            }

            var currentTimeOfDay = currentTime.TimeOfDay;
            var lower = rule.TimeLowerBound ?? TimeSpan.Zero;
            var upper = rule.TimeUpperBound ?? TimeSpan.FromDays(1);

            // Handle time ranges that span midnight (e.g., 10 PM to 7 AM)
            if (lower > upper)
            {
                // Range spans midnight: current time must be >= lower OR <= upper
                return currentTimeOfDay >= lower || currentTimeOfDay <= upper;
            }
            else
            {
                // Normal range: current time must be between lower and upper
                return currentTimeOfDay >= lower && currentTimeOfDay <= upper;
            }
        }).ToList();

        // Step 2: Filter by past bedtime (if between 10 PM and 7 AM)
        var bedtimeStart = new TimeSpan(22, 0, 0); // 10:00 PM
        var bedtimeEnd = new TimeSpan(7, 0, 0);   // 7:00 AM
        var currentTimeOfDay = currentTime.TimeOfDay;
        
        bool isPastBedtime = currentTimeOfDay >= bedtimeStart || currentTimeOfDay <= bedtimeEnd;

        var activitiesAfterBedtime = activitiesAfterTimeRule.Where(activity =>
        {
            // If past bedtime and activity has Past Bedtime = "N", filter it out
            if (isPastBedtime && !activity.PastBedtime)
            {
                return false;
            }
            return true;
        }).ToList();

        var debug = new Dictionary<string, object>
        {
            ["Step2"] = new
            {
                CurrentActivity = currentActivity,
                CurrentTime = currentTime,
                IsPastBedtime = isPastBedtime,
                TotalActivities = allActivities.Count,
                AfterTimeRuleFilter = activitiesAfterTimeRule.Count,
                AfterBedtimeFilter = activitiesAfterBedtime.Count,
                FinalAvailable = activitiesAfterBedtime.Count
            }
        };

        return new ActivityFilterResult(
            CurrentActivity: currentActivity,
            CurrentActivityDetails: currentActivityDetails,
            ActivitiesAfterTimeRuleFilter: activitiesAfterTimeRule,
            ActivitiesAfterBedtimeFilter: activitiesAfterBedtime,
            FinalAvailableActivities: activitiesAfterBedtime,
            CurrentTime: currentTime,
            Debug: debug
        );
    }
}
