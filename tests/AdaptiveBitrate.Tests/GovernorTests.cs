using System;
using AdaptiveBitrate.Core;
using Xunit;

namespace AdaptiveBitrate.Tests;

public class GovernorTests
{
    // échelle d'un fichier à 8 Mb/s : 8,1 · 4,5 · 2,5 · 1,5 · 0,9 Mb/s (débits annoncés, audio compris)
    private static readonly int[] Ladder = { 8_100_000, 4_500_000, 2_500_000, 1_500_000, 900_000 };
    private static readonly GovernorOptions On = new() { Enabled = true, AnticipateDown = true };

    /// <summary>Envoi d'un segment de 4 s à la qualité <paramref name="rung"/> sur une connexion à <paramref name="mbps"/> Mb/s.</summary>
    private static void Send(Governor g, int rung, double mbps) => g.Sent(Ladder[rung] / 8 * 4, Ladder[rung] * 4 / (mbps * 1e6));

    [Fact]
    public void OffItOnlyMeasuresAndServesWhatThePlayerAsks()
    {
        var g = new Governor(Ladder);
        Assert.Equal(new Decision(0, Verdict.AsRequested), g.Decide(0, 0, 4, 0, new GovernorOptions()));
        Send(g, 0, 20);
        Assert.InRange(g.Rate!.Value, 19e6, 21e6);
        Assert.Equal(new Decision(3, Verdict.AsRequested), g.Decide(3, 1, 4, 4, new GovernorOptions()));
        Assert.Equal(new Decision(0, Verdict.AsRequested), g.Decide(0, 2, 4, 5, new GovernorOptions()));
    }

    [Fact]
    public void APlayerSteppingDownIsAlwaysServedAtOnce()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        Assert.Equal(new Decision(2, Verdict.AsRequested), g.Decide(2, 1, 4, 1, On));   // même juste après un changement
        Assert.Equal(new Decision(4, Verdict.AsRequested), g.Decide(4, 2, 4, 2, On));
    }

    [Fact]
    public void GoingBackUpWaitsAfterADropThenClimbsOneStepAtATimeWhenTheMeasureAllows()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        g.Decide(3, 1, 4, 4, On);                                                   // descente à 1,5 Mb/s
        Send(g, 3, 30); Send(g, 3, 30);
        Assert.Equal(new Decision(3, Verdict.Held), g.Decide(0, 2, 4, 10, On));     // trop tôt après la descente
        Assert.Equal(new Decision(2, Verdict.Held), g.Decide(0, 3, 4, 35, On));     // 31 s après : une marche seulement
        Assert.Equal(new Decision(2, Verdict.Held), g.Decide(0, 4, 4, 40, On));     // 10 s au moins entre deux changements
        Send(g, 2, 30); Send(g, 2, 30); Send(g, 2, 30);
        Assert.Equal(new Decision(1, Verdict.Held), g.Decide(0, 5, 4, 46, On));
        Assert.Equal(new Decision(0, Verdict.AsRequested), g.Decide(0, 6, 4, 57, On));
        Assert.Equal(2, g.Held);                                                    // deux remontées retenues entièrement
    }

    [Fact]
    public void NoClimbWhenTheMeasuredRateDoesNotLeaveTheMargin()
    {
        var g = new Governor(Ladder);
        g.Decide(3, 0, 4, 0, On);
        Send(g, 3, 2.8); Send(g, 3, 2.8);                                           // 2,8 Mb/s : 2,5 × 1,25 ne passe pas
        Assert.Equal(new Decision(3, Verdict.Held), g.Decide(0, 1, 4, 100, On));
    }

    [Fact]
    public void WhenTheRateNoLongerSufficesTheServerStepsDownAheadToAQualityThatFits()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        Send(g, 0, 2.2); Send(g, 0, 2.2);                                           // le réseau tombe à 2,2 Mb/s
        var d = g.Decide(0, 1, 4, 20, On);                                          // le lecteur ne s'en est pas encore aperçu
        Assert.Equal(new Decision(3, Verdict.Anticipated), d);                      // 1,5 × 1,25 < 2,2 : la marche qui passe
        Assert.Equal(1, g.Anticipated);
        // une seule mesure : pas de décision (garde-fou)
        var h = new Governor(Ladder);
        h.Decide(0, 0, 4, 0, On);
        Send(h, 0, 2.2);
        Assert.Equal(new Decision(0, Verdict.AsRequested), h.Decide(0, 1, 4, 20, On));
        // descente anticipée coupée : le serveur laisse faire le lecteur
        var k = new Governor(Ladder);
        k.Decide(0, 0, 4, 0, On);
        Send(k, 0, 2.2); Send(k, 0, 2.2);
        Assert.Equal(new Decision(0, Verdict.AsRequested), k.Decide(0, 1, 4, 20, new GovernorOptions { Enabled = true, AnticipateDown = false }));
    }

    [Fact]
    public void LeadFollowsTheRequestRhythmAndRestartsAfterASeekOrAPause()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        g.Decide(0, 1, 4, 0.5, On);
        g.Decide(0, 2, 4, 1, On);
        Assert.InRange(g.Lead!.Value, 10.9, 11.1);                                  // 12 s demandées en 1 s
        g.Decide(0, 3, 4, 9, On);
        Assert.InRange(g.Lead!.Value, 6.9, 7.1);                                    // il consomme : 16 s demandées en 9 s
        g.Decide(0, 40, 4, 10, On);                                                 // saut : repart de zéro
        Assert.InRange(g.Lead!.Value, 3.9, 4.1);
        Assert.Equal(0, g.Samples);
        g.Decide(0, 41, 4, 60, On);                                                 // longue pause : repart de zéro
        Assert.InRange(g.Lead!.Value, 3.9, 4.1);
    }

    [Fact]
    public void OutOfRangeSettingsAreBounded()
    {
        var s = new GovernorOptions { HoldAfterDownSeconds = -5, MinChangeSeconds = double.NaN, Margin = 0.2, MinSamples = 99 }.Sanitized();
        Assert.Equal((0.0, 0.0, 1.0, 10), (s.HoldAfterDownSeconds, s.MinChangeSeconds, s.Margin, s.MinSamples));
        Assert.Equal(0.0, new GovernorOptions { Margin = double.NaN }.Sanitized().Margin);    // 0 = automatique
    }

    [Fact]
    public void AutomaticValuesFollowTheSegmentLengthAndTheConnectionsSteadiness()
    {
        var auto = new GovernorOptions { Enabled = true };
        var g = new Governor(Ladder);
        g.Decide(0, 0, 3, 0, auto);
        Assert.Equal(7.5, g.MinChangeFor(auto));          // 2,5 segments de 3 s
        Assert.Equal(2, g.MinSamplesFor(auto));           // 6 s de vidéo
        Assert.Equal(1.25, g.MarginFor(auto));            // moins de trois mesures
        var h = new Governor(Ladder);
        h.Decide(0, 0, 20, 0, auto);
        Assert.Equal(30, h.MinChangeFor(auto));           // plafond
        Assert.Equal(1, h.MinSamplesFor(auto));
        // connexion régulière : petite marge ; connexion irrégulière : grande marge
        for (int i = 0; i < 6; i++) { g.Sent(1_000_000, 0.5); }
        Assert.InRange(g.MarginFor(auto), 1.09, 1.11);
        var k = new Governor(Ladder);
        foreach (var t in new[] { 0.2, 0.9, 0.3, 1.2, 0.25, 0.8 }) { k.Sent(1_000_000, t); }
        Assert.InRange(k.MarginFor(auto), 1.5, 2.0);
        // une valeur réglée l'emporte
        Assert.Equal(1.6, g.MarginFor(new GovernorOptions { Margin = 1.6 }));
        Assert.Equal(12, g.MinChangeFor(new GovernorOptions { MinChangeSeconds = 12 }));
    }

    [Fact]
    public void SteppingDownAheadWaitsUntilThePlayersLeadNoLongerCoversAnEncoderStart()
    {
        Governor.ResetStartup();
        Governor.ReportStartup(2.0);
        var o = new GovernorOptions { Enabled = true, AnticipateDown = true };
        // le lecteur a plus de 40 s d'avance : le débit baisse (6 Mb/s pour 8,1), mais il reste le temps de voir venir
        var g = new Governor(Ladder);
        for (int i = 0; i < 12; i++) { g.Decide(0, i, 4, i * 0.1, o); }
        Send(g, 0, 6); Send(g, 0, 6);
        Assert.Equal(Verdict.AsRequested, g.Decide(0, 12, 4, 5, o).Verdict);
        // un segment toutes les 9 s : l'avance fond de 5 s à chaque demande ; le serveur descend d'avance seulement
        // quand elle ne couvre plus le démarrage (2 s) + le segment suivant à ce débit (5,4 s) + un segment (4 s)
        double now = 5;
        Verdict v = Verdict.AsRequested;
        for (int seg = 13; seg < 30 && v == Verdict.AsRequested; seg++)
        {
            now += 9;
            v = g.Decide(0, seg, 4, now, o).Verdict;
            if (v == Verdict.AsRequested) { Assert.True(g.Lead >= 11.4, $"lead {g.Lead}"); }
        }

        Assert.Equal(Verdict.Anticipated, v);
        Assert.True(g.Lead < 11.4);
        Assert.InRange(Governor.Startup!.Value, 1.99, 2.01);
        Governor.ReportStartup(-1);
        Governor.ReportStartup(500);                       // valeurs absurdes ignorées
        Assert.InRange(Governor.Startup!.Value, 1.99, 2.01);
        Governor.ResetStartup();
    }
}
