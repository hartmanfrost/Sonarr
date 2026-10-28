using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DataAugmentation.Scene
{
    /// <summary>
    /// Supplies the names trackers actually use for anime in the library, which services.sonarr.tv and TheXEM do not
    /// know (romaji, English and synonym titles from AniList). Ids come from Fribb/anime-lists (AniList id to TVDB id
    /// and TVDB season). Lookups are cached under the app data folder, so a restart or a failed refresh costs nothing.
    /// Manual entries can be added in scene-mappings.override.json next to the database.
    /// </summary>
    public class AnimeAliasProvider : ISupplementarySceneMappingProvider
    {
        public const string SeasonOrigin = "anilist-season";

        private const string AnimeListUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";
        private const string AniListUrl = "https://graphql.anilist.co";
        private const string AniListQuery = "query($ids:[Int],$page:Int){Page(page:$page,perPage:50){media(id_in:$ids,type:ANIME){id title{romaji english} synonyms}}}";

        private static readonly TimeSpan AnimeListMaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan AniListMaxAge = TimeSpan.FromDays(30);

        private readonly ISeriesService _seriesService;
        private readonly IHttpClient _httpClient;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        public AnimeAliasProvider(ISeriesService seriesService, IHttpClient httpClient, IAppFolderInfo appFolderInfo, Logger logger)
        {
            _seriesService = seriesService;
            _httpClient = httpClient;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public static bool IsSeasonOrigin(string sceneOrigin)
        {
            return sceneOrigin == SeasonOrigin;
        }

        public List<SceneMapping> GetSceneMappings()
        {
            var allSeries = _seriesService.GetAllSeries();
            var animeTvdbIds = allSeries.Where(s => s.SeriesType == SeriesTypes.Anime).Select(s => s.TvdbId).ToHashSet();

            var folder = Path.Combine(_appFolderInfo.AppDataFolder, "anime-aliases");
            Directory.CreateDirectory(folder);

            var animeList = new List<AnimeListEntry>();
            var titles = new Dictionary<int, List<string>>();

            if (animeTvdbIds.Any())
            {
                try
                {
                    animeList = LoadAnimeList(Path.Combine(folder, "anime-list-full.json"), animeTvdbIds);
                    titles = LoadAniListTitles(Path.Combine(folder, "anilist-titles.json"), animeList.Select(e => e.AniListId).Distinct().OrderBy(i => i).ToList());
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Could not refresh anime aliases, using what is cached");
                }
            }

            var mappings = AnimeAliasMapper.Build(allSeries, animeList, titles, LoadOverrides(Path.Combine(_appFolderInfo.AppDataFolder, "scene-mappings.override.json")));

            _logger.Info("Anime alias provider produced {0} scene mappings", mappings.Count);

            return mappings;
        }

        private List<AnimeListEntry> LoadAnimeList(string path, HashSet<int> tvdbIds)
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > AnimeListMaxAge)
            {
                try
                {
                    var temp = path + ".tmp";
                    _httpClient.DownloadFile(AnimeListUrl, temp);
                    File.Move(temp, path, true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Could not download anime-list-full.json");

                    if (!File.Exists(path))
                    {
                        throw;
                    }
                }
            }

            var result = new List<AnimeListEntry>();

            using (var reader = new JsonTextReader(new StreamReader(path)))
            {
                foreach (var token in JArray.Load(reader))
                {
                    var tvdbId = token.Value<int?>("tvdb_id") ?? 0;
                    var aniListId = token.Value<int?>("anilist_id") ?? 0;

                    if (tvdbId <= 0 || aniListId <= 0 || !tvdbIds.Contains(tvdbId))
                    {
                        continue;
                    }

                    result.Add(new AnimeListEntry
                    {
                        Type = token.Value<string>("type"),
                        AniListId = aniListId,
                        TvdbId = tvdbId,
                        TvdbSeason = token["season"]?.Value<int?>("tvdb")
                    });
                }
            }

            return result;
        }

        private Dictionary<int, List<string>> LoadAniListTitles(string path, List<int> ids)
        {
            var cache = new Dictionary<int, CachedTitles>();

            if (File.Exists(path))
            {
                try
                {
                    cache = JsonConvert.DeserializeObject<Dictionary<int, CachedTitles>>(File.ReadAllText(path)) ?? cache;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Ignoring unreadable AniList cache");
                }
            }

            var stale = ids.Where(id => !cache.TryGetValue(id, out var c) || DateTime.UtcNow - c.Fetched > AniListMaxAge).ToList();

            try
            {
                for (var i = 0; i < stale.Count; i += 50)
                {
                    var batch = stale.Skip(i).Take(50).ToList();

                    foreach (var media in FetchAniList(batch))
                    {
                        var names = new List<string>();
                        names.Add(media["title"]?.Value<string>("romaji"));
                        names.Add(media["title"]?.Value<string>("english"));
                        names.AddRange(media["synonyms"]?.Values<string>() ?? Enumerable.Empty<string>());

                        cache[media.Value<int>("id")] = new CachedTitles
                        {
                            Fetched = DateTime.UtcNow,
                            Titles = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList()
                        };
                    }

                    // Ids AniList no longer knows are cached as empty so they are not asked for again.
                    foreach (var id in batch.Where(id => !cache.TryGetValue(id, out var c) || DateTime.UtcNow - c.Fetched > AniListMaxAge))
                    {
                        cache[id] = new CachedTitles { Fetched = DateTime.UtcNow, Titles = new List<string>() };
                    }

                    // AniList allows 90 requests a minute; stay far below it.
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "AniList lookup stopped early, the remaining names are fetched on the next update");
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(cache));

            return ids.Where(cache.ContainsKey).ToDictionary(id => id, id => cache[id].Titles);
        }

        private IEnumerable<JToken> FetchAniList(List<int> ids)
        {
            var request = new HttpRequestBuilder(AniListUrl).Post().Build();
            request.Headers.ContentType = "application/json";
            request.Headers.Accept = "application/json";
            request.SetContent(JsonConvert.SerializeObject(new { query = AniListQuery, variables = new { ids, page = 1 } }));

            var response = _httpClient.Post(request);

            return JObject.Parse(response.Content)["data"]?["Page"]?["media"] ?? new JArray();
        }

        private List<SceneMappingOverride> LoadOverrides(string path)
        {
            if (!File.Exists(path))
            {
                return new List<SceneMappingOverride>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<SceneMappingOverride>>(File.ReadAllText(path)) ?? new List<SceneMappingOverride>();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not read {0}", path);
                return new List<SceneMappingOverride>();
            }
        }

        private class CachedTitles
        {
            public DateTime Fetched { get; set; }
            public List<string> Titles { get; set; }
        }
    }
}
