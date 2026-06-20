from utilities.merger_ui_build import MergerUIBuildMixin
from utilities.merger_ui_style import MergerUIStyleMixin
from utilities.merger_ui_widgets import MergerUIWidgetsMixin

class MergerUI(MergerUIBuildMixin, MergerUIStyleMixin, MergerUIWidgetsMixin):
    def __init__(self, parent):
        self.parent = parent