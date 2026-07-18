
using System.Text;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Builds the FFmpeg filter_complex graph for portrait (9:16) mobile conversion.
/// 
/// Pipeline:
/// 1. Source video → scale to 1280x1920 internal space (center crop)
/// 2. Split into N+1 streams (1 base + N HUD layers)
/// 3. For each HUD layer: crop from source, scale, overlay at computed position
/// 4. Scale composed result from 1280x1920 → 1080x1620 (content area)
/// 5. Pad onto 1080x1920 black canvas with 150px top offset
/// 6. Overlay optional text PNG at y=0 (the 150px text strip)
/// 7. Force yuv420p format
/// </summary>
public class MobileFilterBuilder
{
    /// <summary>
    /// Builds the full mobile filter chain. Returns (filterString, outputPadLabel).
    /// Exact port of build_mobile_filter_chain().
    /// </summary>
    public static (string filterChain, string outputLabel) Build(
        string inputPad,
        JsonObject mobileCoords,
        bool isBossHp,
        bool showTeammates,
        bool showSpectating = false,
        string? txtInputLabel = null,
        bool useCuda = false,
        string originalResolution = "1920x1080")
    {
        var parts = new List<string>();
        var scales = mobileCoords["scales"]?.AsObject() ?? new JsonObject();
        var overlays = mobileCoords["overlays"]?.AsObject() ?? new JsonObject();
        var zOrders = mobileCoords["z_orders"]?.AsObject() ?? new JsonObject();

        string hpKey = isBossHp ? "boss_hp" : "normal_hp";
        var activeLayers = new List<LayerSpec>();

        var crops1080p = mobileCoords["crops_1080p"]?.AsObject();
        if (crops1080p != null)
        {
            foreach (var kvp in crops1080p)
            {
                string key = kvp.Key;
                
                if (key == "boss_hp" || key == "normal_hp")
                {
                    if (key != hpKey) continue;
                }
                else if (key == "spectating" && !showSpectating) continue;
                else if (key == "team" && !showTeammates) continue;

                activeLayers.RegisterLayer(mobileCoords, key, key, key, key);
            }
        }

        activeLayers.Sort((a, b) => a.Z.CompareTo(b.Z));

        string currV;

        if (activeLayers.Count > 0)
        {
            int splitCount = 1 + activeLayers.Count;

            var splitLabels = new StringBuilder();
            for (int i = 0; i < activeLayers.Count; i++)
                splitLabels.Append($"[v_layer_in_{i}]");

            parts.Add($"{inputPad}split={splitCount}[v_base_in]{splitLabels}");

            parts.Add($"[v_base_in]scale={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:" +
                      $"force_original_aspect_ratio=increase:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}[main_base]");
            currV = "[main_base]";

            for (int i = 0; i < activeLayers.Count; i++)
            {
                var layer = activeLayers[i];

                var sourceRect = CoordinateMath.InverseTransformFromContentAreaInt(
                    (layer.UiRect[2], layer.UiRect[3], layer.UiRect[0], layer.UiRect[1]),
                    originalResolution,
                    HudConfig.CropDriftType(layer.ConfKey));

                int sx = sourceRect.x, sy = sourceRect.y, sw = sourceRect.w, sh = sourceRect.h;

                Frac scaleFrac = Frac.FromDouble(layer.Scale);
                Frac backendScale = CoordinateConstants.BackendScale;
                int rw = Math.Max(2, CanvasMath.EvenCeil(
                    new Frac(layer.UiRect[0], 1) * scaleFrac * backendScale));
                int rh = Math.Max(2, CanvasMath.EvenCeil(
                    new Frac(layer.UiRect[1], 1) * scaleFrac * backendScale));

                var pos = layer.Pos;
                Frac lxRaw = Frac.FromDouble(pos.x) * backendScale;
                Frac lyRaw = (Frac.FromDouble(pos.y) - new Frac(CoordinateConstants.UIPaddingTop, 1)) * backendScale;

                Frac maxLx = new(CoordinateConstants.TargetW - rw, 1);
                // ISSUE_4: the internal-space Y range 0..TargetH-rh already maps 1:1 to the
                // content area (content 1620 × 32/27 = 1920). Subtracting bottom padding
                // here double-applied it and pushed bottom-placed elements ~150 final px
                // above their previewed position. The UI clamp (ClampOverlayPosition) is
                // the single source of truth for the allowed placement range.
                Frac maxLy = new(CoordinateConstants.TargetH - rh, 1);

                int lx = CoordinateMath.ScaleRound(
                    Frac.FromDouble(0) > lxRaw ? Frac.FromDouble(0) :
                    (lxRaw > maxLx ? maxLx : lxRaw));
                int ly = CoordinateMath.ScaleRound(
                    Frac.FromDouble(0) > lyRaw ? Frac.FromDouble(0) :
                    (lyRaw > maxLy ? maxLy : lyRaw));

                parts.Add($"[v_layer_in_{i}]crop=w={sw}:h={sh}:x={sx}:y={sy}," +
                          $"scale=w={rw}:h={rh}:flags=lanczos," +
                          $"pad=w=iw+4:h=ih+4:x=2:y=2:color=black[v_layer_out_{i}]");

                string nextV = $"[v_comp_{i}]";
                parts.Add($"{currV}[v_layer_out_{i}]overlay=x={lx - 2}:y={ly - 2}:eof_action=pass{nextV}");
                currV = nextV;
            }
        }
        else
        {
            parts.Add($"{inputPad}scale={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:" +
                      $"force_original_aspect_ratio=increase:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}[main_base]");
            currV = "[main_base]";
        }

        parts.Add($"{currV}scale={CoordinateConstants.ContentW}:{CoordinateConstants.ContentH}:" +
                  $"flags=lanczos," +
                  $"pad={CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}:" +
                  $"0:{CoordinateConstants.PaddingTop}:black,setsar=1[v_padded]");
        currV = "[v_padded]";

        if (!string.IsNullOrEmpty(txtInputLabel))
        {
            parts.Add($"{currV}{txtInputLabel}overlay=0:0:shortest=1:eof_action=repeat:format=auto[v_final_raw]");
            currV = "[v_final_raw]";
        }

        parts.Add($"{currV}format=yuv420p[v_final]");

        return (string.Join(";", parts), "[v_final]");
    }

    // NOTE: layer registration lives in MobileFilterBuilderExtensions.RegisterLayer below.
    // A private duplicate that previously lived here was dead code (the instance-syntax
    // call in Build binds the extension method) and was removed (ISSUE_11).
    public record LayerSpec(
        string Name, string ConfKey,
        int[] UiRect,
        double Scale,
        (double x, double y) Pos,
        int Z);

    public static (string filterChain, string outputLabel) BuildMobileFilter(
        JsonObject mobileCoords,
        string originalResolution,
        bool isBossHp = false,
        bool showTeammates = false,
        bool showSpectating = false)
    {
        return Build("[0:v]", mobileCoords, isBossHp, showTeammates, showSpectating,
            null, false, originalResolution);
    }
}

internal static class MobileFilterBuilderExtensions
{
    internal static void RegisterLayer(
        this List<MobileFilterBuilder.LayerSpec> list,
        JsonObject coords,
        string name, string confKey, string cropKey1080, string ovKey)
    {
        int[] rect = GetRectHelper(coords, "crops_1080p", cropKey1080);
        var scalesObj = coords["scales"]?.AsObject();
        double scale = 1.0;
        if (scalesObj != null && scalesObj.ContainsKey(confKey))
        {
            try { scale = (double)scalesObj[confKey]!; } catch { try { scale = double.Parse(scalesObj[confKey]!.ToString()); } catch { } }
        }

        var overlaysObj = coords["overlays"]?.AsObject();
        double posX = 0, posY = CoordinateConstants.UIPaddingTop;
        if (overlaysObj != null && overlaysObj[ovKey] is JsonObject ov)
        {
            try { posX = (double)ov["x"]!; } catch { try { posX = double.Parse(ov["x"]!.ToString()); } catch { } }
            try { posY = (double)ov["y"]!; } catch { try { posY = double.Parse(ov["y"]!.ToString()); } catch { } }
        }

        var zOrdersObj = coords["z_orders"]?.AsObject();
        int z = 50;
        if (zOrdersObj != null && zOrdersObj.ContainsKey(ovKey))
        {
            try { z = zOrdersObj[ovKey]!.GetValue<int>(); } catch { }
        }

        if (rect.Length >= 4 && rect[0] >= 1 && rect[1] >= 1)
        {
            list.Add(new MobileFilterBuilder.LayerSpec(name, confKey, rect, scale, (posX, posY), z));
        }
    }

    private static int[] GetRectHelper(JsonObject coords, string section, string key)
    {
        var sectionObj = coords[section]?.AsObject();
        if (sectionObj == null) return [0, 0, 0, 0];
        var node = sectionObj[key];
        if (node is JsonArray arr && arr.Count >= 4)
            return [arr[0]!.GetValue<int>(), arr[1]!.GetValue<int>(), arr[2]!.GetValue<int>(), arr[3]!.GetValue<int>()];
        return [0, 0, 0, 0];
    }
}
