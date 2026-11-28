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
public sealed class HlsFilter : IAsyncActionFilter, IAsyncResultFilter, IOrderedFilter
{
    /// <summary>Contrôleur des flux HLS de Jellyfin.</summary>
    public const string Controller = "DynamicHls";

    /// <summary>Playlist maîtresse d'une vidéo.</summary>
    public const string MasterAction = "GetMasterHlsVideoPlaylist";

    /// <summary>Segment d'une qualité.</summary>
    public const string SegmentAction = "GetHlsVideoSegment";

    private const string ItemKey = "AdaptiveBitrate.Session";
    private const string AskedKey = "AdaptiveBitrate.Asked";
    private const string FreshKey = "AdaptiveBitrate.Fresh";
    private const string SegmentKey = "AdaptiveBitrate.Segment";

    /// <summary>Paramètres de l'action dont dépend le plugin (vérifiés avant tout appel).</summary>
    public static readonly string[] Required = { "itemId", "mediaSourceId", "videoBitRate", "maxHeight", "maxWidth", "width", "height", "allowVideoStreamCopy", "enableAutoStreamCopy", "enableAdaptiveBitrateStreaming", "liveStreamId", "playSessionId" };

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _sources;
    private readonly IUserManager _users;
    private readonly INetworkManager _network;
    private readonly SwitchLog _log;
    private readonly Sessions _sessions;
    private readonly ILogger<HlsFilter> _logger;
    private bool _warned;

    public HlsFilter(ILibraryManager library, IMediaSourceManager sources, IUserManager users, INetworkManager network, SwitchLog log, Sessions sessions, ILogger<HlsFilter> logger)
    {
        _library = library;
        _sources = sources;
        _users = users;
        _network = network;
        _log = log;
        _sessions = sessions;
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

        if (d.ActionName == SegmentAction)
        {
            // premier segment d'une qualité : Jellyfin démarre un encodage et attend le segment ; ce temps, mesuré sur
            // ce serveur, règle le moment de la descente anticipée (mode automatique)
            context.HttpContext.Items[AskedKey] = Now();
            context.HttpContext.Items[SegmentKey] = Arg<int>(context, "segmentId");
            context.HttpContext.Items[FreshKey] = Segment(context);
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
            // paramètres de chaque qualité, la qualité de Jellyfin comprise (pour servir une qualité à la place d'une autre)
            var top = MasterPlaylist.Variants(text).FirstOrDefault();
            var known = new List<RungParams>();
            if (top is not null)
            {
                var p0 = new RungParams { Bandwidth = top.Bandwidth };
                foreach (var k in RungParams.Keys)
                {
                    p0.Values[k] = context.ActionArguments.TryGetValue(k, out var v) ? v : null;
                }

                known.Add(p0);
            }

            foreach (var rung in rungs)
            {
                var values = Overrides(rung);
                var playlist = await RungPlaylist(context, d.MethodInfo, values).ConfigureAwait(false);
                var found = playlist is null ? new List<Variant>() : MasterPlaylist.Variants(playlist);
                added.AddRange(found);
                if (found.Count > 0)
                {
                    var p = new RungParams { Bandwidth = found[0].Bandwidth };
                    foreach (var k in RungParams.Keys)
                    {
                        p.Values[k] = values.GetValueOrDefault(k);
                    }

                    known.Add(p);
                }
            }

            var merged = MasterPlaylist.Merge(text, added);
            executed.Result = new FileContentResult(Encoding.UTF8.GetBytes(merged), original.ContentType);
            var offers = MasterPlaylist.Variants(merged).Select(v => new Offer((int)(v.Bandwidth / 1000), MasterPlaylist.Attribute(v.Info, "RESOLUTION") ?? string.Empty)).ToList();
            var session = Arg<string>(context, "playSessionId") ?? string.Empty;
            _log.Started(session, TitleOf(context), ClientOf(context), offers, UserOf(context));
            _sessions.Start(session, known.OrderByDescending(x => x.Bandwidth).ToList());
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
    /// <summary>Paramètres d'une marche : débit, taille, vidéo jamais copiée.</summary>
    private static Dictionary<string, object?> Overrides(Rung rung) => new(StringComparer.OrdinalIgnoreCase)
    {
        // largeur aussi : sinon Jellyfin garde la largeur de la source et sa propre table de résolutions peut rendre une
        // marche plus basse en 1080p (mesuré : 1920x1080 à 3 Mb/s, moins net qu'un 720p)
        ["videoBitRate"] = rung.VideoBitrate, ["maxHeight"] = rung.MaxHeight, ["maxWidth"] = rung.MaxWidth > 0 ? rung.MaxWidth : null, ["width"] = null, ["height"] = null,
        ["allowVideoStreamCopy"] = false, ["enableAutoStreamCopy"] = false, ["enableAdaptiveBitrateStreaming"] = false,
    };

    /// <summary>Requête dont les paramètres donnés sont remplacés (même écriture que les adresses de Jellyfin).</summary>
    private static QueryString WithValues(QueryString original, IReadOnlyDictionary<string, object?> values)
    {
        var q = new Dictionary<string, StringValues>(QueryHelpers.ParseQuery(original.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            q.Remove(k);
            if (v is not null)
            {
                q[char.ToUpperInvariant(k[0]) + k[1..]] = v is bool b ? (b ? "true" : "false") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return QueryString.Create(q.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))));
    }

    private static async Task<string?> RungPlaylist(ActionExecutingContext context, MethodInfo method, Dictionary<string, object?> overrides)
    {
        var request = context.HttpContext.Request;
        var saved = request.QueryString;
        var args = method.GetParameters().Select(p =>
            overrides.TryGetValue(p.Name!, out var o) ? o
            : context.ActionArguments.TryGetValue(p.Name!, out var a) ? a
            : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        try
        {
            request.QueryString = WithValues(saved, overrides);
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

    /// <summary>
    /// Segment demandé : la qualité à servir (celle du lecteur, sauf décision du serveur : remontée retenue, descente
    /// anticipée), notée pour la page et la surcouche. Toutes les qualités ont les mêmes segments aux mêmes instants
    /// (vérifié par le test d'intégration) et Jellyfin ne lit que les paramètres de qualité pour trouver le fichier :
    /// changer ces paramètres suffit. Lecture inconnue ou forme inattendue : la demande du lecteur passe telle quelle.
    /// Vrai quand la qualité servie change (ou au premier segment) : un nouvel encodage démarre.
    /// </summary>
    private bool Segment(ActionExecutingContext context)
    {
        try
        {
            var session = _sessions.Get(Arg<string>(context, "playSessionId"));
            if (session is null || Plugin.Instance?.Configuration is not { Enabled: true } cfg)
            {
                return false;
            }

            int requested = session.IndexOf(context.ActionArguments);
            if (requested < 0)
            {
                return false;
            }

            double seconds = Arg<long>(context, "actualSegmentLengthTicks") / 1e7;
            // segments fMP4 : chaque encodage a son propre en-tête d'initialisation (-1.mp4), que le lecteur ne recharge
            // qu'en changeant lui-même de qualité ; un segment d'une autre qualité derrière cet en-tête casserait l'image.
            // Le serveur ne pilote donc que les segments TS (paramètres de l'image dans chaque segment) ; en fMP4, il mesure.
            var options = cfg.GovernorOptions();
            if (!string.Equals(Arg<string>(context, "container"), "ts", StringComparison.OrdinalIgnoreCase))
            {
                options.Enabled = false;
                session.Pilotable = false;
            }

            Decision decision;
            bool fresh;
            lock (session)
            {
                decision = session.Governor.Decide(requested, Arg<int>(context, "segmentId"), seconds > 0 ? seconds : 3, Now(), options);
                fresh = decision.Rung != session.LastServed;
                session.LastServed = decision.Rung;
            }

            var served = session.Rungs[decision.Rung];
            if (decision.Rung != requested)
            {
                foreach (var (k, v) in served.Values)
                {
                    context.ActionArguments[k] = v;
                }

                context.HttpContext.Request.QueryString = WithValues(context.HttpContext.Request.QueryString, served.Values);
            }

            context.HttpContext.Items[ItemKey] = session;
            var kbps = served.Values.GetValueOrDefault("allowVideoStreamCopy") is false ? (int)(served.Bandwidth / 1000) : 0;
            _log.Requested(Arg<string>(context, "playSessionId") ?? string.Empty, kbps * 1000, DateTime.UtcNow);
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Adaptive Bitrate: segment request left as is");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.Items[ItemKey] is not Session session)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // envoi : son début (après l'attente de l'encodeur) et sa taille ; sa durée de livraison se lit à la demande du
        // segment suivant (Governor.Sending). Le temps de l'envoi lui-même ne dit rien : le système l'avale dans son tampon.
        double start = Now();
        await next().ConfigureAwait(false);
        long bytes = context.HttpContext.Response.ContentLength ?? (context.Result is PhysicalFileResult f && System.IO.File.Exists(f.FileName) ? new System.IO.FileInfo(f.FileName).Length : 0);
        double waited = context.HttpContext.Items[AskedKey] is double asked ? start - asked : -1;
        if (waited >= 0 && context.HttpContext.Items[FreshKey] is true)
        {
            // premier segment d'une qualité : Jellyfin a démarré un encodage et attendu ce segment
            Governor.ReportStartup(waited);
        }

        lock (session)
        {
            session.Governor.Sending(context.HttpContext.Items[SegmentKey] is int seg ? seg : -10, bytes, start, waited);
        }
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

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
            var all = _sources.GetStaticMediaSources(item, false);
            var source = all.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
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

    private static string UserOf(ActionExecutingContext context) => context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value ?? string.Empty;

    private static string ClientOf(ActionExecutingContext context) => context.HttpContext.User.FindFirst("Jellyfin-Client")?.Value ?? string.Empty;

    private static T? Arg<T>(ActionExecutingContext c, string name)
        => c.ActionArguments.TryGetValue(name, out var v) && v is T t ? t : default;
}
