using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Parser
{
    public interface IParsingService
    {
        Series GetSeries(string title);
        RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, int tvdbId, int tvRageId, string imdbId, SearchCriteriaBase searchCriteria = null);
        RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, Series series);
        RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, int seriesId, IEnumerable<int> episodeIds);
        List<Episode> GetEpisodes(ParsedEpisodeInfo parsedEpisodeInfo, Series series, bool sceneSource, SearchCriteriaBase searchCriteria = null);
        ParsedEpisodeInfo ParseSpecialEpisodeTitle(ParsedEpisodeInfo parsedEpisodeInfo, string releaseTitle, int tvdbId, int tvRageId, string imdbId, SearchCriteriaBase searchCriteria = null);
        ParsedEpisodeInfo ParseSpecialEpisodeTitle(ParsedEpisodeInfo parsedEpisodeInfo, string releaseTitle, Series series);
    }

    public class ParsingService : IParsingService
    {
        private readonly IEpisodeService _episodeService;
        private readonly ISeriesService _seriesService;
        private readonly ISceneMappingService _sceneMappingService;
        private readonly Logger _logger;

        public ParsingService(IEpisodeService episodeService,
                              ISeriesService seriesService,
                              ISceneMappingService sceneMappingService,
                              Logger logger)
        {
            _episodeService = episodeService;
            _seriesService = seriesService;
            _sceneMappingService = sceneMappingService;
            _logger = logger;
        }

        public Series GetSeries(string title)
        {
            var parsedEpisodeInfo = Parser.ParseTitle(title);

            if (parsedEpisodeInfo == null)
            {
                return _seriesService.FindByTitle(title);
            }

            var tvdbId = _sceneMappingService.FindTvdbId(parsedEpisodeInfo.SeriesTitle, parsedEpisodeInfo.ReleaseTitle, parsedEpisodeInfo.SeasonNumber);

            if (tvdbId.HasValue)
            {
                return _seriesService.FindByTvdbId(tvdbId.Value);
            }

            var series = _seriesService.FindByTitle(parsedEpisodeInfo.SeriesTitle);

            if (series == null && parsedEpisodeInfo.SeriesTitleInfo.AllTitles != null)
            {
                series = GetSeriesByAllTitles(parsedEpisodeInfo);
            }

            if (series == null)
            {
                series = _seriesService.FindByTitle(parsedEpisodeInfo.SeriesTitleInfo.TitleWithoutYear,
                                                    parsedEpisodeInfo.SeriesTitleInfo.Year);
            }

            return series;
        }

        private Series GetSeriesByAllTitles(ParsedEpisodeInfo parsedEpisodeInfo)
        {
            Series foundSeries = null;
            int? foundTvdbId = null;

            // Match each title individually, they must all resolve to the same tvdbid
            foreach (var title in parsedEpisodeInfo.SeriesTitleInfo.AllTitles)
            {
                var series = _seriesService.FindByTitle(title);
                var tvdbId = series?.TvdbId;

                if (series == null)
                {
                    tvdbId = _sceneMappingService.FindTvdbId(title, parsedEpisodeInfo.ReleaseTitle, parsedEpisodeInfo.SeasonNumber);
                }

                if (!tvdbId.HasValue)
                {
                    _logger.Trace("Title {0} not matching any series.", title);
                    continue;
                }

                if (foundTvdbId.HasValue && tvdbId != foundTvdbId)
                {
                    _logger.Trace("Title {0} both matches tvdbid {1} and {2}, no series selected.", parsedEpisodeInfo.SeriesTitle, foundTvdbId, tvdbId);
                    return null;
                }

                if (foundSeries == null)
                {
                    foundSeries = series;
                }

                foundTvdbId = tvdbId;
            }

            if (foundSeries == null && foundTvdbId.HasValue)
            {
                foundSeries = _seriesService.FindByTvdbId(foundTvdbId.Value);
            }

            return foundSeries;
        }

        public RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, int tvdbId, int tvRageId, string imdbId, SearchCriteriaBase searchCriteria = null)
        {
            return Map(parsedEpisodeInfo, tvdbId, tvRageId, imdbId, null, searchCriteria);
        }

        public RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, Series series)
        {
            return Map(parsedEpisodeInfo, 0, 0, null, series, null);
        }

        public RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, int seriesId, IEnumerable<int> episodeIds)
        {
            return new RemoteEpisode
                   {
                       ParsedEpisodeInfo = parsedEpisodeInfo,
                       Series = _seriesService.GetSeries(seriesId),
                       Episodes = _episodeService.GetEpisodes(episodeIds)
                   };
        }

        private RemoteEpisode Map(ParsedEpisodeInfo parsedEpisodeInfo, int tvdbId, int tvRageId, string imdbId, Series series, SearchCriteriaBase searchCriteria)
        {
            var sceneMapping = _sceneMappingService.FindSceneMapping(parsedEpisodeInfo.SeriesTitle, parsedEpisodeInfo.ReleaseTitle, parsedEpisodeInfo.SeasonNumber);

            var remoteEpisode = new RemoteEpisode
            {
                ParsedEpisodeInfo = parsedEpisodeInfo,
                SceneMapping = sceneMapping,
                MappedSeasonNumber = parsedEpisodeInfo.SeasonNumber
            };

            // For now we just detect tvdb vs scene, but we can do multiple 'origins' in the future.
            var sceneSource = ApplySceneMapping(remoteEpisode, sceneMapping, parsedEpisodeInfo);

            if (series == null)
            {
                var seriesMatch = FindSeries(parsedEpisodeInfo, tvdbId, tvRageId, imdbId, sceneMapping, searchCriteria);

                if (seriesMatch == null && sceneMapping == null)
                {
                    seriesMatch = FindSeriesByReleaseSegments(parsedEpisodeInfo, out sceneMapping);

                    if (seriesMatch != null)
                    {
                        sceneSource = ApplySceneMapping(remoteEpisode, sceneMapping, parsedEpisodeInfo);
                    }
                }

                if (seriesMatch != null)
                {
                    series = seriesMatch.Series;
                    remoteEpisode.SeriesMatchType = seriesMatch.MatchType;
                }
            }

            if (series != null)
            {
                remoteEpisode.Series = series;

                if (ValidateParsedEpisodeInfo.ValidateForSeriesType(parsedEpisodeInfo, series))
                {
                    remoteEpisode.Episodes = GetEpisodes(parsedEpisodeInfo, series, remoteEpisode.MappedSeasonNumber, sceneSource, searchCriteria);
                }
            }

            remoteEpisode.Languages = parsedEpisodeInfo.Languages;

            if (remoteEpisode.Episodes == null)
            {
                remoteEpisode.Episodes = new List<Episode>();
            }

            if (searchCriteria != null)
            {
                var requestedEpisodes = searchCriteria.Episodes.ToDictionaryIgnoreDuplicates(v => v.Id);
                remoteEpisode.EpisodeRequested = remoteEpisode.Episodes.Any(v => requestedEpisodes.ContainsKey(v.Id));
            }

            return remoteEpisode;
        }

        private bool ApplySceneMapping(RemoteEpisode remoteEpisode, SceneMapping sceneMapping, ParsedEpisodeInfo parsedEpisodeInfo)
        {
            remoteEpisode.SceneMapping = sceneMapping;
            remoteEpisode.MappedSeasonNumber = parsedEpisodeInfo.SeasonNumber;

            var sceneSource = true;

            if (sceneMapping != null)
            {
                if (AnimeAliasProvider.IsSeasonOrigin(sceneMapping.SceneOrigin) && sceneMapping.SeasonNumber.HasValue && sceneMapping.SeasonNumber.Value >= 0)
                {
                    // The alias names one specific TVDB season; the tracker's own season number is meaningless for it.
                    remoteEpisode.MappedSeasonNumber = sceneMapping.SeasonNumber.Value;
                    sceneSource = false;
                }
                else if (sceneMapping.SeasonNumber.HasValue && sceneMapping.SeasonNumber.Value >= 0 &&
                    sceneMapping.SceneSeasonNumber <= parsedEpisodeInfo.SeasonNumber)
                {
                    remoteEpisode.MappedSeasonNumber += sceneMapping.SeasonNumber.Value - sceneMapping.SceneSeasonNumber.Value;
                }

                if (sceneMapping.SceneOrigin == "tvdb")
                {
                    sceneSource = false;
                }
                else if (sceneMapping.Type == "XemService" &&
                         sceneMapping.SceneSeasonNumber.NonNegative().HasValue &&
                         parsedEpisodeInfo.SeasonNumber == 1 &&
                         sceneMapping.SceneSeasonNumber != parsedEpisodeInfo.SeasonNumber)
                {
                    remoteEpisode.MappedSeasonNumber = sceneMapping.SceneSeasonNumber.Value;
                }
            }

            return sceneSource;
        }

        /// <summary>
        /// Last resort for tracker-style titles ("Romaji (S2) / English Season 2 [TV] ..."): try each " / " part as a
        /// whole name, exact CleanSeriesTitle equality only, against the scene mappings and the library. Accepted only
        /// when every hit is the same series and the release year fits the series' air years.
        /// </summary>
        private FindSeriesResult FindSeriesByReleaseSegments(ParsedEpisodeInfo parsedEpisodeInfo, out SceneMapping sceneMapping)
        {
            sceneMapping = null;

            var segments = ReleaseSegments.Split(parsedEpisodeInfo.ReleaseTitle);

            if (!segments.Any())
            {
                return null;
            }

            var hitMappings = new List<SceneMapping>();
            var hitTvdbIds = new HashSet<int>();

            foreach (var segment in segments)
            {
                var found = LookupSegmentTerms(segment.AsIs, false, parsedEpisodeInfo, hitMappings, hitTvdbIds);

                if (!found)
                {
                    LookupSegmentTerms(segment.WithoutSeasonToken, true, parsedEpisodeInfo, hitMappings, hitTvdbIds);
                }
            }

            if (hitTvdbIds.Count != 1)
            {
                if (hitTvdbIds.Count > 1)
                {
                    _logger.Debug("Release segments of '{0}' match several series, leaving it unknown", parsedEpisodeInfo.ReleaseTitle);
                }

                return null;
            }

            var matchedTvdbId = hitTvdbIds.First();
            var series = _seriesService.FindByTvdbId(matchedTvdbId);

            if (series == null)
            {
                return null;
            }

            sceneMapping = ChooseSceneMapping(hitMappings.Where(m => m.TvdbId == matchedTvdbId).ToList());

            var undoNumbering = RepairNumberingFromSeasonToken(parsedEpisodeInfo);

            // "(13 из 13) Complete" reads as absolute episode 13. Without an explicit season token a name match
            // is not enough to trust a bare episode number, so such a release stays unknown.
            if (parsedEpisodeInfo.IsAbsoluteNumbering)
            {
                sceneMapping = null;
                return null;
            }

            // Dry run on a scratch object so a year mismatch leaves the caller's RemoteEpisode untouched.
            var scratch = new RemoteEpisode { ParsedEpisodeInfo = parsedEpisodeInfo };
            var sceneSource = ApplySceneMapping(scratch, sceneMapping, parsedEpisodeInfo);

            var episodes = ValidateParsedEpisodeInfo.ValidateForSeriesType(parsedEpisodeInfo, series)
                ? GetEpisodes(parsedEpisodeInfo, series, scratch.MappedSeasonNumber, sceneSource, null)
                : new List<Episode>();

            var window = ReleaseYearHelper.GetAirYearWindow(episodes);

            if (window == null)
            {
                window = ReleaseYearHelper.GetAirYearWindow(_episodeService.GetEpisodeBySeries(series.Id));
            }

            if (ReleaseYearHelper.IsOutsideWindow(parsedEpisodeInfo.ReleaseTitle, window))
            {
                _logger.Debug("Release '{0}' matched {1} by name but its year is outside the series' air years, leaving it unknown", parsedEpisodeInfo.ReleaseTitle, series);
                sceneMapping = null;
                undoNumbering?.Invoke();
                return null;
            }

            _logger.Debug("Matched '{0}' to {1} by release segment", parsedEpisodeInfo.ReleaseTitle, series);

            return new FindSeriesResult(series, SeriesMatchType.Alias);
        }

        private static readonly Regex SeasonTokenRegex = new Regex(@"\(S(?<season>\d{1,2})(?:E(?<from>\d{1,4})(?:-E?(?<to>\d{1,4}))?)?[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// "Slime Taoshite 300-nen ... (S2) (2025)" parses as absolute episode 300 because the parser reads the
        /// digits of the name. When the name is matched by segment and the title carries an explicit "(S2)" or
        /// "(S01E01-04)" token, that token is the numbering. Returns an action that restores the parse, or null
        /// when nothing was changed.
        /// </summary>
        private static Action RepairNumberingFromSeasonToken(ParsedEpisodeInfo info)
        {
            if (!info.IsAbsoluteNumbering || info.SeasonNumber != 0 || info.ReleaseTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var token = SeasonTokenRegex.Match(info.ReleaseTitle);

            if (!token.Success)
            {
                return null;
            }

            var name = info.ReleaseTitle.Substring(0, token.Index);

            if (!info.AbsoluteEpisodeNumbers.All(n => Regex.IsMatch(name, $@"(?<!\d){n}(?!\d)")))
            {
                return null;
            }

            var season = info.SeasonNumber;
            var episodes = info.EpisodeNumbers;
            var absolute = info.AbsoluteEpisodeNumbers;
            var fullSeason = info.FullSeason;

            info.SeasonNumber = int.Parse(token.Groups["season"].Value);
            info.AbsoluteEpisodeNumbers = Array.Empty<int>();

            if (token.Groups["from"].Success)
            {
                var from = int.Parse(token.Groups["from"].Value);
                var to = token.Groups["to"].Success ? int.Parse(token.Groups["to"].Value) : from;

                info.EpisodeNumbers = to >= from && to - from < 500 ? Enumerable.Range(from, to - from + 1).ToArray() : new[] { from };
                info.FullSeason = false;
            }
            else
            {
                info.EpisodeNumbers = Array.Empty<int>();
                info.FullSeason = true;
            }

            return () =>
            {
                info.SeasonNumber = season;
                info.EpisodeNumbers = episodes;
                info.AbsoluteEpisodeNumbers = absolute;
                info.FullSeason = fullSeason;
            };
        }

        private bool LookupSegmentTerms(List<string> terms, bool seasonTokenStripped, ParsedEpisodeInfo parsedEpisodeInfo, List<SceneMapping> hitMappings, HashSet<int> hitTvdbIds)
        {
            var found = false;

            foreach (var term in terms)
            {
                var mappings = _sceneMappingService.FindMappingsByTerm(term, parsedEpisodeInfo.ReleaseTitle, parsedEpisodeInfo.SeasonNumber);

                if (mappings != null)
                {
                    // A name that only matched after its season token was cut must not land on an alias that
                    // itself names a later season ("... 3rd Season" cut to "..." must not hit a season 3 alias).
                    foreach (var mapping in mappings.Where(m => !seasonTokenStripped || (m.SeasonNumber ?? -1) <= 1))
                    {
                        hitMappings.Add(mapping);
                        hitTvdbIds.Add(mapping.TvdbId);
                        found = true;
                    }
                }

                var libraryMatch = _seriesService.FindByTitle(term);

                if (libraryMatch != null)
                {
                    hitTvdbIds.Add(libraryMatch.TvdbId);
                    found = true;
                }
            }

            return found;
        }

        private static SceneMapping ChooseSceneMapping(List<SceneMapping> mappings)
        {
            if (!mappings.Any())
            {
                return null;
            }

            var seasons = mappings.Select(m => m.SeasonNumber).Distinct().ToList();

            if (seasons.Count == 1)
            {
                return mappings.First();
            }

            // The parts disagree on the season: keep the season-agnostic one if there is one, otherwise stay neutral.
            return mappings.FirstOrDefault(m => !m.SeasonNumber.HasValue && !m.SceneSeasonNumber.HasValue);
        }

        public List<Episode> GetEpisodes(ParsedEpisodeInfo parsedEpisodeInfo, Series series, bool sceneSource, SearchCriteriaBase searchCriteria = null)
        {
            if (sceneSource)
            {
                var remoteEpisode = Map(parsedEpisodeInfo, 0, 0, null, series, searchCriteria);

                return remoteEpisode.Episodes;
            }

            return GetEpisodes(parsedEpisodeInfo, series, parsedEpisodeInfo.SeasonNumber, sceneSource, searchCriteria);
        }

        private List<Episode> GetEpisodes(ParsedEpisodeInfo parsedEpisodeInfo, Series series, int mappedSeasonNumber, bool sceneSource, SearchCriteriaBase searchCriteria)
        {
            if (parsedEpisodeInfo.FullSeason)
            {
                if (series.UseSceneNumbering && sceneSource)
                {
                    var episodes = _episodeService.GetEpisodesBySceneSeason(series.Id, mappedSeasonNumber);

                    // If episodes were found by the scene season number return them, otherwise fallback to look-up by season number
                    if (episodes.Any())
                    {
                        return episodes;
                    }
                }

                return _episodeService.GetEpisodesBySeason(series.Id, mappedSeasonNumber);
            }

            if (parsedEpisodeInfo.IsDaily)
            {
                var episodeInfo = GetDailyEpisode(series, parsedEpisodeInfo.AirDate, parsedEpisodeInfo.DailyPart, searchCriteria);

                if (episodeInfo != null)
                {
                    return new List<Episode> { episodeInfo };
                }

                return new List<Episode>();
            }

            if (parsedEpisodeInfo.IsAbsoluteNumbering)
            {
                return GetAnimeEpisodes(series, parsedEpisodeInfo, mappedSeasonNumber, sceneSource, searchCriteria);
            }

            if (parsedEpisodeInfo.IsPossibleSceneSeasonSpecial)
            {
                var parsedSpecialEpisodeInfo = ParseSpecialEpisodeTitle(parsedEpisodeInfo, parsedEpisodeInfo.ReleaseTitle, series);

                if (parsedSpecialEpisodeInfo != null)
                {
                    // Use the season number and disable scene source since the season/episode numbers that were returned are not scene numbers
                    return GetStandardEpisodes(series, parsedSpecialEpisodeInfo, parsedSpecialEpisodeInfo.SeasonNumber, false, searchCriteria);
                }
            }

            if (parsedEpisodeInfo.Special && mappedSeasonNumber != 0)
            {
                return new List<Episode>();
            }

            return GetStandardEpisodes(series, parsedEpisodeInfo, mappedSeasonNumber, sceneSource, searchCriteria);
        }

        public ParsedEpisodeInfo ParseSpecialEpisodeTitle(ParsedEpisodeInfo parsedEpisodeInfo, string releaseTitle, int tvdbId, int tvRageId, string imdbId, SearchCriteriaBase searchCriteria = null)
        {
            if (searchCriteria != null)
            {
                if (tvdbId != 0 && tvdbId == searchCriteria.Series.TvdbId)
                {
                    return ParseSpecialEpisodeTitle(parsedEpisodeInfo, releaseTitle, searchCriteria.Series);
                }

                if (tvRageId != 0 && tvRageId == searchCriteria.Series.TvRageId)
                {
                    return ParseSpecialEpisodeTitle(parsedEpisodeInfo, releaseTitle, searchCriteria.Series);
                }

                if (imdbId.IsNotNullOrWhiteSpace() && imdbId.Equals(searchCriteria.Series.ImdbId, StringComparison.Ordinal))
                {
                    return ParseSpecialEpisodeTitle(parsedEpisodeInfo, releaseTitle, searchCriteria.Series);
                }
            }

            var series = GetSeries(releaseTitle);

            if (series == null)
            {
                series = _seriesService.FindByTitleInexact(releaseTitle);
            }

            if (series == null && tvdbId > 0)
            {
                series = _seriesService.FindByTvdbId(tvdbId);
            }

            if (series == null && tvRageId > 0)
            {
                series = _seriesService.FindByTvRageId(tvRageId);
            }

            if (series == null && imdbId.IsNotNullOrWhiteSpace())
            {
                series = _seriesService.FindByImdbId(imdbId);
            }

            if (series == null)
            {
                _logger.Debug("No matching series {0}", releaseTitle);
                return null;
            }

            return ParseSpecialEpisodeTitle(parsedEpisodeInfo, releaseTitle, series);
        }

        public ParsedEpisodeInfo ParseSpecialEpisodeTitle(ParsedEpisodeInfo parsedEpisodeInfo, string releaseTitle, Series series)
        {
            // SxxE00 episodes are sometimes mapped via TheXEM, don't use episode title parsing in that case.
            if (parsedEpisodeInfo != null && parsedEpisodeInfo.IsPossibleSceneSeasonSpecial && series.UseSceneNumbering)
            {
                if (_episodeService.FindEpisodesBySceneNumbering(series.Id, parsedEpisodeInfo.SeasonNumber, 0).Any())
                {
                    return parsedEpisodeInfo;
                }
            }

            // find special episode in series season 0
            var episode = _episodeService.FindEpisodeByTitle(series.Id, 0, releaseTitle);

            if (episode != null)
            {
                // create parsed info from tv episode
                var info = new ParsedEpisodeInfo
                {
                    ReleaseTitle = releaseTitle,
                    SeriesTitle = series.Title,
                    SeriesTitleInfo = new SeriesTitleInfo
                        {
                            Title = series.Title
                        },
                    SeasonNumber = episode.SeasonNumber,
                    EpisodeNumbers = new int[1] { episode.EpisodeNumber },
                    FullSeason = false,
                    Quality = QualityParser.ParseQuality(releaseTitle),
                    ReleaseGroup = Parser.ParseReleaseGroup(releaseTitle),
                    Languages = LanguageParser.ParseLanguages(releaseTitle),
                    Special = true
                };

                _logger.Debug("Found special episode {0} for title '{1}'", info, releaseTitle);
                return info;
            }

            return null;
        }

        private FindSeriesResult FindSeries(ParsedEpisodeInfo parsedEpisodeInfo, int tvdbId, int tvRageId, string imdbId, SceneMapping sceneMapping, SearchCriteriaBase searchCriteria)
        {
            Series series = null;

            if (sceneMapping != null)
            {
                if (searchCriteria != null && searchCriteria.Series.TvdbId == sceneMapping.TvdbId)
                {
                    return new FindSeriesResult(searchCriteria.Series, SeriesMatchType.Alias);
                }

                series = _seriesService.FindByTvdbId(sceneMapping.TvdbId);

                if (series == null)
                {
                    _logger.Debug("No matching series {0}", parsedEpisodeInfo.SeriesTitle);
                    return null;
                }

                return new FindSeriesResult(series, SeriesMatchType.Alias);
            }

            if (searchCriteria != null)
            {
                if (searchCriteria.Series.CleanTitle == parsedEpisodeInfo.SeriesTitle.CleanSeriesTitle())
                {
                    return new FindSeriesResult(searchCriteria.Series, SeriesMatchType.Title);
                }

                if (tvdbId > 0 && tvdbId == searchCriteria.Series.TvdbId)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by TVDB ID {0}, an alias may be needed for: {1}", tvdbId, parsedEpisodeInfo.SeriesTitle)
                           .Property("TvdbId", tvdbId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("TvdbIdMatch", tvdbId.ToString(), parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    return new FindSeriesResult(searchCriteria.Series, SeriesMatchType.Id);
                }

                if (tvRageId > 0 && tvRageId == searchCriteria.Series.TvRageId && tvdbId <= 0)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by TVRage ID {0}, an alias may be needed for: {1}", tvRageId, parsedEpisodeInfo.SeriesTitle)
                           .Property("TvRageId", tvRageId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("TvRageIdMatch", tvRageId.ToString(), parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    return new FindSeriesResult(searchCriteria.Series, SeriesMatchType.Id);
                }

                if (imdbId.IsNotNullOrWhiteSpace() && imdbId.Equals(searchCriteria.Series.ImdbId, StringComparison.Ordinal) && tvdbId <= 0)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by IMDb ID {0}, an alias may be needed for: {1}", imdbId, parsedEpisodeInfo.SeriesTitle)
                           .Property("ImdbId", imdbId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("ImdbIdMatch", imdbId, parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    return new FindSeriesResult(searchCriteria.Series, SeriesMatchType.Id);
                }
            }

            var matchType = SeriesMatchType.Unknown;
            series = _seriesService.FindByTitle(parsedEpisodeInfo.SeriesTitle);

            if (series != null)
            {
                matchType = SeriesMatchType.Title;
            }

            if (series == null && parsedEpisodeInfo.SeriesTitleInfo.AllTitles != null)
            {
                series = GetSeriesByAllTitles(parsedEpisodeInfo);
                matchType = SeriesMatchType.Title;
            }

            if (series == null && parsedEpisodeInfo.SeriesTitleInfo.Year > 0)
            {
                series = _seriesService.FindByTitle(parsedEpisodeInfo.SeriesTitleInfo.TitleWithoutYear, parsedEpisodeInfo.SeriesTitleInfo.Year);
                matchType = SeriesMatchType.Title;
            }

            if (series == null && tvdbId > 0)
            {
                series = _seriesService.FindByTvdbId(tvdbId);

                if (series != null)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by TVDB ID {0}, an alias may be needed for: {1}", tvdbId, parsedEpisodeInfo.SeriesTitle)
                           .Property("TvdbId", tvdbId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("TvdbIdMatch", tvdbId.ToString(), parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    matchType = SeriesMatchType.Id;
                }
            }

            if (series == null && tvRageId > 0 && tvdbId <= 0)
            {
                series = _seriesService.FindByTvRageId(tvRageId);

                if (series != null)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by TVRage ID {0}, an alias may be needed for: {1}", tvRageId, parsedEpisodeInfo.SeriesTitle)
                           .Property("TvRageId", tvRageId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("TvRageIdMatch", tvRageId.ToString(), parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    matchType = SeriesMatchType.Id;
                }
            }

            if (series == null && imdbId.IsNotNullOrWhiteSpace() && tvdbId <= 0)
            {
                series = _seriesService.FindByImdbId(imdbId);

                if (series != null)
                {
                    _logger.ForDebugEvent()
                           .Message("Found matching series by IMDb ID {0}, an alias may be needed for: {1}", imdbId, parsedEpisodeInfo.SeriesTitle)
                           .Property("ImdbId", imdbId)
                           .Property("ParsedEpisodeInfo", parsedEpisodeInfo)
                           .WriteSentryWarn("ImdbIdMatch", imdbId, parsedEpisodeInfo.SeriesTitle)
                           .Log();

                    matchType = SeriesMatchType.Id;
                }
            }

            if (series == null)
            {
                _logger.Debug("No matching series {0}", parsedEpisodeInfo.SeriesTitle);
                return null;
            }

            return new FindSeriesResult(series, matchType);
        }

        private Episode GetDailyEpisode(Series series, string airDate, int? part, SearchCriteriaBase searchCriteria)
        {
            Episode episodeInfo = null;

            if (searchCriteria != null)
            {
                episodeInfo = searchCriteria.Episodes.SingleOrDefault(
                    e => e.AirDate == airDate);
            }

            if (episodeInfo == null)
            {
                episodeInfo = _episodeService.FindEpisode(series.Id, airDate, part);
            }

            return episodeInfo;
        }

        private List<Episode> GetAnimeEpisodes(Series series, ParsedEpisodeInfo parsedEpisodeInfo, int seasonNumber, bool sceneSource, SearchCriteriaBase searchCriteria)
        {
            var result = new List<Episode>();

            var sceneSeasonNumber = _sceneMappingService.GetSceneSeasonNumber(parsedEpisodeInfo.SeriesTitle, parsedEpisodeInfo.ReleaseTitle);

            foreach (var absoluteEpisodeNumber in parsedEpisodeInfo.AbsoluteEpisodeNumbers)
            {
                var episodes = new List<Episode>();

                if (parsedEpisodeInfo.Special)
                {
                    var episode = _episodeService.FindEpisode(series.Id, 0, absoluteEpisodeNumber);
                    episodes.AddIfNotNull(episode);
                }
                else if (sceneSource)
                {
                    // Is there a reason why we excluded season 1 from this handling before?
                    // Might have something to do with the scene name to season number check
                    // If this needs to be reverted tests will need to be added
                    if (sceneSeasonNumber.HasValue)
                    {
                        episodes = _episodeService.FindEpisodesBySceneNumbering(series.Id, sceneSeasonNumber.Value, absoluteEpisodeNumber);

                        if (episodes.Empty())
                        {
                            var episode = _episodeService.FindEpisode(series.Id, sceneSeasonNumber.Value, absoluteEpisodeNumber);
                            episodes.AddIfNotNull(episode);
                        }
                    }
                    else if (parsedEpisodeInfo.SeasonNumber > 1 && parsedEpisodeInfo.EpisodeNumbers.Empty())
                    {
                        episodes = _episodeService.FindEpisodesBySceneNumbering(series.Id, parsedEpisodeInfo.SeasonNumber, absoluteEpisodeNumber);

                        if (episodes.Empty())
                        {
                            var episode = _episodeService.FindEpisode(series.Id, parsedEpisodeInfo.SeasonNumber, absoluteEpisodeNumber);
                            episodes.AddIfNotNull(episode);
                        }
                    }
                    else
                    {
                        episodes = _episodeService.FindEpisodesBySceneNumbering(series.Id, absoluteEpisodeNumber);

                        // Don't allow multiple results without a scene name mapping.
                        if (episodes.Count > 1)
                        {
                            episodes.Clear();
                        }
                    }
                }

                if (episodes.Empty())
                {
                    var episode = _episodeService.FindEpisode(series.Id, absoluteEpisodeNumber);
                    episodes.AddIfNotNull(episode);
                }

                foreach (var episode in episodes)
                {
                    _logger.Debug("Using absolute episode number {0} for: {1} - TVDB: {2}x{3:00}",
                                absoluteEpisodeNumber,
                                series.Title,
                                episode.SeasonNumber,
                                episode.EpisodeNumber);

                    result.Add(episode);
                }
            }

            return result;
        }

        private List<Episode> GetStandardEpisodes(Series series, ParsedEpisodeInfo parsedEpisodeInfo, int mappedSeasonNumber, bool sceneSource, SearchCriteriaBase searchCriteria)
        {
            var result = new List<Episode>();

            if (parsedEpisodeInfo.EpisodeNumbers == null)
            {
                return new List<Episode>();
            }

            foreach (var episodeNumber in parsedEpisodeInfo.EpisodeNumbers)
            {
                if (series.UseSceneNumbering && sceneSource)
                {
                    var episodes = new List<Episode>();

                    if (searchCriteria != null)
                    {
                        episodes = searchCriteria.Episodes.Where(e => e.SceneSeasonNumber == parsedEpisodeInfo.SeasonNumber &&
                                                                      e.SceneEpisodeNumber == episodeNumber).ToList();
                    }

                    if (!episodes.Any())
                    {
                        episodes = _episodeService.FindEpisodesBySceneNumbering(series.Id, mappedSeasonNumber, episodeNumber);
                    }

                    if (episodes != null && episodes.Any())
                    {
                        _logger.Debug("Using Scene to TVDB Mapping for: {0} - Scene: {1}x{2:00} - TVDB: {3}",
                                    series.Title,
                                    episodes.First().SceneSeasonNumber,
                                    episodes.First().SceneEpisodeNumber,
                                    string.Join(", ", episodes.Select(e => string.Format("{0}x{1:00}", e.SeasonNumber, e.EpisodeNumber))));

                        result.AddRange(episodes);
                        continue;
                    }
                }

                Episode episodeInfo = null;

                if (searchCriteria != null)
                {
                    episodeInfo = searchCriteria.Episodes.SingleOrDefault(e => e.SeasonNumber == mappedSeasonNumber && e.EpisodeNumber == episodeNumber);
                }

                if (episodeInfo == null)
                {
                    episodeInfo = _episodeService.FindEpisode(series.Id, mappedSeasonNumber, episodeNumber);
                }

                if (episodeInfo != null)
                {
                    result.Add(episodeInfo);
                }
                else
                {
                    _logger.Debug("Unable to find {0}", parsedEpisodeInfo);
                }
            }

            return result;
        }
    }
}
