// Updates from GitHub releases: each release carries a small USBProxy-vX.Y.Z-update.zip (program files only).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace UsbProxy
{
    static class Updater
    {
        public const string Current = "0.0.9";
        const string Repo = "ikenican/usb-proxy";
        // Folders that belong to this machine and are never touched by an update.
        static readonly string[] keep = { "runtime", "logs", "backups", "downloads", "dist" };
        static string token;

        // The newest release above this version that has an update zip, or null. Throws when GitHub can't be reached.
        public static Map Check()
        {
            var releases = J.A(J.Read("{\"r\":" + Get("https://api.github.com/repos/" + Repo + "/releases?per_page=20", "application/vnd.github+json") + "}"), "r");
            Map best = null; Version bestVer = Parse(Current);
            foreach (Map r in releases.OfType<Map>())
            {
                Version v = Parse(J.S(r, "tag_name"));
                if (J.B(r, "draft") || v == null || v <= bestVer) continue;
                var asset = J.Maps(r, "assets").FirstOrDefault(a => J.S(a, "name").EndsWith("-update.zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null) continue;
                best = J.Obj("version", v.ToString(), "url", J.S(asset, "url"), "digest", J.S(asset, "digest"), "notes", J.S(r, "body"));
                bestVer = v;
            }
            return best;
        }

        // Downloads, verifies and copies the new files over this folder. The running exe is renamed aside (Windows allows that).
        public static void Install(Map info)
        {
            string dir = Path.Combine(Core.Runtime, "update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "update.zip");
            File.WriteAllBytes(zip, GetBytes(J.S(info, "url"), "application/octet-stream"));
            string digest = J.S(info, "digest");
            if (digest.StartsWith("sha256:") && !string.Equals(digest.Substring(7), Core.Sha256File(zip), StringComparison.OrdinalIgnoreCase))
                throw new ToolException(Core.T("下載的更新檔驗證失敗（SHA-256 不符），未做任何變更。"));
            string extracted = Path.Combine(dir, "files");
            ZipFile.ExtractToDirectory(zip, extracted);
            string source = Directory.GetFiles(extracted, "USB Proxy.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
            if (source == null) throw new ToolException(Core.T("更新檔內容不正確，未做任何變更。"));
            string old = Core.ExePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Core.ExePath, old);
            try
            {
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(source.Length + 1);
                    if (keep.Contains(rel.Split('\\')[0], StringComparer.OrdinalIgnoreCase)) continue;
                    string target = Path.Combine(Core.Root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                }
            }
            catch
            {
                if (!File.Exists(Core.ExePath)) File.Move(old, Core.ExePath);
                throw;
            }
            try { Directory.Delete(dir, true); } catch { }
        }

        // Leftover from the previous update; it can be deleted once the new exe is running.
        public static void CleanOld() { try { File.Delete(Core.ExePath + ".old"); } catch { } }

        static Version Parse(string tag)
        {
            Version v; return System.Version.TryParse((tag ?? "").TrimStart('v', 'V'), out v) ? v : null;
        }

        static string Get(string url, string accept) { return Core.Utf8.GetString(GetBytes(url, accept)); }

        static byte[] GetBytes(string url, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try { return Fetch(url, accept, token); }
            catch (WebException ex)
            {
                // A private repository answers 404 without a login; use the GitHub CLI login on this PC when there is one.
                var response = ex.Response as HttpWebResponse;
                if (token != null || response == null || response.StatusCode != HttpStatusCode.NotFound) throw;
                string gh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"GitHub CLI\gh.exe");
                if (!File.Exists(gh)) throw;
                try { token = Core.Run(gh, new[] { "auth", "token" }, "", 20).Trim(); } catch { throw ex; }
                return Fetch(url, accept, token);
            }
        }

        static byte[] Fetch(string url, string accept, string auth)
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "USBProxy/" + Current;
                client.Headers[HttpRequestHeader.Accept] = accept;
                if (auth != null) client.Headers[HttpRequestHeader.Authorization] = "Bearer " + auth;
                return client.DownloadData(url);
            }
        }
    }
}
