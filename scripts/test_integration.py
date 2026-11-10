# Integration test of "USB Proxy.exe --run" against an emulated FAT32 USB disk (/dev/sdb in the guest).
# Development only; never touches a real USB drive. Setup: create the disk with scripts\New-ValidationDisk.ps1,
# stop Linux, then
#   VBoxManage storagectl <vm> --name ValidationUSB --add usb --controller USB --bootable off
#   VBoxManage storageattach <vm> --storagectl ValidationUSB --port 0 --device 0 --type hdd --medium runtime\validation\fat32.vdi
# start Linux and run: python -I scripts\test_integration.py. Afterwards detach the disk and remove the controller.
import json, os, subprocess, sys, tempfile, shutil, filecmp, time
EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'USB Proxy.exe')
work = os.path.join(tempfile.gettempdir(), 'upbuild')
req = os.path.join(work, 'req.json')

def run(r):
    with open(req, 'w', encoding='utf-8') as f: json.dump(r, f, ensure_ascii=False)
    t = time.time()
    out = subprocess.run([EXE, '--run', req], capture_output=True).stdout.decode('utf-8')
    res = json.loads(out)
    print(f"{r['action']:9} {time.time()-t:5.1f}s ok={res['ok']} {res.get('error','')}")
    return res

dev = '/dev/sdb'
src = os.path.join(work, '中文測試資料夾')
dl = os.path.join(work, 'dl')
for p in (src, dl): shutil.rmtree(p, ignore_errors=True)
os.makedirs(os.path.join(src, '子資料夾')); os.makedirs(dl)
open(os.path.join(src, '中文 with spaces.txt'), 'w', encoding='utf-8').write('USB Proxy integration\n中文內容\n')
open(os.path.join(src, 'empty.bin'), 'wb').close()
open(os.path.join(src, '子資料夾', 'binary.bin'), 'wb').write(os.urandom(3 * 1024 * 1024 + 7))

r = run({'action': 'copy', 'device': dev, 'dest': '', 'paths': [src]}); assert r['ok'] and len(r['data']['copied']) == 3 and r['data'].get('verified'), r
r = run({'action': 'copy', 'device': dev, 'dest': '', 'paths': [src]}); assert not r['ok'] and 'exists' in r['error'], r
r = run({'action': 'list', 'device': dev, 'path': '中文測試資料夾'}); assert r['ok'] and {e['name'] for e in r['data']['entries']} == {'中文 with spaces.txt', 'empty.bin', '子資料夾'}, r
r = run({'action': 'fetch', 'device': dev, 'path': '中文測試資料夾/中文 with spaces.txt', 'size': 34, 'name': '中文 with spaces.txt'})
assert r['ok'] and open(r['data']['local'], encoding='utf-8').read() == 'USB Proxy integration\n中文內容\n', r
local = r['data']['local']
saveto = os.path.join(work, 'saved.bin')
r = run({'action': 'fetch', 'device': dev, 'path': '中文測試資料夾/子資料夾/binary.bin', 'size': 3145735, 'name': 'binary.bin', 'saveTo': saveto})
assert r['ok'] and filecmp.cmp(saveto, os.path.join(src, '子資料夾', 'binary.bin'), shallow=False), r
r = run({'action': 'download', 'device': dev, 'paths': ['中文測試資料夾'], 'destination': dl}); assert r['ok'] and r['data']['downloaded'] == 3, r
assert filecmp.cmp(os.path.join(dl, '中文測試資料夾', '子資料夾', 'binary.bin'), os.path.join(src, '子資料夾', 'binary.bin'), shallow=False)
r = run({'action': 'download', 'device': dev, 'paths': ['中文測試資料夾'], 'destination': dl}); assert not r['ok'] and 'Already exists' in r['error'], r
# write-back of an edited opened file
open(local, 'a', encoding='utf-8').write('edited\n')
r = run({'action': 'copy', 'device': dev, 'dest': '中文測試資料夾', 'paths': [local], 'overwrite': True, 'targetName': '中文 with spaces.txt'}); assert r['ok'], r
r = run({'action': 'mkdir', 'device': dev, 'path': '', 'name': '新資料夾'}); assert r['ok'], r
r = run({'action': 'transfer', 'source': dev, 'device': dev, 'paths': ['中文測試資料夾/子資料夾/binary.bin'], 'dest': '新資料夾'}); assert r['ok'] and len(r['data']['copied']) == 1, r
r = run({'action': 'rename', 'device': dev, 'path': '新資料夾', 'name': '改名資料夾'}); assert r['ok'], r
r = run({'action': 'list', 'device': dev, 'path': '改名資料夾'}); assert r['ok'] and [e['name'] for e in r['data']['entries']] == ['binary.bin'], r
r = run({'action': 'delete', 'device': dev, 'paths': ['改名資料夾', '中文測試資料夾']}); assert r['ok'], r
r = run({'action': 'list', 'device': dev, 'path': ''}); assert r['ok'] and r['data']['entries'] == [], r
print('ALL PASSED')
