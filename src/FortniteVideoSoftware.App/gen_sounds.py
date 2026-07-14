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

duration_click = 0.02
s_click = []
for i in range(int(sample_rate * duration_click)):
    t = i / sample_rate
    freq = 1200 * math.exp(-t * 200)
    amp = math.exp(-t * 150) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    if i > int(sample_rate * duration_click) - 50:
        val *= (int(sample_rate * duration_click) - i) / 50.0
    s_click.append(int(val * 32767))

b64_click = get_b64(s_click)

duration_proc = 0.06
s_proc = []
for i in range(int(sample_rate * duration_proc)):
    t = i / sample_rate
    freq = 800 if t < 0.02 else 1600
    amp = math.exp(-t * 60) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    if t > 0.015 and t < 0.025: val *= 0.5
    if i > int(sample_rate * duration_proc) - 100:
        val *= (int(sample_rate * duration_proc) - i) / 100.0
    s_proc.append(int(val * 32767))

b64_proc = get_b64(s_proc)

duration_cancel = 0.04
s_cancel = []
for i in range(int(sample_rate * duration_cancel)):
    t = i / sample_rate
    freq = 400 * math.exp(-t * 50)
    amp = math.exp(-t * 100) * 0.15
    val = math.sin(2 * math.pi * freq * t) * amp
    if i > int(sample_rate * duration_cancel) - 50:
        val *= (int(sample_rate * duration_cancel) - i) / 50.0
    s_cancel.append(int(val * 32767))

b64_cancel = get_b64(s_cancel)

cs_code = f"""using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public static class UiSoundEffect
{{
    private static readonly byte[] ClickSoundWav = Convert.FromBase64String("{b64_click}");
    private static readonly byte[] ProcessSoundWav = Convert.FromBase64String("{b64_proc}");
    private static readonly byte[] CancelSoundWav = Convert.FromBase64String("{b64_cancel}");

    public static void PlayClick() => PlayBuffer(ClickSoundWav);
    public static void PlayProcess() => PlayBuffer(ProcessSoundWav);
    public static void PlayCancel() => PlayBuffer(CancelSoundWav);

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
