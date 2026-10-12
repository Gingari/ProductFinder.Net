using System;
using System.Globalization;

namespace ProductFinder.Models;

public static class MatchQuality
{
    public const double LowThreshold = .50;
    public const double HighThreshold = .80;
    public static double MaxReviewScore => Math.Max(0, HighThreshold - .01);
    public static double MaxConflictScore => Math.Max(0, LowThreshold - .01);
    public static string LegendText => "Красный <" + Percent(LowThreshold) + " · Жёлтый " + Percent(LowThreshold).TrimEnd('%')
        + "–" + Percent(MaxReviewScore) + " · Зелёный ≥" + Percent(HighThreshold);
    public static ConfidenceState Classify(double score) => Math.Round(Math.Clamp(score, 0, 1) * 100, MidpointRounding.ToEven) >= HighThreshold * 100 ? ConfidenceState.Confirmed
        : Math.Round(Math.Clamp(score, 0, 1) * 100, MidpointRounding.ToEven) >= LowThreshold * 100 ? ConfidenceState.Weak : ConfidenceState.Missing;
    public static string Percent(double score) => (Math.Max(score, 0) * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
    public static string Label(ConfidenceState state) => state switch
    {
        ConfidenceState.Confirmed => "Высокое совпадение",
        ConfidenceState.Weak => "Среднее совпадение",
        _ => "Низкое совпадение"
    };
}
