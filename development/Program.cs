using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyTitle("DeepSeekBridge")]
[assembly: AssemblyDescription("Selected text to DeepSeek browser companion")]
[assembly: AssemblyVersion("1.1.8.0")]
[assembly: AssemblyFileVersion("1.1.8.0")]

namespace DeepSeekBridge
{
    static class PortableStorage
    {
        internal static string Resolve(string applicationDirectory)
        { return Path.Combine(Path.GetFullPath(applicationDirectory),"logs"); }
        internal static void EnsureWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe=Path.Combine(directory,".write-check-"+Guid.NewGuid().ToString("N")+".tmp");
                using(var stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose))
                { stream.WriteByte(0); }
            }
            catch(IOException) { throw new Stop("F01","无法写入程序所在文件夹的 logs 目录。请将完整压缩包解压到可写的普通文件夹后再运行，不要直接在压缩包里启动。"); }
            catch(UnauthorizedAccessException) { throw new Stop("F01","程序所在文件夹没有写入权限。请将程序解压到你有写入权限的目录；运行记录不会改存到其他隐藏位置。"); }
        }
    }

    sealed class Stop : Exception
    {
        public readonly string Code;
        public Stop(string code, string message) : base(message) { Code = code; }
    }

    static class Rules
    {
        public static bool IsDestination(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                uri.Host.Equals("chat.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
                uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo);
        }
        internal static bool FreshClipboard(uint before, uint observed, uint after) { return observed != before && observed == after; }
        public static bool IsBrowser(string name) { return name == "msedge" || name == "chrome"; }
        public static bool SourceTitleMatches(string windowTitle,string tabTitle)
        {
            if(String.IsNullOrWhiteSpace(windowTitle) || String.IsNullOrWhiteSpace(tabTitle)) return false;
            string title=windowTitle.Replace("\u200b","").Trim();
            string tab=tabTitle.Replace("\u200b","").Trim();
            return title==tab || title.StartsWith(tab+" - ",StringComparison.Ordinal) || title.StartsWith(tab+" 和另外 ",StringComparison.Ordinal);
        }
        // Only for verifying the fixed URL we have just typed into a fresh address field.
        // Browsers may omit the scheme while editing. Never use this to verify a loaded site.
        public static bool IsTypedDestination(string value)
        {
            string s = (value ?? "").Trim();
            return Named(s, "https://chat.deepseek.com", "https://chat.deepseek.com/", "chat.deepseek.com", "chat.deepseek.com/");
        }
        public static string Normalize(string s) { return (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n"); }
        public static bool ExactText(string a, string b) { return Normalize(a) == Normalize(b); }
        public static bool SendAcknowledged(string currentText, bool generationStarted)
        { return generationStarted || (currentText != null && String.IsNullOrWhiteSpace(currentText)); }
        public static double RightWidth(double total, double dpi)
        { return Math.Min(total * 0.45, Math.Max(total * 0.18, 320 * dpi / 96)); }
        public static bool SideBySide(System.Windows.Rect a, System.Windows.Rect b)
        {
            return !a.IsEmpty && !b.IsEmpty && a.Width > 150 && b.Width > 150 && a.Height > 200 && b.Height > 200 &&
                Math.Abs(a.Top - b.Top) < 100 && Math.Abs(a.Right - b.Left) < 65 &&
                Math.Min(a.Bottom,b.Bottom) - Math.Max(a.Top,b.Top) > Math.Min(a.Height,b.Height) * 0.7;
        }
        public static bool Named(string name, params string[] choices)
        {
            return choices.Any(c => String.Equals((name ?? "").Trim(), c, StringComparison.OrdinalIgnoreCase));
        }
        public static string Fingerprint(string s)
        {
            using (var sha = SHA256.Create()) { return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s))); }
        }
    }

    static class Program
    {
        static readonly string DataDir = PortableStorage.Resolve(AppDomain.CurrentDomain.BaseDirectory);
        static string stage = "启动";
        static int done;
        static readonly Stopwatch lifetime = Stopwatch.StartNew();
        internal static bool DiagnosticsEnabled;
        static long edgeScanMilliseconds;
        static int edgeScanCount;
        internal static void RecordEdgeScan(long elapsed) { Interlocked.Add(ref edgeScanMilliseconds,elapsed); Interlocked.Increment(ref edgeScanCount); }
        static System.Threading.Timer watchdog;
        static readonly List<string> steps=new List<string>();
        internal static void Stage(string value) { stage = value; lock(steps) { if(steps.Count<40) steps.Add(lifetime.ElapsedMilliseconds+"ms:"+value); } Trace(value); }
        internal static void Trace(string value)
        {
            if(!DiagnosticsEnabled) return;
            try { Directory.CreateDirectory(DataDir); File.AppendAllText(Path.Combine(DataDir, "trace.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + value + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        internal static void Status(string code)
        {
            try
            {
                using(var process = Process.GetCurrentProcess())
                {
                    Directory.CreateDirectory(DataDir);
                    string metrics = "version=1.1.8 elapsed_ms=" + lifetime.ElapsedMilliseconds + " cpu_ms=" + Math.Round(process.TotalProcessorTime.TotalMilliseconds) +
                        " peak_working_set_mb=" + Math.Round(process.PeakWorkingSet64/1048576.0,1) +
                        " edge_scan_count="+edgeScanCount+" edge_scan_ms="+Interlocked.Read(ref edgeScanMilliseconds);
                    string timings; lock(steps) { timings=String.Join(" | ",steps.ToArray()); }
                    File.WriteAllText(Path.Combine(DataDir, "status.txt"), DateTime.Now.ToString("s") + " " + code + " " + stage + Environment.NewLine + metrics + Environment.NewLine + timings, Encoding.UTF8);
                    Maintenance.Record(DataDir, DateTime.Now.ToString("s") + " " + code + " " + metrics + " stage="+stage);
                }
            } catch { }
        }
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--self-test")) { SelfTest(); return; }
            // Timeout notification runs separately, so a stalled automation process can
            // terminate immediately rather than continue while a modal dialog is open.
            if(args.Contains("--timeout-notice"))
            {
                ShowError("TIMEOUT", "本次操作超过 10 秒，自动化进程已停止。\n请检查 DeepSeek 是否已经填入或发送文字，不要立即重复启动。", "等待浏览器响应");
                return;
            }
            DiagnosticsEnabled=args.Contains("--diagnostics");
            // Capture before creating any window. Never fall back to default browser or another window.
            IntPtr source = Native.GetForegroundWindow();
            bool created;
            string failureCode=null, failureMessage=null;
            using (var mutex = new Mutex(true, "Local\\DeepSeekBridge.SingleRun", out created))
            {
                if (!created) { ShowError("B00", "上一轮操作还在运行，请稍候。此次没有重复发送。", "重复启动"); return; }
                try
                {
                    PortableStorage.EnsureWritable(DataDir);
                    if(DiagnosticsEnabled) File.WriteAllText(Path.Combine(DataDir, "trace.txt"), "DeepSeekBridge 1.1.8" + Environment.NewLine, Encoding.UTF8);
                    uint pid;
                    Native.GetWindowThreadProcessId(source, out pid);
                    string browser = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                    if (!Rules.IsBrowser(browser)) throw new Stop("B01", "请先选中文字，保持 Edge 或 Chrome 在前台，再按鼠标键启动。\n不会转到默认浏览器。");
                    watchdog = new System.Threading.Timer(delegate
                    {
                        if (Interlocked.Exchange(ref done, 1) != 0) return;
                        Status("TIMEOUT");
                        try
                        {
                            using(var notice=Process.Start(new ProcessStartInfo {
                                FileName=Application.ExecutablePath, Arguments="--timeout-notice",
                                UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden })) { }
                        }
                        catch { }
                        Environment.Exit(2);
                    }, null, 10000, Timeout.Infinite);
                    Thread.Sleep(400);
                    var session = new Session(source, (int)pid, browser);
                    session.Guard();
                    string text = CopySelection(session.Guard);
                    string fingerprint = Rules.Fingerprint(text);
                    string recent = Path.Combine(DataDir, "recent.txt");
                    CheckRecent(recent, source, fingerprint);
                    session.Run(text, delegate
                    {
                        Directory.CreateDirectory(DataDir);
                        File.WriteAllText(recent, DateTime.UtcNow.Ticks + "|" + source.ToInt64() + "|" + fingerprint);
                    });
                    EndWatchdog();
                    Status("SENT_ACKNOWLEDGED");
                }
                catch (Stop e) { EndWatchdog(); Status(e.Code); failureCode=e.Code; failureMessage=e.Message; Environment.ExitCode=1; }
                catch (Exception e) { EndWatchdog(); failureCode="ERROR_" + e.GetType().Name; Status(failureCode); failureMessage="本次操作已停止。请检查 DeepSeek 中是否已经填入或发送文字，再决定是否重试。"; Environment.ExitCode=2; }
            }
            // Automation is finished and the mutex is released before displaying an error.
            if(failureCode!=null) { ShowError(failureCode,failureMessage,stage); return; }
            // Outside the send mutex: a maintenance reminder must not block another macro run.
            Maintenance.Check(DataDir);
        }
        static string CopySelection(Action guard)
        {
            Stage("复制当前选中文字");
            guard();
            uint before = Native.GetClipboardSequenceNumber();
            // A fresh sequence is required even when the selected text equals the old
            // clipboard. Never clear the clipboard or fall back to stale contents.
            Native.Chord(17, 67);
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 1500)
            {
                guard();
                uint observed = Native.GetClipboardSequenceNumber();
                if (observed != before)
                {
                    try
                    {
                        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                        {
                            string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                            uint after = Native.GetClipboardSequenceNumber();
                            guard();
                            if (Rules.FreshClipboard(before, observed, after) && !String.IsNullOrWhiteSpace(text))
                            {
                                if (text.Length > 30000) throw new Stop("C02", "一次最多发送 30,000 个字符，请缩短选区。");
                                if (text.IndexOf('\0') >= 0) throw new Stop("C03", "选中内容包含不支持的控制字符，请重新选择。");
                                return text;
                            }
                        }
                    }
                    catch (ExternalException) { /* Clipboard may be briefly locked by the browser. */ }
                }
                Thread.Sleep(30);
            }
            throw new Stop("C01", "未能复制当前选中文字，已停止，不会发送旧剪贴板内容。\n请在网页或 PDF 中选中文字后按鼠标键；扫描图片或禁止复制的页面可能无法复制。");
        }

        static void ShowError(string code,string message,string atStage)
        {
            MessageBox.Show(message + "\n\n代码：" + code + "\n阶段：" + atStage,
                "DeepSeekBridge · 操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,MessageBoxOptions.DefaultDesktopOnly);
        }
        static void EndWatchdog() { Interlocked.Exchange(ref done, 1); if (watchdog != null) watchdog.Dispose(); }
        static void CheckRecent(string path, IntPtr hwnd, string fingerprint)
        {
            if (!File.Exists(path)) return;
            string[] parts = File.ReadAllText(path).Split('|'); long ticks;
            if (parts.Length == 3 && Int64.TryParse(parts[0], out ticks) && parts[1] == hwnd.ToInt64().ToString() && parts[2] == fingerprint)
            {
                long elapsed = DateTime.UtcNow.Ticks - ticks;
                if (elapsed >= 0 && elapsed < TimeSpan.FromSeconds(15).Ticks)
                    throw new Stop("D01", "15 秒内已尝试发送相同文字。请先检查 DeepSeek，避免重复发送。");
            }
        }
        static void SelfTest()
        {
            var checks = new Dictionary<string, bool> {
                {"copy-reject-stale", !Rules.FreshClipboard(10,10,10)},
                {"copy-fresh-stable", Rules.FreshClipboard(10,11,11)},
                {"copy-reject-changing", !Rules.FreshClipboard(10,11,12)},
                {"copy-sequence-wrap", Rules.FreshClipboard(UInt32.MaxValue,0,0)},
                {"destination", Rules.IsDestination("https://chat.deepseek.com/a/chat/s")},
                {"reject-http", !Rules.IsDestination("http://chat.deepseek.com")},
                {"reject-suffix", !Rules.IsDestination("https://chat.deepseek.com.evil.test")},
                {"reject-userinfo", !Rules.IsDestination("https://chat.deepseek.com@evil.test")},
                {"reject-port", !Rules.IsDestination("https://chat.deepseek.com:444")},
                {"reject-title", !Rules.IsDestination("DeepSeek")},
                {"typed-hidden-scheme", Rules.IsTypedDestination("chat.deepseek.com")},
                {"typed-trailing-slash", Rules.IsTypedDestination("https://chat.deepseek.com/")},
                {"typed-reject-suffix", !Rules.IsTypedDestination("chat.deepseek.com.evil.test")},
                {"typed-reject-http", !Rules.IsTypedDestination("http://chat.deepseek.com")},
                {"typed-reject-path", !Rules.IsTypedDestination("https://chat.deepseek.com/other")},
                {"loaded-url-still-strict", !Rules.IsDestination("chat.deepseek.com")},
                {"edge", Rules.IsBrowser("msedge")}, {"chrome", Rules.IsBrowser("chrome")},
                {"reject-other-browser", !Rules.IsBrowser("firefox")},
                {"source-title-exact",Rules.SourceTitleMatches("Paper - Personal - Microsoft Edge","Paper")},
                {"source-title-reject-prefix",!Rules.SourceTitleMatches("Paper 2 - Microsoft Edge","Paper")},
                {"source-title-empty",!Rules.SourceTitleMatches("Microsoft Edge","")},
                {"source-title-unicode",Rules.SourceTitleMatches("Paper - Microsoft\u200b Edge","Paper")},
                {"line-endings", Rules.ExactText("a\r\nb", "a\nb")},
                {"keep-whitespace", !Rules.ExactText("a ", "a")},
                {"ack-empty-editor", Rules.SendAcknowledged("",false)},
                {"ack-placeholder-newline", Rules.SendAcknowledged("\r\n",false)},
                {"ack-generation-started", Rules.SendAcknowledged("old text",true)},
                {"reject-unchanged-draft", !Rules.SendAcknowledged("old text",false)},
                {"reject-unreadable-editor", !Rules.SendAcknowledged(null,false)},
                {"ack-busy-with-replaced-editor", Rules.SendAcknowledged(null,true)},
                {"fingerprint", Rules.Fingerprint("test") != Rules.Fingerprint("test2")},
                {"exact-button", !Rules.Named("发送到朋友", "发送", "Send")},
                {"input-struct-x64", Marshal.SizeOf(typeof(Native.INPUT)) == 40},
                {"width-large", Math.Abs(Rules.RightWidth(2560,96) - 460.8) < 0.01},
                {"width-minimum", Rules.RightWidth(1366,96) == 320},
                {"width-dpi", Rules.RightWidth(1920,144) == 480},
                {"width-small-window", Rules.RightWidth(600,96) == 270},
                {"adjacent-panes", Rules.SideBySide(new System.Windows.Rect(0,100,800,700),new System.Windows.Rect(808,100,400,700))},
                {"reject-nested-documents", !Rules.SideBySide(new System.Windows.Rect(0,100,1200,700),new System.Windows.Rect(800,100,400,700))},
                {"reject-stacked-documents", !Rules.SideBySide(new System.Windows.Rect(0,100,800,700),new System.Windows.Rect(808,850,400,700))}
            };
            Maintenance.Tests(checks);
            string storageTemp=Path.Combine(Path.GetTempPath(),"DeepSeekBridge-storage-"+Guid.NewGuid().ToString("N"));
            string storageLogs=PortableStorage.Resolve(storageTemp);
            try
            {
                PortableStorage.EnsureWritable(storageLogs);
                checks.Add("logs-beside-application",storageLogs==Path.Combine(storageTemp,"logs"));
                string oldWorkingDirectory=Directory.GetCurrentDirectory();
                try
                {
                    Directory.SetCurrentDirectory(storageTemp);
                    checks.Add("logs-independent-of-working-directory",PortableStorage.Resolve(AppDomain.CurrentDomain.BaseDirectory)==Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),"logs"));
                }
                finally { Directory.SetCurrentDirectory(oldWorkingDirectory); }
                checks.Add("logs-write-probe-cleaned",Directory.GetFiles(storageLogs).Length==0);
                Maintenance.Record(storageLogs,"test metadata");
                checks.Add("logs-report-in-portable-directory",File.Exists(Path.Combine(storageLogs,"review-report.txt")));
            }
            finally { string reportFile=Path.Combine(storageLogs,"review-report.txt"); if(File.Exists(reportFile)) File.Delete(reportFile); if(Directory.Exists(storageLogs)) Directory.Delete(storageLogs); if(Directory.Exists(storageTemp)) Directory.Delete(storageTemp); }
            string report = String.Join(Environment.NewLine, checks.Select(kv => (kv.Value ? "PASS " : "FAIL ") + kv.Key).ToArray());
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-results.txt"), report, Encoding.UTF8);
            Environment.ExitCode = checks.Values.All(x => x) ? 0 : 1;
        }
    }

    sealed class Session
    {
        readonly IntPtr hwnd;
        readonly int pid;
        readonly string browser;
        bool createdSplitThisRun;
        sealed class EdgeNode
        {
            internal AutomationElement Element;
            internal ControlType Type;
            internal string Name;
        }
        List<EdgeNode> edgeNodes;
        Stopwatch edgeAge;
        static readonly ConditionalWeakTable<AutomationElement,EdgeNode> edgeNames = new ConditionalWeakTable<AutomationElement,EdgeNode>();
        AutomationElement root;
        public Session(IntPtr handle, int process, string name) { hwnd = handle; pid = process; browser = name; root = AutomationElement.FromHandle(handle); }
        public void Guard()
        {
            uint currentPid; Native.GetWindowThreadProcessId(hwnd, out currentPid);
            if (Native.GetForegroundWindow() != hwnd || currentPid != pid || !Native.IsWindow(hwnd))
                throw new Stop("B02", "当前窗口已改变，已停止操作。请回到原来的浏览器再启动。");
        }
        static bool Visible(AutomationElement e) { try { return !e.Current.IsOffscreen && e.Current.BoundingRectangle.Width > 0; } catch { return false; } }
        static string Name(AutomationElement e) { try { EdgeNode node; return edgeNames.TryGetValue(e,out node) ? node.Name : (e.Current.Name ?? ""); } catch { return ""; } }
        static string Value(AutomationElement e)
        {
            object p;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out p)) return ((ValuePattern)p).Current.Value;
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out p)) return ((TextPattern)p).DocumentRange.GetText(31000);
            return null;
        }
        static bool Under(AutomationElement e, AutomationElement ancestor)
        {
            try {
            for (int i = 0; e != null && i < 45; i++, e = TreeWalker.ControlViewWalker.GetParent(e))
                if (Automation.Compare(e, ancestor)) return true;
            return false;
            } catch(ElementNotAvailableException) { return false; }
        }
        static List<AutomationElement> Find(AutomationElement parent, ControlType type)
        {
            return parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>().Where(Visible).ToList();
        }
        void InvalidateEdge() { edgeNodes=null; edgeAge=null; }
        List<EdgeNode> EdgeSnapshot()
        {
            if(edgeNodes!=null && edgeAge!=null && edgeAge.ElapsedMilliseconds<100) return edgeNodes;
            Guard();
            var output=new List<EdgeNode>();
            var queue=new Queue<AutomationElement>(); queue.Enqueue(root);
            var scan=Stopwatch.StartNew(); int visited=0;
            {
                var cache=new CacheRequest();
                cache.TreeScope=TreeScope.Element;
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                while(queue.Count>0)
                {
                    if(++visited>2000 || scan.ElapsedMilliseconds>1800)
                    { Program.RecordEdgeScan(scan.ElapsedMilliseconds); throw new Stop("U12","Edge 的界面控件响应过慢，本次已停止。请稍后重试；无需关闭其他标签页。"); }
                    var parent=queue.Dequeue();
                    try
                    {
                        AutomationElementCollection children;
                        // One provider request per parent, including all requested properties.
                        // Do not cross into documents, hidden containers or tab-item subtrees.
                        using(cache.Activate()) children=parent.FindAll(TreeScope.Children,Condition.TrueCondition);
                        foreach(AutomationElement child in children)
                        {
                            var info=child.Cached;
                            if(info.IsOffscreen) continue;
                            var node=new EdgeNode{Element=child,Type=info.ControlType,Name=info.Name ?? ""};
                            output.Add(node); edgeNames.Remove(child); edgeNames.Add(child,node);
                            // Tab strips are queried separately only when needed. Their full
                            // tab/group/close-button trees are irrelevant to document lookup.
                            bool tabStrip=node.Type==ControlType.Tab || info.ClassName=="TabStrip" || info.ClassName=="VerticalTabStrip";
                            if(node.Type!=ControlType.Document && node.Type!=ControlType.TabItem && !tabStrip) queue.Enqueue(child);
                        }
                    }
                    catch(ElementNotAvailableException) { }
                }
            }
            edgeNodes=output; edgeAge=Stopwatch.StartNew();
            Program.RecordEdgeScan(scan.ElapsedMilliseconds);
            return output;
        }
        List<AutomationElement> Documents()
        {
            return browser=="msedge" ? EdgeSnapshot().Where(n=>n.Type==ControlType.Document).Select(n=>n.Element).Where(Visible).ToList() : Find(root, ControlType.Document);
        }
        static string DocumentUrl(AutomationElement doc)
        {
            object p;
            if (doc.TryGetCurrentPattern(ValuePattern.Pattern, out p)) return ((ValuePattern)p).Current.Value;
            return "";
        }
        List<AutomationElement> NativeControls(ControlType type)
        {
            if(browser=="msedge" && type==ControlType.TabItem) return EdgeTabs(false);
            if(browser=="msedge") return EdgeSnapshot().Where(n=>n.Type==type).Select(n=>n.Element).Where(Visible).ToList();
            // Prune web documents rather than traversing long papers/chat histories.
            var result = new List<AutomationElement>();
            var pending = new Queue<AutomationElement>(); pending.Enqueue(root);
            int visited = 0;
            while(pending.Count > 0 && visited++ < 2200)
            {
                var current = pending.Dequeue();
                try
                {
                var kind = current.Current.ControlType;
                if(kind == ControlType.Document) continue;
                if(kind == type && Visible(current)) result.Add(current);
                for(var child=TreeWalker.ControlViewWalker.GetFirstChild(current); child!=null; child=TreeWalker.ControlViewWalker.GetNextSibling(child))
                    pending.Enqueue(child);
                }
                catch(ElementNotAvailableException) { /* Discard a removed native node, not the whole traversal. */ }
            }
            return result;
        }
        void TraceControls(string reason)
        {
            if(!Program.DiagnosticsEnabled) return;
            Program.Trace("CONTROLS " + reason);
            foreach(var type in new ControlType[]{ControlType.Button,ControlType.MenuItem})
                foreach(var e in NativeControls(type).Take(35))
                    Program.Trace(type.ProgrammaticName + " " + Name(e).Replace("\r"," ").Replace("\n"," "));
            // No values, URLs, document titles, clipboard text, or chat text in diagnostics.
            foreach(var e in Find(root,ControlType.Edit).Where(x => String.IsNullOrEmpty(Value(x))).Take(10))
                Program.Trace("EmptyEdit label=" + Name(e).Replace("\r"," ").Replace("\n"," ") + " bounds=" + e.Current.BoundingRectangle);
        }
        static bool IsInternalPicker(AutomationElement d)
        {
            Uri uri;
            return Uri.TryCreate(DocumentUrl(d),UriKind.Absolute,out uri) &&
                (uri.Scheme=="edge" || uri.Scheme=="chrome") &&
                Rules.Named(uri.Host,"newtab","new-tab-page","split-screen","split-view","splitview");
        }
        void Invoke(AutomationElement e)
        {
            Guard();
            object pattern;
            if (!e.Current.IsEnabled || !e.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                throw new Stop("U01", "该浏览器控件没有开放可调用操作。请手动完成分屏后重试。");
            ((InvokePattern)pattern).Invoke();
            InvalidateEdge();
        }
        static List<AutomationElement> Editors(AutomationElement doc)
        {
            return Find(doc, ControlType.Edit).Where(e => {
                try {
                if (!e.Current.IsEnabled || e.Current.IsPassword) return false;
                object p;
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out p) && ((ValuePattern)p).Current.IsReadOnly) return false;
                string name = Name(e);
                return !name.Contains("搜索") && name.IndexOf("search", StringComparison.OrdinalIgnoreCase) < 0;
                } catch(ElementNotAvailableException) { return false; }
            }).ToList();
        }
        AutomationElement Destination()
        {
            for(int retry=0;retry<2;retry++)
            {
                Guard();
                try { return DestinationSnapshot(); }
                catch(ElementNotAvailableException) { Thread.Sleep(80); }
            }
            throw new Stop("U11","网页正在切换，暂时无法稳定识别 DeepSeek。没有自动重试发送，请等页面稳定后再调用。");
        }
        AutomationElement DestinationSnapshot()
        {
            var docs = Documents();
            var exact = docs.Where(d => Rules.IsDestination(DocumentUrl(d))).ToList();
            // Nested documents: prefer the deepest exact-origin document containing the editor.
            exact = exact.Where(d => Editors(d).Count > 0 && !exact.Any(other => !Automation.Compare(d, other) && Under(other, d) && Editors(other).Count > 0)).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) throw new Stop("U02", "当前窗口有多个 DeepSeek 输入页面，无法唯一选择。请只保留一个可见的 DeepSeek 分屏。");
            // Focus-only fallback: title is a candidate, never proof of destination.
            var candidates = docs.Where(d => Name(d).IndexOf("DeepSeek", StringComparison.OrdinalIgnoreCase) >= 0 && Editors(d).Count == 1).ToList();
            candidates = candidates.Where(d => !candidates.Any(other => !Automation.Compare(d,other) && Under(other,d))).ToList();
            if (candidates.Count == 1)
            {
                var editor = Editors(candidates[0])[0];
                Guard(); editor.SetFocus(); Thread.Sleep(160); Guard();
                // In Chrome, SetFocus alone may not activate the inactive split pane.
                // Click the actual empty editor rectangle if its native focus did not follow.
                if (!Automation.Compare(AutomationElement.FocusedElement,editor)) ClickControl(editor, false);
                var address = NativeControls(ControlType.Edit).Where(e => IsAddressName(Name(e))).ToList();
                var pane = candidates[0].Current.BoundingRectangle;
                var local = address.Where(e => {
                    var r = e.Current.BoundingRectangle;
                    return r.Left + r.Width / 2 >= pane.Left && r.Left + r.Width / 2 <= pane.Right;
                }).ToList();
                if (address.Count == 1 && Rules.IsDestination(Value(address[0]))) return candidates[0];
                if (local.Count == 1 && Rules.IsDestination(Value(local[0]))) return candidates[0];
                // Ctrl+L asks the browser to expose the *active pane's* omnibox.
                // It is read-only here: never write a guessed URL to an existing page.
                ClickControl(editor, false); Guard(); Native.Chord(17,76); Thread.Sleep(120); Guard();
                var focused = AutomationElement.FocusedElement;
                bool valid = Under(focused,root) && !docs.Any(d => Under(focused,d)) && Rules.IsDestination(Value(focused));
                Native.Escape(); Guard(); editor.SetFocus();
                if (valid) return candidates[0];
            }
            return null;
        }
        static bool IsAddressName(string name)
        {
            return Rules.Named(name, "地址和搜索栏", "地址栏", "地址和搜索", "地址和搜索框", "Address and search bar", "Address bar", "搜索或输入网址", "搜索或输入 Web 地址", "Search or enter web address", "Search or enter URL", "Search tabs or enter a URL", "搜索标签页或输入网址");
        }
        void ClickControl(AutomationElement element, bool right)
        {
            Guard(); var r = element.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 3 || r.Height < 3 || !Under(element,root)) throw new Stop("U10", "控件位置失效，已停止。");
            Native.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2), hwnd, right);
            InvalidateEdge();
            Thread.Sleep(120); Guard();
        }
        List<AutomationElement> MenuItems()
        {
            var result = NativeControls(ControlType.MenuItem);
            // Chromium popup menus may be sibling HWNDs owned by the same browser window.
            foreach (var top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty,pid)).Cast<AutomationElement>())
            {
                int handle = top.Current.NativeWindowHandle;
                if (handle != 0 && new IntPtr(handle) != hwnd && Native.GetAncestor(new IntPtr(handle),3) == hwnd)
                    result.AddRange(Find(top,ControlType.MenuItem));
            }
            return result;
        }
        void MenuAction(AutomationElement item)
        {
            Guard(); object p;
            if (item.TryGetCurrentPattern(InvokePattern.Pattern,out p)) ((InvokePattern)p).Invoke();
            else if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out p)) ((ExpandCollapsePattern)p).Expand();
            else throw new Stop("S05", "浏览器菜单没有可调用操作，已停止。");
            InvalidateEdge();
            Thread.Sleep(180); Guard();
        }
        List<AutomationElement> Pair()
        {
            var docs = Documents();
            var pairs = new List<List<AutomationElement>>();
            foreach (var a in docs) foreach (var b in docs)
                if (Rules.SideBySide(a.Current.BoundingRectangle,b.Current.BoundingRectangle)) pairs.Add(new List<AutomationElement>{a,b});
            return pairs.OrderByDescending(p => p[0].Current.BoundingRectangle.Width * p[0].Current.BoundingRectangle.Height + p[1].Current.BoundingRectangle.Width * p[1].Current.BoundingRectangle.Height).FirstOrDefault();
        }
        void NavigateFreshAddress()
        {
            Guard(); Native.Chord(17,76); Thread.Sleep(100); Guard();
            var address = AutomationElement.FocusedElement;
            if (!Under(address,root) || Documents().Any(d => Under(address,d)) || !IsAddressName(Name(address)))
                throw new Stop("S06", "新标签页的原生地址栏未能确认，已停止导航。");
            FillFreshAddress(address);
        }
        bool WaitForTypedAddress(AutomationElement address)
        {
            // Unicode input is queued. UIA frequently reports the previous value for a few frames.
            for(int i=0;i<24;i++)
            {
                Guard();
                try
                {
                    if(Rules.IsTypedDestination(Value(address)))
                    {
                        var focused=AutomationElement.FocusedElement;
                        if(Automation.Compare(focused,address) || Under(focused,address)) return true;
                    }
                }
                catch(ElementNotAvailableException) { return false; }
                Thread.Sleep(75);
            }
            return false;
        }
        void FillFreshAddress(AutomationElement address)
        {
            Guard(); address.SetFocus(); Thread.Sleep(80); Guard();
            if(!Automation.Compare(AutomationElement.FocusedElement,address))
                throw new Stop("S02", "网址输入框焦点未确认，没有输入或导航。");
            Native.Chord(17,65);
            Native.TypeUnicode("https://chat.deepseek.com",Guard);
            bool confirmed=WaitForTypedAddress(address);
            if(!confirmed)
            {
                // Some native controls do not process KEYEVENTF_UNICODE reliably. A supported
                // ValuePattern setter is a bounded fallback, only on the same fresh address field.
                Guard(); object pattern;
                if(address.TryGetCurrentPattern(ValuePattern.Pattern,out pattern) && !((ValuePattern)pattern).Current.IsReadOnly)
                {
                    address.SetFocus(); Guard();
                    ((ValuePattern)pattern).SetValue("https://chat.deepseek.com");
                    confirmed=WaitForTypedAddress(address);
                }
            }
            if(!confirmed)
            {
                string observed=Value(address);
                Program.Trace("ADDRESS confirmation-failed; readable="+(observed!=null)+"; length="+(observed==null ? -1:observed.Length)+"; focused="+Automation.Compare(AutomationElement.FocusedElement,address));
                throw new Stop("S03", "网址输入后仍未能核对内容与焦点，未按回车。请反馈此代码。");
            }
            Guard(); Program.Trace("ADDRESS typed-url-confirmed"); Native.Enter(); InvalidateEdge();
        }
        void OpenChromeSplit()
        {
            Program.Stage("Chrome：创建 DeepSeek 标签页并与来源配对");
            var tabs = NativeControls(ControlType.TabItem).Where(e => {
                object p; return e.TryGetCurrentPattern(SelectionItemPattern.Pattern,out p) && ((SelectionItemPattern)p).Current.IsSelected;
            }).ToList();
            if (tabs.Count != 1) throw new Stop("S07", "Chrome 当前标签页不唯一，无法配对分屏。");
            var sourceTab = tabs[0];
            Guard(); Native.Chord(17,84); Thread.Sleep(250); NavigateFreshAddress(); Thread.Sleep(400);
            // Source tab is now inactive. Official Chrome menu pairs it with current DeepSeek tab.
            ClickControl(sourceTab,true);
            var items = MenuItems().Where(e => Rules.Named(Name(e), "使用当前标签页创建新的拆分视图", "使用当前标签页创建新拆分视图", "与当前标签页一起在拆分视图中打开", "New split view with current tab", "Split view with current tab")).ToList();
            if (items.Count != 1)
            {
                foreach(var e in MenuItems().Take(35)) Program.Trace("TAB-MENU " + Name(e));
                Native.Escape();
                throw new Stop("S08", "DeepSeek 已在同一窗口的新标签页打开，但未识别到 Chrome 配对分屏菜单。原阅读页仍保留。请把这两页手动分屏后重试。");
            }
            MenuAction(items[0]);
            createdSplitThisRun = true;
        }
        List<AutomationElement> EdgeTabs(bool selectedOnly)
        {
            Guard();
            Condition condition=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.TabItem);
            if(selectedOnly) condition=new AndCondition(condition,new PropertyCondition(SelectionItemPattern.IsSelectedProperty,true));
            var cache=new CacheRequest(); cache.TreeScope=TreeScope.Element;
            cache.Add(AutomationElement.NameProperty); cache.Add(AutomationElement.BoundingRectangleProperty);
            var result=new List<AutomationElement>();
            var docs=Documents();
            AutomationElementCollection found;
            using(cache.Activate()) found=root.FindAll(TreeScope.Descendants,condition);
            foreach(AutomationElement tab in found)
            {
                try
                {
                    var r=tab.Cached.BoundingRectangle;
                    // A web page may itself contain ARIA tabs. Check ancestry only for
                    // candidates geometrically inside a visible document, not every native tab.
                    if(docs.Any(d=>d.Current.BoundingRectangle.Contains(r) && Under(tab,d))) continue;
                    var node=new EdgeNode{Element=tab,Type=ControlType.TabItem,Name=tab.Cached.Name ?? ""};
                    edgeNames.Remove(tab); edgeNames.Add(tab,node); result.Add(tab);
                }
                catch(ElementNotAvailableException) { }
            }
            return result;
        }
        void PrepareEdgeTab()
        {
            Program.Stage("Edge：准备可加入分屏的标签页");
            var selected=EdgeTabs(true);
            if(selected.Count!=1)
            {
                // Multi-selected tabs can all expose IsSelected. Window title is used
                // only to pick a unique source tab, never as proof of DeepSeek origin.
                string title=root.Current.Name ?? "";
                var candidates=selected.Count>0 ? selected : EdgeTabs(false);
                var matches=candidates.Where(e=>Rules.SourceTitleMatches(title,Name(e))).ToList();
                if(matches.Count==1) selected=matches;
            }
            Program.Stage("Edge：源标签页候选数="+selected.Count);
            if(selected.Count!=1) throw new Stop("S10","Edge 返回的原阅读标签页不唯一，尚未新建空白页。请先单击当前阅读标签页，再调用；这不会影响已开分屏的发送。");
            var source=selected[0];
            string sourceName=Name(source);
            // Always prepare one blank candidate for this first split. Do not enumerate
            // every open tab looking for a title that only looks reusable.
            // The Edge picker can lack a navigable field when there is no other tab.
            // Prepare a blank tab first, then return to the exact source tab before splitting.
            Program.Stage("Edge：新建空白标签页");
            Guard(); Native.Chord(17,84); InvalidateEdge(); Thread.Sleep(180); Guard();
            object selection;
            bool restored=false;
            Program.Stage("Edge：返回原阅读标签页");
            for(int retry=0;retry<2 && !restored;retry++)
            {
                try
                {
                    if(source.TryGetCurrentPattern(SelectionItemPattern.Pattern,out selection)) ((SelectionItemPattern)selection).Select();
                    else ClickControl(source,false);
                    InvalidateEdge();
                    restored=true;
                }
                catch(ElementNotAvailableException)
                {
                    var matches=NativeControls(ControlType.TabItem).Where(e=>Name(e)==sourceName).ToList();
                    if(matches.Count!=1) throw new Stop("S11","标签页更新后无法唯一找回原阅读页，已停止。请回到原阅读页再调用。");
                    source=matches[0];
                }
            }
            Thread.Sleep(120); Guard();
            if(!source.TryGetCurrentPattern(SelectionItemPattern.Pattern,out selection) || !((SelectionItemPattern)selection).Current.IsSelected)
                throw new Stop("S11","空白标签页已准备，但未确认回到原阅读页。未继续分屏，请检查标签页。");
        }
        List<AutomationElement> PickerControls(ControlType type,List<AutomationElement> sourceDocuments)
        {
            if(browser!="msedge") return Find(root,type);
            var result=NativeControls(type);
            var windowBounds=root.Current.BoundingRectangle;
            foreach(var document in Documents())
            {
                try
                {
                    var bounds=document.Current.BoundingRectangle;
                    if(bounds.Left<windowBounds.Left+windowBounds.Width*0.40) continue;
                    if(sourceDocuments.Any(source=>Under(document,source))) continue;
                    result.AddRange(Find(document,type));
                }
                catch(ElementNotAvailableException) { InvalidateEdge(); }
            }
            return result;
        }
        void TryOpenSplit()
        {
            Program.Stage("查找原生分屏入口");
            // Do not toggle an existing split or act on similarly named webpage controls.
            var originalDocs = Documents().Where(d => !IsInternalPicker(d)).ToList();
            bool alreadySplit = Pair() != null;
            if (!alreadySplit && browser == "chrome") { OpenChromeSplit(); return; }
            if (!alreadySplit)
            {
                if(browser=="msedge") PrepareEdgeTab();
                // Switching away and back may destroy the original UIA Document objects.
                // Exclude source-page fields using a fresh snapshot, not the pre-switch objects.
                originalDocs=Documents().Where(d=>!IsInternalPicker(d)).ToList();
                var buttons = NativeControls(ControlType.Button).Where(e => Rules.Named(Name(e), "拆分屏幕", "分屏", "分屏显示", "Split screen", "Split view", "Open split view")).ToList();
                if (buttons.Count != 1) { TraceControls("no-split-button"); throw new Stop("S01", "没有识别到可安全调用的原生分屏按钮。\n请手动在当前窗口右侧打开 https://chat.deepseek.com，登录后再按鼠标键。\n原文仍在剪贴板中。"); }
                Invoke(buttons[0]); createdSplitThisRun = true; Thread.Sleep(350);
            }
            Program.Stage("寻找右侧网址入口");
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Guard();
                if (Destination() != null) return;
                var bounds = root.Current.BoundingRectangle;
                // Edge's split picker can be an internal Document, not browser toolbar chrome.
                // Only target an empty named URL field in the right pane, never a source-page edit.
                var edits = PickerControls(ControlType.Edit,originalDocs).Where(e => IsAddressName(Name(e)) &&
                    e.Current.BoundingRectangle.Left > bounds.Left + bounds.Width * 0.40 &&
                    !originalDocs.Any(d => Under(e,d)) &&
                    String.IsNullOrWhiteSpace(Value(e))).ToList();
                if (edits.Count == 1)
                {
                    Guard(); edits[0].SetFocus(); Thread.Sleep(100); Guard();
                    if (!Automation.Compare(AutomationElement.FocusedElement, edits[0])) throw new Stop("S02", "右侧地址输入框焦点不明确，请手动打开 DeepSeek。");
                    FillFreshAddress(edits[0]);
                    return;
                }
                // Some Edge builds provide an "open new tab" button instead of a URL field.
                var fresh = PickerControls(ControlType.Button,originalDocs).Concat(PickerControls(ControlType.ListItem,originalDocs)).Concat(PickerControls(ControlType.Hyperlink,originalDocs)).Where(e => Rules.Named(Name(e), "新建标签页", "新标签页", "New tab", "Open a new tab", "打开新标签页") &&
                    e.Current.BoundingRectangle.Left > bounds.Left + bounds.Width * 0.40 &&
                    e.Current.BoundingRectangle.Top > bounds.Top + 110 && !originalDocs.Any(d => Under(e,d))).ToList();
                if (fresh.Count == 1)
                {
                    object action;
                    Guard();
                    if(fresh[0].TryGetCurrentPattern(InvokePattern.Pattern,out action)) ((InvokePattern)action).Invoke();
                    else if(fresh[0].TryGetCurrentPattern(SelectionItemPattern.Pattern,out action)) ((SelectionItemPattern)action).Select();
                    else ClickControl(fresh[0],false);
                    InvalidateEdge();
                    Thread.Sleep(200);
                    // Confirm right-side new-tab document before Ctrl+L, so source URL is untouched.
                    var right = Documents().Where(d => d.Current.BoundingRectangle.Left > bounds.Left + bounds.Width * 0.40 &&
                        Rules.Named(Name(d), "新建标签页", "新标签页", "New tab", "New Tab")).ToList();
                    if (right.Count == 1) { Guard(); right[0].SetFocus(); NavigateFreshAddress(); return; }
                    throw new Stop("S09", "已打开右侧新标签页，但无法确认它已激活。请手动输入 DeepSeek 网址后重试。");
                }
                Thread.Sleep(200);
            }
            TraceControls("no-right-picker");
            throw new Stop("S04", "已检查分屏，但未找到空白的右侧网址输入框。\n请在右侧手动打开 https://chat.deepseek.com 后再启动。");
        }
        static bool Busy(AutomationElement doc)
        {
            return Find(doc, ControlType.Button).Any(e => Rules.Named(Name(e), "停止生成", "停止回答", "停止响应", "Stop generating", "Stop response", "Stop"));
        }
        bool CheckSendAcknowledgement(ref AutomationElement verifiedDocument, System.Windows.Rect pane, bool refreshWindow)
        {
            // Read-only after sending. Never refocus the page or press Send a second time.
            var candidates=refreshWindow ? Documents() : new List<AutomationElement>{verifiedDocument};
            foreach(var current in candidates)
            {
                try
                {
                    var r=current.Current.BoundingRectangle;
                    if(r.IsEmpty || Math.Abs(r.Left-pane.Left)>40 || Math.Abs(r.Right-pane.Right)>40) continue;
                    if(!Rules.IsDestination(DocumentUrl(current)) && !Automation.Compare(current,verifiedDocument)) continue;
                    var editors=Editors(current);
                    string currentValue=editors.Count==1 ? Value(editors[0]) : null;
                    // Most calls finish here. Avoid enumerating hundreds of chat buttons
                    // or the browser's entire document tree after the editor has cleared.
                    bool generating=!Rules.SendAcknowledged(currentValue,false) && Busy(current);
                    if(Rules.SendAcknowledged(currentValue,generating))
                    {
                        Program.Stage(generating ? "发送后已开始生成回答" : "发送后当前输入框已清空");
                        return true;
                    }
                    verifiedDocument=current;
                }
                catch(ElementNotAvailableException) { /* Page replaced this node; retry a fresh snapshot. */ }
            }
            return false;
        }
        AutomationElement Arrange(AutomationElement doc)
        {
            Program.Stage("调整 DeepSeek 分屏位置与宽度");
            var pair = Pair();
            if (pair == null) { Program.Trace("LAYOUT no-two-panes"); return doc; }
            var d = doc.Current.BoundingRectangle;
            var left = pair[0].Current.BoundingRectangle; var right = pair[1].Current.BoundingRectangle;
            if (d.Left < (left.Left + right.Right) / 2 && d.Right <= left.Right + 20)
            {
                // Preserve working left-side send when swap controls are unavailable.
                var menus = NativeControls(ControlType.Button).Where(e => Rules.Named(Name(e), "拆分视图", "管理拆分视图", "排列拆分视图", "Split view", "Arrange split view", "拆分屏幕选项", "分屏选项", "Split screen options")).ToList();
                if (menus.Count == 1)
                {
                    Invoke(menus[0]); Thread.Sleep(120);
                    var swap = MenuItems().Where(e => Rules.Named(Name(e), "交换位置", "交换视图", "切换左右屏幕", "交换左右屏幕", "Switch sides", "Swap sides", "Swap views")).ToList();
                    if (swap.Count == 1)
                    {
                        MenuAction(swap[0]); Thread.Sleep(180);
                        var changed = Destination(); if (changed != null) doc = changed;
                        pair = Pair();
                    }
                    else { Native.Escape(); Program.Trace("LAYOUT swap-menu-unavailable"); }
                }
                else Program.Trace("LAYOUT swap-button-unavailable");
            }
            if (pair == null) return doc;
            left = pair[0].Current.BoundingRectangle; right = pair[1].Current.BoundingRectangle;
            d = doc.Current.BoundingRectangle;
            // Never shrink the reading pane when DeepSeek is still on the left.
            if (d.Left < right.Left - 25) { Program.Trace("LAYOUT kept-left-destination"); return doc; }
            double total = right.Right - left.Left;
            double wanted = Rules.RightWidth(total, Native.Dpi(hwnd));
            if (Math.Abs(right.Width - wanted) < 28) { Program.Trace("LAYOUT already-sized"); return doc; }
            double seam = (left.Right + right.Left) / 2;
            var separators = NativeControls(ControlType.Separator).Concat(NativeControls(ControlType.Thumb)).Concat(NativeControls(ControlType.Slider)).Where(e => {
                var r = e.Current.BoundingRectangle;
                return Math.Abs(r.Left + r.Width / 2 - seam) < 30 && r.Height > Math.Min(left.Height,right.Height) * 0.3 && r.Width < 65;
            }).ToList();
            System.Windows.Rect handle;
            if (separators.Count == 1) handle = separators[0].Current.BoundingRectangle;
            else
            {
                // Geometry comes from two actual adjacent Document bounds, not a fixed screen coordinate.
                // Require the seam hit-test to belong to native chrome outside all web documents.
                var point = new System.Windows.Point(seam, Math.Max(left.Top,right.Top) + Math.Min(left.Height,right.Height) * 0.4);
                var hit = AutomationElement.FromPoint(point);
                if (!Under(hit,root) || Documents().Any(x => Under(hit,x))) { Program.Trace("LAYOUT native-divider-not-exposed"); return doc; }
                var r = hit.Current.BoundingRectangle;
                if (r.Width > 65 || r.Height < 150) { Program.Trace("LAYOUT divider-hit-test-ambiguous"); return doc; }
                handle = r;
            }
            Guard();
            Native.Drag((int)(handle.Left + handle.Width/2), (int)(handle.Top + handle.Height * 0.4), (int)(right.Right-wanted), hwnd, Guard);
            Thread.Sleep(220); Guard();
            var after = Pair();
            if (after != null) Program.Trace("LAYOUT right-percent=" + Math.Round(after[1].Current.BoundingRectangle.Width/(after[1].Current.BoundingRectangle.Right-after[0].Current.BoundingRectangle.Left)*100,1));
            var refreshed = Destination();
            if (refreshed == null) throw new Stop("L01", "分屏调整后无法重新确认 DeepSeek，未粘贴或发送。");
            return refreshed;
        }
        public void Run(string text, Action markAttempt)
        {
            Program.Stage("确认 DeepSeek 网页");
            AutomationElement doc = Destination();
            if (doc == null)
            {
                TryOpenSplit();
                for (int i = 0; i < 25 && doc == null; i++) { Guard(); doc = Destination(); if (doc == null) Thread.Sleep(250); }
            }
            if (doc == null) throw new Stop("U03", "无法确认 DeepSeek 网址与输入框。\n请确认右侧已登录、页面加载完成。若已经满足，当前浏览器的可访问性控件需要进一步适配。");
            Guard();
            if(createdSplitThisRun) doc = Arrange(doc);
            else Program.Trace("LAYOUT existing-split-preserved");
            Program.Stage("检查草稿和生成状态");
            if (Busy(doc)) throw new Stop("D02", "DeepSeek 正在回答，请等回答结束后再启动。没有填入或发送本次文字。");
            var editors = Editors(doc);
            if (editors.Count != 1) throw new Stop("U04", "DeepSeek 输入框不唯一，已停止。");
            var editor = editors[0];
            string before = Value(editor);
            if (before == null) throw new Stop("U05", "无法读取输入框内容，不能确认是否已有草稿。已停止。");
            if (before.Length != 0) throw new Stop("D03", "DeepSeek 输入框已有草稿，已保留原样。请先处理草稿，再启动。");
            Program.Stage("填入原文");
            Guard(); editor.SetFocus(); Thread.Sleep(120); Guard();
            if (!Automation.Compare(AutomationElement.FocusedElement, editor)) throw new Stop("U06", "输入框焦点核对失败，没有发送。");
            // Paste with the browser's native paste path (including multiline text).
            // Do not send newline keystrokes or rewrite/restore the user's clipboard.
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText) || Clipboard.GetText(TextDataFormat.UnicodeText) != text)
                throw new Stop("C04", "剪贴板在操作期间发生变化，没有粘贴或发送。请重新复制后再启动。");
            Guard(); Native.PastePlain();
            Program.Stage("核对输入原文");
            bool matched = false;
            for (int i = 0; i < 12; i++) { Guard(); if (Rules.ExactText(Value(editor), text)) { matched = true; break; } Thread.Sleep(100); }
            if (!matched) throw new Stop("D04", "输入框内容与复制原文未能核对一致，没有点击发送。请检查草稿。");
            if (Busy(doc)) throw new Stop("D05", "页面开始生成回答，已保留本次草稿，没有发送。");
            Program.Stage("查找发送按钮");
            var sends = Find(doc, ControlType.Button).Where(e => Rules.Named(Name(e), "发送", "发送消息", "Send", "Send message") && e.Current.IsEnabled).ToList();
            if (sends.Count > 1) throw new Stop("U08", "原文已填入，但存在多个发送按钮，无法唯一确认。请手动发送。");
            // Revalidate document, focus and text immediately before the single irreversible action.
            var verified = Destination();
            if (verified == null || !Automation.Compare(verified, doc) || !Rules.ExactText(Value(editor), text))
                throw new Stop("D06", "发送前页面或草稿发生变化，已停止。");
            var destinationBounds=doc.Current.BoundingRectangle;
            Guard();
            if (sends.Count == 1)
            {
                markAttempt(); Program.Stage("已触发一次发送"); Invoke(sends[0]);
            }
            else
            {
                // Some DeepSeek versions expose an unnamed icon. Use the user-requested
                // Enter action only after revalidating the exact input and its focus.
                editor.SetFocus(); Thread.Sleep(80); Guard();
                if (!Automation.Compare(AutomationElement.FocusedElement, editor) || !Rules.ExactText(Value(editor), text))
                    throw new Stop("U09", "回车前输入框核对失败，已保留草稿。");
                markAttempt(); Program.Stage("已在输入框触发一次回车"); Native.Enter();
            }
            for (int i = 0; i < 25; i++)
            {
                Guard();
                try { if(CheckSendAcknowledgement(ref doc,destinationBounds,i==4 || i==12)) return; }
                catch(ElementNotAvailableException) { /* Refresh on next poll without repeating input. */ }
                Thread.Sleep(100);
            }
            throw new Stop("D07", "已触发一次发送，但无法确认页面是否接收。\n请检查对话，不要立即重复启动。程序不会自动再次发送。");
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x,y; public POINT(int a,int b) { x=a;y=b; } }
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, INPUT[] input, int size);
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public UNION data; }
        [StructLayout(LayoutKind.Explicit)] internal struct UNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
        static INPUT Key(ushort vk, ushort scan, uint flags) { return new INPUT { type = 1, data = new UNION { ki = new KEYBDINPUT { vk = vk, scan = scan, flags = flags } } }; }
        static INPUT Mouse(uint flags) { return new INPUT { type=0,data=new UNION{mi=new MOUSEINPUT{flags=flags}}}; }
        internal static double Dpi(IntPtr hwnd) { try { uint dpi=GetDpiForWindow(hwnd); return dpi==0 ? 96 : dpi; } catch(EntryPointNotFoundException) { return 96; } }
        static void CheckPoint(int x,int y,IntPtr hwnd)
        {
            if (GetAncestor(WindowFromPoint(new POINT(x,y)),2) != hwnd)
                throw new Stop("I03", "目标控件被其他窗口遮挡，已停止点击。");
        }
        internal static void Click(int x,int y,IntPtr hwnd,bool right)
        {
            CheckModifiers(); CheckPoint(x,y,hwnd);
            POINT old; GetCursorPos(out old);
            SetCursorPos(x,y);
            Submit(new INPUT[]{Mouse(right ? 8u : 2u),Mouse(right ? 16u : 4u)});
            POINT now; GetCursorPos(out now);
            if(now.x==x && now.y==y) SetCursorPos(old.x,old.y);
        }
        internal static void Drag(int x,int y,int targetX,IntPtr hwnd,Action guard)
        {
            CheckModifiers(); CheckPoint(x,y,hwnd);
            POINT old; GetCursorPos(out old); int lastX=x;
            SetCursorPos(x,y); bool pressed=false;
            try
            {
                Submit(new INPUT[]{Mouse(2)}); pressed=true;
                for(int i=1;i<=12;i++)
                {
                    guard(); int next=x+(targetX-x)*i/12; CheckPoint(next,y,hwnd);
                    SetCursorPos(next,y); lastX=next; Thread.Sleep(12);
                }
            }
            finally
            {
                if(pressed) Submit(new INPUT[]{Mouse(4)});
                POINT now; GetCursorPos(out now);
                if(now.x==lastX && now.y==y) SetCursorPos(old.x,old.y);
            }
        }
        static void CheckModifiers()
        {
            if ((GetAsyncKeyState(16) & 0x8000) != 0 || (GetAsyncKeyState(17) & 0x8000) != 0 || (GetAsyncKeyState(18) & 0x8000) != 0)
                throw new Stop("I02", "请松开 Ctrl、Shift、Alt 等按键，再启动程序。");
        }
        internal static void Enter() { CheckModifiers(); Submit(new INPUT[] { Key(13,0,0), Key(13,0,2) }); }
        internal static void Escape() { CheckModifiers(); Submit(new INPUT[]{Key(27,0,0),Key(27,0,2)}); }
        internal static void Chord(ushort modifier,ushort key)
        { CheckModifiers(); Submit(new INPUT[]{Key(modifier,0,0),Key(key,0,0),Key(key,0,2),Key(modifier,0,2)}); }
        internal static void PastePlain()
        {
            CheckModifiers();
            // Ctrl+Shift+V asks Chromium to paste plain text, preserving line breaks.
            Submit(new INPUT[] { Key(17,0,0), Key(16,0,0), Key(86,0,0), Key(86,0,2), Key(16,0,2), Key(17,0,2) });
        }
        static void Submit(INPUT[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) != inputs.Length)
                throw new Stop("I01", "Windows 未完整接受输入，已停止。请勿以管理员身份运行浏览器；检查当前草稿后再处理。");
        }
        internal static void TypeUnicode(string value, Action guard)
        {
            // One physical input pair per UTF-16 code unit; no virtual Enter for text newlines.
            string normalized = Rules.Normalize(value);
            for (int start = 0; start < normalized.Length; start += 32)
            {
                guard();
                var inputs = new List<INPUT>();
                foreach (char ch in normalized.Substring(start, Math.Min(32, normalized.Length - start)))
                { inputs.Add(Key(0,ch,4)); inputs.Add(Key(0,ch,6)); }
                Submit(inputs.ToArray()); Thread.Sleep(5);
            }
        }
    }
}
