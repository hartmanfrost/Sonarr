using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DataAugmentation.Scene
{
    [TestFixture]
    public class AnimeAliasMapperFixture
    {
        private List<Series> _series;
        private List<AnimeListEntry> _animeList;
        private Dictionary<int, List<string>> _titles;

        [SetUp]
        public void Setup()
        {
            _series = new List<Series>
            {
                Make(10, "I've Been Killing Slimes for 300 Years and Maxed Out My Level", SeriesTypes.Anime),
                Make(155, "Sword Art Online", SeriesTypes.Anime),
                Make(20, "Some Western Show", SeriesTypes.Standard),
                Make(30, "Another Anime", SeriesTypes.Anime),
                Make(31, "Shared Name Anime", SeriesTypes.Anime)
            };

            _animeList = new List<AnimeListEntry>
            {
                new AnimeListEntry { Type = "TV", AniListId = 1, TvdbId = 10, TvdbSeason = 1 },
                new AnimeListEntry { Type = "TV", AniListId = 2, TvdbId = 10, TvdbSeason = 2 },
                new AnimeListEntry { Type = "OVA", AniListId = 3, TvdbId = 10, TvdbSeason = 0 },
                new AnimeListEntry { Type = "TV", AniListId = 100, TvdbId = 155, TvdbSeason = 1 },
                new AnimeListEntry { Type = "TV", AniListId = 101, TvdbId = 155, TvdbSeason = 3 },
                new AnimeListEntry { Type = "TV", AniListId = 102, TvdbId = 155, TvdbSeason = 4 },
                new AnimeListEntry { Type = "TV", AniListId = 103, TvdbId = 155, TvdbSeason = 4 },
                new AnimeListEntry { Type = "TV", AniListId = 200, TvdbId = 20, TvdbSeason = 1 },
                new AnimeListEntry { Type = "TV", AniListId = 300, TvdbId = 30, TvdbSeason = 1 },
                new AnimeListEntry { Type = "TV", AniListId = 301, TvdbId = 30, TvdbSeason = 2 },
                new AnimeListEntry { Type = "TV", AniListId = 310, TvdbId = 31, TvdbSeason = 1 }
            };

            _titles = new Dictionary<int, List<string>>
            {
                [1] = new List<string> { "Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita", "I've Been Killing Slimes for 300 Years and Maxed Out My Level", "スライム倒して300年" },
                [2] = new List<string> { "Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita: Sono ni", "Slime 300 Season 2", "Слизни" },
                [3] = new List<string> { "Slime OVA" },
                [100] = new List<string> { "Sword Art Online" },
                [101] = new List<string> { "Sword Art Online: Alicization" },
                [102] = new List<string> { "Sword Art Online: Alicization - War of Underworld" },
                [103] = new List<string> { "Sword Art Online: Alicization - War of Underworld Last Season" },
                [200] = new List<string> { "Some Western Show" },
                [300] = new List<string> { "Another Anime", "Same Words" },
                [301] = new List<string> { "Another Anime 2", "Same Words" },
                [310] = new List<string> { "Same Words" }
            };
        }

        private static Series Make(int tvdbId, string title, SeriesTypes type)
        {
            return new Series { Id = tvdbId, TvdbId = tvdbId, Title = title, CleanTitle = title.CleanSeriesTitle(), SeriesType = type };
        }

        private List<SceneMapping> Build(IEnumerable<SceneMappingOverride> overrides = null)
        {
            return AnimeAliasMapper.Build(_series, _animeList, _titles, overrides);
        }

        [Test]
        public void should_map_a_later_season_entry_to_its_tvdb_season_with_the_season_origin()
        {
            var mapping = Build().Single(m => m.Title.EndsWith("Sono ni"));

            mapping.TvdbId.Should().Be(10);
            mapping.SeasonNumber.Should().Be(2);
            mapping.SceneOrigin.Should().Be(AnimeAliasProvider.SeasonOrigin);
            mapping.ParseTerm.Should().Be(mapping.Title.CleanSeriesTitle());
        }

        [Test]
        public void should_keep_season_one_names_season_agnostic()
        {
            var mapping = Build().Single(m => m.Title.StartsWith("Slime Taoshite") && !m.Title.EndsWith("Sono ni"));

            mapping.SeasonNumber.Should().BeNull();
            mapping.SceneOrigin.Should().BeNull();
        }

        [Test]
        public void should_search_for_the_series_title_never_the_alias()
        {
            Build().Where(m => m.TvdbId == 10).Should().OnlyContain(m => m.SearchTerm == "I've Been Killing Slimes for 300 Years and Maxed Out My Level");
        }

        [Test]
        public void should_skip_non_latin_names_and_ova_entries()
        {
            var titles = Build().Select(m => m.Title).ToList();

            titles.Should().NotContain("スライム倒して300年");
            titles.Should().NotContain("Слизни");
            titles.Should().NotContain("Slime OVA");
        }

        [Test]
        public void should_map_alicization_to_tvdb_season_3_but_skip_both_cours_that_share_season_4()
        {
            var mappings = Build().Where(m => m.TvdbId == 155).ToList();

            mappings.Single(m => m.Title == "Sword Art Online: Alicization").SeasonNumber.Should().Be(3);
            mappings.Should().NotContain(m => m.Title.Contains("War of Underworld"));
        }

        [Test]
        public void should_ignore_series_that_are_not_anime()
        {
            Build().Should().NotContain(m => m.TvdbId == 20);
        }

        [Test]
        public void should_drop_a_name_that_two_shows_share()
        {
            Build().Should().NotContain(m => m.ParseTerm == "samewords");
        }

        [Test]
        public void should_drop_a_name_that_is_the_title_of_another_series_in_the_library()
        {
            _titles[300].Add("Shared Name Anime");

            Build().Should().NotContain(m => m.ParseTerm == "sharednameanime" && m.TvdbId == 30);
        }

        [Test]
        public void should_not_repeat_the_library_title_as_a_season_agnostic_alias()
        {
            Build().Should().NotContain(m => m.ParseTerm == "swordartonline");
        }

        [Test]
        public void should_become_season_agnostic_when_one_name_belongs_to_several_seasons_of_a_show()
        {
            _titles[1].Add("Slime Multi");
            _titles[2].Add("Slime Multi");

            var mapping = Build().Single(m => m.ParseTerm == "slimemulti");

            mapping.SeasonNumber.Should().BeNull();
            mapping.SceneOrigin.Should().BeNull();
        }

        [Test]
        public void should_let_the_override_file_win_and_pin_a_season()
        {
            var mappings = Build(new[]
            {
                new SceneMappingOverride { Title = "Koukaku Kidoutai", TvdbId = 45 },
                new SceneMappingOverride { Title = "Slime Taoshite 300-nen, Shiranai Uchi ni Level Max ni Nattemashita", TvdbId = 10, Season = 1 }
            });

            mappings.Single(m => m.ParseTerm == "koukakukidoutai").SearchTerm.Should().Be("Koukaku Kidoutai");

            var overridden = mappings.Single(m => m.ParseTerm == "slimetaoshite300nenshiranaiuchinilevelmaxninattemashita");
            overridden.Comment.Should().Be("override");
            overridden.SeasonNumber.Should().Be(1);
        }
    }
}
