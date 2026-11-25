namespace Launcher.Host;

internal static class GameWarmupAssets
{
    internal const int MaxCandidates = 512;
    internal static readonly string[] Standard =
    [
        "res://images/packed/combat_ui/end_turn_button.png",
        "res://images/packed/combat_ui/end_turn_button_glow.png",
        "res://images/packed/sprite_fonts/ironclad_energy_icon.png",
        "res://images/packed/sprite_fonts/silent_energy_icon.png",
        "res://images/packed/sprite_fonts/regent_energy_icon.png",
        "res://images/atlases/intent_atlas.sprites/intent_attack.tres",
        "res://images/atlases/intent_atlas.sprites/intent_defend.tres",
        "res://materials/cards/banners/card_banner_common_mat.tres",
        "res://materials/cards/banners/card_banner_uncommon_mat.tres",
        "res://materials/cards/banners/card_banner_rare_mat.tres",
        "res://materials/cards/banners/card_banner_status_mat.tres",
        "res://materials/cards/banners/card_banner_curse_mat.tres",
        "res://materials/cards/frames/card_frame_red_mat.tres",
        "res://materials/cards/frames/card_frame_green_mat.tres",
        "res://materials/cards/frames/card_frame_blue_mat.tres",
        "res://materials/cards/frames/card_frame_pink_mat.tres",
        "res://materials/cards/frames/card_frame_orange_mat.tres",
        "res://materials/cards/frames/card_frame_colorless_mat.tres",
        "res://scenes/cards/card_portrait_blur_material.tres",
        "res://scenes/cards/card_canvas_group_blur_material.tres",
        "res://scenes/cards/card_canvas_group_mask_material.tres",
        "res://shaders/vfx/common/vfx_flipbook_shader.gdshader",
        "res://shaders/vfx/common/vfx_row_flipbook_shader.gdshader",
        "res://shaders/hsv.gdshader",
        "res://scenes/vfx/hit_spark_vfx.tscn",
        "res://scenes/vfx/vfx_damage_num.tscn",
        "res://scenes/vfx/vfx_attack_slash.tscn",
        "res://scenes/vfx/vfx_attack_blunt.tscn",
        "res://scenes/vfx/vfx_block.tscn",
        "res://scenes/vfx/vfx_shiv_throw.tscn",
        "res://scenes/vfx/vfx_poison_impact.tscn"
    ];

    static readonly string[] Additional =
    [
        "res://scenes/vfx/vfx_slime_impact.tscn",
        "res://scenes/vfx/vfx_starry_impact.tscn",
        "res://scenes/vfx/thin_slice_vfx.tscn",
        "res://scenes/vfx/stab_vfx.tscn",
        "res://scenes/vfx/vfx_dagger_throw.tscn",
        "res://scenes/vfx/vfx_dagger_spray.tscn",
        "res://scenes/vfx/vfx_dagger_spray_flurry.tscn",
        "res://scenes/vfx/vfx_dagger_spray_impact.tscn",
        "res://scenes/vfx/vfx_smoke_puff.tscn",
        "res://scenes/vfx/power_applied_vfx.tscn",
        "res://scenes/cards/card.tscn",
        "res://scenes/cards/holders/hand_card_holder.tscn",
        "res://scenes/cards/holders/selected_hand_card_holder.tscn",
        "res://scenes/cards/holders/grid_card_holder.tscn",
        "res://scenes/cards/overlays/afflictions/hexed.tscn",
        "res://scenes/ui/card_hover_tip.tscn",
        "res://scenes/relics/relic_basic_holder.tscn",
        "res://scenes/cards/card_canvas_group_mask_blur_material.tres",
        "res://materials/vfx/potion/potion_liquid_overlay.tres",
        "res://shaders/dark_blur.gdshader",
        "res://shaders/radial_blur.gdshader",
        "res://shaders/doom_overlay.gdshader",
        "res://shaders/overlay_blend.gdshader",
        "res://shaders/scry_reveal.gdshader",
        "res://shaders/vfx/distortion/vfx_screen_distortion_outward_shader.gdshader",
        "res://shaders/vfx/scream/vfx_scream_distortion_polar_shader.gdshader",
        "res://images/powers/missing_power.png"
    ];

    static readonly (string Directory, string Extension, int Limit)[] Directories =
    [
        ("res://materials/cards/banners/", ".tres", 16),
        ("res://materials/cards/frames/", ".tres", 16),
        ("res://shaders/vfx/common/", ".gdshader", 16),
        ("res://shaders/blur/", ".gdshader", 16),
        ("res://images/packed/combat_ui/", ".png", 32),
        ("res://images/packed/sprite_fonts/", ".png", 20),
        ("res://images/atlases/intent_atlas.sprites/", ".tres", 32),
        ("res://scenes/vfx/", ".tscn", 64),
        ("res://images/card_portraits/", ".png", 32),
        ("res://images/card_portraits/ironclad/", ".png", 16),
        ("res://images/card_portraits/silent/", ".png", 16),
        ("res://images/card_portraits/regent/", ".png", 16),
        ("res://images/card_portraits/necrobinder/", ".png", 16),
        ("res://images/card_portraits/defect/", ".png", 16),
        ("res://images/packed/powers/", ".png", 24),
        ("res://images/packed/potions/", ".png", 16),
        ("res://images/packed/relics/", ".png", 24)
    ];

    internal static string[] Discover(bool extended, Func<string, string[]> listDirectory)
    {
        if (!extended) return Standard;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var assets = new List<string>();
        foreach (var path in Standard.Concat(Additional))
            if (seen.Add(path)) assets.Add(path);
        foreach (var (directory, extension, limit) in Directories)
        {
            var files = listDirectory(directory).Where(file => AllowedFile(file, extension))
                .Order(StringComparer.Ordinal).Select(file => directory + file).Where(seen.Add);
            assets.AddRange(files.Take(Math.Min(limit, MaxCandidates - assets.Count)));
        }
        return assets.ToArray();
    }

    internal static bool IsAllowed(string path, bool extended)
    {
        if (Standard.Contains(path, StringComparer.Ordinal)) return true;
        if (!extended) return false;
        if (Additional.Contains(path, StringComparer.Ordinal)) return true;
        return Directories.Any(rule => path.StartsWith(rule.Directory, StringComparison.Ordinal) &&
            AllowedFile(path[rule.Directory.Length..], rule.Extension));
    }

    static bool AllowedFile(string name, string extension) =>
        name.Length > extension.Length && name.Length <= 120 && name.EndsWith(extension, StringComparison.Ordinal)
        && !name.StartsWith('.') && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');

    internal static string Phase(string path) => Path.GetExtension(path) switch
    {
        ".tscn" => "Preparing combat effects",
        ".gdshader" => "Warming shaders",
        ".png" => "Loading textures",
        _ => "Preparing card and UI materials"
    };
}
