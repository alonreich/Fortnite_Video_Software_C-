import os
import json
import logging
import tempfile
try:
    from system.shared_paths import SharedPaths
except ImportError:
    try:
        import sys
        sys.path.append(os.path.dirname(os.path.abspath(__file__)))

        from shared_paths import SharedPaths
    except ImportError:
        class SharedPaths:
            import tempfile
            ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
            TEMP = os.path.join(tempfile.gettempdir(), 'FVS_Temp')

class StateTransfer:
    @staticmethod
    def get_session_file():
        path = os.path.join(SharedPaths.TEMP, "fvs_session_state.json")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        return path
    @staticmethod
    def save_state(state_data: dict):
        logger = logging.getLogger("StateTransfer")
        final_path = StateTransfer.get_session_file()
        parent_dir = os.path.dirname(final_path)
        os.makedirs(parent_dir, exist_ok=True)
        fd = None
        tmp_path = None
        try:
            fd, tmp_path = tempfile.mkstemp(prefix="fvs_session_state_", suffix=".tmp", dir=parent_dir)
            with os.fdopen(fd, 'w', encoding='utf-8') as f:
                fd = None
                json.dump(state_data, f, indent=2)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_path, final_path)
            logger.info(f"Session state saved atomically to {final_path}")
        except Exception as e:
            logger.error(f"Failed to save session state: {e}")
            try:
                if fd is not None:
                    os.close(fd)
            except Exception:
                pass
            try:
                if tmp_path and os.path.exists(tmp_path):
                    os.remove(tmp_path)
            except Exception:
                pass
    @staticmethod
    def update_state(updates: dict):
        current = StateTransfer.load_state()
        current.update(updates)
        StateTransfer.save_state(current)
    @staticmethod
    def load_state() -> dict:
        path = StateTransfer.get_session_file()
        if not os.path.exists(path):
            return {}
        try:
            with open(path, 'r', encoding='utf-8') as f:
                data = json.load(f)
            logging.getLogger("StateTransfer").info("Session state loaded.")
            return data
        except json.JSONDecodeError as e:
            logging.getLogger("StateTransfer").error(f"Failed to load session state (corrupted JSON): {e}")
            try:
                broken_path = path + ".corrupted"
                if os.path.exists(broken_path):
                    os.remove(broken_path)
                os.replace(path, broken_path)
                logging.getLogger("StateTransfer").warning(f"Corrupted session file moved to: {broken_path}")
            except Exception as move_err:
                logging.getLogger("StateTransfer").warning(f"Failed to move corrupted session state: {move_err}")
            return {}
        except Exception as e:
            logging.getLogger("StateTransfer").error(f"Failed to load session state: {e}")
            return {}
    @staticmethod
    def clear_state():
        path = StateTransfer.get_session_file()
        if os.path.exists(path):
            try:
                os.remove(path)
                logging.getLogger("StateTransfer").info("Session state cleared.")
            except Exception as e:
                logging.getLogger("StateTransfer").error(f"Failed to clear session state: {e}")
