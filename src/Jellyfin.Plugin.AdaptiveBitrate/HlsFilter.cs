using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AdaptiveBitrate.Core;
using Jellyfin.Data;
using Jellyfin.Plugin.AdaptiveBitrate.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>
/// Pose une échelle de qualités dans la playlist HLS maîtresse de Jellyfin (/Videos/{id}/master.m3u8) et note les
/// changements de qualité (/Videos/{id}/main.m3u8). Chaque marche est produite par Jellyfin lui-même : son action est
/// rappelée avec un débit et une hauteur plus bas (copie de la vidéo interdite, tout le reste identique : piste audio,
/// sous-titres, conteneur des segments, session de lecture). Le lecteur (hls.js dans le navigateur, ExoPlayer, AVPlayer)
/// change ensuite de marche tout seul : il charge et met en mémoire la nouvelle qualité avant de basculer, à la limite
/// d'un segment, sans relancer la lecture. Jellyfin arrête lui-même l'encodage de l'ancienne marche quand la nouvelle
/// démarre (même session de lecture). Toute forme inattendue : la playlist de Jellyfin part telle quelle.
/// </summary>
public sealed class HlsFilter : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>Contrôleur des flux HLS de Jellyfin.</summary>
    public const string Controller = "DynamicHls";

    /// <summary>Playlist maîtresse d'une vidéo.</summary>
    public const string MasterAction = "GetMasterHlsVideoPlaylist";

    /// <summary>Playlist d'une qualité (demandée par le lecteur quand il passe à cette qualité).</summary>
    public const string VariantAction = "GetVariantHlsVideoPlaylist";

    /// <summary>Paramètres de l'action dont dépend le plugin (vérifiés avant tout appel).</summary>
    public static readonly string[] Required = { "itemId", "mediaSourceId", "videoBitRate", "maxHeight", "maxWidth", "width", "height", "allowVideoStreamCopy", "enableAutoStreamCopy", "enableAdaptiveBitrateStreaming", "liveStreamId", "playSessionId" };

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _sources;
    private readonly IUserManager _users;
    private readonly INetworkManager _network;
    private readonly SwitchLog _log;
    private readonly ILogger<HlsFilter> _logger;
    private bool _warned;

    public HlsFilter(ILibraryManager library, IMediaSourceManager sources, IUserManager users, INetworkManager network, SwitchLog log, ILogger<HlsFilter> logger)
    {
        _library = library;
        _sources = sources;
        _users = users;
        _network = network;
        _log = log;
        _logger = logger;
    }

    /// <summary>Exécuté au plus près de l'action : les filtres des autres plugins enveloppent le nôtre.</summary>
    public int Order => int.MaxValue - 50;

    /// <summary>Dernière raison de ne pas poser l'échelle (page de configuration), texte français.</summary>
    public static string? LastSkip { get; private set; }

    /// <summary>La forme de l'action de Jellyfin n'est pas celle attendue : le plugin ne fait rien (page de configuration).</summary>
    public static bool Incompatible { get; private set; }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor d || d.ControllerName != Controller)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (d.ActionName == VariantAction)
        {
            Track(context);
            await next().ConfigureAwait(false);
            return;
        }

        if (d.ActionName != MasterAction || Plugin.Instance?.Configuration is not { } cfg)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var parameters = d.MethodInfo.GetParameters();
        if (Required.Any(r => !parameters.Any(p => p.Name == r)))
        {
            Incompatible = true;
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning("Adaptive Bitrate: Jellyfin's HLS master playlist action no longer has the expected parameters; playlists are left untouched");
            }

            await next().ConfigureAwait(false);
            return;
        }

        string? skip;
        try
        {
            skip = Policy.Skip(cfg.Enabled, cfg.Scope, Policy.ParseClients(cfg.ExcludedClients), Describe(context));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adaptive Bitrate: could not read the playback request; playlist left untouched");
            skip = "demande illisible";
        }

        LastSkip = skip;
        if (skip is not null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // la playlist de Jellyfin, sans ses deux variantes à peine plus basses (remplacées par l'échelle)
        context.ActionArguments["enableAdaptiveBitrateStreaming"] = false;
        var executed = await next().ConfigureAwait(false);
        if (executed.Exception is not null || executed.Result is not FileContentResult original)
        {
            return;
        }

        try
        {
            var text = Encoding.UTF8.GetString(original.FileContents);
            var rungs = Ladder.Rungs(Arg<int?>(context, "videoBitRate"), Source(context), cfg.LadderOptions());
            var added = new List<Variant>();
            foreach (var rung in rungs)
            {
                var playlist = await RungPlaylist(context, d.MethodInfo, rung).ConfigureAwait(false);
                if (playlist is not null)
                {
                    added.AddRange(MasterPlaylist.Variants(playlist));
                }
            }

            var merged = MasterPlaylist.Merge(text, added);
            executed.Result = new FileContentResult(Encoding.UTF8.GetBytes(merged), original.ContentType);
            _log.Started(Arg<string>(context, "playSessionId") ?? string.Empty, TitleOf(context), ClientOf(context), MasterPlaylist.Variants(merged).Count);
        }
        catch (Exception ex)
        {
            // la playlist de Jellyfin reste celle servie
            _logger.LogWarning(ex, "Adaptive Bitrate: could not build the quality ladder; Jellyfin's playlist is served as is");
        }
    }

    /// <summary>
    /// Playlist maîtresse d'une marche, produite par l'action de Jellyfin. Jellyfin construit l'adresse de la qualité à
    /// partir de la requête HTTP (pas des paramètres de l'action) : la requête est remplacée le temps de l'appel.
    /// </summary>
    private static async Task<string?> RungPlaylist(ActionExecutingContext context, MethodInfo method, Rung rung)
    {
        var request = context.HttpContext.Request;
        var saved = request.QueryString;
        var query = QueryHelpers.ParseQuery(saved.Value);
        var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            // largeur aussi : sinon Jellyfin garde la largeur de la source et sa propre table de résolutions peut rendre une
            // marche plus basse en 1080p (mesuré : 1920x1080 à 3 Mb/s, moins net qu'un 720p)
            ["videoBitRate"] = rung.VideoBitrate, ["maxHeight"] = rung.MaxHeight, ["maxWidth"] = rung.MaxWidth > 0 ? rung.MaxWidth : null, ["width"] = null, ["height"] = null,
            ["allowVideoStreamCopy"] = false, ["enableAutoStreamCopy"] = false, ["enableAdaptiveBitrateStreaming"] = false,
        };
        var q = new Dictionary<string, StringValues>(query, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
        {
            q.Remove(k);
            if (v is not null)
            {
                // même écriture que les adresses de Jellyfin : VideoBitrate=…, AllowVideoStreamCopy=false
                q[char.ToUpperInvariant(k[0]) + k[1..]] = v is bool b ? (b ? "true" : "false") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var args = method.GetParameters().Select(p =>
            overrides.TryGetValue(p.Name!, out var o) ? o
            : context.ActionArguments.TryGetValue(p.Name!, out var a) ? a
            : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        try
        {
            request.QueryString = QueryString.Create(q.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))));
            if (method.Invoke(context.Controller, args) is not Task task)
            {
                return null;
            }

            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result is FileContentResult f ? Encoding.UTF8.GetString(f.FileContents) : null;
        }
        finally
        {
            request.QueryString = saved;
        }
    }

    /// <summary>Le lecteur passe à une qualité : noté pour la page de configuration (débit vidéo de la marche, 0 = copie).</summary>
    private void Track(ActionExecutingContext context)
    {
        try
        {
            var copy = Arg<bool?>(context, "allowVideoStreamCopy") != false && Arg<int?>(context, "videoBitRate") is null;
            _log.Requested(Arg<string>(context, "playSessionId") ?? string.Empty, copy ? 0 : Arg<int?>(context, "videoBitRate") ?? 0, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Adaptive Bitrate: variant request not tracked");
        }
    }

    private PlaybackRequest Describe(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        bool local = _network.IsInLocalNetwork(http.GetNormalizedRemoteIP());
        bool live = !string.IsNullOrEmpty(Arg<string>(context, "liveStreamId"));
        var user = Guid.TryParse(http.User.FindFirst("Jellyfin-UserId")?.Value, out var g) ? _users.GetUserById(g) : null;
        bool transcode = user is not null && user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableVideoPlaybackTranscoding);
        return new PlaybackRequest(local, live, ClientOf(context), transcode);
    }

    /// <summary>Vidéo d'origine : dimensions, débit et images par seconde de la source lue.</summary>
    private SourceVideo Source(ActionExecutingContext context)
    {
        if (Arg<Guid>(context, "itemId") is var id && id != Guid.Empty && _library.GetItemById(id) is BaseItem item)
        {
            var sourceId = Arg<string>(context, "mediaSourceId");
            var source = _sources.GetStaticMediaSources(item, false).FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                ?? _sources.GetStaticMediaSources(item, false).FirstOrDefault();
            var video = source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video is not null)
            {
                int bitrate = video.BitRate ?? source!.Bitrate ?? 0;
                return new SourceVideo(video.Width ?? 0, video.Height ?? 0, bitrate, video.RealFrameRate ?? video.AverageFrameRate ?? 0);
            }
        }

        return new SourceVideo(0, 0, 0, 0);
    }

    private string TitleOf(ActionExecutingContext context)
        => Arg<Guid>(context, "itemId") is var id && id != Guid.Empty && _library.GetItemById(id) is BaseItem item ? item.Name ?? string.Empty : string.Empty;

    private static string ClientOf(ActionExecutingContext context) => context.HttpContext.User.FindFirst("Jellyfin-Client")?.Value ?? string.Empty;

    private static T? Arg<T>(ActionExecutingContext c, string name)
        => c.ActionArguments.TryGetValue(name, out var v) && v is T t ? t : default;
}
