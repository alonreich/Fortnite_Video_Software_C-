import subprocess
import shutil
import os
import sys
from PyQt5.QtWidgets import (
    QFileDialog,
    QDesktopWidget,
    QHeaderView,
    QTreeView,
    QListView,
    QComboBox,
    QRubberBand,
    QAbstractItemView,
    QPushButton,
    QMenu,
    QMessageBox,
    QProxyStyle,
    QStyle,
    QInputDialog,
    QLineEdit,
    QApplication,
    QStyledItemDelegate,
    QFrame,
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QSizePolicy,
    QGridLayout,
    QSplitter,
)

from system.utils import MPVSafetyManager
from PyQt5.QtCore import (
    QByteArray,
    Qt,
    QUrl,
    QPoint,
    QRect,
    QRectF,
    QEvent,
    QObject,
    QTimer,
    QMimeData,
    QSize,
    pyqtSignal,
    QPointF,
)

from PyQt5.QtGui import (
    QColor,
    QPalette,
    QPainter,
    QPolygonF,
    QPixmap,
)
from PyQt5.QtSvg import QSvgRenderer

class _CenteredTextDelegate(QStyledItemDelegate):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._fvs_dialog = None

    def initStyleOption(self, option, index):
        super().initStyleOption(option, index)
        if index.column() == 0:
            option.displayAlignment = Qt.AlignLeft | Qt.AlignVCenter
        else:
            option.displayAlignment = Qt.AlignCenter
        if self._fvs_dialog is None:
            dlg = self.parent()
            while dlg and not hasattr(dlg, "_cut_file_paths"):
                dlg = dlg.parent()
            self._fvs_dialog = dlg
        dialog = self._fvs_dialog
        if not dialog:
            return
        model = index.model()
        idx = index
        while hasattr(model, "sourceModel"):
            source = model.sourceModel()
            if not source:
                break
            idx = model.mapToSource(idx)
            model = source
        if hasattr(model, "filePath"):
            file_path = model.filePath(idx)
            if file_path in dialog._cut_file_paths:
                option.palette.setColor(QPalette.Text, QColor("#808080"))

    def sizeHint(self, option, index):
        s = super().sizeHint(option, index)
        return QSize(s.width(), s.height())

class RubberBandHelper(QObject):
    def __init__(self, tree_view: QTreeView):
        super().__init__(tree_view)
        self._tree = tree_view
        self._vp = tree_view.viewport()
        self._rb = QRubberBand(QRubberBand.Rectangle, self._vp)
        self._origin = QPoint()
        self._dragging = False
        self._configure_tree_view()
        self._configure_viewport()

    def _configure_tree_view(self):
        self._tree.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self._tree.setSelectionBehavior(QAbstractItemView.SelectRows)
        self._tree.setDragEnabled(True)
        self._tree.setAcceptDrops(True)
        self._tree.setDragDropMode(QAbstractItemView.DragDrop)
        self._tree.setDropIndicatorShown(True)

    def _configure_viewport(self):
        self._vp.setMouseTracking(True)
        self._vp.installEventFilter(self)

    def eventFilter(self, obj, event):
        if obj is not self._vp:
            return False
        et = event.type()
        if et == QEvent.MouseButtonPress:
            return self._handle_press(event)
        if et == QEvent.MouseMove:
            return self._handle_move(event)
        if et == QEvent.MouseButtonRelease:
            return self._handle_release(event)
        return False

    def _handle_press(self, event):
        if event.button() != Qt.LeftButton:
            return False
        if self._tree.indexAt(event.pos()).isValid():
            return False
        self._origin = event.pos()
        self._dragging = True
        self._rb.setGeometry(QRect(self._origin, self._origin))
        self._rb.show()
        mods = event.modifiers()
        if not (mods & (Qt.ControlModifier | Qt.ShiftModifier)):
            sm = self._tree.selectionModel()
            if sm is not None:
                sm.clearSelection()
        return True

    def _handle_move(self, event):
        if not self._dragging:
            return False
        rect = QRect(self._origin, event.pos()).normalized()
        self._rb.setGeometry(rect)
        mods = event.modifiers()
        self._select_rows_in_rect(rect, mods)
        return True

    def _handle_release(self, event):
        if not self._dragging:
            return False
        if event.button() != Qt.LeftButton:
            return False
        self._dragging = False
        self._rb.hide()
        return True

    def _select_rows_in_rect(self, rect: QRect, modifiers: Qt.KeyboardModifiers):
        sm = self._tree.selectionModel()
        model = self._tree.model()
        root = self._tree.rootIndex()
        if sm is None or model is None:
            return
        additive = bool(modifiers & (Qt.ControlModifier | Qt.ShiftModifier))
        row_count = model.rowCount(root)
        if row_count <= 0:
            return
        for row in range(row_count):
            idx = model.index(row, 0, root)
            if not idx.isValid():
                continue
            vrect = self._tree.visualRect(idx)
            hit = rect.intersects(vrect)
            if hit:
                sm.select(idx, sm.Select | sm.Rows)
            else:
                if not additive:
                    sm.select(idx, sm.Deselect | sm.Rows)

    def set_enabled(self, enabled: bool):
        if enabled:
            self._vp.installEventFilter(self)
        else:
            self._vp.removeEventFilter(self)
            self._rb.hide()
            self._dragging = False

    def is_dragging(self) -> bool:
        return self._dragging

    def hide_rubberband(self):
        self._rb.hide()
        self._dragging = False

    def show_rubberband(self):
        self._rb.show()

    def rubberband_geometry(self) -> QRect:
        return self._rb.geometry()

    def set_rubberband_geometry(self, rect: QRect):
        self._rb.setGeometry(rect)

    def reset_origin(self, pos: QPoint):
        self._origin = pos

    def origin(self) -> QPoint:
        return self._origin

class CenterHeaderProxyStyle(QProxyStyle):
    def drawPrimitive(self, element, option, painter, widget=None):
        if element == QStyle.PE_IndicatorHeaderArrow:
            return
        super().drawPrimitive(element, option, painter, widget)

    def drawControl(self, element, option, painter, widget=None):
        if element == QStyle.CE_HeaderLabel:
            original_indicator = 0
            if hasattr(option, "sortIndicator"):
                original_indicator = option.sortIndicator
            option.sortIndicator = 0
            option.textAlignment = Qt.AlignCenter
            super().drawControl(element, option, painter, widget)
            option.sortIndicator = original_indicator
            if option.sortIndicator:
                arrow_color = QColor("#ecf0f1")
                painter.save()
                painter.setRenderHint(painter.Antialiasing)
                painter.setBrush(arrow_color)
                painter.setPen(Qt.NoPen)
                r = option.rect
                cx = r.center().x()
                cy = r.center().y()
                fm = option.fontMetrics
                text_width = fm.width(option.text)
                padding = 26
                left_x = int(cx - (text_width / 2) - padding)
                right_x = int(cx + (text_width / 2) + padding)
                s = 4
                is_down = (option.sortIndicator == 1)

                def draw_arrow(x, y, down):
                    if down:
                        painter.drawConvexPolygon(QPoint(x - s, y - s), QPoint(x + s, y - s), QPoint(x, y + s))
                    else:
                        painter.drawConvexPolygon(QPoint(x - s, y + s), QPoint(x + s, y + s), QPoint(x, y - s))
                draw_arrow(left_x, cy, is_down)
                draw_arrow(right_x, cy, is_down)
                painter.restore()
            return
        super().drawControl(element, option, painter, widget)

class CenterAlignedTreeView(QTreeView):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._align_timer = None

    def showEvent(self, event):
        super().showEvent(event)
        self._apply_center_alignment()

    def setModel(self, model):
        super().setModel(model)
        self._apply_center_alignment()

    def _apply_center_alignment(self):
        model = self.model()
        if model is None:
            return
        try:
            for col in range(model.columnCount(self.rootIndex())):
                self.setTextElideMode(Qt.ElideRight)
                self.setColumnWidth(col, self.columnWidth(col))
        except Exception:
            pass

class ClickableLabel(QLabel):
    clicked = pyqtSignal()
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.setCursor(Qt.PointingHandCursor)
    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton:
            self.clicked.emit()
        super().mouseReleaseEvent(event)

class SVGSeekButton(QPushButton):
    def __init__(self, direction="fwd", parent=None):
        super().__init__("", parent)
        self.direction = direction
        self.setFixedSize(65, 30)
        self.setCursor(Qt.PointingHandCursor)
        self.setFlat(True)
        self.setAutoFillBackground(False)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_NoSystemBackground)
        self._hover = False
        self._pressed = False
        self.setStyleSheet("background: transparent; border: none;")
        
        # Load local SVG assets
        base_path = r"C:\Users\alon\Downloads"
        file_name = "f7--forward-fill.svg" if direction == "fwd" else "f7--backward-fill.svg"
        self.svg_path = os.path.join(base_path, file_name)
        self.renderer = None
        if os.path.exists(self.svg_path):
            self.renderer = QSvgRenderer(self.svg_path)

    def enterEvent(self, event):
        self._hover = True
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event):
        self._hover = False
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._pressed = True
            self.update()
        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._pressed = False
            self.update()
        super().mouseReleaseEvent(event)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        
        # Define color based on state
        from developer_tools.config import UI_COLORS
        color_hex = UI_COLORS.PRIMARY
        if self._pressed:
            color_hex = UI_COLORS.PRIMARY_PRESSED
        elif self._hover:
            color_hex = UI_COLORS.BORDER_ACCENT
        
        if self.renderer and self.renderer.isValid():
            from PyQt5.QtGui import QImage
            # Use QImage for better transparency handling on top of native windows
            img = QImage(self.size(), QImage.Format_ARGB32)
            img.fill(Qt.transparent)
            
            p = QPainter(img)
            svg_size = 24
            target_rect = QRect(int((self.width()-svg_size)/2), int((self.height()-svg_size)/2), svg_size, svg_size)
            self.renderer.render(p, QRectF(target_rect))
            
            # Apply color tint
            p.setCompositionMode(QPainter.CompositionMode_SourceIn)
            p.fillRect(img.rect(), QColor(color_hex))
            p.end()
            
            painter.drawImage(0, 0, img)
        else:
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(color_hex))
            y_mid = self.height() / 2
            h_half = 9; w = 11; gap = 2
            total_w = (w * 2) + gap
            start_x = (self.width() - total_w) / 2
            
            def draw_tri(x):
                poly = QPolygonF()
                if self.direction == "fwd":
                    poly << QPointF(x, y_mid - h_half) << QPointF(x + w, y_mid) << QPointF(x, y_mid + h_half)
                else:
                    poly << QPointF(x + w, y_mid - h_half) << QPointF(x, y_mid) << QPointF(x + w, y_mid + h_half)
                painter.drawPolygon(poly)
            draw_tri(start_x)
            draw_tri(start_x + w + gap)

class CustomFileDialog(QFileDialog):
    def __init__(self, *args, config=None, **kwargs):
        super(CustomFileDialog, self).__init__(*args, **kwargs)
        self.config = config
        self.setObjectName("CustomFileDialog")
        self._rb_helper = None
        self.tree_view = None
        self.list_view = None
        self._text_delegate = None
        self._header_style = None
        self._cut_file_paths = set()
        self._preview_path = None
        self._preview_source_view = None
        self._preview_player = None
        self._preview_panel = None
        self._preview_video = None
        self._preview_title = None
        self._preview_status = None
        self._preview_timer = QTimer(self)
        self._preview_timer.setSingleShot(True)
        self._preview_timer.timeout.connect(self._preview_current_selection)
        self._init_dialog_flags()
        self._init_modes()
        self._init_title()
        self._apply_styles()
        self._bind_tree_view()
        self._setup_preview_panel()
        self._restructure_bottom_layout()
        self._setup_lookin_width()
        self._setup_sidebar()
        self._tune_buttons()

    def _init_dialog_flags(self):
        self.setOption(QFileDialog.DontUseNativeDialog, True)

    def _init_modes(self):
        self.setFileMode(QFileDialog.ExistingFiles)

    def _init_title(self):
        self.setWindowTitle("Select Video File(s)")

    def _apply_styles(self):
        from developer_tools.config import UI_COLORS, UI_LAYOUT
        self.setStyleSheet(
            f"""
            QFileDialog#CustomFileDialog {{
                background-color: {UI_COLORS.BACKGROUND_MEDIUM};
                color: {UI_COLORS.TEXT_PRIMARY};
            }}
            QFileDialog#CustomFileDialog QWidget {{
                background-color: {UI_COLORS.BACKGROUND_MEDIUM};
                color: {UI_COLORS.TEXT_PRIMARY};
            }}
            QFileDialog#CustomFileDialog QLabel {{
                color: {UI_COLORS.TEXT_PRIMARY};
            }}
            QFileDialog#CustomFileDialog QHeaderView::section {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                color: {UI_COLORS.TEXT_PRIMARY};
                padding: 4px;
                padding-right: 24px;
                border: 1px solid {UI_COLORS.BORDER_DARK};
            }}
            QFileDialog#CustomFileDialog QTreeView {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 1px solid {UI_COLORS.BORDER_DARK};
            }}
            QFileDialog#CustomFileDialog QListView {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 1px solid {UI_COLORS.BORDER_DARK};
            }}
            QFileDialog#CustomFileDialog QPushButton {{
                color: #ffffff;
                border-style: solid;
                border-radius: 4px;
                font-weight: bold;
                font-size: 10px;
                padding: 4px 8px;
                height: 32px;
                min-height: 32px;
                max-height: 32px;
                min-width: 110px;
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3a8db0, stop:0.1 #2d7da1, stop:1 #1a5276);
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 2px solid rgba(0, 0, 0, 0.6);
                border-right: 2px solid rgba(0, 0, 0, 0.6);
                qproperty-cursor: "PointingHandCursor";
            }}
            QFileDialog#CustomFileDialog QPushButton:hover {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QFileDialog#CustomFileDialog QPushButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d2c3d, stop:1 #1a5276);
                border-top: 2px solid rgba(0, 0, 0, 0.7);
                border-left: 2px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: 6px;
                padding-left: 10px;
                padding-bottom: 2px;
                padding-right: 6px;
            }}
            QFileDialog#CustomFileDialog QPushButton#openButton {{
                color: #ffffff;
                border-style: solid;
                border-radius: 4px;
                font-weight: bold;
                font-size: 10px;
                padding: 4px 8px;
                height: 32px;
                min-height: 32px;
                max-height: 32px;
                min-width: 110px;
                max-width: 110px;
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3a8db0, stop:0.1 #2d7da1, stop:1 #1a5276);
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 2px solid rgba(0, 0, 0, 0.6);
                border-right: 2px solid rgba(0, 0, 0, 0.6);
                qproperty-cursor: "PointingHandCursor";
            }}
            QFileDialog#CustomFileDialog QPushButton#openButton:hover {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QFileDialog#CustomFileDialog QPushButton#openButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d2c3d, stop:1 #1a5276);
                border-top: 2px solid rgba(0, 0, 0, 0.7);
                border-left: 2px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: 6px;
                padding-left: 10px;
                padding-bottom: 2px;
                padding-right: 6px;
            }}
            QFileDialog#CustomFileDialog QPushButton#cancelButton {{
                color: #ffffff;
                border-style: solid;
                border-radius: 4px;
                font-weight: bold;
                font-size: 10px;
                padding: 4px 8px;
                height: 32px;
                min-height: 32px;
                max-height: 32px;
                min-width: 110px;
                max-width: 110px;
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #a94a4a, stop:0.1 #963d3d, stop:1 #782b2b);
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 2px solid rgba(0, 0, 0, 0.6);
                border-right: 2px solid rgba(0, 0, 0, 0.6);
                qproperty-cursor: "PointingHandCursor";
            }}
            QFileDialog#CustomFileDialog QPushButton#cancelButton:hover {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QFileDialog#CustomFileDialog QPushButton#cancelButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #4d1515, stop:1 #782b2b);
                border-top: 2px solid rgba(0, 0, 0, 0.7);
                border-left: 2px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: 6px;
                padding-left: 10px;
                padding-bottom: 2px;
                padding-right: 6px;
            }}
            QFileDialog#CustomFileDialog QFrame#filePreviewPanel {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 1px solid {UI_COLORS.BORDER_DARK};
                border-radius: 8px;
            }}
            QFileDialog#CustomFileDialog QLabel#filePreviewTitle {{
                color: {UI_COLORS.BORDER_ACCENT};
                font-size: 12px;
                font-weight: bold;
                padding: 4px;
            }}
            QFileDialog#CustomFileDialog QLabel#filePreviewStatus {{
                color: {UI_COLORS.BORDER_ACCENT};
                font-size: 11px;
                padding: 4px;
            }}
            QFileDialog#CustomFileDialog QLabel#filePreviewTitle:hover,
            QFileDialog#CustomFileDialog QLabel#filePreviewStatus:hover {{
                color: #9cdffa;
            }}
            QFileDialog#CustomFileDialog QFrame#filePreviewVideo {{
                background-color: #05080c;
                border: 1px solid {UI_COLORS.BORDER_DARK};
            }}
            QFileDialog#CustomFileDialog QRubberBand {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
                background-color: transparent;
            }}
            QFileDialog#CustomFileDialog QTreeView::item:selected,
            QFileDialog#CustomFileDialog QListView::item:selected {{
                background-color: {UI_COLORS.PRIMARY_PRESSED};
                color: {UI_COLORS.TEXT_PRIMARY};
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QFileDialog#CustomFileDialog QTreeView::item:hover,
            QFileDialog#CustomFileDialog QListView::item:hover {{
                background-color: {UI_COLORS.BACKGROUND_LIGHT};
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QLineEdit {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 1px solid {UI_COLORS.BORDER_MEDIUM};
                border-radius: 4px;
                padding: 4px 8px;
                color: {UI_COLORS.TEXT_PRIMARY};
                font-size: 12px;
                height: 28px;
                min-height: 28px;
                max-height: 28px;
            }}
            QLineEdit:hover, QLineEdit:focus {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QComboBox {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 1px solid {UI_COLORS.BORDER_MEDIUM};
                border-radius: 4px;
                padding: 4px 8px;
                color: {UI_COLORS.TEXT_PRIMARY};
                font-size: 12px;
                height: 28px;
                min-height: 28px;
                max-height: 28px;
            }}
            QComboBox:hover, QComboBox:focus {{
                border: 1px solid {UI_COLORS.BORDER_ACCENT};
            }}
            QComboBox QAbstractItemView {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                color: {UI_COLORS.TEXT_PRIMARY};
            }}
            QFileDialog#CustomFileDialog QMenu {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                color: {UI_COLORS.TEXT_PRIMARY};
                border: 1px solid {UI_COLORS.BORDER_DARK};
            }}
            QFileDialog#CustomFileDialog QMenu::item {{
                padding: 10px 25px;
                margin: 2px 0px;
            }}
            QFileDialog#CustomFileDialog QMenu::item:selected {{
                background-color: {UI_COLORS.PRIMARY_PRESSED};
            }}
            QFileDialog#CustomFileDialog QMenu::separator {{
                height: 2px;
                background: {UI_COLORS.BORDER_DARK};
                margin: 2px 0px;
            }}
            QFileDialog#CustomFileDialog QLabel#backButton,
            QFileDialog#CustomFileDialog QLabel#forwardButton,
            QFileDialog#CustomFileDialog QLabel#parentDirButton,
            QFileDialog#CustomFileDialog QLabel#newFolderButton {{
                min-width: 50px;
            }}
            """
        )

    def _bind_tree_view(self):
        self.tree_view = self.findChild(QTreeView)
        if self.tree_view:
            header = self.tree_view.header()
            self.tree_view.setSortingEnabled(True)
            header.setSortIndicatorShown(True)
            header.setSectionsClickable(True)
            header.setStretchLastSection(False)
            header.sortIndicatorChanged.connect(self._save_sort_state)
            self._header_style = CenterHeaderProxyStyle(header.style())
            header.setStyle(self._header_style)
            header.setDefaultAlignment(Qt.AlignCenter)
            self.restore_state(header)
            try:
                col = header.sortIndicatorSection()
                if col < 0:
                    header.setSortIndicator(0, Qt.AscendingOrder)
            except Exception:
                pass
            self.tree_view.setUniformRowHeights(True)
            try:
                self.tree_view.setAllColumnsShowFocus(True)
            except Exception:
                pass
            self._text_delegate = _CenteredTextDelegate(self.tree_view)
            self.tree_view.setItemDelegate(self._text_delegate)
            if self.tree_view.model():
                try:
                    self.tree_view.model().setReadOnly(False)
                except Exception:
                    pass
            self._rb_helper = RubberBandHelper(self.tree_view)
            self._bind_preview_selection(self.tree_view)
        self.list_view = self.findChild(QListView, "listView")
        self._bind_preview_selection(self.list_view)
        self._install_silent_delete(self.tree_view)
        self._install_silent_delete(self.list_view)

    def _setup_preview_panel(self):
        if self._preview_panel is not None:
            return
        layout = self.layout()
        if layout is None:
            return
        self._preview_panel = QFrame(self)
        self._preview_panel.setObjectName("filePreviewPanel")
        self._preview_panel.setMinimumWidth(320)
        self._preview_panel.setMaximumWidth(800)
        self._preview_panel.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Expanding)
        preview_layout = QVBoxLayout(self._preview_panel)
        preview_layout.setContentsMargins(10, 10, 10, 10)
        preview_layout.setSpacing(8)
        self._preview_title = ClickableLabel("Video Preview")
        self._preview_title.setObjectName("filePreviewTitle")
        self._preview_title.setAlignment(Qt.AlignCenter)
        self._preview_title.clicked.connect(self._launch_default_player)
        self._preview_video = QFrame(self._preview_panel)
        self._preview_video.setObjectName("filePreviewVideo")
        self._preview_video.setMinimumSize(320, 220)
        self._preview_video.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self._preview_video.setAttribute(Qt.WA_DontCreateNativeAncestors)
        self._preview_video.setAttribute(Qt.WA_NativeWindow)
        self._preview_video.setAttribute(Qt.WA_OpaquePaintEvent)
        self._preview_video.setAttribute(Qt.WA_NoSystemBackground)
        self._preview_video.setAutoFillBackground(False)
        self._preview_status = ClickableLabel("Select a video")
        self._preview_status.setObjectName("filePreviewStatus")
        self._preview_status.setAlignment(Qt.AlignCenter)
        self._preview_status.setWordWrap(True)
        self._preview_status.clicked.connect(self._launch_default_player)
        
        # Bulletproof centering container
        self.seek_controls_widget = QWidget(self._preview_panel)
        self.seek_controls_widget.setAttribute(Qt.WA_TransparentForMouseEvents, False)
        self.seek_controls_widget.setAttribute(Qt.WA_TranslucentBackground)
        self.seek_controls_widget.setStyleSheet("background: transparent;")
        self.seek_controls_widget.hide()
        
        seek_layout = QHBoxLayout(self.seek_controls_widget)
        seek_layout.setContentsMargins(0, 0, 0, 0)
        seek_layout.setSpacing(20)
        
        self.btn_seek_back = SVGSeekButton(direction="back", parent=self.seek_controls_widget)
        self.btn_seek_fwd = SVGSeekButton(direction="fwd", parent=self.seek_controls_widget)
        
        self.btn_seek_back.clicked.connect(lambda: self._seek_preview(-20))
        self.btn_seek_fwd.clicked.connect(lambda: self._seek_preview(20))
        
        seek_layout.addStretch(1)
        seek_layout.addWidget(self.btn_seek_back)
        seek_layout.addWidget(self.btn_seek_fwd)
        seek_layout.addStretch(1)
        
        preview_layout.addWidget(self._preview_title)
        preview_layout.addWidget(self._preview_video, 1)
        preview_layout.addWidget(self._preview_status)
        
        self._preview_video.installEventFilter(self)
        QTimer.singleShot(100, self._position_seek_buttons)

        try:
            self._preview_video.show()
            self._preview_player = MPVSafetyManager.create_safe_mpv(
                wid=int(self._preview_video.winId()),
                osc=False,
                hr_seek='yes',
                hwdec='auto',
                keep_open='yes',
                ytdl=False,
                demuxer_max_bytes='500M',
                demuxer_max_back_bytes='100M',
                vo='gpu,direct3d,d3d11,null',
                input_vo_keyboard=False,
                input_default_bindings=False,
                aid='no',
            )
            if self._preview_player is not None:
                MPVSafetyManager.safe_mpv_set(self._preview_player, "volume", 0)
                MPVSafetyManager.safe_mpv_set(self._preview_player, "mute", True)

                @self._preview_player.property_observer('idle-active')
                def _on_idle_change(name, value):
                    if value is True: # Player became idle (video finished)
                        MPVSafetyManager.run_on_qt_thread(self._handle_eof_reset)
        except Exception as exc:
            self._preview_player = None
            self._preview_status.setText(f"Preview unavailable: {exc}")

        # Find the splitter to add the preview panel to
        splitter = self.findChild(QSplitter, "splitter")
        if splitter:
            splitter.addWidget(self._preview_panel)
            splitter.setStretchFactor(0, 0)
            splitter.setStretchFactor(1, 3)
            splitter.setStretchFactor(2, 2)
            
            # Try to restore saved splitter state first
            restored = False
            if self.config:
                splitter_state_b64 = self.config.config.get("file_dialog_splitter_state")
                if splitter_state_b64:
                    try:
                        restored = splitter.restoreState(QByteArray.fromBase64(splitter_state_b64.encode()))
                    except Exception:
                        pass
            
            # If not restored, apply default sizes (e.g. 500px wide preview)
            if not restored:
                w_total = self.width() if self.width() > 0 else 1400
                splitter.setSizes([250, w_total - 250 - 500, 500])
        else:
            if isinstance(layout, QGridLayout):
                layout.addWidget(self._preview_panel, 0, layout.columnCount(), layout.rowCount(), 1)
            else:
                layout.addWidget(self._preview_panel)

    def _restructure_bottom_layout(self):
        layout = self.layout()
        if not isinstance(layout, QGridLayout):
            return
            
        file_name_label = self.findChild(QLabel, "fileNameLabel")
        file_name_edit = self.findChild(QLineEdit, "fileNameEdit")
        file_type_label = self.findChild(QLabel, "fileTypeLabel")
        file_type_combo = self.findChild(QComboBox, "fileTypeCombo")
        button_box = self.findChild(QWidget, "buttonBox")
        
        # Find open and cancel buttons from QPushButton children to style and layout them
        buttons = self.findChildren(QPushButton)
        open_button = None
        cancel_button = None
        for b in buttons:
            raw = (b.text() or "")
            txt = raw.replace("&", "").replace("...", "").strip().lower()
            if txt in ("open", "ok", "choose"):
                open_button = b
                b.setObjectName("openButton")
                b.setFixedSize(110, 32)
            elif txt == "cancel":
                cancel_button = b
                b.setObjectName("cancelButton")
                b.setFixedSize(110, 32)
        
        if all([file_name_label, file_name_edit, file_type_label, file_type_combo, button_box]):
            # Remove from grid layout
            layout.removeWidget(file_name_label)
            layout.removeWidget(file_name_edit)
            layout.removeWidget(file_type_label)
            layout.removeWidget(file_type_combo)
            layout.removeWidget(button_box)
            
            # Replace default buttonBox layout with a tight custom QHBoxLayout to ensure
            # both Open and Cancel buttons are displayed side-by-side without extra spacers
            temp_widget = QWidget()
            if button_box.layout():
                temp_widget.setLayout(button_box.layout())
                
            btn_layout = QHBoxLayout(button_box)
            btn_layout.setContentsMargins(0, 0, 0, 0)
            btn_layout.setSpacing(8)
            btn_layout.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
            
            if open_button:
                btn_layout.addWidget(open_button)
            if cancel_button:
                btn_layout.addWidget(cancel_button)
            
            # Constrain buttonBox size tightly to match both 110px buttons and 8px spacing
            button_box.setFixedSize(228, 32)
            
            # Create horizontal layout for bottom inputs and button box container
            bottom_inputs_layout = QHBoxLayout()
            bottom_inputs_layout.setContentsMargins(0, 0, 0, 0)
            bottom_inputs_layout.setSpacing(8)
            
            # Set size policies
            file_name_edit.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
            file_type_combo.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
            
            # Constrain File Name edit box width
            file_name_edit.setMinimumWidth(100)
            file_name_edit.setMaximumWidth(350)
            
            # Add inputs and buttonBox to the horizontal layout in the requested order:
            # File name label -> File name edit -> buttonBox (Open + Cancel) -> File type label -> File type combo
            bottom_inputs_layout.addWidget(file_name_label)
            bottom_inputs_layout.addWidget(file_name_edit, 2)
            bottom_inputs_layout.addWidget(button_box)
            bottom_inputs_layout.addWidget(file_type_label)
            bottom_inputs_layout.addWidget(file_type_combo, 1)
            
            # Add bottom inputs layout to Row 2, Col 0, Span 1x3 (spanning all 3 columns)
            layout.addLayout(bottom_inputs_layout, 2, 0, 1, 3)
            
            # Set grid column stretch factors:
            # Col 0 (lookInLabel / left side) -> 0 (do not stretch)
            # Col 1 (lookInCombo and navigation buttons / middle) -> 1 (absorbs expansion)
            # Col 2 (preview panel / right side) -> 0 (do not stretch extra)
            layout.setColumnStretch(0, 0)
            layout.setColumnStretch(1, 1)
            layout.setColumnStretch(2, 0)
            
            # Distribute stretches so the splitter row (Row 1) expands vertically
            # and other rows remain compact.
            for r in range(layout.rowCount()):
                if r == 1:
                    layout.setRowStretch(r, 1)
                else:
                    layout.setRowStretch(r, 0)

    def _bind_preview_selection(self, view):
        if view is None:
            return
        if getattr(view, "_fvs_preview_bound", False):
            return
        setattr(view, "_fvs_preview_bound", True)
        try:
            view.clicked.connect(lambda _idx, v=view: self._schedule_preview(v))
        except Exception:
            pass
        try:
            sm = view.selectionModel()
            if sm is not None:
                sm.selectionChanged.connect(lambda _selected, _deselected, v=view: self._schedule_preview(v))
        except Exception:
            pass

    def _schedule_preview(self, view):
        self._preview_source_view = view
        self._preview_timer.start(400)

    def _preview_current_selection(self):
        view = self._preview_source_view
        if view is None:
            view = getattr(self, "tree_view", None) or getattr(self, "list_view", None)
        paths = self._selected_paths_from_view(view)
        if not paths:
            self._set_preview_idle("Select a video")
            return
        path = paths[0]
        if len(paths) > 1:
            self._set_preview_idle("Select one video")
            return
        if not self._is_video_path(path):
            self._set_preview_idle("Select a video")
            return
        self._preview_video_path(path)

    def _is_video_path(self, path):
        if not path or not os.path.isfile(path):
            return False
        ext = os.path.splitext(path)[1].lower()
        return ext in {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".flv", ".wmv",
            ".3gp", ".mpeg", ".mpg", ".asf", ".ogg", ".ogv", ".vob", ".qt",
            ".ts", ".mts", ".m2ts", ".yuv", ".rm", ".rmvb", ".amv", ".mp2",
            ".mpe", ".mpv", ".m2v", ".m4p"
        }

    def _seek_preview(self, seconds):
        if self._preview_player is not None and self._preview_path:
            try:
                curr = MPVSafetyManager.safe_mpv_get(self._preview_player, "time-pos") or 0.0
                target = max(0, curr + seconds)
                MPVSafetyManager.safe_mpv_set(self._preview_player, "time-pos", target)
            except Exception:
                pass

    def _handle_eof_reset(self):
        if self._preview_player is not None:
            self._set_preview_idle("Select a video")
        if hasattr(self, 'seek_controls_widget'):
            self.seek_controls_widget.hide()

    def _launch_default_player(self):
        if self._preview_path:
            self._play_video([self._preview_path])

    def _set_preview_idle(self, text):
        if self._preview_player is not None:
            try:
                MPVSafetyManager.safe_mpv_set(self._preview_player, "pause", True)
                MPVSafetyManager.safe_mpv_command(self._preview_player, "stop")
            except Exception:
                pass
        self._preview_path = None
        if self._preview_title is not None:
            self._preview_title.setText("Video Preview")
        if self._preview_status is not None:
            self._preview_status.setText(text)
        if hasattr(self, 'seek_controls_widget'):
            self.seek_controls_widget.hide()

    def _preview_video_path(self, path):
        if self._preview_player is None:
            if self._preview_status is not None:
                self._preview_status.setText("Preview unavailable")
            return
        if self._preview_path == path:
            try:
                MPVSafetyManager.safe_mpv_set(self._preview_player, "pause", False)
            except Exception:
                pass
            return
        self._preview_path = path
        if self._preview_title is not None:
            self._preview_title.setText(os.path.basename(path))
        if self._preview_status is not None:
            self._preview_status.setText("Click To Preview")
        
        if hasattr(self, 'seek_controls_widget'):
            self.seek_controls_widget.show()
            self.seek_controls_widget.raise_()
            self._position_seek_buttons()

        try:
            MPVSafetyManager.safe_mpv_set(self._preview_player, "pause", True)
            ok = MPVSafetyManager.safe_mpv_command(self._preview_player, "loadfile", path, "replace")
            MPVSafetyManager.safe_mpv_set(self._preview_player, "volume", 0)
            MPVSafetyManager.safe_mpv_set(self._preview_player, "mute", True)
            MPVSafetyManager.safe_mpv_set(self._preview_player, "pause", False)
            if not ok and self._preview_status is not None:
                self._preview_status.setText("Preview unavailable")
        except Exception as exc:
            if self._preview_status is not None:
                self._preview_status.setText(f"Preview unavailable: {exc}")

    def _stop_embedded_preview(self):
        try:
            self._preview_timer.stop()
        except Exception:
            pass
        if self._preview_player is not None:
            try:
                MPVSafetyManager.safe_mpv_set(self._preview_player, "pause", True)
                MPVSafetyManager.safe_mpv_command(self._preview_player, "stop")
                MPVSafetyManager.safe_mpv_shutdown(self._preview_player, timeout=1.0)
            except Exception:
                pass
        self._preview_player = None
        self._preview_path = None
        if hasattr(self, 'seek_controls_widget'):
            self.seek_controls_widget.hide()

    def _install_silent_delete(self, view):
        if view is None:
            return
        view.installEventFilter(self)
        view.viewport().installEventFilter(self)

    def _position_seek_buttons(self):
        if not hasattr(self, 'seek_controls_widget') or not self.seek_controls_widget:
            return

        v_rect = self._preview_video.geometry()

        # Calculate actual video height inside the frame (Assuming 16:9 aspect ratio)
        container_w = v_rect.width()
        container_h = v_rect.height()

        # mpv letterboxes content. We need to find the bottom of the visible video.
        target_ratio = 16.0 / 9.0
        actual_video_h = container_w / target_ratio

        if actual_video_h > container_h:
            # Video is taller than container (pillarboxed)
            actual_video_h = container_h
            y_offset = 0
        else:
            # Video is wider than container (letterboxed)
            y_offset = (container_h - actual_video_h) / 2

        # Container spans the width of the video area
        self.seek_controls_widget.setFixedWidth(container_w)
        self.seek_controls_widget.setFixedHeight(40) # Room for buttons
        
        # Anchor exactly 5px BELOW the active video content
        # We move the container to (video_x, video_y + y_offset + footage_h + 5)
        y = v_rect.y() + y_offset + actual_video_h + 5
        
        # Safety clamp
        y_max = v_rect.y() + container_h - 40 - 5
        y = min(y, y_max)
        
        self.seek_controls_widget.move(v_rect.x(), int(y))
        self.seek_controls_widget.raise_()

    def eventFilter(self, obj, event):
        if obj == getattr(self, "_preview_video", None) and event.type() == QEvent.Resize:
            self._position_seek_buttons()
            
        if event.type() == QEvent.ContextMenu:
            view = obj
            if not isinstance(obj, (QTreeView, QListView)):
                view = obj.parent()
            self._show_context_menu(view, event.globalPos())
            return True
        if event.type() in (QEvent.KeyPress, QEvent.ShortcutOverride):
            if hasattr(event, 'key'):
                if event.key() == Qt.Key_Delete:
                    if event.type() == QEvent.ShortcutOverride:
                        event.accept()
                        return True
                    self._delete_selected_files_silent()
                    return True
                elif event.key() == Qt.Key_F2:
                    if event.type() == QEvent.ShortcutOverride:
                        event.accept()
                        return True
                    view = obj if isinstance(obj, (QTreeView, QListView)) else None
                    if not view and getattr(self, "tree_view", None) is not None and self.tree_view.hasFocus():
                        view = self.tree_view
                    elif not view and getattr(self, "list_view", None) is not None and self.list_view.hasFocus():
                        view = self.list_view
                    if not view:
                        view = getattr(self, "tree_view", None) or getattr(self, "list_view", None)
                    paths = self._selected_paths_from_view(view)
                    if paths and len(paths) == 1:
                        self._rename_file(paths[0])
                    return True
        return super().eventFilter(obj, event)

    def _show_context_menu(self, view, global_pos):
        paths = self._selected_paths_from_view(view)
        menu = QMenu(view)
        act_cut = None
        act_copy = None
        act_paste = None
        act_play = None
        act_rename = None
        act_new_folder = None
        act_delete = None
        if paths:
            act_cut = menu.addAction("        ✂️        Cut         ✂️")
            act_copy = menu.addAction("      📄       Copy        📄")
            menu.addSeparator()
            act_play = menu.addAction("▶   Preview Play the Video    ▶")
            menu.addSeparator()
        clipboard = QApplication.clipboard()
        if clipboard.mimeData().hasUrls():
            act_paste = menu.addAction("      📋       Paste       📋")
        if len(paths) == 1:
            act_rename = menu.addAction("          Rename the File")
            menu.addSeparator()
        act_new_folder = menu.addAction("📂   Create a New Folder   📂")
        if paths:
            menu.addSeparator()
            act_delete = menu.addAction("        ⛔    Delete File   ⛔")
        chosen = menu.exec_(global_pos)
        if chosen == act_cut:
            self._cut_files(paths)
        elif chosen == act_copy:
            self._copy_files(paths)
        elif chosen == act_paste:
            self._paste_files()
        elif chosen == act_new_folder:
            self._create_new_folder()
        elif paths and chosen == act_play:
            self._play_video(paths)
        elif paths and chosen == act_delete:
            self._delete_selected_files_silent()
        elif paths and len(paths) == 1 and chosen == act_rename:
            self._rename_file(paths[0])

    def _cut_files(self, paths):
        if not paths:
            return
        self._copy_files(paths, is_cut=True)

    def _copy_files(self, paths, is_cut=False):
        if not paths:
            return
        if self._cut_file_paths:
            self._cut_file_paths.clear()
            self.tree_view.viewport().update()
        mime_data = QMimeData()
        urls = [QUrl.fromLocalFile(p) for p in paths]
        mime_data.setUrls(urls)
        if is_cut:
            mime_data.setData("application/x-qt-cut-files", QByteArray(b"1"))
            self._cut_file_paths.update(paths)
            self.tree_view.viewport().update()
        clipboard = QApplication.clipboard()
        clipboard.setMimeData(mime_data)

    def _paste_files(self):
        clipboard = QApplication.clipboard()
        mime_data = clipboard.mimeData()
        if not mime_data.hasUrls():
            return
        is_cut = mime_data.hasFormat("application/x-qt-cut-files")
        source_paths = [url.toLocalFile() for url in mime_data.urls()]
        dest_dir = self.directory().absolutePath()
        for src_path in source_paths:
            if not os.path.exists(src_path):
                continue
            base_name = os.path.basename(src_path)
            dest_path = os.path.join(dest_dir, base_name)
            if os.path.exists(dest_path) and src_path.lower() != dest_path.lower():
                action = self._handle_overwrite(base_name)
                if action == "skip":
                    continue
                elif action == "rename":
                    name, ext = os.path.splitext(base_name)
                    i = 1
                    while True:
                        new_name = f"{name} ({i}){ext}"
                        new_dest_path = os.path.join(dest_dir, new_name)
                        if not os.path.exists(new_dest_path):
                            dest_path = new_dest_path
                            break
                        i += 1
            try:
                if is_cut:
                    shutil.move(src_path, dest_path)
                else:
                    if os.path.isdir(src_path):
                        shutil.copytree(src_path, dest_path)
                    else:
                        shutil.copy(src_path, dest_path)
            except Exception as e:
                QMessageBox.warning(self, "Paste Error", f"Could not paste file:\n{src_path}\n\nError: {e}")
        if is_cut:
            self._cut_file_paths.clear()
        self.setDirectory(self.directory())
        self.tree_view.viewport().update()

    def _handle_overwrite(self, file_name):
        msg_box = QMessageBox(self)
        msg_box.setWindowTitle("Confirm Overwrite")
        msg_box.setText(f"The destination already has a file named '{file_name}'.")
        msg_box.setInformativeText("Do you want to replace it?")
        overwrite_btn = msg_box.addButton("Overwrite", QMessageBox.AcceptRole)
        skip_btn = msg_box.addButton("Skip", QMessageBox.RejectRole)
        rename_btn = msg_box.addButton("Rename", QMessageBox.ActionRole)
        msg_box.setDefaultButton(overwrite_btn)
        msg_box.exec_()
        if msg_box.clickedButton() == skip_btn:
            return "skip"
        elif msg_box.clickedButton() == rename_btn:
            return "rename"
        else:
            return "overwrite"

    def _create_new_folder(self):
        current_dir = self.directory().absolutePath()
        name, ok = QInputDialog.getText(self, "New Folder", "Folder Name:")
        if ok and name:
            new_path = os.path.join(current_dir, name)
            try:
                os.mkdir(new_path)
                self.setDirectory(current_dir)
            except Exception as e:
                QMessageBox.warning(self, "Error", f"Could not create folder: {e}")

    def _rename_file(self, old_path):
        dirname = os.path.dirname(old_path)
        basename = os.path.basename(old_path)
        dialog = QInputDialog(self)
        dialog.setWindowTitle("Rename File")
        dialog.setLabelText("New Name:")
        dialog.setTextValue(basename)
        dialog.resize(400, 150)
        if dialog.exec_() == QInputDialog.Accepted:
            name = dialog.textValue()
            if name and name != basename:
                new_path = os.path.join(dirname, name)
                try:
                    os.rename(old_path, new_path)
                    self.setDirectory(dirname)
                except Exception as e:
                    QMessageBox.warning(self, "Error", f"Could not rename file: {e}")

    def _play_video(self, paths):
        if not paths:
            return
        target = paths[0]
        try:
            if hasattr(os, 'startfile'):
                os.startfile(target)
            else:
                opener = 'open' if sys.platform == 'darwin' else 'xdg-open'
                subprocess.Popen([opener, target])
        except Exception:
            pass

    def _delete_selected_files_silent(self):
        view = None
        if getattr(self, "tree_view", None) is not None and self.tree_view.hasFocus():
            view = self.tree_view
        elif getattr(self, "list_view", None) is not None and self.list_view.hasFocus():
            view = self.list_view
        else:
            view = getattr(self, "tree_view", None) or getattr(self, "list_view", None)
        paths = self._selected_paths_from_view(view)
        if not paths:
            return

        def _send_to_bin_windows(path):
            import ctypes
            from ctypes import wintypes

            class SHFILEOPSTRUCTW(ctypes.Structure):
                _fields_ = [("hwnd", wintypes.HWND), ("wFunc", wintypes.UINT), ("pFrom", wintypes.LPCWSTR),
                            ("pTo", wintypes.LPCWSTR), ("fFlags", ctypes.c_uint), ("fAnyOperationsAborted", wintypes.BOOL),
                            ("hNameMappings", wintypes.LPVOID), ("lpszProgressTitle", wintypes.LPCWSTR)]
            FO_DELETE, FOF_ALLOWUNDO, FOF_NOCONFIRMATION = 3, 0x40, 0x10
            fileop = SHFILEOPSTRUCTW(hwnd=None, wFunc=FO_DELETE, pFrom=path + '\0', pTo=None,
                                     fFlags=FOF_ALLOWUNDO | FOF_NOCONFIRMATION, fAnyOperationsAborted=False,
                                     hNameMappings=None, lpszProgressTitle=None)
            return ctypes.windll.shell32.SHFileOperationW(ctypes.byref(fileop)) == 0
        failed = []
        for p in paths:
            try:
                if not os.path.exists(p): continue
                try:
                    from send2trash import send2trash
                    send2trash(p)
                except ImportError:
                    if not _send_to_bin_windows(os.path.abspath(p)):
                        raise OSError("Windows Shell delete failed.")
            except Exception as e:
                failed.append((p, str(e)))
        try:
            self.setDirectory(self.directory())
        except Exception:
            pass
        if failed:
            msg = "Some items could not be deleted:\n\n"
            msg += "\n".join([f"- {p}\n  {err}" for p, err in failed[:8]])
            if len(failed) > 8:
                msg += f"\n\n(and {len(failed) - 8} more...)"
            QMessageBox.warning(self, "Delete failed", msg)

    def _selected_paths_from_view(self, view):
        if view is None or not hasattr(view, "selectionModel"):
            return []
        sm = view.selectionModel()
        model = view.model()
        if sm is None or model is None:
            return []
        indexes = sm.selectedRows(0)
        if not indexes:
            indexes = sm.selectedIndexes()
        paths = []
        for idx in indexes:
            try:
                p = model.filePath(idx)
            except Exception:
                p = None
            if p:
                paths.append(p)
        seen = set()
        out = []
        for p in paths:
            if p not in seen:
                seen.add(p)
                out.append(p)
        return out

    def _setup_lookin_width(self):
        look_in_combobox = self.findChild(QComboBox, "lookInCombo")
        if look_in_combobox:
            look_in_combobox.setMaximumWidth(450)
            look_in_combobox.setSizePolicy(QSizePolicy.MinimumExpanding, QSizePolicy.Fixed)
            look_in_combobox.currentTextChanged.connect(self._adjust_lookin_width)
            self._adjust_lookin_width()
            
        layout = self.layout()
        if isinstance(layout, QGridLayout):
            for i in range(layout.count()):
                item = layout.itemAt(i)
                r, c, rs, cs = layout.getItemPosition(i)
                if r == 0 and c == 1 and item.layout():
                    h_layout = item.layout()
                    if isinstance(h_layout, QHBoxLayout):
                        has_stretch = False
                        for j in range(h_layout.count()):
                            if h_layout.itemAt(j).spacerItem():
                                has_stretch = True
                                break
                        if not has_stretch:
                            h_layout.addStretch(1)

    def _adjust_lookin_width(self):
        look_in_combobox = self.findChild(QComboBox, "lookInCombo")
        if look_in_combobox:
            text = look_in_combobox.currentText()
            fm = look_in_combobox.fontMetrics()
            text_width = fm.boundingRect(text).width()
            total_w = text_width + 55
            min_w = max(150, min(450, total_w))
            look_in_combobox.setMinimumWidth(min_w)

    def _setup_sidebar(self):
        sidebar = self.findChild(QListView, "sidebar")
        if not sidebar:
            return
        sidebar.setMinimumWidth(250)
        quick_access_paths = self.get_windows_quick_access()
        if not quick_access_paths:
            return
        urls = [QUrl.fromLocalFile(p) for p in quick_access_paths]
        if "C:/" not in quick_access_paths and "C:\\" not in quick_access_paths:
            urls.append(QUrl.fromLocalFile("C:/"))
        self.setSidebarUrls(urls)

    def _tune_buttons(self):
        QTimer.singleShot(0, self._apply_button_ids_and_restyle)

    def _apply_button_ids_and_restyle(self):
        buttons = self.findChildren(QPushButton)
        for b in buttons:
            raw = (b.text() or "")
            txt = raw.replace("&", "").replace("...", "").strip().lower()
            if txt in ("open", "ok", "choose"):
                b.setObjectName("openButton")
                b.setFixedSize(110, 32)
            elif txt == "cancel":
                b.setObjectName("cancelButton")
                b.setFixedSize(110, 32)
        self.update()

    def _save_sort_state(self, logicalIndex: int, order: Qt.SortOrder):
        if self.config:
            self.config.config["file_dialog_sort_column"] = logicalIndex
            self.config.config["file_dialog_sort_order"] = int(order)
            self.config.save_config(self.config.config)

    def get_windows_quick_access(self):
        try:
            ps_script = (
                "$s = New-Object -ComObject Shell.Application; "
                "$s.Namespace('shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}').Items() | "
                "ForEach-Object { $_.Path }"
            )
            cmd = ["powershell", "-NoProfile", "-NonInteractive", "-Command", ps_script]
            result = subprocess.run(cmd, capture_output=True, text=True, creationflags=0x08000000)
            paths = [line.strip() for line in result.stdout.split("\n") if line.strip()]
            return [p for p in paths if os.path.exists(p)]
        except Exception:
            return []

    def save_state(self):
        if not self.config:
            return
        self.config.config["file_dialog_geometry"] = (self.saveGeometry().toBase64().data().decode())
        
        splitter = self.findChild(QSplitter, "splitter")
        if splitter:
            self.config.config["file_dialog_splitter_state"] = (splitter.saveState().toBase64().data().decode())
            
        if self.tree_view:
            header = self.tree_view.header()
            self.config.config["file_dialog_header_state"] = (header.saveState().toBase64().data().decode())
            self.config.config["file_dialog_sort_column"] = header.sortIndicatorSection()
            self.config.config["file_dialog_sort_order"] = int(header.sortIndicatorOrder())
        self.config.save_config(self.config.config)

    def restore_state(self, header):
        if not self.config:
            self.set_default_state(header)
            return
        geom_b64 = self.config.config.get("file_dialog_geometry")
        if geom_b64:
            self.restoreGeometry(QByteArray.fromBase64(geom_b64.encode()))
        else:
            self.set_default_position()
        header_state_b64 = self.config.config.get("file_dialog_header_state")
        if header_state_b64:
            header.restoreState(QByteArray.fromBase64(header_state_b64.encode()))
        else:
            self.set_default_column_widths(header)
        sort_column = self.config.config.get("file_dialog_sort_column", -1)
        sort_order = self.config.config.get("file_dialog_sort_order", Qt.AscendingOrder)
        if sort_column != -1:
            try:
                order = Qt.SortOrder(sort_order)
            except Exception:
                order = Qt.AscendingOrder
            header.setSortIndicator(sort_column, order)

    def set_default_state(self, header):
        self.set_default_position()
        self.set_default_column_widths(header)

    def set_default_position(self):
        from PyQt5.QtWidgets import QDesktopWidget
        desktop = QDesktopWidget()
        screen_rect = desktop.screenGeometry()
        self.resize(1400, 800)
        self.move(screen_rect.center() - self.rect().center())

    def set_default_column_widths(self, header):
        from PyQt5.QtWidgets import QHeaderView
        header.setSectionResizeMode(QHeaderView.Interactive)
        header.setStretchLastSection(False)
        header.resizeSection(0, 330) # Name
        header.resizeSection(1, 95)  # Size
        header.resizeSection(3, 180) # Date Modified
        header.setSectionHidden(2, True)

    def selectedFiles(self):
        return super().selectedFiles()

    def done(self, result):
        self._stop_embedded_preview()
        self.save_state()
        self.hide()
        QApplication.processEvents()
        super().done(result)

    def closeEvent(self, event):
        self._stop_embedded_preview()
        super().closeEvent(event)
if __name__ == "__main__":
    app = QApplication(sys.argv)

    class MockConfig:
        def __init__(self):
            self.config = {}

        def save_config(self, cfg):
            pass
    dialog = CustomFileDialog(config=MockConfig())
    dialog.exec_()
