using System;
using System.Linq;
using System.Threading.Tasks;
using AdaptiveBitrate.Core;
using Jellyfin.Data;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>
/// Option « flux adaptatif plutôt que lecture directe » (coupée par défaut) : pour un lecteur concerné, la demande de
/// lecture (/Items/{id}/PlaybackInfo) est faite sans lecture directe du fichier. Jellyfin propose alors un flux HLS (vidéo
/// copiée quand c'est possible, donc presque sans calcul), auquel l'échelle de qualités s'ajoute : une baisse du réseau
/// fait descendre la qualité au lieu d'arrêter l'image. Le paramètre de la requête l'emporte sur celui du corps. Seulement
/// pour une vidéo : un morceau de musique ou un livre audio gardent leur lecture directe (aucune échelle pour le son seul),
/// une chaîne en direct aussi (pas d'échelle pour un flux en direct).
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IUserManager _users;
    private readonly INetworkManager _network;
    private readonly ILibraryManager _library;

    public PlaybackInfoFilter(IUserManager users, INetworkManager network, ILibraryManager library)
    {
        _users = users;
        _network = network;
        _library = library;
    }

    /// <inheritdoc />
    public int Order => int.MaxValue - 50;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor d && d.ControllerName == "MediaInfo" && d.ActionName == "GetPostedPlaybackInfo"
            && Plugin.Instance?.Configuration is { PreferAdaptive: true } cfg
            && d.MethodInfo.GetParameters().Any(p => p.Name == "enableDirectPlay")
            && context.ActionArguments.TryGetValue("itemId", out var id) && id is Guid itemId && _library.GetItemById(itemId) is Video
            && !(context.ActionArguments.TryGetValue("liveStreamId", out var live) && live is string { Length: > 0 }))
        {
            var http = context.HttpContext;
            bool local = _network.IsInLocalNetwork(http.GetNormalizedRemoteIP());
            var user = Guid.TryParse(http.User.FindFirst("Jellyfin-UserId")?.Value, out var g) ? _users.GetUserById(g) : null;
            bool transcode = user is not null && user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableVideoPlaybackTranscoding);
            var request = new PlaybackRequest(local, false, http.User.FindFirst("Jellyfin-Client")?.Value, transcode);
            if (Policy.Skip(cfg.Enabled, cfg.Scope, Policy.ParseClients(cfg.ExcludedClients), request) is null)
            {
                context.ActionArguments["enableDirectPlay"] = false;
            }
        }

        await next().ConfigureAwait(false);
    }
}
