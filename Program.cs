using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

ApplicationConfiguration.Initialize();

// Где Windows хранит настройку «Доступ к камере»
const string camKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
const string camSystemKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

// ===== Действия без состояния: --action имя (блокировка, сон, выключение, перезагрузка) =====
int aIdx = Array.IndexOf(args, "--action");
if (aIdx >= 0 && aIdx + 1 < args.Length)
{
    var action = args[aIdx + 1].ToLowerInvariant();
    if (action == "lock")
    {
        // Защита от двойного клика: пока первая копия работает, вторая молча выходит
        if (!Guard.TryEnter("lock")) return;
        NativeMethods.LockWorkStation();
        Thread.Sleep(SystemInformation.DoubleClickTime);   // держим «табличку» ещё немного
        return;
    }
    if (action == "sleep")
    {
        if (!Guard.TryEnter("sleep")) return;
        NativeMethods.SetSuspendState(false, false, false);
        Thread.Sleep(SystemInformation.DoubleClickTime);   // иначе повторный клик усыпил бы компьютер снова после пробуждения
        return;
    }
    if (action == "shutdown")
    {
        // С приставкой --test компьютер в конце не выключается
        ShutdownCountdown.Run(false, args.Contains("--test"));
        return;
    }
    if (action == "restart")
    {
        // С приставкой --test компьютер в конце не перезагружается
        ShutdownCountdown.Run(true, args.Contains("--test"));
        return;
    }
}

// ===== Список выключателей: ключ из ярлыка -> название, переключение и «узнать состояние» =====
// Состояние: 1 = включён, 0 = выключен, -1 = нет такого устройства
// Результат переключения: 1 = включили, 0 = выключили, -1 = не найдено, -2 = не удалось
var switches = new Dictionary<string, (string Name, Func<Task<int>> Toggle, Func<Task<int>> GetState)>
{
    ["bluetooth"] = ("Bluetooth", () => ToggleRadio(RadioKind.Bluetooth), () => GetRadioState(RadioKind.Bluetooth)),
    ["wifi"] = ("Wi-Fi", () => ToggleRadio(RadioKind.WiFi), () => GetRadioState(RadioKind.WiFi)),
    ["mic"] = ("Микрофон", () => Task.FromResult(ToggleMicrophones()), () => Task.FromResult(GetMicState())),
    ["sound"] = ("Звук", () => Task.FromResult(ToggleSound()), () => Task.FromResult(GetSoundState())),
};

// ===== Плашка «Статус»: --status показывает состояние всех выключателей на 5 секунд =====
// Повторный запуск (ещё один клик по ярлыку) закрывает плашку, а не открывает вторую
if (args.Contains("--status"))
{
    // Одна копия: вторая просит первую закрыться и сразу выходит
    using var statusMutex = new Mutex(true, "OCS_StatusPanel", out bool statusIsNew);
    var statusCloseEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "OCS_StatusClose");
    if (!statusIsNew)
    {
        statusCloseEvent.Set();
        return;
    }
    var statusStarted = DateTime.Now;

    var rows = new List<(string Key, string Name, int State)>();
    foreach (var kv in switches)
        rows.Add((kv.Key, kv.Value.Name, kv.Value.GetState().GetAwaiter().GetResult()));

    // Размеры в пикселях умножаются на масштаб экрана: Ui.S(...)
    var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
    int rowH = Ui.S(54);
    int pad = Ui.S(10);
    var panel = new OsdForm
    {
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        ShowInTaskbar = false,
        TopMost = true,
        BackColor = Theme.Card,
        Size = new Size(Ui.S(360), pad * 2 + rows.Count * rowH),
        Opacity = 0.97
    };
    panel.Location = new Point(wa.Left + (wa.Width - panel.Width) / 2, wa.Bottom - panel.Height - Ui.S(48));

    panel.Paint += (s, e) =>
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Ui.DrawCardBorder(g, panel.ClientSize);
        using var nameFont = Ui.MakeFont("Segoe UI", 20);
        using var stateFont = Ui.MakeFont("Segoe UI", 20, FontStyle.Bold);
        for (int i = 0; i < rows.Count; i++)
        {
            float y = pad + i * rowH;
            int st = rows[i].State;
            var (col, tint) = Theme.State(st);
            // Значок функции в цветной плитке: серый и перечёркнутый, если выключено
            var tile = new RectangleF(pad + Ui.S(14), y + (rowH - Ui.S(36)) / 2f, Ui.S(36), Ui.S(36));
            Ui.DrawTile(g, tile, Ui.F(9), tint, rows[i].Key, col, st == 0, 1.9, 0.62f);
            float tx = tile.Right + Ui.S(14);
            Ui.DrawText(g, rows[i].Name, nameFont, Theme.Text,
                new RectangleF(tx, y, panel.Width - tx - Ui.S(130), rowH));
            string txt = st == 1 ? "вкл" : st == 0 ? "выкл" : "нет";
            Ui.DrawText(g, txt, stateFont, col,
                new RectangleF(panel.Width - pad - Ui.S(14) - Ui.S(110), y, Ui.S(110), rowH), StringAlignment.Far);
        }
    };
    panel.Click += (s, e) => panel.Close();

    var statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
    statusTimer.Tick += (s, e) => panel.Close();
    statusTimer.Start();

    // Фоновый «слушатель»: ждёт сигнала «закрой» от второй копии программы
    _ = panel.Handle;
    statusCloseEvent.Reset();   // клики, пришедшие, пока плашка ещё готовилась, не считаем
    var statusWaiter = new Thread(() =>
    {
        while (true)
        {
            statusCloseEvent.WaitOne();
            panel.BeginInvoke(new Action(() =>
            {
                // Время двойного щелчка Windows игнорируем: так случайный двойной клик не закроет плашку
                if ((DateTime.Now - statusStarted).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
                panel.Close();
            }));
        }
    })
    { IsBackground = true };
    statusWaiter.Start();

    Application.Run(panel);
    return;
}

// ===== Какой выключатель просят? Ищем в запуске слова: --toggle имя =====
string? key = null;
int idx = Array.IndexOf(args, "--toggle");
if (idx >= 0 && idx + 1 < args.Length) key = args[idx + 1].ToLowerInvariant();

if (key == null || !switches.ContainsKey(key))
{
    // Запуск без параметров (двойной щелчок по программе) или --setup: окно первого запуска
    if (args.Length == 0 || args.Contains("--setup"))
    {
        Application.Run(new FirstRunForm());
        return;
    }

    MessageBox.Show(
        "Запускайте программу через ярлыки, например:" + Environment.NewLine +
        "OneClickSwitcher.exe --toggle bluetooth" + Environment.NewLine +
        "OneClickSwitcher.exe --action lock" + Environment.NewLine +
        "OneClickSwitcher.exe --status" + Environment.NewLine + Environment.NewLine +
        "OneClickSwitcher.exe --setup (окно с созданием ярлыков)" + Environment.NewLine + Environment.NewLine +
        "Выключатели: " + string.Join(", ", switches.Keys) + Environment.NewLine +
        "Действия: lock, sleep, shutdown, restart" + Environment.NewLine + Environment.NewLine +
        "Получено: [" + string.Join(" | ", args) + "]",
        "One Click Switcher");
    return;
}
var sw = switches[key];

// ===== Защита от двойного клика =====
// У каждой функции своя именованная «табличка» (mutex). Пока она занята,
// вторая копия программы для этой же функции сразу выходит.
var guard = new Mutex(false, "OCS_Guard_" + key);
bool gotGuard;
try { gotGuard = guard.WaitOne(0); }
catch (AbandonedMutexException) { gotGuard = true; }   // прошлая копия аварийно закрылась: табличка свободна
if (!gotGuard) return;

// ===== Радиомодули (Bluetooth, Wi-Fi): узнать состояние =====
async Task<int> GetRadioState(RadioKind kind)
{
    var radios = await Radio.GetRadiosAsync();
    var r = radios.FirstOrDefault(x => x.Kind == kind);
    if (r == null) return -1;
    return r.State == RadioState.On ? 1 : 0;
}

// ===== Радиомодули (Bluetooth, Wi-Fi): переключить =====
async Task<int> ToggleRadio(RadioKind kind)
{
    var radios = await Radio.GetRadiosAsync();
    var r = radios.FirstOrDefault(x => x.Kind == kind);
    if (r == null) return -1;

    var access = await Radio.RequestAccessAsync();
    if (access != RadioAccessStatus.Allowed) return -2;

    var target = r.State == RadioState.On ? RadioState.Off : RadioState.On;
    var res = await r.SetStateAsync(target);
    if (res != RadioAccessStatus.Allowed) return -2;

    return target == RadioState.On ? 1 : 0;
}

// ===== Микрофоны: узнать состояние (1, если хоть один не заглушен) =====
int GetMicState()
{
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        var mics = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (mics.Count == 0) return -1;
        return mics.Any(m => !m.AudioEndpointVolume.Mute) ? 1 : 0;
    }
    catch
    {
        return -1;
    }
}

// ===== Микрофоны: глушим или включаем сразу все =====
int ToggleMicrophones()
{
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        var mics = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (mics.Count == 0) return -1;

        // Если хоть один микрофон не заглушен, то сейчас «включён»: глушим все
        bool anyOn = mics.Any(m => !m.AudioEndpointVolume.Mute);

        int changed = 0;
        foreach (var m in mics)
        {
            try { m.AudioEndpointVolume.Mute = anyOn; changed++; }
            catch { }
        }
        if (changed == 0) return -2;

        return anyOn ? 0 : 1;
    }
    catch
    {
        return -2;
    }
}

// ===== Звук: узнать состояние основного устройства вывода =====
int GetSoundState()
{
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return dev.AudioEndpointVolume.Mute ? 0 : 1;
    }
    catch
    {
        return -1;
    }
}

// ===== Звук: заглушаем основное устройство вывода =====
int ToggleSound()
{
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        bool muted = dev.AudioEndpointVolume.Mute;
        dev.AudioEndpointVolume.Mute = !muted;
        return muted ? 1 : 0;
    }
    catch
    {
        return -1;
    }
}

// ===== Камера: есть ли на компьютере хотя бы одна камера =====
async Task<bool> HasCamera()
{
    try
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Count > 0;
    }
    catch
    {
        return false;
    }
}

// Закрыт ли доступ к камере политикой на уровне всего компьютера
bool CameraDeniedBySystem()
{
    var v = Registry.GetValue(camSystemKey, "Value", null) as string;
    return string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
}

// Доступ приложений к камере: 1 = разрешён, 0 = запрещён
int ReadCameraAccess()
{
    if (CameraDeniedBySystem()) return 0;
    var v = Registry.GetValue(camKey, "Value", null) as string;
    return string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}

// Записать настройку так же, как это делает сама Windows: значение и время изменения
void WriteCameraAccess(string value)
{
    Registry.SetValue(camKey, "Value", value, RegistryValueKind.String);
    Registry.SetValue(camKey, "LastSetTime", DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
}

// ===== Камера: узнать состояние =====
async Task<int> GetCameraState()
{
    // Если доступ закрыт, показываем «выключен» даже когда камера не определяется: так можно вернуть доступ
    if (ReadCameraAccess() == 0) return 0;
    return await HasCamera() ? 1 : -1;
}

// ===== Камера: разрешить или запретить доступ приложениям =====
async Task<int> ToggleCamera()
{
    try
    {
        if (ReadCameraAccess() == 0)
        {
            // Если закрыто на уровне компьютера, обычная программа это изменить не может
            if (CameraDeniedBySystem()) return -2;
            WriteCameraAccess("Allow");
        }
        else
        {
            if (!await HasCamera()) return -1;
            WriteCameraAccess("Deny");
        }
        // Читаем обратно: так видно, что запись действительно прошла
        return ReadCameraAccess();
    }
    catch
    {
        return -2;
    }
}

// ===== Звук щелчка: собираем из чисел и превращаем в WAV прямо в памяти =====
const int rate = 44100;

void AddSilence(List<short> samples, int ms)
{
    for (int i = 0; i < rate * ms / 1000; i++) samples.Add(0);
}

void AddClick(List<short> samples, double clickHz, double thumpHz, double volume)
{
    int n = rate * 90 / 1000;
    var rnd = new Random(1);
    for (int i = 0; i < n; i++)
    {
        double t = (double)i / rate;
        double click = Math.Sin(2 * Math.PI * clickHz * t) * Math.Exp(-t / 0.004);
        double thump = Math.Sin(2 * Math.PI * thumpHz * t) * Math.Exp(-t / 0.018);
        double noise = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t / 0.0015);
        double v = (click * 0.6 + thump * 0.7 + noise * 0.35) * volume * SoundSettings.Level;
        samples.Add((short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue));
    }
}

MemoryStream ToWav(List<short> samples)
{
    var stream = new MemoryStream();
    var w = new BinaryWriter(stream);
    int dataLen = samples.Count * 2;
    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    w.Write(36 + dataLen);
    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
    w.Write(16);
    w.Write((short)1);
    w.Write((short)1);
    w.Write(rate);
    w.Write(rate * 2);
    w.Write((short)2);
    w.Write((short)16);
    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    w.Write(dataLen);
    foreach (var s in samples) w.Write(s);
    w.Flush();
    stream.Position = 0;
    return stream;
}

// ===== Выполняем действие =====
int result = sw.Toggle().GetAwaiter().GetResult();

// ===== Выбираем надпись и звук (цвет плашки зависит от состояния: Theme.State) =====
string line;
var samples = new List<short>();
AddSilence(samples, 100);

if (result == 1)
{
    line = "Включён";
    AddClick(samples, 2600, 220, 0.45);
}
else if (result == 0)
{
    line = "Выключен";
    AddClick(samples, 1500, 130, 0.45);
}
else
{
    line = result == -1 ? "Не найден" : "Не удалось";
    AddClick(samples, 700, 90, 0.5);
    AddSilence(samples, 110);
    AddClick(samples, 700, 90, 0.5);
}

var player = new SoundPlayer(ToWav(samples));
player.Play();

// ===== Показываем плашку внизу по центру экрана на 1,3 секунды =====
string glyphKey = key;   // здесь key уже точно не пустой
var (stateColor, stateTint) = Theme.State(result);
var area = Screen.FromPoint(Cursor.Position).WorkingArea;
var osd = new OsdForm
{
    FormBorderStyle = FormBorderStyle.None,
    StartPosition = FormStartPosition.Manual,
    ShowInTaskbar = false,
    TopMost = true,
    BackColor = Theme.Card,
    Size = new Size(Ui.S(300), Ui.S(110)),
    Opacity = 0.97
};
osd.Location = new Point(area.Left + (area.Width - osd.Width) / 2, area.Bottom - osd.Height - Ui.S(48));
osd.Paint += (s, e) =>
{
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    Ui.DrawCardBorder(g, osd.ClientSize);

    // Слева значок функции в плитке цвета состояния (перечёркнутый, если выключено)
    float tileSize = Ui.S(56);
    var tile = new RectangleF(Ui.S(22), (osd.Height - tileSize) / 2f, tileSize, tileSize);
    Ui.DrawTile(g, tile, Ui.F(14), stateTint, glyphKey, stateColor, result == 0, 1.9, 0.54f);

    // Справа название и состояние
    using var nameFont = Ui.MakeFont("Segoe UI Semibold", 22);
    using var stateFont = Ui.MakeFont("Segoe UI", 17, FontStyle.Bold);
    float tx = tile.Right + Ui.S(16);
    float tw = osd.Width - tx - Ui.S(16);
    Ui.DrawText(g, sw.Name, nameFont, Theme.Text, new RectangleF(tx, Ui.S(24), tw, Ui.S(32)));
    Ui.DrawText(g, line, stateFont, stateColor, new RectangleF(tx, Ui.S(56), tw, Ui.S(28)));
};

var closeTimer = new System.Windows.Forms.Timer { Interval = 1300 };
closeTimer.Tick += (s, e) => osd.Close();
closeTimer.Start();

// Табличку снимаем через время двойного щелчка Windows (обычно 500 мс)
var guardTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
guardTimer.Tick += (s, e) => { guardTimer.Stop(); guard.ReleaseMutex(); };
guardTimer.Start();

Application.Run(osd);
GC.KeepAlive(player);

// ===== Типы: обязательно в самом конце файла =====

// ----- Оформление: цвета, масштаб, значки -----

// Масштаб экрана: на экране 150% все размеры умножаются на 1,5
static class Ui
{
    public static readonly float K = GetScale();

    static float GetScale()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96f;
        }
        catch { return 1f; }
    }

    public static int S(double v) => (int)Math.Round(v * K);
    public static float F(double v) => (float)(v * K);

    // Шрифт в пикселях (а не в пунктах): так размер совпадает с эскизами
    public static Font MakeFont(string family, double px, FontStyle style = FontStyle.Regular)
        => new Font(family, F(px), style, GraphicsUnit.Pixel);

    // Тонкая рамка вокруг плашки
    public static void DrawCardBorder(Graphics g, Size size)
    {
        using var pen = new Pen(Theme.Line);
        g.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
    }

    // Текст в прямоугольнике: одна строка, по центру по вертикали, длинное обрезается многоточием
    public static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r,
        StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
    {
        using var brush = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = h,
            LineAlignment = v,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(s, f, brush, r, fmt);
    }

    // Скруглённая плитка с рисунком внутри
    public static void DrawTile(Graphics g, RectangleF r, float radius, Color bg, string glyph, Color fg,
        bool slash, double stroke = 1.9, float glyphRatio = 0.54f)
    {
        using (var path = Shapes.Round(r, radius))
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);

        float gs = r.Width * glyphRatio;
        var box = new RectangleF(r.X + (r.Width - gs) / 2f, r.Y + (r.Height - gs) / 2f, gs, gs);
        Glyphs.Draw(g, glyph, box, fg, stroke, slash);
    }
}

// Цвета: светлые или тёмные, как в самой Windows (настройка «Режим по умолчанию для приложений»)
static class Theme
{
    public static readonly bool Light = DetectLight();

    static bool DetectLight()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return !(v is int i && i == 0);
        }
        catch { return true; }
    }

    static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    static Color Pick(int light, int dark) => C(Light ? light : dark);

    public static Color Card => Pick(0xf9fafc, 0x363942);
    public static Color Line => Pick(0xd3d8e2, 0x4d505b);
    public static Color Text => Pick(0x16181d, 0xf1f2f5);
    public static Color Sub => Pick(0x3f4552, 0xb6bbc6);
    public static Color On => Pick(0x0a5cb8, 0x7db8ff);
    public static Color OnBg => Pick(0xe3eefb, 0x2a4465);
    public static Color Mute => Pick(0x555b68, 0xa8aebb);
    public static Color MuteBg => Pick(0xe9ebf0, 0x474b58);
    public static Color Bad => Pick(0xb3261e, 0xff8f86);
    public static Color BadBg => Pick(0xfbe7e5, 0x5c3532);
    public static Color Row => Pick(0xffffff, 0x2f3139);
    public static Color Btn => Pick(0xffffff, 0x444854);
    public static Color BtnLine => Pick(0xc3c9d6, 0x5e6270);
    public static Color Note => Pick(0xe9ebf0, 0x2f3139);
    public static Color Primary => Pick(0x0a5cb8, 0x2f6fc8);
    public static Color Tile => C(0x2f3b52);   // плитка значка ярлыка

    // Цвет значка и фона плитки для состояния: 1 = включено, 0 = выключено, иначе ошибка
    public static (Color Fg, Color Bg) State(int st)
        => st == 1 ? (On, OnBg) : st == 0 ? (Mute, MuteBg) : (Bad, BadBg);
}

// Скруглённый прямоугольник как контур
static class Shapes
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Рисунки значков: линиями на сетке 24×24, как в эскизах
static class Glyphs
{
    // Дуга окружности радиуса r из точки (x1,y1) в точку (x2,y2): как команда «a» в SVG
    static void ArcTo(GraphicsPath p, double x1, double y1, double x2, double y2, double r, bool large, bool clockwise)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double q = Math.Sqrt(dx * dx + dy * dy);
        double h = Math.Sqrt(Math.Max(0, r * r - q * q / 4));
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double nx = -dy / q, ny = dx / q;
        foreach (int sign in new[] { 1, -1 })
        {
            double cx = mx + sign * h * nx, cy = my + sign * h * ny;
            double a1 = Math.Atan2(y1 - cy, x1 - cx) * 180 / Math.PI;
            double a2 = Math.Atan2(y2 - cy, x2 - cx) * 180 / Math.PI;
            double cw = (a2 - a1) % 360;
            if (cw < 0) cw += 360;
            double sweep = clockwise ? cw : cw - 360;
            if (h < 1e-6 || (Math.Abs(sweep) > 180.0001) == large)
            {
                p.AddArc((float)(cx - r), (float)(cy - r), (float)(2 * r), (float)(2 * r), (float)a1, (float)sweep);
                return;
            }
        }
    }

    // Рисует значок name в квадрате box. slash = true: перечёркивает (функция выключена)
    public static void Draw(Graphics g, string name, RectangleF box, Color color, double stroke, bool slash)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        float k = box.Width / 24f;
        g.ScaleTransform(k, k);

        using var pen = new Pen(color, (float)stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);
        using var p = new GraphicsPath();

        switch (name)
        {
            case "bluetooth":
                g.DrawLines(pen, new[]
                {
                    new PointF(7, 7), new PointF(17, 17), new PointF(12, 22),
                    new PointF(12, 2), new PointF(17, 7), new PointF(7, 17)
                });
                break;

            case "wifi":
                p.StartFigure(); ArcTo(p, 2, 9, 22, 9, 15, false, true);
                p.StartFigure(); ArcTo(p, 5.5, 12.5, 18.5, 12.5, 10, false, true);
                p.StartFigure(); ArcTo(p, 9, 16, 15, 16, 5, false, true);
                g.DrawPath(pen, p);
                g.FillEllipse(brush, 10.8f, 18.3f, 2.4f, 2.4f);
                break;

            case "mic":
                p.AddArc(9f, 3f, 6f, 6f, 180, 180);
                p.AddArc(9f, 9f, 6f, 6f, 0, 180);
                p.CloseFigure();
                p.StartFigure(); ArcTo(p, 5, 11, 19, 11, 7, false, false);
                p.StartFigure(); p.AddLine(12, 18, 12, 21);
                g.DrawPath(pen, p);
                break;

            case "sound":
                g.DrawPolygon(pen, new[]
                {
                    new PointF(4, 9), new PointF(4, 15), new PointF(8, 15),
                    new PointF(13, 19), new PointF(13, 5), new PointF(8, 9)
                });
                p.StartFigure(); ArcTo(p, 16, 9, 16, 15, 4, false, true);
                p.StartFigure(); ArcTo(p, 18.5, 6.5, 18.5, 17.5, 8, false, true);
                g.DrawPath(pen, p);
                break;

            case "lock":
                g.DrawRectangle(pen, 6f, 11f, 12f, 9f);
                p.AddLine(8, 11, 8, 8);
                p.AddArc(8f, 4f, 8f, 8f, 180, 180);
                p.AddLine(16, 8, 16, 11);
                g.DrawPath(pen, p);
                break;

            case "sleep":
                p.StartFigure();
                ArcTo(p, 20, 14.5, 9.5, 4, 8, false, true);
                ArcTo(p, 9.5, 4, 20, 14.5, 8, true, false);
                p.CloseFigure();
                g.DrawPath(pen, p);
                break;

            case "shutdown":
                p.StartFigure(); p.AddLine(12, 3, 12, 12);
                p.StartFigure(); ArcTo(p, 6.4, 6.6, 17.6, 6.6, 8, true, false);
                g.DrawPath(pen, p);
                break;

            case "restart":
                p.StartFigure(); ArcTo(p, 20, 12, 17.5, 6.2, 8, true, true);
                p.StartFigure();
                p.AddLine(20, 4, 20, 9);
                p.AddLine(20, 9, 15, 9);
                g.DrawPath(pen, p);
                break;

            case "check":
                g.DrawLines(pen, new[] { new PointF(4, 12.5f), new PointF(9.5f, 18), new PointF(20, 6.5f) });
                break;

            default:   // status: четыре квадрата
                g.DrawRectangle(pen, 4f, 4f, 6f, 6f);
                g.DrawRectangle(pen, 14f, 4f, 6f, 6f);
                g.DrawRectangle(pen, 4f, 14f, 6f, 6f);
                g.DrawRectangle(pen, 14f, 14f, 6f, 6f);
                break;
        }

        if (slash) g.DrawLine(pen, 4, 20, 20, 4);
        g.Restore(state);
    }
}

// Файлы значков (.ico) для ярлыков: рисуем сами, в нескольких размерах
static class IconMaker
{
    static byte[] Png(string key, int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // На мелких размерах линию делаем толще, чтобы значок не пропадал
            double stroke = size <= 32 ? 2.5 : 2.0;
            Ui.DrawTile(g, new RectangleF(0, 0, size, size), size * 0.22f, Theme.Tile, key, Color.White, false, stroke, 0.62f);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    // Собирает содержимое файла .ico (внутри обычные PNG)
    public static byte[] Build(string key)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var pngs = sizes.Select(s => Png(key, s)).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    public static string Save(string key, string dir)
    {
        string path = Path.Combine(dir, key + ".ico");
        File.WriteAllBytes(path, Build(key));
        return path;
    }
}

// Создание ярлыков на рабочем столе
static class Shortcuts
{
    // Ключ значка, название ярлыка, параметры запуска
    public static readonly (string Key, string Title, string Args)[] All =
    {
        ("bluetooth", "Bluetooth", "--toggle bluetooth"),
        ("wifi", "Wi-Fi", "--toggle wifi"),
        ("mic", "Микрофон", "--toggle mic"),
        ("sound", "Звук", "--toggle sound"),
        ("lock", "Блокировка", "--action lock"),
        ("sleep", "Сон", "--action sleep"),
        ("status", "Статус", "--status"),
        ("shutdown", "Выключение", "--action shutdown"),
        ("restart", "Перезагрузка", "--action restart"),
    };

    // Значки лежат отдельно от программы, в папке пользователя
    public static string IconsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneClickSwitcher", "icons");

    // Постоянная папка программы: в профиле пользователя, права администратора не нужны
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OneClickSwitcher");

    // Копирует программу в постоянную папку и возвращает путь к копии.
    // copied = false, если программа уже запущена оттуда (копировать нечего).
    // Если копия занята (например, открыт отсчёт выключения), бросает исключение
    public static string Install(out bool copied)
    {
        string exe = Environment.ProcessPath ?? "";
        string target = Path.Combine(InstallDir, "OneClickSwitcher.exe");
        copied = false;
        if (exe == "") throw new InvalidOperationException("Не удалось определить путь к программе");
        if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return exe;

        Directory.CreateDirectory(InstallDir);
        File.Copy(exe, target, true);
        copied = true;
        return target;
    }

    // Создаёт ярлыки на программу exe. Возвращает, сколько ярлыков создано
    public static int CreateOnDesktop(IEnumerable<string> keys, string exe)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (exe == "" || shellType == null) return 0;

        Directory.CreateDirectory(IconsDir);
        var wanted = new HashSet<string>(keys);
        dynamic shell = Activator.CreateInstance(shellType)!;
        int created = 0;
        foreach (var item in All)
        {
            if (!wanted.Contains(item.Key)) continue;
            try
            {
                string ico = IconMaker.Save(item.Key, IconsDir);
                dynamic lnk = shell.CreateShortcut(Path.Combine(desktop, item.Title + ".lnk"));
                lnk.TargetPath = exe;
                lnk.Arguments = item.Args;
                lnk.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
                lnk.IconLocation = ico + ",0";
                lnk.Description = "One Click Switcher: " + item.Title;
                lnk.Save();
                created++;
            }
            catch { }
        }
        return created;
    }
}

// Квадратик с рисунком значка (для заголовка окна)
class GlyphBox : Control
{
    public string Key = "status";
    public Color? TileColor;    // если не задан, тёмно-синяя плитка и белый рисунок
    public Color? GlyphColor;

    public GlyphBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        Ui.DrawTile(g, new RectangleF(0, 0, Width, Height), Ui.F(12), TileColor ?? Theme.Tile, Key,
            GlyphColor ?? Color.White, false, 1.9, 0.56f);
    }
}

// Строка выбора: отметка, значок и название. Нажимается мышью и пробелом
class CheckRow : Control
{
    public string Key = "";
    public string Title = "";
    public bool Checked;

    public CheckRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
        Focus();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            Invalidate();
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        // Рамка и фон строки
        var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
        using (var path = Shapes.Round(rect, Ui.F(10)))
        {
            using var fill = new SolidBrush(Theme.Row);
            g.FillPath(fill, path);
            using var border = new Pen(Focused ? Theme.On : Theme.Line, Focused ? 2f : 1f);
            g.DrawPath(border, path);
        }

        // Отметка
        float cb = Ui.F(20);
        float cx = Ui.F(14);
        float cy = (Height - cb) / 2f;
        using (var box = Shapes.Round(new RectangleF(cx, cy, cb, cb), Ui.F(5)))
        {
            if (Checked)
            {
                using var on = new SolidBrush(Theme.Primary);
                g.FillPath(on, box);
                using var tick = new Pen(Color.White, Ui.F(2.2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(cx + cb * 0.22f, cy + cb * 0.53f),
                    new PointF(cx + cb * 0.43f, cy + cb * 0.72f),
                    new PointF(cx + cb * 0.78f, cy + cb * 0.30f)
                });
            }
            else
            {
                using var off = new Pen(Theme.Sub, Ui.F(1.6));
                g.DrawPath(off, box);
            }
        }

        // Значок и название
        float t = Ui.F(30);
        var tile = new RectangleF(cx + cb + Ui.F(12), (Height - t) / 2f, t, t);
        Ui.DrawTile(g, tile, Ui.F(8), Theme.Tile, Key, Color.White, false, 2.2, 0.60f);
        using var font = Ui.MakeFont("Segoe UI", 17);
        Ui.DrawText(g, Title, font, Theme.Text, new RectangleF(tile.Right + Ui.F(12), 0, Width - tile.Right - Ui.F(16), Height));
    }
}

// Окно первого запуска: выбор ярлыков и подсказка, как закрепить их на панели задач
class FirstRunForm : Form
{
    public FirstRunForm()
    {
        Text = "One Click Switcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;

        try { Icon = new Icon(new MemoryStream(IconMaker.Build("shutdown"))); } catch { }

        // Если экран маленький, окно становится ниже и получает полосу прокрутки
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int fullHeight = Ui.S(736);
        ClientSize = new Size(Ui.S(680), Math.Min(fullHeight, Math.Max(Ui.S(300), wa.Height - Ui.S(90))));
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, fullHeight);

        // Заголовок
        Controls.Add(new GlyphBox
        {
            Key = "shutdown",
            Location = new Point(Ui.S(24), Ui.S(24)),
            Size = new Size(Ui.S(48), Ui.S(48))
        });
        AddLabel("One Click Switcher", 84, 24, 560, 30, 22, "Segoe UI Semibold");
        AddLabel("Один клик по ярлыку, и функция переключена.", 84, 54, 560, 22, 15, color: Theme.Sub);

        // Шаг 1: выбор ярлыков (после создания этот блок заменяется сообщением «Готово»)
        var step1 = new List<Control>();
        step1.Add(AddLabel("1. Какие ярлыки создать на рабочем столе", 24, 92, 632, 26, 17, "Segoe UI Semibold"));
        var rows = new List<CheckRow>();
        string[] order = { "bluetooth", "wifi", "mic", "sound", "lock", "sleep", "status", "shutdown", "restart" };
        for (int i = 0; i < order.Length; i++)
        {
            var item = Shortcuts.All.First(a => a.Key == order[i]);
            var row = new CheckRow
            {
                Key = item.Key,
                Title = item.Title,
                // Выключение и перезагрузка по умолчанию не отмечены
                Checked = item.Key != "shutdown" && item.Key != "restart",
                Location = new Point(Ui.S(24 + (i % 2) * 320), Ui.S(126 + (i / 2) * 50)),
                Size = new Size(Ui.S(312), Ui.S(44))
            };
            rows.Add(row);
            step1.Add(row);
            Controls.Add(row);
        }
        step1.Add(AddLabel("Выключение и перезагрузка по умолчанию не отмечены: отметьте их, если они нужны.",
            24, 376, 632, 22, 14, color: Theme.Sub));

        // Шаг 2: закрепление на панели задач
        var lower = new List<Control>();   // всё, что поднимется вверх после создания ярлыков
        var step2Title = AddLabel("2. Закрепите ярлыки на панели задач", 24, 408, 632, 26, 17, "Segoe UI Semibold");
        lower.Add(step2Title);
        lower.Add(AddLabel("Ярлыки появятся на рабочем столе. Закреплять их нужно оттуда:", 24, 436, 632, 22, 15));
        lower.Add(AddLabel(
            "1. Нажмите на ярлык правой кнопкой мыши." + Environment.NewLine +
            "2. Если в меню есть «Закрепить на панели задач», выберите его. Так бывает в Windows 10 и в части версий Windows 11." + Environment.NewLine +
            "3. Если такого пункта нет, выберите «Показать дополнительные параметры», а в открывшемся меню «Закрепить на панели задач».",
            24, 460, 632, 106, 15));

        // Заметка
        var note = AddLabel(
            "Закреплённые значки работают сами по себе, поэтому ярлыки с рабочего стола после этого можно удалить. " +
            "Саму программу, которую мы скопируем в постоянную папку, удалять и переносить нельзя: ярлыки ведут в неё.",
            24, 574, 632, 84, 15);
        note.BackColor = Theme.Note;
        note.Padding = new Padding(Ui.S(14), Ui.S(10), Ui.S(14), Ui.S(10));
        lower.Add(note);

        // Кнопки и результат
        var result = AddLabel("Программа скопируется в вашу папку пользователя, ярлыки будут вести туда.",
            24, 672, 320, 40, 13, color: Theme.Sub);
        result.TextAlign = ContentAlignment.MiddleLeft;
        var close = AddButton("Закрыть", 536, 120, false);
        var create = AddButton("Создать ярлыки", 354, 170, true);
        lower.Add(result);
        lower.Add(close);
        lower.Add(create);
        close.Click += (s, e) => Close();
        create.Click += (s, e) =>
        {
            var chosen = rows.Where(r => r.Checked).Select(r => r.Key).ToList();
            if (chosen.Count == 0)
            {
                result.ForeColor = Theme.Sub;
                result.Text = "Ничего не выбрано.";
                return;
            }

            // Сначала копируем программу в постоянную папку, потом делаем ярлыки на копию
            string exePath;
            bool copied;
            try
            {
                exePath = Shortcuts.Install(out copied);
            }
            catch
            {
                result.ForeColor = Theme.Bad;
                result.Text = "Не удалось скопировать программу. Закройте окна One Click Switcher и повторите.";
                return;
            }

            int n;
            try
            {
                n = Shortcuts.CreateOnDesktop(chosen, exePath);
            }
            catch
            {
                n = 0;
            }
            if (n == 0)
            {
                result.ForeColor = Theme.Bad;
                result.Text = "Не удалось создать ярлыки.";
                return;
            }

            // Готово: убираем выбор ярлыков и показываем крупное сообщение, что всё сделано
            Beeper.Play(0.35, (659.0, 100), (880.0, 180));
            foreach (var c in step1) c.Visible = false;
            create.Visible = false;
            result.Visible = false;

            Controls.Add(new GlyphBox
            {
                Key = "check",
                TileColor = Theme.OnBg,
                GlyphColor = Theme.On,
                Location = new Point(Ui.S(24), Ui.S(100)),
                Size = new Size(Ui.S(56), Ui.S(56))
            });
            AddLabel("Готово: ярлыки созданы", 96, 98, 560, 34, 24, "Segoe UI Semibold");
            AddLabel("Создано ярлыков: " + n + ". Они лежат на рабочем столе. Установка закончена, окно можно закрыть.",
                96, 134, 560, 48, 16, color: Theme.Sub);
            AddLabel(copied
                    ? "Программа скопирована в папку " + Shortcuts.InstallDir + ". Файл, который вы запускали, теперь можно удалить."
                    : "Программа уже работает из своей постоянной папки: " + Shortcuts.InstallDir + ".",
                24, 188, 632, 44, 15, color: Theme.Sub);
            AddLabel("Осталось закрепить нужные ярлыки на панели задач. Как это сделать, написано ниже.",
                24, 236, 632, 26, 16);

            // Остальное поднимаем вверх на место выбора ярлыков
            int delta = Ui.S(408 - 276);
            foreach (var c in lower) c.Top -= delta;
            close.Left = Ui.S(656) - close.Width;     // кнопка «Закрыть» становится главной и правее
            close.BackColor = Theme.Primary;
            close.ForeColor = Color.White;
            close.FlatAppearance.BorderColor = Theme.Primary;
            int newFull = Ui.S(736) - delta;
            AutoScrollMinSize = new Size(0, newFull);
            ClientSize = new Size(ClientSize.Width, Math.Min(newFull, ClientSize.Height));
        };
        AcceptButton = create;
        CancelButton = close;
    }

    // Надпись: координаты и размеры в пикселях эскиза, масштаб добавляется сам
    Label AddLabel(string text, double x, double y, double w, double h, double px,
        string family = "Segoe UI", Color? color = null)
    {
        var l = new Label
        {
            AutoSize = false,
            Text = text,
            Location = new Point(Ui.S(x), Ui.S(y)),
            Size = new Size(Ui.S(w), Ui.S(h)),
            Font = Ui.MakeFont(family, px),
            ForeColor = color ?? Theme.Text,
            BackColor = Color.Transparent
        };
        Controls.Add(l);
        return l;
    }

    Button AddButton(string text, double x, double w, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(Ui.S(x), Ui.S(672)),
            Size = new Size(Ui.S(w), Ui.S(40)),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Theme.Primary : Theme.Btn,
            ForeColor = primary ? Color.White : Theme.Text,
            Font = Ui.MakeFont("Segoe UI Semibold", 16)
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? Theme.Primary : Theme.BtnLine;
        Controls.Add(b);
        return b;
    }
}

// Окно со скруглёнными углами (Windows 11) и тенью
class RoundForm : Form
{
    public RoundForm()
    {
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW: тень вокруг окна без рамки
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2;   // DWMWCP_ROUND: скруглённые углы. В Windows 10 вызов просто ничего не делает
            NativeMethods.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
    }
}

// Окно-плашка, которое не забирает фокус у других программ
class OsdForm : RoundForm
{
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080;
            return cp;
        }
    }
}

// Вызовы готовых функций Windows: блокировка и сон
static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // Скруглённые углы окон в Windows 11
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

// «Табличка» для действий (блокировка, сон): вторая копия с тем же именем сразу выходит
static class Guard
{
    static Mutex? held;   // держим ссылку, пока работает программа

    public static bool TryEnter(string name)
    {
        held = new Mutex(false, "OCS_Guard_" + name);
        try { return held.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }   // прошлая копия закрылась аварийно: табличка свободна
    }
}

// Общая громкость всех звуков программы: 1.0 = как раньше, 0.7 = чуть тише, 0.4 = заметно тише, 0 = без звука.
// Это единственное место, где её нужно менять.
static class SoundSettings
{
    public const double Level = 0.7;
}

// Звук отсчёта: короткие мягкие ноты, собранные в памяти (одна или несколько подряд)
static class Beeper
{
    const int Rate = 44100;
    static SoundPlayer? current;   // держим ссылку, чтобы звук не оборвался при уборке памяти

    // Ноты: (частота в герцах, длительность в миллисекундах)
    public static void Play(double volume, params (double Hz, int Ms)[] notes)
    {
        var samples = new List<short>();
        for (int i = 0; i < Rate / 10; i++) samples.Add(0);   // 100 мс тишины: устройство вывода успевает «проснуться»

        foreach (var (hz, ms) in notes)
        {
            int n = Rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                // Плавное нарастание (10 мс) и затухание (40 мс), чтобы не было щелчков
                double attack = Math.Min(1.0, t / 0.010);
                double release = Math.Min(1.0, (double)(n - i) / (Rate * 0.040));
                double v = Math.Sin(2 * Math.PI * hz * t) * attack * release * volume * SoundSettings.Level;
                samples.Add((short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue));
            }
        }

        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        int dataLen = samples.Count * 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        stream.Position = 0;

        current = new SoundPlayer(stream);
        current.Play();
    }
}

// Выключение или перезагрузка с отсчётом: 5, 4, 3, 2, 1, затем секунда «Shut down» / «Restart» и действие
static class ShutdownCountdown
{
    public static void Run(bool restart, bool test)
    {
        // Всё, что отличается у выключения и перезагрузки, собрано в четырёх строках
        string verb = restart ? "Перезагрузка" : "Выключение";
        string finalWord = restart ? "Restart" : "Shut down";
        string finalHint = restart ? "Перезагрузка..." : "Выключение...";
        string command = restart ? "/r /t 0" : "/s /t 0";
        string subText = restart ? "Компьютер перезагрузится через" : "Компьютер выключится через";

        // Ноты отсчёта сверху вниз: на каждую цифру 5, 4, 3, 2, 1 своя (частота в герцах)
        double[] tones = { 988.0, 880.0, 784.0, 659.0, 587.0 };

        // Одна копия: вторая не начинает отсчёт заново, а просит первую отменить его
        using var mutex = new Mutex(true, "OCS_ShutdownCountdown", out bool isNew);
        var cancelEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "OCS_ShutdownCancel");
        if (!isNew)
        {
            cancelEvent.Set();
            return;
        }

        var started = DateTime.Now;
        bool canCancel = true;
        int left = 5;

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var form = new RoundForm
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = Theme.Card,
            Size = new Size(Ui.S(360), Ui.S(204))
        };
        // Справа внизу над панелью задач, в стороне от закреплённой иконки
        form.Location = new Point(area.Right - form.Width - Ui.S(16), area.Bottom - form.Height - Ui.S(16));
        form.Paint += (s, e) => Ui.DrawCardBorder(e.Graphics, form.ClientSize);

        // Слева большая цифра, справа название и пояснение, ниже подсказка и кнопка
        var big = new Label
        {
            Location = new Point(Ui.S(22), Ui.S(22)), Size = new Size(Ui.S(72), Ui.S(76)),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Bad,
            Font = Ui.MakeFont("Segoe UI Light", 64),
            Text = "5"
        };
        var title = new Label
        {
            Location = new Point(Ui.S(104), Ui.S(26)), Size = new Size(Ui.S(240), Ui.S(30)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Ui.MakeFont("Segoe UI Semibold", 22),
            Text = test ? verb + " (тест)" : verb
        };
        var sub = new Label
        {
            Location = new Point(Ui.S(104), Ui.S(58)), Size = new Size(Ui.S(240), Ui.S(44)),
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Theme.Sub,
            Font = Ui.MakeFont("Segoe UI", 15),
            Text = subText + " 5 с"
        };
        var hint = new Label
        {
            Location = new Point(Ui.S(22), Ui.S(104)), Size = new Size(Ui.S(316), Ui.S(34)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Sub,
            Font = Ui.MakeFont("Segoe UI", 13),
            Text = "Отмена: кнопка, Esc или ещё один клик по иконке"
        };
        var btn = new Button
        {
            Location = new Point(Ui.S(22), Ui.S(146)), Size = new Size(Ui.S(316), Ui.S(44)),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = Theme.Btn,
            ForeColor = Theme.Text,
            Font = Ui.MakeFont("Segoe UI Semibold", 17),
            Text = "Отмена"
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = Theme.BtnLine;
        form.Controls.AddRange(new Control[] { big, title, sub, hint, btn });

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };

        // Отмена возможна, пока не появилась надпись «Shut down» / «Restart»
        void Cancel()
        {
            if (!canCancel) return;
            canCancel = false;   // второй раз не отменяем
            timer.Stop();

            // Восходящий сигнал отмены. Окно исчезает сразу, а процесс живёт ещё полсекунды ради звука
            Beeper.Play(0.35, (523.0, 90), (784.0, 150));
            form.Hide();
            var closeLater = new System.Windows.Forms.Timer { Interval = 450 };
            closeLater.Tick += (s2, e2) => { closeLater.Stop(); form.Close(); };
            closeLater.Start();
        }

        form.Click += (s, e) => Cancel();
        title.Click += (s, e) => Cancel();
        big.Click += (s, e) => Cancel();
        sub.Click += (s, e) => Cancel();
        hint.Click += (s, e) => Cancel();
        btn.Click += (s, e) => Cancel();
        form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Cancel(); };
        form.Shown += (s, e) =>
        {
            form.Activate();
            Beeper.Play(0.35, (tones[0], 150));   // нота для цифры 5
        };

        timer.Tick += (s, e) =>
        {
            left--;
            if (left > 0)
            {
                big.Text = left.ToString();
                sub.Text = subText + " " + left + " с";
                Beeper.Play(0.35, (tones[5 - left], 150));   // нота для цифры 4, 3, 2, 1
            }
            else if (left == 0)
            {
                canCancel = false;
                Beeper.Play(0.4, (392.0, 450));   // низкая длинная нота: отмена уже невозможна
                big.Text = "0";
                title.Text = finalWord;
                sub.Text = test ? "Тестовый режим: компьютер не " + (restart ? "перезагрузится" : "выключится") : finalHint;
                hint.Text = "";
                btn.Enabled = false;
            }
            else
            {
                timer.Stop();
                if (!test)
                {
                    Process.Start(new ProcessStartInfo("shutdown", command)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                form.Close();
            }
        };

        // Фоновый «слушатель»: ждёт сигнала «отмени» от второй копии программы
        _ = form.Handle;
        var waiter = new Thread(() =>
        {
            while (true)
            {
                cancelEvent.WaitOne();
                form.BeginInvoke(new Action(() =>
                {
                    // Время двойного щелчка Windows игнорируем: так случайный двойной клик не отменит отсчёт
                    if ((DateTime.Now - started).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
                    Cancel();
                }));
            }
        })
        { IsBackground = true };
        waiter.Start();

        timer.Start();
        Application.Run(form);
    }
}
