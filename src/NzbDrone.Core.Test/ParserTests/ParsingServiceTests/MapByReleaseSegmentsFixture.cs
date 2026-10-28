using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.ParserTests.ParsingServiceTests
{
    [TestFixture]
    public class MapByReleaseSegmentsFixture : CoreTest<ParsingService>
    {
        private const int Slimes = 401;
        private const int Vending = 402;
        private const int LordOfMysteries = 403;
        private const int NiaListon = 404;
        private const int Sao = 155;
        private const int HunterX1999 = 112;
        private const int Ghibli = 405;

        private readonly List<Series> _library = new List<Series>();
        private readonly List<SceneMapping> _mappings = new List<SceneMapping>();
        private readonly Dictionary<int, List<Episode>> _episodes = new Dictionary<int, List<Episode>>();

        private void AddSeries(int tvdbId, string title, params (int Season, int Year)[] seasons)
        {
            _library.Add(new Series { Id = tvdbId, TvdbId = tvdbId, Title = title, CleanTitle = title.CleanSeriesTitle(), SeriesType = SeriesTypes.Anime });

            var episodes = new List<Episode>();

            foreach (var (season, year) in seasons)
            {
                for (var n = 1; n <= 12; n++)
                {
                    episodes.Add(new Episode
                    {
                        SeriesId = tvdbId,
                        SeasonNumber = season,
                        EpisodeNumber = n,
                        AirDateUtc = new DateTime(year, 1 + (n / 2), 1, 0, 0, 0, DateTimeKind.Utc)
                    });
                }
            }

            _episodes[tvdbId] = episodes;
        }

        private void AddAlias(string title, int tvdbId, int? season = null)
        {
            _mappings.Add(new SceneMapping
            {
                Title = title,
                ParseTerm = title.CleanSeriesTitle(),
                SearchTerm = _library.First(s => s.TvdbId == tvdbId).Title,
                TvdbId = tvdbId,
                SeasonNumber = season,
                SceneOrigin = season.HasValue ? AnimeAliasProvider.SeasonOrigin : null,
                Type = "AnimeAliasProvider"
            });
        }

        [SetUp]
        public void Setup()
        {
            AddSeries(Slimes, "I've Been Killing Slimes for 300 Years and Maxed Out My Level", (1, 2021), (2, 2025));
            AddSeries(Vending, "Reborn as a Vending Machine, I Now Wander the Dungeon", (1, 2023), (2, 2025), (3, 2026));
            AddSeries(LordOfMysteries, "Lord of Mysteries", (1, 2025));
            AddSeries(NiaListon, "Nia Liston: The Merciless Maiden", (1, 2026));
            AddSeries(Sao, "Sword Art Online", (1, 2012), (2, 2014), (3, 2018), (4, 2019));
            AddSeries(HunterX1999, "Hunter x Hunter", (1, 1999));

            // What AnimeAliasProvider would produce from AniList.
            AddAlias("Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita", Slimes);
            AddAlias("Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita: Sono ni", Slimes, 2);
            AddAlias("Kyouran Reijou Nia Liston: Byoujaku Reijou ni Tensei shita Kamigoroshi no Bujin no Karei Naru Musouroku", NiaListon);
            AddAlias("Guimi Zhi Zhu: Xiaochou Pian", LordOfMysteries);
            AddAlias("Lord of the Mysteries", LordOfMysteries);
            AddAlias("Jidouhanbaiki ni Umarekawatta Ore wa Meikyuu wo Samayou", Vending);
            AddAlias("Jidouhanbaiki ni Umarekawatta Ore wa Meikyuu wo Samayou 3rd Season", Vending, 3);
            AddAlias("Sword Art Online: Alicization", Sao, 3);
            AddAlias("SAO Alicization", Sao, 3);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.FindByTitle(It.IsAny<string>()))
                  .Returns<string>(t => _library.FirstOrDefault(s => s.CleanTitle == t.CleanSeriesTitle()));

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                  .Returns<int>(id => _library.FirstOrDefault(s => s.TvdbId == id));

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindMappingsByTerm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                  .Returns<string, string, int>((term, _, _) => _mappings.Where(m => m.ParseTerm == term.CleanSeriesTitle()).ToList());

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodesBySeason(It.IsAny<int>(), It.IsAny<int>()))
                  .Returns<int, int>((id, season) => _episodes[id].Where(e => e.SeasonNumber == season).ToList());

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.FindEpisodesBySceneNumbering(It.IsAny<int>(), It.IsAny<int>()))
                  .Returns(() => new List<Episode>());

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.FindEpisodesBySceneNumbering(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                  .Returns(() => new List<Episode>());

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.FindEpisode(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                  .Returns<int, int, int>((id, season, number) => _episodes[id].FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == number));

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodeBySeries(It.IsAny<int>()))
                  .Returns<int>(id => _episodes[id]);
        }

        private static void AssertEpisodes(RemoteEpisode remote, int season, int count)
        {
            remote.Episodes.Should().HaveCount(count);
            remote.Episodes.Should().OnlyContain(e => e.SeasonNumber == season);
        }

        private RemoteEpisode MapTitle(string releaseTitle)
        {
            var parsed = Parser.Parser.ParseTitle(releaseTitle);

            parsed.Should().NotBeNull("the parser must understand '{0}'", releaseTitle);

            // The real SceneMappingService resolves the parsed title; here only the segment path is under test unless a test says otherwise.
            return Subject.Map(parsed, 0, 0, null);
        }

        [Test]
        public void should_match_nia_liston_by_its_english_segment_equal_to_the_library_title()
        {
            var remote = MapTitle("Kyouran Reijou Nia Liston: Byoujaku Reijou ni Tensei shita Kamigoroshi (Kami-goroshi) no Bujin no Karei Naru Musouroku (S01E01-04) / Nia Liston: The Merciless Maiden [TV] [RUS(ext), JAP] [2026, WEB-DL] [1080p]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(NiaListon);
            AssertEpisodes(remote, 1, 4);
        }

        [Test]
        public void should_match_slimes_season_2_through_the_sono_ni_alias_and_map_it_to_tvdb_season_2()
        {
            var remote = MapTitle("Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita Sono Ni (S2) / I've Been Killing Slimes for 300 Years and Maxed Out My Level Season 2 [TV] [RUS(int), JAP] [2025, WEB-DL] [1080p]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(Slimes);
            remote.MappedSeasonNumber.Should().Be(2);
            AssertEpisodes(remote, 2, 12);
        }

        [Test]
        public void should_not_read_the_300_in_the_slimes_name_as_absolute_episode_300()
        {
            var remote = MapTitle("Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita (S2) (2025) WEBRip Ai Rem 1080p H.265 Ukr/Jap | Sub Ukr (S2) [Deeman]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(Slimes);
            AssertEpisodes(remote, 2, 12);
            remote.ParsedEpisodeInfo.AbsoluteEpisodeNumbers.Should().BeEmpty();
        }

        [Test]
        public void should_keep_the_original_numbering_when_the_year_guard_rejects_the_release()
        {
            var remote = MapTitle("Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita (S2) (1999) WEBRip 1080p");

            remote.Series.Should().BeNull();
            remote.ParsedEpisodeInfo.AbsoluteEpisodeNumbers.Should().Equal(300);
            remote.ParsedEpisodeInfo.SeasonNumber.Should().Be(0);
        }

        [Test]
        public void should_match_lord_of_mysteries_by_the_romaji_alias_with_year_and_season_tokens()
        {
            var remote = MapTitle("Guimi Zhi Zhu: Xiaochou Pian (S1) (2025) WEB-DL 1080p H.265 (S1+) [FanVoxUA]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(LordOfMysteries);
            AssertEpisodes(remote, 1, 12);
        }

        [Test]
        public void should_match_vending_machine_season_3_by_the_english_segment_without_the_trailing_number()
        {
            var remote = MapTitle("Jidouhanbaiki (Jidou Hanbaiki) ni Umarekawatta Ore wa Meikyuu o (wo) Samayou 3 (S3) / Reborn as a Vending Machine, I Now Wander the Dungeon 3 [TV] [RUS(int), JAP] & [ENG] [2026, WEB-DL] [1080p]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(Vending);
            remote.MappedSeasonNumber.Should().Be(3);
            AssertEpisodes(remote, 3, 12);
        }

        [Test]
        public void should_map_alicization_to_tvdb_season_3_whatever_season_number_the_tracker_uses()
        {
            var parsed = Parser.Parser.ParseTitle("Sword Art Online: Alicization (S1) [TV+SP] [RUS(int), JAP] [2018, BDRip]");

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindSceneMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                  .Returns(_mappings.First(m => m.Title == "Sword Art Online: Alicization"));

            var remote = Subject.Map(parsed, 0, 0, null);

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(Sao);
            remote.MappedSeasonNumber.Should().Be(3);
            AssertEpisodes(remote, 3, 12);
        }

        [Test]
        public void should_keep_mapping_war_of_underworld_season_1_to_tvdb_season_4_through_the_existing_alias()
        {
            // The services.sonarr.tv alias stays authoritative: tracker S1 = scene season 1 = TVDB season 4.
            var servicesAlias = new SceneMapping
            {
                Title = "Sword Art Online: Alicization - War of Underworld",
                ParseTerm = "swordartonlinealicizationwarofunderworld",
                SearchTerm = "Sword Art Online: Alicization - War of Underworld",
                TvdbId = Sao,
                SeasonNumber = 4,
                SceneSeasonNumber = 1,
                Type = "ServicesProvider"
            };

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindSceneMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                  .Returns(servicesAlias);

            var remote = Subject.Map(Parser.Parser.ParseTitle("Sword Art Online: Alicization - War of Underworld (S1) / SAO Alicization 2nd Season [TV] [RUS(ext), JAP] [2019, BDRip] [1080p]"), 0, 0, null);

            remote.Series.Should().NotBeNull();
            remote.MappedSeasonNumber.Should().Be(4);
        }

        [Test]
        public void should_not_turn_the_last_season_of_war_of_underworld_into_alicization()
        {
            // "SAO Alicization 3rd Season" cut to "SAO Alicization" would hit the season 3 alias; the release is season 4's second cour.
            var remote = MapTitle("Sword Art Online: Alicization - War of Underworld the last season (S2) / SAO Alicization 3rd Season [TV] [RUS(ext), JAP] [2020, BDRip] [1080p]");

            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_leave_sao_alternative_gun_gale_online_unknown()
        {
            var remote = MapTitle("Sword Art Online Alternative: Gun Gale Online II (S2) / SAO Alternative: GGO II [TV] [RUS(ext), JAP] [2024, WEB-DL] [1080p]");

            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_leave_the_2011_hunter_x_hunter_remake_unknown_for_the_1999_series()
        {
            var remote = MapTitle("HxH Remake (S01E01-04) / Hunter x Hunter [TV] [RUS(int), JAP] [2011, BDRip]");

            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_still_match_hunter_x_hunter_when_the_release_year_fits_the_1999_series()
        {
            var remote = MapTitle("HxH Remake (S01E01-04) / Hunter x Hunter [TV] [RUS(int), JAP] [1999, BDRip]");

            remote.Series.Should().NotBeNull();
            remote.Series.TvdbId.Should().Be(HunterX1999);
        }

        [Test]
        public void should_not_read_a_pack_counter_as_an_absolute_episode_of_a_name_match()
        {
            var remote = MapTitle("Lord of Mysteries / Guimi Zhi Zhu: Xiaochou Pian / Повелитель тайн: Шут (13 из 13) Complete [544p]");

            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_not_match_ova_or_special_releases_by_name()
        {
            var remote = MapTitle("Guimi Zhi Zhu Tebie Pian: Liewu / Lord of Mysteries Specials (S1) [OVA] [2025, WEB-DL] [1080p]");

            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_stay_unknown_when_segments_point_at_different_series()
        {
            var remote = MapTitle("Some Unknown Romaji Name (S1) / Lord of Mysteries / Nia Liston: The Merciless Maiden [TV] [2026, WEB-DL] [1080p]");

            // Both are exact library titles of different series: ambiguous, so nothing is picked.
            remote.Series.Should().BeNull();
        }

        [Test]
        public void should_not_match_by_prefix_or_substring()
        {
            var remote = MapTitle("Lord of Mysteries Extra Long Unrelated Name (S1) / Another Title [TV] [2025, WEB-DL] [1080p]");

            remote.Series.Should().BeNull();
        }
    }
}
