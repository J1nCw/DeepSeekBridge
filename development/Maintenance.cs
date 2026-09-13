using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekBridge
{
    static class Maintenance
    {
        internal const int MaxEntries = 500;
        internal static bool Due(DateTime now, DateTime last)
        { return now >= last && now - last >= TimeSpan.FromDays(30); }
        internal static void Record(string directory,string entry)
        {
            // Bounded metadata only. Never record the clipboard, window title, URLs or hashes.
            string file=Path.Combine(directory,"review-report.txt");
            var lines = File.Exists(file) && new FileInfo(file).Length < 256*1024
                ? File.ReadAllLines(file,Encoding.UTF8).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList()
                : new List<string>();
            lines.Add(entry);
            if(lines.Count>MaxEntries) lines.RemoveRange(0,lines.Count-MaxEntries);
            File.WriteAllLines(file,lines.ToArray(),Encoding.UTF8);
        }
        internal static void Check(string directory)
        {
            // Invoked only after a user-triggered run; no service, scheduled task or persistent timer.
            try
            {
                bool created;
                using(var mutex=new Mutex(true,"Local\\DeepSeekBridge.Maintenance",out created))
                {
                    if(!created) return;
                    string stamp=Path.Combine(directory,"maintenance.txt");
                    DateTime now=DateTime.UtcNow; long ticks;
                    if(!File.Exists(stamp) || !Int64.TryParse(File.ReadAllText(stamp),out ticks) ||
                        ticks<DateTime.MinValue.Ticks || ticks>DateTime.MaxValue.Ticks || ticks>now.Ticks)
                    { File.WriteAllText(stamp,now.Ticks.ToString()); return; }
                    if(!Due(now,new DateTime(ticks,DateTimeKind.Utc))) return;
                    using(var form=new Reminder(directory))
                    {
                        form.Shown+=delegate { try { File.WriteAllText(stamp,DateTime.UtcNow.Ticks.ToString()); } catch { } };
                        Application.Run(form);
                    }
                }
            }
            catch { /* Maintenance must never break the sending workflow. */ }
        }
        internal static void Tests(Dictionary<string,bool> checks)
        {
            DateTime start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
            checks.Add("reminder-not-before-30-days",!Due(start.AddDays(30).AddTicks(-1),start));
            checks.Add("reminder-at-30-days",Due(start.AddDays(30),start));
            checks.Add("reminder-after-long-inactivity",Due(start.AddDays(90),start));
            checks.Add("reminder-clock-backwards",!Due(start.AddDays(-1),start));
            checks.Add("reminder-no-repeat-same-day",!Due(start.AddHours(2),start));
            string temp=Path.Combine(Path.GetTempPath(),"DeepSeekBridge-test-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string file=Path.Combine(temp,"review-report.txt");
            try
            {
                File.WriteAllLines(file,Enumerable.Range(0,MaxEntries).Select(i=>"metadata-"+i).ToArray());
                Record(temp,"new-metadata");
                string[] result=File.ReadAllLines(file);
                checks.Add("history-capped-at-500",result.Length==MaxEntries);
                checks.Add("history-oldest-removed",result[0]=="metadata-1" && result.Last()=="new-metadata");
            }
            finally { File.Delete(file); Directory.Delete(temp); }
        }
    }

    sealed class Reminder : Form
    {
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        protected override bool ShowWithoutActivation { get { return true; } }
        internal Reminder(string directory)
        {
            Text="DeepSeekBridge · 每月检查";
            Font=new Font("Microsoft YaHei UI",9F);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(440,175);
            FormBorderStyle=FormBorderStyle.FixedToolWindow;
            ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual;
            var note=new Label { Text="已到每 30 天的运行检查时间。\n可以把 review-report.txt 发给助手，检查耗时、资源占用和失败记录。\n记录不含文献正文，不会自动上传。",AutoSize=false,Location=new Point(18,16),Size=new Size(404,83)};
            var open=new Button {Text="打开运行记录",Location=new Point(18,108),Size=new Size(135,32)};
            var later=new Button {Text="30 天后再提醒",Location=new Point(169,108),Size=new Size(145,32)};
            var hint=new Label {Text="20 秒后自动关闭，不影响下一次调用。",AutoSize=true,Location=new Point(18,150)};
            Controls.AddRange(new Control[]{note,open,later,hint});
            open.Click+=delegate {
                try { Process.Start(new ProcessStartInfo {FileName="explorer.exe",Arguments="\""+directory+"\"",UseShellExecute=true}); }
                catch { }
                Close();
            };
            later.Click+=delegate { Close(); };
            Shown+=delegate { var area=Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea; Location=new Point(area.Right-Width-16,area.Bottom-Height-16); timer.Start(); };
            timer.Interval=20000; timer.Tick+=delegate { Close(); };
        }
        protected override void Dispose(bool disposing)
        { if(disposing) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }
    }
}
