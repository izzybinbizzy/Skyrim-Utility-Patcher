"""Root Builder Profiles - the file work, kept apart from MO2 so it can be tested on its own.

Root Builder (Kezyma) deploys every ticked mod's Root folder into the game folder when a program
starts and clears it when the program closes. Two things it does are not per profile, and this
add-on covers them without touching Root Builder's code:

1. On Clear, a NEW file (shader cache, a preset edit to a file no mod owns, a log) goes into
   overwrite\\Root - one folder for every profile, deployed on every profile's next run. After the
   Clear, we move those files into a mod named "Root Changes - <profile>", ticked only in that
   profile, so each profile keeps its own.
2. Root Builder treats whatever sat in the game folder on its FIRST run as game files and keeps it
   there under every profile (a hand-placed ENB preset is the usual case - the "uninstall the old
   preset completely" problem preset authors warn about). We find preset files that Root Builder
   counts as game files, copy them into a Root mod, and send the game folder copies to the Recycle
   Bin once every copy is proven equal; Root Builder's game-file snapshot is set aside (renamed,
   never deleted) so it takes a fresh, clean one on its next build.
"""

import ctypes
import errno
import hashlib
import json
import os
import shutil
import subprocess
import time
from ctypes import wintypes
from pathlib import Path

# Files that belong to an ENB / Effects 11 / ReShade preset, never to the game itself.
PRESET_FILES = {
    "enbseries.ini", "enblocal.ini", "d3d11.dll", "d3dcompiler_46e.dll", "enbhost.exe",
    "dxgi.dll", "reshade.ini", "reshadepreset.ini",
}
PRESET_FOLDERS = ("enbseries\\", "enbcache\\", "reshade-shaders\\")

# Game-root files that no tool should ever move, whatever a pattern says.
NEVER_MOVE = {"skyrimse.exe", "skse64_loader.exe", "steam_api64.dll", "skyrimselauncher.exe"}


def is_preset_file(rel_lower):
    return rel_lower in PRESET_FILES or rel_lower.startswith(PRESET_FOLDERS)


# ENB's own program files (and the ini it writes for itself on its first start). One of these turning up in overwrite\Root
# for a profile that deploys no ENB is never that profile's change: 2026-10-08 22:32 an ENB TEST run's leftovers were swept in
# while the RELight profile ran and were filed into "Root Changes - RELight - Spell Addon", which then started ENB on RELight.
ENB_FILES = {"d3d11.dll", "dxgi.dll", "d3dcompiler_46e.dll", "enbhost.exe", "enblocal.ini"}
LEFTOVERS = "Root Leftovers"   # one mod, ticked nowhere; its files sit OUTSIDE Root\ so Root Builder never deploys them


def is_enb_file(rel_lower):
    return rel_lower in ENB_FILES or (rel_lower.startswith("enbseries\\") and rel_lower.endswith(".dllplugin"))


def path_safe(text):
    """Root Builder's CommonStrings.pathSafeString, so we find its data folder the same way."""
    for space in (" ", "."):
        text = text.replace(space, "_")
    for unsafe in ("<", ">", ":", "|", "*", "\\", "/", "?", "\""):
        text = text.replace(unsafe, "")
    return text


# A run of Skyrim Utility Patcher holds this lock: it may go on with nobody at the PC, and a question here would stop it.
SUP_RUN_LOCK = "Local\\Skyrim Utility Patcher run"


def sup_running():
    kernel32 = ctypes.WinDLL("kernel32")
    kernel32.OpenMutexW.restype = wintypes.HANDLE
    kernel32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    synchronize = 0x00100000
    handle = kernel32.OpenMutexW(synchronize, False, SUP_RUN_LOCK)
    if handle:
        kernel32.CloseHandle(handle)
        return True
    return False


def rb_data_dir(plugin_data_path, game_path, game_version):
    return Path(plugin_data_path) / "RootBuilder" / path_safe(game_path) / path_safe(game_version or "unknown")


def sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def move_file(src, dest):
    """Into place, whether or not the two folders share a drive: the source goes only once the copy is proven equal."""
    size = src.stat().st_size
    try:
        os.replace(src, dest)
    except OSError as e:
        if e.errno != errno.EXDEV and getattr(e, "winerror", None) != 17:
            raise
        part = dest.with_name(dest.name + ".part")
        shutil.copy2(src, part)
        if sha1(part) != sha1(src):
            part.unlink(missing_ok=True)
            raise OSError(f"the copy of {src.name} is not equal to it")
        os.replace(part, dest)
        src.unlink()
    if not dest.exists() or dest.stat().st_size != size:
        raise OSError(f"move failed for {dest.name}")


def files_under(root):
    """Every file under root as {relative path with backslashes: absolute Path}."""
    root = Path(root)
    out = {}
    if not root.is_dir():
        return out
    for dirpath, _dirs, names in os.walk(root):
        for name in names:
            full = Path(dirpath) / name
            out[str(full.relative_to(root))] = full
    return out


def remove_empty_dirs(root, keep_root=True):
    root = Path(root)
    if not root.is_dir():
        return
    for dirpath, _dirs, _names in sorted(os.walk(root), key=lambda w: -len(w[0])):
        p = Path(dirpath)
        if keep_root and p == root:
            continue
        try:
            p.rmdir()  # only succeeds when empty
        except OSError:
            pass


# While any of these runs, the game folder holds a live deploy (Root Builder's Clear cannot remove a DLL the game
# still has loaded - 2026-10-06 20:05 it cleared with SkyrimSE.exe alive and d3d11.dll, d3dcompiler_46e.dll and
# KiLoaderSatelliteENB.dllplugin stayed behind, which the check then called strays).
GAME_IMAGES = ("SkyrimSE.exe", "skse64_loader.exe")


def game_running(images=GAME_IMAGES):
    try:
        done = subprocess.run(["tasklist", "/NH", "/FO", "CSV"], capture_output=True, text=True,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), timeout=20)
        if done.returncode != 0:
            return True
        out = done.stdout.lower()
        return any(f'"{i.lower()}"' in out for i in images)
    except Exception:
        return True  # cannot tell - treat as running, so nothing is moved or reported


class _SHFILEOPSTRUCTW(ctypes.Structure):
    _fields_ = [("hwnd", wintypes.HWND), ("wFunc", wintypes.UINT), ("pFrom", ctypes.c_void_p),
                ("pTo", ctypes.c_void_p), ("fFlags", ctypes.c_ushort), ("fAnyOperationsAborted", wintypes.BOOL),
                ("hNameMappings", ctypes.c_void_p), ("lpszProgressTitle", wintypes.LPCWSTR)]


def recycle(paths):
    """Send files to the Recycle Bin in one operation. Returns True when the shell reports success."""
    paths = [str(Path(p).resolve()) for p in paths]
    if not paths:
        return True
    buf = ctypes.create_unicode_buffer("\0".join(paths) + "\0")
    op = _SHFILEOPSTRUCTW()
    op.wFunc = 3  # FO_DELETE
    op.pFrom = ctypes.cast(buf, ctypes.c_void_p)
    op.fFlags = 0x40 | 0x10 | 0x4 | 0x400  # ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
    result = ctypes.windll.shell32.SHFileOperationW(ctypes.byref(op))
    return result == 0 and not op.fAnyOperationsAborted


class Refused(RuntimeError):
    """Nothing was changed, and the message says why in the player's terms."""


class Core:
    """Everything the add-on does to files. `say` writes one log line."""

    def __init__(self, game_path, overwrite_path, mods_path, rb_dir, state_dir, exclusions, say=print, running=None):
        self.running = running or game_running
        self.game = Path(game_path)
        self.overwrite_root = Path(overwrite_path) / "Root"
        self.mods = Path(mods_path)
        self.rb_dir = Path(rb_dir)
        self.state_dir = Path(state_dir)
        self.state_file = self.state_dir / "state.json"
        self.exclusions = [e.strip().lower().rstrip("\\/") for e in exclusions if e.strip()]
        self.say = say
        self.last_leftovers = 0

    # ---- state ------------------------------------------------------------------------------
    def load_state(self):
        try:
            return json.loads(self.state_file.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None

    def save_state(self, state):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.state_file.with_suffix(".tmp")
        tmp.write_text(json.dumps(state, indent=1), encoding="utf-8")
        os.replace(tmp, self.state_file)

    def reset_snapshot(self):
        """Root Builder's snapshot of the game folder set aside, so its next build records the clean folder (otherwise it would restore
        what just left, for every profile). Noted first: a file held now is finished at the next look."""
        state = self.load_state() or {}
        state["reset snapshot"] = True
        self.save_state(state)
        self.finish_reset()

    def finish_reset(self):
        state = self.load_state() or {}
        if not state.get("reset snapshot"):
            return
        stamp = time.strftime("%Y-%m-%d %H-%M-%S")
        try:
            for name in ("GameData.json", "Backup"):
                src = self.rb_dir / name
                if src.exists():
                    dest = self.rb_dir / f"{Path(name).stem} before Root Builder Profiles {stamp}{Path(name).suffix}"
                    os.replace(src, dest)
                    self.say(f"Root Builder's {name} set aside as '{dest.name}' (it makes a fresh one on its next build)")
        except OSError as e:
            self.say(f"Root Builder's snapshot could not be set aside yet ({e}) - tried again at the next look")
            return
        state.pop("reset snapshot")
        self.save_state(state)

    def tick_owed(self):
        """[profile, mod] when a new Root Changes mod still has to be ticked, else None. It stays until the tick is done, so a move
        that failed half way, or a profile switch before the tick, does not leave the mod's files unused."""
        return (self.load_state() or {}).get("tick")

    def tick_done(self):
        state = self.load_state() or {}
        if state.pop("tick", None) is not None:
            self.save_state(state)

    def first_start(self):
        """First time: what is in overwrite\\Root now stays where it is (we never take files the user put there)."""
        if self.load_state() is not None:
            return False
        legacy = {rel.lower(): [p.stat().st_size, p.stat().st_mtime] for rel, p in files_under(self.overwrite_root).items()}
        self.save_state({"legacy": legacy, "created": time.strftime("%Y-%m-%d %H:%M:%S")})
        self.say(f"first start: {len(legacy)} files already in overwrite\\Root are left where they are")
        return True

    # ---- Root Builder's own files -----------------------------------------------------------
    def build_deployed(self):
        return (self.rb_dir / "BuildData.json").exists()

    def busy_reason(self):
        """Why the game folder must not be judged right now, or None. A live deploy (Root Builder's build data) or a
        running game means the preset files in it may be a ticked mod's, still deployed."""
        if self.build_deployed():
            return "Root Builder has a build deployed"
        if self.running():
            return "the game is running"
        if sup_running():
            return "a Skyrim Utility Patcher run is going"
        return None

    def root_sources(self, ticked_mods):
        """{lower relative path: [files]} of everything Root Builder deploys for this profile: each ticked mod's Root
        folder, and overwrite\\Root (deployed on every profile)."""
        out = {}
        for root in [self.mods / m / "Root" for m in ticked_mods] + [self.overwrite_root]:
            for rel, p in files_under(root).items():
                out.setdefault(rel.lower(), []).append(p)
        return out

    def rb_game_files(self):
        """Root Builder's snapshot of the game folder (lower-case relative paths), or None when it has none."""
        try:
            return set(json.loads((self.rb_dir / "GameData.json").read_text(encoding="utf-8-sig")).keys())
        except (OSError, ValueError):
            return None

    # ---- 1: after a run, overwrite\Root -> the profile's own mod -----------------------------
    def leftover_reason(self, rel, path, ticked_mods, own_mod):
        """Why a new overwrite\\Root file is NOT this profile's change, or None (1.2.0, 2026-10-08). `ticked_mods` None =
        unknown: only the copy rule applies."""
        low = rel.lower()
        for mod_dir in self.mods.iterdir() if self.mods.is_dir() else ():
            if mod_dir.name in (own_mod, LEFTOVERS):
                continue
            twin = mod_dir / "Root" / rel
            if twin.is_file() and twin.stat().st_size == path.stat().st_size and sha1(twin) == sha1(path):
                return f"a copy of '{mod_dir.name}'s Root file (a deploy Root Builder could not clear)"
        if ticked_mods is None:
            return None
        roots = [self.mods / m / "Root" for m in ticked_mods if not m.startswith("Root Changes - ") and (self.mods / m / "Root").is_dir()]
        if not roots:
            return "this profile deploys nothing through Root Builder"
        if is_enb_file(low) and not any((r / f).is_file() for r in roots for f in ("d3d11.dll", "dxgi.dll")):
            return "an ENB file, and this profile runs no ENB"
        return None

    def move_changes(self, profile, ticked_mods=None):
        """Returns (mod name, moved count, created). Call only after Root Builder's Clear has finished. Files that are not
        this profile's change (leftover_reason) go into the LEFTOVERS mod instead; their count is in self.last_leftovers."""
        self.last_leftovers = 0
        state = self.load_state() or {"legacy": {}}
        legacy = state.get("legacy", {})
        found = files_under(self.overwrite_root)
        todo = {}
        for rel, p in found.items():
            st = p.stat()
            old = legacy.get(rel.lower())
            if old is not None and old[0] == st.st_size and abs(old[1] - st.st_mtime) < 0.01:
                continue
            todo[rel] = p
        if not todo:
            return None, 0, False
        mod = f"Root Changes - {profile}"
        stray = {rel: why for rel, p in todo.items() if (why := self.leftover_reason(rel, p, ticked_mods, mod))}
        if stray:
            self._set_aside({rel: todo.pop(rel) for rel in stray}, profile, stray)
            for rel in stray:
                legacy.pop(rel.lower(), None)
            state["legacy"] = legacy
            self.save_state(state)
            remove_empty_dirs(self.overwrite_root)
        if not todo:
            return None, 0, False
        mod_dir = self.mods / mod
        created = not mod_dir.exists()
        if created:
            mod_dir.mkdir(parents=True)
            (mod_dir / "meta.ini").write_text(
                "[General]\nmodid=0\nversion=\ninstallationFile=\nnotes=\"Files the game or a preset menu wrote into the game "
                f"folder while profile '{profile}' ran. Made by Root Builder Profiles; deployed by Root Builder for this profile only.\"\n",
                encoding="utf-8")
            state["tick"] = [profile, mod]
            self.save_state(state)
        moved = 0
        for rel, src in todo.items():
            dest = mod_dir / "Root" / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            move_file(src, dest)
            legacy.pop(rel.lower(), None)
            moved += 1
        state["legacy"] = legacy
        self.save_state(state)
        remove_empty_dirs(self.overwrite_root)
        self.say(f"moved {moved} new file(s) from overwrite\\Root into '{mod}'" + (" (new mod)" if created else ""))
        for rel in list(todo)[:8]:
            self.say(f"  {rel}")
        return mod, moved, created

    def _set_aside(self, files, profile, reasons):
        """Move files out of overwrite\\Root into mods\\<LEFTOVERS>\\<date time> <profile>\\ - outside any Root folder, so Root
        Builder never deploys them, and the mod is ticked nowhere. Nothing is deleted."""
        box = self.mods / LEFTOVERS
        if not box.exists():
            box.mkdir(parents=True)
            (box / "meta.ini").write_text(
                "[General]\nmodid=0\nversion=\ninstallationFile=\nnotes=\"Files Root Builder swept out of the game folder that are not "
                "the profile's own change (a copy of another mod's Root file, an ENB file under a profile without ENB, or any file "
                "of a profile with no Root setup). Kept here by Root Builder Profiles, ticked nowhere, never deployed.\"\n",
                encoding="utf-8")
        here = box / f"{time.strftime('%Y-%m-%d %H-%M-%S')} {profile}"
        for rel, src in files.items():
            dest = here / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            move_file(src, dest)
        self.last_leftovers = len(files)
        self.say(f"set aside {len(files)} file(s) that are not '{profile}''s change into '{LEFTOVERS}\\{here.name}' (ticked nowhere):")
        for rel in list(files)[:8]:
            self.say(f"  {rel} - {reasons[rel]}")

    # ---- 2: the game folder ------------------------------------------------------------------
    def game_files(self):
        out = {}
        for dirpath, dirs, names in os.walk(self.game):
            here = Path(dirpath)
            at_top = here == self.game
            if at_top:
                dirs[:] = [d for d in dirs if d.lower() not in self.exclusions]
            for name in names:
                full = here / name
                rel = str(full.relative_to(self.game))
                if rel.lower() not in self.exclusions and not (at_top and name.lower() in self.exclusions):
                    out[rel] = full
        return out

    def scan_game(self, ticked_mods=()):
        """Preset files in the game folder while nothing is deployed.
        Returns (adoptable, strays): adoptable = Root Builder counts them as game files (they never leave);
        strays = Root Builder does not know them (a build that never got cleared, or files put there by hand since).
        Nothing is returned while busy_reason() names a reason, and a file that a ticked mod's Root folder (or
        overwrite\\Root) provides - the same relative path, or a file with the same sha1 - is never a stray."""
        reason = self.busy_reason()
        if reason:
            self.say(f"game folder check skipped: {reason}")
            return {}, {}
        known = self.rb_game_files()
        provided = self.root_sources(ticked_mods)
        by_size = None
        adoptable, strays, owned = {}, {}, []
        for rel, p in self.game_files().items():
            low = rel.lower()
            if low in NEVER_MOVE or not is_preset_file(low):
                continue
            if known is not None and low in known:
                adoptable[rel] = p
                continue
            if low in provided:
                owned.append(rel)
                continue
            if by_size is None:
                by_size = {}
                for paths in provided.values():
                    for q in paths:
                        try:
                            by_size.setdefault(q.stat().st_size, []).append(q)
                        except OSError:
                            pass
            size = p.stat().st_size
            digest = sha1(p) if by_size.get(size) else None
            if digest and any(sha1(q) == digest for q in by_size[size]):
                owned.append(rel)
                continue
            strays[rel] = p
        if owned:
            self.say(f"{len(owned)} preset file(s) in the game folder belong to a ticked mod's Root (left alone): "
                     + ", ".join(sorted(owned)[:5]))
        return adoptable, strays

    def copies_of_mod_roots(self, files):
        """{rel: mod name} for the game-folder files that are byte copies of a file at the same place in ANY mod's Root
        folder, ticked or not (1.3.0). Such a file is a deploy Root Builder could not clear - a game that was killed held it
        open - never a preset placed by hand. 2026-10-12 02:16: a stopped test left DXVK's d3d11.dll + dxgi.dll (an unticked
        test mod's Root files) in the game folder; this add-on offered them as a preset, they were moved into a new mod and
        ticked in the playing profile, and every start of it then loaded DXVK (the frame generation of his shader mod could
        not start and the game closed)."""
        out = {}
        mod_dirs = [d for d in self.mods.iterdir() if d.is_dir() and d.name != LEFTOVERS] if self.mods.is_dir() else []
        for rel, p in files.items():
            try:
                size = p.stat().st_size
            except OSError:
                continue
            digest = None
            for mod_dir in mod_dirs:
                twin = mod_dir / "Root" / rel
                try:
                    if not twin.is_file() or twin.stat().st_size != size:
                        continue
                except OSError:
                    continue
                digest = digest or sha1(p)
                if sha1(twin) == digest:
                    out[rel] = mod_dir.name
                    break
        return out

    def remove_copies(self, files, reset_snapshot=False):
        """Send game-folder files that copies_of_mod_roots() named to the Recycle Bin - each one checked again first (still a
        byte copy of a mod's Root file, nothing deployed, the game closed). The mods keep their own copies. Returns the count."""
        if self.build_deployed():
            raise RuntimeError("Root Builder has files deployed right now - close the game or program first")
        if self.running():
            raise RuntimeError("the game is running")
        again = self.copies_of_mod_roots(files)
        if set(again) != set(files):
            raise RuntimeError("a file changed since it was checked - nothing was removed")
        if not recycle(files.values()):
            raise RuntimeError("the Recycle Bin refused the game folder files - check the game folder")
        left = [rel for rel, p in files.items() if p.exists()]
        if left:
            raise RuntimeError(f"{len(left)} file(s) are still in the game folder (first: {left[0]})")
        for top in {Path(rel).parts[0] for rel in files if len(Path(rel).parts) > 1}:
            remove_empty_dirs(self.game / top, keep_root=False)
        if reset_snapshot:
            self.reset_snapshot()
        self.say(f"leftovers: {len(files)} copy/copies of mods' Root files recycled from the game folder: "
                 + ", ".join(sorted(files)[:6]))
        return len(files)

    def adopt(self, files, mod, reset_snapshot=True):
        """Copy `files` (rel -> game path) into mods\\<mod>\\Root, prove every copy equal, recycle the game copies,
        and - when some of them were in Root Builder's game snapshot - set that snapshot + backup aside so its next build
        records the clean folder (otherwise Root Builder would restore them from its backup)."""
        if self.build_deployed():
            raise RuntimeError("Root Builder has files deployed right now - close the game or program first")
        if self.running():
            raise RuntimeError("the game is running")
        copies = self.copies_of_mod_roots(files)   # 1.3.0: a mod's own Root file left behind is never adopted as a preset
        if copies:
            first = sorted(copies)[0]
            raise RuntimeError(f"{len(copies)} file(s) are copies of mods' Root files, not a preset (first: {first} from "
                               f"'{copies[first]}') - nothing was moved")
        mod_dir = self.mods / mod
        clash = sorted(rel for rel, src in files.items() if (mod_dir / "Root" / rel).exists() and sha1(mod_dir / "Root" / rel) != sha1(src))
        if clash:
            raise Refused(f"'{mod}' already holds a different {clash[0]}" + (f" and {len(clash) - 1} more" if len(clash) > 1 else "") +
                          ". Nothing was moved: name another mod, or move those files out of it first.")
        created = not mod_dir.exists()
        if created:
            mod_dir.mkdir(parents=True)
            (mod_dir / "meta.ini").write_text(
                "[General]\nmodid=0\nversion=\ninstallationFile=\nnotes=\"Preset files moved out of the game folder by "
                "Root Builder Profiles. Root Builder deploys them for the profiles that tick this mod.\"\n", encoding="utf-8")
        made = []
        try:
            for rel, src in files.items():
                dest = mod_dir / "Root" / rel
                if dest.exists() and sha1(dest) == sha1(src):
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                made.append(dest)
                shutil.copy2(src, dest)
            bad = [rel for rel, src in files.items() if sha1(mod_dir / "Root" / rel) != sha1(src)]
            if bad:
                raise RuntimeError(f"{len(bad)} copies are not equal to the game folder's (first: {bad[0]}) - nothing was removed")
            copied = len(made)
            self.say(f"adopt: {len(files)} file(s) in '{mod}' proven equal ({copied} copied now)")
            if not recycle(files.values()):
                raise RuntimeError("the Recycle Bin refused the game folder files - check the game folder")
        except Exception:
            # every game folder file still there: nothing half done stays, so the next look does not take these copies for a mod's leftovers
            if all(p.exists() for p in files.values()):
                for dest in made:
                    dest.unlink(missing_ok=True)
                remove_empty_dirs(mod_dir, keep_root=True)
                if created:
                    (mod_dir / "meta.ini").unlink(missing_ok=True)
                    remove_empty_dirs(mod_dir, keep_root=False)
            raise
        left = [rel for rel, p in files.items() if p.exists()]
        if left:
            raise RuntimeError(f"{len(left)} file(s) are still in the game folder (first: {left[0]})")
        for top in {Path(rel).parts[0] for rel in files if len(Path(rel).parts) > 1}:
            remove_empty_dirs(self.game / top, keep_root=False)
        if reset_snapshot:
            self.reset_snapshot()
        self.say(f"adopt: {len(files)} file(s) recycled from the game folder")
        return created, copied
