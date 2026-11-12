# Development only: publishes the version in src\Update.cs as a GitHub release with
#   USBProxy-Setup-vX.Y.Z.exe   installer (src\Installer.cs with the full package appended)
#   USBProxy-vX.Y.Z-full.zip    the same package for manual install
#   USBProxy-vX.Y.Z-update.zip  program files only, what the in-app updater downloads
# Bump Updater.Current and run build.cmd first, then:  python -I scripts\publish_release.py notes.md [--no-upload]
import os, re, shutil, struct, subprocess, sys, zipfile
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = 'ikenican/usb-proxy'
PROGRAM = ['USB Proxy.exe', 'build.cmd', '使用說明.txt', 'src', 'scripts', 'guest']
DOWNLOADS = ['VirtualBox-7.2.22-175245-Win.exe', 'virtualbox-SHA256SUMS', 'debian-13-generic-amd64.raw.zip', 'debian-SHA512SUMS']
EMPTY = ['runtime', 'logs', 'backups']

def files(rel):
    path = os.path.join(ROOT, rel)
    if os.path.isfile(path): yield rel; return
    for d, dirs, names in os.walk(path):
        dirs[:] = [x for x in dirs if x != '__pycache__']
        for n in names: yield os.path.relpath(os.path.join(d, n), ROOT)

def make_zip(path, rels, empty=()):
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for rel in rels:
            for f in files(rel): z.write(os.path.join(ROOT, f), f.replace(os.sep, '/'))
        for d in empty: z.writestr(d + '/', '')

def make_raw_zip():
    # Debian's tar.xz repacked as a zip of the unmodified disk.raw: tar.exe on Windows 10 cannot read xz.
    out = os.path.join(ROOT, 'downloads', 'debian-13-generic-amd64.raw.zip')
    if os.path.exists(out): return
    import tarfile
    with tarfile.open(os.path.join(ROOT, 'downloads', 'debian-13-generic-amd64.tar.xz')) as t, \
         zipfile.ZipFile(out + '.tmp', 'w', zipfile.ZIP_DEFLATED) as z:
        info = zipfile.ZipInfo('debian-13-generic-amd64.raw', (2025, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        with t.extractfile(t.getmember('disk.raw')) as src, z.open(info, 'w', force_zip64=True) as dst:
            shutil.copyfileobj(src, dst, 1 << 24)
    os.replace(out + '.tmp', out)

def main():
    make_raw_zip()
    notes = sys.argv[1]
    version = re.search(r'Current = "([^"]+)"', open(os.path.join(ROOT, 'src', 'Update.cs'), encoding='utf-8').read()).group(1)
    dist = os.path.join(ROOT, 'dist')
    os.makedirs(dist, exist_ok=True)
    full = os.path.join(dist, 'USBProxy-v%s-full.zip' % version)
    update = os.path.join(dist, 'USBProxy-v%s-update.zip' % version)
    setup = os.path.join(dist, 'USBProxy-Setup-v%s.exe' % version)
    make_zip(update, PROGRAM)
    make_zip(full, PROGRAM + [os.path.join('downloads', n) for n in DOWNLOADS], EMPTY)
    stub = os.path.join(dist, 'setup-stub.exe')
    csc = os.path.join(os.environ['WINDIR'], r'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
    subprocess.run([csc, '/nologo', '/target:winexe', '/optimize', '/platform:x64', '/win32icon:' + os.path.join(ROOT, 'src', 'app.ico'), '/out:' + stub,
                    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.IO.Compression.dll',
                    '/reference:System.IO.Compression.FileSystem.dll', '/reference:Microsoft.CSharp.dll', os.path.join(ROOT, 'src', 'Installer.cs')], check=True)
    with open(setup, 'wb') as out:
        for part in (stub, full):
            with open(part, 'rb') as f: shutil.copyfileobj(f, out, 1 << 20)
        out.write(struct.pack('<q', os.path.getsize(full)))
    os.remove(stub)
    print(setup, full, update, sep='\n')
    if '--no-upload' not in sys.argv:
        subprocess.run(['gh', 'release', 'create', 'v' + version, setup, full, update, '-R', REPO, '--title', 'USB Proxy v' + version, '--notes-file', notes], check=True)

if __name__ == '__main__':
    main()
