// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Text;
using System.Text.Json.Nodes;

using FortniteVideoSoftware.Core.Infrastructure;

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
///
/// ZERO-LAYER CASE (NOMASK_01): when no crop rectangle is active — every rect is [0,0,0,0], which
/// is what the reserved "No Mask Profile" ships — steps 2 and 3 are skipped entirely, the HUD pad
/// is terminated with nullsink, and the result is the Portrait Canvas Trick plus the optional text
/// strip and nothing else. Steps 1, 4, 5, 6 and 7 are byte-for-byte identical to a masked profile,
/// so the framing of a no-mask export matches a HUD export exactly.
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
        bool showTeammates,
        bool showSpectating = true,
        string? txtInputLabel = null,
        bool useCuda = false,
        string originalResolution = "1920x1080")
    {
        var parts = new List<string>();
        var scales = mobileCoords["scales"]?.AsObject() ?? new JsonObject();
        var overlays = mobileCoords["overlays"]?.AsObject() ?? new JsonObject();
        var zOrders = mobileCoords["z_orders"]?.AsObject() ?? new JsonObject();

        var activeLayers = new List<LayerSpec>();

        var crops1080p = mobileCoords["crops_1080p"]?.AsObject();
        if (crops1080p != null)
        {
            foreach (var kvp in crops1080p)
            {
                string key = kvp.Key;
                
                if (HudConfig.IsRetiredRole(key)) continue; // NO_BOSS_HP_01: also guard unsanitized callers.
                if (key == "spectating" && !showSpectating) continue;
                else if (key == "team" && !showTeammates) continue;

                activeLayers.RegisterLayer(mobileCoords, key, key, key, key);
            }
        }

        // ZTIEBREAK_01 — List<T>.Sort is an UNSTABLE introsort, so two layers sharing a z order
        // came out in an order decided by the pivot, not by the document: the same config could
        // stack them one way in a 3-layer export and the other way in a 5-layer export, and the
        // composer in Crop Tools (which breaks the same tie by element key) agreed with neither.
        // This is the shared tie-break rule: ascending Z, then element key, OrdinalIgnoreCase.
        activeLayers = activeLayers
            .OrderBy(l => l.Z)
            .ThenBy(l => l.ConfKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

            // NOMASK_01 — THE HUD PAD MUST STILL BE TERMINATED WHEN THERE ARE NO LAYERS.
            // ProcessWorker ALWAYS hands this method a HUD pad: either the granular chain's
            // [gVHud], or a `split=2[v_mob_main][v_mob_hud]` it inserts when there is none. With
            // zero active layers nothing above consumes it, and an unconnected output pad makes
            // ffmpeg reject the whole filter_complex — the export dies before it encodes a frame.
            // The landscape branch in ProcessWorker already terminates the same pad with nullsink
            // for exactly this reason; this is the portrait counterpart.
            // This branch was unreachable until the reserved "No Mask Profile" existed (every
            // shipped profile has layers), which is why the fault never surfaced.
            // DO NOT remove this because "the pad looks unused" — unused is precisely the problem.
            if (!string.IsNullOrEmpty(inputHudPad) && inputHudPad != inputMainPad)
            {
                parts.Add($"{inputHudPad}nullsink");
            }
        }

        if (!string.IsNullOrEmpty(txtInputLabel))
        {
            parts.Add($"{currV}scale={CoordinateConstants.ContentW}:{CoordinateConstants.ContentH}:" +
                      $"flags=lanczos,setsar=1[v_scaled_content]");
            parts.Add($"[v_scaled_content]split=2[v_sc_base][v_sc_video]");
            parts.Add($"[v_sc_base]pad={CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}:0:0:black," +
                      $"drawbox=x=0:y=0:w={CoordinateConstants.PortraitW}:h={CoordinateConstants.PortraitH}:color=black:t=fill[v_bg_canvas]");
            parts.Add($"[v_bg_canvas][v_sc_video]overlay=x=0:y='if(lt(t,0.11),320,{CoordinateConstants.PaddingTop})':shortest=1[v_padded]");
            currV = "[v_padded]";
            parts.Add($"{currV}{txtInputLabel}overlay=x=0:y='if(lt(t,0.11),170,0)':shortest=1:eof_action=repeat:format=auto[v_final_raw]");
            currV = "[v_final_raw]";
        }
        else
        {
            parts.Add($"{currV}scale={CoordinateConstants.ContentW}:{CoordinateConstants.ContentH}:" +
                      $"flags=lanczos," +
                      $"pad={CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}:" +
                      $"0:{CoordinateConstants.PaddingTop}:black,setsar=1[v_padded]");
            currV = "[v_padded]";
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
        JsonNode? scaleNode = scalesObj != null ? LookupHelper(scalesObj, confKey) : null;
        if (scaleNode != null)
        {
            double parsed;
            try { parsed = (double)scaleNode!; }
            catch (System.Exception swallowed)
            {
                // ZEROSCALE_01 — the old code checked `!= Frac.Zero`, which let a NEGATIVE fraction
                // straight through; and the numeric branch above had no check at all, so a JSON 0
                // became scale 0. Either one reaches QuantizeBackendSizeInternal, whose
                // Math.Max(factor, ...) floor silently rewrites the layer as a 32x32 sliver, and a
                // negative would land there via a negative Frac. Only a strictly positive scale is
                // meaningful; anything else falls back to 1/1 and is logged.
                try { parsed = Frac.FromString(scaleNode!.ToString()).ToDouble(); }
                catch (System.Exception swallowed2)
                {
                parsed = double.NaN;
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
                }
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            }

            if (double.IsFinite(parsed) && parsed > 0.0)
            {
                scale = parsed;
            }
            else
            {
                CoreLogger.Info("EXPORT", $"Layer '{confKey}' has a non-positive or unreadable scale ('{scaleNode}'); using 1/1.");
            }
        }

        var overlaysObj = coords["overlays"]?.AsObject();
        double posX = 0, posY = CoordinateConstants.UIPaddingTop;
        if (overlaysObj != null && LookupHelper(overlaysObj, ovKey) is JsonObject ov)
        {
            try { posX = (double)ov["x"]!; } catch { try { posX = double.Parse(ov["x"]!.ToString()); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
            try { posY = (double)ov["y"]!; } catch { try { posY = double.Parse(ov["y"]!.ToString()); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
        }

        var zOrdersObj = coords["z_orders"]?.AsObject();
        int z = 50;
        JsonNode? zNode = zOrdersObj != null ? LookupHelper(zOrdersObj, ovKey) : null;
        if (zNode != null)
        {
            try { z = zNode.GetValue<int>(); }
            catch
            {
                // A z order written as 20.0, "20" or by another tool must not silently become 50 —
                // that is a stacking change the user never asked for.
                try { z = (int)System.Math.Round(double.Parse(zNode.ToString(), System.Globalization.CultureInfo.InvariantCulture)); }
                catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            }
        }

        if (rect.Length >= 4 && rect[0] >= 1 && rect[1] >= 1)
        {
            list.Add(new MobileFilterBuilder.LayerSpec(name, confKey, rect, scale, (posX, posY), z));
        }
    }

    /// <summary>
    /// KEYCASE_01 — JsonObject indexes ordinally, but every element-key comparison in the Crop Tool
    /// editor is OrdinalIgnoreCase. A config whose "scales" says "Loot" while "crops_1080p" says
    /// "loot" therefore exported the layer at scale 1/1 with a default z, silently, with the user's
    /// real values sitting untouched in the file. Look the key up exactly first, then ignoring case.
    /// </summary>
    private static JsonNode? LookupHelper(JsonObject section, string key)
    {
        if (section.TryGetPropertyValue(key, out JsonNode? exact)) return exact;

        foreach (var pair in section)
        {
            if (string.Equals(pair.Key, key, System.StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return null;
    }

    private static int[] GetRectHelper(JsonObject coords, string section, string key)
    {
        var sectionObj = coords[section]?.AsObject();
        if (sectionObj == null) return [0, 0, 0, 0];
        var node = LookupHelper(sectionObj, key);
        if (node is JsonArray arr && arr.Count >= 4)
            return [arr[0]!.GetValue<int>(), arr[1]!.GetValue<int>(), arr[2]!.GetValue<int>(), arr[3]!.GetValue<int>()];
        return [0, 0, 0, 0];
    }
}
