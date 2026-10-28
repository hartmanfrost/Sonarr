using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class ReleaseYearSpecificationFixture : CoreTest<ReleaseYearSpecification>
    {
        private static List<Episode> Episodes(int season, params int?[] airYears)
        {
            var list = new List<Episode>();
            var n = 1;

            foreach (var year in airYears)
            {
                list.Add(new Episode
                {
                    SeasonNumber = season,
                    EpisodeNumber = n++,
                    AirDateUtc = year.HasValue ? new DateTime(year.Value, 6, 1, 0, 0, 0, DateTimeKind.Utc) : (DateTime?)null
                });
            }

            return list;
        }

        private RemoteEpisode Remote(string title, List<Episode> episodes, List<Episode> matched = null)
        {
            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodesBySeason(It.IsAny<int>(), It.IsAny<int>()))
                  .Returns<int, int>((_, season) => episodes.Where(e => e.SeasonNumber == season).ToList());

            return new RemoteEpisode
            {
                Release = new ReleaseInfo { Title = title },
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                Episodes = matched ?? episodes
            };
        }

        [Test]
        public void should_reject_2024_live_action_release_for_2014_series()
        {
            var remote = Remote(
                "Ronja Rövardotter / Ronja the Robber's Daughter / S1E1-6 of 6 [2024, WEB-DL 1080p] [RUS, SWE]",
                Episodes(1, 2014, 2014, 2014, 2015));

            var decision = Subject.IsSatisfiedBy(remote, null);

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.ReleaseYearMismatch);
        }

        [Test]
        public void should_accept_matching_release_year_for_the_same_series()
        {
            var remote = Remote(
                "Sanzoku no Musume Ronja (S1) [TV] [RUS(int)] [2014, BDRip] [1080p]",
                Episodes(1, 2014, 2014, 2015));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_year_two_off_the_air_years()
        {
            var remote = Remote("Show S01 (2017) 1080p", Episodes(1, 2014, 2015));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_year_three_off_the_air_years()
        {
            var remote = Remote("Show S01 (2018) 1080p", Episodes(1, 2014, 2015));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_range_overlapping_the_window()
        {
            var remote = Remote("Rugrats S08 [1998-2000, DVDRip]", Episodes(8, 2000, 2001));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_the_rugrats_pack_dated_by_its_span_for_season_8()
        {
            var remote = Remote("Rugrats / S8E1 [1998-2000, DVDRip]", Episodes(8, 2002, 2003, 2004));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_range_entirely_outside_the_window()
        {
            var remote = Remote("Show S01 [1990-1995, DVDRip]", Episodes(1, 2014, 2015));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_when_any_year_in_a_list_is_inside_the_window()
        {
            var remote = Remote("Collection [1992, 1995, 2003, BDRip]", Episodes(1, 1995));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_date_a_late_episode_range_by_the_season_not_by_the_episodes_in_it()
        {
            // "Hunter x Hunter (S02E76-148) [2011, HDTV]": episodes 76-148 aired 2012-2014, the tracker writes the season's start year.
            var season = new List<Episode>();

            for (var n = 1; n <= 148; n++)
            {
                season.Add(new Episode { SeasonNumber = 2, EpisodeNumber = n, AirDateUtc = new DateTime(2011 + (n / 40), 6, 1, 0, 0, 0, DateTimeKind.Utc) });
            }

            var late = season.Where(e => e.EpisodeNumber >= 100).ToList();
            var remote = Remote("Hunter x Hunter (S02E100-148) [TV] [RUS(int), JAP] [2011, HDTV] [720p]", season, late);

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_release_without_year()
        {
            var remote = Remote("Show.S01.1080p.WEB-DL-GRP", Episodes(1, 2014));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_not_treat_resolution_or_unbracketed_numbers_as_years()
        {
            var remote = Remote("Show 2024 S01 [2160p] 1080p 1999 episodes", Episodes(1, 2014));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_season_has_no_air_dates()
        {
            var remote = Remote("Show S01 [2024, WEB-DL]", Episodes(1, null, null));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_no_episodes_matched()
        {
            var remote = Remote("Show S01 [2024, WEB-DL]", new List<Episode>());

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_ignore_specials_when_building_the_window()
        {
            var episodes = Episodes(1, 2014);
            episodes.AddRange(Episodes(0, 2024));

            var remote = Remote("Show S01 [2024, WEB-DL]", episodes);

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_sao_alicization_2018_for_season_3_even_though_series_started_in_2012()
        {
            var remote = Remote(
                "Sword Art Online: Alicization (S1) [TV+SP] [RUS(ext), JAP+Sub] [2018, BDRip] [1080p]",
                Episodes(3, 2018, 2018, 2019, 2019));

            Subject.IsSatisfiedBy(remote, null).Accepted.Should().BeTrue();
        }

        [TestCase("Ronja [2024, WEB-DL]", 2024, 2024)]
        [TestCase("Show (2026) S1", 2026, 2026)]
        [TestCase("Show [1998-2000] S1", 1998, 2000)]
        [TestCase("Show [2018 - 2019, BDRip]", 2018, 2019)]
        public void should_extract_year_tokens(string title, int from, int to)
        {
            var tokens = ReleaseYearHelper.ExtractYearTokens(title);

            tokens.Should().NotBeEmpty();
            tokens[0].From.Should().Be(from);
            tokens[0].To.Should().Be(to);
        }
    }
}
