// Shared plumbing: paths, JSON, external commands, SSH to the guest agent, UI language.
// Compiled by build.cmd with the C# 5 compiler built into Windows, so no newer syntax.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace UsbProxy
{
    // Loosely typed JSON values: objects are Dictionary<string,object>, arrays object[] (parsed) or any IList (built here).
    public static class J
    {
        static readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static string Write(object value) { return serializer.Serialize(value); }
        public static Dictionary<string, object> Read(string text) { return serializer.DeserializeObject(text) as Dictionary<string, object>; }
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        public static object Get(IDictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }
        public static string S(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static long L(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static int I(IDictionary<string, object> d, string key) { return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, L(d, key))); }
        public static double D(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static bool B(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0;
            return v != null && L(d, key) != 0;
        }
        public static Dictionary<string, object> M(IDictionary<string, object> d, string key) { return Get(d, key) as Dictionary<string, object>; }
        public static List<object> A(object value)
        {
            var list = new List<object>();
            if (value == null || value is string || value is IDictionary) { if (value != null) list.Add(value); return list; }
            var e = value as IEnumerable;
            if (e != null) foreach (var x in e) if (x != null) list.Add(x);
            return list;
        }
        public static List<object> A(IDictionary<string, object> d, string key) { return A(Get(d, key)); }
        public static List<Dictionary<string, object>> Maps(object value) { return A(value).OfType<Dictionary<string, object>>().ToList(); }
        public static List<Dictionary<string, object>> Maps(IDictionary<string, object> d, string key) { return Maps(Get(d, key)); }
        public static List<string> Strings(IDictionary<string, object> d, string key)
        {
            return A(d, key).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }
    }

    public class ToolException : Exception { public ToolException(string message) : base(message) { } }

    public static class Core
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Runtime = Path.Combine(Root, "runtime");
        public static readonly string ConfigFile = Path.Combine(Runtime, "config.json");
        public static readonly string UiFile = Path.Combine(Runtime, "ui.json");
        public static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static string VBoxManage { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Oracle\VirtualBox\VBoxManage.exe"); } }

        public static void WriteUtf8(string path, string text) { File.WriteAllText(path, text, Utf8); }
        public static string ReadText(string path) { return File.ReadAllText(path, Encoding.UTF8); }
        public static string Sha256File(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var s = File.OpenRead(path)) return Hex(sha.ComputeHash(s));
        }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static string NewId() { return Guid.NewGuid().ToString("N"); }

        // --- UI language: runtime\ui.json "lang" = zh (default) or en; source strings are Chinese, src\en.json maps them.
        static Dictionary<string, object> english;
        public static void LoadLanguage()
        {
            string lang = "zh";
            try { lang = J.S(J.Read(ReadText(UiFile)), "lang"); } catch { }
            UseLanguage(lang);
        }
        public static void UseLanguage(string lang)
        {
            english = null;
            try { if (lang == "en") english = J.Read(ReadText(Path.Combine(Root, @"src\en.json"))); }
            catch { english = null; }
        }
        public static string T(string text)
        {
            object v;
            return english != null && english.TryGetValue(text, out v) && v is string ? (string)v : text;
        }

        // --- External commands ---
        // Windows CommandLineToArgvW quoting, including trailing backslashes.
        public static string Quote(string value)
        {
            if (value.Length > 0 && !Regex.IsMatch(value, "[\\s\"]")) return value;
            return "\"" + Regex.Replace(Regex.Replace(value, "(\\\\*)\"", "$1$1\\\""), "(\\\\+)$", "$1$1") + "\"";
        }
        public static string JoinArgs(IEnumerable<string> args) { return string.Join(" ", args.Select(Quote)); }

        public static Process StartHidden(string file, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo(file, JoinArgs(args))
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Utf8
            };
            return Process.Start(info);
        }
        // UTF-8 without BOM: the console code page would mangle non-ASCII paths, and a BOM breaks the agent.
        public static void WriteLine(Process p, string text)
        {
            byte[] bytes = Utf8.GetBytes(text + "\n");
            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            p.StandardInput.BaseStream.Flush();
        }

        // Runs a command, returns trimmed stdout, throws with stderr on a non-zero exit.
        // poll runs about once a second while the command is still working (progress reporting).
        public static string Run(string file, IEnumerable<string> args, string input = "", int timeoutSeconds = 120, Action poll = null)
        {
            var info = new ProcessStartInfo(file, JoinArgs(args))
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
            };
            using (var p = Process.Start(info))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!string.IsNullOrEmpty(input)) WriteLine(p, input);
                p.StandardInput.Close();
                DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
                while (!p.WaitForExit(1000))
                {
                    if (DateTime.Now > deadline) { try { p.Kill(); } catch { } throw new ToolException("Command timed out: " + file); }
                    if (poll != null) try { poll(); } catch { }
                }
                p.WaitForExit();
                string output = stdout.Result, errorText = stderr.Result;
                if (p.ExitCode != 0) throw new ToolException(file + " exited " + p.ExitCode + "\n" + errorText + "\n" + output);
                return output.Trim();
            }
        }
        public static string VBox(params string[] args)
        {
            if (!File.Exists(VBoxManage)) throw new ToolException("VirtualBox is not installed.");
            return Run(VBoxManage, args);
        }

        // --- Config (runtime\config.json), kept as a dictionary so unknown keys survive a save ---
        public static Dictionary<string, object> ReadConfig()
        {
            if (!File.Exists(ConfigFile)) throw new ToolException(T("尚未安裝 Linux 環境，請先按「安裝」。"));
            return J.Read(ReadText(ConfigFile)) ?? new Dictionary<string, object>();
        }
        public static void SaveConfig(Dictionary<string, object> config) { WriteUtf8(ConfigFile, J.Write(config)); }

        // --- SSH to the guest ---
        public static List<string> SshArgs(Dictionary<string, object> config, bool scp = false)
        {
            return new List<string> {
                "-i", Path.Combine(Runtime, "id_ed25519"),
                "-o", "BatchMode=yes", "-o", "ConnectTimeout=4",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=runtime/known_hosts",
                "-o", "IdentitiesOnly=yes", "-o", "LogLevel=ERROR",
                scp ? "-P" : "-p", J.S(config, "sshPort") };
        }
        public static List<string> SshTo(Dictionary<string, object> config, string command)
        {
            var a = SshArgs(config); a.Add("usbproxy@127.0.0.1"); a.Add(command); return a;
        }
        public static string Ssh(Dictionary<string, object> config, string command, int timeoutSeconds = 120)
        {
            return Run("ssh.exe", SshTo(config, command), "", timeoutSeconds);
        }

        public static Dictionary<string, object> Guest(Dictionary<string, object> config, Dictionary<string, object> request, int timeoutSeconds = 120, Action poll = null)
        {
            string raw;
            try { raw = Run("ssh.exe", SshTo(config, "sudo -n /usr/local/sbin/usbproxy-agent"), J.Write(request), timeoutSeconds, poll); }
            catch (ToolException e)
            {
                // The agent exits non-zero with a JSON error on stdout; surface just its message.
                var line = e.Message.Split('\n').LastOrDefault(l => Regex.IsMatch(l, "^\\s*\\{\"ok\": false"));
                if (line != null) throw new ToolException(J.S(J.Read(line), "error"));
                throw;
            }
            var response = J.Read(raw);
            if (!J.B(response, "ok")) throw new ToolException(J.S(response, "error"));
            return J.M(response, "data") ?? new Dictionary<string, object>();
        }

        public static void Sleep(int ms) { Thread.Sleep(ms); }
    }
}
