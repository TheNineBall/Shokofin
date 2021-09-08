using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.Utils;

using Info = Shokofin.API.Info;
using SeriesType = Shokofin.API.Models.SeriesType;
using EpisodeType = Shokofin.API.Models.EpisodeType;

namespace Shokofin.Providers
{
    public class EpisodeProvider: IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        public string Name => Plugin.MetadataProviderName;

        private readonly IHttpClientFactory HttpClientFactory;

        private readonly ILogger<EpisodeProvider> Logger;

        private readonly ShokoAPIManager ApiManager;

        public EpisodeProvider(IHttpClientFactory httpClientFactory, ILogger<EpisodeProvider> logger, ShokoAPIManager apiManager)
        {
            HttpClientFactory = httpClientFactory;
            Logger = logger;
            ApiManager = apiManager;
        }

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            try {
                var result = new MetadataResult<Episode>();
                var config = Plugin.Instance.Configuration;
                Ordering.GroupFilterType? filterByType = config.SeriesGrouping == Ordering.GroupType.ShokoGroup ? config.FilterOnLibraryTypes ? Ordering.GroupFilterType.Others : Ordering.GroupFilterType.Default : null;

                // Fetch the episode, series and group info (and file info, but that's not really used (yet))
                Info.FileInfo fileInfo = null;
                Info.EpisodeInfo episodeInfo = null;
                Info.SeriesInfo seriesInfo = null;
                Info.GroupInfo groupInfo = null;
                if (info.IsMissingEpisode || info.Path == null) {
                    // We're unable to fetch the latest metadata for the virtual episode.
                    if (!info.ProviderIds.TryGetValue("Shoko Episode", out var episodeId))
                        return result;

                    episodeInfo = await ApiManager.GetEpisodeInfo(episodeId);
                    if (episodeInfo == null)
                        return result;

                    seriesInfo = await ApiManager.GetSeriesInfoForEpisode(episodeId);
                    if (seriesInfo == null)
                        return result;

                    groupInfo = filterByType.HasValue ? (await ApiManager.GetGroupInfoForSeries(seriesInfo.Id, filterByType.Value)) : null;
                }
                else {
                    (fileInfo, episodeInfo, seriesInfo, groupInfo) = await ApiManager.GetFileInfoByPath(info.Path, filterByType);
                }

                // if the episode info is null then the series info and conditionally the group info is also null.
                if (episodeInfo == null) {
                    Logger.LogWarning("Unable to find episode info for path {Path}", info.Path);
                    return result;
                }

                var fileId = fileInfo?.Id ?? null;
                result.Item = CreateMetadata(groupInfo, seriesInfo, episodeInfo, fileId, info.MetadataLanguage);
                Logger.LogInformation("Found episode {EpisodeName} (File={FileId},Episode={EpisodeId},Series={SeriesId})", result.Item.Name, fileId, episodeInfo.Id, seriesInfo.Id);

                result.HasMetadata = true;

                if (fileInfo != null) {
                    var episodeNumberEnd = episodeInfo.AniDB.EpisodeNumber + fileInfo.ExtraEpisodesCount;
                    if (episodeInfo.AniDB.EpisodeNumber != episodeNumberEnd)
                        result.Item.IndexNumberEnd = episodeNumberEnd;
                }


                return result;
            }
            catch (Exception e) {
                Logger.LogError(e, $"Threw unexpectedly; {e.Message}");
                return new MetadataResult<Episode>();
            }
        }

        public static Episode CreateMetadata(Info.GroupInfo group, Info.SeriesInfo series, Info.EpisodeInfo episode, Season season, System.Guid episodeId)
            => CreateMetadata(group, series, episode, null, null, season, episodeId);

        public static Episode CreateMetadata(Info.GroupInfo group, Info.SeriesInfo series, Info.EpisodeInfo episode, string fileId, string metadataLanguage)
            => CreateMetadata(group, series, episode, fileId, metadataLanguage, null, Guid.Empty);

        private static Episode CreateMetadata(Info.GroupInfo group, Info.SeriesInfo series, Info.EpisodeInfo episode, string fileId, string metadataLanguage, Season season, System.Guid episodeId)
        {
            if (string.IsNullOrEmpty(metadataLanguage) && season != null)
                metadataLanguage = season.GetPreferredMetadataLanguage();
            var config = Plugin.Instance.Configuration;
            string displayTitle, alternateTitle;
            if (series.AniDB.Type == SeriesType.Movie && (episode.AniDB.Type == EpisodeType.Normal || episode.AniDB.Type == EpisodeType.Special))
                ( displayTitle, alternateTitle ) = Text.GetMovieTitles(series.AniDB.Titles, episode.AniDB.Titles, series.Shoko.Name, episode.Shoko.Name, metadataLanguage);
            else
                ( displayTitle, alternateTitle ) = Text.GetEpisodeTitles(series.AniDB.Titles, episode.AniDB.Titles, episode.Shoko.Name, metadataLanguage);

            var episodeNumber = Ordering.GetEpisodeNumber(group, series, episode);
            var seasonNumber = Ordering.GetSeasonNumber(group, series, episode);
            var description = Text.GetDescription(episode);

            if (group != null && config.MarkSpecialsWhenGrouped && episode.AniDB.Type != EpisodeType.Normal) switch (episode.AniDB.Type) {
                case EpisodeType.Special:
                    displayTitle = $"S{episodeNumber} {displayTitle}";
                    alternateTitle = $"S{episodeNumber} {alternateTitle}";
                    break;
                case EpisodeType.ThemeSong:
                case EpisodeType.EndingSong:
                case EpisodeType.OpeningSong:
                    displayTitle = $"C{episodeNumber} {displayTitle}";
                    alternateTitle = $"C{episodeNumber} {alternateTitle}";
                    break;
                case EpisodeType.Trailer:
                    displayTitle = $"T{episodeNumber} {displayTitle}";
                    alternateTitle = $"T{episodeNumber} {alternateTitle}";
                    break;
                case EpisodeType.Parody:
                    displayTitle = $"P{episodeNumber} {displayTitle}";
                    alternateTitle = $"P{episodeNumber} {alternateTitle}";
                    break;
                case EpisodeType.Unknown:
                    displayTitle = $"U{episodeNumber} {displayTitle}";
                    alternateTitle = $"U{episodeNumber} {alternateTitle}";
                    break;
                default:
                    displayTitle = $"O{episodeNumber} {displayTitle}";
                    alternateTitle = $"O{episodeNumber} {alternateTitle}";
                    break;
            }

            Episode result;
            if (group != null && episode.AniDB.Type == EpisodeType.Special) {
                int? previousEpisodeNumber = null;
                if (series.SpesialsAnchors.TryGetValue(episode.Id, out var previousEpisode))
                    previousEpisodeNumber = Ordering.GetEpisodeNumber(group, series, previousEpisode);
                int? nextEpisodeNumber = previousEpisodeNumber.HasValue && previousEpisodeNumber.Value < series.EpisodeList.Count ? previousEpisodeNumber.Value + 1 : null;
                if (season != null) {
                    result = new Episode {
                        Name = displayTitle,
                        OriginalTitle = alternateTitle,
                        IndexNumber = episodeNumber,
                        ParentIndexNumber = 0,
                        AirsAfterSeasonNumber = seasonNumber,
                        AirsBeforeEpisodeNumber = nextEpisodeNumber,
                        AirsBeforeSeasonNumber = seasonNumber + 1,
                        Id = episodeId,
                        IsVirtualItem = true,
                        SeasonId = season.Id,
                        SeriesId = season.Series.Id,
                        Overview = description,
                        CommunityRating = episode.AniDB.Rating.ToFloat(10),
                        PremiereDate = episode.AniDB.AirDate,
                        SeriesName = season.Series.Name,
                        SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                        SeasonName = season.Name,
                        DateLastSaved = DateTime.UtcNow,
                    };
                    result.PresentationUniqueKey = result.GetPresentationUniqueKey();
                }
                else {
                    result = new Episode {
                        IndexNumber = episodeNumber,
                        ParentIndexNumber = 0,
                        AirsAfterSeasonNumber = seasonNumber,
                        AirsBeforeEpisodeNumber = nextEpisodeNumber,
                        AirsBeforeSeasonNumber = seasonNumber + 1,
                        Name = displayTitle,
                        OriginalTitle = alternateTitle,
                        PremiereDate = episode.AniDB.AirDate,
                        Overview = description,
                        CommunityRating = episode.AniDB.Rating.ToFloat(10),
                    };
                }
            }
            else {
                if (season != null) {
                    result = new Episode {
                        Name = displayTitle,
                        OriginalTitle = alternateTitle,
                        IndexNumber = episodeNumber,
                        ParentIndexNumber = seasonNumber,
                        Id = episodeId,
                        IsVirtualItem = true,
                        SeasonId = season.Id,
                        SeriesId = season.Series.Id,
                        Overview = description,
                        CommunityRating = episode.AniDB.Rating.ToFloat(10),
                        PremiereDate = episode.AniDB.AirDate,
                        SeriesName = season.Series.Name,
                        SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                        SeasonName = season.Name,
                        DateLastSaved = DateTime.UtcNow,
                    };
                    result.PresentationUniqueKey = result.GetPresentationUniqueKey();
                }
                else {
                    result = new Episode {
                        Name = displayTitle,
                        OriginalTitle = alternateTitle,
                        IndexNumber = episodeNumber,
                        ParentIndexNumber = seasonNumber,
                        PremiereDate = episode.AniDB.AirDate,
                        Overview = description,
                        CommunityRating = episode.AniDB.Rating.ToFloat(10),
                    };
                }
            }
            // NOTE: This next line will remain here till they fix the series merging for providers outside the MetadataProvider enum.
            if (config.SeriesGrouping == Ordering.GroupType.ShokoGroup)
                result.SetProviderId(MetadataProvider.Imdb, $"INVALID-BUT-DO-NOT-TOUCH:{episode.Id}");
            else if (config.SeriesGrouping == Ordering.GroupType.MergeFriendly && episode.TvDB != null && config.SeriesGrouping != Ordering.GroupType.ShokoGroup)
                result.SetProviderId(MetadataProvider.Tvdb, episode.TvDB.ID.ToString());
            result.SetProviderId("Shoko Episode", episode.Id);
            if (!string.IsNullOrEmpty(fileId))
                result.SetProviderId("Shoko File", fileId);
            if (config.AddAniDBId)
                result.SetProviderId("AniDB", episode.AniDB.ID.ToString());

            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            // Isn't called from anywhere. If it is called, I don't know from where.
            throw new NotImplementedException();
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return HttpClientFactory.CreateClient().GetAsync(url, cancellationToken);
        }
    }
}
