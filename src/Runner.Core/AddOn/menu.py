"""Skyrim Utility Patcher's entry in Mod Organizer's Tools menu. Author: izzydoingit.

Opens the program's window on the profile Mod Organizer shows. The program writes the line below when it adds this file; to take the entry
out, delete this file from Mod Organizer's plugins folder.
"""

import os
import subprocess

import mobase

try:
    from PyQt6.QtCore import QCoreApplication
    from PyQt6.QtGui import QIcon
    from PyQt6.QtWidgets import QMessageBox
except ImportError:
    from PyQt5.QtCore import QCoreApplication
    from PyQt5.QtGui import QIcon
    from PyQt5.QtWidgets import QMessageBox

PROGRAM = r"@PROGRAM@"
NAME = "Skyrim Utility Patcher"

# it must outlive the Mod Organizer a run closes, should that Mod Organizer sit in a job
BREAKAWAY = 0x01000000


class Entry(mobase.IPluginTool):
    def __init__(self):
        super().__init__()
        self._org = None

    def init(self, organizer):
        self._org = organizer
        return True

    def name(self):
        return NAME

    def displayName(self):
        return NAME

    def author(self):
        return "izzydoingit"

    def description(self):
        return self.tr("Opens Skyrim Utility Patcher on this profile.")

    def tooltip(self):
        return self.description()

    def version(self):
        return mobase.VersionInfo(1, 0, 0)

    def settings(self):
        return []

    def icon(self):
        return QIcon(PROGRAM)

    def tr(self, text):
        return QCoreApplication.translate("SkyrimUtilityPatcher", text)

    def display(self):
        if not os.path.exists(PROGRAM):
            QMessageBox.warning(None, NAME, self.tr("The program was moved or removed. Add this entry again from its Settings."))
            return
        args = [PROGRAM, "--for-profile", self._org.profileName(), "--mo2", QCoreApplication.applicationFilePath(), "--base", self._org.basePath()]
        try:
            subprocess.Popen(args, cwd=os.path.dirname(PROGRAM), close_fds=True, creationflags=subprocess.CREATE_NO_WINDOW | BREAKAWAY)
        except OSError:
            # a job that allows no breakaway
            subprocess.Popen(args, cwd=os.path.dirname(PROGRAM), close_fds=True, creationflags=subprocess.CREATE_NO_WINDOW)


def createPlugin():
    return Entry()
