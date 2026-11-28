using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AdaptiveBitrate.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>Adaptive Bitrate : une vraie échelle de qualités dans les flux HLS de Jellyfin, pour changer de qualité sans coupure.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Identifiant fixe du plugin.</summary>
    public static readonly Guid PluginGuid = Guid.Parse("3c8e5a71-2f4b-4d96-a1e0-7b9d64c2f815");

    /// <summary>Versions de Jellyfin (majeure.mineure) testées.</summary>
    public static readonly Version TestedFrom = new(12, 2), TestedTo = new(12, 4);

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Migrate();
    }

    /// <summary>Anciennes valeurs par défaut restées telles quelles : les nouvelles (une valeur réglée à la main reste).</summary>
    private void Migrate()
    {
        var c = Configuration;
        if (c.SettingsVersion >= 2)
        {
            return;
        }

        // 1.0.0 : rapport fixe de 0,6 entre qualités ; 1.0.1 : écart maximal de 0,5, l'échelle descend toujours jusqu'au minimum
        if (c.SettingsVersion < 1 && Math.Abs(c.Step - 0.6) < 1e-9)
        {
            c.Step = 0.5;
        }

        // 1.2.0 : réactivité du pilotage automatique (anciens défauts fixes : 10 s, 1,25, 2 segments)
        if (c.MinChangeSeconds == 10)
        {
            c.MinChangeSeconds = 0;
        }

        if (Math.Abs(c.Margin - 1.25) < 1e-9)
        {
            c.Margin = 0;
        }

        if (c.MinSamples == 2)
        {
            c.MinSamples = 0;
        }

        c.SettingsVersion = 2;
        SaveConfiguration();
    }

    /// <summary>Instance courante (null avant le chargement).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Adaptive Bitrate";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description => "Real adaptive bitrate for Jellyfin's HLS streams: the player changes quality with the network, without a cut.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "AdaptiveBitrate",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "network_check",
            DisplayName = "Adaptive Bitrate",
        },
    };
}
