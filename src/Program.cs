using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("OpenCode Go Tray")]
[assembly: System.Reflection.AssemblyDescription("Windows tray widget for OpenCode Go subscription limits")]
[assembly: System.Reflection.AssemblyProduct("OpenCode Go Tray")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]

namespace OpenCodeGoTray
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(15, 22, 34);
        public static readonly Color BGradient = Color.FromArgb(19, 28, 43);
        public static readonly Color Border = Color.FromArgb(38, 51, 72);
        public static readonly Color Title = Color.FromArgb(238, 244, 252);
        public static readonly Color Subtle = Color.FromArgb(126, 143, 168);
        public static readonly Color Label = Color.FromArgb(185, 198, 217);
        public static readonly Color Track = Color.FromArgb(27, 37, 55);
        public static readonly Color Green = Color.FromArgb(70, 224, 166);
        public static readonly Color Amber = Color.FromArgb(255, 198, 92);
        public static readonly Color Red = Color.FromArgb(255, 122, 122);
        public static readonly Color Footer = Color.FromArgb(110, 127, 151);
        public static readonly Color MenuBg = Color.FromArgb(17, 25, 38);
        public static readonly Color MenuHover = Color.FromArgb(34, 48, 68);
        public static readonly Color PillOkBg = Color.FromArgb(22, 58, 46);
        public static readonly Color PillOkText = Color.FromArgb(88, 227, 175);
        public static readonly Color PillWarnBg = Color.FromArgb(58, 27, 31);
        public static readonly Color PillWarnText = Color.FromArgb(255, 139, 139);

        public static Color ForPercent(double p)
        {
            if (p >= 85) return Red;
            if (p >= 60) return Amber;
            return Green;
        }
    }

    internal sealed class Meter
    {
        public double Percent;
        public bool Limited;
        public DateTime ResetsAt;
    }

    internal sealed class UsageData
    {
        public Meter Rolling;
        public Meter Weekly;
        public Meter Monthly;
        public DateTime FetchedAt;

        public bool IsLimited
        {
            get
            {
                return Rolling.Limited || Weekly.Limited || Monthly.Limited;
            }
        }

        public static UsageData Parse(string json)
        {
            var ser = new JavaScriptSerializer();
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new Exception("Пустой ответ API");
            object usageObj;
            if (!root.TryGetValue("usage", out usageObj)) throw new Exception("В ответе нет блока usage");
            var usage = usageObj as Dictionary<string, object>;
            if (usage == null) throw new Exception("Некорректный блок usage");

            var data = new UsageData();
            data.Rolling = ParseMeter(usage, "rolling");
            data.Weekly = ParseMeter(usage, "weekly");
            data.Monthly = ParseMeter(usage, "monthly");
            data.FetchedAt = DateTime.Now;
            return data;
        }

        private static Meter ParseMeter(Dictionary<string, object> usage, string key)
        {
            object obj;
            if (!usage.TryGetValue(key, out obj) || obj == null) throw new Exception("Нет метрики " + key);
            var d = obj as Dictionary<string, object>;
            if (d == null) throw new Exception("Некорректная метрика " + key);

            var m = new Meter();
            object p;
            if (d.TryGetValue("percent", out p) && p != null)
            {
                m.Percent = Math.Max(0, Math.Min(100, Convert.ToDouble(p, CultureInfo.InvariantCulture)));
            }
            object s;
            if (d.TryGetValue("status", out s) && s != null)
            {
                m.Limited = Convert.ToString(s) == "rate-limited";
            }
            object r;
            if (d.TryGetValue("resetsAt", out r) && r != null)
            {
                m.ResetsAt = ParseTime(Convert.ToString(r));
            }
            return m;
        }

        private static DateTime ParseTime(string value)
        {
            DateTime dt;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt;
            return DateTime.UtcNow;
        }
    }

    internal static class AuthStore
    {
        public static string AuthPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "opencode", "auth.json");
            }
        }

        public static string LoadKey(out string error)
        {
            error = null;
            var env = Environment.GetEnvironmentVariable("OPENCODE_GO_KEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();

            try
            {
                if (!File.Exists(AuthPath))
                {
                    error = "Не найден файл авторизации OpenCode: " + AuthPath;
                    return null;
                }

                var ser = new JavaScriptSerializer();
                var root = ser.DeserializeObject(File.ReadAllText(AuthPath)) as Dictionary<string, object>;
                if (root == null)
                {
                    error = "Файл auth.json повреждён";
                    return null;
                }

                object entryObj;
                if (root.TryGetValue("opencode-go", out entryObj) && entryObj != null)
                {
                    var entry = entryObj as Dictionary<string, object>;
                    if (entry != null)
                    {
                        object k;
                        if (entry.TryGetValue("key", out k) && k != null && Convert.ToString(k).Length > 0)
                            return Convert.ToString(k).Trim();
                        if (entry.TryGetValue("access", out k) && k != null && Convert.ToString(k).Length > 0)
                            return Convert.ToString(k).Trim();
                    }
                }

                error = "В auth.json нет ключа провайдера opencode-go. Подключите Go через /connect";
                return null;
            }
            catch (Exception ex)
            {
                error = "Не удалось прочитать auth.json: " + ex.Message;
                return null;
            }
        }
    }

    internal sealed class UsageClient
    {
        private const string Url = "https://opencode.ai/zen/go/v1/usage";
        private readonly string _key;

        public UsageClient(string key)
        {
            _key = key;
        }

        public UsageData Fetch()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _key);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "opencode-go-tray/1.0");

                var response = http.GetAsync(Url).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    string snippet = body == null ? "" : body.Replace("\n", " ");
                    if (snippet.Length > 160) snippet = snippet.Substring(0, 160) + "…";
                    throw new Exception("HTTP " + (int)response.StatusCode + ". " + snippet);
                }
                return UsageData.Parse(body);
            }
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "OpenCodeGoTray";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(Name) != null;
            }
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enabled) key.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(Name, false);
            }
        }
    }

    internal sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            var rect = new Rectangle(2, 1, item.Width - 4, item.Height - 2);
            if (item.Selected)
            {
                using (var b = new SolidBrush(Theme.MenuHover))
                using (var path = Draw.RoundRect(rect, 6))
                    e.Graphics.FillPath(b, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(226, 236, 248) : Color.FromArgb(110, 127, 151);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var p = new Pen(Color.FromArgb(42, 57, 79)))
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
            }
        }
    }

    internal sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientBegin { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.MenuBg; } }
        public override Color MenuBorder { get { return Color.FromArgb(45, 61, 85); } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return Theme.MenuHover; } }
        public override Color SeparatorDark { get { return Color.FromArgb(42, 57, 79); } }
        public override Color SeparatorLight { get { return Color.FromArgb(42, 57, 79); } }
    }

    internal static class Draw
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.1f, radius * 2f);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath RoundRect(Rectangle r, float radius)
        {
            return RoundRect(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }

        public static Color Mix(Color a, Color b, float amount)
        {
            int r = (int)Math.Round(a.R + (b.R - a.R) * amount);
            int g = (int)Math.Round(a.G + (b.G - a.G) * amount);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * amount);
            return Color.FromArgb(255, Clamp(r), Clamp(g), Clamp(bl));
        }

        private static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }
    }

    internal sealed class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Bitmap Render(double percent, bool error, bool loading, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                PaintIcon(g, size, percent, error, loading);
            }
            return bmp;
        }

        public static Icon Create(double percent, bool error, bool loading)
        {
            using (var bmp = Render(percent, error, loading, 32))
            {
                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(handle))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static void PaintIcon(Graphics g, int size, double percent, bool error, bool loading)
        {
            float k = size / 32f;
            var state = g.Save();
            g.ScaleTransform(k, k);

            Color color;
            string text;
            double value = 0;
            bool drawArc = false;

            if (loading)
            {
                color = Color.FromArgb(120, 132, 150);
                text = "…";
            }
            else if (error)
            {
                color = Color.FromArgb(255, 122, 122);
                text = "!";
                value = 100;
                drawArc = true;
            }
            else
            {
                color = Theme.ForPercent(percent);
                int rounded = (int)Math.Round(percent);
                if (rounded < 0) rounded = 0;
                if (rounded > 100) rounded = 100;
                text = rounded.ToString(CultureInfo.InvariantCulture);
                value = rounded;
                drawArc = rounded > 0;
            }

            const float cx = 8.1f;
            const float cy = 16f;
            const float radius = 5.8f;
            const float stroke = 2.8f;
            var ringRect = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);

            using (var trackPen = new Pen(Color.FromArgb(165, 126, 139, 158), stroke))
            {
                g.DrawEllipse(trackPen, ringRect);
            }

            if (drawArc)
            {
                using (var pen = new Pen(color, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float sweep = (float)(360.0 * Math.Max(0, Math.Min(100, value)) / 100.0);
                    if (sweep > 0.5f) g.DrawArc(pen, ringRect, -90f, sweep);
                }
            }

            var textZone = new RectangleF(16.2f, 0f, 15.6f, 32f);
            Font font = null;
            for (float fontSize = 13.5f; fontSize >= 5.5f; fontSize -= 0.5f)
            {
                var candidate = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
                var measured = g.MeasureString(text, candidate, new PointF(0, 0), StringFormat.GenericTypographic);
                if (measured.Width <= textZone.Width - 0.8f && measured.Height <= 27f)
                {
                    font = candidate;
                    break;
                }
                candidate.Dispose();
            }
            if (font == null) font = new Font("Segoe UI", 5.5f, FontStyle.Bold, GraphicsUnit.Point);

            using (font)
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            using (var brush = new SolidBrush(Draw.Mix(color, Color.White, 0.22f)))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, textZone, format);
            }

            g.Restore(state);
        }
    }

    internal sealed class PopupForm : Form
    {
        private const int LogicalWidth = 336;
        private const int LogicalHeight = 294;

        private readonly Font _titleFont;
        private readonly Font _subFont;
        private readonly Font _labelFont;
        private readonly Font _valueFont;
        private readonly Font _smallFont;
        private readonly Font _pillFont;
        private readonly Font _linkFont;
        private readonly Font _messageFont;

        private UsageData _data;
        private string _error;
        private bool _loading = true;

        private readonly float[] _shown = new float[3];
        private readonly float[] _target = new float[3];
        private readonly System.Windows.Forms.Timer _animTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _fadeTimer = new System.Windows.Forms.Timer();
        private DateTime _lastAnim;
        private double _lastDataStamp;

        private RectangleF _refreshRect;
        private RectangleF _consoleRect;
        private bool _hoverRefresh;
        private bool _hoverConsole;

        public DateTime HiddenAt { get; private set; }
        private DateTime _shownAt;

        public event EventHandler RefreshRequested;
        public event EventHandler ConsoleRequested;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "OpenCode Go";
            AllowTransparency = false;

            _titleFont = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
            _subFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _labelFont = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            _valueFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point);
            _smallFont = new Font("Segoe UI", 8.2f, FontStyle.Regular, GraphicsUnit.Point);
            _pillFont = new Font("Segoe UI", 7.6f, FontStyle.Bold, GraphicsUnit.Point);
            _linkFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _messageFont = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

            _animTimer.Interval = 16;
            _animTimer.Tick += OnAnimTick;

            _fadeTimer.Interval = 16;
            _fadeTimer.Tick += OnFadeTick;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        public float ScaleFactor
        {
            get
            {
                int dpi;
                try { dpi = DeviceDpi; }
                catch { dpi = 96; }
                if (dpi <= 0) dpi = 96;
                return dpi / 96f;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyScale();
            UpdateRegion();
        }

        private void ApplyScale()
        {
            float s = ScaleFactor;
            ClientSize = new Size((int)Math.Round(LogicalWidth * s), (int)Math.Round(LogicalHeight * s));
        }

        private void UpdateRegion()
        {
            using (var path = Draw.RoundRect(new RectangleF(0, 0, Width, Height), 14f * ScaleFactor))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        public void UpdateData(UsageData data, string error, bool loading)
        {
            double stamp = data == null ? 0 : data.FetchedAt.Ticks;
            _data = data;
            _error = error;
            _loading = loading;

            if (data != null)
            {
                _target[0] = (float)data.Rolling.Percent;
                _target[1] = (float)data.Weekly.Percent;
                _target[2] = (float)data.Monthly.Percent;
                if (!Visible)
                {
                    _shown[0] = _target[0]; _shown[1] = _target[1]; _shown[2] = _target[2];
                }
                _lastDataStamp = stamp;
            }
            else
            {
                _target[0] = 0; _target[1] = 0; _target[2] = 0;
            }

            Invalidate();
        }

        public void ShowNearTray(Rectangle anchor)
        {
            if (_data != null)
            {
                _shown[0] = 0; _shown[1] = 0; _shown[2] = 0;
            }

            ApplyScale();

            Rectangle wa;
            if (anchor.IsEmpty)
            {
                var screen = Screen.FromPoint(Cursor.Position);
                wa = screen.WorkingArea;
                Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            }
            else
            {
                wa = Screen.FromRectangle(anchor).WorkingArea;
                Location = new Point(anchor.Right - Width, anchor.Top - Height - 10);
            }

            if (Location.X < wa.Left + 8) Location = new Point(wa.Left + 8, Location.Y);
            if (Location.Y < wa.Top + 8) Location = new Point(Location.X, wa.Top + 8);

            Opacity = 0;
            _shownAt = DateTime.UtcNow;
            Show();
            BringToFront();
            Activate();

            _lastAnim = DateTime.UtcNow;
            _animTimer.Start();
            _fadeTimer.Start();
        }

        public void HidePopup()
        {
            HiddenAt = DateTime.UtcNow;
            Hide();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) return;
            if (Visible) HidePopup();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { HidePopup(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var back = new SolidBrush(Theme.Bg)) g.FillRectangle(back, ClientRectangle);

            float s = ScaleFactor;
            var state = g.Save();
            g.ScaleTransform(s, s);
            Render(g);
            g.Restore(state);
        }

        internal void Render(Graphics g)
        {
            float W = LogicalWidth;
            float H = LogicalHeight;

            using (var path = Draw.RoundRect(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 14f))
            using (var fill = new LinearGradientBrush(new RectangleF(0, 0, W, H), Theme.BGradient, Theme.Bg, 90f))
            {
                g.FillPath(fill, path);
            }

            using (var border = new Pen(Theme.Border, 1f))
            using (var path = Draw.RoundRect(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 14f))
            {
                g.DrawPath(border, path);
            }

            TextRenderer.DrawText(g, "", _titleFont, Point.Empty, Color.Transparent);

            using (var titleBrush = new SolidBrush(Theme.Title))
            {
                g.DrawString("OpenCode Go", _titleFont, titleBrush, 18f, 15f);
            }
            using (var subBrush = new SolidBrush(Theme.Subtle))
            {
                g.DrawString("лимиты подписки · % израсходовано", _subFont, subBrush, 18f, 39f);
            }

            DrawPill(g, W);

            using (var line = new Pen(Color.FromArgb(30, 43, 62), 1f))
            {
                g.DrawLine(line, 18f, 60f, W - 18f, 60f);
            }

            if (_data == null)
            {
                DrawMessage(g, W);
            }
            else
            {
                double[] percents = new double[] { _data.Rolling.Percent, _data.Weekly.Percent, _data.Monthly.Percent };
                DrawMeter(g, 68f, "5 часов", _shown[0], percents[0], _data.Rolling);
                DrawMeter(g, 134f, "Неделя", _shown[1], percents[1], _data.Weekly);
                DrawMeter(g, 200f, "Месяц", _shown[2], percents[2], _data.Monthly);
            }

            using (var line = new Pen(Color.FromArgb(30, 43, 62), 1f))
            {
                g.DrawLine(line, 18f, 258f, W - 18f, 258f);
            }

            DrawFooter(g, W);
        }

        private void DrawPill(Graphics g, float W)
        {
            string text;
            Color bg;
            Color fg;

            if (_data == null && _loading)
            {
                text = "ЗАГРУЗКА"; bg = Color.FromArgb(35, 45, 62); fg = Color.FromArgb(160, 175, 196);
            }
            else if (_data != null && _data.IsLimited)
            {
                text = "ЛИМИТ"; bg = Theme.PillWarnBg; fg = Theme.PillWarnText;
            }
            else if (_data == null)
            {
                text = "НЕТ ДАННЫХ"; bg = Theme.PillWarnBg; fg = Theme.PillWarnText;
            }
            else
            {
                text = "АКТИВНА"; bg = Theme.PillOkBg; fg = Theme.PillOkText;
            }

            var size = g.MeasureString(text, _pillFont);
            float width = size.Width + 16f;
            var rect = new RectangleF(W - 18f - width, 15f, width, 21f);

            using (var path = Draw.RoundRect(rect, 10.5f))
            using (var brush = new SolidBrush(bg))
            using (var pen = new Pen(Color.FromArgb(70, fg), 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (var brush = new SolidBrush(fg))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, _pillFont, brush, rect, format);
            }
        }

        private void DrawMeter(Graphics g, float y, string label, float animated, double actual, Meter meter)
        {
            Color color = Theme.ForPercent(actual);

            using (var labelBrush = new SolidBrush(Theme.Label))
            {
                g.DrawString(label, _labelFont, labelBrush, 18f, y);
            }

            string value = ((int)Math.Round(actual)).ToString(CultureInfo.InvariantCulture) + "%";
            var valueSize = g.MeasureString(value, _valueFont);
            using (var valueBrush = new SolidBrush(color))
            {
                g.DrawString(value, _valueFont, valueBrush, LogicalWidth - 18f - valueSize.Width, y - 4f);
            }

            if (meter.Limited)
            {
                using (var dot = new SolidBrush(Theme.Red))
                {
                    g.FillEllipse(dot, 18f + g.MeasureString(label, _labelFont).Width + 7f, y + 5f, 5f, 5f);
                }
            }

            var track = new RectangleF(18f, y + 23f, LogicalWidth - 36f, 6f);
            using (var path = Draw.RoundRect(track, 3f))
            using (var brush = new SolidBrush(Theme.Track))
            {
                g.FillPath(brush, path);
            }

            float pct = Math.Max(0f, Math.Min(100f, animated));
            if (pct > 0.4f)
            {
                float fillWidth = Math.Max(6f, (track.Width - 4f) * pct / 100f);
                var fill = new RectangleF(track.X + 2f, track.Y + 2f, fillWidth, track.Height - 4f);
                using (var path = Draw.RoundRect(fill, 1.5f))
                using (var brush = new LinearGradientBrush(
                    new RectangleF(fill.X, fill.Y - 8f, Math.Max(1f, fill.Width), fill.Height + 16f),
                    Draw.Mix(color, Color.White, 0.25f), color, 90f))
                {
                    g.FillPath(brush, path);
                }
            }

            string reset = ResetText(meter.ResetsAt);
            string left = "осталось " + (int)Math.Round(100 - actual) + "%";

            using (var smallBrush = new SolidBrush(Theme.Subtle))
            using (var format = new StringFormat())
            {
                g.DrawString(reset, _smallFont, smallBrush, 18f, y + 35f);
                format.Alignment = StringAlignment.Far;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(left, _smallFont, smallBrush, new RectangleF(18f, y + 35f, LogicalWidth - 36f, 14f), format);
            }
        }

        private void DrawMessage(Graphics g, float W)
        {
            string title = _loading ? "Получаем данные…" : "Нет данных";
            string detail = _error == null ? "OpenCode Go" : _error;

            using (var format = new StringFormat())
            using (var titleBrush = new SolidBrush(Theme.Label))
            {
                format.Alignment = StringAlignment.Center;
                g.DrawString(title, _labelFont, titleBrush, new RectangleF(18f, 110f, W - 36f, 24f), format);
            }

            using (var format = new StringFormat())
            using (var detailBrush = new SolidBrush(Theme.Subtle))
            {
                format.Alignment = StringAlignment.Center;
                g.DrawString(detail, _messageFont, detailBrush, new RectangleF(18f, 136f, W - 36f, 84f), format);
            }
        }

        private void DrawFooter(Graphics g, float W)
        {
            string stamp;
            Color stampColor = Theme.Footer;

            if (_data == null)
            {
                stamp = _error == null ? "подключение к opencode.ai" : "повторите попытку";
            }
            else
            {
                stamp = "обновлено в " + _data.FetchedAt.ToString("HH:mm");
                if (_error != null)
                {
                    stamp += " · нет связи";
                    stampColor = Theme.Amber;
                }
            }

            using (var brush = new SolidBrush(stampColor))
            {
                g.DrawString(stamp, _smallFont, brush, 18f, 266f);
            }

            var consoleSize = g.MeasureString("Консоль", _linkFont);
            float consoleX = W - 18f - consoleSize.Width;
            _consoleRect = new RectangleF(consoleX - 2f, 262f, consoleSize.Width + 4f, 16f);

            var refreshSize = g.MeasureString("Обновить", _linkFont);
            float refreshX = consoleX - 18f - refreshSize.Width;
            _refreshRect = new RectangleF(refreshX - 2f, 262f, refreshSize.Width + 4f, 16f);

            using (var brush = new SolidBrush(_hoverConsole ? Color.FromArgb(226, 236, 248) : Theme.Footer))
            {
                g.DrawString("Консоль", _linkFont, brush, consoleX, 264f);
            }
            using (var brush = new SolidBrush(_hoverRefresh ? Color.FromArgb(226, 236, 248) : Theme.Footer))
            {
                g.DrawString("Обновить", _linkFont, brush, refreshX, 264f);
            }

            if (_hoverRefresh)
            {
                using (var pen = new Pen(Color.FromArgb(90, 226, 236, 248), 1f))
                {
                    g.DrawLine(pen, refreshX, 278f, refreshX + refreshSize.Width, 278f);
                }
            }
            if (_hoverConsole)
            {
                using (var pen = new Pen(Color.FromArgb(90, 226, 236, 248), 1f))
                {
                    g.DrawLine(pen, consoleX, 278f, consoleX + consoleSize.Width, 278f);
                }
            }
        }

        private string ResetText(DateTime utc)
        {
            if (utc.Year < 2000) return "";
            DateTime local = utc.ToLocalTime();
            DateTime now = DateTime.Now;
            TimeSpan span = local - now;

            if (span.TotalHours < 24 && local.Date == now.Date)
                return "сброс в " + local.ToString("HH:mm");

            if (span.TotalDays < 7)
                return "сброс " + DayShort(local) + " " + local.ToString("HH:mm");

            return "сброс " + local.Day + " " + MonthShort(local);
        }

        private static string DayShort(DateTime dt)
        {
            string[] days = new string[] { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };
            return days[(int)dt.DayOfWeek];
        }

        private static string MonthShort(DateTime dt)
        {
            string[] months = new string[] { "янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек" };
            return months[dt.Month - 1];
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF p = ToLogical(e.Location);
            bool hoverRefresh = _refreshRect.Contains(p);
            bool hoverConsole = _consoleRect.Contains(p);

            if (hoverRefresh != _hoverRefresh || hoverConsole != _hoverConsole)
            {
                _hoverRefresh = hoverRefresh;
                _hoverConsole = hoverConsole;
                Cursor = hoverRefresh || hoverConsole ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            else
            {
                Cursor = hoverRefresh || hoverConsole ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRefresh || _hoverConsole)
            {
                _hoverRefresh = false;
                _hoverConsole = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            PointF p = ToLogical(e.Location);

            if (_refreshRect.Contains(p))
            {
                var handler = RefreshRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            if (_consoleRect.Contains(p))
            {
                var handler = ConsoleRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
        }

        private PointF ToLogical(Point point)
        {
            float s = ScaleFactor;
            return new PointF(point.X / s, point.Y / s);
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            bool moving = false;
            DateTime now = DateTime.UtcNow;
            double dt = Math.Max(0.008, (now - _lastAnim).TotalSeconds);
            _lastAnim = now;
            float step = (float)(1 - Math.Pow(0.002, dt));

            for (int i = 0; i < 3; i++)
            {
                float diff = _target[i] - _shown[i];
                if (Math.Abs(diff) < 0.25f)
                {
                    _shown[i] = _target[i];
                }
                else
                {
                    _shown[i] += diff * Math.Min(0.5f, step * 1.6f);
                    moving = true;
                }
            }

            Invalidate();
            if (!moving) _animTimer.Stop();
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            double next = Opacity + 0.18;
            if (next >= 1) { Opacity = 1; _fadeTimer.Stop(); }
            else Opacity = next;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
                _fadeTimer.Dispose();
                _titleFont.Dispose();
                _subFont.Dispose();
                _labelFont.Dispose();
                _valueFont.Dispose();
                _smallFont.Dispose();
                _pillFont.Dispose();
                _linkFont.Dispose();
                _messageFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Settings
    {
        private const string Root = @"Software\OpenCodeGoTray";

        public static int GetInt(string name, int fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Root))
                {
                    if (key == null) return fallback;
                    object value = key.GetValue(name);
                    if (value == null) return fallback;
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }
            catch { return fallback; }
        }

        public static void SetInt(string name, int value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Root))
                {
                    if (key != null) key.SetValue(name, value);
                }
            }
            catch { }
        }

        public static bool GetBool(string name, bool fallback)
        {
            return GetInt(name, fallback ? 1 : 0) != 0;
        }

        public static void SetBool(string name, bool value)
        {
            SetInt(name, value ? 1 : 0);
        }
    }

    internal sealed class BadgeForm : Form
    {
        private const int LogicalWidth = 96;
        private const int LogicalHeight = 52;

        private readonly Font _valueFont;
        private UsageData _data;
        private bool _dragging;
        private bool _moved;
        private bool _hover;
        private Point _dragStart;
        private Point _formStart;

        public event EventHandler OpenRequested;
        public event EventHandler Moved;

        public BadgeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Opacity = 0.97;
            Text = "OpenCode Go 5ч";

            _valueFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point);

            float scale = 1f;
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            }
            catch { }
            ClientSize = new Size((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public float ScaleFactor
        {
            get
            {
                int dpi;
                try { dpi = DeviceDpi; }
                catch { dpi = 96; }
                if (dpi <= 0) dpi = 96;
                return dpi / 96f;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UpdateRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = Draw.RoundRect(new RectangleF(0, 0, Width, Height), 16f * ScaleFactor))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        public void UpdateData(UsageData data)
        {
            _data = data;
            Invalidate();
        }

        public void PlaceDefault()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 10, wa.Bottom - Height - 8);
        }

        public bool IsPositionOnScreen(Point location)
        {
            var rect = new Rectangle(location, Size);
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(rect)) return true;
            }
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var back = new SolidBrush(Theme.Bg)) g.FillRectangle(back, ClientRectangle);

            float s = ScaleFactor;
            var state = g.Save();
            g.ScaleTransform(s, s);
            Render(g);
            g.Restore(state);
        }

        private void Render(Graphics g)
        {
            float w = LogicalWidth;
            float h = LogicalHeight;

            var area = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
            using (var path = Draw.RoundRect(area, 16f))
            using (var fill = new LinearGradientBrush(new RectangleF(0, 0, w, h), _hover ? Theme.BGradient : Theme.Bg, Theme.Bg, 90f))
            {
                g.FillPath(fill, path);
            }
            using (var path = Draw.RoundRect(area, 16f))
            using (var border = new Pen(_hover ? Color.FromArgb(64, 82, 110) : Theme.Border, 1f))
            {
                g.DrawPath(border, path);
            }

            Color color;
            string text;
            double value;
            bool drawArc;

            if (_data == null)
            {
                color = Color.FromArgb(120, 132, 150);
                text = "…";
                value = 0;
                drawArc = false;
            }
            else
            {
                value = _data.Rolling.Percent;
                color = Theme.ForPercent(value);
                int rounded = (int)Math.Round(value);
                if (rounded < 0) rounded = 0;
                if (rounded > 100) rounded = 100;
                text = rounded.ToString(CultureInfo.InvariantCulture) + "%";
                drawArc = rounded > 0;
            }

            const float cx = 26f;
            const float cy = 26f;
            const float radius = 11f;
            const float stroke = 5f;
            var ring = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);

            using (var track = new Pen(Color.FromArgb(150, 126, 139, 158), stroke))
            {
                g.DrawEllipse(track, ring);
            }

            if (drawArc)
            {
                using (var pen = new Pen(color, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float sweep = (float)(360.0 * Math.Max(0, Math.Min(100, value)) / 100.0);
                    if (sweep > 0.5f) g.DrawArc(pen, ring, -90f, sweep);
                }
            }

            var zone = new RectangleF(46f, 8f, 44f, 36f);
            Font font = null;
            for (float size = 16f; size >= 9f; size -= 0.5f)
            {
                var candidate = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);
                var measured = g.MeasureString(text, candidate, new PointF(0, 0), StringFormat.GenericTypographic);
                if (measured.Width <= zone.Width - 1f && measured.Height <= 34f)
                {
                    font = candidate;
                    break;
                }
                candidate.Dispose();
            }
            if (font == null) font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);

            using (font)
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            using (var brush = new SolidBrush(Draw.Mix(color, Color.White, 0.15f)))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, zone, format);
            }


        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _moved = false;
            _dragStart = Cursor.Position;
            _formStart = Location;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Point now = Cursor.Position;
            if (Math.Abs(now.X - _dragStart.X) > 3 || Math.Abs(now.Y - _dragStart.Y) > 3) _moved = true;
            if (_moved)
            {
                Location = new Point(_formStart.X + now.X - _dragStart.X, _formStart.Y + now.Y - _dragStart.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            Capture = false;

            if (_moved)
            {
                var handler = Moved;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            else
            {
                var handler = OpenRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _valueFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly PopupForm _popup;
        private readonly BadgeForm _badge;
        private string _key;
        private string _keyError;

        private UsageData _data;
        private string _error;
        private bool _fetching;
        private Icon _icon;
        private int _lastPercent = -1;
        private bool _lastError;
        private bool _lastLoading = true;
        private ToolStripMenuItem _autostartItem;
        private bool _welcomed;

        public TrayContext(bool openOnStart)
        {
            _key = AuthStore.LoadKey(out _keyError);

            _popup = new PopupForm();
            _popup.RefreshRequested += delegate { Refresh(true); };
            _popup.ConsoleRequested += delegate { OpenConsole(); };

            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.Text = "OpenCode Go";
            _tray.ContextMenuStrip = BuildMenu();
            _tray.MouseClick += OnTrayClick;
            SetIcon(0, false, true);

            _badge = new BadgeForm();
            _badge.ContextMenuStrip = _tray.ContextMenuStrip;
            _badge.OpenRequested += delegate
            {
                if (_popup.Visible) _popup.HidePopup();
                else ShowPopup();
            };
            _badge.Moved += delegate
            {
                Settings.SetInt("BadgeX", _badge.Left);
                Settings.SetInt("BadgeY", _badge.Top);
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 60000;
            _timer.Tick += delegate { Refresh(false); };
            _timer.Start();

            SetBadgeVisible(Settings.GetBool("Badge", true), false);

            Refresh(false);

            if (openOnStart)
            {
                var starter = new System.Windows.Forms.Timer();
                starter.Interval = 900;
                starter.Tick += delegate
                {
                    starter.Stop();
                    starter.Dispose();
                    ShowPopup();
                };
                starter.Start();
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.BackColor = Theme.MenuBg;
            menu.ForeColor = Color.FromArgb(226, 236, 248);
            menu.Renderer = new DarkRenderer();
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(4);
            menu.Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            var refresh = new ToolStripMenuItem("Обновить");
            refresh.ShortcutKeyDisplayString = "F5";
            refresh.Click += delegate { Refresh(true); };
            menu.Items.Add(refresh);

            var console = new ToolStripMenuItem("Открыть консоль");
            console.Click += delegate { OpenConsole(); };
            menu.Items.Add(console);

            var badgeItem = new ToolStripMenuItem("Крупный индикатор");
            badgeItem.CheckOnClick = true;
            badgeItem.Checked = Settings.GetBool("Badge", true);
            badgeItem.CheckedChanged += delegate
            {
                bool on = badgeItem.Checked;
                Settings.SetBool("Badge", on);
                SetBadgeVisible(on, true);
            };
            menu.Items.Add(badgeItem);

            var traySettings = new ToolStripMenuItem("Значок в трее…");
            traySettings.Click += delegate
            {
                try { Process.Start("ms-settings:taskbar"); }
                catch { }
            };
            menu.Items.Add(traySettings);

            menu.Items.Add(new ToolStripSeparator());

            _autostartItem = new ToolStripMenuItem("Запускать при входе");
            _autostartItem.CheckOnClick = true;
            _autostartItem.Checked = Autostart.IsEnabled();
            _autostartItem.CheckedChanged += delegate { Autostart.Set(_autostartItem.Checked); };
            menu.Items.Add(_autostartItem);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            return menu;
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (_popup.Visible)
            {
                _popup.HidePopup();
                return;
            }

            if ((DateTime.UtcNow - _popup.HiddenAt).TotalMilliseconds < 260) return;

            var opener = new System.Windows.Forms.Timer();
            opener.Interval = 60;
            opener.Tick += delegate
            {
                opener.Stop();
                opener.Dispose();
                if (!_popup.Visible) ShowPopup();
            };
            opener.Start();
        }

        private void ShowPopup()
        {
            _popup.UpdateData(_data, _error, _fetching);
            _popup.ShowNearTray(_badge != null && _badge.Visible ? _badge.Bounds : Rectangle.Empty);
            if (_data == null || (DateTime.Now - _data.FetchedAt).TotalSeconds > 30) Refresh(false);
        }

        private void SetBadgeVisible(bool visible, bool persist)
        {
            if (_badge == null) return;
            if (!visible)
            {
                _badge.Hide();
                return;
            }

            _badge.UpdateData(_data);
            var saved = new Point(Settings.GetInt("BadgeX", int.MinValue), Settings.GetInt("BadgeY", int.MinValue));
            if (saved.X != int.MinValue && saved.Y != int.MinValue && _badge.IsPositionOnScreen(saved))
                _badge.Location = saved;
            else
                _badge.PlaceDefault();

            if (!_badge.Visible) _badge.Show();
            _badge.BringToFront();
        }

        private void OpenConsole()
        {
            try { Process.Start("https://opencode.ai/console/go"); }
            catch { }
            if (_popup.Visible) _popup.HidePopup();
        }

        private void Refresh(bool userInitiated)
        {
            if (_fetching) return;
            _fetching = true;

            string refreshedKey;
            string refreshedError;
            refreshedKey = AuthStore.LoadKey(out refreshedError);
            if (refreshedKey != null)
            {
                _key = refreshedKey;
                _keyError = null;
            }
            else if (_key == null)
            {
                _keyError = refreshedError;
            }

            if (_key == null)
            {
                _error = _keyError;
                _fetching = false;
                ApplyUi();
                return;
            }

            string key = _key;
            IntPtr handle = _popup.Handle;

            Task.Run(delegate
            {
                UsageData data = null;
                string error = null;
                try { data = new UsageClient(key).Fetch(); }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    _popup.BeginInvoke((MethodInvoker)delegate
                    {
                        _fetching = false;
                        if (data != null)
                        {
                            _data = data;
                            _error = null;
                        }
                        else
                        {
                            _error = error;
                            if (_data != null && _error != null)
                                _error = "обновление не удалось: " + _error;
                        }
                        ApplyUi();
                    });
                }
                catch
                {
                    _fetching = false;
                }
            });
        }

        private void ApplyUi()
        {
            double percent = _data == null ? 0 : _data.Rolling.Percent;
            SetIcon(percent, _data == null && _error != null, _data == null && _error == null);
            UpdateTooltip();
            _popup.UpdateData(_data, _error, _fetching);
            if (_badge != null) _badge.UpdateData(_data);

            if (_data != null && !_welcomed) MaybeWelcome();
        }

        private void MaybeWelcome()
        {
            _welcomed = true;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\OpenCodeGoTray"))
                {
                    if (key == null || key.GetValue("Welcomed") != null) return;
                    key.SetValue("Welcomed", 1);
                    _tray.ShowBalloonTip(6000, "OpenCode Go",
                        "Виджет запущен. Если кружка с процентом не видно — перетащите его из меню ^ на панель задач.",
                        ToolTipIcon.Info);
                }
            }
            catch { }
        }

        private void UpdateTooltip()
        {
            string text;
            if (_data == null)
            {
                text = _error == null ? "OpenCode Go\nЗагрузка…" : "OpenCode Go\n" + Trim(_error, 100);
            }
            else
            {
                text = string.Format(CultureInfo.InvariantCulture,
                    "OpenCode Go\n5ч: {0}% · нед: {1}% · мес: {2}%",
                    (int)Math.Round(_data.Rolling.Percent),
                    (int)Math.Round(_data.Weekly.Percent),
                    (int)Math.Round(_data.Monthly.Percent));

                if (_data.Rolling.ResetsAt.Year > 2000)
                {
                    text += "\nСброс 5ч: " + _data.Rolling.ResetsAt.ToLocalTime().ToString("HH:mm");
                }
                if (_error != null) text += "\n⚠ " + Trim(_error, 60);
            }

            if (text.Length > 127) text = text.Substring(0, 127);
            _tray.Text = text;
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }

        private void SetIcon(double percent, bool error, bool loading)
        {
            int rounded = (int)Math.Round(percent);
            if (_icon != null && rounded == _lastPercent && error == _lastError && loading == _lastLoading) return;
            _lastPercent = rounded;
            _lastError = error;
            _lastLoading = loading;

            var next = IconFactory.Create(percent, error, loading);
            Icon previous = _icon;
            _icon = next;
            _tray.Icon = next;
            if (previous != null) previous.Dispose();
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _popup.Dispose();
            if (_badge != null) _badge.Dispose();
            if (_icon != null) _icon.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Preview
    {
        public static void Save(string path)
        {
            string keyError;
            string key = AuthStore.LoadKey(out keyError);
            UsageData data = null;
            string error = keyError;

            if (key != null)
            {
                var fetch = Task.Run(delegate { return new UsageClient(key).Fetch(); });
                if (fetch.Wait(12000))
                {
                    try { data = fetch.Result; }
                    catch (AggregateException ex)
                    {
                        error = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    }
                }
                else
                {
                    error = "таймаут запроса к opencode.ai";
                }
            }

            const int scale = 2;
            const int margin = 46;
            int width = 336 * scale + margin * 2;
            int height = 294 * scale + margin * 2 + 90;

            using (var bmp = new Bitmap(width, height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height),
                    Color.FromArgb(30, 41, 58), Color.FromArgb(12, 17, 26), 90f))
                {
                    g.FillRectangle(bg, 0, 0, width, height);
                }

                using (var iconBmp = IconFactory.Render(data == null ? 0 : data.Rolling.Percent, data == null, false, 32))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(iconBmp, new Rectangle(margin, 18, 160, 160));
                }

                using (var caption = new Font("Segoe UI", 9f))
                using (var brush = new SolidBrush(Color.FromArgb(150, 166, 188)))
                {
                    g.DrawString("иконка в трее (увеличено)", caption, brush, margin + 180, 86);
                    g.DrawString(data == null ? ("ошибка: " + Trim(error)) : ("данные: 5ч " + Math.Round(data.Rolling.Percent) + "%"),
                        caption, brush, margin + 180, 106);
                }

                var form = new PopupForm();
                form.UpdateData(data, error, false);
                using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    for (int i = 8; i > 0; i--)
                    {
                        using (var shadowPath = OpenCodeGoTray.Draw.RoundRect(new RectangleF(margin - i, margin + 90 - i, 336 * scale + i * 2, 294 * scale + i * 2), 18f + i))
                        {
                            using (var brush = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                            {
                                g.FillPath(brush, shadowPath);
                            }
                        }
                    }
                }

                var state = g.Save();
                g.TranslateTransform(margin, margin + 90);
                g.ScaleTransform(scale, scale);
                form.Render(g);
                g.Restore(state);

                form.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static string Trim(string value)
        {
            if (value == null) return "";
            return value.Length > 90 ? value.Substring(0, 90) + "…" : value;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool preview = args.Length > 0 && args[0] == "--shot";
            bool openOnStart = args.Length > 0 && args[0] == "--open";

            if (preview)
            {
                string path = args.Length > 1 ? args[1] : "preview.png";
                try
                {
                    Preview.Save(path);
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(path + ".err.txt", ex.ToString()); }
                    catch { }
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\OpenCodeGoTray", out created))
            {
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(openOnStart));

                GC.KeepAlive(mutex);
            }
        }
    }
}













