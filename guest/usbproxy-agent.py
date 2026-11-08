#!/usr/bin/env python3
"""Privileged guest helper. Only USB-backed volumes may be mounted or written.

Each volume is mounted at /mnt/usbproxy/<device name>, so several drives can be
used at once. Request paths are always relative to a volume's root.
"""
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
CHUNK = 1024 * 1024


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def devices():
    tree = json.loads(run('lsblk', '--json', '--tree', '--bytes', '--output',
                          'PATH,TYPE,TRAN,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINTS,MODEL,SERIAL'))
    result = []

    # Partitions inherit the serial/model of their disk so the host can tell drives apart.
    def visit(node, usb=False, serial=None, model=None):
        usb = usb or node.get('tran') == 'usb'
        serial = node.get('serial') or serial
        model = node.get('model') or model
        if usb and node.get('fstype') in ('vfat', 'exfat', 'ntfs', 'ext4'):
            entry = {k: node.get(k) for k in ('path', 'size', 'fstype', 'label', 'uuid', 'mountpoints')}
            entry['serial'] = (serial or '').strip()
            entry['model'] = (model or '').strip()
            result.append(entry)
        for child in node.get('children', []):
            visit(child, usb, serial, model)
    for node in tree['blockdevices']:
        visit(node)
    return result


def mounts():
    """Our mounts: [{'source': device, 'target': mountpoint}]."""
    p = subprocess.run(['findmnt', '--json', '--list', '--output', 'SOURCE,TARGET'],
                       capture_output=True, text=True)
    if p.returncode != 0:
        return []
    prefix = str(MOUNT) + '/'
    return [m for m in json.loads(p.stdout)['filesystems'] if m['target'].startswith(prefix)]


def mount_root(device):
    return MOUNT / Path(device).name


def mount(device):
    """Mount a USB volume (once) and return its description with 'root'."""
    candidates = {d['path']: d for d in devices()}
    if device not in candidates:
        raise ValueError('Only a supported USB volume may be selected')
    volume = dict(candidates[device])
    root = mount_root(device)
    volume['root'] = root
    if os.path.ismount(root):
        return volume
    if any(volume.get('mountpoints') or []):
        raise ValueError('Volume is already mounted elsewhere')
    root.mkdir(parents=True, exist_ok=True)
    driver = 'ntfs-3g' if volume['fstype'] == 'ntfs' else volume['fstype']
    subprocess.run(['mount', '-t', driver, '-o', 'rw,nosuid,nodev,noexec', device, str(root)],
                   check=True, capture_output=True, text=True)
    return volume


def unmount(serial=None):
    """Flush and unmount our volumes; with a serial, only that drive's partitions."""
    os.sync()
    owners = {d['path']: d['serial'] for d in devices()}
    released = []
    for m in mounts():
        if serial and owners.get(m['source']) not in (serial, None):
            continue
        subprocess.run(['umount', m['target']], check=True, capture_output=True, text=True)
        try:
            os.rmdir(m['target'])
        except OSError:
            pass
        released.append(m['source'])
    return released


def digest(path, on_bytes=None):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(CHUNK), b''):
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


def resolve(root, relative):
    """Map a volume-relative path below root without following symlinks."""
    rel = PurePosixPath(relative or '.')
    if rel.is_absolute() or '..' in rel.parts:
        raise ValueError('Invalid path')
    parts = [p for p in rel.parts if p != '.']
    current = root
    for part in parts:
        current = current / part
        if current.is_symlink():
            raise ValueError('Symbolic links are not followed')
    return current, '/'.join(parts)


def spool_dir(value):
    spool = Path(value).resolve()
    if spool.parent != INCOMING or spool.is_symlink() or not spool.is_dir():
        raise ValueError('Invalid staging directory')
    return spool


def check_destination(root, target, overwrite=False):
    for part in [target, *target.parents]:
        if part == root:
            break
        if part.is_symlink():
            raise ValueError('Destination symlinks are not allowed')
    if (target.exists() or target.is_symlink()) and not (
            overwrite and target.is_file() and not target.is_symlink()):
        raise ValueError('Destination already exists: ' + str(target.relative_to(root)))


def write_verified(source, target, expected, progress, label, index, overwrite=False):
    """Copy to a temp name, check the source hash and the read-back hash, then rename into place."""
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix='.usbproxy-', dir=target.parent)
    try:
        h = hashlib.sha256()
        progress.begin('write', label, index)
        with os.fdopen(fd, 'wb') as dest, open(source, 'rb') as src:
            for chunk in iter(lambda: src.read(CHUNK), b''):
                h.update(chunk)
                dest.write(chunk)
                progress.add(len(chunk))
            dest.flush()
            os.fsync(dest.fileno())
        # A source that does not match never reaches the destination name.
        if expected is not None and h.hexdigest() != expected:
            raise ValueError('Staged file checksum mismatch: ' + label)
        progress.begin('verify', label, index)
        if digest(temporary, progress.add) != h.hexdigest():
            raise ValueError('USB read-back checksum mismatch: ' + label)
        # No other application writes these exclusively attached volumes.
        if target.is_symlink() or (target.exists() and not overwrite):
            raise ValueError('Destination appeared during copy')
        os.replace(temporary, target)
        return h.hexdigest()
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def collect(root, paths):
    """Files and folders under the selected volume-relative paths, keyed by name relative to their parent."""
    files, folders = [], []
    for relative in paths:
        target, rel = resolve(root, relative)
        if target == root:
            raise ValueError('Select files or folders, not the whole volume')
        if target.is_symlink() or not target.exists():
            raise ValueError('Not found: ' + rel)
        base = target.parent
        if target.is_file():
            files.append((target, str(PurePosixPath(target.relative_to(base)))))
            continue
        folders.append(str(PurePosixPath(target.relative_to(base))))
        for current, dirs, names in os.walk(target, followlinks=False):
            here = Path(current)
            dirs[:] = sorted(d for d in dirs if not (here / d).is_symlink())
            for d in dirs:
                folders.append(str(PurePosixPath((here / d).relative_to(base))))
            for name in sorted(names):
                path = here / name
                if path.is_symlink() or not path.is_file() or name.startswith('.usbproxy-'):
                    continue
                files.append((path, str(PurePosixPath(path.relative_to(base)))))
    return files, folders


def listing(request):
    volume = mount(request['device'])
    root = volume['root']
    folder, rel = resolve(root, request.get('path', ''))
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
    usage = shutil.disk_usage(root)
    return {'path': rel, 'entries': entries, 'free': usage.free, 'total': usage.total,
            'fstype': volume['fstype']}


def walk(request):
    """List every file under the selection so the host can download folders."""
    root = mount(request['device'])['root']
    files, folders = collect(root, request['paths'])
    return {'files': [{'path': str(p.relative_to(root)), 'relative': r, 'size': p.stat().st_size}
                      for p, r in files],
            'folders': folders}


def delete(request):
    root = mount(request['device'])['root']
    removed = []
    for relative in request['paths']:
        target, rel = resolve(root, relative)
        if target == root:
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
    root = mount(request['device'])['root']
    parent, rel = resolve(root, request.get('path', ''))
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
    root = mount(request['device'])['root']
    source, rel = resolve(root, request['path'])
    if source == root or not (source.exists() or source.is_symlink()):
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


def check_relative(relative):
    rel = PurePosixPath(relative)
    if rel.is_absolute() or not rel.parts or any(p in ('.', '..') for p in rel.parts):
        raise ValueError('Invalid destination path')
    for part in rel.parts:
        check_name(part)
    return rel


def copy(request):
    """Write files the host staged in the spool into a folder on a volume."""
    volume = mount(request['device'])
    root = volume['root']
    spool = spool_dir(request['spool'])
    base, _ = resolve(root, request.get('dest', ''))
    if not base.is_dir():
        raise ValueError('Destination folder not found')
    # Overwrite replaces existing regular files only (writing back an edited file).
    overwrite = bool(request.get('overwrite'))
    files = json.loads((spool / 'manifest.json').read_text(encoding='utf-8-sig'))['files']
    if not files:
        raise ValueError('No files to copy')
    required = sum(int(f['size']) for f in files)
    if shutil.disk_usage(root).free < required + CHUNK:
        raise ValueError('Insufficient free space on USB volume')
    checked, seen = [], set()
    for entry in files:
        rel = check_relative(entry['relative'])
        if str(rel).casefold() in seen:
            raise ValueError('Duplicate destination filename')
        seen.add(str(rel).casefold())
        target = base.joinpath(*rel.parts)
        check_destination(root, target, overwrite)
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
    for index, (source, target, entry) in enumerate(checked, 1):
        write_verified(source, target, entry['sha256'], progress, entry['relative'], index, overwrite)
    os.sync()
    shutil.rmtree(spool)
    return {'copied': [e['relative'] for _, _, e in checked], 'bytes': required, 'verified': True}


def transfer(request):
    """Copy files and folders between volumes (or folders) inside the guest, verified by SHA-256."""
    source = mount(request['source'])
    destination = mount(request['device'])
    spool = spool_dir(request['spool'])
    base, _ = resolve(destination['root'], request.get('dest', ''))
    if not base.is_dir():
        raise ValueError('Destination folder not found')
    files, folders = collect(source['root'], request['paths'])
    for relative in request['paths']:
        selected, _ = resolve(source['root'], relative)
        if selected.is_dir() and (base == selected or selected in base.parents):
            raise ValueError('A folder cannot be copied into itself')
    targets = []
    for relative in folders:
        check_relative(relative)
        check_destination(destination['root'], base / relative)
    for path, relative in files:
        target = base / check_relative(relative)
        check_destination(destination['root'], target)
        if destination['fstype'] == 'vfat' and path.stat().st_size >= 4294967296:
            raise ValueError('FAT32 does not support files of 4 GiB or larger: ' + relative)
        targets.append((path, target, relative))
    required = sum(p.stat().st_size for p, _, _ in targets)
    if shutil.disk_usage(destination['root']).free < required + CHUNK:
        raise ValueError('Insufficient free space on the destination volume')
    progress = Progress(spool / '.progress', 2 * required, len(targets))
    for relative in folders:
        (base / relative).mkdir(parents=True, exist_ok=True)
    for index, (path, target, relative) in enumerate(targets, 1):
        write_verified(path, target, None, progress, relative, index)
    os.sync()
    return {'copied': [r for _, _, r in targets], 'bytes': required, 'verified': True}


def stream(request):
    """Write one file's bytes to stdout; the result (with SHA-256) goes to stderr."""
    root = mount(request['device'])['root']
    source, rel = resolve(root, request['path'])
    if source.is_symlink() or not source.is_file():
        raise ValueError('Only regular files can be opened')
    h = hashlib.sha256()
    size = 0
    out = sys.stdout.buffer
    with source.open('rb') as src:
        for chunk in iter(lambda: src.read(CHUNK), b''):
            h.update(chunk)
            out.write(chunk)
            size += len(chunk)
    out.flush()
    return {'path': rel, 'name': source.name, 'size': size, 'sha256': h.hexdigest()}


def dispatch(request):
    action = request['action']
    if action == 'status':
        return {'ready': Path('/var/lib/usbproxy/ready').exists(),
                'volumes': devices(), 'mounted': mounts()}
    handlers = {'copy': copy, 'transfer': transfer, 'list': listing, 'walk': walk, 'delete': delete,
                'mkdir': make_folder, 'rename': rename, 'stream': stream, 'self-update': self_update}
    if action in handlers:
        return handlers[action](request)
    if action == 'eject':
        return {'unmounted': unmount(request.get('serial') or None)}
    if action == 'shutdown':
        unmount()
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
