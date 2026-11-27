using System;
using System.Linq;
using AdaptiveBitrate.Core;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdaptiveBitrate.Api;

/// <summary>État du plugin (administration) et textes de la page.</summary>
[ApiController]
[Route("AdaptiveBitrate")]
public sealed class AdaptiveBitrateController : ControllerBase
{
    private readonly SwitchLog _log;

    public AdaptiveBitrateController(SwitchLog log) => _log = log;

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
            sessions = _log.Recent().Select(x => new
            {
                title = x.Title, client = x.Client, offered = x.Offered, down = x.Down, up = x.Up,
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
