using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class IgnoredDownloadServiceFixture : CoreTest<IgnoredDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                          .With(h => h.Title = "Unknown.Show.S01E01.1080p")
                                                          .With(h => h.DownloadId = "ABCD")
                                                          .With(h => h.DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qbit", Type = "QBittorrent" })
                                                          .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.DownloadItem = downloadItem)
                                                       .With(c => c.RemoteEpisode = null)
                                                       .Build();
        }

        [Test]
        public void should_ignore_download_without_remote_episode()
        {
            Subject.IgnoreDownload(_trackedDownload).Should().BeTrue();

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.Is<DownloadIgnoredEvent>(e => e.SeriesId == 0 &&
                                                                                 e.EpisodeIds.Count == 0 &&
                                                                                 e.DownloadId == "ABCD")),
                      Times.Once());
        }

        [Test]
        public void should_ignore_download_without_series()
        {
            _trackedDownload.RemoteEpisode = new RemoteEpisode
            {
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                Episodes = new List<Episode>()
            };

            Subject.IgnoreDownload(_trackedDownload).Should().BeTrue();

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.Is<DownloadIgnoredEvent>(e => e.SeriesId == 0 && e.DownloadId == "ABCD")), Times.Once());
        }

        [Test]
        public void should_ignore_download_with_series_and_episodes()
        {
            _trackedDownload.RemoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                Episodes = new List<Episode> { new Episode { Id = 7 } }
            };

            Subject.IgnoreDownload(_trackedDownload).Should().BeTrue();

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.Is<DownloadIgnoredEvent>(e => e.SeriesId == 5 && e.EpisodeIds.Contains(7))), Times.Once());
        }
    }
}
