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

// ===== Действия без состояния: --action имя (блокировка, сон, выключение) =====
int aIdx = Array.IndexOf(args, "--action");
if (aIdx >= 0 && aIdx + 1 < args.Length)
{
    var action = args[aIdx + 1].ToLowerInvariant();
    if (action == "lock")
    {
        NativeMethods.LockWorkStation();
        return;
    }
    if (action == "sleep")
    {
        NativeMethods.SetSuspendState(false, false, false);
        return;
    }
    if (action == "shutdown")
    {
        // С приставкой --test компьютер в конце не выключается
        ShutdownCountdown.Run(args.Contains("--test"));
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

// ===== Плашка «Статус»: --status показывает состояние всех выключателей на 4 секунды =====
if (args.Contains("--status"))
{
    var rows = new List<(string Name, int State)>();
    foreach (var kv in switches)
        rows.Add((kv.Value.Name, kv.Value.GetState().GetAwaiter().GetResult()));

    var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
    int height = 28 + rows.Count * 36;
    var panel = new OsdForm
    {
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        ShowInTaskbar = false,
        TopMost = true,
        BackColor = Color.FromArgb(40, 40, 40),
        Size = new Size(360, height),
        Opacity = 0.97
    };
    panel.Location = new Point(wa.Left + (wa.Width - panel.Width) / 2, wa.Bottom - panel.Height - 48);

    panel.Paint += (s, e) =>
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var nameFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var white = new SolidBrush(Color.White);
        using var grey = new SolidBrush(Color.FromArgb(170, 170, 170));
        for (int i = 0; i < rows.Count; i++)
        {
            int y = 14 + i * 36;
            int st = rows[i].State;
            var dotColor = st == 1 ? Color.FromArgb(46, 184, 77)
                         : st == 0 ? Color.FromArgb(120, 120, 120)
                         : Color.FromArgb(190, 60, 60);
            using var dotBrush = new SolidBrush(dotColor);
            g.FillEllipse(dotBrush, 20, y + 6, 16, 16);
            g.DrawString(rows[i].Name, nameFont, white, 48, y);
            string txt = st == 1 ? "включён" : st == 0 ? "выключен" : "нет устройства";
            g.DrawString(txt, nameFont, grey, 190, y);
        }
    };
    panel.Click += (s, e) => panel.Close();

    var statusTimer = new System.Windows.Forms.Timer { Interval = 4000 };
    statusTimer.Tick += (s, e) => panel.Close();
    statusTimer.Start();

    Application.Run(panel);
    return;
}

// ===== Какой выключатель просят? Ищем в запуске слова: --toggle имя =====
string? key = null;
int idx = Array.IndexOf(args, "--toggle");
if (idx >= 0 && idx + 1 < args.Length) key = args[idx + 1].ToLowerInvariant();

if (key == null || !switches.ContainsKey(key))
{
    MessageBox.Show(
        "Запускайте программу через ярлыки, например:" + Environment.NewLine +
        "OneClickSwitcher.exe --toggle bluetooth" + Environment.NewLine +
        "OneClickSwitcher.exe --action lock" + Environment.NewLine +
        "OneClickSwitcher.exe --status" + Environment.NewLine + Environment.NewLine +
        "Выключатели: " + string.Join(", ", switches.Keys) + Environment.NewLine +
        "Действия: lock, sleep, shutdown" + Environment.NewLine + Environment.NewLine +
        "Получено: [" + string.Join(" | ", args) + "]",
        "One Click Switcher");
    return;
}
var sw = switches[key];

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
        double v = (click * 0.6 + thump * 0.7 + noise * 0.35) * volume;
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

// ===== Выбираем цвет плашки, надпись и звук =====
Color bg;
string line;
var samples = new List<short>();
AddSilence(samples, 100);

if (result == 1)
{
    bg = Color.FromArgb(46, 184, 77);
    line = "включён";
    AddClick(samples, 2600, 220, 0.45);
}
else if (result == 0)
{
    bg = Color.FromArgb(90, 90, 90);
    line = "выключен";
    AddClick(samples, 1500, 130, 0.45);
}
else
{
    bg = Color.FromArgb(190, 60, 60);
    line = result == -1 ? "не найден" : "не удалось";
    AddClick(samples, 700, 90, 0.5);
    AddSilence(samples, 110);
    AddClick(samples, 700, 90, 0.5);
}

var player = new SoundPlayer(ToWav(samples));
player.Play();

// ===== Показываем плашку внизу по центру экрана на 1,3 секунды =====
var area = Screen.FromPoint(Cursor.Position).WorkingArea;
var osd = new OsdForm
{
    FormBorderStyle = FormBorderStyle.None,
    StartPosition = FormStartPosition.Manual,
    ShowInTaskbar = false,
    TopMost = true,
    BackColor = bg,
    Size = new Size(300, 110),
    Opacity = 0.96
};
osd.Location = new Point(area.Left + (area.Width - osd.Width) / 2, area.Bottom - osd.Height - 48);
osd.Controls.Add(new Label
{
    Dock = DockStyle.Fill,
    TextAlign = ContentAlignment.MiddleCenter,
    ForeColor = Color.White,
    Font = new Font("Segoe UI", 18, FontStyle.Bold),
    Text = sw.Name + Environment.NewLine + line
});

var closeTimer = new System.Windows.Forms.Timer { Interval = 1300 };
closeTimer.Tick += (s, e) => osd.Close();
closeTimer.Start();

Application.Run(osd);
GC.KeepAlive(player);

// ===== Типы: обязательно в самом конце файла =====

// Окно-плашка, которое не забирает фокус у других программ
class OsdForm : Form
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
}

// Выключение с отсчётом: 5, 4, 3, 2, 1, затем секунда «Shut down» и выключение
static class ShutdownCountdown
{
    public static void Run(bool test)
    {
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
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = Color.FromArgb(40, 40, 40),
            Size = new Size(360, 190)
        };
        // Справа внизу над панелью задач, в стороне от закреплённой иконки
        form.Location = new Point(area.Right - form.Width - 16, area.Bottom - form.Height - 16);

        var title = new Label
        {
            Location = new Point(0, 10), Size = new Size(360, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Segoe UI", 12),
            Text = test ? "Выключение компьютера (тест)" : "Выключение компьютера"
        };
        var big = new Label
        {
            Location = new Point(0, 38), Size = new Size(360, 70),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 36, FontStyle.Bold),
            Text = "5"
        };
        var hint = new Label
        {
            Location = new Point(10, 108), Size = new Size(340, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(170, 170, 170),
            Font = new Font("Segoe UI", 9),
            Text = "Отмена: кнопка, Esc, клик по окну или ещё раз по иконке"
        };
        var btn = new Button
        {
            Location = new Point(20, 146), Size = new Size(320, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(90, 90, 90),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Text = "Отмена"
        };
        btn.FlatAppearance.BorderSize = 0;
        form.Controls.AddRange(new Control[] { title, big, hint, btn });

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };

        // Отмена возможна, пока не появилась надпись «Shut down»
        void Cancel()
        {
            if (!canCancel) return;
            timer.Stop();
            form.Close();
        }

        form.Click += (s, e) => Cancel();
        title.Click += (s, e) => Cancel();
        big.Click += (s, e) => Cancel();
        hint.Click += (s, e) => Cancel();
        btn.Click += (s, e) => Cancel();
        form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Cancel(); };
        form.Shown += (s, e) => form.Activate();

        timer.Tick += (s, e) =>
        {
            left--;
            if (left > 0)
            {
                big.Text = left.ToString();
            }
            else if (left == 0)
            {
                canCancel = false;
                big.Text = "Shut down";
                big.Font = new Font("Segoe UI", 26, FontStyle.Bold);
                hint.Text = test ? "Тестовый режим: компьютер не выключится" : "Выключение...";
                btn.Enabled = false;
            }
            else
            {
                timer.Stop();
                if (!test)
                {
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
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
                    // Первую секунду игнорируем: так двойной клик не отменит отсчёт
                    if ((DateTime.Now - started).TotalMilliseconds < 1000) return;
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