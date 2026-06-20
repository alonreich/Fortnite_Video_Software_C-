import os, sys, pytest, tempfile, shutil, json
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton, DummyLogger
install_qt_mpv_stubs()

from developer_tools.state_manager import StateManager

def test_crop_to_main_recovery():
    """
    Test if the StateManager correctly saves and loads application state.
    Success: Data loaded from a state file matches the data originally saved.
    """
    logger = DummyLogger()
    manager = StateManager(logger=logger)
    crop_state = {
        "video_path": "C:/videos/fortnite.mp4",
        "crop_box": {"x": 100, "y": 200, "w": 300, "h": 400},
        "selected_layers": ["hp", "loot"]
    }
    state_file = manager.save_application_state(crop_state)
    assert os.path.exists(state_file)
    loaded_state = manager.load_application_state(state_file)
    assert loaded_state == crop_state
    if os.path.exists(state_file):
        os.remove(state_file)
    assert manager.load_application_state("non_existent_state.json") == None
    undo_val = [0]

    def undo_func(): 
        undo_val[0] = 1
        return True

    def redo_func():
        undo_val[0] = 2
        return True
    manager.add_undo_action("test", "test action", undo_func, redo_func)
    assert manager.can_undo()
    assert not manager.can_redo()
    manager.undo()
    assert undo_val[0] == 1
    assert not manager.can_undo()
    assert manager.can_redo()
    manager.redo()
    assert undo_val[0] == 2
    assert manager.can_undo()
    assert not manager.can_redo()
