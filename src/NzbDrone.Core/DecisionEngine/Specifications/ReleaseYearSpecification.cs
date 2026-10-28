using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Rejects a release whose title names a year (e.g. "[2024, WEB-DL 1080p]") when none of the years
    /// falls within two years of the air dates of the season(s) it was matched to. Catches same-named shows
    /// (a 2014 anime and a 2024 live-action series) that an alias match cannot tell apart.
    /// </summary>
    public class ReleaseYearSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public ReleaseYearSpecification(IEpisodeService episodeService, Logger logger)
        {
            _episodeService = episodeService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public DownloadSpecDecision IsSatisfiedBy(RemoteEpisode subject, SearchCriteriaBase searchCriteria)
        {
            var title = subject.Release?.Title;

            if (title == null || subject.Episodes == null || !subject.Episodes.Any())
            {
                return DownloadSpecDecision.Accept();
            }

            // The whole season, not just the episodes in the release: trackers date a late-episode range
            // ("S02E76-148 [2011]") with the year the season started.
            var seasonEpisodes = subject.Episodes
                                        .Where(e => e.SeasonNumber > 0)
                                        .Select(e => (e.SeriesId, e.SeasonNumber))
                                        .Distinct()
                                        .SelectMany(k => _episodeService.GetEpisodesBySeason(k.SeriesId, k.SeasonNumber) ?? new List<Episode>())
                                        .ToList();

            var window = ReleaseYearHelper.GetAirYearWindow(seasonEpisodes.Any() ? seasonEpisodes : subject.Episodes);

            if (!ReleaseYearHelper.IsOutsideWindow(title, window))
            {
                return DownloadSpecDecision.Accept();
            }

            var years = string.Join(", ", ReleaseYearHelper.ExtractYearTokens(title).Select(t => t.From == t.To ? t.From.ToString() : $"{t.From}-{t.To}"));

            _logger.Debug("Release year {0} is outside the air years {1}-{2} of the matched season", years, window.Value.From, window.Value.To);

            return DownloadSpecDecision.Reject(DownloadRejectionReason.ReleaseYearMismatch,
                $"Release year {years} does not match the air years {window.Value.From + ReleaseYearHelper.ToleranceYears}-{window.Value.To - ReleaseYearHelper.ToleranceYears} of the matched season");
        }
    }
}
