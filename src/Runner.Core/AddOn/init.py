"""Root Builder Profiles - an add-on for Kezyma's Root Builder (MO2 plugin). Author: izzydoingit.

It never changes Root Builder's code or settings. It runs after Root Builder's own Clear and:
  - moves new files from overwrite\\Root into "Root Changes - <profile>" (ticked in that profile only),
  - offers to move any preset in the game folder that no mod owns into a Root mod the user names.
The file work is in core.py.
"""

import time
import traceback
from pathlib import Path

import mobase

try:
    from PyQt6.QtCore import QCoreApplication, QTimer
    from PyQt6.QtWidgets import QInputDialog, QLineEdit, QMessageBox
except ImportError:
    from PyQt5.QtCore import QCoreApplication, QTimer
    from PyQt5.QtWidgets import QInputDialog, QLineEdit, QMessageBox

from .core import Core, rb_data_dir

NAME = "RootBuilderProfiles"
TITLE = "Root Builder Profiles"


class RootBuilderProfiles(mobase.IPluginTool):
    def __init__(self):
        super().__init__()
        self._org = None
        self._core = None
        self._retries = 0
        self._busy = False

    # ---- MO2 plugin identity ----------------------------------------------------------------
    def name(self):
        return NAME

    def displayName(self):
        return TITLE

    def author(self):
        return "izzydoingit"

    def description(self):
        return self.tr("Keeps Root Builder's game-folder changes per profile and keeps the game folder clean of presets no mod owns. Needs Root Builder by Kezyma.")

    def tooltip(self):
        return self.description()

    def version(self):
        return mobase.VersionInfo(1, 4, 0)

    def icon(self):
        try:
            from PyQt6.QtGui import QIcon
        except ImportError:
            from PyQt5.QtGui import QIcon
        return QIcon()

    def tr(self, text):
        return QCoreApplication.translate(NAME, text)

    def requirements(self):
        return [mobase.PluginRequirementFactory.pluginDependency(["RootBuilder"])]

    # On / off is MO2's own switch in its plugin settings (MO2 2.5 has no isActive and keeps "enabled" itself).
    DEFAULTS = {"movechanges": True, "checkgame": True}

    def settings(self):
        return [
            mobase.PluginSetting("movechanges", self.tr("After a program closes, move new files from overwrite\\Root into the profile's own 'Root Changes - <profile>' mod."), True),
            mobase.PluginSetting("checkgame", self.tr("On start, on profile change and after a program closes, offer to move preset files that no mod owns out of the game folder into a mod."), True),
        ]

    def _setting(self, key):
        value = self._org.pluginSetting(NAME, key)
        return self.DEFAULTS[key] if value is None else value

    def isActive(self):
        if not self._org:
            return False
        try:
            return bool(self._org.isPluginEnabled(NAME))
        except Exception:
            return True

    # ---- start --------------------------------------------------------------------------------
    def init(self, organizer):
        self._org = organizer
        organizer.onFinishedRun(lambda app, code: self._later(self._after_run))
        organizer.onProfileChanged(lambda old, new: self._later(self._check_game))
        organizer.onUserInterfaceInitialized(lambda window: self._later(self._startup))
        self._say(f"loaded (version 1.4.0), profile '{organizer.profileName()}'")
        return True

    def _say(self, line):
        try:
            folder = Path(self._org.pluginDataPath()) / NAME
            folder.mkdir(parents=True, exist_ok=True)
            with open(folder / "Root Builder Profiles log.txt", "a", encoding="utf-8") as f:
                f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {line}\n")
        except OSError:
            pass

    def _make_core(self):
        game = self._org.managedGame()
        game_path = game.gameDirectory().absolutePath()
        rb_dir = rb_data_dir(self._org.pluginDataPath(), game_path, game.gameVersion())
        exclusions = str(self._org.pluginSetting("RootBuilder", "exclusions") or "Saves,Data,Data Files").split(",")
        return Core(game_path, self._org.overwritePath(), self._org.modsPath(), rb_dir,
                    Path(self._org.pluginDataPath()) / NAME, exclusions, say=self._say)

    def _later(self, fn, ms=0):
        """Run after the current MO2 callbacks have all returned - so Root Builder's own Clear is finished first."""
        QTimer.singleShot(ms, lambda: self._guard(fn))

    def _guard(self, fn):
        if not self.isActive() or self._busy:
            self._say(f"{fn.__name__}: skipped ({'turned off in MO2' if not self.isActive() else 'already working'})")
            return
        self._say(f"{fn.__name__}: running")
        self._busy = True
        try:
            self._core = self._make_core()
            self._core.first_start()  # idempotent; must run before anything moves, whichever step comes first
            fn()
        except Exception:
            self._say("ERROR\n" + traceback.format_exc())
            QMessageBox.warning(None, TITLE, self.tr("Something went wrong - nothing more was changed. The log is in MO2's plugins\\data\\RootBuilderProfiles folder."))
        finally:
            self._busy = False

    def _startup(self):
        self._core.first_start()
        self._say(f"started on profile '{self._org.profileName()}'")
        self._check_game(startup=True)

    # ---- 1: after a program closes ---------------------------------------------------------
    def _after_run(self):
        if not self._setting("movechanges"):
            return
        if self._core.build_deployed():
            # Root Builder has not cleared yet (or autobuild is off) - look again a few times, then leave it.
            if self._retries < 10:
                self._retries += 1
                self._later(self._after_run, 3000)
            else:
                self._say("Root Builder still has files deployed after 30 s - nothing moved this time")
                self._retries = 0
            return
        self._retries = 0
        # 1.2.0: the ticked mods decide what is this profile's change (ENB leftovers never land in a non-ENB profile's mod)
        mod, moved, created = self._core.move_changes(self._org.profileName(), self._ticked_mods())
        if moved and created:
            self._org.refresh(True)
            self._org.onNextRefresh(lambda: self._tick(mod), True)
        self._check_game()

    def _tick(self, mod):
        try:
            self._org.modList().setActive(mod, True)
            self._say(f"ticked '{mod}' in profile '{self._org.profileName()}'")
        except Exception:
            self._say("could not tick " + mod + "\n" + traceback.format_exc())

    # ---- 2: the game folder ----------------------------------------------------------------
    def _check_game(self, startup=False):
        """Any ENB / Effects 11 / ReShade file in the game folder that no ticked mod deploys (nothing is deployed while
        this runs) is offered for moving into a mod the user names. Saying no to a set means it is not asked again
        until the set changes; the Tools menu entry asks again."""
        if not self._setting("checkgame"):
            return
        reason = self._core.busy_reason()
        if reason:
            # 1.1.1: a live deploy or a running game leaves ticked mods' Root files in the game folder - never judged then
            self._say(f"game folder check skipped: {reason}")
            return
        known, strays = self._core.scan_game(self._ticked_mods())
        files = {**known, **strays}
        if not files:
            return
        state = self._core.load_state() or {}
        # 1.3.0: copies of a mod's own Root files (a deploy Root Builder could not clear - a killed game held them) are never
        # offered as a preset: 2026-10-12 a stopped test's DXVK files were adopted that way and ticked in the playing profile
        copies = self._core.copies_of_mod_roots(files)
        if copies:
            ckey = ["copies"] + sorted(k.lower() for k in copies)
            if state.get("declined") != ckey:
                names = "\n".join(f"{rel}   (from '{copies[rel]}')" for rel in sorted(copies)[:10]) + ("\n..." if len(copies) > 10 else "")
                answer = QMessageBox.question(None, TITLE, self.tr(
                    f"Your game folder holds {len(copies)} file(s) that are exact copies of files in a mod's Root folder - "
                    "Root Builder could not clear them after a game was closed (it held them open):\n\n")
                    + names + self.tr("\n\nThey are not a preset. Send these game folder copies to the Recycle Bin? The mods keep "
                    "their own copies, and Root Builder deploys them again only where those mods are ticked."))
                if answer == QMessageBox.StandardButton.Yes:
                    n = self._core.remove_copies({rel: files[rel] for rel in copies},
                                                 reset_snapshot=any(rel in known for rel in copies))
                    QMessageBox.information(None, TITLE, self.tr(f"Done: {n} leftover file(s) removed from the game folder."))
                else:
                    state["declined"] = ckey
                    self._core.save_state(state)
                    self._say(f"{len(copies)} leftover copies of mods' Root files: he said no - not asked again until they change")
            for rel in copies:
                files.pop(rel, None)
                known.pop(rel, None)
            if not files:
                return
            state = self._core.load_state() or {}
        key = sorted(k.lower() for k in files)
        if state.get("declined") == key:
            self._say(f"{len(files)} unowned preset file(s) in the game folder - he said no to this set before, not asked")
            return
        names = "\n".join(sorted(files)[:10]) + ("\n..." if len(files) > 10 else "")
        answer = QMessageBox.question(None, TITLE, self.tr(
            f"Your game folder holds {len(files)} preset file(s) that no ticked mod puts there, so every profile uses them:\n\n")
            + names + self.tr("\n\nMove them into a mod of their own? You name the mod next. Profiles that tick it keep the "
            "preset; the others stop seeing it. The game folder copies go to the Recycle Bin only after every copy is checked."))
        if answer != QMessageBox.StandardButton.Yes:
            state["declined"] = key
            self._core.save_state(state)
            self._say(f"{len(files)} unowned preset file(s): he said no - not asked again until they change (Tools menu asks again)")
            return
        mod = self._ask_name()
        if not mod:
            self._say("no mod name given - nothing moved")
            return
        reset = any(k in known for k in files)
        created, copied = self._core.adopt(files, mod, reset_snapshot=reset)
        if created:
            self._org.refresh(True)
            self._org.onNextRefresh(lambda: self._tick(mod), True)
        QMessageBox.information(None, TITLE, self.tr(
            f"Done: {len(files)} file(s) are now in '{mod}' and gone from the game folder.")
            + (self.tr(" The new mod is ticked in this profile - tick it in any other profile that should use this preset.") if created else ""))

    def _ticked_mods(self):
        """The mods ticked in the current profile: MO2's own list, else the profile's modlist.txt."""
        try:
            ml = self._org.modList()
            active = int(mobase.ModState.ACTIVE)
            return [m for m in ml.allModsByProfilePriority() if int(ml.state(m)) & active]
        except Exception:
            self._say("ticked mods: MO2's list could not be read, using modlist.txt\n" + traceback.format_exc())
        try:
            text = (Path(self._org.profilePath()) / "modlist.txt").read_text(encoding="utf-8", errors="replace")
            return [line[1:].rstrip("\r\n") for line in text.splitlines() if line.startswith("+")]
        except Exception:
            self._say("ticked mods: modlist.txt could not be read either - no file counts as provided\n" + traceback.format_exc())
            return []

    def _ask_name(self):
        default = time.strftime("Preset from game folder %Y-%m-%d")
        while True:
            text, ok = QInputDialog.getText(None, TITLE, self.tr(
                "Name for the mod that will hold the preset (an existing mod's name adds the files to it):"),
                QLineEdit.EchoMode.Normal, default)
            if not ok:
                return None
            text = text.strip()
            if text and not any(c in text for c in '<>:"/\\|?*') and not text.endswith("."):
                return text
            QMessageBox.warning(None, TITLE, self.tr("That name cannot be a folder name. Try another."))

    # ---- the Tools menu entry ----------------------------------------------------------------
    def display(self):
        def run():
            state = self._core.load_state() or {}
            state.pop("declined", None)
            self._core.save_state(state)
            if self._core.busy_reason():
                QMessageBox.information(None, TITLE, self.tr("Root Builder has files in the game folder right now (or the game or a Skyrim Utility Patcher run is going) - let it end first."))
                return
            known, strays = self._core.scan_game(self._ticked_mods())
            if not known and not strays:
                QMessageBox.information(None, TITLE, self.tr("The game folder holds no preset files that a mod does not own."))
                return
            self._check_game()
        self._guard(run)


def createPlugin():
    return RootBuilderProfiles()
