using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// Candidate series names taken from one " / " separated part of a tracker-style release title.
    /// <see cref="AsIs"/> variants are tried first; <see cref="WithoutSeasonToken"/> variants only when
    /// nothing matched as-is.
    /// </summary>
    public class ReleaseSegment
    {
        public List<string> AsIs { get; set; } = new List<string>();
        public List<string> WithoutSeasonToken { get; set; } = new List<string>();
    }

    public static class ReleaseSegments
    {
        // Releases that are not the main TV series (OVA, specials, mini episodes, movies) must not be matched by name alone.
        private static readonly Regex ExcludedRegex = new Regex(@"(?<![\p{L}\p{N}])(?:OVA|OAD|ODA|Specials?|Mini|Movie|Shorts?)(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BracketBlockRegex = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex ParenBlockRegex = new Regex(@"\([^)]*\)", RegexOptions.Compiled);

        private static readonly Regex TrailingSeasonTokenRegex = new Regex(
            @"\s+(?:Season\s*\d+|\d+(?:st|nd|rd|th)\s+Season|Part\s*\d+|\d{1,2})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsExcluded(string releaseTitle)
        {
            return releaseTitle.IsNotNullOrWhiteSpace() && ExcludedRegex.IsMatch(releaseTitle);
        }

        /// <summary>
        /// Splits the raw release title on " / ", drops [..] blocks and (..) tails, and returns the candidate names
        /// for each part. Empty when the title is excluded (OVA, special, mini, movie) or has nothing usable.
        /// </summary>
        public static List<ReleaseSegment> Split(string releaseTitle)
        {
            var result = new List<ReleaseSegment>();

            if (releaseTitle.IsNullOrWhiteSpace() || IsExcluded(releaseTitle))
            {
                return result;
            }

            var withoutBrackets = BracketBlockRegex.Replace(releaseTitle, " ");

            foreach (var part in withoutBrackets.Split(new[] { " / " }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = new ReleaseSegment();

                var openParen = part.IndexOf('(');
                var cut = Normalize(openParen >= 0 ? part.Substring(0, openParen) : part);
                var noParens = Normalize(ParenBlockRegex.Replace(part, " "));

                AddDistinct(segment.AsIs, cut);
                AddDistinct(segment.AsIs, noParens);

                foreach (var candidate in segment.AsIs.ToList())
                {
                    var stripped = TrailingSeasonTokenRegex.Replace(candidate, string.Empty).Trim();

                    if (stripped != candidate)
                    {
                        AddDistinct(segment.WithoutSeasonToken, stripped);
                    }
                }

                if (segment.AsIs.Any() || segment.WithoutSeasonToken.Any())
                {
                    result.Add(segment);
                }
            }

            return result;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '_', '.', ',', ':', ';', '&', '+', '|');
        }

        private static void AddDistinct(List<string> list, string value)
        {
            // Names that clean down to nothing or to a couple of characters would match unrelated shows.
            if (value.IsNotNullOrWhiteSpace() && value.CleanSeriesTitle().Length >= 3 && !list.Contains(value))
            {
                list.Add(value);
            }
        }
    }
}
