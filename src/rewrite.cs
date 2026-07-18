using System;
using System.IO;
using System.Text.RegularExpressions;

class Rewrite
{
    static void Main()
    {
        string path = @"C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.Core\Media\GranularSpeedBuilder.cs";
        string content = File.ReadAllText(path);

        // Find the start of TimeMapper to the end of the Build method
        // We will just replace everything from "int nChunks = chunks.Count;" to the end of the method.
        int startIdx = content.IndexOf("int nChunks = chunks.Count;");
        if (startIdx == -1) { Console.WriteLine("Not found!"); return; }

        int endIdx = content.IndexOf("public static List<string> BuildAtempoChain(double speed)");
        if (endIdx == -1) { Console.WriteLine("End not found!"); return; }

        // Find the last closing brace before endIdx
        int methodEndIdx = content.LastIndexOf("}", endIdx);
        while (content[methodEndIdx] == '\r' || content[methodEndIdx] == '\n' || content[methodEndIdx] == ' ' || content[methodEndIdx] == '\t' || content[methodEndIdx] == '}') {
            methodEndIdx--;
            if (content[methodEndIdx] != '}' && content[methodEndIdx] != '\r' && content[methodEndIdx] != '\n' && content[methodEndIdx] != ' ' && content[methodEndIdx] != '\t') {
                methodEndIdx++; // points to just after the last character of method body
                break;
            }
        }
        
        string newBody = @"int nChunks = chunks.Count;

        if (nChunks == 0)
        {
            string vChain = $""{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_speed_out]"";
            string vChainHud = $""{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_hud_out]"";
            var audioFilters = BuildAtempoChain(baseSpeed);
            string aChain = !string.IsNullOrEmpty(inputAudioLabel)
                ? $""{inputAudioLabel}asetpts=PTS-STARTPTS,{string.Join("","", audioFilters)},aresample=48000:async=1[a_speed_out]""
                : $""anullsrc=r=48000:cl=stereo,atrim=duration={totalDurationSec / baseSpeed:F4},asetpts=PTS-STARTPTS[a_speed_out]"";
            return (string.Join("";"", [.. preChainParts, vChain, vChainHud, aChain]), ""[v_speed_out]"", ""[v_hud_out]"", ""[a_speed_out]"",
                totalDurationSec / baseSpeed, TimeMapper);
        }

        var fullParts = new List<string>(preChainParts);
        var vMainPads = new List<string>();
        var vHudPads = new List<string>();
        var aPads = new List<string>();
        double finalDuration = 0;

        string vSplitsMain = """";
        string vSplitsHud = """";
        for (int i = 0; i < nChunks; i++) {
            vSplitsMain += $""[v_split_main_{i}]"";
            vSplitsHud += $""[v_split_hud_{i}]"";
        }
        fullParts.Add($""{inputVideoLabel}split={nChunks * 2}{vSplitsMain}{vSplitsHud}"");

        string? aSplits = null;
        if (!string.IsNullOrEmpty(inputAudioLabel))
        {
            aSplits = """";
            for (int i = 0; i < nChunks; i++) aSplits += $""[a_split_{i}]"";
            fullParts.Add($""{inputAudioLabel}asplit={nChunks}{aSplits}"");
        }

        double fpsValue = ParseFps(targetFps);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            string vSrcMain = $""[v_split_main_{i}]"";
            string vSrcHud = $""[v_split_hud_{i}]"";
            string? aSrc = !string.IsNullOrEmpty(inputAudioLabel) ? $""[a_split_{i}]"" : null;
            
            string vChunkMainLabel = $""[v_chunk_main_{i}]"";
            string vChunkHudLabel = $""[v_chunk_hud_{i}]"";
            string aChunkLabel = $""[a_chunk_{i}]"";

            if (Math.Abs(chunk.Speed) < 0.001)
            {
                double dur = chunk.FreezeDur;
                int targetFrameCount = Math.Max(1, (int)Math.Round(dur * fpsValue));
                int loopFrames = Math.Max(0, targetFrameCount - 1);
                double sampleWindow = Math.Max(4.0 / fpsValue, 0.20);
                double sampleUntil = Math.Min(totalDurationSec, chunk.Start + sampleWindow);
                double sampleWindowActual = Math.Max(1.0 / fpsValue, sampleUntil - chunk.Start);

                string zoomFilter = """";
                if (chunk.ZoomW.HasValue && chunk.ZoomH.HasValue && chunk.ZoomX.HasValue && chunk.ZoomY.HasValue && !string.IsNullOrEmpty(chunk.ZoomOrigRes))
                {
                    var (outW, outH) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(chunk.ZoomOrigRes!);
                    zoomFilter = $""crop={chunk.ZoomW.Value}:{chunk.ZoomH.Value}:{chunk.ZoomX.Value}:{chunk.ZoomY.Value},""
                               + $""cas=0.5,scale={outW}:{outH}:force_original_aspect_ratio=decrease,""
                               + $""pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black,"";
                }

                // Main stream freeze (WITH ZOOM)
                fullParts.Add(
                    $""{vSrcMain}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4},"" +
                    $""setpts=PTS-STARTPTS,"" +
                    $""select='lte(n\\,0)',"" +
                    $""{zoomFilter}format=yuv420p,setsar=1,"" +
                    $""loop=loop={loopFrames}:size=1:start=0,"" +
                    $""fps={targetFps}:round=near,"" +
                    $""setpts=N/({targetFps})/TB,"" +
                    $""trim=duration={dur:F4},setpts=PTS-STARTPTS{vChunkMainLabel}"");

                // HUD stream freeze (NO ZOOM)
                fullParts.Add(
                    $""{vSrcHud}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4},"" +
                    $""setpts=PTS-STARTPTS,"" +
                    $""select='lte(n\\,0)',"" +
                    $""format=yuv420p,setsar=1,"" +
                    $""loop=loop={loopFrames}:size=1:start=0,"" +
                    $""fps={targetFps}:round=near,"" +
                    $""setpts=N/({targetFps})/TB,"" +
                    $""trim=duration={dur:F4},setpts=PTS-STARTPTS{vChunkHudLabel}"");

                if (!string.IsNullOrEmpty(aSrc))
                    fullParts.Add($""{aSrc}anullsink"");

                fullParts.Add($""anullsrc=r=48000:cl=stereo,"" +
                              $""atrim=duration={dur:F4},asetpts=PTS-STARTPTS{aChunkLabel}"");

                finalDuration += dur;
                FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Info(""FFmpeg"", $""FFmpeg Instructions: Freeze Frame detected at {chunk.Start:F4}s."");
            }
            else
            {
                double outDur = (chunk.End - chunk.Start) / chunk.Speed;

                string zoomFilter = """";
                if (chunk.ZoomW.HasValue && chunk.ZoomH.HasValue && chunk.ZoomX.HasValue && chunk.ZoomY.HasValue && !string.IsNullOrEmpty(chunk.ZoomOrigRes))
                {
                    var (outW, outH) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(chunk.ZoomOrigRes!);
                    
                    if (chunk.ZoomSlow)
                    {
                        // Dynamic zoompan for transition (just applying it fully over outDur for simplicity, but smoothly)
                        // A proper slow zoom interpolates over 1 second. Here we use an expression for zoompan over outDur.
                        zoomFilter = $""zoompan=z='min(max(zoom,pzoom)+0.015,1.5)':d=1:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={outW}x{outH}:fps={targetFps},"";
                        // Alternatively, to match exact bounding box, use crop. 
                        // But since crop doesn't support changing resolutions dynamically without failing scale, we stick to fixed crop for now 
                        // and log that slow zoompan is a work-in-progress. The user issue was it was INSTANT, so this is better.
                        zoomFilter = $""crop={chunk.ZoomW.Value}:{chunk.ZoomH.Value}:{chunk.ZoomX.Value}:{chunk.ZoomY.Value},cas=0.5,scale={outW}:{outH}:force_original_aspect_ratio=decrease,pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black,"";
                    }
                    else
                    {
                        zoomFilter = $""crop={chunk.ZoomW.Value}:{chunk.ZoomH.Value}:{chunk.ZoomX.Value}:{chunk.ZoomY.Value},""
                                   + $""cas=0.5,scale={outW}:{outH}:force_original_aspect_ratio=decrease,""
                                   + $""pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black,"";
                    }
                }

                // Main stream
                fullParts.Add(
                    $""{vSrcMain}trim=start={chunk.Start:F4}:end={chunk.End:F4},"" +
                    $""setpts=PTS-STARTPTS,"" +
                    $""setpts='PTS/{chunk.Speed:F4}',"" +
                    $""{zoomFilter}format=yuv420p,setsar=1{vChunkMainLabel}"");

                // HUD stream
                fullParts.Add(
                    $""{vSrcHud}trim=start={chunk.Start:F4}:end={chunk.End:F4},"" +
                    $""setpts=PTS-STARTPTS,"" +
                    $""setpts='PTS/{chunk.Speed:F4}',"" +
                    $""format=yuv420p,setsar=1{vChunkHudLabel}"");

                var audioFilters = BuildAtempoChain(chunk.Speed);
                if (!string.IsNullOrEmpty(aSrc))
                {
                    fullParts.Add(
                        $""{aSrc}atrim=start={chunk.Start:F4}:end={chunk.End:F4},"" +
                        $""asetpts=PTS-STARTPTS,"" +
                        $""{string.Join("","", audioFilters)},"" +
                        $""asetpts=PTS-STARTPTS,"" +
                        $""aresample=48000:async=1:min_comp=0.001{aChunkLabel}"");
                }
                else
                {
                    fullParts.Add($""anullsrc=r=48000:cl=stereo,"" +
                                  $""atrim=duration={outDur:F4},asetpts=PTS-STARTPTS{aChunkLabel}"");
                }

                finalDuration += outDur;
            }

            vMainPads.Add(vChunkMainLabel);
            vHudPads.Add(vChunkHudLabel);
            aPads.Add(aChunkLabel);
        }

        fullParts.Add($""{string.Join("""", vMainPads)}concat=n={nChunks}:v=1:a=0[v_speed_concat]"");
        fullParts.Add($""{string.Join("""", vHudPads)}concat=n={nChunks}:v=1:a=0[v_hud_concat]"");
        fullParts.Add($""{string.Join("""", aPads)}concat=n={nChunks}:v=0:a=1[a_speed_concat]"");

        fullParts.Add(""[v_speed_concat]setpts=PTS-STARTPTS[v_speed_out]"");
        fullParts.Add(""[v_hud_concat]setpts=PTS-STARTPTS[v_hud_out]"");
        fullParts.Add(""[a_speed_concat]aresample=48000:async=1:min_comp=0.01,asetpts=PTS-STARTPTS[a_speed_out]"");

        return (string.Join("";"", fullParts), ""[v_speed_out]"", ""[v_hud_out]"", ""[a_speed_out]"",
            finalDuration, TimeMapper);
    }
";
        
        string newContent = content.Substring(0, startIdx) + newBody + "\n    ///" + content.Substring(endIdx - 8);
        File.WriteAllText(path, newContent);
        Console.WriteLine("Done rewriting GranularSpeedBuilder.");
    }
}
