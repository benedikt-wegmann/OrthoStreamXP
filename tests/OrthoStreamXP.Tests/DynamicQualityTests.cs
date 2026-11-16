using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Tests;

/// <summary>Dynamic quality: the step for the camera's height, its settings and its preset.</summary>
public sealed class DynamicQualityTests : IDisposable
{
    private const double Ft = 0.3048;
    private static readonly DateTime Start = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Extra's steps as the preset sets them: Sharp from 5,000 ft, Balanced from 25,000 ft.</summary>
    private static AltitudeDetail ExtraSteps() => new(new DynamicOptions().TiersBelow(new TexturePlan(1)), "Extra");

    private static CameraReport Camera(double seconds, double aglFt, double? mslFt = null, bool jumped = false) =>
        new(50, 17, aglFt * Ft, (mslFt ?? aglFt) * Ft, null, jumped, Start.AddSeconds(seconds));

    // ---------------------------------------------------------------- the step for the height

    [Fact]
    public void A_climb_goes_from_full_detail_to_Sharp_and_then_Balanced()
    {
        AltitudeDetail detail = ExtraSteps();
        Assert.Null(detail.Tier); // before X-Plane sends a position
        string?[] steps = new string?[45];
        for (int i = 0; i < steps.Length; i++) // 2,000 ft/min from the ground to 29,333 ft
        {
            detail.Offer(Camera(i * 20, i * 20 * 2000 / 60.0));
            steps[i] = detail.Tier?.Name;
        }
        Assert.Null(steps[7]);                  // 4,667 ft
        Assert.Equal("Sharp", steps[8]);        // 5,333 ft
        Assert.Equal("Sharp", steps[37]);       // 24,667 ft
        Assert.Equal("Balanced", steps[38]);    // 25,333 ft
    }

    [Fact]
    public void Terrain_passing_underneath_does_not_flip_the_step_to_and_fro()
    {
        AltitudeDetail detail = ExtraSteps();
        // Level at 8,000 ft above the sea; the ground rises and falls beneath.
        detail.Offer(Camera(0, 5200, 8000));
        Assert.Equal("Sharp", detail.Tier?.Name);
        detail.Offer(Camera(10, 4700, 8000)); // within the 500 ft margin
        Assert.Equal("Sharp", detail.Tier?.Name);
        detail.Offer(Camera(20, 5100, 8000));
        Assert.Equal("Sharp", detail.Tier?.Name);
        detail.Offer(Camera(30, 4400, 8000)); // past it
        Assert.Null(detail.Tier);
        detail.Offer(Camera(40, 4900, 8000)); // back up, not yet at the step
        Assert.Null(detail.Tier);
    }

    [Fact]
    public void In_a_descent_full_detail_comes_back_four_minutes_before_the_step()
    {
        AltitudeDetail detail = ExtraSteps();
        detail.Offer(Camera(0, 12_000));
        detail.Offer(Camera(10, 12_000));
        Assert.Equal("Sharp", detail.Tier?.Name);
        // 1,500 ft/min down: four minutes ahead is 6,000 ft lower.
        double? lifted = null;
        for (int i = 1; i <= 120 && lifted is null; i++)
        {
            double agl = 12_000 - i * 2 * 1500 / 60.0;
            detail.Offer(Camera(10 + i * 2, agl));
            if (detail.Tier is null) lifted = agl;
        }
        Assert.NotNull(lifted);
        Assert.InRange(lifted!.Value, 10_000, 10_600); // 4,500 ft + 6,000 ft, not 4,500 ft
    }

    [Fact]
    public void A_slow_sink_is_not_a_descent()
    {
        AltitudeDetail detail = ExtraSteps();
        for (int i = 0; i <= 30; i++) detail.Offer(Camera(i * 2, 5800 - i * 2 * 200 / 60.0)); // 200 ft/min down for a minute
        Assert.Equal("Sharp", detail.Tier?.Name);
    }

    [Fact]
    public void A_new_flight_forgets_the_last_ones_descent()
    {
        AltitudeDetail detail = ExtraSteps();
        for (int i = 0; i <= 10; i++) detail.Offer(Camera(i * 2, 8000 - i * 2 * 3000 / 60.0)); // a steep descent
        Assert.Null(detail.Tier);
        detail.Offer(Camera(30, 12_000, jumped: true)); // placed at cruise: no descent to look ahead along
        Assert.Equal("Sharp", detail.Tier?.Name);
    }

    [Fact]
    public void Only_steps_coarser_than_the_chosen_quality_are_used()
    {
        var options = new DynamicOptions();
        Assert.Equal(["Sharp", "Balanced"], options.TiersBelow(new TexturePlan(1)).Select(t => t.Name));
        Assert.Equal(["Balanced"], options.TiersBelow(new TexturePlan(-1)).Select(t => t.Name)); // Sharp is no saving at Sharp
        Assert.Empty(options.TiersBelow(new TexturePlan(-2)));
        Assert.Equal(["Sharp"], new DynamicOptions(BalancedAboveFt: null).TiersBelow(new TexturePlan(1)).Select(t => t.Name));
    }

    // ---------------------------------------------------------------- the settings

    private MountSettings Parse(params string[] extra)
    {
        Directory.CreateDirectory(_tree.PathOf("source"));
        return MountSettings.FromValues(TestSettings.Of(
            [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", .. extra]));
    }

    [Fact]
    public void Dynamic_quality_is_off_unless_turned_on_and_its_heights_are_checked()
    {
        Assert.Null(Parse().Dynamic);
        Assert.Equal(new DynamicOptions(5000, 25000), Parse("dynamic-quality = on").Dynamic);
        Assert.Equal(new DynamicOptions(8000, null), Parse("dynamic-quality = on", "dynamic-sharp-above = 8000", "dynamic-balanced-above = off").Dynamic);
        Assert.Throws<SettingsException>(() => Parse("dynamic-quality = sometimes"));
        Assert.Throws<SettingsException>(() => Parse("dynamic-quality = on", "dynamic-sharp-above = 500"));
        Assert.Throws<SettingsException>(() => Parse("dynamic-quality = on", "dynamic-sharp-above = 9000", "dynamic-balanced-above = 8000"));
    }

    // ---------------------------------------------------------------- the preset

    [Fact]
    public void Dynamic_is_Extra_with_dynamic_quality_on_and_any_other_preset_turns_it_off()
    {
        var settings = SettingsDocument.FromText("x.ini", "level-change = -1\ndownload-workers = 160\n");

        QualityPreset.Dynamic.ApplyTo(settings);
        Assert.Equal((1, true), (settings.GetInt("level-change"), settings.GetBool("dynamic-quality") == true));
        Assert.Equal(QualityPreset.Extra.Get("dds-build-workers"), settings.Get("dds-build-workers"));
        Assert.Equal(QualityPreset.Dynamic, QualityPreset.Current(settings.GetInt("level-change"), true));

        QualityPreset.Extra.ApplyTo(settings);
        Assert.Equal("off", settings.Get("dynamic-quality"));
        Assert.Equal(QualityPreset.Extra, QualityPreset.Current(settings.GetInt("level-change"), false));

        Assert.Equal(QualityPreset.Sharp, QualityPreset.Current(-1, true)); // dynamic-quality set by hand at another level
        Assert.Equal(QualityPreset.Extra, QualityPreset.ForLevel(1)); // the cache, tuning and texture size stay Extra's
        Assert.Equal(160, settings.GetInt("download-workers"));
    }

    [Fact]
    public void An_older_settings_file_without_dynamic_quality_is_still_at_its_presets_defaults()
    {
        var settings = SettingsDocument.FromText("x.ini", "");
        foreach (var (key, value) in TunedSettings.Defaults(QualityPreset.Extra)) settings.Set(key, value);
        settings.Set("level-change", 1);
        Assert.False(TunedSettings.DiffersFromDefaults(settings, QualityPreset.Extra));
    }

    [Fact]
    public void Dynamic_shares_what_was_tuned_for_Extra()
    {
        string path = _tree.PathOf("orthostreamxp.ini");
        TunedStore store = TunedStore.Load(path);
        store.Set(1, "max-wait", "7.5");
        store.Save();
        var settings = SettingsDocument.FromText(path, "");

        TunedSettings.ChoosePreset(settings, QualityPreset.Dynamic, TunedStore.Load(path));

        Assert.Equal(("7.5", true), (settings.Get("max-wait"), settings.GetBool("dynamic-quality") == true));
    }
}
