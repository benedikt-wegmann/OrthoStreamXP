using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>A step of Dynamic quality: from <paramref name="AboveFt"/> above the ground, textures are no sharper than <paramref name="Plan"/>'s.</summary>
/// <param name="Name">The preset it looks like, for the log ("Sharp").</param>
internal sealed record DetailTier(string Name, double AboveFt, TexturePlan Plan);

/// <summary>The heights of Dynamic quality's steps, in feet above the ground; Balanced's is null when it is not used.</summary>
internal sealed record DynamicOptions(double SharpAboveFt = DynamicOptions.DefaultSharpAboveFt, double? BalancedAboveFt = DynamicOptions.DefaultBalancedAboveFt)
{
    public const double DefaultSharpAboveFt = 5000, DefaultBalancedAboveFt = 25000;

    /// <summary>The steps, lowest first, that are coarser than <paramref name="mount"/> (a step no coarser is no saving).</summary>
    public IReadOnlyList<DetailTier> TiersBelow(TexturePlan mount)
    {
        var tiers = new List<DetailTier> { new("Sharp", SharpAboveFt, new TexturePlan(-1)) };
        if (BalancedAboveFt is { } balanced) tiers.Add(new DetailTier("Balanced", balanced, new TexturePlan(-2)));
        return [.. tiers.Where(t => t.Plan.SizeFor(16) < mount.SizeFor(16) || t.Plan.SizeFor(18) < mount.SizeFor(18))];
    }
}

/// <summary>
/// Dynamic quality: the photos' detail follows the camera's height above the ground, as the presets would (Extra
/// near the ground, Sharp higher up, Balanced at airliner heights). X-Plane wants nearly the same full-detail
/// textures at 10,000 ft as at 1,000 ft, so most of a climb and cruise downloads detail too fine to see.
/// <para>
/// X-Plane reads each level of a texture once while it keeps it loaded: a level given with less detail stays that
/// way. So a step down comes early: in a descent the height <see cref="Lookahead"/> ahead counts, and a step is left
/// only <see cref="MarginFt"/> below its height, so terrain passing underneath does not flip it to and fro.
/// </para>
/// Offered every camera position by the position listener; read by the file-system threads.
/// </summary>
internal sealed class AltitudeDetail
{
    /// <summary>How far ahead a descent is looked at: textures read at full detail this long before they are passed close by.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromMinutes(4);
    /// <summary>A step is left this far below its height.</summary>
    public const double MarginFt = 500;
    /// <summary>A sink slower than this (feet per minute) is not a descent: turbulence or a level-off.</summary>
    public const double DescentFpm = -300;
    private const double FeetPerMetre = 1 / 0.3048;
    /// <summary>The vertical speed is measured over this much of the latest positions.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<DetailTier> _tiers;
    private readonly string _fullName;
    private readonly Queue<(DateTime Utc, double HeightM)> _recent = new();
    private int _index = -1;
    private volatile DetailTier? _tier;

    /// <param name="tiers">The steps, lowest first.</param>
    /// <param name="fullName">The quality below the first step, for the log ("Extra").</param>
    public AltitudeDetail(IReadOnlyList<DetailTier> tiers, string fullName)
    {
        _tiers = [.. tiers.OrderBy(t => t.AboveFt)];
        _fullName = fullName;
    }

    /// <summary>The step the textures are held to now; null for the full detail of the chosen quality (also before X-Plane sends a position).</summary>
    public DetailTier? Tier => _tier;

    public IReadOnlyList<DetailTier> Tiers => _tiers;

    /// <summary>The camera's latest position; returns the new step when it changed (logged), otherwise null.</summary>
    public DetailTier? Offer(CameraReport camera)
    {
        if (camera.Jumped) _recent.Clear();
        double aglFt = camera.HeightAglM * FeetPerMetre;
        double? fpm = ClimbRate(camera);
        // In a descent, the height a few minutes ahead (never below the ground): its textures are read at full detail before they are reached.
        double heightFt = fpm is < DescentFpm ? Math.Max(0, Math.Min(aglFt, aglFt + fpm.Value * Lookahead.TotalMinutes)) : aglFt;

        int index = _index;
        while (index + 1 < _tiers.Count && heightFt >= _tiers[index + 1].AboveFt) index++;
        while (index >= 0 && heightFt < _tiers[index].AboveFt - MarginFt) index--;
        if (index == _index) return null;

        _index = index;
        DetailTier? tier = index < 0 ? null : _tiers[index];
        _tier = tier;
        string why = fpm is < DescentFpm ? $", descending {-fpm.Value:N0} ft/min" : "";
        Log.Step($"Dynamic quality: {tier?.Name ?? _fullName} at {aglFt:N0} ft above the ground{why}");
        return tier;
    }

    /// <summary>Feet per minute over the latest positions (the height above sea level, which terrain does not move), or null until there are enough.</summary>
    private double? ClimbRate(CameraReport camera)
    {
        double heightM = camera.ElevationM ?? camera.HeightAglM;
        _recent.Enqueue((camera.ReceivedUtc, heightM));
        while (_recent.Count > 2 && camera.ReceivedUtc - _recent.Peek().Utc > Window) _recent.Dequeue();
        var (firstUtc, firstM) = _recent.Peek();
        TimeSpan span = camera.ReceivedUtc - firstUtc;
        return span < MinWindow ? null : (heightM - firstM) * FeetPerMetre / span.TotalMinutes;
    }
}
