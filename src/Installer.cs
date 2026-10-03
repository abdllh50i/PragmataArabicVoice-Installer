// PRAGMATA Arabic Voice-Over — offline installer (Nexus Mods build).
// Copies PragmataArabicVoice.pak into <game>\pak_mods. No network access. Build: build.bat
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Pragmata Arabic Voice Installer")]
[assembly: AssemblyProduct("Pragmata Arabic Voice")]
[assembly: AssemblyDescription("مثبّت الدبلجة العربية لبراقماتا")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace PragmataArabic
{
    class InstallError : Exception
    {
        public InstallError(string m) : base(m) { }
    }

    class Core
    {
        public const string ReframeworkUrl = "https://www.nexusmods.com/pragmata/mods/40";
        public Action<string, double, string> Report = delegate { };   // الحالة، النسبة (-1 = انتظار)، تفاصيل
        public volatile bool Cancel;
        public bool SkipRunningCheck;     // للاختبار بس (--no-game-check) على مجلد وهمي

        public const string MarkerRel = @"pak_mods\PragmataArabicVoice.install.json";
        public const string PakRel = @"pak_mods\PragmataArabicVoice.pak";
        const string BackupSuffix = ".before-arabic";

        public static bool LooksLikeGame(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "PRAGMATA.exe")); }
            catch { return false; }
        }

        public static bool HasReframework(string game)
        {
            try { return File.Exists(Path.Combine(game, "dinput8.dll")); }
            catch { return false; }
        }

        public bool GameRunning()
        {
            return !SkipRunningCheck && Process.GetProcessesByName("PRAGMATA").Length > 0;
        }

        // مجلد اللعبة: مكتبات ستيم (من الريجستري وlibraryfolders.vdf) وبعض الأماكن المعروفة في كل قرص
        public static string Detect()
        {
            var cands = new List<string>();
            try
            {
                var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    steam = steam.Replace('/', '\\');
                    cands.Add(Path.Combine(steam, @"steamapps\common\PRAGMATA"));
                    var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                            cands.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\PRAGMATA"));
                }
            }
            catch { }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var r = d.RootDirectory.FullName;
                    cands.Add(Path.Combine(r, "PRAGMATA"));
                    cands.Add(Path.Combine(r, @"SteamLibrary\steamapps\common\PRAGMATA"));
                    cands.Add(Path.Combine(r, @"Games\PRAGMATA"));
                    cands.Add(Path.Combine(r, @"Program Files (x86)\Steam\steamapps\common\PRAGMATA"));
                    cands.Add(Path.Combine(r, @"Program Files\Steam\steamapps\common\PRAGMATA"));
                }
                catch { }
            }
            foreach (var c in cands)
                if (LooksLikeGame(c)) return c;
            return null;
        }

        static JavaScriptSerializer Json()
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = 1 << 24;
            return js;
        }

        public static Dictionary<string, object> ReadMarker(string game)
        {
            try
            {
                var p = Path.Combine(game, MarkerRel);
                if (File.Exists(p)) return Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { }
            return null;
        }

        static void WriteMarker(string game, Dictionary<string, object> m)
        {
            File.WriteAllText(Path.Combine(game, MarkerRel), Json().Serialize(m), new UTF8Encoding(false));
        }

        // النسخة المثبّتة: من العلامة، أو «موجودة» لو المود منسوخ يدوي بدون المثبّت
        public static string InstalledVersion(string game)
        {
            var m = ReadMarker(game);
            if (m != null && m.ContainsKey("version")) return Convert.ToString(m["version"]);
            return File.Exists(Path.Combine(game, PakRel)) ? "manual" : null;
        }

        public static string Fmt(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " جيجا";
            return (bytes / (double)(1L << 20)).ToString("0") + " ميقا";
        }

        void CheckCommon(string game)
        {
            if (!LooksLikeGame(game))
                throw new InstallError("ما لقيت PRAGMATA.exe في هالمجلد — اختار مجلد اللعبة الصحيح.");
            if (GameRunning())
                throw new InstallError("اللعبة شغّالة — سكّرها وبعدين اضغط تثبيت.");
        }

        static void CheckSpace(string game, long need)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(game)));
            if (drive.AvailableFreeSpace < need + (200L << 20))
                throw new InstallError("المساحة ما تكفي في القرص " + drive.Name + ": يحتاج " + Fmt(need + (200L << 20)) +
                                       " والموجود " + Fmt(drive.AvailableFreeSpace) + ".");
        }

        public void Uninstall(string game)
        {
            CheckCommon(game);
            Report("أشيل الدبلجة…", -1, "");
            var marker = ReadMarker(game) ?? new Dictionary<string, object>();
            foreach (var p in new[] { PakRel, PakRel + ".download", PakRel + ".tmp" })
            {
                var f = Path.Combine(game, p);
                if (File.Exists(f)) File.Delete(f);
            }
            var dll = Path.Combine(game, "dinput8.dll");
            if (marker.ContainsKey("dll_backup") && File.Exists(dll + BackupSuffix))
            {
                if (File.Exists(dll)) File.Delete(dll);
                File.Move(dll + BackupSuffix, dll);                  // نرجّع REFramework اللي كان قبل
            }
            else if (marker.ContainsKey("dll_added") && File.Exists(dll))
            {
                File.Delete(dll);                                    // ما كان فيه REFramework قبلنا
            }
            var mk = Path.Combine(game, MarkerRel);
            if (File.Exists(mk)) File.Delete(mk);
            Report("انشالت الدبلجة ✓", 1, "");
        }

        // ---------- نسخة نيكسس: ملف المود نازل مع المثبّت ----------
        public static string SourceDir()
        {
            return Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath));
        }

        // ملف الدبلجة اللي اختاره اللاعب بنفسه (زر «اختيار…») — يتقدّم على البحث التلقائي
        public static string ChosenPak;
        const string PakName = "PragmataArabicVoice.pak";

        // نيكسس فيه ملفين (المثبّت + المود) وكل واحد ينفك بمجلد — ندوّر على ملف الدبلجة:
        // جنب المثبّت، ثم المجلدات اللي حوله (نفس مجلد التنزيل)، ثم التنزيلات وسطح المكتب
        public static string FindSourcePak()
        {
            if (ChosenPak != null && File.Exists(ChosenPak)) return ChosenPak;
            var dir = SourceDir();
            foreach (var p in new[] { Path.Combine(dir, PakRel), Path.Combine(dir, PakName), Path.Combine(dir, @"..\" + PakRel) })
                if (File.Exists(p)) return Path.GetFullPath(p);
            var roots = new List<string>();
            try { roots.Add(Path.GetFullPath(Path.Combine(dir, ".."))); } catch { }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, "Downloads"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            roots.Add(Path.Combine(home, "Desktop"));
            foreach (var r in roots)
            {
                var hit = Search(r, 3);
                if (hit != null) return hit;
            }
            return null;
        }

        // بحث محدود العمق (الأحدث أول لو فيه أكثر من نسخة)
        static string Search(string root, int depth)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string best = null;
            DateTime bestT = DateTime.MinValue;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 4000)
            {
                var cur = queue.Dequeue();
                visited++;
                try
                {
                    var f = Path.Combine(cur.Key, PakName);
                    if (File.Exists(f) && !cur.Key.EndsWith(@"\PRAGMATA\pak_mods", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = File.GetLastWriteTime(f);
                        if (best == null || t > bestT) { best = f; bestT = t; }
                    }
                    if (cur.Value < depth)
                        foreach (var sub in Directory.GetDirectories(cur.Key))
                            queue.Enqueue(new KeyValuePair<string, int>(sub, cur.Value + 1));
                }
                catch { }
            }
            return best;
        }

        public static string SourceVersion()
        {
            // version.txt جنب المثبّت أو جنب ملف الدبلجة (أو المجلد اللي فوقه)
            var cands = new List<string> { Path.Combine(SourceDir(), "version.txt") };
            var pak = FindSourcePak();
            if (pak != null)
            {
                var d = Path.GetDirectoryName(pak);
                cands.Add(Path.Combine(d, "version.txt"));
                cands.Add(Path.Combine(d, @"..\version.txt"));
            }
            foreach (var v in cands)
            {
                try { if (File.Exists(v)) return File.ReadAllText(v).Trim(); } catch { }
            }
            return "1.0";
        }

        public void Install(string game)
        {
            CheckCommon(game);
            var src = FindSourcePak();
            if (src == null)
                throw new InstallError("ما لقيت ملف الدبلجة (PragmataArabicVoice.pak).\n\n" +
                                       "حمّل ملف «Arabic Voice-Over Mod» من صفحة المود وفك ضغطه، " +
                                       "وبعدين اضغط «اختيار…» في الخطوة ٣ وحدّد الملف.");
            var dest = Path.Combine(game, PakRel);
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                throw new InstallError("المثبّت شغّال من داخل مجلد اللعبة نفسه — شغّله من المجلد اللي فكّيت فيه الملف.");
            long size = new FileInfo(src).Length;
            CheckSpace(game, size);
            Directory.CreateDirectory(Path.Combine(game, "pak_mods"));
            var tmp = dest + ".tmp";
            using (var fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var fo = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 22];
                long done = 0;
                int n;
                var last = DateTime.MinValue;
                var t0 = DateTime.Now;
                while ((n = fi.Read(buf, 0, buf.Length)) > 0)
                {
                    if (Cancel) { fo.Close(); File.Delete(tmp); throw new InstallError("انلغى."); }
                    fo.Write(buf, 0, n);
                    done += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 150)
                    {
                        last = DateTime.Now;
                        Report("أنسخ الدبلجة للعبة…", done / (double)size, Fmt(done) + " من " + Fmt(size));
                    }
                }
            }
            if (new FileInfo(tmp).Length != size)
            {
                File.Delete(tmp);
                throw new InstallError("النسخ ما كمل — جرّب مرة ثانية.");
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            var marker = ReadMarker(game) ?? new Dictionary<string, object>();
            marker["version"] = SourceVersion();
            marker["source"] = "nexus";
            marker["installed_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            WriteMarker(game, marker);
            Report("تم التثبيت ✓", 1, "النسخة " + SourceVersion());
        }
    }

    // بطاقة خطوة: علامة الحالة + عنوان + تفاصيل + زر اختياري
    class StepCard : Panel
    {
        public readonly Label Mark, Title, Detail;
        public readonly Button Action;
        // الأماكن محسوبة من اليمين بنفسنا (بدون عكس ويندوز التلقائي — يعكس بعض الحاويات وبعضها لا):
        // العلامة يمين، العنوان والتفاصيل بالنص، الزر يسار
        public StepCard(string title, string actionText)
        {
            Size = new Size(592, 66);
            BackColor = Theme.Surface;
            Mark = new Label { Location = new Point(592 - 12 - 38, 13), Size = new Size(38, 38), TextAlign = ContentAlignment.MiddleCenter,
                               Font = new Font("Segoe UI Symbol", 16f, FontStyle.Bold), ForeColor = Theme.Muted, Text = "●" };
            Title = new Label { Location = new Point(154, 9), Size = new Size(380, 24), Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                                ForeColor = Theme.Fg, Text = title };
            Detail = new Label { Location = new Point(154, 34), Size = new Size(380, 24), Font = new Font("Segoe UI", 9f),
                                 ForeColor = Theme.Muted, AutoEllipsis = true };
            Controls.AddRange(new Control[] { Mark, Title, Detail });
            if (actionText != null)
            {
                Action = Theme.MakeButton(actionText, new Point(12, 16), new Size(132, 34), false);
                Controls.Add(Action);
            }
            Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
        }

        public void Set(string glyph, Color c, string detail)
        {
            Mark.Text = glyph;
            Mark.ForeColor = c;
            Detail.Text = detail;
        }
    }

    // شريط تقدّم مرسوم بألوان التصميم (شريط ويندوز يطلع أبيض على الخلفية الداكنة) — يتعبّى من اليمين
    class Bar : Control
    {
        double value;
        bool marquee;
        int phase;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 30 };

        public Bar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            timer.Tick += delegate { phase = (phase + 6) % (Width + 160); Invalidate(); };
        }

        public void Set(double v)
        {
            marquee = v < 0;
            timer.Enabled = marquee;
            value = Math.Max(0, Math.Min(1, v));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            using (var track = new SolidBrush(Theme.Line)) g.FillRectangle(track, 0, 0, Width, Height);
            using (var fill = new SolidBrush(Theme.Accent))
            {
                if (marquee)
                {
                    int x = Width - phase;                       // قطعة تمشي من اليمين لليسار
                    g.FillRectangle(fill, Math.Max(0, x), 0, Math.Min(160, Width - Math.Max(0, x)), Height);
                }
                else
                {
                    int w = (int)(Width * value);
                    g.FillRectangle(fill, Width - w, 0, w, Height);
                }
            }
        }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(12, 16, 22), Surface = Color.FromArgb(20, 27, 36),
            Header = Color.FromArgb(16, 33, 41), Line = Color.FromArgb(40, 52, 66), Fg = Color.FromArgb(230, 237, 243),
            Muted = Color.FromArgb(138, 152, 168), Accent = Color.FromArgb(95, 212, 232), AccentInk = Color.FromArgb(6, 34, 42),
            Ok = Color.FromArgb(94, 211, 154), Warn = Color.FromArgb(242, 180, 90), Err = Color.FromArgb(255, 123, 123);

        public static Button MakeButton(string text, Point loc, Size size, bool primary)
        {
            var b = new Button { Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                                 BackColor = primary ? Accent : Surface, ForeColor = primary ? AccentInk : Fg,
                                 Font = new Font("Segoe UI", primary ? 12f : 9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
                                 UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(127, 222, 238) : Color.FromArgb(30, 40, 52);
            // الزر المعطّل يبين باهت (النمط المسطّح يخلي اللون نفسه لو ما غيّرناه)
            b.EnabledChanged += delegate
            {
                b.BackColor = b.Enabled ? (primary ? Accent : Surface) : Surface;
                b.ForeColor = b.Enabled ? (primary ? AccentInk : Fg) : Muted;
                b.FlatAppearance.BorderColor = b.Enabled && primary ? Accent : Line;
            };
            return b;
        }
    }

    class MainForm : Form
    {
        readonly Core core;
        readonly string[] args;
        string game;
        StepCard stepGame, stepRf, stepDub;
        Button installBtn, uninstallBtn;
        Bar bar;
        Label status, detail;
        Thread worker;
        readonly ToolTip toolTip = new ToolTip();

        public MainForm(Core core, string[] args)
        {
            this.core = core;
            this.args = args;
            Text = "الدبلجة العربية — براقماتا";
            // نص عربي (محاذاة يمين) لكن الأماكن محسوبة من اليمين بنفسنا — بدون RightToLeftLayout
            RightToLeft = RightToLeft.Yes;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            ClientSize = new Size(640, 590);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // الرأس: شعار دائري بحرف «ع» يمين + العنوان جنبه
            var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 92), BackColor = Theme.Header };
            var logo = new Label { Text = "ع", Location = new Point(640 - 24 - 56, 18), Size = new Size(56, 56), BackColor = Theme.Accent,
                                   ForeColor = Theme.AccentInk, Font = new Font("Segoe UI", 22f, FontStyle.Bold),
                                   TextAlign = ContentAlignment.MiddleCenter };
            using (var gp = new GraphicsPath()) { gp.AddEllipse(0, 0, 56, 56); logo.Region = new Region(gp); }
            var title = new Label { Text = "الدبلجة العربية لبراقماتا", Location = new Point(24, 14), Size = new Size(524, 40),
                                    Font = new Font("Segoe UI", 17f, FontStyle.Bold), ForeColor = Theme.Fg };
            var sub = new Label { Text = "PRAGMATA — Arabic Voice-Over", Location = new Point(24, 56), Size = new Size(522, 22),
                                  Font = new Font("Segoe UI", 9.5f), ForeColor = Theme.Muted, RightToLeft = RightToLeft.No,
                                  TextAlign = ContentAlignment.TopRight };
            header.Controls.AddRange(new Control[] { logo, title, sub });

            var stepsLbl = new Label { Text = "جاهزية التثبيت", Location = new Point(24, 108), Size = new Size(592, 22),
                                       Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Theme.Muted };
            stepGame = new StepCard("١. مجلد اللعبة", "تغيير…") { Location = new Point(24, 136) };
            stepRf = new StepCard("٢. REFramework (لازم للمودات)", "صفحة التحميل") { Location = new Point(24, 210) };
            stepDub = new StepCard("٣. ملف الدبلجة", "اختيار…") { Location = new Point(24, 284) };
            stepDub.Action.Click += delegate { PickPak(); };
            stepGame.Detail.RightToLeft = RightToLeft.No;       // المسار إنجليزي — يبين صح من اليسار
            stepGame.Detail.TextAlign = ContentAlignment.TopRight;

            bar = new Bar { Location = new Point(24, 368), Size = new Size(592, 8) };
            status = new Label { Location = new Point(24, 384), Size = new Size(592, 24), ForeColor = Theme.Fg,
                                 Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
            detail = new Label { Location = new Point(24, 408), Size = new Size(592, 22), ForeColor = Theme.Muted };

            // الزر الأساسي يمين (أول شي تشوفه العين بالعربي)، وإلغاء التثبيت يساره
            installBtn = Theme.MakeButton("تثبيت الدبلجة", new Point(640 - 24 - 250, 440), new Size(250, 50), true);
            uninstallBtn = Theme.MakeButton("إلغاء التثبيت", new Point(640 - 24 - 250 - 12 - 150, 440), new Size(150, 50), false);

            // كيف تشغّلها بعد التثبيت — شريط ملوّن يمين
            var how = new Panel { Location = new Point(24, 506), Size = new Size(592, 62), BackColor = Theme.Surface };
            how.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Line)) e.Graphics.DrawRectangle(pen, 0, 0, how.Width - 1, how.Height - 1);
                using (var br = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(br, how.Width - 4, 0, 4, how.Height);
            };
            how.Controls.Add(new Label { Text = "بعد التثبيت", Location = new Point(16, 8), Size = new Size(560, 20),
                                         Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Theme.Accent });
            how.Controls.Add(new Label { Text = "شغّل اللعبة ← الإعدادات ← اللغة ← لغة الصوت ← العربية", Location = new Point(16, 31),
                                         Size = new Size(560, 24), ForeColor = Theme.Fg, Font = new Font("Segoe UI", 10.5f) });

            Controls.AddRange(new Control[] { header, stepsLbl, stepGame, stepRf, stepDub, bar, status, detail,
                                              installBtn, uninstallBtn, how });

            stepGame.Action.Click += delegate { Browse(); };
            if (stepRf.Action != null)
                stepRf.Action.Click += delegate { try { Process.Start(Core.ReframeworkUrl); } catch { } };
            installBtn.Click += delegate { Run(false); };
            uninstallBtn.Click += delegate
            {
                if (Ask("تبي تشيل الدبلجة العربية من اللعبة؟", MessageBoxIcon.Question)) Run(true);
            };
            core.Report = (s, f, d) => BeginInvoke((Action)(() => SetStatus(s, f, d, s.Contains("✓") ? Theme.Ok : Theme.Fg)));
            Load += delegate { OnLoaded(); };
            FormClosing += (s, e) =>
            {
                if (worker != null && worker.IsAlive)
                {
                    if (!Ask("التثبيت شغّال — توقفه؟", MessageBoxIcon.Question)) { e.Cancel = true; return; }
                    core.Cancel = true;
                    worker.Join(3000);
                }
            };
        }

        bool Ask(string msg, MessageBoxIcon icon)
        {
            return MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2,
                                   MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == DialogResult.Yes;
        }

        void Info(string msg, MessageBoxIcon icon)
        {
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
                            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        void OnLoaded()
        {
            game = Arg(args, "--path") ?? Core.Detect();
            RefreshState();
            if (Array.IndexOf(args, "--auto") >= 0 && Core.LooksLikeGame(game)) Run(Array.IndexOf(args, "--uninstall") >= 0);
        }

        void RefreshState()
        {
            if (worker != null && worker.IsAlive) return;
            bool ok = Core.LooksLikeGame(game);
            stepGame.Set(ok ? "✓" : "✗", ok ? Theme.Ok : Theme.Err,
                         ok ? game : (string.IsNullOrEmpty(game) ? "ما لقيت اللعبة — اضغط «تغيير…» واختار مجلدها" : "هذا مو مجلد براقماتا (ما فيه PRAGMATA.exe)"));
            stepGame.Action.Text = ok ? "تغيير…" : "اختيار…";

            bool rf = ok && Core.HasReframework(game);
            stepRf.Set(!ok ? "●" : rf ? "✓" : "!", !ok ? Theme.Muted : rf ? Theme.Ok : Theme.Warn,
                       !ok ? "—" : rf ? "موجود" : "مو موجود — اضغط «صفحة التحميل» وحمّله");
            stepRf.Action.Visible = ok && !rf;
            string ver = ok ? Core.InstalledVersion(game) : null;
            string src = Core.FindSourcePak();
            if (src != null) Core.ChosenPak = src;          // نحفظ اللي لقيناه — ما نعيد البحث كل مرة
            bool haveSrc = src != null;
            string srcInfo = haveSrc ? ("النسخة " + Core.SourceVersion() + " · " + Core.Fmt(new FileInfo(src).Length)) : null;
            // نسخة نيكسس: الخطوة ٣ = ملف الدبلجة اللي نزّله اللاعب (المثبّت والمود ملفين منفصلين)
            if (!haveSrc)
                stepDub.Set("✗", Theme.Err, "ما لقيته — فك ضغط «Arabic Voice-Over Mod» واضغط «اختيار…»");
            else
                stepDub.Set("✓", Theme.Ok, "لقيته  ·  " + srcInfo);
            stepDub.Action.Text = haveSrc ? "تغيير…" : "اختيار…";
            if (haveSrc) toolTip.SetToolTip(stepDub.Detail, src);

            installBtn.Enabled = ok && haveSrc;
            uninstallBtn.Enabled = ok && ver != null;
            installBtn.Text = ver == null ? "تثبيت الدبلجة" : (ver == Core.SourceVersion() ? "إعادة التثبيت" : "تحديث الدبلجة");
            if (!ok) SetStatus("اختار مجلد اللعبة أول.", 0, "", Theme.Muted);
            else if (!haveSrc) SetStatus("ملف الدبلجة ناقص — حمّل «Arabic Voice-Over Mod» وفك ضغطه.", 0, "", Theme.Err);
            else if (ver != null) SetStatus("الدبلجة مثبّتة ✓" + (ver != "manual" ? "  (النسخة " + ver + ")" : ""), 1, "", Theme.Ok);
            else SetStatus("كل شي جاهز — اضغط «تثبيت الدبلجة».", 0, "", Theme.Fg);
        }

        void PickPak()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "اختار ملف الدبلجة PragmataArabicVoice.pak (من مجلد Arabic Voice-Over Mod بعد فك الضغط)";
                d.Filter = "ملف الدبلجة (PragmataArabicVoice.pak)|*.pak|ملفات مضغوطة|*.7z;*.zip;*.rar|كل الملفات|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var ext = Path.GetExtension(d.FileName).ToLowerInvariant();
                if (ext == ".7z" || ext == ".zip" || ext == ".rar")
                {
                    Info("هذا الملف لسا مضغوط.\n\nاضغط عليه بالزر الأيمن ← «استخراج الكل» (أو 7-Zip ← «Extract Here»)، " +
                         "وبعدين اختار ملف PragmataArabicVoice.pak اللي يطلع داخل مجلد pak_mods.", MessageBoxIcon.Information);
                    return;
                }
                if (ext != ".pak") { Info("اختار ملف PragmataArabicVoice.pak", MessageBoxIcon.Information); return; }
                Core.ChosenPak = d.FileName;
                RefreshState();
            }
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "اختار مجلد براقماتا (اللي فيه PRAGMATA.exe)";
                d.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(game) && Directory.Exists(game)) d.SelectedPath = game;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    game = d.SelectedPath;
                    RefreshState();
                }
            }
        }

        void SetStatus(string s, double f, string d, Color c)
        {
            status.Text = s;
            status.ForeColor = c;
            detail.Text = d;
            bar.Set(f);
        }

        void Run(bool uninstall)
        {
            var g = game;
            installBtn.Enabled = uninstallBtn.Enabled = stepGame.Action.Enabled = false;
            core.Cancel = false;
            worker = new Thread(() =>
            {
                string err = null;
                bool needAdmin = false;
                try
                {
                    if (uninstall) core.Uninstall(g); else core.Install(g);
                }
                catch (InstallError ex) { err = ex.Message; }
                catch (UnauthorizedAccessException) { needAdmin = true; }
                catch (Exception ex) { err = "صار خطأ: " + ex.Message; }
                BeginInvoke((Action)(() => Done(g, uninstall, err, needAdmin)));
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void Done(string g, bool uninstall, string err, bool needAdmin)
        {
            stepGame.Action.Enabled = true;
            worker = null;
            if (needAdmin)
            {
                SetStatus("ما عندي صلاحية أكتب في مجلد اللعبة.", 0, "", Theme.Err);
                if (Ask("مجلد اللعبة يحتاج صلاحيات المسؤول. أعيد تشغيل المثبّت بصلاحيات المسؤول؟", MessageBoxIcon.Warning))
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath,
                            "--path \"" + g + "\" --auto" + (uninstall ? " --uninstall" : "")) { Verb = "runas", UseShellExecute = true };
                        Process.Start(psi);
                        Close();
                        return;
                    }
                    catch { }
                }
                RefreshState();
                return;
            }
            if (err != null)
            {
                RefreshState();
                SetStatus(err.Split('\n')[0], 0, "", Theme.Err);
                Info(err, MessageBoxIcon.Warning);
                return;
            }
            RefreshState();
            if (uninstall) { Info("انشالت الدبلجة العربية من اللعبة.", MessageBoxIcon.Information); return; }
            string extra = Core.HasReframework(g) ? "" :
                "\n\n⚠️ باقي خطوة: REFramework مو موجود، والدبلجة ما تشتغل بدونه. اضغط «صفحة التحميل» في الخطوة ٢.";
            Info("تم التثبيت ✓\n\nشغّل اللعبة ← الإعدادات ← اللغة ← لغة الصوت ← العربية." + extra,
                 extra.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    static class Program
    {

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            var core = new Core { SkipRunningCheck = Array.IndexOf(args, "--no-game-check") >= 0 };
            // وضع بدون واجهة (للاختبار): --silent --path <مجلد> [--uninstall] — السجل في %TEMP%
            if (Array.IndexOf(args, "--silent") >= 0)
            {
                var log = Path.Combine(Path.GetTempPath(), "PragmataArabicInstaller.log");
                string lastS = null;
                core.Report = (s, f, d) =>
                {
                    if (s != lastS || f >= 1) File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss ") + s + " " +
                        (f >= 0 ? (f * 100).ToString("0") + "% " : "") + d + "\r\n", Encoding.UTF8);
                    lastS = s;
                };
                try
                {
                    var g = Arg(args, "--path") ?? Core.Detect();
                    if (Array.IndexOf(args, "--uninstall") >= 0) core.Uninstall(g); else core.Install(g);
                    File.AppendAllText(log, "OK\r\n", Encoding.UTF8);
                    return 0;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(log, "ERROR " + ex.Message + "\r\n", Encoding.UTF8);
                    return 1;
                }
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(core, args));
            return 0;
        }
    }
}
