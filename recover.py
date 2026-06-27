import json
import re

transcript_path = r'C:\Users\alon\.gemini\antigravity-cli\brain\a7dceaef-af39-4f00-82ed-3622c7abbc57\.system_generated\logs\transcript_full.jsonl'
output_path = r'C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.App\MusicWizardWindow.axaml.cs'

lines_dict = {}

with open(transcript_path, 'r', encoding='utf-8') as f:
    for line in f:
        try:
            entry = json.loads(line)
        except:
            continue
        if entry.get('type') == 'TOOL_RESPONSE' and 'view_file' in str(entry.get('content', '')):
            content_str = str(entry.get('content'))
            if 'MusicWizardWindow.axaml.cs' in content_str:
                # find the "Showing lines X to Y" part or similar, and extract lines
                # the format is "<line_number>: <original_line>"
                for m in re.finditer(r'^(\d+):\s(.*)$', content_str, re.MULTILINE):
                    lineno = int(m.group(1))
                    linetext = m.group(2)
                    lines_dict[lineno] = linetext

if lines_dict:
    max_line = max(lines_dict.keys())
    with open(output_path, 'w', encoding='utf-8') as out:
        for i in range(1, max_line + 1):
            out.write(lines_dict.get(i, '') + '\n')
    print(f"Recovered {max_line} lines to {output_path}")
else:
    print("Failed to find lines.")
