"""Skyrim Utility Patcher's entry in Mod Organizer's Tools menu. Author: izzydoingit.

Opens the program's window on the profile Mod Organizer shows. The program writes the line below when it adds this file; to take the entry
out, delete this file from Mod Organizer's plugins folder.

It is also the companion: a run that keeps Mod Organizer open (an option) writes "ask <id>.txt" into this plugin's data folder. "ping" is answered at
once; "refresh" reads the mod and plugin lists back in, without saving them first, and is answered when that is over.
"""

import os
import subprocess

import mobase

try:
    from PyQt6.QtCore import QCoreApplication, QTimer
    from PyQt6.QtGui import QIcon
    from PyQt6.QtWidgets import QMessageBox
except ImportError:
    from PyQt5.QtCore import QCoreApplication, QTimer
    from PyQt5.QtGui import QIcon
    from PyQt5.QtWidgets import QMessageBox

PROGRAM = r"@PROGRAM@"
NAME = "Skyrim Utility Patcher"

# it must outlive the Mod Organizer a run closes, should that Mod Organizer sit in a job
BREAKAWAY = 0x01000000
LOOK_EVERY_MS = 500


class Entry(mobase.IPluginTool):
    def __init__(self):
        super().__init__()
        self._org = None
        self._timer = None
        self._refreshing = False

    def init(self, organizer):
        self._org = organizer
        self._timer = QTimer()
        self._timer.timeout.connect(self._look)
        self._timer.start(LOOK_EVERY_MS)
        return True

    def _folder(self):
        return os.path.join(self._org.pluginDataPath(), "SkyrimUtilityPatcher")

    def _answer(self, ask_id):
        done = os.path.join(self._folder(), f"done {ask_id}.txt")
        with open(done + ".part", "w", encoding="utf-8") as f:
            f.write("ok")
        os.replace(done + ".part", done)

    def _look(self):
        if self._refreshing:
            return
        try:
            names = [n for n in os.listdir(self._folder()) if n.startswith("ask ") and n.endswith(".txt")]
        except OSError:
            return
        for name in names:
            ask_id = name[len("ask "):-len(".txt")]
            path = os.path.join(self._folder(), name)
            try:
                with open(path, encoding="utf-8") as f:
                    what = f.read().strip()
                os.remove(path)
                if what == "refresh":
                    self._refreshing = True

                    def finished(a_id=ask_id):
                        self._refreshing = False
                        self._answer(a_id)

                    self._org.onNextRefresh(finished, False)
                    self._org.refresh(False)
                    return
                self._answer(ask_id)
            except OSError:
                self._refreshing = False

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
