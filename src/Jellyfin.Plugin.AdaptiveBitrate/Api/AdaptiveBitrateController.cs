using System;
using System.Linq;
using System.Reflection;
using AdaptiveBitrate.Core;
using Jellyfin.Data;
using Jellyfin.Plugin.AdaptiveBitrate.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdaptiveBitrate.Api;

/// <summary>État du plugin (administration) et textes de la page.</summary>
[ApiController]
[Route("AdaptiveBitrate")]
public sealed class AdaptiveBitrateController : ControllerBase
{
    private readonly SwitchLog _log;
    private readonly Sessions _sessions;
    private readonly IUserManager _users;

    public AdaptiveBitrateController(SwitchLog log, Sessions sessions, IUserManager users)
    {
        _log = log;
        _sessions = sessions;
        _users = users;
    }

    /// <summary>Mesure du serveur pour une lecture (débit d'envoi, avance du lecteur, décisions), ou null.</summary>
    private object? Measure(string session)
    {
        if (_sessions.Get(session) is not { } s)
        {
            return null;
        }

        var o = Plugin.Instance?.Configuration.GovernorOptions() ?? new GovernorOptions().Sanitized();
        lock (s)
        {
            var g = s.Governor;
            return new
            {
                rateKbps = g.Rate is double r ? (int)(r / 1000) : (int?)null, lead = g.Lead is double l ? Math.Round(l, 1) : (double?)null,
                served = g.Current, held = g.Held, anticipated = g.Anticipated, samples = g.Samples,
                paced = g.Paced, skipped = g.Skipped, segment = Math.Round(g.SegmentSeconds, 1),
                encoderWait = g.EncoderLoad is double w ? Math.Round(w * g.SegmentSeconds, 2) : (double?)null,
                // valeurs réellement appliquées (réglées, ou automatiques pour cette lecture)
                margin = Math.Round(g.MarginFor(o), 2), minChange = Math.Round(g.MinChangeFor(o), 1), minSamples = g.MinSamplesFor(o),
                startup = Governor.Startup is double st ? Math.Round(st, 1) : (double?)null,
            };
        }
    }

    /// <summary>Script de la surcouche de diagnostic. Public, comme tout fichier de l'interface web ; ne montre rien sans réglage.</summary>
    [HttpGet("overlay.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult OverlayScript()
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.overlay.js");
        if (s is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(s, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Surcouche de diagnostic : la lecture du compte connecté (qualités proposées, changements) et les textes de la
    /// surcouche ; refusé (403) si la surcouche n'est pas ouverte à ce compte. Une lecture d'un autre compte n'est jamais
    /// renvoyée : seule celle dont l'identifiant est donné, et seulement si elle est connue du journal.
    /// </summary>
    [HttpGet("Session")]
    [Authorize]
    public ActionResult Session([FromQuery] string? playSessionId, [FromQuery] string? lang)
    {
        var cfg = Plugin.Instance?.Configuration;
        var user = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var g) ? _users.GetUserById(g) : null;
        bool admin = user is not null && user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator);
        bool allowed = cfg is { Enabled: true } && (cfg.DebugOverlay == DebugOverlay.Everyone || (cfg.DebugOverlay == DebugOverlay.Admins && admin));
        if (!allowed)
        {
            return StatusCode(403);
        }

        var s = string.IsNullOrEmpty(playSessionId) ? null : _log.Get(playSessionId);
        return Ok(new
        {
            texts = Texts.Dictionary(lang),
            session = s is null ? null : new
            {
                title = s.Title, offers = s.Offers.Select(o => new { kbps = o.Kbps, resolution = o.Resolution }),
                steps = s.Steps.Select(p => new { at = p.At, kbps = p.VideoBitrate / 1000 }), down = s.Down, up = s.Up,
                server = Measure(s.Session),
            },
        });
    }

    /// <summary>Lectures récentes et leurs changements de qualité ; compatibilité avec Jellyfin.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Status([FromQuery] string? lang)
    {
        var version = typeof(MediaBrowser.Common.IApplicationHost).Assembly.GetName().Version ?? new Version(0, 0);
        bool tested = new Version(version.Major, version.Minor) >= Plugin.TestedFrom && new Version(version.Major, version.Minor) <= Plugin.TestedTo;
        return Ok(new
        {
            jellyfin = version.ToString(),
            tested,
            compatible = !HlsFilter.Incompatible,
            lastSkip = HlsFilter.LastSkip is { } s ? Texts.T(s, lang) : null,
            startup = Governor.Startup is double st ? Math.Round(st, 1) : (double?)null,
            startupSamples = Governor.RecentStartups().Select(x => Math.Round(x, 3)),
            sessions = _log.Recent().Select(x => new
            {
                title = x.Title, client = x.Client, offered = x.Offered, down = x.Down, up = x.Up,
                offers = x.Offers.Select(o => new { kbps = o.Kbps, resolution = o.Resolution }),
                server = Measure(x.Session),
                steps = x.Steps.Select(p => new { at = p.At, kbps = p.VideoBitrate / 1000 }),
            }),
        });
    }

    /// <summary>Textes de la page dans la langue demandée (dictionnaire complet ; français : vide).</summary>
    [HttpGet("I18n")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult I18n([FromQuery] string? lang)
    {
        var cfg = Plugin.Instance?.Configuration;
        string l = cfg?.Language switch
        {
            Configuration.UiLanguage.French => "fr",
            Configuration.UiLanguage.English => "en",
            Configuration.UiLanguage.German => "de",
            Configuration.UiLanguage.Spanish => "es",
            _ => Texts.Normalize(lang),
        };
        return Ok(new { lang = l, dict = Texts.Dictionary(l) });
    }
}
