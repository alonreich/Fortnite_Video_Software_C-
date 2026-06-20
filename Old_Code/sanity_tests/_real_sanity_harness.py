from __future__ import annotations
from dataclasses import dataclass
import types
from typing import Any
import sys
import threading
import weakref

class DummyLogger:
    def info(self, *args: Any, **kwargs: Any) -> None:
        return None

    def error(self, *args: Any, **kwargs: Any) -> None:
        return None

    def warning(self, *args: Any, **kwargs: Any) -> None:
        return None
        
    def debug(self, *args: Any, **kwargs: Any) -> None:
        return None
        
    def exception(self, *args: Any, **kwargs: Any) -> None:
        return None

class DummySpinBox:
    def __init__(self, value: float):
        self._value = float(value)

    def value(self) -> float:
        return self._value

    def setValue(self, value: float) -> None:
        self._value = float(value)
        
    def blockSignals(self, b: bool) -> None:
        pass
        
    def hide(self) -> None:
        pass
        
    def show(self) -> None:
        pass

class DummyCheckBox:
    def __init__(self, checked: bool):
        self._checked = bool(checked)

    def isChecked(self) -> bool:
        return self._checked

    def setChecked(self, checked: bool) -> None:
        self._checked = bool(checked)

class DummyTimer:
    def __init__(self, active: bool = False):
        self._active = active

    def isActive(self) -> bool:
        return self._active

    def start(self, *args: Any) -> None:
        self._active = True

    def stop(self) -> None:
        self._active = False

class DummyButton:
    def __init__(self, text: str = ""):
        self._text = text
        self._enabled = True
        self._visible = True
        self.style_value = ""
        self.tooltip_value = ""

    def setText(self, text: str) -> None:
        self._text = text

    def text(self) -> str:
        return self._text

    def setIcon(self, *args: Any) -> None:
        pass

    def setEnabled(self, b: bool) -> None:
        self._enabled = b

    def isEnabled(self) -> bool:
        return self._enabled

    def setVisible(self, visible: bool) -> None:
        self._visible = bool(visible)

    def isVisible(self) -> bool:
        return self._visible

    def setStyleSheet(self, value: str) -> None:
        self.style_value = value

    def setToolTip(self, value: str) -> None:
        self.tooltip_value = value

    def setCursor(self, *args: Any) -> None:
        pass

class DummySlider:
    def __init__(self, value: int = 0):
        self._value = value
        self.time_calls: list[tuple[int, int]] = []
        self.visible_calls: list[bool] = []
        self.music_start_ms = -1
        self.music_end_ms = -1
        self._show_music = False
        self.trimmed_start_ms = 0
        self.trimmed_end_ms = 0
        self.speed_segments = []
        self.thumbnail_pos_ms = 0

    def value(self) -> int:
        return self._value

    def setValue(self, v: int) -> None:
        self._value = v

    def maximum(self) -> int:
        return 10000

    def setRange(self, *args: Any) -> None:
        pass

    def set_duration_ms(self, *args: Any) -> None:
        pass

    def reset_music_times(self) -> None:
        self.time_calls.append((0, 0))
        self.music_start_ms = -1
        self.music_end_ms = -1
        self._show_music = False
        
    def set_trim_times(self, start_ms: int, end_ms: int) -> None:
        self.time_calls.append((int(start_ms), int(end_ms)))
        self.trimmed_start_ms = int(start_ms)
        self.trimmed_end_ms = int(end_ms)
        return None

    def set_music_visible(self, visible: bool) -> None:
        self._show_music = bool(visible)

    def set_music_times(self, start_ms: int, end_ms: int) -> None:
        self.music_start_ms = int(start_ms)
        self.music_end_ms = int(end_ms)
        self._show_music = True
        self.time_calls.append((int(start_ms), int(end_ms)))

    def set_speed_segments(self, segments) -> None:
        self.speed_segments = list(segments or [])

    def get_thumbnail_pos_ms(self) -> int:
        return int(self.thumbnail_pos_ms)

    def set_thumbnail_pos_ms(self, value: int) -> None:
        self.thumbnail_pos_ms = int(value)

    def setVisible(self, visible: bool) -> None:
        self.visible_calls.append(bool(visible))

    def update(self) -> None:
        pass
        
    def blockSignals(self, *args: Any) -> None:
        return None
        
    def isSliderDown(self) -> bool:
        return False

class DummyMediaPlayer:
    def __init__(self, playing: bool = False, current_ms: int = 0, rate: float = 1.0):
        self._playing = playing
        self._time = int(current_ms)
        self._rate = float(rate)
        self._volume = 100
        self._mute = False
        self.set_time_calls: list[int] = []
        self.set_rate_calls: list[float] = []
        self.paused = 0

    def is_playing(self) -> bool:
        return self._playing

    def play(self, *args) -> None:
        self._playing = True
    @property
    def pause(self) -> bool:
        return not self._playing
    @pause.setter
    def pause(self, value: bool) -> None:
        if value and not self._playing:
            pass
        elif value and self._playing:
            self.paused += 1
        if value:
            if not hasattr(self, 'pause_calls'): self.pause_calls = 0
            self.pause_calls += 1
        self._playing = not value
    @property
    def volume(self) -> int:
        return self._volume
    @volume.setter
    def volume(self, value: int) -> None:
        self._volume = value
    @property
    def speed(self) -> float:
        return self._rate
    @speed.setter
    def speed(self, value: float) -> None:
        self._rate = value
        self.set_rate_calls.append(value)
    @property
    def time_pos(self) -> float:
        return self._time / 1000.0

    def set_time(self, ms: int) -> None:
        self._time = int(ms)
        self.set_time_calls.append(int(ms))

    def get_time(self) -> int:
        return self._time

    def set_rate(self, rate: float) -> None:
        self.speed = float(rate)

    def get_rate(self) -> float:
        return self._rate

    def audio_set_volume(self, value: int) -> None:
        self.volume = int(value)

    def audio_set_mute(self, muted: bool) -> None:
        self._mute = bool(muted)

    def set_media(self, media) -> None:
        self.media = media

    def observe_property(self, *args, **kwargs): pass

    def event_callback(self, *args, **kwargs):
        return lambda f: f

    def command(self, *args): pass

    def terminate(self): pass

class DummySignal:
    def __init__(self):
        self._slots = []

    def connect(self, slot):
        self._slots.append(slot)

    def disconnect(self, slot=None):
        pass

    def emit(self, *args, **kwargs):
        for slot in self._slots:
            try:
                slot(*args, **kwargs)
            except:
                pass

class DummyConfigManager:
    def __init__(self, config: dict[str, Any] | None = None):
        self.config = config or {}

    def save_config(self, config: dict[str, Any]) -> None:
        self.config.update(config)

    def load_config(self) -> dict[str, Any]:
        return self.config

class DummyListItem:
    def __init__(self, text: Any = "", user_data: Any | None = None):
        self._text = text if isinstance(text, str) else ""
        self._data: dict[int, Any] = {}
        self._hidden = False
        self.size_hint = None
        if user_data is not None:
            self._data[0x0100] = user_data

    def text(self) -> str:
        return self._text

    def setText(self, text: str) -> None:
        self._text = str(text)

    def setSizeHint(self, value: Any) -> None:
        self.size_hint = value

    def setData(self, role: int, value: Any) -> None:
        self._data[int(role)] = value

    def data(self, role: int) -> Any:
        return self._data.get(int(role))

    def setHidden(self, hidden: bool) -> None:
        self._hidden = bool(hidden)

    def isHidden(self) -> bool:
        return self._hidden

class DummyKeyEvent:
    def __init__(self, text: str = "", key: int | None = None):
        self._text = text
        self._key = key if key is not None else (ord(text.upper()) if text else 0)

    def text(self) -> str:
        return self._text

    def key(self) -> int:
        return int(self._key)

    def modifiers(self) -> int:
        return 0

def install_qt_mpv_stubs():
    pyqt5 = types.ModuleType("PyQt5")
    qtcore = types.ModuleType("PyQt5.QtCore")
    qtwidgets = types.ModuleType("PyQt5.QtWidgets")
    qtgui = types.ModuleType("PyQt5.QtGui")
    for n in ["Qt", "QEvent", "QThread", "QTimer", "QRect", "QPoint", "QSize", "QLocale", "QCoreApplication", "QByteArray"]:
        setattr(qtcore, n, types.SimpleNamespace())
    qtcore.Qt.LeftButton = 1
    qtcore.Qt.RightButton = 2
    qtcore.Qt.NoPen = 0
    qtcore.Qt.transparent = 0
    qtcore.Qt.ApplicationModal = 1
    qtcore.Qt.WA_NativeWindow = 1
    qtcore.Qt.PointingHandCursor = 1
    qtcore.Qt.WaitCursor = 2
    qtcore.Qt.SizeAllCursor = 3
    qtcore.Qt.NoCursor = 4
    qtcore.Qt.ArrowCursor = 5
    qtcore.Qt.IBeamCursor = 6
    qtcore.Qt.SizeHorCursor = 7
    qtcore.Qt.SizeVerCursor = 8
    qtcore.Qt.SplitHCursor = 9
    qtcore.Qt.SplitVCursor = 10
    qtcore.Qt.OpenHandCursor = 11
    qtcore.Qt.ClosedHandCursor = 12
    qtcore.Qt.ControlModifier = 0x04000000
    qtcore.Qt.ShiftModifier = 0x02000000
    qtcore.Qt.NoModifier = 0
    qtcore.Qt.Key_Space = 0x20
    qtcore.Qt.Key_Backspace = 0x01000003
    qtcore.Qt.Key_Left = 0x01000012
    qtcore.Qt.Key_Right = 0x01000014
    qtcore.Qt.Key_Up = 0x01000013
    qtcore.Qt.Key_Down = 0x01000015
    qtcore.Qt.Key_V = 0x56
    qtcore.Qt.Key_Delete = 0x01000007
    qtcore.Qt.Key_BracketLeft = 0x5b
    qtcore.Qt.Key_BracketRight = 0x5d
    qtcore.Qt.Key_F11 = 0x0100003a
    qtcore.Qt.Key_F12 = 0x0100003b
    qtcore.Qt.UserRole = 0x0100
    qtcore.Qt.BottomDockWidgetArea = 0x8
    qtcore.Qt.RightDockWidgetArea = 0x2
    qtcore.Qt.LeftDockWidgetArea = 0x1
    qtcore.Qt.Horizontal = 0x1
    qtcore.Qt.Vertical = 0x2
    qtcore.Qt.Alignment = lambda *a: MockObject()
    qtcore.Qt.AlignCenter = 0x84
    qtcore.Qt.AlignLeft = 0x01
    qtcore.Qt.AlignRight = 0x02
    qtcore.Qt.AlignTop = 0x20
    qtcore.Qt.AlignBottom = 0x40
    qtcore.Qt.AlignHCenter = 0x04
    qtcore.Qt.AlignVCenter = 0x80
    qtcore.Qt.KeepAspectRatio = 1
    qtcore.Qt.KeepAspectRatioByExpanding = 2
    qtcore.Qt.SmoothTransformation = 1
    qtcore.Qt.FastTransformation = 0
    qtcore.Qt.IgnoreAspectRatio = 0
    qtcore.Qt.NoFocus = 0
    qtcore.Qt.WA_DontCreateNativeAncestors = 101
    qtcore.Qt.WA_TranslucentBackground = 120
    qtcore.Qt.WA_TransparentForMouseEvents = 121
    qtcore.Qt.WA_StaticContents = 122
    qtcore.Qt.WA_NoSystemBackground = 123
    qtcore.Qt.WA_OpaquePaintEvent = 125
    qtcore.Qt.Tool = 0x00000005
    qtcore.Qt.FramelessWindowHint = 0x00000800
    qtcore.Qt.WA_ShowWithoutActivating = 124
    qtcore.Qt.KeyboardModifiers = lambda *a: 0
    qtcore.Qt.SortOrder = lambda *a: 0
    qtcore.Qt.AscendingOrder = 0
    qtcore.Qt.DescendingOrder = 1
    qtcore.QEvent.KeyPress = 6
    qtcore.QEvent.MouseMove = 5
    qtcore.QEvent.MouseButtonPress = 2
    qtcore.QEvent.MouseButtonRelease = 3
    qtcore.QEvent.Enter = 10
    qtcore.QEvent.Leave = 11
    qtcore.QEvent.ShortcutOverride = 51
    qtcore.QEvent.ContextMenu = 8
    
    def pyqtSignal(*args, **kwargs):
        return DummySignal()
    qtcore.pyqtSignal = pyqtSignal
    
    def _generic_void(*args, **kwargs): return MockObject()

    class _Point:
        def __init__(self, x: int = 0, y: int = 0):
            self._x = int(x)
            self._y = int(y)

        def x(self) -> int:
            return self._x

        def y(self) -> int:
            return self._y

        def manhattanLength(self) -> int:
            return abs(self._x) + abs(self._y)

        def __sub__(self, other):
            return _Point(self._x - int(other.x()), self._y - int(other.y()))

    class _Size:
        def __init__(self, w: int = 0, h: int = 0):
            self._w = int(w)
            self._h = int(h)

        def width(self) -> int:
            return self._w

        def height(self) -> int:
            return self._h

    class _Rect:
        def __init__(self, *args):
            if len(args) == 2 and hasattr(args[0], "x") and hasattr(args[1], "width"):
                self._x = int(args[0].x())
                self._y = int(args[0].y())
                self._w = int(args[1].width())
                self._h = int(args[1].height())
            else:
                vals = list(args) + [0, 0, 0, 0]
                self._x, self._y, self._w, self._h = map(int, vals[:4])

        def x(self) -> int: return self._x

        def y(self) -> int: return self._y

        def left(self) -> int: return self._x

        def top(self) -> int: return self._y

        def width(self) -> int: return self._w

        def height(self) -> int: return self._h

        def right(self) -> int: return self._x + self._w

        def bottom(self) -> int: return self._y + self._h

        def isValid(self) -> bool: return self._w > 0 and self._h > 0

        def center(self): return _Point(self._x + self._w // 2, self._y + self._h // 2)

        def contains(self, point) -> bool:
            return self.left() <= int(point.x()) <= self.right() and self.top() <= int(point.y()) <= self.bottom()

    class MockObject:
        _app_instance = None
        TicksBothSides = 3
        Accepted = 1
        StackAll = 1
        Expanding = 1
        Fixed = 2
        Preferred = 3
        Minimum = 4
        Maximum = 5
        MinimumExpanding = 6
        Ignored = 7
        SP_MediaPlay = 1
        SP_MediaPause = 2
        SP_MediaStop = 3
        SP_MediaSkipForward = 4
        SP_MediaSkipBackward = 5
        SP_MediaVolume = 6
        SP_MediaVolumeMuted = 7
        SP_MessageBoxInformation = 8
        SP_MessageBoxWarning = 9
        SP_MessageBoxCritical = 10
        SP_MessageBoxQuestion = 11
        OutCubic = 1
        Rectangle = 1
        ExtendedSelection = 1
        SelectRows = 1
        DragDrop = 1
        DontUseNativeDialog = 1
        ExistingFiles = 1
        AcceptRole = 1
        RejectRole = 2
        ActionRole = 3

        def __init__(self, *args, **kwargs):
            self._signals = {}
            self._visible = True
            self._value = 0
            self._text = ""
            self._items = []
            self._item_widgets = {}
            self._current = None
            if self.__class__.__name__ in {"QApplication", "QCoreApplication"}:
                MockObject._app_instance = self

        def __getattr__(self, name):
            if name.startswith("_"):
                raise AttributeError(name)
            if name.endswith("_signal") or name in ["valueChanged", "toggled", "clicked", "sliderPressed", "sliderReleased", "sliderMoved", "rangeChanged", "trim_times_changed", "currentTextChanged", "editingFinished", "progress", "finished", "error", "level_signal", "recording_started", "recording_finished", "playhead_updated", "time_updated", "state_updated", "data_changed", "file_dropped", "clip_selected", "seek_request", "clip_split_requested", "param_changed", "play_requested", "interaction_started", "interaction_ended", "audio_analysis_finished", "progress_started", "progress_updated", "progress_finished", "waveform_ready", "thumbnail_ready", "timeout", "triggered", "sortIndicatorChanged"]:
                if name not in self._signals: self._signals[name] = DummySignal()
                return self._signals[name]
            numeric_methods = {
                "value": 0, "minimum": 0, "maximum": 100, 
                "width": 100, "height": 100, "x": 0, "y": 0, 
                "left": 0, "top": 0, "right": 100, "bottom": 100, 
                "progress": 0, "volume": 100, "playbackRate": 1, 
                "get_thumbnail_pos_ms": 0, "duration": 100
            }
            if name in numeric_methods:
                val = numeric_methods[name]
                return lambda *a, **kw: val
            allowed_prefixes = ("set", "add", "insert", "clear", "get", "has", "is", "map", "selected", "mime")
            allowed_names = {
                "show", "hide", "update", "repaint", "close", "raise_", "lower", 
                "rect", "width", "height", "size", "geometry", "sizeHint", "minimumSizeHint", 
                "move", "resize", "layout", "parent", "parentWidget", "window", 
                "findChildren", "findChild", "winId", "value", "minimum", "maximum", 
                "text", "isChecked", "isEnabled", "style", "unpolish", "polish", 
                "count", "takeAt", "objectName", "connect", "disconnect", "emit", 
                "adjustSize", "activate", "ensurePolished", "exec_", "exec", 
                "itemAt", "indexOf", "widget", "takeWidget", "setCurrentIndex", 
                "currentIndex", "setCurrentWidget", "currentWidget",
                "x", "y", "left", "top", "right", "bottom", "center",
                "viewport", "model", "header", "selectionModel",
                "horizontalHeader", "verticalHeader", "horizontalScrollBar", "verticalScrollBar",
                "start", "stop", "pause", "resume", "kill", "terminate", "wait", "sleep", "singleShot",
                "invertedAppearance", "invertedControls", "orientation", "state", "blockSignals", "signalsBlocked", "sender", "focusWidget"
            }
            if name.startswith(allowed_prefixes) or name in allowed_names:
                return _generic_void
            raise AttributeError(name)

        def __call__(self, *args, **kwargs): return MockObject()

        def setWindowTitle(self, *args): pass

        def setGeometry(self, *args): pass

        def setMinimumSize(self, *args): pass

        def setMaximumSize(self, *args): pass

        def setFixedSize(self, *args): pass

        def setLayout(self, *args): pass

        def show(self, *args): pass

        def hide(self, *args): pass

        def update(self, *args): pass

        def repaint(self, *args): pass

        def setEnabled(self, *args): pass

        def setDisabled(self, *args): pass

        def setVisible(self, *args):
            if args:
                self._visible = bool(args[0])

        def isVisible(self): return self._visible

        def setStyleSheet(self, *args): pass

        def setObjectName(self, *args): pass

        def setParent(self, *args): pass

        def installEventFilter(self, *args): pass

        def removeEventFilter(self, *args): pass

        def setAcceptDrops(self, *args): pass

        def setFocus(self, *args): pass

        def blockSignals(self, *args): return False

        def property(self, *args): return None

        def setProperty(self, *args): return True

        def rect(self, *args): return qtcore.QRect

        def size(self, *args): return qtcore.QSize

        def width(self): return 100

        def height(self): return 100

        def mapToGlobal(self, p): return p

        def mapFromGlobal(self, p): return p

        def statusBar(self): 
            return MockObject()

        def showMessage(self, *args): pass

        def addPermanentWidget(self, *args, **kwargs): pass

        def addWidget(self, *args, **kwargs): pass

        def addLayout(self, *args, **kwargs): pass

        def setContentsMargins(self, *args): pass

        def setSpacing(self, *args): pass

        def clear(self):
            self._items.clear()
            self._item_widgets.clear()

        def raise_(self): pass

        def isChecked(self): return False

        def value(self): return self._value

        def text(self): return self._text

        def setText(self, text): self._text = str(text)

        def style(self): 
            return types.SimpleNamespace(standardIcon=lambda *_: MockObject())

        def addItems(self, *args): pass

        def setCurrentIndex(self, *args): pass

        def setFixedWidth(self, *args): pass

        def setFixedHeight(self, *args): pass

        def addAction(self, *args): return MockObject()

        def addSeparator(self): pass

        def setPopupMode(self, *args): pass

        def setAutoRaise(self, *args): pass

        def setMenu(self, *args): pass

        def setSizePolicy(self, *args, **kwargs): pass

        def setRange(self, *args): pass

        def setValue(self, *args):
            if args:
                self._value = args[0]

        def setCursor(self, *args): pass

        def setToolTip(self, *args): pass

        def setCheckable(self, *args): pass

        def setChecked(self, *args): pass

        def setShortcut(self, *args): pass

        def count(self): return len(getattr(self, "_items", []))

        def item(self, i):
            try:
                return self._items[i]
            except Exception:
                return MockObject()

        def addItem(self, item):
            self._items.append(item)

        def setItemWidget(self, item, widget):
            self._item_widgets[item] = widget

        def itemWidget(self, item):
            widget = self._item_widgets.get(item)
            if widget is not None:
                return widget
            text_func = getattr(item, "text", None)
            if callable(text_func):
                return types.SimpleNamespace(name_lbl=types.SimpleNamespace(text=text_func))
            return None

        def setCurrentItem(self, item):
            self._current = item

        def scrollToItem(self, *args): pass

        def data(self, *args): return None

        def horizontalScrollBar(self): return MockObject()

        def verticalScrollBar(self): return MockObject()

        def set_duration_ms(self, *args): pass

        def set_trim_times(self, start_ms, end_ms): pass

        def reset_music_times(self): pass

        def set_music_times(self, *args): pass

        def winId(self): return 12345

        def setDockOptions(self, *args): pass

        def addDockWidget(self, *args, **kwargs): pass

        def resizeDocks(self, *args): pass

        def restoreGeometry(self, *args): pass

        def restoreState(self, *args): pass

        def saveGeometry(self): return qtcore.QByteArray

        def saveState(self): return qtcore.QByteArray

        def screenGeometry(self): return MockObject()

        def left(self): return 0

        def top(self): return 0

        def name(self): return "mock"

        def lighter(self, *args): return MockObject()

        def darker(self, *args): return MockObject()

        def setTickPosition(self, *args): pass

        def setTickInterval(self, *args): pass

        def setPageStep(self, *args): pass

        def setSingleStep(self, *args): pass

        def exec_(self, *args): return 1

        def setFocusPolicy(self, *args): pass

        def setStackingMode(self, *args): pass

        def setAttribute(self, *args, **kwargs): pass

        def setWindowFlags(self, *args): pass
        @staticmethod
        def system(): return MockObject()

        def viewport(self): return MockObject()

        def setSelectionMode(self, *args): pass

        def setSelectionBehavior(self, *args): pass

        def setDragEnabled(self, *args): pass

        def setDragDropMode(self, *args): pass

        def setDropIndicatorShown(self, *args): pass

        def indexAt(self, *args): return MockObject()

        def isValid(self): return False

        def normalized(self): return MockObject()

        def modifiers(self): return 0

        def button(self): return 1

        def pos(self): return qtcore.QPoint()

        def header(self): return MockObject()

        def setSortingEnabled(self, *args): pass

        def setSortIndicatorShown(self, *args): pass

        def setSectionsClickable(self, *args): pass

        def setStretchLastSection(self, *args): pass

        def setDefaultAlignment(self, *args): pass

        def setSortIndicator(self, *args): pass

        def setUniformRowHeights(self, *args): pass

        def setItemDelegate(self, *args): pass

        def model(self): return MockObject()

        def findChild(self, *args, **kwargs): return MockObject()

        def findChildren(self, *args, **kwargs): return [MockObject()]

        def globalPos(self): return qtcore.QPoint()
        @staticmethod
        def clipboard(): return MockObject()
        @staticmethod
        def instance(): return MockObject._app_instance

        def mimeData(self): return MockObject()

        def hasUrls(self): return False

        def hasFormat(self, *args): return False

        def directory(self): return MockObject()

        def absolutePath(self): return ""

        def addButton(self, *args, **kwargs): return MockObject()

        def setDefaultButton(self, *args): pass

        def clickedButton(self): return MockObject()

        def setFileMode(self, *args): pass

        def setOption(self, *args, **kwargs): pass

        def setDirectory(self, *args): pass

        def setSidebarUrls(self, *args): pass

    class QTimer(MockObject):
        @staticmethod
        def singleShot(_delay_ms, callback):
            if callable(callback):
                callback()

    class QApplication(MockObject):
        pass

    class QCoreApplication(QApplication):
        @staticmethod
        def setOrganizationName(*args): pass
    qtcore.QObject = MockObject
    qtcore.QThread = MockObject
    qtcore.QTimer = QTimer
    qtcore.QCoreApplication = QCoreApplication
    qtcore.QPoint = _Point
    qtcore.QSize = _Size
    qtcore.QRect = _Rect
    qtcore.QThreadPool = MockObject
    core_list = ["QObject", "QThread", "QTimer", "QThreadPool", "QPropertyAnimation", "QUrl", "QRunnable", "QPointF", "QRectF", "QLineF", "QProcess", "QBuffer", "QIODevice", "QMimeData", "QModelIndex", "QAbstractListModel", "QAbstractItemModel", "QVariant", "QTranslator", "QLibraryInfo", "QEasingCurve", "QParallelAnimationGroup", "QSequentialAnimationGroup", "QVariantAnimation", "QWaitCondition", "QMutex", "QMutexLocker", "QSemaphore", "QReadWriteLock", "QReadLocker", "QMetaObject", "QSizeF", "QLocale", "QRegularExpression", "QRegularExpressionValidator", "QStandardPaths", "QStorageInfo", "QFileSystemWatcher", "QMimeDatabase", "QMimeType", "QCommandLineParser", "QAbstractAnimation"]
    for n in core_list:
        if n == "QTimer":
            continue
        if n == "QCoreApplication":
            setattr(qtcore, n, QCoreApplication)
            continue
        if n in {"QPoint", "QSize", "QRect"}:
            continue
        setattr(qtcore, n, type(n, (MockObject,), {"system": MockObject.system}))
    qtcore.Q_ARG = lambda *a: None
    qtcore.QAbstractAnimation.Stopped = 0
    qtcore.QAbstractAnimation.Paused = 1
    qtcore.QAbstractAnimation.Running = 2
    qtcore.QAbstractAnimation.Forward = 0
    qtcore.QAbstractAnimation.Backward = 1
    
    class MockProperty:
        def __init__(self, *args, **kwargs): pass

        def __call__(self, func):
            return property(func)
    qtcore.pyqtProperty = MockProperty
    widgets_list = [
        "QWidget", "QMainWindow", "QFrame", "QDialog", "QPushButton", "QCheckBox", 
        "QSpinBox", "QDoubleSpinBox", "QProgressBar", "QSlider", "QComboBox", 
        "QScrollArea", "QGroupBox", "QLineEdit", "QTextEdit", "QPlainTextEdit", 
        "QSplitter", "QStackedWidget", "QTabWidget", "QToolButton", "QAction", 
        "QMenu", "QMenuBar", "QStatusBar", "QToolBar", "QDockWidget", "QLabel",
        "QApplication", "QMessageBox", "QVBoxLayout", "QHBoxLayout", "QGridLayout", "QStackedLayout", 
        "QStyle", "QSizePolicy", "QStyleOptionSpinBox", "QStyleOptionSlider", "QFileDialog", 
        "QListWidgetItem", "QDesktopWidget", "QActionGroup", "QAbstractButton", "QRadioButton", 
        "QTabBar", "QScrollBar", "QHeaderView", "QAbstractSpinBox", "QToolTip", "QGraphicsRectItem",
        "QGraphicsItem", "QGraphicsDropShadowEffect", "QGraphicsView", "QGraphicsScene", "QGraphicsPixmapItem",
        "QGraphicsTextItem", "QGraphicsEllipseItem", "QGraphicsPolygonItem", "QGraphicsLineItem", "QListWidget", "QSystemTrayIcon", "QInputDialog", "QUndoStack", "QUndoCommand", "QKeySequenceEdit", "QGraphicsObject", "QGraphicsSimpleTextItem", "QProgressDialog", "QGraphicsOpacityEffect", "QUndoGroup", "QUndoView", "QErrorMessage", "QShortcut", "QTreeView", "QListView", "QRubberBand", "QAbstractItemView", "QProxyStyle", "QStyledItemDelegate", "QStyleOption"
    ]
    for n in widgets_list:
        if n == "QApplication":
            setattr(qtwidgets, n, QApplication)
        elif n == "QListWidgetItem":
            setattr(qtwidgets, n, DummyListItem)
        else:
            setattr(qtwidgets, n, type(n, (MockObject,), {"clipboard": MockObject.clipboard}))
    qtwidgets.QSizePolicy = MockObject
    qtwidgets.QStyle = MockObject
    qtwidgets.QRubberBand = MockObject
    qtwidgets.QFileDialog = MockObject
    qtwidgets.QMessageBox = MockObject
    gui_list = [
        "QPixmap", "QPainter", "QColor", "QIcon", "QPen", "QCursor", "QPainterPath", 
        "QLinearGradient", "QRadialGradient", "QBrush", "QPalette", "QRegion", 
        "QDesktopServices", "QKeySequence", "QPolygon", "QBrush", "QPalette", "QShortcut", "QPolygonF", "QTransform", "QDrag", "QMovie"
    ]
    for n in gui_list:
        setattr(qtgui, n, type(n, (MockObject,), {}))

    class QFontMetrics:
        def __init__(self, *args: Any) -> None: pass

        def horizontalAdvance(self, *args: Any) -> int: return 50

        def width(self, *args: Any) -> int: return 50

        def height(self, *args: Any) -> int: return 15

    class QFont:
        def __init__(self, *args: Any) -> None: pass

        def setPointSize(self, *args: Any) -> None: pass

        def pointSize(self) -> int: return 10
    qtgui.QFont = QFont
    qtgui.QFontMetrics = QFontMetrics
    qtsvg = types.ModuleType("PyQt5.QtSvg")
    qtsvg.QSvgRenderer = type("QSvgRenderer", (MockObject,), {})
    sys.modules["PyQt5.QtSvg"] = qtsvg
    sys.modules["PyQt5"] = pyqt5
    sys.modules["PyQt5.QtCore"] = qtcore
    sys.modules["PyQt5.QtWidgets"] = qtwidgets
    sys.modules["PyQt5.QtGui"] = qtgui
    mpv_mod = types.ModuleType("mpv")

    class MockMPV:
        def __init__(self, *args, **kwargs):
            self.pause = True
            self.volume = 100
            self.speed = 1.0
            self.time_pos = 0.0
            self.duration = 100.0
            self.path = ""

        def play(self, path): self.path = path

        def stop(self): pass

        def seek(self, *args, **kwargs): pass

        def audio_add(self, path): pass

        def terminate(self): pass

        def event_callback(self, *args, **kwargs): return lambda f: f

        def observe_property(self, *args, **kwargs): pass

        def command(self, *args): pass
    mpv_mod.MPV = MockMPV
    sys.modules["mpv"] = mpv_mod
