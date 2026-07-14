import wave, struct, math, base64

sample_rate = 44100

def get_b64(samples):
    import io
    buf = io.BytesIO()
    with wave.open(buf, 'w') as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        for s in samples:
            wav.writeframes(struct.pack('<h', s))
    return base64.b64encode(buf.getvalue()).decode('utf-8')

# 1. Success (Chime: C5, E5, G5, C6)
dur_success = 0.3
s_success = []
for i in range(int(sample_rate * dur_success)):
    t = i / sample_rate
    # 4 stages
    freq = 523
    if t > 0.05: freq = 659
    if t > 0.10: freq = 783
    if t > 0.15: freq = 1046
    
    amp = math.exp(-t * 10) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    if i > int(sample_rate * dur_success) - 200: val *= (int(sample_rate * dur_success) - i) / 200.0
    s_success.append(int(val * 32767))

# 2. Close (Soft thud)
dur_close = 0.06
s_close = []
for i in range(int(sample_rate * dur_close)):
    t = i / sample_rate
    freq = 200 * math.exp(-t * 100)
    amp = math.exp(-t * 100) * 0.2
    val = math.sin(2 * math.pi * freq * t) * amp
    s_close.append(int(val * 32767))

# 3. Open (Pop sweep UP)
dur_open = 0.08
s_open = []
for i in range(int(sample_rate * dur_open)):
    t = i / sample_rate
    freq = 300 + (t / dur_open) * 400
    amp = math.exp(-t * 40) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    s_open.append(int(val * 32767))

# 4. General (Sharp tick)
dur_general = 0.02
s_general = []
for i in range(int(sample_rate * dur_general)):
    t = i / sample_rate
    freq = 1200 * math.exp(-t * 200)
    amp = math.exp(-t * 150) * 0.1
    val = math.sin(2 * math.pi * freq * t) * amp
    s_general.append(int(val * 32767))

# 5. Error (Dissonant buzz)
dur_error = 0.15
s_error = []
for i in range(int(sample_rate * dur_error)):
    t = i / sample_rate
    val = (math.sin(2 * math.pi * 261 * t) + math.sin(2 * math.pi * 369 * t)) * 0.5
    amp = math.exp(-t * 10) * 0.15
    if i < 400: amp *= i/400.0
    if i > int(sample_rate * dur_error) - 400: amp *= (int(sample_rate * dur_error) - i) / 400.0
    s_error.append(int(val * amp * 32767))

# 6. Mark (Swipe DOWN)
dur_mark = 0.06
s_mark = []
for i in range(int(sample_rate * dur_mark)):
    t = i / sample_rate
    freq = 1000 - (t / dur_mark) * 800
    amp = math.exp(-t * 50) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    s_mark.append(int(val * 32767))

b64_success = get_b64(s_success)
b64_close = get_b64(s_close)
b64_open = get_b64(s_open)
b64_general = get_b64(s_general)
b64_error = get_b64(s_error)
b64_mark = get_b64(s_mark)

cs_code = f"""using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public static class UiSoundEffect
{{
    private static readonly byte[] SuccessWav = Convert.FromBase64String("{b64_success}");
    private static readonly byte[] CloseWav = Convert.FromBase64String("{b64_close}");
    private static readonly byte[] OpenWav = Convert.FromBase64String("{b64_open}");
    private static readonly byte[] GeneralWav = Convert.FromBase64String("{b64_general}");
    private static readonly byte[] ErrorWav = Convert.FromBase64String("{b64_error}");
    private static readonly byte[] MarkWav = Convert.FromBase64String("{b64_mark}");

    public static void PlaySuccess() => PlayBuffer(SuccessWav);
    public static void PlayClose() => PlayBuffer(CloseWav);
    public static void PlayOpen() => PlayBuffer(OpenWav);
    public static void PlayClick() => PlayBuffer(GeneralWav);
    public static void PlayError() => PlayBuffer(ErrorWav);
    public static void PlayMark() => PlayBuffer(MarkWav);

    private static void PlayBuffer(byte[] buffer)
    {{
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
