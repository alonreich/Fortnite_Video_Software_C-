import json
import sys
sys.dont_write_bytecode = True

from utilities.merger_config import MergerConfigManager

def test_merger_config_drops_session_music_on_load_and_save(tmp_path):
    cfg_path = tmp_path / "video_merger.conf"
    cfg_path.write_text(
        json.dumps(
            {
                "last_dir": "C:/Videos",
                "music_widget": {
                    "tracks": [["C:/Music/old.mp3", 0.0, 30.0]],
                    "music_volume": 80,
                    "video_volume": 100,
                },
            }
        ),
        encoding="utf-8",
    )
    manager = MergerConfigManager(str(cfg_path))
    assert manager.config == {"last_dir": "C:/Videos"}
    manager.save_config(
        {
            "last_dir": "C:/Videos",
            "music_widget": {"tracks": [["C:/Music/new.mp3", 1.0, 20.0]]},
        }
    )
    saved = json.loads(cfg_path.read_text(encoding="utf-8"))
    assert saved == {"last_dir": "C:/Videos"}
