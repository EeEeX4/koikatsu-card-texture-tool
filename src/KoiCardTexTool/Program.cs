using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace KoiCardTexTool
{
    /// <summary>CLI output: console when a console is attached, plus an optional log=file.</summary>
    static class Cli
    {
        public static StreamWriter File;
        public static void W(string s)
        {
            try { Console.WriteLine(s); } catch { }
            if (File != null) { try { File.WriteLine(s); File.Flush(); } catch { } }
        }
    }

    static class Program
    {
        /// <summary>uitest 的 hold=毫秒：把窗口留在屏幕上跑消息循环（外部截屏/人眼观察），然后照常退出。</summary>
        static void HoldUi(MainForm f, int ms)
        {
            if (ms <= 0) return;
            Cli.W("HOLD   : 窗口保持 " + ms + "ms");
            var swh = System.Diagnostics.Stopwatch.StartNew();
            while (swh.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
            Cli.W("HOLDEND: 保持结束");
        }
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);
        const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

        /// <summary>dlgtest 用：延迟若干毫秒后把本进程弹出的 #32770 对话框关掉，好让 ShowDialog 返回</summary>
        public static void AutoCloseDialog(int ms)
        {
            var t = new System.Threading.Thread(delegate ()
            {
                System.Threading.Thread.Sleep(ms);
                CloseDialogsNow();
            });
            t.IsBackground = true; t.Start();
        }

        /// <summary>立刻把本进程弹出来的模态框（#32770）关掉 —— 自检期间的"看门狗"用这个。
        /// 为什么需要：自检里只要有一处弹出确认框，无头跑就会**卡死**，
        /// 而且那个窗口还会留在用户桌面上等人点（v2.30 自检时真发生过一次）。</summary>
        public static int CloseDialogsNow()
        {
            int n = 0;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    var sb = new StringBuilder(256);
                    GetClassName(h, sb, 256);
                    if (sb.ToString() == "#32770") { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; }
                }
                return true;
            }, IntPtr.Zero);
            return n;
        }

        /// <summary>自检期间开一个看门狗：每 300ms 扫一遍，有模态框就关掉。
        /// 调用方要持有返回的引用（否则会被 GC 回收、定时器就停了）。</summary>
        static System.Windows.Forms.Timer StartDialogWatchdog()
        {
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += delegate { try { CloseDialogsNow(); } catch { } };
            t.Start();
            return t;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && IsCommand(args[0]))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                HookConsole();
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                try { return RunCli(args); }
                finally { if (Cli.File != null) { Cli.File.Flush(); Cli.File.Dispose(); } }
            }
            // 不是命令 → 走界面：拖到 EXE 图标上 / "打开方式" 传进来的路径会在这里被预载
            InstallCrashHandlers();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm(args.Length > 0 ? args : null)); }
            catch (Exception ex) { Report("[启动异常]", ex); return 3; }
            return 0;
        }

        /// <summary>WinExe 没有自己的控制台：AttachConsole 之后 stdout 句柄可能仍是无效的，
        /// 这时 Console.WriteLine 会静默丢弃（这就是"CLI 什么都不打印"的原因）。
        /// 重新用 Console.OpenStandardOutput() 包一个 stream 就能写出来了。</summary>
        static void HookConsole()
        {
            try
            {
                var so = Console.OpenStandardOutput();
                // 句柄无效时 OpenStandardOutput 返回 Stream.Null —— 千万别拿它去 SetOut，
                // 否则后面所有 Console.WriteLine 都会被静默吞掉。
                if (so != null && so != Stream.Null)
                    Console.SetOut(new StreamWriter(so, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch { }
            try
            {
                var se = Console.OpenStandardError();
                if (se != null && se != Stream.Null)
                    Console.SetError(new StreamWriter(se, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch { }
        }

        /// <summary>全局崩溃兜底：把异常完整写到 EXE 同目录的 crash.log，再弹窗提示，
        /// 而不是让进程静默消失（WinExe 没有控制台，用户只会看到一句看不懂的报错）。</summary>
        static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            { Report("[界面线程异常]", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            { Report("[后台线程异常]", e.ExceptionObject as Exception); };
        }

        public static string CrashLogPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return Path.Combine(dir, "KoiCardTexTool-crash.log");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "KoiCardTexTool-crash.log");
        }

        public static void Report(string tag, Exception ex)
        {
            string path = CrashLogPath();
            string text = L.F("{0} {1}{2}{3}{2}{2}—— 把这一段发给开发者即可定位问题。{2}EXE: {4}{2}OS: {5}{2}CLR: {6}{2}区域: {7} / {8}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), tag, Environment.NewLine,
                ex == null ? "(无异常对象)" : ex.ToString(),
                Application.ExecutablePath, Environment.OSVersion, Environment.Version,
                System.Globalization.CultureInfo.CurrentCulture.Name,
                System.Globalization.CultureInfo.CurrentUICulture.Name);
            try { File.AppendAllText(path, text + Environment.NewLine + new string('-', 70) + Environment.NewLine, new UTF8Encoding(false)); }
            catch { path = "(日志写入失败)"; }
            try
            {
                MessageBox.Show(text, "KoiCardTexTool 出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        static bool IsCommand(string s)
        {
            string c = (s ?? "").ToLowerInvariant();
            return c == "scan" || c == "compress" || c == "batch" || c == "info" || c == "uitest" || c == "help" || c == "-h" || c == "--help" || c == "dlgtest" || c == "mboxtest" || c == "hdrtest" || c == "parts" || c == "preset" || c == "chara" || c == "pngtest" || c == "pngre" || c == "jpegopt" || c == "blk";
        }

        static bool IsAllDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        static int CountParts(TexTool.PartsScan ps)
        {
            int n = 0;
            foreach (var p in ps.Parts) if (p.Tex.Count > 0) n++;
            return n;
        }

        /// <summary>解析 slots=0:auto:1024,1:png:512,1003:jpeg:512（部位:格式:最大边）。
        /// slot 也可以写名字里的关键词（top/bot/bra/shorts/gloves/panst/socks/shoes_inner/shoes_outer）。
        /// 只写部位名时默认 auto/1024。返回 null = 没给 slots=，走原来的类型表流程。</summary>
        static Dictionary<int, Rule> ParseSlotRules(List<string> args)
        {
            string spec = null;
            for (int i = 0; i < args.Count; i++)
                if (args[i].ToLowerInvariant().StartsWith("slots=")) spec = args[i].Substring(6);
            if (spec == null) return null;
            var d = new Dictionary<int, Rule>();
            if (spec.Trim().Length == 0 || spec.Trim().ToLowerInvariant() == "all")
            {
                for (int sn = 0; sn < Card.ClothesSlotNames.Length; sn++)
                    d[Card.SlotKey(Card.ObjClothes, sn)] = new Rule("auto", 1024);
                return d;
            }
            foreach (var part in spec.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var f = p.Split(':');
                int key = SlotKeyFromToken(f[0].Trim());
                if (key < 0) continue;
                d[key] = new Rule(NormFmt(f.Length > 1 ? f[1] : null), NormSize(f.Length > 2 ? f[2] : null, 1024));
            }
            return d;
        }

        /// <summary>命令行里写的格式名归一化成内部键（自动/原样不动/PNG/JPEG 也认）。</summary>
        static string NormFmt(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return "auto";
            if (t == "自动" || t == "auto") return "auto";
            if (t == "原样不动" || t == "原样" || t == "keep") return "keep";
            if (t == "png") return "png";
            if (t == "jpeg" || t == "jpg") return "jpeg";
            return t;
        }

        /// <summary>命令行里的边长：不缩放/0 → 0。</summary>
        static int NormSize(string s, int dflt)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0) return dflt;
            if (t == "不缩放" || t == "原尺寸" || t == "无") return 0;
            int n;
            return int.TryParse(t, out n) ? n : dflt;
        }

        /// <summary>groups=body / groups=0,5 / groups=all → 组掩码（位 g = 第 g 组，0=角色本体）。
        /// 0 表示不限制。用掩码而不是"展开成部位规则"，这样组内的规则仍按类型表/参数走。</summary>
        /// <summary>批处理并行度：jobs=N &gt; 环境变量 KOITEX_JOBS &gt; 默认 min(4, 核数)。
        /// 不直接按核数开，是因为并发数真正受**内存**限制（一张 200 MB 的卡峰值约 1.1 GB）。</summary>
        static int ParseJobs(List<string> args, int cardCount)
        {
            int n = 0;
            foreach (var s in args)
            {
                string low = s.ToLowerInvariant();
                if (low.StartsWith("jobs=")) { int v; if (int.TryParse(s.Substring(5), out v)) n = v; }
            }
            if (n <= 0)
            {
                string e = Environment.GetEnvironmentVariable("KOITEX_JOBS");
                int v;
                if (!string.IsNullOrEmpty(e) && int.TryParse(e, out v)) n = v;
            }
            if (n <= 0) n = Math.Min(4, Environment.ProcessorCount);
            if (n < 1) n = 1;
            if (n > 32) n = 32;
            if (n > cardCount) n = Math.Max(1, cardCount);
            return n;
        }

        /// <summary>批处理里一张卡的产出（并行算完、串行打印，保证日志顺序与旧版一致）。</summary>
        sealed class BatchRow
        {
            public string Fn;          // 文件名
            public string Dst;         // 输出全路径
            public RepackResult R;     // 处理结果（Err != null 时为 null）
            public string Err;         // 读卡/处理时的异常信息
        }

        /// <summary>把命令行里的全局开关（pnga= / own= / ownmax= / premul= / denoise= …）应用到静态设置上。
        /// 2.14 之前叫 ApplyPngMax —— 名字早就不止管 PNG 了，删「PNG 极限压缩」时一并改名。</summary>
        static void ApplyGlobalFlags(List<string> args)
        {
            // 「独占贴图保护」：plan=/preset= 文件里带了 ownProtect/ownMax 就照它走；
            // 命令里显式写了 own=/ownmax= 的以命令行为准（命令行优先）。
            bool ownSet = false, fileOwn = false, fileOwnSeen = false;
            int fileMax = 4096;
            bool caSet = false, shSet = false, pctSet = false, modeSet = false;
            bool fileCa = false, fileCaSeen = false, fileShpSeen = false, fileModeExplicit = false;
            string fileSh = "", fileMode = "";
            int filePct = 100;
            Dictionary<string, Rule> tmpPlan; int tmpQ; bool tmpP;
            foreach (var s in args)
            {
                string lo = s.ToLowerInvariant();
                if (lo == "own=0" || lo == "own=1" || lo.StartsWith("ownmax=")) { ownSet = true; continue; }
                string pj = null;
                if (lo.StartsWith("plan=")) pj = s.Substring(5);
                else if (lo.StartsWith("preset=")) pj = s.Substring(7);
                if (pj == null) continue;
                string js = PresetIo.ReadJson(pj);
                bool on; int mx;
                if (PresetIo.TryReadOwnProtect(js, out on, out mx)) { fileOwn = on; fileMax = mx; fileOwnSeen = true; }
                // 预设里的「处理开关」也一并带上（colorArea / sharpen / sharpenPct / sharpenMode），
                // 这样命令行 preset=性能 与界面上选「性能」才是同一件事（否则 colorArea 会丢）。
                // v2.32：这里的 caSeen 以前接的是 TryReadPlanEx 的返回值 —— 那个返回值的意思是
                // **"预设里有没有类型表"**，不是"有没有 colorArea 字段"。于是"有类型表但没写 colorArea"
                // 的老预设会被当成 colorArea=false 照收，把 2.32 的新默认值悄悄关掉。
                // 实测抓到过：老预设跑出来逐字节等于旧核。现在改用带 colorAreaSeen 的重载。
                bool fca = false; string fsh = null; bool fcaSeen = false;
                bool hasPlan = PresetIo.TryReadPlanEx(js, out tmpPlan, out tmpQ, out tmpP, out fca, out fsh, out fcaSeen);
                if (hasPlan) { fileCa = fca; fileSh = fsh; fileCaSeen = fcaSeen; }
                int fpct; string fmode;
                { int fpct2; string fmode2; bool fexp; if (PresetIo.TryReadSharpenEx(js, out fpct2, out fmode2, out fexp)) { filePct = fpct2; fileMode = fmode2; fileModeExplicit = fexp; fileShpSeen = true; } }
            }
            if (fileOwnSeen && !ownSet) { TexTool.ProtectOwn = fileOwn; TexTool.ProtectOwnMax = fileMax; }
            foreach (var s in args)
            {
                string lo = s.ToLowerInvariant();
                if (lo.StartsWith("colorarea=")) caSet = true;
                if (lo.StartsWith("sharpen=")) shSet = true;
                if (lo.StartsWith("sharpenpct=")) pctSet = true;
                if (lo == "sharpcas=0" || lo == "sharpcas=1") modeSet = true;
            }
            if (fileCaSeen && !caSet) Resample.ColorMode = fileCa;
            if (fileCaSeen && !shSet && !string.IsNullOrEmpty(fileSh))
            {
                if (fileSh == "edge") { Resample.Sharpen = 0.6; Resample.SharpenEdgeMax = 2.5; }
                else { double v; if (double.TryParse(fileSh, out v)) { Resample.Sharpen = v; Resample.SharpenEdgeMax = 0; } }
            }
            if (fileShpSeen && !pctSet && filePct >= 0) Resample.SharpenPct = filePct > 200 ? 200 : filePct;
            if (fileShpSeen && !modeSet && fileModeExplicit && !string.IsNullOrEmpty(fileMode))
            { Resample.SharpMode = fileMode == "cas" ? "cas" : "edge"; Resample.SharpModeExplicit = true; }

            foreach (var s in args)
            {
                string low = s.ToLowerInvariant();
                if (low == "pnga=1") PngEnc.UseGrayAlpha = true;      // 灰度/灰度+alpha → 2/1 通道
                if (low == "pnga=0") PngEnc.UseGrayAlpha = false;
                if (low == "jpegopt=0") JpegOpt.Enabled = false;       // 关掉 JPEG 无损哈夫曼再优化
                if (low == "kernel=area") Resample.DetailMode = true;   // 数据贴图用面积平均降采样（默认已开）
                if (low == "kernel=bicubic") Resample.DetailMode = false; // 退回 GDI+ 双三次（旧行为）
                if (low == "colorarea=1") Resample.ColorMode = true;      // 彩色贴图(maintex 等)也用面积平均（2.32 起默认已开）
                if (low == "colorarea=0") Resample.ColorMode = false;     // 彩色贴图退回 GDI+ 双三次（2.31 及以前的行为）
                if (low == "premul=0") Resample.Premul = false;           // 关掉预乘 alpha 平均
                // v2.33 放大：up=0/2/4（全局倍率，默认 0 = 不放大）；upkernel= 选核；upmax= 改硬上限
                if (low.StartsWith("up=")) { int uv; if (int.TryParse(low.Substring(3), out uv)) Resample.UpGlobal = (uv >= 4) ? 4 : (uv >= 2 ? 2 : 0); }
                if (low.StartsWith("upkernel="))
                {
                    string uk = low.Substring(9);
                    if (Resample.IsUpKernel(uk)) Resample.UpKernel = uk;
                    else Cli.W("[警告] 不认识的放大核「" + uk + "」，用默认 " + Resample.UpKernel
                               + "（可选：" + string.Join(" / ", Resample.UpKernels) + "）");
                }
                if (low.StartsWith("upmax=")) { int uv; if (int.TryParse(low.Substring(6), out uv)) Resample.UpCeiling = uv; }
                if (low == "own=0") TexTool.ProtectOwn = false;           // 关掉「独占贴图保护」
                if (low == "own=1") TexTool.ProtectOwn = true;
                if (low.StartsWith("ownmax=")) { int ov; if (int.TryParse(low.Substring(7), out ov)) TexTool.ProtectOwnMax = ov; }   // 0 = 独占贴图完全不降采样
                if (low == "copyskips=1") SkipCopy.On = true;    // 处理不了的卡（不是卡片/结构异常/不匹配）原样复制到输出目录
                if (low == "copyskips=0") SkipCopy.On = false;
                if (low.StartsWith("denoise=")) Resample.Denoise = low.Substring(8);   // off/all/dirty
                if (low.StartsWith("deps=")) { double dv; if (double.TryParse(low.Substring(5), out dv)) Resample.DenoiseEps = dv; }
                if (low.StartsWith("dthr=")) { double dv; if (double.TryParse(low.Substring(5), out dv)) Resample.DirtyThreshold = dv; }
                if (low.StartsWith("sharpenpct=")) { int sv; if (int.TryParse(s.Substring(11), out sv)) Resample.SharpenPct = sv < 0 ? 0 : (sv > 500 ? 500 : sv); }
                if (low.StartsWith("forcejpg=")) { TexTool.ForceJpgClasses.Clear(); foreach (var tk in s.Substring(9).Split(',')) if (tk.Trim().Length > 0) TexTool.ForceJpgClasses.Add(tk.Trim().ToLowerInvariant()); }
                if (low == "nonalphajpeg=1") TexTool.NonalphaJpeg = true;   // 【测试】非 alpha shader 的贴图强制转 JPEG
                if (low == "nonalphajpeg=0") TexTool.NonalphaJpeg = false;
                if (low.StartsWith("forcejpgsafe=")) { int lv; if (int.TryParse(low.Substring(13), out lv)) TexTool.ForceJpgAlphaLevel = lv < 0 ? 0 : (lv > 2 ? 2 : lv); }
                if (low == "matcapjpg=1") TexTool.MatcapCircleJpeg = true;    // 内建判据：matcap 圆掩罩自动转 JPEG（默认开）
                if (low == "matcapjpg=0") TexTool.MatcapCircleJpeg = false;   // 关掉（对照实验用）
                if (low.StartsWith("struct=")) { double gv; if (double.TryParse(s.Substring(7), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out gv)) Resample.StructGamma = gv < 0 ? 0 : (gv > 1 ? 1 : gv); }
                if (low == "sharpcas=1") { Resample.SharpMode = "cas"; Resample.SharpModeExplicit = true; }
                if (low == "sharpcas=0") { Resample.SharpMode = "edge"; Resample.SharpModeExplicit = true; }
                if (low == "alphasharp=0") Resample.SharpAlphaData = false;
                if (low == "alphasharp=1") Resample.SharpAlphaData = true;
                if (low.StartsWith("sharpen="))
                {
                    string sv2 = low.Substring(8);
                    if (sv2 == "edge") { Resample.Sharpen = 0.6; Resample.SharpenEdgeMax = 2.5; }   // 边缘感知锐化
                    else { double sv; if (double.TryParse(sv2, out sv)) Resample.Sharpen = sv; }
                }
                                            }
        }

        static int ParseGroupMask(List<string> args)
        {
            string spec = null;
            foreach (var s in args)
                if (s.ToLowerInvariant().StartsWith("groups=")) spec = s.Substring(7).Trim();
            if (spec == null) return 0;
            string low = spec.ToLowerInvariant();
            if (low.Length == 0 || low == "all") return 0;
            int mask = 0;
            foreach (var tk in spec.Split(','))
            {
                string t = tk.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                if (t == "body" || t == "本体") { mask |= 1; continue; }
                if (t.StartsWith("g")) t = t.Substring(1);
                int g;
                if (int.TryParse(t, out g) && g >= 0 && g <= 30) mask |= (1 << g);
            }
            return mask;
        }

        /// <summary>groups=body / groups=0,5 / groups=all：把"要压哪些组（角色本体 + 换装1..7）"
        /// 展开成「组·部位」规则（人物卡专用）。给了 groups= 就以它为准。</summary>
        static Dictionary<int, Rule> ExpandGroups(List<string> args, string cardPath, Dictionary<int, Rule> cur)
        {
            string spec = null;
            foreach (var s in args)
                if (s.ToLowerInvariant().StartsWith("groups=")) spec = s.Substring(7).Trim();
            if (spec == null || !File.Exists(cardPath)) return cur;
            var ps = TexTool.ScanParts(cardPath);
            if (ps.Err.Length > 0) return cur;
            var want = new List<int>();
            string low = spec.ToLowerInvariant();
            bool all = low.Length == 0 || low == "all";
            foreach (var tk in spec.Split(','))
            {
                string t = tk.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                if (t == "body" || t == "本体" || t == "0") want.Add(0);
                else if (t.StartsWith("g")) { int g; if (int.TryParse(t.Substring(1), out g)) want.Add(g); }
                else { int g; if (int.TryParse(t, out g)) want.Add(g); }
            }
            var d = new Dictionary<int, Rule>();
            foreach (var pi in ps.Parts)
            {
                if (pi.Tex.Count == 0 || !Chara.IsCharaKey(pi.SlotKey)) continue;
                int g = Chara.KeyGroup(pi.SlotKey);
                if (!all && !want.Contains(g)) continue;
                d[pi.SlotKey] = new Rule("auto", 1024);
            }
            return d.Count > 0 ? d : cur;
        }

        /// <summary>preset= 指向的预设文件里的 JSON 文本（没给 / 读不出返回 null）。</summary>
        static string PresetJsonArg(List<string> args)
        {
            foreach (var s in args)
            {
                if (!s.ToLowerInvariant().StartsWith("preset=")) continue;
                string p = s.Substring(7);
                if (!File.Exists(p)) return null;
                return PresetIo.ReadJson(p);
            }
            return null;
        }

        /// <summary>preset=file.json → 预设对象（没给返回 null）。force=1/0 控制"不像同款也强制用通用设置"。</summary>
        static TexTool.Preset ParsePresetArg(List<string> args)
        {
            TexTool.Preset pf = null;
            bool force = false;
            foreach (var s in args)
            {
                string low = s.ToLowerInvariant();
                if (low.StartsWith("preset="))
                {
                    string p = s.Substring(7);
                    if (!File.Exists(p)) throw new FileNotFoundException("预设文件不存在: " + p);
                    pf = TexTool.PresetLoad(p);
                }
                else if (low == "force=1" || low == "force") force = true;
                else if (low.StartsWith("minmatch="))
                {
                    double m = double.Parse(s.Substring(9).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (m > 1) m /= 100.0;
                    if (pf != null) pf.MinMatch = m;
                }
            }
            if (pf != null) pf.ForceUniform = force;
            return pf;
        }

        /// <summary>slots=all（或 slots= 空）——需要按卡片实际部位展开。</summary>
        static bool SlotRulesWantsAll(List<string> args)
        {
            foreach (var s in args)
            {
                string low = s.ToLowerInvariant();
                if (!low.StartsWith("slots=")) continue;
                string spec = s.Substring(6).Trim().ToLowerInvariant();
                return spec.Length == 0 || spec == "all";
            }
            return false;
        }

        /// <summary>部位标识：数字（服装 0..8，饰品 1000+n）或名字关键词。</summary>
        static int SlotKeyFromToken(string tok)
        {
            if (IsAllDigits(tok))
            {
                int n = int.Parse(tok);
                return n >= 1000 ? n : Card.SlotKey(Card.ObjClothes, n);
            }
            string t = tok.ToLowerInvariant();
            if (t.StartsWith("acc"))                     // acc3 / acc_3
            {
                string dig = "";
                foreach (char c in t) if (c >= '0' && c <= '9') dig += c;
                if (dig.Length > 0) return 1000 + int.Parse(dig);
            }
            for (int sn = 0; sn < Card.ClothesSlotNames.Length; sn++)
            {
                string nm = Card.ClothesSlotNames[sn].ToLowerInvariant();
                if (nm.StartsWith(t) || nm.Contains(t)) return Card.SlotKey(Card.ObjClothes, sn);
            }
            return -1;
        }

        static void Usage()
        {
            Cli.W(@"KoiCardTexTool - Koikatsu 衣服卡贴图压缩工具（原生 EXE，不需要 Python）

GUI:   KoiCardTexTool.exe                  （双击运行）

CLI:
  KoiCardTexTool.exe scan     <card.png|目录> [...] [recurse=0] [log=out.txt]
  KoiCardTexTool.exe parts    <card.png> [log=out.txt]
  KoiCardTexTool.exe preset   <card.png> <out.json> [parts=部位:格式:最大边,...] [tex=TexID:格式:最大边,...]
                              [source=名字] [log=out.txt]
  KoiCardTexTool.exe compress <in> <out> [max_size] [mode] [quality] [mask_size] [only=A,B]
                              [slots=部位:格式:最大边,...] [preset=preset.json] [log=out.txt]
  KoiCardTexTool.exe batch    <in_dir> [out_dir] [max_size] [mode] [quality] [mask_size]
                              [plan=plan.json] [preset=preset.json] [recurse=0|1] [suffix=[zip]] [log=out.txt]

mode   = png | jpg | auto（默认）| maintex（只压 MainTex，其余贴图字节不动）
max_size / mask_size = 最长边上限，0 = 不缩放

preset = 预设：一张卡调好后导出的「部位规则 + 单张贴图规则 + 通用设置」，可套用到同款服装的其它卡
         （只有贴图不同的那种）。跨卡靠「部位|材质|属性」绑定签名匹配（材质名自动忽略
         .MECopyN 副本后缀），TexID 平移也能对上；对不上的会在日志里列出来。
         压之前先算「同款匹配度」：低于 minMatch（默认 0.5）就跳过不压，日志写明匹配度；
         加 force=1 则改用通用设置整卡压（不同款也能压，但只用通用设置，不套部位/单张规则）。
         导出: preset 卡A.png a.json parts=top:auto:512 tex=26:png:256 uniform=auto:1024 minmatch=0.5
         套用: batch D:\同款卡 preset=a.json        （不同款的卡会被跳过）
               batch D:\混装 preset=a.json force=1  （不同款的也按通用设置压）
         预设里的单张贴图规则优先级最高（压过部位规则与共用规则）。

parts  = 列出一张卡里每个部位有哪些贴图（就是界面「单服装卡细分压缩」分页的数据）

slots= = 单服装卡细分压缩（只对单张卡生效）。部位写法：数字或名字关键词
         服装：0 top / 1 bot / 2 bra / 3 shorts / 4 gloves / 5 panst / 6 socks
               7 shoes_inner / 8 shoes_outer
         饰品：1000+n（acc3 等同于 1003）
         格式：keep | png | jpeg | auto（默认 auto）；最大边省略 = 1024
         例：slots=0:auto:1024,7:png:512     slots=top,bot,8:jpeg:2048
         没写进 slots= 的部位一律原样不动；被多个部位共用的贴图，
         必须那些部位**全部**写进 slots= 才会动，合并时格式取最保守、尺寸取最大。

batch 给目录时默认递归读所有子文件夹，输出按同样结构摆回去：
  · 省略 out_dir → 输出到 <in_dir>\<in_dir 的名字>[zip]\
  · 输出卡片名默认是「原名[zip].png」，用 suffix= 改（suffix= 空表示保持原名）
  · 以 [zip] 结尾的目录 / 带 .koicardtex-outdir 标记的目录不会被当作输入
  · recurse=0 只读顶层");
        }

        static int RunCli(string[] argv)
        {
            var a = new List<string>(argv);
            for (int i = a.Count - 1; i >= 0; i--)
                if (a[i].ToLowerInvariant().StartsWith("log="))
                {
                    Cli.File = new StreamWriter(a[i].Substring(4), false, new UTF8Encoding(false)) { AutoFlush = true };
                    a.RemoveAt(i);
                }
            try
            {
                if (a.Count == 0) { Usage(); return 1; }
                string cmd = a[0].ToLowerInvariant();

                if (cmd == "scan")
                {
                    if (a.Count < 2) { Usage(); return 1; }
                    bool rec = true;
                    var paths = new List<string>();
                    for (int i = 1; i < a.Count; i++)
                    {
                        if (a[i].ToLowerInvariant().StartsWith("recurse=")) { rec = a[i].Substring(8) != "0"; continue; }
                        paths.Add(a[i]);
                    }
                    int total = 0;
                    bool anyDir = false;
                    foreach (var p in paths)
                    {
                        // 目录就递归读（跳过 [zip] 输出目录），文件就直接扫
                        var list = new List<string>();
                        if (Directory.Exists(p))
                        {
                            anyDir = true;
                            foreach (var cf in TexTool.Enumerate(p, TexTool.DefaultOut(p), rec))
                                list.Add(cf.Rel.Length > 0 ? Path.Combine(cf.Rel, Path.GetFileName(cf.Src)) : Path.GetFileName(cf.Src));
                            Cli.W(L.F("{0}: 共 {1} 个 .png（{2}子文件夹）", p, list.Count, rec ? "含" : "不含"));
                        }
                        else list.Add(p);
                        foreach (var rel in list)
                        {
                            string full = Directory.Exists(p) ? Path.Combine(p, rel) : rel;
                            var s = TexTool.Scan(full);
                            if (s == null) { Cli.W(rel + ": 不是卡片"); continue; }
                            total += s.NTex;
                            Cli.W(L.F("{0}: {1} 张贴图 / {2}", rel, s.NTex, TexTool.Human(s.Bytes)));
                            foreach (var c in Classes.Order)
                            {
                                long[] v;
                                if (s.Classes.TryGetValue(c, out v))
                                    Cli.W(L.F("    {0,-10} {1,4} 张  {2,10}  最大 {3}px", c, v[0], TexTool.Human(v[1]), v[2]));
                            }
                        }
                    }
                    if (anyDir || paths.Count > 1) Cli.W(L.F("合计 {0} 张贴图。", total));
                    return 0;
                }

                if (cmd == "blk")
                {
                    // 诊断：打印每张贴图的块状度（>0.5 视为疑似 JPEG 来源，即"脏源"）
                    if (a.Count < 2) { Cli.W("用法: blk <卡片> [阈值]"); return 1; }
                    double thr = 0.05;
                    if (a.Count > 2) double.TryParse(a[2], out thr);
                    var psc0 = TexTool.ScanParts(a[1]);
                    int hit = 0, done = 0;
                    foreach (var part in psc0.Parts)
                        foreach (var t in part.Tex)
                        {
                            done++;
                            try
                            {
                                byte[] raw = TexTool.TextureBytes(a[1], t.Id.ToString());
                                if (raw == null) continue;
                                using (var img = TexTool.LoadTextureImage(raw, out string err0))
                                {
                                    if (img == null) continue;
                                    using (var bm = new Bitmap(img))
                                    {
                                    double bk = Resample.Blockiness(bm);
                                    if (bk > thr)
                                    {
                                        hit++;
                                        Cli.W(L.F("TexID {0,-5} {1,-10} {2}x{3}  块状度 {4:F3} {5}",
                                            t.Id, t.Cls, bm.Width, bm.Height, bk, bk > 0.5 ? "← 脏源" : ""));
                                    }
                                    }
                                }
                            }
                            catch { }
                        }
                    Cli.W(L.F("共检查 {0} 张，块状度 > {1:F2} 的 {2} 张", done, thr, hit));
                    return 0;
                }

                if (cmd == "jpegopt")
                {
                    // 无损再优化的单文件自检：jpegopt <in.jpg> <out.jpg>
                    if (a.Count < 3) { Cli.W("用法: jpegopt <in.jpg> <out.jpg>"); return 1; }
                    byte[] src = File.ReadAllBytes(a[1]);
                    byte[] dst = JpegOpt.Optimize(src);
                    if (dst == null) { Cli.W("放弃：" + JpegOpt.LastReason + "（原 " + src.Length + " 字节）"); return 0; }
                    File.WriteAllBytes(a[2], dst);
                    Cli.W(L.F("{0} → {1}   {2} → {3} 字节（{4:+#0.0%;-#0.0%}）", a[1], a[2], src.Length, dst.Length,
                        (double)(dst.Length - src.Length) / src.Length));
                    return 0;
                }

                if (cmd == "pngre")
                {
                    // 无损再压缩的单文件自检：pngre <输入.png> <输出.png> [pnga=0|1]
                    if (a.Count < 3) { Cli.W("用法: pngre <in.png> <out.png> [pnga=0|1]"); return 1; }
                    ApplyGlobalFlags(a);                       // 让 pnga= / own= 等开关在诊断命令里也生效
                    byte[] src = File.ReadAllBytes(a[1]);
                    string gk;
                    byte[] dst = PngEnc.Recompress(src, PngEnc.UseGrayAlpha, out gk);
                    if (dst == null) { Cli.W("再压缩失败（隔行/位深不支持/数据异常）→ 保持原样"); return 1; }
                    File.WriteAllBytes(a[2], dst);
                    Cli.W(L.F("{0} → {1}   {2} → {3} 字节  ({4:+#0.0%;-#0.0%}）", a[1], a[2], src.Length, dst.Length, (double)(dst.Length - src.Length) / src.Length));
                    return 0;
                }

                if (cmd == "pngtest")
                {
                    // 对比 PNG 编码器：GDI+ vs 自写（自适应滤波 + zlib-ng SmallestSize）
                    if (a.Count < 3) { Cli.W("用法: pngtest <卡片> <TexID[,TexID...]>"); return 1; }
                    string card = a[1];
                    long g = 0, m = 0;
                    foreach (var tk in a[2].Split(','))
                    {
                        int tid; if (!int.TryParse(tk.Trim(), out tid)) continue;
                        byte[] raw = TexTool.TextureBytes(card, tid.ToString());
                        if (raw == null || raw.Length == 0) { Cli.W("TexID " + tid + ": 取不到"); continue; }
                        string err;
                        var img = TexTool.LoadTextureImage(raw, out err);
                        if (img == null) { Cli.W("TexID " + tid + ": 解码失败 " + err); continue; }
                        var bmp = new System.Drawing.Bitmap(img);
                        using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); g += ms.Length; }
                        string info;
                        byte[] mine = PngEnc.Encode(bmp, out info);
                        m += mine.Length;
                        int gw; using (var ms2 = new MemoryStream()) { bmp.Save(ms2, System.Drawing.Imaging.ImageFormat.Png); gw = (int)ms2.Length; }
                        Cli.W(L.F("TexID {0,-4} 原 {1,9}  GDI+ {2,9}  自写 {3,9}  ({4:+#0.0%;-#0.0%}）  {5}",
                            tid, raw.Length, gw, mine.Length, (double)(mine.Length - gw) / gw, info));
                        bmp.Dispose(); img.Dispose();
                    }
                    Cli.W(L.F("合计：GDI+ {0}  自写 {1}  → {2:+#0.0%;-#0.0%}", g, m, g == 0 ? 0 : (double)(m - g) / g));
                    return 0;
                }

                if (cmd == "chara")
                {
                    // 列出人物卡的「角色本体 + 7 套换装 × 部位」，以及每处有多少张贴图
                    if (a.Count < 2) { Usage(); return 1; }
                    for (int ci2 = 1; ci2 < a.Count; ci2++)
                    {
                        string path = a[ci2];
                        if (!File.Exists(path)) { Cli.W("!! 找不到 " + path); continue; }
                        var psc = TexTool.ScanParts(path);
                        if (psc.Err.Length > 0) { Cli.W("[X] " + psc.Err); continue; }
                        var info = Card.ReadInfo(TexTool.Peek(path));
                        Cli.W(L.F("{0}  〖{1}〗  名称：{2}",
                            Path.GetFileName(path), info.Tag, info.Name));
                        Cli.W(L.F("  {0} 张贴图 / {1}；卡片 {2}", psc.NTex, TexTool.Human(psc.Bytes),
                            TexTool.Human(new FileInfo(path).Length)));
                        // 按组汇总
                        var byGroup = new List<int>();
                        foreach (var pi in psc.Parts)
                            if (pi.Tex.Count > 0)
                            {
                                int g = Chara.IsCharaKey(pi.SlotKey) ? Chara.KeyGroup(pi.SlotKey) : -1;
                                if (!byGroup.Contains(g)) byGroup.Add(g);
                            }
                        byGroup.Sort();
                        foreach (var g in byGroup)
                        {
                            int n = 0; long b = 0; var rows = new List<TexTool.PartInfo>();
                            foreach (var pi in psc.Parts)
                            {
                                int gg = Chara.IsCharaKey(pi.SlotKey) ? Chara.KeyGroup(pi.SlotKey) : -1;
                                if (gg != g || pi.Tex.Count == 0) continue;
                                n += pi.Tex.Count; b += pi.Bytes; rows.Add(pi);
                            }
                            Cli.W(L.F("  【{0}】{1} 个部位 / {2} 张 / {3}",
                                g < 0 ? "?" : Chara.GroupName(g), rows.Count, n, TexTool.Human(b)));
                            foreach (var pi in rows)
                                Cli.W(L.F("        {0,-34} {1,3} 张 {2,10}  材质 {3}",
                                    Chara.IsCharaKey(pi.SlotKey) ? Card.SlotName(Chara.KeySlot(pi.SlotKey)) : pi.Name,
                                    pi.Tex.Count, TexTool.Human(pi.Bytes), pi.MatText));
                        }
                        if (psc.Orphans.Count > 0)
                        {
                            long ob = 0;
                            foreach (var t in psc.Orphans) ob += t.Bytes;
                            Cli.W(L.F("  没被任何材质引用的贴图 {0} 张 / {1}", psc.Orphans.Count, TexTool.Human(ob)));
                        }
                    }
                    return 0;
                }

                if (cmd == "parts")
                {
                    // 列出一张卡里每个部位有哪些贴图（与「按部位压缩」分页同一套数据）
                    if (a.Count < 2) { Usage(); return 1; }
                    var ps = TexTool.ScanParts(a[1]);
                    if (ps.Err.Length > 0) { Cli.W("[X] " + ps.Err); return 2; }
                    Cli.W(L.F("{0}：{1} 张贴图 / {2}，有贴图的部位 {3} 个",
                        Path.GetFileName(a[1]), ps.NTex, TexTool.Human(ps.Bytes),
                        CountParts(ps)));
                    foreach (var pi in ps.Parts)
                    {
                        if (pi.Tex.Count == 0)
                        {
                            Cli.W(L.F("  {0,-24} （这张卡没用到）", pi.Name));
                            continue;
                        }
                        Cli.W(L.F("  {0,-24} {1,3} 张 {2,11}  独享 {3} 张/{4,10}  材质 {5}",
                            pi.Name, pi.Tex.Count, TexTool.Human(pi.Bytes), pi.OwnCount,
                            TexTool.Human(pi.OwnBytes), pi.MatText));
                        foreach (var t in pi.Tex)
                            Cli.W(string.Format("        TexID {0,-5} {1,-10} {2,-7} {3,10} {4,-5} {5}",
                                t.Id, t.Cls, t.Dim > 0 ? t.Dim + "px" : "?", TexTool.Human(t.Bytes),
                                t.Fmt, t.Shared ? ("共享: " + t.SlotNames) : "独享"));
                    }
                    if (ps.Orphans.Count > 0)
                    {
                        Cli.W(L.F("  没被任何部位引用的贴图 {0} 张：", ps.Orphans.Count));
                        foreach (var t in ps.Orphans)
                            Cli.W(string.Format("        TexID {0,-5} {1,-10} {2,10}", t.Id, t.Cls, TexTool.Human(t.Bytes)));
                    }
                    return 0;
                }

                if (cmd == "preset")
                {
                    // 建预设：preset <card> <out.json> [parts=top:auto:512,...] [tex=18:png:256,...] [source=...]
                    if (a.Count < 3) { Usage(); return 1; }
                    string card = a[1], outJson = a[2];
                    bool isCharaCard = Chara.IsCharaCard(TexTool.Peek(card));
                    var pf = new TexTool.Preset { Source = Path.GetFileName(card), IsChara = isCharaCard };
                    var ps0 = TexTool.ScanParts(card);
                    if (ps0.Err.Length > 0) { Cli.W("[X] " + ps0.Err); return 2; }
                    // 整卡签名指纹（同款检测的基准）。人物卡只取「本体」签名：
                    // 换装/材质因卡而异，拿全量签名比会把不同角色卡的匹配度拉成 0。
                    var sigAll = Chara.IsCharaCard(TexTool.Peek(card))
                        ? Chara.BodySigs(TexTool.Peek(card))
                        : new HashSet<string>(StringComparer.Ordinal);
                    if (sigAll.Count == 0)
                        foreach (var tx in ps0.All) foreach (var sg in tx.Sigs) sigAll.Add(sg);
                    pf.SigSet = new List<string>(sigAll);
                    pf.SigSet.Sort(StringComparer.Ordinal);
                    foreach (var arg in a)
                    {
                        string low = arg.ToLowerInvariant();
                        if (low.StartsWith("parts="))
                        {
                            foreach (var part in arg.Substring(6).Split(','))
                            {
                                var f = part.Trim().Split(':');
                                if (f.Length == 0 || f[0].Trim().Length == 0) continue;
                                int k = SlotKeyFromToken(f[0].Trim());
                                if (k < 0) { Cli.W("[!] 认不出的部位：" + f[0]); continue; }
                                pf.Parts[k] = new Rule(NormFmt(f.Length > 1 ? f[1] : null), NormSize(f.Length > 2 ? f[2] : null, 1024));
                            }
                        }
                        else if (low.StartsWith("tex="))
                        {
                            foreach (var t in arg.Substring(4).Split(','))
                            {
                                var f = t.Trim().Split(':');
                                if (f.Length == 0 || f[0].Trim().Length == 0) continue;
                                string id = f[0].Trim();
                                var pt = new TexTool.PresetTex { Id = id, Rule = new Rule(NormFmt(f.Length > 1 ? f[1] : null), NormSize(f.Length > 2 ? f[2] : null, 1024)) };
                                foreach (var tx in ps0.All)
                                    if (tx.Id != null && tx.Id.ToString() == id) pt.Sigs = new List<string>(tx.Sigs);
                                if (pt.Sigs.Count == 0) Cli.W("[!] 卡片里没有 TexID " + id + "（这条也写进预设了，但套用时只能按编号认）");
                                pf.Textures.Add(pt);
                            }
                        }
                        else if (low.StartsWith("source=")) pf.Source = arg.Substring(7);
                        else if (low.StartsWith("uniform="))
                        {
                            var f = arg.Substring(8).Trim().Split(':');
                            pf.Uniform = new Rule(NormFmt(f.Length > 0 ? f[0] : null), NormSize(f.Length > 1 ? f[1] : null, 1024));
                        }
                        else if (low.StartsWith("minmatch="))
                        {
                            pf.MinMatch = double.Parse(arg.Substring(9).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            if (pf.MinMatch > 1) pf.MinMatch /= 100.0;      // 也允许写 50 表示 50%
                        }
                    }
                    TexTool.PresetSave(pf, outJson);
                    Cli.W(L.F("已写出预设 {0}：部位规则 {1} 条，单贴图规则 {2} 条，整卡签名指纹 {3} 个，通用设置 {4}",
                        outJson, pf.Parts.Count, pf.Textures.Count, pf.SigSet.Count,
                        pf.Uniform == null ? "（无）" : pf.Uniform.Format + (pf.Uniform.Size > 0 ? "/" + pf.Uniform.Size : "/不缩放")));
                    foreach (var kv in pf.Parts)
                        Cli.W(L.F("   部位 {0,-24} {1}{2}", Card.SlotName(kv.Key), kv.Value.Format,
                            kv.Value.Size > 0 ? "/" + kv.Value.Size : "/不缩放"));
                    foreach (var t in pf.Textures)
                        Cli.W(L.F("   贴图 TexID {0,-6} {1}{2}  签名 {3}", t.Id,
                            t.Rule.Format, t.Rule.Size > 0 ? "/" + t.Rule.Size : "/不缩放",
                            t.Sigs.Count > 0 ? string.Join(" ; ", t.Sigs.ToArray()) : "（无）"));
                    return 0;
                }

                if (cmd == "compress")
                {
                    if (a.Count < 3) { Usage(); return 1; }
                    string src = a[1], dst = a[2];
                    int maxSize = a.Count > 3 ? int.Parse(a[3]) : 1024;
                    string mode = a.Count > 4 ? a[4].ToLowerInvariant() : "auto";
                    int quality = a.Count > 5 ? int.Parse(a[5]) : 90;
                    int maskSize = a.Count > 6 ? int.Parse(a[6]) : maxSize;
                    List<string> only = null;
                    for (int i = 7; i < a.Count; i++)
                        if (a[i].ToLowerInvariant().StartsWith("only="))
                        {
                            only = new List<string>();
                            foreach (var s in a[i].Substring(5).Split(','))
                                if (s.Trim().Length > 0) only.Add(s.Trim().ToLowerInvariant());
                        }
                    Dictionary<int, Rule> slotRules = ParseSlotRules(a);
                    TexTool.Preset pres = ParsePresetArg(a);
                    int gmask = ParseGroupMask(a);
                    ApplyGlobalFlags(a);
                    if (slotRules != null && SlotRulesWantsAll(a) && File.Exists(src))
                    {
                        // slots=all 必须按"这张卡里实际存在的部位"展开：
                        // 饰品槽是另一套编号（ObjectType 2），只覆盖 9 个服装槽会漏掉整块饰品。
                        var pss = TexTool.ScanParts(src);
                        var d = new Dictionary<int, Rule>();
                        foreach (var pi in pss.Parts)
                            if (pi.Tex.Count > 0) d[pi.SlotKey] = new Rule("auto", 1024);
                        if (d.Count > 0) slotRules = d;
                    }
                    if (mode == "maintex") { mode = "auto"; only = new List<string> { "maintex" }; }
                    string fname = Path.GetFileName(src);
                    Dictionary<string, Rule> planC = null;
                    for (int ci = 1; ci < a.Count; ci++)
                        if (a[ci].ToLowerInvariant().StartsWith("plan=")) planC = PlanIo.Load(a[ci].Substring(5));
                    // 只给了 preset= 时，用**预设里的类型表** —— 否则 preset 只带部位/单贴图规则，
                    // 类型表会悄悄退回位置参数的默认值，和"套用预设"的直觉不符（实测踩过）。
                    if (planC == null && pres != null)
                    {
                        string pj = PresetJsonArg(a);
                        Dictionary<string, Rule> pp; int pq; bool ppa;
                        if (!string.IsNullOrEmpty(pj) && PresetIo.TryReadPlan(pj, out pp, out pq, out ppa))
                        {
                            planC = pp;
                            Cli.W(L.F("使用预设里的类型表（{0} 个类；未显式给 plan=）", pp.Count));
                        }
                    }
                    var r = TexTool.Repack(src, dst, planC, maxSize, mode, quality, maskSize, only, true, slotRules, pres, gmask,
                                           delegate (string s) { Cli.W(s); }, null, null);
                    var partList1 = new List<string>();
                    foreach (var sk in r.Skipped) partList1.Add(fname + ": " + sk);
                    if (r.Rc == 0)
                    {
                        string msg;
                        bool ok = TexTool.QuickVerify(dst, out msg);
                        Cli.W(ok ? "[OK] 校验通过: " + msg : "[X] 校验失败: " + msg);
                        if (r.Copied)
                        {
                            // 原样复制的卡不算"压缩成功"（体积没变），单独报一句
                            Cli.W("本卡没有压缩：处理不了 → 已原样复制到输出目录（体积不变）。");
                            TexTool.PrintSummary(Cli.W, 0, 0, 0, 0, 0, null, null, partList1);
                            return 0;
                        }
                        TexTool.PrintSummary(Cli.W, ok ? 1 : 0, 0, ok ? 0 : 1, r.CardBefore, r.CardAfter,
                            null, ok ? null : new List<string> { fname + " —— 产出校验失败: " + msg }, partList1);
                        return ok ? 0 : 1;
                    }
                    string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                    if (r.Rc == 6)
                    {
                        Cli.W("[跳过] " + why + "（要强行压就加 force=1，会改用预设里的通用设置）");
                        TexTool.PrintSummary(Cli.W, 0, 1, 0, 0, 0,
                            new List<string> { fname + " —— " + why }, null, partList1);
                        return 0;
                    }
                    Cli.W("[X] 失败 —— " + why);
                    TexTool.PrintSummary(Cli.W, 0, r.Rc == 3 ? 1 : 0, r.Rc == 3 ? 0 : 1, 0, 0,
                        r.Rc == 3 ? new List<string> { fname + " —— " + why } : null,
                        r.Rc == 3 ? null : new List<string> { fname + " —— " + why }, partList1);
                    return r.Rc;
                }

                if (cmd == "batch")
                {
                    if (a.Count < 2) { Usage(); return 1; }
                    string inDir = a[1];
                    var pos = new List<string>();
                    Dictionary<string, Rule> plan = null;
                    string suffix = "[zip]";                 // 输出卡片名：原名[zip].png
                    bool recurse = true;                     // 默认连子文件夹一起读
                    for (int i = 2; i < a.Count; i++)
                    {
                        string low = a[i].ToLowerInvariant();
                        if (low.StartsWith("plan=")) { plan = PlanIo.Load(a[i].Substring(5)); continue; }
                        if (low.StartsWith("recurse=")) { recurse = a[i].Substring(8) != "0"; continue; }
                        if (low.StartsWith("suffix=")) { suffix = a[i].Substring(7); continue; }
                        if (low.StartsWith("preset=")) continue;          // 由 ParsePresetArg 统一读
                        if (low.StartsWith("force=") || low == "force") continue;
                        if (low.StartsWith("minmatch=")) continue;
                        if (low.StartsWith("log=")) continue;
                        pos.Add(a[i]);
                    }
                    if (!Directory.Exists(inDir)) { Cli.W("输入目录不存在: " + inDir); return 1; }
                    // 位置参数：out_dir(可省) max_size mode quality mask_size；纯数字的第一项按 max_size 认
                    string outDir = null;
                    if (pos.Count > 0 && !IsAllDigits(pos[0])) outDir = pos[0];
                    int pi = outDir == null ? 0 : 1;
                    if (outDir == null) outDir = TexTool.DefaultOut(inDir);
                    int maxSize = pos.Count > pi ? int.Parse(pos[pi]) : 1024; pi++;
                    string mode = pos.Count > pi ? pos[pi].ToLowerInvariant() : "auto"; pi++;
                    int quality = pos.Count > pi ? int.Parse(pos[pi]) : 90; pi++;
                    int maskSize = pos.Count > pi ? int.Parse(pos[pi]) : maxSize; pi++;
                    List<string> only = null;
                    if (mode == "maintex") { mode = "auto"; only = new List<string> { "maintex" }; }
                    TexTool.Preset presB = ParsePresetArg(a);
                    int gmaskB = ParseGroupMask(a);
                    ApplyGlobalFlags(a);
                    if (plan == null && presB != null)
                    {
                        string pj = PresetJsonArg(a);
                        Dictionary<string, Rule> pp; int pq; bool ppa;
                        if (!string.IsNullOrEmpty(pj) && PresetIo.TryReadPlan(pj, out pp, out pq, out ppa))
                        {
                            plan = pp;
                            Cli.W(L.F("使用预设里的类型表（{0} 个类；未显式给 plan=）", pp.Count));
                        }
                    }
                    if (presB != null)
                        Cli.W(L.F("套用预设：{0}（部位规则 {1} 条，单贴图规则 {2} 条，来自 {3}）",
                            "", presB.Parts.Count, presB.Textures.Count, presB.Source));
                    int presetHit = 0, presetMiss = 0;

                    var cards = TexTool.Enumerate(inDir, outDir, recurse);
                    Cli.W(L.F("输入 {0}（{1}子文件夹）→ 输出 {2}",
                        inDir, recurse ? "含" : "不含", outDir));
                    Cli.W(L.F("找到 {0} 个 .png；输出名后缀 \"{1}\"", cards.Count, suffix));
                    if (cards.Count == 0) Cli.W("（没有 .png 可处理）");
                    Directory.CreateDirectory(outDir);
                    TexTool.MarkOut(outDir);
                    // ---- 卡级并行 ----
                    // 一张卡之间完全独立（各读各的卡、各写各的文件），所以可以并行跑。
                    // 并发数按内存兜底：一张 200 MB 的卡峰值约 1.1 GB，默认 4 路已经接近 4.5 GB。
                    // 想更稳就 jobs=2，想榨满机器就 jobs=8（内存够的话）。
                    int jobs = ParseJobs(a, cards.Count);
                    if (jobs > 1)
                        Cli.W(L.F("并行处理：{0} 路（jobs=N 可改；每路约需 0.5~1.5 GB 内存）", jobs));
                    var outs = new BatchRow[cards.Count];
                    System.Threading.Tasks.Parallel.For(0, cards.Count,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = jobs },
                        delegate (int ci)
                        {
                            var cf = cards[ci];
                            var o = new BatchRow();
                            o.Fn = Path.GetFileName(cf.Src);
                            string f = cf.Src;
                            string stem = Path.GetFileNameWithoutExtension(f);
                            string rd = cf.Rel.Length > 0 ? Path.Combine(outDir, cf.Rel) : outDir;
                            try { Directory.CreateDirectory(rd); } catch { }
                            o.Dst = Path.Combine(rd, stem + suffix + ".png");
                            try
                            {
                                o.R = TexTool.Repack(f, o.Dst, plan, maxSize, mode, quality, maskSize, only, true,
                                                     null, presB, gmaskB, null, null, null);
                            }
                            catch (Exception ex)
                            {
                                // 连卡片都没读进来（权限/占用/坏文件）也要进失败清单
                                o.Err = ex.Message;
                            }
                            outs[ci] = o;
                        });

                    long tb = 0, ta = 0;
                    int nok = 0, nfail = 0, ncopy = 0;
                    var failList = new List<string>();
                    var skipList = new List<string>();
                    var partList = new List<string>();
                    foreach (var o in outs)
                    {
                        string fn = o.Fn;
                        if (o.Err != null)
                        {
                            Cli.W(L.F("{0,-52} 失败 —— {1}", fn, o.Err));
                            failList.Add(fn + " —— 读取/处理时异常: " + o.Err);
                            nfail++;
                            continue;
                        }
                        var r = o.R;
                        string d = o.Dst;
                        presetHit += r.PresetMatched; presetMiss += r.PresetMissed.Count;
                        foreach (var sk in r.Skipped) partList.Add(fn + ": " + sk);
                        if (r.Rc == 3 || r.Rc == 6)
                        {
                            string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Cli.W(L.F("{0,-52} 跳过（{1}）", fn, r.Rc == 6 ? "不是同款服装" : "无贴图"));
                            skipList.Add(fn + " —— " + why);
                            continue;
                        }
                        if (r.Rc != 0)
                        {
                            string why = string.IsNullOrEmpty(r.Reason) ? RepackResult.RcText(r.Rc) : r.Reason;
                            Cli.W(L.F("{0,-52} 失败 —— {1}", fn, why));
                            failList.Add(fn + " —— " + why);
                            nfail++;
                            continue;
                        }
                        if (r.Copied)
                        {
                            // 【处理不了的卡直接复制】复制出来的是**原卡**，不算压缩产物：
                            // 不进成功数、也不计体积（否则汇总里会出现"压了但 0%"的假象）。
                            ncopy++;
                            Cli.W(L.F("{0,-52} 原样复制（处理不了的卡，体积不变）", fn));
                            continue;
                        }
                        string msg;
                        bool ok = TexTool.QuickVerify(d, out msg);
                        if (ok) nok++; else nfail++;
                        tb += r.CardBefore; ta += r.CardAfter;
                        if (!ok) failList.Add(fn + " —— 产出校验失败: " + msg);
                        Cli.W(string.Format("{0,-52} {1,11} {2,11} {3,7:0.0}% jpg={4,-4} {5}",
                            fn, r.CardBefore, r.CardAfter,
                            100.0 * r.CardAfter / Math.Max(1, r.CardBefore), r.NJpg, ok ? "PASS" : "FAIL " + msg));
                    }
                    if (ncopy > 0) Cli.W(L.F("其中 {0} 张是「处理不了的卡 → 原样复制」（未压缩，不计入上面的体积）。", ncopy));
                    Cli.W(L.F("合计：成功 {0} / 失败 {1}；{2} -> {3}（{4:0.0}%）",
                        nok, nfail, tb, ta, 100.0 * ta / Math.Max(1, tb)));
                    if (presB != null)
                        Cli.W(L.F("预设落地：命中 {0} 张单贴图规则；未匹配 {1} 张（换版本/换材质的卡会少一些，属正常）",
                            presetHit, presetMiss));
                    TexTool.PrintSummary(Cli.W, nok, skipList.Count, nfail, tb, ta, skipList, failList, partList);
                    Cli.W("输出目录: " + outDir);
                    return nfail > 0 ? 1 : 0;
                }

                if (cmd == "info")
                {
                    if (a.Count < 2) { Usage(); return 1; }
                    for (int i = 1; i < a.Count; i++)
                    {
                        ScanResult s = null;
                        try { s = TexTool.Scan(a[i]); } catch { }
                        Cli.W(TexTool.Describe(a[i], s, Card.ReadInfo(TexTool.Peek(a[i]))));
                    }
                    return 0;
                }

                if (cmd == "uitest")
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate (object se, System.Threading.ThreadExceptionEventArgs te)
                    { Cli.W("[UIEXC] " + te.Exception); };
                    AppDomain.CurrentDomain.UnhandledException += delegate (object se, UnhandledExceptionEventArgs ue)
                    { Cli.W("[DOMEXC] " + ue.ExceptionObject); };
                    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate (object se, System.Threading.Tasks.UnobservedTaskExceptionEventArgs ue)
                    { Cli.W("[TASKEXC] " + ue.Exception); };
                    MainForm.LangOverride = Lang.Zh;   // v1.0.2：自检输出固定中文（脚本靠中文关键字 grep）
                    // langforce=en → 连"建界面之前"就用这个语言（用来实测日志/状态栏的翻译）
                    foreach (var av in argv) if (av.StartsWith("langforce="))
                    {
                        var lv = av.Substring(10);
                        MainForm.LangOverride = lv.StartsWith("en") ? Lang.En
                            : (lv.StartsWith("ja") ? Lang.Ja : (lv.StartsWith("ko") ? Lang.Ko : Lang.Zh));
                    }
                    // 无头自检拖入逻辑：建一个隐藏窗口，走 AcceptPaths，再把状态/日志打出来
                    // 加 run=1 → 接着走界面「开始压缩」的同一条路径（DoRun），验证 GUI 压缩全链路
                    if (a.Count < 2) { Usage(); return 1; }
                    bool doRun = false, tab2 = false, partsRun = false, tab3 = false, g3run = false;
                    int g3group = -1, g3onlygroup = -1, g3prev = -1, p2prev = -1;
                    string g3uni = null;
                    string p2only = null, p2fmt = null, p2max = null, p2out = null, allsim = null, copyskip = null;
                    string texset = null, savepf = null, loadpf = null, batchdir = null;
                    string uniform = null, allparts = null, alltex = null, grp = null;
                    string presave = null, p2savepf = null, g3savepf = null, setplan = null;
                    int presel = -1;
                    string predef = null;
                    string big = null, big3 = null, bigsnap = null;
                    string sorttex2 = null;
                    bool g3part = false;
                    bool grpshow = false;
                    string sorttex3 = null;
                    int? grpmax = null;
                    string upset = null;         // v2.33：upset=maintex=2,matcap=4 → 类型表「放大」列
                    bool? showup = null;         // v2.34：showup=0/1 → 放大选项收起/展开（三页一起）
                    string upall = null;         // v2.36：upall=×2 → 设全局倍率并点「应用放大设置」
                    string dragcol = null;       // v2.37：dragcol=tex:ga:60 → 模拟拖宽某列并回读
                    string typeuni = null;       // v2.38：typeuni2=maintex / typeuni3=maintex → 走「应用到该类型贴图」
                    string colact = null;        // v2.38：colact=save/reset → 「记住列宽」/「恢复默认列宽」
                    string findtext = null;      // v1.0：按文字找控件并打印父链（排查看不见）
                    string lang = null;
                    string realsnap = null;   // v1.1.7：realsnap=路径 → 切换语言后用 PrintWindow 抓真实窗口
                    int tabbench = 0;          // 1.0.1：lang=ja → 在界面建好后切一次语言（抓崩溃）
                    bool? showtype = null;       // v2.38：showtype=0/1 → 按类型统一那一栏展开/收起
                    bool? colorkernel = null;      // v2.32：colorkernel=0/1 → 设「彩色贴图也走面积平均」并回读状态
                    string sort2 = null, sort3 = null;
                    int hold = 0;
                    bool prelist = false, rects = false, rowsplit = false;
                    string snap = null;
                    int snaptab = -1;
                    bool force = false, sub = true;
                    var paths = new List<string>();
                    for (int i = 1; i < a.Count; i++)
                    {
                        string low = a[i].ToLowerInvariant();
                        if (low == "run=1") { doRun = true; continue; }
                        if (low == "tab2=1") { tab2 = true; continue; }
                        if (low == "partsrun=1") { partsRun = true; continue; }
                        if (low == "tab3=1") { tab3 = true; continue; }
                        if (low == "g3run=1") { g3run = true; continue; }
                        if (low.StartsWith("g3group=")) { g3group = int.Parse(a[i].Substring(8)); continue; }
                        if (low.StartsWith("g3only=")) { g3onlygroup = int.Parse(a[i].Substring(7)); continue; }
                        if (low.StartsWith("g3uni=")) { g3uni = a[i].Substring(6); continue; }
                        if (low.StartsWith("upset=")) { upset = a[i].Substring(6); continue; }   // v2.33：类型表「放大」列
                        if (low.StartsWith("showup=")) { showup = a[i].Substring(7) != "0"; MainForm.UpUiWanted = showup.Value; continue; }   // 解析时就设：截图/布局 dump 都在窗体出现之前
                        if (low.StartsWith("upall=")) { upall = a[i].Substring(6); continue; }
                        if (low.StartsWith("dragcol=")) { dragcol = a[i].Substring(8); continue; }
                        if (low.StartsWith("typeuni")) { typeuni = a[i]; continue; }
                        if (low.StartsWith("colact=")) { colact = a[i].Substring(7); continue; }
                        if (low.StartsWith("findtext=")) { findtext = a[i].Substring(9); continue; }
                        if (low.StartsWith("lang=")) { lang = a[i].Substring(5); continue; }
                        if (low.StartsWith("tabbench=")) { tabbench = int.Parse(a[i].Substring(9)); continue; }
                        if (low.StartsWith("theme=")) { Theme.Override = a[i].Substring(6).StartsWith("dark") ? ThemeKind.Dark : ThemeKind.Light; continue; }
                        if (low.StartsWith("showtype=")) { showtype = a[i].Substring(9) != "0"; MainForm.TypeUniWanted = showtype.Value; continue; }
                        // "colorkernel=" 是 12 个字符（colorkernel + =），别数错 —— 数错会安静地当成"开"。
                        if (low.StartsWith("colorkernel=")) { colorkernel = a[i].Substring(12) != "0"; continue; }
                        // kernel=bicubic/area 在 uitest 里也要能设（验证「细节保护」勾掉时彩色那项跟着灰掉）
                        if (low == "kernel=bicubic") { Resample.DetailMode = false; continue; }
                        if (low == "kernel=area") { Resample.DetailMode = true; continue; }
                        if (low.StartsWith("g3prev=")) { g3prev = int.Parse(a[i].Substring(7)); continue; }
                        if (low.StartsWith("p2prev=")) { p2prev = int.Parse(a[i].Substring(7)); continue; }
                        if (low.StartsWith("p2only=")) { p2only = low.Substring(7); continue; }
                        if (low.StartsWith("p2fmt=")) { p2fmt = a[i].Substring(6); continue; }
                        if (low.StartsWith("p2max=")) { p2max = a[i].Substring(6); continue; }
                        if (low.StartsWith("p2out=")) { p2out = a[i].Substring(6); continue; }
                        if (low.StartsWith("allsim=")) { allsim = a[i].Substring(7) != "0" ? "1" : "0"; continue; }
                        if (low.StartsWith("copyskip=")) { copyskip = a[i].Substring(9) != "0" ? "1" : "0"; continue; }
                        if (low.StartsWith("texset=")) { texset = a[i].Substring(7); continue; }
                        if (low.StartsWith("savepf=")) { savepf = a[i].Substring(7); continue; }
                        if (low.StartsWith("loadpf=")) { loadpf = a[i].Substring(7); continue; }
                        if (low.StartsWith("batchdir=")) { batchdir = a[i].Substring(9); continue; }
                        if (low.StartsWith("uniform=")) { uniform = a[i].Substring(8); continue; }
                        if (low == "force=1") { force = true; continue; }
                        if (low == "sub=0") { sub = false; continue; }
                        if (low == "sub=1") { sub = true; continue; }
                        if (low.StartsWith("grp=")) { grp = a[i].Substring(4); continue; }
                        if (low.StartsWith("presave=")) { presave = a[i].Substring(8); continue; }
                        if (low.StartsWith("presel=")) { presel = int.Parse(a[i].Substring(7)); continue; }
                        if (low.StartsWith("predef=")) { predef = a[i].Substring(7); continue; }
                        if (low.StartsWith("big=")) { big = a[i].Substring(4); continue; }
                        if (low.StartsWith("big3=")) { big3 = a[i].Substring(5); continue; }
                        if (low.StartsWith("bigsnap=")) { bigsnap = a[i].Substring(8); continue; }
                        if (low.StartsWith("hold=")) { hold = int.Parse(a[i].Substring(5)); continue; }
                        if (low.StartsWith("sort2=")) { sort2 = a[i].Substring(6); continue; }
                        if (low.StartsWith("sorttex2=")) { sorttex2 = a[i].Substring(9); continue; }
                        if (low == "g3part=1") { g3part = true; continue; }
                        if (low == "grpshow=1") { grpshow = true; continue; }
                        if (low.StartsWith("sorttex3=")) { sorttex3 = a[i].Substring(9); continue; }
                        if (low.StartsWith("grpmax=")) { grpmax = int.Parse(a[i].Substring(7)); continue; }
                        if (low.StartsWith("sort3=")) { sort3 = a[i].Substring(6); continue; }
                        if (low.StartsWith("p2savepf=")) { p2savepf = a[i].Substring(9); continue; }
                        if (low.StartsWith("g3savepf=")) { g3savepf = a[i].Substring(9); continue; }
                        if (low == "prelist=1") { prelist = true; continue; }
                        if (low == "rowsplit=1") { rowsplit = true; continue; }
                        if (low.StartsWith("snap=")) { snap = a[i].Substring(5); continue; }
                        if (low.StartsWith("realsnap=")) { realsnap = a[i].Substring(9); continue; }
                        if (low.StartsWith("snaptab=")) { snaptab = int.Parse(a[i].Substring(8)); continue; }
                        if (low == "rects=1") { rects = true; continue; }
                        if (low.StartsWith("setplan=")) { setplan = a[i].Substring(8); continue; }
                        if (low.StartsWith("allparts=")) { allparts = a[i].Substring(9); continue; }
                        if (low.StartsWith("alltex=")) { alltex = a[i].Substring(7); continue; }
                        paths.Add(a[i]);
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var f = new MainForm();
                    f.Show();
                    // 自检看门狗：任何弹出来的模态框都自动关掉。
                    // 否则只要有一处弹出确认框，无头自检就会卡死，还会把窗口留在用户桌面上。
                    var dlgWatchdog = StartDialogWatchdog();
                    Cli.W("TABS   : " + f.UiTabCount + " 页 —— " + f.UiTabTitle(0) + " / " + f.UiTabTitle(1));
                    if (grpmax != null)
                    {
                        // grpmax=N → 按 N 套换装重建第 1 页组过滤（验证"超过 7 套"的卡）。
                        // 放在 tab 分支之前：组过滤属于第 1 页，和 tab2/tab3 自检互不干扰。
                        Cli.W("--- 第 1 页人物卡组过滤：按 N 套重建 ---");
                        if (grpshow) f.UiGrpShow(true);        // 展开那一行，方便截图核对
                        Cli.W("GRPMAX : " + f.UiGrpRebuild(grpmax.Value));
                        f.UiGrpSet("0,3," + grpmax.Value);
                        Cli.W("GRPSET : " + f.UiGrpDump);
                    }
                    if (tab2)
                    {
                        // 第 2 页自检：读部位 → 打印部位表 + 明细 → 按部位压缩
                        f.UiSelectTab(1);
                        Cli.W("SHOWUP2: " + f.UiShowUp(null));   // 该页已选中 → 可见性读得准
                        string card = paths.Count > 0 ? paths[0] : "";
                        if (Directory.Exists(card))
                        {
                            var l = TexTool.Enumerate(card, null, false);
                            card = l.Count > 0 ? l[0].Src : card;
                        }
                        f.UiReadStatReset();
                        f.UiPartsLoad(card);
                        {   // 读卡已改成后台线程 → 自检要等它结束
                            var swp = System.Diagnostics.Stopwatch.StartNew();
                            while (f.UiPartsBusy && swp.ElapsedMilliseconds < 120000) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            while (f.UiPartsBusy && swp.ElapsedMilliseconds < 120000) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            Application.DoEvents();
                            Cli.W(L.F("LOADWAIT: 读部位用了 {0}ms（后台线程）", swp.ElapsedMilliseconds));
                        }
                        if (p2out != null) f.UiPartsOutSet(p2out);
                        f.UiNoConfirm(true);                                  // 无头自检：不弹确认框
                        if (allsim != null) f.UiAllSimilar(allsim == "1");
                        if (copyskip != null) f.UiCopySkip(copyskip == "1");
                        Application.DoEvents();
                        Cli.W("ALLSIM  : " + f.UiAllSimilarDump);
                        Cli.W("LAYOUT : " + f.UiLayoutDump);
                        Cli.W("CLIP   : " + f.UiClippedText());
                        if (p2only != null)
                        {
                            // p2only=top,8 → 只勾这些部位；p2fmt/p2max 套用到勾上的行
                            var want = new List<int>();
                            foreach (var tk in p2only.Split(','))
                            {
                                int sk = SlotKeyFromToken(tk.Trim());
                                if (sk >= 0) want.Add(sk);
                            }
                            var psNow = TexTool.ScanParts(card);
                            for (int i = 0; i < psNow.Parts.Count; i++)
                            {
                                var pi = psNow.Parts[i];
                                bool on = want.Contains(pi.SlotKey) && pi.Tex.Count > 0;
                                f.UiPartsSetRow(i, on, p2fmt ?? "自动", p2max ?? "1024");
                            }
                            Application.DoEvents();
                        }
                        Cli.W("CARDP   : " + card);
                        Cli.W("READSTAT: " + f.UiReadStat);
                        Cli.W("P2STAT  : " + f.UiPartsStatus);
                        Cli.W("P2OUT   : " + f.UiPartsOutDir);
                        Cli.W("--- 部位表（启用|部位|贴图数|大小|独享|处理方式|最大边）---");
                        Cli.W(f.UiPartsGridDump().TrimEnd());
                        f.UiPartsSelectRow(0);
                        Application.DoEvents();
                        if (texset != null)                        {
                            // texset=行号:格式:最大边（同一行的多张贴图批量设）→ 单张贴图覆盖
                            var f2 = texset.Split(':');
                            int row = int.Parse(f2[0]);
                            f.UiPartsSelectRow(row);
                            Application.DoEvents();
                            string tf = f2.Length > 1 ? f2[1] : "跟随部位";
                            string ts = f2.Length > 2 ? f2[2] : "跟随部位";
                            int n = 0;
                            foreach (var line in f.UiPartsTexDump().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) n++;
                            for (int i = 0; i < n; i++) f.UiTexSetRow(i, tf, ts);
                            Application.DoEvents();
                            Cli.W("TEXSET  : 第 " + row + " 行部位的全部贴图 → " + tf + "/" + ts + "（" + n + " 张）");
                        }
                        Cli.W("--- 选中部位的贴图明细（TexID|类型|尺寸|字节|原格式|共用|处理方式|最大边|最终处理）---");
                        Cli.W(f.UiPartsTexDump().TrimEnd());
                        Cli.W("TEXOV   : " + f.UiTexOvDump());
                        if (uniform != null)
                        {
                            // v2.21：「通用设置」已从界面删除，这个选项只回报当前的不匹配策略
                            Cli.W("UNIFORM : （通用设置已删除）" + f.UiOptionDump());
                        }
                        if (allparts != null)
                        {
                            // allparts=PNG:512[:锐化:灰度] → 一键应用到所有部位（后两项可选，验证 v2.20 新列）
                            var af = allparts.Split(':');
                            // allparts=格式:边长[:锐化[:锐化算法[:灰度]]]
                            f.UiSetAllParts(af[0], af.Length > 1 ? af[1] : "1024",
                                            af.Length > 2 ? af[2] : null,
                                            af.Length > 3 ? af[3] : null,
                                            af.Length > 4 ? af[4] : null,
                                            af.Length > 5 ? af[5] : null);   // v2.34：第 6 项 = 放大
                            Application.DoEvents();
                            Cli.W("ALLPARTS: " + allparts + "  " + f.UiLastAllParts);
                            Cli.W("--- 一键统一后的部位表 ---");
                            Cli.W(f.UiPartsGridDump().TrimEnd());
                        }
                        if (alltex != null)
                        {
                            // alltex=JPEG:512[:锐化:灰度] → 一键应用到当前部位的全部贴图
                            var tf2 = alltex.Split(':');
                            f.UiSetAllTex(tf2[0], tf2.Length > 1 ? tf2[1] : "1024",
                                          tf2.Length > 2 ? tf2[2] : null,
                                          tf2.Length > 3 ? tf2[3] : null,
                                          tf2.Length > 4 ? tf2[4] : null,
                                          tf2.Length > 5 ? tf2[5] : null);   // v2.34：第 6 项 = 放大
                            Application.DoEvents();
                            Cli.W("ALLTEX  : " + alltex + "  → " + f.UiTexOvDump());
                        }
                        if (savepf != null)
                        {
                            f.UiPresetSave(savepf);
                            Cli.W("SAVEPF  : " + savepf + "  " + (File.Exists(savepf) ? new FileInfo(savepf).Length + " 字节" : "没写出来"));
                        }
                        if (loadpf != null)
                        {
                            f.UiPresetLoad(loadpf);
                            Application.DoEvents();
                            f.UiPartsSelectRow(0);
                            Application.DoEvents();
                            Cli.W("LOADPF  : " + loadpf);
                            Cli.W("P2STAT  : " + f.UiPartsStatus);
                            Cli.W("TEXOV   : " + f.UiTexOvDump());
                            Cli.W("--- 载入预设后的部位表 ---");
                            Cli.W(f.UiPartsGridDump().TrimEnd());
                            Cli.W("--- 载入预设后的明细（第 1 行部位）---");
                            Cli.W(f.UiPartsTexDump().TrimEnd());
                        }
                        if (p2prev >= 0)
                        {
                            Cli.W("--- 第 2 页缩略图预览 ---");
                            for (int i = 0; i < p2prev; i++)
                            {
                                if (i >= f.UiTexRows) break;
                                Cli.W("PREV2   : " + f.UiP2PreviewAt(i));
                            }
                        }
                        if (rowsplit)
                        {
                            Cli.W("--- 预览框上下拖动 ---");
                            Cli.W(L.F("PREVH   : 初始 {0}", f.UiPreviewHeight(2)));
                            Cli.W(L.F("PREVH   : 往上拖 60 → 行高 {0}", f.UiDragRowSplitter(2, -60)));
                            Application.DoEvents();
                            Cli.W(L.F("PREVH   : 实际预览框 {0}", f.UiPreviewHeight(2)));
                            Cli.W(L.F("PREVH   : 往下拖 100 → 行高 {0}", f.UiDragRowSplitter(2, 100)));
                            Application.DoEvents();
                            Cli.W(L.F("PREVH   : 实际预览框 {0}", f.UiPreviewHeight(2)));
                            Cli.W(L.F("PREVH3  : 第 3 页往上拖 50 → 行高 {0}", f.UiDragRowSplitter(3, -50)));
                        }
                        if (big != null)
                        {
                            // big=行号[:+/-滚轮次数] → 开独立悬浮预览窗、换行、滚轮缩放、关闭
                            var bf = big.Split(':');
                            int brow = int.Parse(bf[0]);
                            int wheel = bf.Length > 1 ? int.Parse(bf[1]) : 0;
                            Cli.W("--- 独立悬浮预览窗（第 2 页）---");
                            Cli.W("BIGOPEN : " + f.UiBigOpen(false, brow));
                            Cli.W("BIGSAME: " + f.UiSelectTexRow(false, brow));           // 再点同一行（跟随 + 缓存路径）
                            if (brow + 1 < f.UiTexRows) Cli.W("BIGNEXT: " + f.UiSelectTexRow(false, brow + 1));
                            if (brow + 1 < f.UiTexRows) Cli.W("BIGNEX2: " + f.UiSelectTexRow(false, brow + 1));   // 同一行两次
                            for (int i = 0; i < Math.Abs(wheel); i++)
                                Cli.W(string.Format("BIGWHEEL({0}) : {1}", i + 1, f.UiBigWheel(wheel > 0 ? 120 : -120)));
                            if (bigsnap != null) Cli.W("BIGSNAP: " + f.UiSnapBig(bigsnap));
                            if (hold > 0) Cli.W("BIGKEEP: hold>0，悬浮窗留着不关（给人看/截屏）");
                            else Cli.W("BIGCLOSE: " + f.UiBigClose());
                        }
                        if (p2savepf != null)
                        {
                            Cli.W("--- 第 2 页保存预设 → preset\\coordinate ---");
                            string p2p = f.UiP2SavePreset(p2savepf);
                            Cli.W("P2SAVE : " + p2p);
                            if (File.Exists(p2p))
                            {
                                Cli.W("P2SIZE : " + new FileInfo(p2p).Length + " 字节");
                                Cli.W("P2PEEK :\r\n" + f.UiPrePeek(p2p));
                            }
                            Cli.W("P2LIST :\r\n" + MainForm.UiPresetList(PresetIo.KindCoord, 48));
                        }
                        if (batchdir != null)
                        {
                            f.UiSubfolders(sub);
                            if (force) f.UiCopySkip(true);          // v2.21：force=1 → 改成"不匹配就原样复制"
                            Cli.W("OPTION  : " + f.UiOptionDump());
                            f.UiBatchPreset(batchdir);
                            var swb = System.Diagnostics.Stopwatch.StartNew();
                            while (swb.ElapsedMilliseconds < 600000)
                            {
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(100);
                                if (f.UiPartsLog.Contains("批量完成")) break;
                                if (f.UiPartsLog.Contains("运行出错")) break;
                            }
                            Application.DoEvents();
                            Cli.W("--- 界面批量套用预设 ---");
                            foreach (var ln in f.UiPartsLog.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                if (ln.Contains("批量完成") || ln.Contains("预设落地") || ln.Contains("成功 ") || ln.Contains("[X]"))
                                    Cli.W("BATCH   : " + ln);
                        }
                        if (partsRun)
                        {
                            Cli.W("--- 走界面「按部位压缩」(DoPartsRun) ---");
                            f.UiPartsRun();
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();
                            while (sw2.ElapsedMilliseconds < 300000)
                            {
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(50);
                                if (!f.UiP2Busy) break;                  // 部位压缩结束（与语言无关）
                                if (!f.UiPartsStatus.StartsWith("压缩") && f.UiPartsLog.Contains("完成：")) break;
                                if (f.UiPartsLog.Contains("失败") || f.UiPartsStatus.StartsWith("失败")) break;
                            }
                            Application.DoEvents();
                            Cli.W("P2STAT2 : " + f.UiPartsStatus);
                            Cli.W("P2LOG   :\r\n" + f.UiPartsLog.TrimEnd());
                            Cli.W("RUNSTAT2: " + f.UiPartsStatus);
                        }
                        if (snap != null)
                        {
                            Cli.W("SNAP   : " + f.UiSnap(snap, snaptab));
                            Cli.W("CLIPPED:\r\n" + f.UiClippedText());
                            if (rects) Cli.W("RECTS  :\r\n" + f.UiRectDump(snaptab < 0 ? 0 : snaptab));
                        }
                        if (sort2 != null)
                        {
                            // sort2=列名[:点击次数] → 模拟点第 2 页部位表表头（真实点击同一条路）
                            var sf = sort2.Split(':');
                            int n = sf.Length > 1 ? int.Parse(sf[1]) : 1;
                            Cli.W("--- 第 2 页部位表排序（点表头）---");
                            for (int k = 1; k <= n; k++)
                                Cli.W(string.Format("SORT2/{0}: {1}", k, f.UiSortParts(sf[0], 1)));
                            Cli.W("SORT2R : " + f.UiSortParts(null, 0));
                            Cli.W("SORT2P : " + f.UiRefillParts());       // 重填表后排序还在不在
                        }
                        if (sorttex2 != null)
                        {
                            // sorttex2=列名[:点击次数] → 点第 2 页「贴图明细」表头（v2.30：TexID/尺寸/大小 按数值排）
                            var sf = sorttex2.Split(':');
                            int n = sf.Length > 1 ? int.Parse(sf[1]) : 1;
                            Cli.W("--- 第 2 页贴图明细排序（点表头）---");
                            for (int k = 1; k <= n; k++)
                                Cli.W(string.Format("SORTT2/{0}: {1}", k, f.UiSortTex(false, sf[0], 1)));
                        }
                        // v2.37：列宽自检（tab2/tab3 段末尾就 return 了，插在这里最合适；
                        // 别插到 if 的"条件"与"函数体"之间 —— 那样后面的块会无条件执行）
                        Cli.W("GRIDW  : " + f.UiGridWidths());
                    if (typeuni != null) Cli.W("TYPEUNI: " + f.UiTypeUni(typeuni.StartsWith("typeuni3"), typeuni.Substring(8)));
                    Cli.W("TYPEVIS: " + f.UiTypeVisible(showtype));
                        if (dragcol != null)
                        {
                            var dc = dragcol.Split(':');
                            int ddx = dc.Length > 2 ? int.Parse(dc[2]) : 60;
                            Cli.W("DRAGCOL: " + f.UiDragCol(dc[0], dc[1], ddx));
                            Cli.W("DRAGCOL: " + f.UiDragCol(dc[0], dc[1], ddx));
                            Cli.W("GRIDW2 : " + f.UiGridWidths());
                        }
                    if (colact != null) Cli.W("COLACT : " + f.UiColWidths(colact));
                    if (findtext != null) Cli.W("FINDTX : " + f.UiFindTrace(findtext));
                    if (tabbench > 0) Cli.W("TABBEN : " + f.UiTabBench(tabbench));
                    if (realsnap != null) Cli.W("REALSNAP: " + f.UiSnapReal(realsnap));
                    Cli.W("LANGRECT: " + f.UiLangPanelRect());
                    // v1.1.3：窄窗口下也要检查一遍（底栏按钮换行后被裁就是在这个场景暴露的）
                    Cli.W("NARROW : " + f.UiResize(1050, 900));
                    Cli.W("CLIPN  : " + f.UiClippedText());
                    f.UiResize(1360, 942);
                    Cli.W("THEME  : " + f.UiThemeProbe());
                    Cli.W("TIP    : " + f.UiTipProbe());
                    Cli.W("CJKLEFT: " + f.UiCjkLeft());
                    if (lang != null)
                    {
                        // lang=ja / ko / en，可以用逗号连着一串（测反复切换，原来就是在这里崩的）
                        foreach (var one in lang.Split(','))
                        {
                            Lang want = one.StartsWith("ja") ? Lang.Ja : (one.StartsWith("ko") ? Lang.Ko : (one.StartsWith("zh") ? Lang.Zh : Lang.En));
                            try
                            {
                                long fmtBefore = MainForm.CellFmtCount;
                                f.SetLang(want);
                                // 切语言是延迟执行的（BeginInvoke），这里要泵一遍消息，
                                // 否则自检读到的还是旧状态、截图拍到的还是旧画面
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(120);
                                Application.DoEvents();
                                Cli.W("LANG   : " + one + " → ok, 现在=" + L.LangName(L.Cur)
                                    + " 单元格重绘=" + (MainForm.CellFmtCount - fmtBefore) + " 次"
                                    + " 表头=" + f.UiHeaderProbe()
                                    + " 按钮数=" + f.UiLangSwitchProbe());
                            }
                            catch (Exception ex) { Cli.W("LANGX  : " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                    Cli.W("UNIPAN : " + f.UiUniPanelInfo());
                        HoldUi(f, hold);
                        return 0;
                    }
                    if (tab3)
                    {
                        // 第 3 页自检：读人物卡 → 打印组·部位表 + 贴图明细 → 可选真跑一次压缩
                        f.UiSelectTab(2);
                        Cli.W("SHOWUP3: " + f.UiShowUp(null));   // 该页已选中 → 可见性读得准
                        string card3 = paths.Count > 0 ? paths[0] : "";
                        if (Directory.Exists(card3))
                        {
                            var l3 = TexTool.Enumerate(card3, null, false);
                            card3 = l3.Count > 0 ? l3[0].Src : card3;
                        }
                        f.UiReadStatReset();
                        if (paths.Count > 1) { f.UiG3LoadMany(paths.ToArray()); }
                        else { f.UiG3Load(card3); }
                        {   // 人物卡页同样是后台读取
                            var swg = System.Diagnostics.Stopwatch.StartNew();
                            while (f.UiG3Busy && swg.ElapsedMilliseconds < 120000) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                            Application.DoEvents();
                            Cli.W(L.F("LOADWAIT3: 读人物卡用了 {0}ms（后台线程）", swg.ElapsedMilliseconds));
                        }
                        Cli.W("READSTAT: " + f.UiReadStat);
                        Application.DoEvents();
                        Cli.W("TABS   : " + f.UiTabCount + " 页 —— " + f.UiTabTitle(0) + " / " + f.UiTabTitle(1) + " / " + f.UiTabTitle(2));
                        Cli.W("G3CARD : " + card3);
                        Cli.W("G3STAT : " + f.UiG3Status);
                        Cli.W("G3OUT  : " + f.UiG3Out);
                        Cli.W("G3FILT : " + f.UiG3FilterItems);
                        Cli.W("--- 组·部位表（启用|组·部位|贴图|大小|独享|处理方式|最大边）---");
                        Cli.W(f.UiG3GridDump().TrimEnd());
                        if (g3group >= 0)
                        {
                            f.UiG3SelectGroup(g3group);
                            Application.DoEvents();
                            Cli.W("G3SEL  : 选中组 " + g3group);
                            Cli.W("--- 该组第一行的贴图明细 ---");
                            Cli.W(f.UiG3TexDump().TrimEnd());
                        }
                        if (g3onlygroup >= 0)
                        {
                            // 只勾某一组（模拟用户"只压这一套衣服"）
                            f.UiG3Filter(0);
                            Application.DoEvents();
                            int n = 0;
                            var keys = new List<int>();
                            foreach (var line in f.UiG3GridDump().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                keys.Add(n++);
                            int hit = 0;
                            for (int i = 0; i < n; i++)
                            {
                                string nm = f.UiG3GridDump().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)[i];
                                bool inG = nm.Contains(Chara.GroupName(g3onlygroup) + " ·");
                                f.UiG3SetRow(i, inG, "自动", "1024");
                                if (inG) hit++;
                            }
                            Cli.W(L.F("G3ONLY : 只勾「{0}」→ {1} 行", Chara.GroupName(g3onlygroup), hit));
                            Cli.W("--- 只勾该组后的组·部位表 ---");
                            Cli.W(f.UiG3GridDump().TrimEnd());
                        }
                        Cli.W("TEXOV3 : " + f.UiG3TexOvDump());
                        Cli.W("CLIP3  : " + f.UiClippedText());
                        if (g3savepf != null)
                        {
                            Cli.W("--- 第 3 页保存预设 → preset\\chara ---");
                            string g3p = f.UiG3SavePreset(g3savepf);
                            Cli.W("G3SAVE : " + g3p);
                            if (File.Exists(g3p))
                            {
                                Cli.W("G3SIZE : " + new FileInfo(g3p).Length + " 字节");
                                Cli.W("G3PEEK :\r\n" + f.UiPrePeek(g3p));
                            }
                            Cli.W("G3LIST :\r\n" + MainForm.UiPresetList(PresetIo.KindChara, 48));
                        }
                        if (g3uni != null)
                        {
                            // g3uni=范围|格式|最大边[|锐化[|锐化算法[|灰度]]]（范围写 全部组 / 角色本体 / 换装3 / 仅当前选中组）
                            var us = g3uni.Split('|');
                            Cli.W("--- 一键统一（组过滤）---");
                            Cli.W(string.Format("UNISCOPE: {0}", f.UiG3UniItems));
                            // 范围写「仅已启用的部位」时，先把一部分行"取消启用"看它是不是真的只改勾上的
                            if (us[0] == "仅已启用的部位")
                            {
                                Cli.W("G3UNCHK: 先把第 2、3 行的「启用」取消 → " + f.UiG3Uncheck(2, 3));
                            }
                            f.UiG3Uni(us[0], us.Length > 1 ? us[1] : "自动", us.Length > 2 ? us[2] : "1024",
                                      us.Length > 3 ? us[3] : null,
                                      us.Length > 4 ? us[4] : null,
                                      us.Length > 5 ? us[5] : null,
                                      us.Length > 6 ? us[6] : null);   // v2.34：第 7 项 = 放大
                            Application.DoEvents();
                            Cli.W("UNISTAT : " + f.UiG3Status);
                            Cli.W("UNILOG  : " + f.UiG3LastLog);
                            Cli.W("TEXOV3B : " + f.UiG3TexOvDump());
                            Cli.W("--- 统一后的组·部位表 ---");
                            Cli.W(f.UiG3GridDump().TrimEnd());
                            if (g3group >= 0)
                            {
                                f.UiG3SelectGroup(g3group);
                                Application.DoEvents();
                                Cli.W("--- 统一后该组明细（看生效/未生效）---");
                                Cli.W(f.UiG3TexDump().TrimEnd());
                            }
                        }
                        if (g3prev >= 0)
                        {
                            Cli.W("--- 第 3 页缩略图预览 ---");
                            for (int i = 0; i < g3prev; i++)
                            {
                                if (i >= f.UiG3GridRows) break;
                                Cli.W("PREV3   : 行 " + i + " → " + f.UiG3PreviewAt(i));
                            }
                        }
                        if (big3 != null)
                        {
                            // big3=行号[:+/-滚轮次数] → 第 3 页（人物卡）的独立悬浮预览窗
                            var bf = big3.Split(':');
                            int brow = int.Parse(bf[0]);
                            int wheel = bf.Length > 1 ? int.Parse(bf[1]) : 0;
                            Cli.W("--- 独立悬浮预览窗（第 3 页）---");
                            if (f.UiGTexRows == 0) Cli.W("G3SEL0  : " + f.UiG3PreviewAt(0));   // 先选组，明细表才有内容
                            Cli.W("BIGOPEN3: " + f.UiBigOpen(true, brow));
                            Cli.W("BIGSAME3: " + f.UiSelectTexRow(true, brow));
                            if (brow + 1 < f.UiGTexRows) Cli.W("BIGNEXT3: " + f.UiSelectTexRow(true, brow + 1));
                            if (brow + 2 < f.UiGTexRows) Cli.W("BIGNEX3b: " + f.UiSelectTexRow(true, brow + 2));
                            Cli.W("G3SEL1  : " + f.UiG3PreviewAt(1));                          // 换「组·部位」→ 悬浮窗也要跟着换
                            for (int i = 0; i < Math.Abs(wheel); i++)
                                Cli.W(string.Format("BIGWHEEL3({0}) : {1}", i + 1, f.UiBigWheel(wheel > 0 ? 120 : -120)));
                            if (bigsnap != null) Cli.W("BIGSNAP3: " + f.UiSnapBig(bigsnap));
                            if (hold > 0) Cli.W("BIGKEEP3: hold>0，悬浮窗留着不关（给人看/截屏）");
                            else Cli.W("BIGCLOSE3: " + f.UiBigClose());
                        }
                        if (g3run)
                        {
                            Cli.W("--- 走界面「压缩人物卡」---");
                            f.UiG3Run();
                            var sw3 = System.Diagnostics.Stopwatch.StartNew();
                            int lastTick = 0;
                            while (sw3.ElapsedMilliseconds < 1800000)
                            {
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(150);
                                if ((int)(sw3.ElapsedMilliseconds / 1000) >= lastTick + 10)
                                {
                                    lastTick = (int)(sw3.ElapsedMilliseconds / 1000);
                                    var ls = f.UiG3Log.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                                    Cli.W(L.F("G3TICK : {0}s 日志 {1} 行；末行: {2}", lastTick, ls.Length,
                                        ls.Length > 0 ? ls[ls.Length - 1] : "(空)"));
                                }
                                if (f.UiG3Log.Contains("人物卡完成")) break;
                                if (f.UiG3Log.Contains("运行出错")) break;
                            }
                            Application.DoEvents();
                            foreach (var ln in f.UiG3Log.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                if (ln.Contains("人物卡完成") || ln.Contains("成功 ") || ln.Contains("[X]") || ln.Contains("[OK]"))
                                    Cli.W("G3RUN  : " + ln);
                        }
                        if (snap != null)
                        {
                            Cli.W("SNAP   : " + f.UiSnap(snap, snaptab));
                            Cli.W("CLIPPED:\r\n" + f.UiClippedText());
                            if (rects) Cli.W("RECTS  :\r\n" + f.UiRectDump(snaptab < 0 ? 0 : snaptab));
                        }
                        if (sorttex3 != null)
                        {
                            var sf = sorttex3.Split(':');
                            int n3 = sf.Length > 1 ? int.Parse(sf[1]) : 1;
                            Cli.W("--- 第 3 页贴图明细排序（点表头）---");
                            for (int k = 1; k <= n3; k++)
                                Cli.W(string.Format("SORTT3/{0}: {1}", k, f.UiSortTex(true, sf[0], 1)));
                        }
                        Cli.W("G3SCOPE: " + f.UiG3UniScopeItems);
                        if (g3part)
                        {
                            Cli.W("--- 走按钮「设置到目前浏览的部位」---");
                            Cli.W(L.F("G3PART : 第 {0} 行 → {1}", f.UiG3GridRows > 0 ? 1 : 0, f.UiG3UniToPart()));
                        }
                        if (sort3 != null)
                        {
                            // sort3=列名[:点击次数] → 模拟点第 3 页「组·部位」表表头
                            var sf = sort3.Split(':');
                            int n = sf.Length > 1 ? int.Parse(sf[1]) : 1;
                            Cli.W("--- 第 3 页组·部位表排序（点表头）---");
                            for (int k = 1; k <= n; k++)
                                Cli.W(string.Format("SORT3/{0}: {1}", k, f.UiSortG3(sf[0], 1)));
                            Cli.W("SORT3R : " + f.UiSortG3(null, 0));
                            Cli.W("SORT3P : " + f.UiRefillG3());          // 重填表后排序还在不在
                        }
                        // v2.37：列宽自检（tab2/tab3 段末尾就 return 了，插在这里最合适；
                        // 别插到 if 的"条件"与"函数体"之间 —— 那样后面的块会无条件执行）
                        Cli.W("GRIDW  : " + f.UiGridWidths());
                    if (typeuni != null) Cli.W("TYPEUNI: " + f.UiTypeUni(typeuni.StartsWith("typeuni3"), typeuni.Substring(8)));
                    Cli.W("TYPEVIS: " + f.UiTypeVisible(showtype));
                        if (dragcol != null)
                        {
                            var dc = dragcol.Split(':');
                            int ddx = dc.Length > 2 ? int.Parse(dc[2]) : 60;
                            Cli.W("DRAGCOL: " + f.UiDragCol(dc[0], dc[1], ddx));
                            Cli.W("DRAGCOL: " + f.UiDragCol(dc[0], dc[1], ddx));
                            Cli.W("GRIDW2 : " + f.UiGridWidths());
                        }
                    if (colact != null) Cli.W("COLACT : " + f.UiColWidths(colact));
                    if (findtext != null) Cli.W("FINDTX : " + f.UiFindTrace(findtext));
                    if (tabbench > 0) Cli.W("TABBEN : " + f.UiTabBench(tabbench));
                    if (realsnap != null) Cli.W("REALSNAP: " + f.UiSnapReal(realsnap));
                    Cli.W("LANGRECT: " + f.UiLangPanelRect());
                    // v1.1.3：窄窗口下也要检查一遍（底栏按钮换行后被裁就是在这个场景暴露的）
                    Cli.W("NARROW : " + f.UiResize(1050, 900));
                    Cli.W("CLIPN  : " + f.UiClippedText());
                    f.UiResize(1360, 942);
                    Cli.W("THEME  : " + f.UiThemeProbe());
                    Cli.W("TIP    : " + f.UiTipProbe());
                    Cli.W("CJKLEFT: " + f.UiCjkLeft());
                    if (lang != null)
                    {
                        // lang=ja / ko / en，可以用逗号连着一串（测反复切换，原来就是在这里崩的）
                        foreach (var one in lang.Split(','))
                        {
                            Lang want = one.StartsWith("ja") ? Lang.Ja : (one.StartsWith("ko") ? Lang.Ko : (one.StartsWith("zh") ? Lang.Zh : Lang.En));
                            try
                            {
                                long fmtBefore = MainForm.CellFmtCount;
                                f.SetLang(want);
                                // 切语言是延迟执行的（BeginInvoke），这里要泵一遍消息，
                                // 否则自检读到的还是旧状态、截图拍到的还是旧画面
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(120);
                                Application.DoEvents();
                                Cli.W("LANG   : " + one + " → ok, 现在=" + L.LangName(L.Cur)
                                    + " 单元格重绘=" + (MainForm.CellFmtCount - fmtBefore) + " 次"
                                    + " 表头=" + f.UiHeaderProbe()
                                    + " 按钮数=" + f.UiLangSwitchProbe());
                            }
                            catch (Exception ex) { Cli.W("LANGX  : " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                    Cli.W("UNIPAN : " + f.UiUniPanelInfo());
                        HoldUi(f, hold);
                        return 0;
                    }

                    f.AcceptPaths(paths.ToArray(), true);
                    if (grp != null) { f.UiGrpSet(grp); Application.DoEvents(); Cli.W("GRPFILT: " + f.UiGrpDump); }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 120000)
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(50);
                        if (!f.UiBusyNow) break;                 // 扫描结束（与语言无关）
                        if (f.UiStatus.StartsWith("扫描完成") || f.UiStatus.StartsWith("扫描出错")) break;
                    }
                    Application.DoEvents();
                    Cli.W("STATUS : " + f.UiStatus);
                    Cli.W("INPUT  : " + f.UiInputText);
                    Cli.W("OUTDIR : " + f.UiOutDir);
                    Cli.W("DROPBAR: " + f.UiDropText);
                    Cli.W("RECT   : drop=" + f.UiDropRect + " content=" + f.UiContentRect);
                    Cli.W("LAYOUT : " + f.UiLayoutDump);
                    // 窗口放大 → 多出来的宽度应该优先给日志列（v2.20）
                    Cli.W("RESIZE1: " + f.UiResize(1360, 942));
                    Cli.W("RESIZE2: " + f.UiResize(1700, 942));
                    Cli.W("RESIZE3: " + f.UiResize(1100, 942));
                    f.UiResize(1360, 942);
                    Application.DoEvents();
                    Cli.W("LOGTGL : 初始 " + f.UiLogVisible);
                    f.UiClickLogButton(1); f.UiClickLogButton(2);      // 走真实按钮路径
                    Application.DoEvents();
                    Cli.W("LOGTGL : 点一次后 " + f.UiLogVisible + "  " + f.UiLayoutDump);
                    f.UiClickLogButton(1); f.UiClickLogButton(2);
                    Application.DoEvents();
                    Cli.W("LOGTGL : 再点一次 " + f.UiLogVisible + "  " + f.UiLayoutDump);
                    f.UiDragSplitter(1, 80); f.UiDragSplitter(2, 120); f.UiDragSplitter(3, -140); f.UiDragSplitter(0, -60);
                    Application.DoEvents();
                    Cli.W("SPLIT  : 各条 +80/+120/-140/-60 后 " + f.UiLayoutDump);
                    f.UiDragSplitter(1, -80); f.UiDragSplitter(2, -120); f.UiDragSplitter(3, 140); f.UiDragSplitter(0, 60);
                    Application.DoEvents();
                    Cli.W("SPLIT  : 反向拖回后 " + f.UiLayoutDump);
                    // 连续小步拖动（模拟鼠标一路拖过去）：位移必须与步长成比例，不能越拖越飞
                    // 一次按住不放、连续 MouseMove（模拟真实拖动）：终点必须≈起点+总位移，不能越拖越飞
                    int w0 = (int)f.UiLogWidth(3);
                    f.UiDragSteps(3, -10, -20, -30, -40, -50, -60, -70, -80);
                    Application.DoEvents();
                    Cli.W(L.F("DRAG1  : 日志列宽 {0} → {1}（一次拖动总位移 -80，应为 {2}）",
                        w0, (int)f.UiLogWidth(3), w0 + 80));
                    f.UiDragSteps(2, 20, 40, 60, 80, 100, 120);
                    Application.DoEvents();
                    Cli.W("DRAG2  : 部位表|明细 一次拖 +120 → " + f.UiLayoutDump);
                    Cli.W("CLIP   : " + f.UiClippedText());
                    Cli.W("PLAN   : " + f.UiPlanSummary());
                    Cli.W("COLORK : " + f.UiColorKernel(colorkernel));
                    if (upset != null) Cli.W("UPSET  : " + f.UiUpSet(upset));
                    Cli.W("SHOWUP : " + f.UiShowUp(showup));
                    if (upall != null) Cli.W("UPALL  : " + f.UiApplyUp(upall));
                    Cli.W("GRPF   : " + f.UiGrpDump);
                    if (prelist) Cli.W("PRESET :\r\n" + MainForm.UiPresetList(PresetIo.KindBatch, 48));
                    if (presel >= 0)
                    {
                        Cli.W("--- 预设框选中第 " + presel + " 项（走真实 SelectedIndexChanged）---");
                        f.UiPreSelect(presel);
                        Application.DoEvents();
                        Cli.W("PREDUMP: " + f.UiPreDump.Replace("\r\n", " | "));
                        Cli.W("PREPLAN: " + f.UiPrePlanSummary);
                        Cli.W("COLORK2: " + f.UiColorKernel(null));   // v2.32：套用预设后回读彩色核
                    }
                    if (predef != null)
                    {
                        // predef=on|off → 走界面同一条路设/清「将当前预设设为默认」，再重载看选中项
                        Cli.W("PREDEF1: " + f.UiSetDefaultPreset(predef == "on"));
                        Cli.W("PREDEF2: " + f.UiSetDefaultPreset(predef == "on"));
                    }
                    if (setplan != null)
                    {
                        f.UiPreSetPlan(setplan);
                        Application.DoEvents();
                        Cli.W("SETPLAN: " + f.UiPrePlanSummary);
                    }
                    if (presave != null)
                    {
                        Cli.W("--- 第 1 页保存预设（卡面 = " + f.UiInputText + " 的第一张卡）---");
                        string savedPath = f.UiPreSave(presave, null);
                        Cli.W("PRESAVE: " + savedPath);
                        if (File.Exists(savedPath))
                        {
                            Cli.W("PRESIZE: " + new FileInfo(savedPath).Length + " 字节（PNG 卡面 + 内嵌 JSON）");
                            Cli.W("PREPEEK:\r\n" + f.UiPrePeek(savedPath));
                        }
                        Cli.W("PRELIST:\r\n" + MainForm.UiPresetList(PresetIo.KindBatch, 48));
                        Cli.W("PREDUMP: " + f.UiPreDump.Replace("\r\n", " | "));
                        // 存完再选回来（第 2 项 = 刚存的这个），验证"能被读取预设读取"
                        f.UiPreReload();
                        int idx = -1;
                        var dump = f.UiPreDump;
                        if (dump.Contains(Path.GetFileName(savedPath))) idx = 1;
                        if (idx > 0) { f.UiPreSelect(idx); Application.DoEvents(); Cli.W("PRESEL  : " + f.UiPrePlanSummary); }
                    }
                    if (doRun)
                    {
                        Cli.W("--- 走界面「开始压缩」(DoRun) ---");
                        f.UiRun();
                        sw.Restart();
                        while (sw.ElapsedMilliseconds < 900000)
                        {
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(100);
                            // 用"是否还在忙"判断，**不要靠中文前缀**：界面语言一切换，中文前缀就匹配不上，
                            // 这一循环会一直空转到 900 秒超时（v1.0.2 排查日志翻译时被这个坑了很久）。
                            if (!f.UiBusyNow) break;
                            string s = f.UiStatus;
                            if (s.StartsWith("完成：") || s.StartsWith("已取消") || s.StartsWith("运行出错")) break;
                        }
                        for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                        Cli.W("RUNSTAT: " + f.UiStatus);
                    }
                    Cli.W("--- 日志 ---");
                    Cli.W(f.UiLogText.TrimEnd());
                    Cli.W("READEND: " + f.UiReadStat);        // 自检全过程一共整读了几遍卡
                    if (snap != null)
                    {
                        Cli.W("SNAP   : " + f.UiSnap(snap, snaptab));
                        Cli.W("CLIPPED:\r\n" + f.UiClippedText());
                        if (rects) Cli.W("RECTS  :\r\n" + f.UiRectDump(snaptab < 0 ? 0 : snaptab));
                    }
                    HoldUi(f, hold);
                    f.Close();
                    return 0;
                }

                if (cmd == "dlgtest")
                {
                    // 探针：构造各处会用到的 WinForms 对话框/消息框资源（这些地方才会去解析具体区域 en-US=1033）
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Cli.W("区域: culture=" + System.Globalization.CultureInfo.CurrentCulture.Name
                        + " ui=" + System.Globalization.CultureInfo.CurrentUICulture.Name
                        + " invariant-mode=" + (System.Globalization.CultureInfo.CurrentCulture.Name == ""
                            ? "(name 为空)" : "no"));
                    try
                    {
                        var ofd = new OpenFileDialog();
                        Cli.W("[1] OpenFileDialog  Filter=" + ofd.Filter + " | Title=" + ofd.Title);
                        var sfd = new SaveFileDialog();
                        Cli.W("[2] SaveFileDialog  DefaultExt=" + sfd.DefaultExt);
                        var fbd = new FolderBrowserDialog();
                        Cli.W("[3] FolderBrowserDialog  Desc=" + fbd.Description);
                        var f2 = new MainForm();
                        Cli.W("[4] MainForm 标题=" + f2.Text + " / 字体=" + f2.Font.Name);
                        f2.Dispose();
                        if (a.Count > 1 && a[1].ToLowerInvariant().StartsWith("full"))
                        {
                            AutoCloseDialog(1500);
                            Cli.W("[6] MessageBox.Show ...");
                            MessageBox.Show(L.Tr("消息框测试"), "dlgtest", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            AutoCloseDialog(1500);
                            Cli.W("[7] OpenFileDialog.ShowDialog ...");
                            new OpenFileDialog().ShowDialog();
                            AutoCloseDialog(1500);
                            Cli.W("[8] FolderBrowserDialog.ShowDialog ...");
                            new FolderBrowserDialog().ShowDialog();
                            Cli.W("[9] 对话框实测通过");
                        }
                        // 最后才碰这个：它是「invariant 模式」的试金石，放最后才不会掩盖上面真正的自然触发点
                        Cli.W("[5] 新建 CultureInfo(1033) = " + new System.Globalization.CultureInfo(1033).EnglishName);
                        Cli.W("全部通过：这个构建不会出区域错误");
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Cli.W("[X] 区域相关失败: " + ex.GetType().FullName + ": " + ex.Message);
                        return 4;
                    }
                }

                if (cmd == "mboxtest")
                {
                    // 关键差异：真正的 GUI 是在 Application.Run 的消息循环里弹模态框的
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var f = new Form { Text = "mboxtest", Width = 360, Height = 200 };
                    string res = "(没跑到)";
                    f.Shown += delegate
                    {
                        AutoCloseDialog(1200);
                        try
                        {
                            res = "在消息循环里弹 YesNo 框 → " + MessageBox.Show(f, "在消息循环里弹框",
                                "mboxtest", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        }
                        catch (Exception ex)
                        {
                            res = "[X] " + ex.GetType().FullName + ": " + ex.Message;
                        }
                        f.Close();
                    };
                    try { Application.Run(f); }
                    catch (Exception ex) { Cli.W("[X] Application.Run: " + ex.GetType().FullName + ": " + ex.Message); }
                    Cli.W(res);
                    return res.StartsWith("[X]") ? 4 : 0;
                }

                if (cmd == "hdrtest")
                {
                    // 自检 Radiance RGBE 编解码：解码 → 同尺寸重编码 → 再解码，浮点必须逐位相同
                    if (a.Count < 2) { Usage(); return 1; }
                    byte[] raw = File.ReadAllBytes(a[1]);
                    Cli.W("isHdr=" + Hdr.IsHdr(raw) + " bytes=" + raw.Length);
                    int pw, ph;
                    Cli.W("peek=" + (Hdr.PeekSize(raw, out pw, out ph) ? pw + "x" + ph : "失败"));
                    var im = Hdr.Decode(raw);
                    double sum = 0, mx = double.MinValue, mn = double.MaxValue;
                    for (int i = 0; i < im.Rgb.Length; i++)
                    {
                        double v = im.Rgb[i];
                        sum += v; if (v > mx) mx = v; if (v < mn) mn = v;
                    }
                    Cli.W(string.Format("decode {0}x{1}  mean={2:0.000000} min={3:0.000000} max={4:0.0000} sum={5:0.0000}",
                        im.W, im.H, sum / im.Rgb.Length, mn, mx, sum));
                    var re = Hdr.Encode(im);
                    var im2 = Hdr.Decode(re);
                    bool same = im2.W == im.W && im2.H == im.H && im2.Rgb.Length == im.Rgb.Length;
                    int bad = 0;
                    if (same) for (int i = 0; i < im.Rgb.Length; i++) if (im.Rgb[i] != im2.Rgb[i]) bad++;
                    Cli.W(L.F("roundtrip 同尺寸重编码: {0}（差异 {1} 个分量）编码后 {2} 字节（原 {3}）",
                        bad == 0 ? "逐位相同" : "不一致", bad, re.Length, raw.Length));
                    if (a.Count > 2)
                    {
                        int cap = a.Count > 3 ? int.Parse(a[3]) : 1024;
                        double sc = Math.Min(1.0, (double)cap / Math.Max(im.W, im.H));
                        var sm = Hdr.ResizeBox(im, Math.Max(1, (int)Math.Round(im.W * sc)), Math.Max(1, (int)Math.Round(im.H * sc)));
                        var enc = Hdr.Encode(sm);
                        var back = Hdr.Decode(enc);
                        double s2 = 0;
                        for (int i = 0; i < back.Rgb.Length; i++) s2 += back.Rgb[i];
                        Cli.W(L.F("resize -> {0}x{1}  {2} 字节（再解码 {3}x{4} ok，sum={5:0.0000}）",
                            sm.W, sm.H, enc.Length, back.W, back.H, s2));
                        File.WriteAllBytes(a[2], enc);
                        Cli.W("已写出 " + a[2]);
                    }
                    return 0;
                }

                Usage();
                return 1;
            }
            catch (Exception ex)
            {
                Cli.W("[X] " + ex);
                return 2;
            }
        }
    }

    static class PlanIo
    {
        /// <summary>plan.json &lt;-&gt; Dictionary&lt;class, Rule&gt;（BCL 的 System.Text.Json，无外部依赖）</summary>
        public static Dictionary<string, Rule> Load(string path)
        {
            var plan = new Dictionary<string, Rule>();
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    plan[p.Name] = TexTool.ReadRule(p.Value);
            }
            return plan;
        }

        public static void Save(Dictionary<string, Rule> plan, string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            int i = 0;
            foreach (var kv in plan)
            {
                sb.AppendFormat("  \"{0}\": {{ \"format\": \"{1}\", \"size\": {2}{3} }}{4}\n",
                    kv.Key, kv.Value.Format, kv.Value.Size, TexTool.RuleExtraJson(kv.Value),
                    ++i < plan.Count ? "," : "");
            }
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
