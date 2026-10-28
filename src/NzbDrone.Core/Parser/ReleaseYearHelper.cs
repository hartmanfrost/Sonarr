using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// Reads bracketed or parenthesised year tokens from a release title, e.g. "[2024, WEB-DL 1080p]",
    /// "(2026)" or "[1998-2000]", and compares them with the air years of the episodes a release was
    /// matched to. The window never uses series.Year: a later season legitimately airs years after
    /// the series premiere.
    /// </summary>
    public static class ReleaseYearHelper
    {
        // Trackers date a release by the show, the season, the disc or the dub, and a multi-year pack by its span
        // ("Rugrats S8 [1998-2000]" for episodes aired 2002-2004), so the window is two years wide on each side.
        public const int ToleranceYears = 2;

        private const string Year = @"(?:19|20)\d\d";
        private const string YearOrRange = Year + @"(?:\s*-\s*" + Year + ")?";

        // A bracket opens with a year, a range, or a comma/slash separated list of them: "[2024, ", "(2026)", "[1992, 1995, 2003]".
        private static readonly Regex YearGroupRegex = new Regex(
            @"[\[\(]\s*(?<list>" + YearOrRange + @"(?:\s*[,/]\s*" + YearOrRange + @")*)\s*(?=[,\]\)/ ]|$)",
            RegexOptions.Compiled);

        private static readonly Regex YearPartRegex = new Regex(@"(?<from>" + Year + @")(?:\s*-\s*(?<to>" + Year + "))?", RegexOptions.Compiled);

        public readonly struct YearRange
        {
            public YearRange(int from, int to)
            {
                From = Math.Min(from, to);
                To = Math.Max(from, to);
            }

            public int From { get; }
            public int To { get; }
        }

        public static List<YearRange> ExtractYearTokens(string releaseTitle)
        {
            var tokens = new List<YearRange>();

            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return tokens;
            }

            foreach (Match group in YearGroupRegex.Matches(releaseTitle))
            {
                foreach (Match part in YearPartRegex.Matches(group.Groups["list"].Value))
                {
                    var from = int.Parse(part.Groups["from"].Value);
                    var to = part.Groups["to"].Success ? int.Parse(part.Groups["to"].Value) : from;

                    tokens.Add(new YearRange(from, to));
                }
            }

            return tokens;
        }

        /// <summary>
        /// [first air year - 2, last air year + 2] over the episodes that have an air date and are not specials.
        /// Null when no episode qualifies.
        /// </summary>
        public static YearRange? GetAirYearWindow(IEnumerable<Episode> episodes)
        {
            if (episodes == null)
            {
                return null;
            }

            var years = episodes.Where(e => e.SeasonNumber > 0 && e.AirDateUtc.HasValue)
                                .Select(e => e.AirDateUtc.Value.Year)
                                .ToList();

            if (!years.Any())
            {
                return null;
            }

            return new YearRange(years.Min() - ToleranceYears, years.Max() + ToleranceYears);
        }

        /// <summary>
        /// True only when the title carries at least one year token and every token lies completely outside the window.
        /// </summary>
        public static bool IsOutsideWindow(string releaseTitle, YearRange? window)
        {
            if (!window.HasValue)
            {
                return false;
            }

            var tokens = ExtractYearTokens(releaseTitle);

            if (!tokens.Any())
            {
                return false;
            }

            return tokens.All(t => t.To < window.Value.From || t.From > window.Value.To);
        }

        public static bool IsOutsideWindow(string releaseTitle, IEnumerable<Episode> episodes)
        {
            return IsOutsideWindow(releaseTitle, GetAirYearWindow(episodes));
        }
    }
}
