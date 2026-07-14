import os
import subprocess
import base64

urls = {
    "Success": "https://pixabay.com/sound-effects/technology-ui-success-chime-513565/",
    "Close": "https://pixabay.com/sound-effects/technology-ui-click-soft-512213/",
    "Open": "https://pixabay.com/sound-effects/film-special-effects-ui-pop-sound-316482/",
    "General": "https://pixabay.com/sound-effects/immersivecontrol-button-click-sound-463065/",
    "Error": "https://pixabay.com/sound-effects/technology-ui-pop-up-open-516939/",
    "Mark": "https://pixabay.com/sound-effects/film-special-effects-swipe-236674/"
}

b64_results = {}

for name, url in urls.items():
    print(f"Downloading {name}...")
    mp3_path = f"{name}.mp3"
    wav_path = f"{name}.wav"
    
    # 1. Download using yt-dlp
    dl_cmd = [
        ".\\yt-dlp.exe", 
        "--extractor-args", "generic:impersonate", 
        url, 
        "-o", mp3_path
    ]
    subprocess.run(dl_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    
    if not os.path.exists(mp3_path):
        print(f"FAILED to download {name}")
        continue
        
    # 2. Process with ffmpeg
    # NO silenceremove (it was destroying soft sounds).
    # Use loudnorm to match them all to -24 LUFS (very soft standard), then volume=0.4 (40%).
    # This guarantees they will be extremely gentle.
    ff_cmd = [
        "ffmpeg", "-y", "-i", mp3_path, 
        "-af", "loudnorm=I=-24:TP=-2:LRA=11,volume=0.4", 
        "-acodec", "pcm_s16le", "-ar", "44100", "-ac", "1", wav_path
    ]
    subprocess.run(ff_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    
    # 3. Read and encode
    if os.path.exists(wav_path):
        with open(wav_path, 'rb') as f:
            b64_results[name] = base64.b64encode(f.read()).decode('utf-8')
        os.remove(wav_path)
    os.remove(mp3_path)

# Generate C# class
cs_code = f"""using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public static class UiSoundEffect
{{
    private static readonly byte[] SuccessWav = Convert.FromBase64String("{b64_results.get('Success', '')}");
    private static readonly byte[] CloseWav = Convert.FromBase64String("{b64_results.get('Close', '')}");
    private static readonly byte[] OpenWav = Convert.FromBase64String("{b64_results.get('Open', '')}");
    private static readonly byte[] GeneralWav = Convert.FromBase64String("{b64_results.get('General', '')}");
    private static readonly byte[] ErrorWav = Convert.FromBase64String("{b64_results.get('Error', '')}");
    private static readonly byte[] MarkWav = Convert.FromBase64String("{b64_results.get('Mark', '')}");

    public static void PlayProcess() => PlayBuffer(SuccessWav);
    public static void PlayClose() => PlayBuffer(CloseWav);
    public static void PlayOpen() => PlayBuffer(OpenWav);
    public static void PlayClick() => PlayBuffer(GeneralWav);
    public static void PlayError() => PlayBuffer(ErrorWav);
    public static void PlayMark() => PlayBuffer(MarkWav);

    private static void PlayBuffer(byte[] buffer)
    {{
        if (buffer == null || buffer.Length < 100) return;
        Task.Run(() => 
        {{
            try
            {{
                using (var ms = new MemoryStream(buffer))
                using (var player = new SoundPlayer(ms))
                {{
                    player.PlaySync();
                }}
            }}
            catch {{ }}
        }});
    }}
}}
"""

with open('UiSoundEffect.cs', 'w', encoding='utf-8') as f:
    f.write(cs_code)
    
print("Successfully generated gentle normalized sounds in UiSoundEffect.cs")
