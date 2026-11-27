using System;
using System.Linq;
using System.Threading.Tasks;
using AdaptiveBitrate.Core;
using Jellyfin.Data;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>
/// Option « flux adaptatif plutôt que lecture directe » (coupée par défaut) : pour un lecteur concerné, la demande de
/// lecture (/Items/{id}/PlaybackInfo) est faite sans lecture directe du fichier. Jellyfin propose alors un flux HLS (vidéo
/// copiée quand c'est possible, donc presque sans calcul), auquel l'échelle de qualités s'ajoute : une baisse du réseau
/// fait descendre la qualité au lieu d'arrêter l'image. Le paramètre de la requête l'emporte sur celui du corps.
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IUserManager _users;
    private readonly INetworkManager _network;

    public PlaybackInfoFilter(IUserManager users, INetworkManager network)
    {
        _users = users;
        _network = network;
    }

    /// <inheritdoc />
    public int Order => int.MaxValue - 50;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor d && d.ControllerName == "MediaInfo" && d.ActionName == "GetPostedPlaybackInfo"
            && Plugin.Instance?.Configuration is { PreferAdaptive: true } cfg
            && d.MethodInfo.GetParameters().Any(p => p.Name == "enableDirectPlay"))
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
