// dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing.Drawing2D; // make sure this is included
using System.IO; // added
using System.Text.Json; // added
using System.Drawing.Imaging; // added for ImageFormat
using Microsoft.Win32; // add at top if not already
using System.Globalization;

class DesktopWidgets : Form

{
    string textAlign; // moved to config
    Color textcolor; // moved to config
    string globalFormat; // moved to config
    Font font; // moved to config
    float dx; // moved to config
    float dy; // moved to config
    int taskbarGap; // moved to config
    string imagePath; // moved to config
    string withseconds; // moved to config
    string withoutseconds; // moved to config
    bool showSeconds; // moved to config

    Bitmap backgroundImageBitmap = null!;
    System.Windows.Forms.Timer timer;
    // when in minute-update mode we first wait until the start of the next wall-clock minute
    // then switch the timer to a steady 60s interval
    bool minuteFirstTickPending = false;
    string lastTime = "";

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);
    [DllImport("user32.dll")]
    static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint WM_SYSTIMER = 0x052C;
    const int SPI_SETDESKWALLPAPER = 0x0014; // added
    const int SPIF_UPDATEINIFILE = 0x01;     // added
    const int SPIF_SENDWININICHANGE = 0x02;  // added

    IntPtr workerW;
    string GetFormattedText()
    {
        string currentTime = showSeconds ? DateTime.Now.ToString(withseconds) : DateTime.Now.ToString(withoutseconds, CultureInfo.CurrentCulture);
        // normalize "\n" from config to actual newline
        return globalFormat.Replace("%time", currentTime).Replace("\\n", "\n");
    }

    public DesktopWidgets()
    {
        FormBorderStyle = FormBorderStyle.None;
        Width = Screen.PrimaryScreen.Bounds.Width;
        Height = Screen.PrimaryScreen.Bounds.Height;
        Location = new Point(0, 0);
        ShowInTaskbar = false;
        TopMost = false;

        // Load config.json (sibling to executable)
        var cfg = LoadConfig();
        textAlign = cfg.textAlign;
        textcolor = ToColor(cfg.textColor);
        globalFormat = cfg.globalFormat;
        font = ToFont(cfg.font);
        dx = cfg.dx;
        dy = cfg.dy;
        taskbarGap = cfg.taskbarGap;
        imagePath = cfg.imagePath;
        withseconds = cfg.withSeconds;
        withoutseconds = cfg.withoutSeconds;
        showSeconds = cfg.showSeconds;

        if (System.IO.File.Exists(imagePath))
            backgroundImageBitmap = new Bitmap(imagePath);
        else
        {
            MessageBox.Show("Image not found: " + imagePath);
            Environment.Exit(0);
        }
        // --- REGISTER CURRENT EXE FOR STARTUP (PER-USER, NO ADMIN NEEDED) ---
        try
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "DesktopWidget.exe");
            string appName = Path.GetFileNameWithoutExtension(exePath);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                key.SetValue(appName, $"\"{exePath}\"");
            }
        }
        catch
        {
            // fail silently if registry write fails
        }
        Shown += (s, e) =>
        {
            MakeClickThrough();
            PutBehindDesktopIcons();
            SetupTimer();
        };

        // Set wallpaper at startup if configured
        ApplyWallpaper(cfg.wallpaper);
    }

    void SetupTimer()
    {
        timer = new System.Windows.Forms.Timer();
        if (showSeconds)
        {
            timer.Interval = 1000; // 1s
            timer.Tick += Timer_Tick;
            minuteFirstTickPending = false;
        }
        else
        {
            // compute milliseconds until the next minute boundary (when seconds == 0)
            var now = DateTime.Now;
            int msToNextMinute = (60 - now.Second) * 1000 - now.Millisecond;
            if (msToNextMinute <= 0) msToNextMinute = 1; // safety
            timer.Interval = msToNextMinute; // single-shot to align to the minute
            minuteFirstTickPending = true;
            timer.Tick += Timer_Tick;
        }

        timer.Start();
        UpdateLayeredImage(); // initial draw
    }

    void Timer_Tick(object? sender, EventArgs e)
    {
        // First tick in minute mode is used to align to wall-clock minute. After firing,
        // switch to steady 60s interval so subsequent ticks occur exactly on minute boundaries.
        UpdateLayeredImage();

        if (minuteFirstTickPending)
        {
            minuteFirstTickPending = false;
            try
            {
                // switch to a stable 60s interval
                timer.Interval = 60000;
            }
            catch
            {
                // ignore if timer disposed
            }
        }
    }

    void PutBehindDesktopIcons()
    {
        IntPtr progman = FindWindow("Progman", null);
        SendMessage(progman, WM_SYSTIMER, IntPtr.Zero, IntPtr.Zero);

        EnumWindows((hWnd, lParam) =>
        {
            IntPtr shellViewWin = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", "");
            if (shellViewWin != IntPtr.Zero)
                workerW = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", "");
            return true;
        }, IntPtr.Zero);

        if (workerW != IntPtr.Zero)
            SetParent(Handle, workerW);
    }

    void MakeClickThrough()
    {
        int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
    }

    // Applies the desktop wallpaper; converts to BMP in temp if needed for reliability on older APIs
    void ApplyWallpaper(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            string toSet = path;
            if (!string.Equals(Path.GetExtension(path), ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                using (var img = Image.FromFile(path))
                {
                    string tmpBmp = Path.Combine(Path.GetTempPath(), "DesktopWidget_wallpaper.bmp");
                    img.Save(tmpBmp, ImageFormat.Bmp);
                    toSet = tmpBmp;
                }
            }

            bool ok = NativeMethods.SystemParametersInfo(
                SPI_SETDESKWALLPAPER, 0, toSet, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // Optional: handle/log error if desired
                // MessageBox.Show($"Failed to set wallpaper (error {err}).");
            }
        }
        catch
        {
            // ignore; setting wallpaper is best-effort
        }
    }


    void UpdateLayeredImage()
    {
        string currentTime = GetFormattedText();
        if (currentTime == lastTime) return;
        lastTime = currentTime;

        if (backgroundImageBitmap == null) return;

        using (Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; // vector smoothing
                g.Clear(Color.Transparent);

                // --- Draw vector text first ---
                using (GraphicsPath path = new GraphicsPath())
                {
                    // Split multiline text for individual alignment (e.g. "Sunday \n October 21 2025")
                    string[] lines = currentTime.Split(new[] { '\n' }, StringSplitOptions.None);
                    float lineSpacing = font.Size * 1.1f; // adjust vertical spacing

                    // Measure width of each line to find the widest one
                    float maxWidth = 0;
                    // create a small temporary bitmap for measurement and dispose it properly
                    using (var tempBmp = new Bitmap(1, 1))
                    using (Graphics gTemp = Graphics.FromImage(tempBmp))
                    {
                        foreach (string line in lines)
                        {
                            using (GraphicsPath tmpPath = new GraphicsPath())
                            {
                                tmpPath.AddString(line, font.FontFamily, (int)font.Style, font.Size, new PointF(0, 0), StringFormat.GenericDefault);
                                RectangleF b = tmpPath.GetBounds();
                                if (b.Width > maxWidth) maxWidth = b.Width;
                            }
                        }
                    }

                    // Create aligned lines
                    using (GraphicsPath finalPath = new GraphicsPath())
                    {
                        float y = 0;
                        foreach (string line in lines)
                        {
                            using (GraphicsPath linePath = new GraphicsPath())
                            {
                                linePath.AddString(line, font.FontFamily, (int)font.Style, font.Size, new PointF(0, 0), StringFormat.GenericDefault);
                                RectangleF b = linePath.GetBounds();

                                float offsetX = 0;
                                switch (textAlign.ToLower())
                                {
                                    case "left":
                                        offsetX = 0; // left-aligned: start at 0
                                        break;
                                    case "center":
                                        offsetX = (maxWidth - b.Width) / 2; // center relative to widest line
                                        break;
                                    case "right":
                                        offsetX = maxWidth - b.Width; // right-aligned
                                        break;
                                    default:
                                        offsetX = (maxWidth - b.Width) / 2; // fallback to center
                                        break;
                                }


                                using (Matrix mLine = new Matrix())
                                {
                                    mLine.Translate(offsetX, y);
                                    linePath.Transform(mLine);
                                }

                                finalPath.AddPath(linePath, false);
                            }

                            y += lineSpacing;
                        }

                        // center the whole block horizontally and vertically on screen
                        RectangleF total = finalPath.GetBounds();
                        using (Matrix m = new Matrix())
                        {
                            m.Translate((Width - total.Width) / 2 - total.X + dx,
                                        (Height - taskbarGap - total.Height) / 2 - total.Y + dy);
                            finalPath.Transform(m);
                        }

                        using (Brush brush = new SolidBrush(textcolor))
                        {
                            g.FillPath(brush, finalPath);
                        }
                    }


                    using (Brush whiteBrush = new SolidBrush(textcolor))
                    {
                        g.FillPath(whiteBrush, path);
                    }
                }

                // --- Draw image on top ---
                g.DrawImage(backgroundImageBitmap,
                            new Rectangle(0, 0, Width, Height - taskbarGap),
                            new Rectangle(0, 0, backgroundImageBitmap.Width, backgroundImageBitmap.Height),
                            GraphicsUnit.Pixel);
            }

            // --- Apply to layered window ---
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

            NativeMethods.SIZE size = new NativeMethods.SIZE { cx = Width, cy = Height };
            NativeMethods.POINT pointSource = new NativeMethods.POINT { x = 0, y = 0 };
            NativeMethods.POINT topPos = new NativeMethods.POINT { x = Left, y = Top };
            NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = 0x00,
                BlendFlags = 0x00,
                SourceConstantAlpha = 255,
                AlphaFormat = 0x01
            };

            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, 2);

            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // Ensure disposable resources are released when the form is disposed.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stop and dispose timer
            if (timer != null)
            {
                try { timer.Stop(); } catch { }
                try { timer.Tick -= Timer_Tick; } catch { }
                try { timer.Dispose(); } catch { }
                timer = null!;
            }

            // Dispose loaded bitmap and font
            try { backgroundImageBitmap?.Dispose(); } catch { }
            backgroundImageBitmap = null!;
            try { font?.Dispose(); } catch { }
            font = null!;
        }

        base.Dispose(disposing);
    }


    static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
            ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }

    // ---- Config mapping helpers ----
    private static AppConfig LoadConfig()
    {
        try
        {
            var path = FindConfigPath();
            if (path != null && File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, opts);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // ignore and fallback to defaults
        }
        return new AppConfig(); // defaults
    }

    // Probe common locations: CWD (project folder), exe output folder, parent of output (bin/Debug/.. -> project)
    private static string FindConfigPath()
    {
        string file = "config.json";
        string[] candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), file),
            Path.Combine(AppContext.BaseDirectory, file),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", file))
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Color ToColor(ColorConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.hex))
        {
            var hex = c.hex.Trim().TrimStart('#');
            try
            {
                if (hex.Length == 8)
                    return Color.FromArgb(
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16),
                        Convert.ToByte(hex.Substring(6, 2), 16));
                if (hex.Length == 6)
                    return Color.FromArgb(
                        255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { /* fallback to ARGB below */ }
        }
        return Color.FromArgb(c.a, c.r, c.g, c.b);
    }

    private static Font ToFont(FontConfig f)
    {
        var style = FontStyle.Regular;
        if (!string.IsNullOrWhiteSpace(f.style))
        {
            foreach (var part in f.style.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse(part, true, out FontStyle s))
                    style |= s;
            }
        }
        var unit = GraphicsUnit.Pixel;
        if (!string.IsNullOrWhiteSpace(f.unit))
            Enum.TryParse(f.unit, true, out unit);

        var family = string.IsNullOrWhiteSpace(f.family) ? "Stencil" : f.family;
        var size = f.size > 0 ? f.size : 80f;
        return new Font(family, size, style, unit);
    }

    // ---- Config DTOs with sensible defaults ----
    private class AppConfig
    {
        public string textAlign { get; set; } = "center"; // "left" | "center" | "right"
        public ColorConfig textColor { get; set; } = new ColorConfig { a = 255, r = 255, g = 255, b = 255 };
        public string globalFormat { get; set; } = "%time";
        public FontConfig font { get; set; } = new FontConfig { family = "Stencil", size = 80, style = "Bold", unit = "Pixel" };
        public float dx { get; set; } = 0f;
        public float dy { get; set; } = -30f;
        public int taskbarGap { get; set; } = 50;
        public string imagePath { get; set; } = @"D:\Users\Admin\Downloads\1234.png";
        public string withSeconds { get; set; } = "hh:mm:ss";
        public string withoutSeconds { get; set; } = "dddd '\\n' MMMM hh:mm";
        public bool showSeconds { get; set; } = false;
        public string wallpaper { get; set; } // optional path to set wallpaper at startup
    }

    private class ColorConfig
    {
        public byte a { get; set; }
        public byte r { get; set; }
        public byte g { get; set; }
        public byte b { get; set; }
        public string hex { get; set; } // optional, e.g. "#64FFFFFF" (AARRGGBB) or "#FFFFFF" (RRGGBB)
    }

    private class FontConfig
    {
        public string family { get; set; }
        public float size { get; set; }
        public string style { get; set; } // e.g., "Bold", "Bold|Italic"
        public string unit { get; set; }  // e.g., "Pixel"
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new DesktopWidgets());
    }
}
