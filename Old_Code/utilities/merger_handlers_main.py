from utilities.merger_handlers_list import MergerHandlersListMixin
from utilities.merger_handlers_dialogs import MergerHandlersDialogsMixin
from utilities.merger_handlers_preview import MergerHandlersPreviewMixin
from utilities.merger_handlers_buttons import MergerHandlersButtonsMixin
from utilities.merger_music_dialog import MusicDialogHandler

class MergerHandlers(MergerHandlersListMixin, MergerHandlersDialogsMixin, MergerHandlersPreviewMixin, MergerHandlersButtonsMixin):
    def __init__(self, parent):
        self.parent = parent
        self.logger = parent.logger
        self.music_dialog_handler = MusicDialogHandler(self.parent)
        MergerHandlersListMixin.__init__(self)
