
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
        string inputMainPad,
        string inputHudPad,
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
            if (activeLayers.Count == 1)
            {
                parts.Add($"{inputHudPad}null[v_layer_in_0]");
            }
            else
            {
                var splitLabels = new StringBuilder();
                for (int i = 0; i < activeLayers.Count; i++)
                    splitLabels.Append($"[v_layer_in_{i}]");
                parts.Add($"{inputHudPad}split={activeLayers.Count}{splitLabels}");
            }

            var plan = CoordinateMath.ScalePlan(originalResolution);
            parts.Add($"{inputMainPad}scale={plan.scaledW}:{plan.scaledH}:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:{plan.cropX}:{plan.cropY}[main_base]");
            currV = "[main_base]";

            for (int i = 0; i < activeLayers.Count; i++)
            {
                var layer = activeLayers[i];

                var sourceRect = CoordinateMath.InverseTransformFromContentAreaInt(
                    (layer.UiRect[2], layer.UiRect[3], layer.UiRect[0], layer.UiRect[1]),
                    originalResolution,
                    HudConfig.CropDriftType(layer.ConfKey));

                int sx = sourceRect.x, sy = sourceRect.y, sw = sourceRect.w, sh = sourceRect.h;

                // ISSUE_4: this block used to be a hand-copied duplicate of
                // CoordinateMath.QuantizeBackendSize's body, computed in double precision through
                // backendScale.ToDouble() (32/27 is not exactly representable in binary) and rounded
                // with Math.Round's banker's rounding. The preview (CropToolWindow) and
                // HudConfig.Sanitize called the real function instead, so the two sides agreed only
                // because the copied expression happened to be identical — nothing enforced it, and
                // any edit to one would have silently desynchronised export from preview.
                // Both sides now call the same exact-rational quantizer. UiRect is [w, h, x, y].
                Frac backendScale = CoordinateConstants.BackendScale;
                Frac scaleFrac = Frac.FromDouble(layer.Scale);
                var (rw, rh) = CoordinateMath.QuantizeBackendSizeInternal(
                    layer.UiRect[0], layer.UiRect[1], scaleFrac);

                var pos = layer.Pos;
                Frac lxRaw = Frac.FromDouble(pos.x) * backendScale;
                Frac lyRaw = (Frac.FromDouble(pos.y) - new Frac(CoordinateConstants.UIPaddingTop, 1)) * backendScale;

                Frac maxLx = new(CoordinateConstants.TargetW - rw, 1);
                Frac maxLy = new(CoordinateConstants.TargetH - rh, 1);

                int lx = CoordinateMath.ScaleRound(
                    Frac.Zero > lxRaw ? Frac.Zero :
                    (lxRaw > maxLx ? maxLx : lxRaw));
                int ly = CoordinateMath.ScaleRound(
                    Frac.Zero > lyRaw ? Frac.Zero :
                    (lyRaw > maxLy ? maxLy : lyRaw));

                // ISSUE_5: the 2px pad is a bleed guard — it gives lanczos a margin so the layer's
                // outermost columns are not resampled against undefined pixels, and the matching
                // -2 on the overlay below puts the real content back at exactly (lx, ly).
                // It used to be `color=black` on a stream with NO alpha plane, so overlay composited
                // those 4 extra rows/columns as OPAQUE BLACK — a visible border drawn around every
                // HUD element on top of the gameplay. Converting to yuva420p first and padding with
                // a fully transparent black (`@0`) keeps the identical geometry while making the
                // guard invisible. Do not drop the format conversion: pad cannot produce
                // transparency on a format without an alpha plane.
                parts.Add($"[v_layer_in_{i}]crop=w={sw}:h={sh}:x={sx}:y={sy}," +
                          $"scale=w={rw}:h={rh}:flags=lanczos,format=yuva420p," +
                          $"pad=w=iw+4:h=ih+4:x=2:y=2:color=black@0[v_layer_out_{i}]");

                string nextV = $"[v_comp_{i}]";
                parts.Add($"{currV}[v_layer_out_{i}]overlay=x={lx - 2}:y={ly - 2}:eof_action=pass{nextV}");
                currV = nextV;
            }
        }
        else
        {
            var plan = CoordinateMath.ScalePlan(originalResolution);
            parts.Add($"{inputMainPad}scale={plan.scaledW}:{plan.scaledH}:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:{plan.cropX}:{plan.cropY}[main_base]");
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

    public record LayerSpec(
        string Name, string ConfKey,
        int[] UiRect,
        double Scale,
        (double x, double y) Pos,
        int Z);

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
            try { scale = (double)scalesObj[confKey]!; } 
            catch 
            { 
                var parsedFrac = Frac.FromString(scalesObj[confKey]!.ToString());
                if (parsedFrac != Frac.Zero) scale = parsedFrac.ToDouble();
            }
        }

        var overlaysObj = coords["overlays"]?.AsObject();
        double posX = 0, posY = CoordinateConstants.UIPaddingTop;
        if (overlaysObj != null && overlaysObj[ovKey] is JsonObject ov)
        {
            try { posX = (double)ov["x"]!; } catch { try { posX = double.Parse(ov["x"]!.ToString()); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
            try { posY = (double)ov["y"]!; } catch { try { posY = double.Parse(ov["y"]!.ToString()); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
        }

        var zOrdersObj = coords["z_orders"]?.AsObject();
        int z = 50;
        if (zOrdersObj != null && zOrdersObj.ContainsKey(ovKey))
        {
            try { z = zOrdersObj[ovKey]!.GetValue<int>(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
