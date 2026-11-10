"""Non-privileged validation: real file I/O with a temporary mount substitute.

No disks are formatted or attached. These tests do not validate USB passthrough.
"""
import hashlib
import importlib.machinery
import io
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

loader = importlib.machinery.SourceFileLoader('agent', '/usr/local/sbin/usbproxy-agent')
spec = importlib.util.spec_from_loader(loader.name, loader)
agent = importlib.util.module_from_spec(spec)
loader.exec_module(agent)


class TransferTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.destination = Path(self.temporary.name) / 'volume'
        self.destination.mkdir()
        self.spool = Path('/home/usbproxy/incoming') / ('selftest-' + uuid.uuid4().hex)
        self.spool.mkdir(parents=True)
        self.addCleanup(lambda: __import__('shutil').rmtree(self.spool, ignore_errors=True))
        self.addCleanup(self.temporary.cleanup)
        self.mount_patch = patch.object(agent, 'MOUNT', self.destination)
        self.mount_patch.start()
        self.addCleanup(self.mount_patch.stop)
        self.attach_patch = patch.object(agent, 'mount', return_value={'fstype': 'exfat', 'root': self.destination})
        self.attach_patch.start()
        self.addCleanup(self.attach_patch.stop)

    def manifest(self, relative='測試資料夾/檔案 with spaces.txt', payload=b'USB proxy\x00\xff\n'):
        self.spool.mkdir(parents=True, exist_ok=True)
        (self.spool / 'file-0').write_bytes(payload)
        entry = {'relative': relative, 'blob': 'file-0', 'size': len(payload),
                 'sha256': hashlib.sha256(payload).hexdigest()}
        (self.spool / 'manifest.json').write_text(json.dumps({'files': [entry]}), encoding='utf-8')
        return entry

    def copy(self):
        return agent.copy({'device': '/dev/test-only', 'spool': str(self.spool)})

    def test_unicode_and_binary_hash(self):
        entry = self.manifest()
        result = self.copy()
        self.assertTrue(result['verified'])
        self.assertEqual(agent.digest(self.destination / entry['relative']), entry['sha256'])
        self.assertFalse(self.spool.exists())

    def test_existing_file_is_preserved(self):
        self.manifest('keep.txt')
        (self.destination / 'keep.txt').write_bytes(b'original')
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.copy()
        self.assertEqual((self.destination / 'keep.txt').read_bytes(), b'original')

    def test_tampered_stage_is_rejected(self):
        self.manifest('bad.txt')
        (self.spool / 'file-0').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            self.copy()
        self.assertFalse((self.destination / 'bad.txt').exists())

    def test_parent_traversal_is_rejected(self):
        self.manifest('../outside.txt')
        with self.assertRaisesRegex(ValueError, 'Invalid destination'):
            self.copy()

    def test_destination_symlink_is_rejected(self):
        self.manifest('link/file.txt')
        (self.destination / 'link').symlink_to(self.temporary.name, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'symlinks'):
            self.copy()

    def test_non_usb_mount_is_rejected(self):
        self.attach_patch.stop()
        with patch.object(agent, 'devices', return_value=[]):
            with self.assertRaisesRegex(ValueError, 'Only a supported USB'):
                agent.mount('/dev/sda1')

    def test_readback_corruption_is_reported(self):
        self.manifest('verify.txt')
        real_digest = agent.digest
        def corrupt(path, on_bytes=None):
            return '0' * 64 if Path(path).name.startswith('.usbproxy-') else real_digest(path, on_bytes)
        with patch.object(agent, 'digest', side_effect=corrupt):
            with self.assertRaisesRegex(ValueError, 'read-back checksum'):
                self.copy()
        self.assertFalse((self.destination / 'verify.txt').exists())
        self.assertEqual(list(self.destination.glob('.usbproxy-*')), [])


    def test_copy_into_subfolder(self):
        (self.destination / 'docs').mkdir()
        entry = self.manifest('note.txt')
        agent.copy({'device': '/dev/test-only', 'spool': str(self.spool), 'dest': 'docs'})
        self.assertEqual(agent.digest(self.destination / 'docs' / 'note.txt'), entry['sha256'])

    def test_overwrite_replaces_only_files(self):
        entry = self.manifest('edit.txt', b'edited')
        (self.destination / 'edit.txt').write_bytes(b'original')
        agent.copy({'device': '/dev/test-only', 'spool': str(self.spool), 'overwrite': True})
        self.assertEqual((self.destination / 'edit.txt').read_bytes(), b'edited')
        self.manifest('folder', b'x')
        (self.destination / 'folder').mkdir()
        with self.assertRaisesRegex(ValueError, 'already exists'):
            agent.copy({'device': '/dev/test-only', 'spool': str(self.spool), 'overwrite': True})


class BrowserTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'volume'
        self.root.mkdir()
        for target, value in ((agent, 'MOUNT'),):
            p = patch.object(target, value, self.root)
            p.start()
            self.addCleanup(p.stop)
        p = patch.object(agent, 'mount', return_value={'fstype': 'exfat', 'root': self.root})
        p.start()
        self.addCleanup(p.stop)
        (self.root / '資料夾').mkdir()
        (self.root / '資料夾' / 'a.txt').write_bytes(b'alpha')
        (self.root / '.usbproxy-tmp').write_bytes(b'')

    def request(self, **values):
        values.setdefault('device', '/dev/test-only')
        return values

    def test_listing_hides_staging_files(self):
        result = agent.listing(self.request(path=''))
        names = {e['name']: e for e in result['entries']}
        self.assertEqual(set(names), {'資料夾'})
        self.assertTrue(names['資料夾']['dir'])
        inner = agent.listing(self.request(path='資料夾'))
        self.assertEqual(inner['path'], '資料夾')
        self.assertEqual(inner['entries'][0]['size'], 5)

    def test_paths_cannot_escape_or_follow_links(self):
        with self.assertRaisesRegex(ValueError, 'Invalid path'):
            agent.listing(self.request(path='../'))
        with self.assertRaisesRegex(ValueError, 'Invalid path'):
            agent.delete(self.request(paths=['/etc']))
        (self.root / 'link').symlink_to(self.temporary.name, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'Symbolic links'):
            agent.listing(self.request(path='link/volume'))

    def test_mkdir_rename_delete(self):
        agent.make_folder(self.request(path='資料夾', name='新資料夾'))
        self.assertTrue((self.root / '資料夾' / '新資料夾').is_dir())
        with self.assertRaisesRegex(ValueError, 'Already exists'):
            agent.make_folder(self.request(path='資料夾', name='新資料夾'))
        with self.assertRaisesRegex(ValueError, 'safely represented'):
            agent.make_folder(self.request(path='', name='bad:name'))
        agent.rename(self.request(path='資料夾/a.txt', name='b.txt'))
        self.assertEqual((self.root / '資料夾' / 'b.txt').read_bytes(), b'alpha')
        agent.delete(self.request(paths=['資料夾']))
        self.assertFalse((self.root / '資料夾').exists())
        with self.assertRaisesRegex(ValueError, 'root'):
            agent.delete(self.request(paths=['']))

    def test_stream_writes_bytes_and_reports_hash(self):
        class Out:
            buffer = io.BytesIO()
        with patch.object(agent.sys, 'stdout', Out()):
            result = agent.stream(self.request(path='資料夾/a.txt'))
            self.assertEqual(Out.buffer.getvalue(), b'alpha')
        self.assertEqual(result['size'], 5)
        self.assertEqual(result['sha256'], hashlib.sha256(b'alpha').hexdigest())
        with self.assertRaisesRegex(ValueError, 'regular files'):
            agent.stream(self.request(path='資料夾'))


class TransferBetweenVolumesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        base = Path(self.temporary.name)
        self.a, self.b = base / 'a', base / 'b'
        self.a.mkdir(); self.b.mkdir()
        (self.a / '照片').mkdir()
        (self.a / '照片' / '1.jpg').write_bytes(b'one')
        (self.a / '照片' / '空資料夾').mkdir()
        (self.a / 'note.txt').write_bytes(b'note')
        volumes = {'/dev/a': {'fstype': 'exfat', 'root': self.a}, '/dev/b': {'fstype': 'vfat', 'root': self.b}}
        p = patch.object(agent, 'mount', side_effect=lambda device: volumes[device])
        p.start()
        self.addCleanup(p.stop)
        self.spool = Path('/home/usbproxy/incoming') / ('selftest-' + uuid.uuid4().hex)
        self.spool.mkdir(parents=True)
        self.addCleanup(lambda: __import__('shutil').rmtree(self.spool, ignore_errors=True))

    def transfer(self, paths, source='/dev/a', device='/dev/b', dest=''):
        return agent.transfer({'source': source, 'device': device, 'paths': paths, 'dest': dest, 'spool': str(self.spool)})

    def test_folder_and_file_copy_between_volumes(self):
        result = self.transfer(['照片', 'note.txt'])
        self.assertTrue(result['verified'])
        self.assertEqual((self.b / '照片' / '1.jpg').read_bytes(), b'one')
        self.assertTrue((self.b / '照片' / '空資料夾').is_dir())
        self.assertEqual((self.b / 'note.txt').read_bytes(), b'note')
        self.assertEqual((self.a / 'note.txt').read_bytes(), b'note')
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.transfer(['note.txt'])

    def test_folder_cannot_be_copied_into_itself(self):
        with self.assertRaisesRegex(ValueError, 'into itself'):
            self.transfer(['照片'], device='/dev/a', dest='照片')

    def test_walk_lists_files_relative_to_selection(self):
        result = agent.walk({'device': '/dev/a', 'paths': ['照片']})
        self.assertEqual([(f['path'], f['relative']) for f in result['files']], [('照片/1.jpg', '照片/1.jpg')])
        self.assertIn('照片/空資料夾', result['folders'])


class ProgressTests(unittest.TestCase):
    setUp = TransferTests.setUp
    manifest = TransferTests.manifest
    copy = TransferTests.copy

    def test_copy_reports_write_and_verify_progress(self):
        stages = []
        real_begin = agent.Progress.begin
        def record(progress, stage, file, index):
            stages.append((stage, file, index))
            return real_begin(progress, stage, file, index)
        payload = b'x' * (3 * 1024 * 1024)
        self.manifest('big.bin', payload)
        with patch.object(agent.Progress, 'begin', record):
            self.copy()
        self.assertEqual(stages, [('write', 'big.bin', 1), ('verify', 'big.bin', 1)])


class WaitVolumeTests(unittest.TestCase):
    def test_new_single_volume_is_found_and_mounted(self):
        old, new = {'path': '/dev/sdb1'}, {'path': '/dev/sdc1'}
        seen = iter([[old], [old], [old, new]] + [[old, new]] * 10)
        mounted = []
        with patch.object(agent, 'devices', lambda: next(seen)), patch.object(agent, 'mounts', lambda: []), \
                patch.object(agent, 'mount', mounted.append):
            result = agent.wait_volume({'before': ['/dev/sdb1'], 'timeout': 5})
        self.assertEqual(result['new'], ['/dev/sdc1'])
        self.assertEqual(mounted, ['/dev/sdc1'])

    def test_nothing_new_times_out_without_mounting(self):
        mounted = []
        with patch.object(agent, 'devices', lambda: [{'path': '/dev/sdb1'}]), patch.object(agent, 'mounts', lambda: []), \
                patch.object(agent, 'mount', mounted.append):
            result = agent.wait_volume({'before': ['/dev/sdb1'], 'timeout': 0.3})
        self.assertEqual((result['new'], mounted), ([], []))


if __name__ == '__main__':
    unittest.main(verbosity=2)
