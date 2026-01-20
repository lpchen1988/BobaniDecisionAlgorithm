namespace Bobani.Engine;

public record MoodRow(
    string MoodName,
    double CurrentMood,
    int StartingRankMorning
);

public record MoodSelectionResult(
    string SelectedMood,
    string SelectionMode, // "OUTLIER" or "IQR_RANDOM"

    // Core stats
    double Q1,
    double Q3,
    double IQR,
    double LowerFence,

    // Groups for presentable output
    List<MoodRow> SortedMoodsLowToHigh,
    List<MoodRow> OutliersLow,
    List<MoodRow> IqrPool,
    List<MoodRow> TopOutlierCandidates,

    // Extra debug (optional)
    Dictionary<string, object> Debug
);

public class DecisionEngine
{
    private readonly Random _rng;

    public DecisionEngine(int seed)
    {
        _rng = new Random(seed);
    }

    // STEP 1 ONLY: Select a mood using IQR low-outlier logic
    public MoodSelectionResult SelectMood(List<MoodRow> moods)
    {
        // b) Sort by CurrentMood (lowest at top, highest at bottom)
        var sorted = moods.OrderBy(m => m.CurrentMood).ToList();

        // c) Interquartile Range (IQR) on CurrentMood
        var values = sorted.Select(m => m.CurrentMood).ToList(); // already sorted asc by mood sort
        var q1 = Quantile(values, 0.25);
        var q3 = Quantile(values, 0.75);
        var iqr = q3 - q1;

        // d) Low outliers only
        var lowerFence = q1 - 1.5 * iqr;

        var outliersLow = sorted.Where(m => m.CurrentMood < lowerFence).ToList();

        if (outliersLow.Count > 0)
        {
            // Pick top outlier based on Starting Rank (to Boboni) Morning
            // Assumption: lower rank number = higher priority (rank 1 is best)
            var bestRank = outliersLow.Min(x => x.StartingRankMorning);

            var topCandidates = outliersLow
                .Where(x => x.StartingRankMorning == bestRank)
                .ToList();

            var chosen = topCandidates[_rng.Next(topCandidates.Count)];

            var debug = new Dictionary<string, object>
            {
                ["Step1"] = new
                {
                    SortedMoods = sorted.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    Q1 = q1,
                    Q3 = q3,
                    IQR = iqr,
                    LowerFence = lowerFence,
                    OutliersLow = outliersLow.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    TopOutlier = topCandidates.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                    SelectedMood = chosen.MoodName
                }
            };

                        return new MoodSelectionResult(
                SelectedMood: chosen.MoodName,
                SelectionMode: "OUTLIER",
                Q1: q1,
                Q3: q3,
                IQR: iqr,
                LowerFence: lowerFence,
                SortedMoodsLowToHigh: sorted,
                OutliersLow: outliersLow,
                IqrPool: new List<MoodRow>(),
                TopOutlierCandidates: topCandidates,
                Debug: debug
            );
        }

        // No outliers: pick one mood from within the IQR range [Q1, Q3] at random
        var inIqr = sorted.Where(m => m.CurrentMood >= q1 && m.CurrentMood <= q3).ToList();
        var pool = inIqr.Count > 0 ? inIqr : sorted;

        var chosenIqr = pool[_rng.Next(pool.Count)];

        var debugNoOutlier = new Dictionary<string, object>
        {
            ["Step1"] = new
            {
                SortedMoods = sorted.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                Q1 = q1,
                Q3 = q3,
                IQR = iqr,
                LowerFence = lowerFence,
                OutliersLow = Array.Empty<object>(),
                IqrPool = pool.Select(x => new { x.MoodName, x.CurrentMood, x.StartingRankMorning }),
                SelectedMood = chosenIqr.MoodName
            }
        };

                return new MoodSelectionResult(
            SelectedMood: chosenIqr.MoodName,
            SelectionMode: "IQR_RANDOM",
            Q1: q1,
            Q3: q3,
            IQR: iqr,
            LowerFence: lowerFence,
            SortedMoodsLowToHigh: sorted,
            OutliersLow: new List<MoodRow>(),
            IqrPool: pool,
            TopOutlierCandidates: new List<MoodRow>(),
            Debug: debugNoOutlier
        );
    }

    // Quantile for sorted (ascending) list
    private static double Quantile(List<double> sortedAsc, double q)
    {
        if (sortedAsc.Count == 0) return 0;

        var pos = (sortedAsc.Count - 1) * q;
        var baseIndex = (int)Math.Floor(pos);
        var rest = pos - baseIndex;

        if (baseIndex + 1 >= sortedAsc.Count) return sortedAsc[baseIndex];
        return sortedAsc[baseIndex] + rest * (sortedAsc[baseIndex + 1] - sortedAsc[baseIndex]);
    }
}
