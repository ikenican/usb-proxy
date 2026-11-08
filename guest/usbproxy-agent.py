#!/usr/bin/env python3
"""Privileged guest helper. Only USB-backed volumes may be mounted or written."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
import time

MOUNT = Path('/mnt/usbproxy')
INCOMING = Path('/home/usbproxy/incoming')
AGENT = Path('/usr/local/sbin/usbproxy-agent')
UNSAFE = '<>:"\\|?*'


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def devices():
    tree = json.loads(run('lsblk', '--json', '--tree', '--bytes', '--output',
                          'PATH,TYPE,TRAN,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINTS,MODEL,SERIAL'))
    result = []

    def visit(node, usb=False):
        usb = usb or node.get('tran') == 'usb'
        if usb and node.get('fstype') in ('vfat', 'exfat', 'ntfs', 'ext4'):
            result.append({k: node.get(k) for k in
                           ('path', 'size', 'fstype', 'label', 'uuid', 'mountpoints')})
        for child in node.get('children', []):
            visit(child, usb)
    for node in tree['blockdevices']:
        visit(node)
    return result


def mounted():
    p = subprocess.run(['findmnt', '--json', '--mountpoint', str(MOUNT)],
                       capture_output=True, text=True)
    return json.loads(p.stdout)['filesystems'][0] if p.returncode == 0 else None


def mount(device):
    candidates = {d['path']: d for d in devices()}
    if device not in candidates:
        raise ValueError('Only a supported USB volume may be selected')
    current = mounted()
    if current:
        if os.path.realpath(current['source']) == os.path.realpath(device):
            return candidates[device]
        # Switching partitions: flush and release the previous one first.
        os.sync()
        subprocess.run(['umount', str(MOUNT)], check=True, capture_output=True, text=True)
    if any(candidates[device].get('mountpoints') or []):
        raise ValueError('Volume is already mounted elsewhere')
    MOUNT.mkdir(parents=True, exist_ok=True)
    kind = candidates[device]['fstype']
    driver = 'ntfs-3g' if kind == 'ntfs' else kind
    subprocess.run(['mount', '-t', driver, '-o', 'rw,nosuid,nodev,noexec',
                    device, str(MOUNT)], check=True, capture_output=True, text=True)
    return candidates[device]


def digest(path, on_bytes=None):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
            if on_bytes:
                on_bytes(len(chunk))
    return h.hexdigest()


class Progress:
    """Copy progress the host polls; written atomically and at most four times a second."""

    def __init__(self, path, total, count):
        self.path, self.total, self.count = path, total, count
        self.done, self.stage, self.file, self.index, self.last = 0, '', '', 0, 0.0

    def begin(self, stage, file, index):
        self.stage, self.file, self.index = stage, file, index
        self.write(force=True)

    def add(self, amount):
        self.done += amount
        self.write()

    def write(self, force=False):
        now = time.monotonic()
        if not force and now - self.last < 0.25:
            return
        self.last = now
        temporary = self.path.with_name(self.path.name + '.tmp')
        temporary.write_text(json.dumps({'stage': self.stage, 'file': self.file, 'index': self.index,
                                         'count': self.count, 'done': self.done, 'total': self.total},
                                        ensure_ascii=False), encoding='utf-8')
        os.replace(temporary, self.path)


def check_name(name):
    if not name or name in ('.', '..') or '/' in name or '\0' in name:
        raise ValueError('Invalid name')
    if any(c in name for c in UNSAFE) or name.endswith((' ', '.')):
        raise ValueError('Filename cannot be safely represented on this volume')


def resolve(relative):
    """Map a volume-relative path to the mount point without following symlinks."""
    rel = PurePosixPath(relative or '.')
    if rel.is_absolute() or '..' in rel.parts:
        raise ValueError('Invalid path')
    parts = [p for p in rel.parts if p != '.']
    current = MOUNT
    for part in parts:
        current = current / part
        if current.is_symlink():
            raise ValueError('Symbolic links are not followed')
    return current, '/'.join(parts)


def listing(request):
    volume = mount(request['device'])
    folder, rel = resolve(request.get('path', ''))
    if not folder.is_dir():
        raise ValueError('Folder not found: ' + rel)
    entries = []
    with os.scandir(folder) as items:
        for item in items:
            if item.name.startswith('.usbproxy-'):
                continue
            try:
                info = item.stat(follow_symlinks=False)
            except OSError:
                continue
            link = item.is_symlink()
            entries.append({'name': item.name, 'dir': not link and item.is_dir(follow_symlinks=False),
                            'link': link, 'size': info.st_size, 'mtime': int(info.st_mtime)})
    usage = shutil.disk_usage(MOUNT)
    return {'path': rel, 'entries': entries, 'free': usage.free, 'total': usage.total,
            'fstype': volume['fstype']}


def delete(request):
    mount(request['device'])
    removed = []
    for relative in request['paths']:
        target, rel = resolve(relative)
        if target == MOUNT:
            raise ValueError('The volume root cannot be deleted')
        if not (target.exists() or target.is_symlink()):
            raise ValueError('Not found: ' + rel)
        if target.is_dir() and not target.is_symlink():
            shutil.rmtree(target)
        else:
            target.unlink()
        removed.append(rel)
    os.sync()
    return {'deleted': removed}


def make_folder(request):
    mount(request['device'])
    parent, rel = resolve(request.get('path', ''))
    name = request['name']
    check_name(name)
    if not parent.is_dir():
        raise ValueError('Folder not found: ' + rel)
    target = parent / name
    if target.exists() or target.is_symlink():
        raise ValueError('Already exists: ' + name)
    target.mkdir()
    os.sync()
    return {'created': '/'.join(p for p in (rel, name) if p)}


def rename(request):
    mount(request['device'])
    source, rel = resolve(request['path'])
    if source == MOUNT or not (source.exists() or source.is_symlink()):
        raise ValueError('Not found: ' + rel)
    name = request['name']
    check_name(name)
    target = source.parent / name
    # A case-only rename is allowed on case-insensitive volumes.
    if (target.exists() or target.is_symlink()) and target.name.casefold() != source.name.casefold():
        raise ValueError('Already exists: ' + name)
    os.rename(source, target)
    os.sync()
    return {'renamed': name}


def self_update(request):
    source = Path(request['source'])
    if source.parent != INCOMING or source.is_symlink() or not source.is_file():
        raise ValueError('Invalid agent source')
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != request['sha256']:
        raise ValueError('Agent checksum mismatch')
    compile(data, str(AGENT), 'exec')
    temporary = AGENT.with_name('.usbproxy-agent.new')
    temporary.write_bytes(data)
    os.chmod(temporary, 0o755)
    os.replace(temporary, AGENT)
    return {'updated': True}


def copy(request):
    volume = mount(request['device'])
    spool = Path(request['spool']).resolve()
    if spool.parent != INCOMING or spool.is_symlink():
        raise ValueError('Invalid staging directory')
    base, _ = resolve(request.get('dest', ''))
    if not base.is_dir():
        raise ValueError('Destination folder not found')
    # Overwrite replaces existing regular files only (writing back an edited file).
    overwrite = bool(request.get('overwrite'))
    manifest = json.loads((spool / 'manifest.json').read_text(encoding='utf-8-sig'))
    files = manifest['files']
    if not files:
        raise ValueError('No files to copy')
    required = sum(int(f['size']) for f in files)
    if shutil.disk_usage(MOUNT).free < required + 1024 * 1024:
        raise ValueError('Insufficient free space on USB volume')
    checked = []
    seen = set()
    for entry in files:
        rel = PurePosixPath(entry['relative'])
        if rel.is_absolute() or not rel.parts or any(p in ('.', '..') for p in rel.parts):
            raise ValueError('Invalid destination path')
        if any(any(c in part for c in UNSAFE) or part.endswith((' ', '.'))
               for part in rel.parts):
            raise ValueError('Filename cannot be safely represented on this volume')
        target = base.joinpath(*rel.parts)
        folded = str(rel).casefold()
        if folded in seen:
            raise ValueError('Duplicate destination filename')
        seen.add(folded)
        for part in [target, *target.parents]:
            if part == MOUNT:
                break
            if part.is_symlink():
                raise ValueError('Destination symlinks are not allowed')
        if (target.exists() or target.is_symlink()) and not (
                overwrite and target.is_file() and not target.is_symlink()):
            raise ValueError('Destination already exists: ' + str(rel))
        source = spool / entry['blob']
        if source.parent != spool or source.is_symlink() or not source.is_file():
            raise ValueError('Invalid source file')
        if source.stat().st_size != entry['size']:
            raise ValueError('Staged file checksum mismatch')
        if volume['fstype'] == 'vfat' and entry['size'] >= 4294967296:
            raise ValueError('FAT32 does not support files of 4 GiB or larger')
        checked.append((source, target, entry))
    # Progress counts each byte twice: written to the USB, then read back for verification.
    progress = Progress(spool / '.progress', 2 * required, len(checked))
    completed = []
    for index, (source, target, entry) in enumerate(checked, 1):
        target.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix='.usbproxy-', dir=target.parent)
        try:
            staged = hashlib.sha256()
            progress.begin('write', entry['relative'], index)
            with os.fdopen(fd, 'wb') as dest, source.open('rb') as src:
                for chunk in iter(lambda: src.read(1024 * 1024), b''):
                    staged.update(chunk)
                    dest.write(chunk)
                    progress.add(len(chunk))
                dest.flush()
                os.fsync(dest.fileno())
            # The staged copy is hashed while writing; a mismatch never reaches the destination name.
            if staged.hexdigest() != entry['sha256']:
                raise ValueError('Staged file checksum mismatch: ' + entry['relative'])
            progress.begin('verify', entry['relative'], index)
            if digest(temporary, progress.add) != entry['sha256']:
                raise ValueError('USB read-back checksum mismatch: ' + entry['relative'])
            # No other application writes this exclusively attached volume.
            if target.is_symlink() or (target.exists() and not overwrite):
                raise ValueError('Destination appeared during copy')
            os.replace(temporary, target)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        completed.append(entry['relative'])
    os.sync()
    shutil.rmtree(spool)
    return {'copied': completed, 'bytes': required, 'verified': True}


def stream(request):
    """Write one file's bytes to stdout; the result (with SHA-256) goes to stderr."""
    mount(request['device'])
    source, rel = resolve(request['path'])
    if source.is_symlink() or not source.is_file():
        raise ValueError('Only regular files can be opened')
    h = hashlib.sha256()
    size = 0
    out = sys.stdout.buffer
    with source.open('rb') as src:
        for chunk in iter(lambda: src.read(1024 * 1024), b''):
            h.update(chunk)
            out.write(chunk)
            size += len(chunk)
    out.flush()
    return {'path': rel, 'name': source.name, 'size': size, 'sha256': h.hexdigest()}


def dispatch(request):
    action = request['action']
    if action == 'status':
        return {'ready': Path('/var/lib/usbproxy/ready').exists(),
                'volumes': devices(), 'mounted': mounted()}
    if action == 'copy':
        return copy(request)
    if action == 'list':
        return listing(request)
    if action == 'delete':
        return delete(request)
    if action == 'mkdir':
        return make_folder(request)
    if action == 'rename':
        return rename(request)
    if action == 'stream':
        return stream(request)
    if action == 'self-update':
        return self_update(request)
    if action == 'eject':
        os.sync()
        if mounted():
            subprocess.run(['umount', str(MOUNT)], check=True, capture_output=True, text=True)
        return {'unmounted': True}
    if action == 'shutdown':
        os.sync()
        if mounted():
            subprocess.run(['umount', str(MOUNT)], check=True, capture_output=True, text=True)
        subprocess.Popen(['shutdown', '-h', '+1'])
        return {'shutdown': True}
    raise ValueError('Unknown action')


if __name__ == '__main__':
    out = sys.stdout
    try:
        request = json.load(sys.stdin)
        # A stream's stdout carries file bytes, so its JSON result goes to stderr.
        if request.get('action') == 'stream':
            out = sys.stderr
        print(json.dumps({'ok': True, 'data': dispatch(request)}, ensure_ascii=False), file=out)
    except Exception as exc:
        print(json.dumps({'ok': False, 'error': str(exc)}, ensure_ascii=False), file=out)
        sys.exit(1)
