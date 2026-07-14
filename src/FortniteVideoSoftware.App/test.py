import urllib.request
import re

url = 'https://pixabay.com/sound-effects/technology-ui-success-chime-513565/'
headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.5',
}
req = urllib.request.Request(url, headers=headers)
html = urllib.request.urlopen(req).read().decode('utf-8')
match = re.search(r'https://cdn\.pixabay\.com/audio/[^\"]+', html)
if match:
    print('FOUND:', match.group(0))
else:
    print('NO MATCH. Looking for mp3 or m4a...')
    audio_matches = re.findall(r'\"([^\"]+\.(?:mp3|m4a|wav))\"', html)
    for a in audio_matches:
        if 'pixabay' in a:
            print('POTENTIAL:', a)
