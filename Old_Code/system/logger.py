import logging
import os
import sys
import traceback
from system.live_logging import ReopenableFileHandler

class LogFileStream:
    def __init__(self, logger, level=logging.INFO, original_stream=None):
        self.logger = logger
        self.level = level
        self.buffer = ""
        self.original_stream = original_stream
    
    def write(self, message):
        if message:
            if self.original_stream:
                try:
                    self.original_stream.write(message)
                    self.original_stream.flush()
                except (OSError, ValueError):
                    pass
                except Exception:
                    pass
            self.buffer += message
            if '\n' in self.buffer:
                lines = self.buffer.split('\n')
                for line in lines[:-1]:
                    if line.strip():
                        self.logger.log(self.level, line)
                self.buffer = lines[-1]
    
    def flush(self):
        if self.original_stream:
            try:
                self.original_stream.flush()
            except (OSError, ValueError):
                pass
            except Exception:
                pass
        if self.buffer.strip():
            self.logger.log(self.level, self.buffer)
            self.buffer = ""
    
    def isatty(self):
        return False

def setup_logger(base_dir, log_filename, logger_name):
    logger = logging.getLogger(logger_name)
    if logger.handlers:
        return logger
    logger.setLevel(logging.INFO)
    app_prefix = logger_name.lower().replace(" ", "_")
    log_dir = os.path.join(base_dir, "logs")
    os.makedirs(log_dir, exist_ok=True)
    if not log_filename.startswith(app_prefix):
        final_log_filename = f"{app_prefix}_{log_filename}"
    else:
        final_log_filename = log_filename
    log_path = os.path.join(log_dir, final_log_filename)
    fmt = logging.Formatter("%(asctime)s | %(name)-12s | %(levelname)-8s | %(message)s", datefmt="%Y-%m-%d %H:%M:%S")
    file_handler = ReopenableFileHandler(log_path, maxBytes=10 * 1024 * 1024, backupCount=5, encoding="utf-8")
    file_handler.setFormatter(fmt)
    logger.addHandler(file_handler)
    for handler in logger.handlers[:]:
        if isinstance(handler, logging.StreamHandler) and handler.stream in (sys.stdout, sys.stderr):
            logger.removeHandler(handler)
    original_stdout = sys.__stdout__ if hasattr(sys, '__stdout__') else sys.stdout
    original_stderr = sys.__stderr__ if hasattr(sys, '__stderr__') else sys.stderr
    log_stream = LogFileStream(logger, logging.INFO, original_stream=original_stdout)
    sys.stdout = log_stream
    sys.stderr = LogFileStream(logger, logging.ERROR, original_stream=original_stderr)

    def log_uncaught_exception(exc_type, exc_value, exc_traceback):
        if issubclass(exc_type, KeyboardInterrupt):
            sys.__excepthook__(exc_type, exc_value, exc_traceback)
            return
        error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
        logger.critical(f"Uncaught exception: {error_msg}")
        if hasattr(sys, '__stderr__'):
            sys.__stderr__.write(f"Uncaught exception (also logged to {log_path}):\n{error_msg}\n")
    sys.excepthook = log_uncaught_exception
    logging.addLevelName(logging.CRITICAL, "FATAL")
    logging.captureWarnings(True)
    logging.captureWarnings(True)
    return logger
