import os, re

def process_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    def button_replacer(match):
        button_tag = match.group(0)
        # Remove Height="..."
        button_tag = re.sub(r'\sHeight="[^"]+"', '', button_tag)
        # Remove FontSize="..."
        button_tag = re.sub(r'\sFontSize="[^"]+"', '', button_tag)
        return button_tag

    # Match <Button ... /> or <Button ...>
    new_content = re.sub(r'<Button[^>]*>', button_replacer, content)

    if new_content != content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(new_content)
        print(f"Updated {filepath}")

for root, dirs, files in os.walk('src/FortniteVideoSoftware.App'):
    for f in files:
        if f.endswith('.axaml'):
            process_file(os.path.join(root, f))
