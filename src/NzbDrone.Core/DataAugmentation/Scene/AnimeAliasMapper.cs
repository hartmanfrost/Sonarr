using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DataAugmentation.Scene
{
    public class AnimeListEntry
    {
        public string Type { get; set; }
        public int AniListId { get; set; }
        public int TvdbId { get; set; }
        public int? TvdbSeason { get; set; }
    }

    public class SceneMappingOverride
    {
        public string Title { get; set; }
        public int TvdbId { get; set; }
        public int? Season { get; set; }
        public string SearchTerm { get; set; }
        public string FilterRegex { get; set; }
    }

    /// <summary>
    /// Turns AniList names (romaji, English, synonyms) of the library's anime into scene mappings. Pure logic,
    /// no I/O, so it can be tested without a network.
    /// </summary>
    public static class AnimeAliasMapper
    {
        private static readonly HashSet<string> AnimeListTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TV", "ONA" };

        public static List<SceneMapping> Build(IReadOnlyCollection<Series> allSeries,
                                               IEnumerable<AnimeListEntry> animeList,
                                               IReadOnlyDictionary<int, List<string>> aniListTitles,
                                               IEnumerable<SceneMappingOverride> overrides)
        {
            var animeSeries = allSeries.Where(s => s.SeriesType == Tv.SeriesTypes.Anime).ToDictionary(s => s.TvdbId);
            var entriesByTvdb = animeList.Where(e => e.TvdbId > 0 && e.AniListId > 0 && e.Type != null && AnimeListTypes.Contains(e.Type) && animeSeries.ContainsKey(e.TvdbId))
                                         .GroupBy(e => e.TvdbId);

            var libraryTitles = allSeries.GroupBy(s => s.CleanTitle ?? s.Title.CleanSeriesTitle())
                                         .ToDictionary(g => g.Key, g => g.Select(s => s.TvdbId).ToHashSet());

            var mappings = new List<SceneMapping>();

            foreach (var group in entriesByTvdb)
            {
                var series = animeSeries[group.Key];
                var entries = group.GroupBy(e => e.AniListId).Select(g => g.First()).ToList();

                foreach (var entry in entries)
                {
                    int? season = null;

                    if (entry.TvdbSeason.HasValue && entry.TvdbSeason.Value > 1)
                    {
                        // Two AniList entries inside one TVDB season (two cours of "War of Underworld"): the episode
                        // offset inside that season is unknown, so a season override would map the wrong episodes.
                        if (entries.Count(e => e.TvdbSeason == entry.TvdbSeason) > 1)
                        {
                            continue;
                        }

                        season = entry.TvdbSeason.Value;
                    }

                    if (!aniListTitles.TryGetValue(entry.AniListId, out var titles))
                    {
                        continue;
                    }

                    foreach (var title in titles.Where(IsLatin).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        mappings.Add(new SceneMapping
                        {
                            Title = title,
                            ParseTerm = title.CleanSeriesTitle(),
                            SearchTerm = series.Title,
                            TvdbId = series.TvdbId,
                            SeasonNumber = season,
                            SceneOrigin = season.HasValue ? AnimeAliasProvider.SeasonOrigin : null,
                            Comment = $"anilist:{entry.AniListId}"
                        });
                    }
                }
            }

            mappings = ResolveCollisions(mappings, libraryTitles);

            ApplyOverrides(mappings, allSeries, overrides);

            return mappings;
        }

        public static bool IsLatin(string title)
        {
            return title.IsNotNullOrWhiteSpace() && title.All(c => !char.IsLetter(c) || c <= 'ɏ');
        }

        private static List<SceneMapping> ResolveCollisions(List<SceneMapping> mappings, Dictionary<string, HashSet<int>> libraryTitles)
        {
            var result = new List<SceneMapping>();

            foreach (var group in mappings.Where(m => m.ParseTerm.Length >= 3).GroupBy(m => m.ParseTerm))
            {
                var tvdbIds = group.Select(m => m.TvdbId).Distinct().ToList();

                // One name for two different shows: unusable.
                if (tvdbIds.Count > 1)
                {
                    continue;
                }

                // The name of another series in the library.
                if (libraryTitles.TryGetValue(group.Key, out var owners) && owners.Any(o => o != tvdbIds[0]))
                {
                    continue;
                }

                var first = group.First();

                // The same name on several seasons of one show: only the release's own season number can tell.
                if (group.Select(m => m.SeasonNumber).Distinct().Count() > 1)
                {
                    first.SeasonNumber = null;
                    first.SceneOrigin = null;
                }

                // A season-less alias equal to the library title adds nothing to FindByTitle.
                if (!first.SeasonNumber.HasValue && libraryTitles.TryGetValue(group.Key, out owners))
                {
                    continue;
                }

                result.Add(first);
            }

            return result;
        }

        private static void ApplyOverrides(List<SceneMapping> mappings, IEnumerable<Series> allSeries, IEnumerable<SceneMappingOverride> overrides)
        {
            if (overrides == null)
            {
                return;
            }

            var byTvdb = allSeries.GroupBy(s => s.TvdbId).ToDictionary(g => g.Key, g => g.First());

            foreach (var item in overrides)
            {
                if (item == null || item.Title.IsNullOrWhiteSpace() || item.TvdbId <= 0)
                {
                    continue;
                }

                var parseTerm = item.Title.CleanSeriesTitle();

                if (parseTerm.Length < 3)
                {
                    continue;
                }

                byTvdb.TryGetValue(item.TvdbId, out var series);

                // The override file wins over anything derived from AniList.
                mappings.RemoveAll(m => m.ParseTerm == parseTerm);

                mappings.Add(new SceneMapping
                {
                    Title = item.Title,
                    ParseTerm = parseTerm,
                    SearchTerm = item.SearchTerm.IsNotNullOrWhiteSpace() ? item.SearchTerm : series?.Title ?? item.Title,
                    TvdbId = item.TvdbId,
                    SeasonNumber = item.Season,
                    SceneOrigin = item.Season.HasValue ? AnimeAliasProvider.SeasonOrigin : null,
                    FilterRegex = item.FilterRegex,
                    Comment = "override"
                });
            }
        }
    }
}
