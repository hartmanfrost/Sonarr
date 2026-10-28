using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Test.ParserTests
{
    [TestFixture]
    public class ReleaseSegmentsFixture
    {
        [Test]
        public void should_split_on_slash_and_drop_brackets_and_parenthesised_tails()
        {
            var segments = ReleaseSegments.Split("Kyouran Reijou Nia Liston: Byoujaku (Kami-goroshi) Musouroku (S01E01-04) / Nia Liston: The Merciless Maiden [TV] [RUS(ext), JAP] [2026, WEB-DL] [1080p]");

            segments.Should().HaveCount(2);
            segments[0].AsIs.Should().Contain("Kyouran Reijou Nia Liston: Byoujaku");
            segments[0].AsIs.Should().Contain("Kyouran Reijou Nia Liston: Byoujaku Musouroku");
            segments[1].AsIs.Should().Equal("Nia Liston: The Merciless Maiden");
        }

        [TestCase("Show Season 2", "Show")]
        [TestCase("Show 2nd Season", "Show")]
        [TestCase("Show Part 2", "Show")]
        [TestCase("Show 3", "Show")]
        public void should_offer_a_variant_without_the_trailing_season_token(string name, string expected)
        {
            var segments = ReleaseSegments.Split(name + " (S2) [TV]");

            segments.Single().AsIs.Should().Equal(name);
            segments.Single().WithoutSeasonToken.Should().Equal(expected);
        }

        [Test]
        public void should_not_offer_a_stripped_variant_when_there_is_no_season_token()
        {
            ReleaseSegments.Split("Hunter x Hunter (S2) [TV]").Single().WithoutSeasonToken.Should().BeEmpty();
        }

        [Test]
        public void should_drop_trailing_joiners_left_by_removed_brackets()
        {
            ReleaseSegments.Split("A Long Title 3 (S3) / English Name 3 [TV] & [ENG] [2026]")[1].AsIs.Should().Equal("English Name 3");
        }

        [TestCase("Lord of Mysteries Specials / Guimi Zhi Zhu Tebie Pian (3 из 3) [816p]")]
        [TestCase("Some Show (S1) [OVA] [2025]")]
        [TestCase("Some Show Mini Anime (S1) [TV]")]
        [TestCase("Some Show Movie / Some Show [BDRip]")]
        public void should_exclude_ova_special_mini_and_movie_releases(string title)
        {
            ReleaseSegments.IsExcluded(title).Should().BeTrue();
            ReleaseSegments.Split(title).Should().BeEmpty();
        }

        [TestCase("Sword Art Online: Alicization (S1) [TV+SP] [RUS(int), JAP] [2018, BDRip]")]
        [TestCase("Reborn as a Vending Machine, I Now Wander the Dungeon 3 [TV]")]
        public void should_not_exclude_regular_tv_releases(string title)
        {
            ReleaseSegments.IsExcluded(title).Should().BeFalse();
        }

        [Test]
        public void should_ignore_names_too_short_to_be_safe()
        {
            ReleaseSegments.Split("AB / X (S1) [TV]").Should().BeEmpty();
        }
    }
}
