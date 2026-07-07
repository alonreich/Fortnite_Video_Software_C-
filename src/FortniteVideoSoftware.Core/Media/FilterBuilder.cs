using System.Text;

namespace FortniteVideoSoftware.Core.Media;

public static class FilterBuilder
{
    public static string BuildAudioDuckingFilter(string gameAudioInput, string musicAudioInput)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine($"[{gameAudioInput}]asplit=2[game_raw][game_for_split];");
        sb.AppendLine($"[game_for_split]lowpass=f=150[game_low];");
        sb.AppendLine($"[game_raw]highpass=f=150[game_high];");

        sb.AppendLine($"[game_high][{musicAudioInput}]sidechaincompress=threshold=0.15:ratio=2.5[game_high_ducked];");

        sb.AppendLine($"[game_low][game_high_ducked]amix=inputs=2[game_ducked_final];");
        sb.AppendLine($"[game_ducked_final][{musicAudioInput}]amix=inputs=2[final_audio]");

        return sb.ToString();
    }

    public static string BuildWhatsAppIntroFilter(string videoInput)
    {
        
        StringBuilder sb = new StringBuilder();
        
        sb.AppendLine($"[{videoInput}]trim=start_frame=0:end_frame=1,loop=loop=-1:size=1:start=0,setpts=N/FRAME_RATE/TB,trim=duration=0.1[intro_video];");
        sb.AppendLine("anullsrc=r=44100:cl=stereo,trim=duration=0.1[intro_audio];");
        
        sb.AppendLine($"[{videoInput}]setpts=PTS-STARTPTS[main_video];");
        
        return sb.ToString();
    }
}
