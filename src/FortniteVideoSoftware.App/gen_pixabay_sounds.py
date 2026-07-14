import urllib.request
import re
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

headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.5',
    'Connection': 'keep-alive',
    'Upgrade-Insecure-Requests': '1',
    'Sec-Fetch-Dest': 'document',
    'Sec-Fetch-Mode': 'navigate',
    'Sec-Fetch-Site': 'none',
    'Sec-Fetch-User': '?1',
}

b64_results = {}

for name, url in urls.items():
    print(f"Fetching {name}...")
    req = urllib.request.Request(url, headers=headers)
    try:
        html = urllib.request.urlopen(req).read().decode('utf-8')
        # Pixabay audio URLs usually look like: https://cdn.pixabay.com/audio/2022/01/24/audio_123456.mp3
        match = re.search(r'https://cdn\.pixabay\.com/audio/[^\"\'\s>]+\.mp3', html)
        if match:
            mp3_url = match.group(0)
            print(f"Found MP3: {mp3_url}")
            mp3_path = f"{name}.mp3"
            wav_path = f"{name}.wav"
            
            # Download MP3
            req_mp3 = urllib.request.Request(mp3_url, headers=headers)
            with urllib.request.urlopen(req_mp3) as response, open(mp3_path, 'wb') as out_file:
                out_file.write(response.read())
                
            # Process with ffmpeg: 
            # 1. silenceremove 
            # 2. loudnorm
            cmd = [
                "ffmpeg", "-y", "-i", mp3_path, 
                "-af", "silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse,loudnorm=I=-16:TP=-1.5:LRA=11", 
                "-acodec", "pcm_s16le", "-ar", "44100", "-ac", "1", wav_path
            ]
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            
            # Read WAV and encode to Base64
            with open(wav_path, 'rb') as f:
                b64_results[name] = base64.b64encode(f.read()).decode('utf-8')
                
            # Clean up
            os.remove(mp3_path)
            os.remove(wav_path)
        else:
            print(f"NO MP3 URL FOUND FOR {name}")
    except Exception as e:
        print(f"ERROR processing {name}: {e}")

# Generate C# class
cs_code = f"""using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public static class UiSoundEffect
{{
    private static readonly byte[] SuccessSoundWav = Convert.FromBase64String("{b64_results.get('Success', '')}");
    private static readonly byte[] CloseSoundWav = Convert.FromBase64String("{b64_results.get('Close', '')}");
    private static readonly byte[] OpenSoundWav = Convert.FromBase64String("{b64_results.get('Open', '')}");
    private static readonly byte[] GeneralSoundWav = Convert.FromBase64String("{b64_results.get('General', '')}");
    private static readonly byte[] ErrorSoundWav = Convert.FromBase64String("{b64_results.get('Error', '')}");
    private static readonly byte[] MarkSoundWav = Convert.FromBase64String("{b64_results.get('Mark', '')}");

    public static void PlayProcess() => PlayBuffer(SuccessSoundWav);
    public static void PlayClose() => PlayBuffer(CloseSoundWav);
    public static void PlayOpen() => PlayBuffer(OpenSoundWav);
    public static void PlayClick() => PlayBuffer(GeneralSoundWav);
    public static void PlayError() => PlayBuffer(ErrorSoundWav);
    public static void PlayMark() => PlayBuffer(MarkSoundWav);

    private static void PlayBuffer(byte[] buffer)
    {{
        if (buffer == null || buffer.Length == 0) return;
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
    
print("C# file updated successfully.")
