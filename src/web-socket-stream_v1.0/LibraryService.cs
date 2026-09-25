using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.WsStream;

/// <summary>User-scoped library reads. Every lookup goes through Jellyfin's own visibility rules.</summary>
internal sealed class LibraryService
{
    private const int MaxListItems = 2000;
    private const int MaxThumbBytes = 2 * 1024 * 1024;

    private static readonly BaseItemKind[] SearchKinds =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicArtist,
        BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.Video,
        BaseItemKind.BoxSet, BaseItemKind.Playlist, BaseItemKind.AudioBook,
    ];

    private readonly ILibraryManager _library;
    private readonly IUserViewManager _userViews;
    private readonly IImageProcessor _images;

    public LibraryService(ILibraryManager library, IUserViewManager userViews, IImageProcessor images)
    {
        _library = library;
        _userViews = userViews;
        _images = images;
    }

    public object[] Views(User user)
        => _userViews.GetUserViews(new UserViewQuery { User = user })
            .Where(v => KindsFor((v as IHasCollectionType)?.CollectionType) is not null || v is not ICollectionFolder)
            .Select(v => Dto(v))
            .ToArray();

    public object[] Search(User user, string term)
    {
        var items = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = term,
            IncludeItemTypes = SearchKinds,
            IncludeItemsByName = true, // artists live outside library folders
            Recursive = true,
            Limit = 60,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)], // after Jellyfin's relevance ordering
        });

        return items.Where(i => !(i.IsVirtualItem && i is not MusicArtist)).Select(Dto).ToArray();
    }

    /// <summary>Children of a browsable item, or null when it doesn't exist / isn't visible to the user.</summary>
    public (object Parent, object[] Items)? Kids(User user, Guid id)
    {
        var item = id == Guid.Empty ? null : _library.GetItemById<BaseItem>(id, user);
        if (item is null)
        {
            return null;
        }

        IEnumerable<BaseItem> list = item switch
        {
            MusicArtist artist => _library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                ArtistIds = [artist.Id],
                Recursive = true,
                Limit = MaxListItems,
                OrderBy = [(ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)],
            }),
            Series or Season => _library.GetItemList(new InternalItemsQuery(user)
            {
                AncestorIds = [item.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false, // hide "missing episode" placeholders
                Recursive = true,
                Limit = MaxListItems,
                OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
            }),
            MusicAlbum => _library.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = item.Id,
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true,
                Limit = MaxListItems,
                OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
            }),
            IHasCollectionType lib when KindsFor(lib.CollectionType) is { } kinds => _library.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = item.Id,
                IncludeItemTypes = kinds,
                Recursive = true,
                Limit = MaxListItems,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            }),
            Folder folder => folder.GetChildren(user, true),
            _ => [],
        };

        var items = list
            .Where(i => !i.IsVirtualItem && (i is not Playlist p || p.IsVisible(user)))
            .Take(MaxListItems)
            .Select(Dto)
            .ToArray();
        return (Dto(item), items);
    }

    /// <summary>Resized primary image bytes (webp/jpeg), falling back to album / series art.</summary>
    public async Task<byte[]?> ThumbAsync(User user, Guid id, int width)
    {
        var item = id == Guid.Empty ? null : _library.GetItemById<BaseItem>(id, user);
        var source = item is null ? null : ImageSource(item);
        var info = source?.GetImageInfo(ImageType.Primary, 0);
        if (source is null || info is null)
        {
            return null;
        }

        width = Math.Clamp(width, 48, 640);
        var (path, _, _) = await _images.ProcessImage(new ImageProcessingOptions
        {
            Item = source,
            ItemId = source.Id,
            Image = info,
            ImageIndex = 0,
            MaxWidth = width,
            MaxHeight = width * 3 / 2,
            Quality = 80,
            SupportedOutputFormats = [ImageFormat.Webp, ImageFormat.Jpg],
        }).ConfigureAwait(false);

        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxThumbBytes)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }

    /// <summary>Visible, playable item or null.</summary>
    public BaseItem? GetPlayable(User user, Guid id)
    {
        var item = id == Guid.Empty ? null : _library.GetItemById<BaseItem>(id, user);
        return item is not null && IsPlayable(item) ? item : null;
    }

    internal static bool IsPlayable(BaseItem i)
        => !i.IsFolder
           && !i.IsVirtualItem
           && (i.MediaType == MediaType.Audio || i.MediaType == MediaType.Video)
           && !string.IsNullOrEmpty(i.Path)
           && (i is not Video v || v.VideoType == VideoType.VideoFile);

    private static BaseItem? ImageSource(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary, 0))
        {
            return item;
        }

        BaseItem? fallback = item switch
        {
            Audio a => a.AlbumEntity,
            Episode e => e.Series,
            Season s => s.Series,
            _ => null,
        };
        return fallback is not null && fallback.HasImage(ImageType.Primary, 0) ? fallback : null;
    }

    private static BaseItemKind[]? KindsFor(CollectionType? type) => type switch
    {
        CollectionType.movies => [BaseItemKind.Movie],
        CollectionType.tvshows => [BaseItemKind.Series],
        CollectionType.music => [BaseItemKind.MusicAlbum],
        CollectionType.musicvideos => [BaseItemKind.MusicVideo],
        CollectionType.homevideos => [BaseItemKind.Video],
        CollectionType.boxsets => [BaseItemKind.BoxSet],
        CollectionType.playlists => [BaseItemKind.Playlist],
        CollectionType.books => [BaseItemKind.AudioBook],
        _ => null,
    };

    private static object Dto(BaseItem i)
    {
        string? artists = i switch
        {
            Audio a => Join(a.Artists.Count > 0 ? a.Artists : a.AlbumArtists),
            MusicAlbum al => Join(al.AlbumArtists.Count > 0 ? al.AlbumArtists : al.Artists),
            MusicVideo mv => Join(mv.Artists),
            _ => null,
        };

        return new
        {
            id = i.Id.ToString("N"),
            n = i.Name,
            k = i.GetBaseItemKind().ToString(),
            ct = (i as IHasCollectionType)?.CollectionType?.ToString(),
            y = i.ProductionYear,
            d = i.RunTimeTicks is long t && t > 0 ? Math.Round(t / 10_000_000d, 1) : (double?)null,
            ar = artists,
            al = i is Audio ? i.Album : null,
            se = (i as Episode)?.SeriesName ?? (i as Season)?.SeriesName,
            s = i is Episode or Audio ? i.ParentIndexNumber : null,
            e = i is Episode or Audio or Season ? i.IndexNumber : null,
            img = ImageSource(i) is not null,
            play = IsPlayable(i),
            open = i.IsFolder,
            video = i.MediaType == MediaType.Video,
        };
    }

    private static string? Join(IReadOnlyList<string>? values)
        => values is { Count: > 0 } ? string.Join(", ", values) : null;
}
