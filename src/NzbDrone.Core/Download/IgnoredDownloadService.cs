using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public interface IIgnoredDownloadService
    {
        bool IgnoreDownload(TrackedDownload trackedDownload);
    }

    public class IgnoredDownloadService : IIgnoredDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public IgnoredDownloadService(IEventAggregator eventAggregator,
                                      Logger logger)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public bool IgnoreDownload(TrackedDownload trackedDownload)
        {
            // Unknown-series items have no RemoteEpisode (title did not parse) or a RemoteEpisode without a Series.
            // They must still be ignorable, otherwise the queue row can only be cleared by deleting the torrent.
            var remoteEpisode = trackedDownload.RemoteEpisode;
            var series = remoteEpisode?.Series;

            if (series == null)
            {
                _logger.Debug("Ignoring download '{0}' that is not matched to a series", trackedDownload.DownloadItem.Title);
            }

            var downloadIgnoredEvent = new DownloadIgnoredEvent
                                      {
                                          SeriesId = series?.Id ?? 0,
                                          EpisodeIds = remoteEpisode?.Episodes?.Select(e => e.Id).ToList() ?? new List<int>(),
                                          Languages = remoteEpisode?.Languages ?? new List<Language>(),
                                          Quality = remoteEpisode?.ParsedEpisodeInfo?.Quality,
                                          SourceTitle = trackedDownload.DownloadItem.Title,
                                          DownloadClientInfo = trackedDownload.DownloadItem.DownloadClientInfo,
                                          DownloadId = trackedDownload.DownloadItem.DownloadId,
                                          TrackedDownload = trackedDownload,
                                          Message = "Manually ignored"
                                      };

            _eventAggregator.PublishEvent(downloadIgnoredEvent);
            return true;
        }
    }
}
