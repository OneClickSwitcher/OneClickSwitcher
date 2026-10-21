using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;

ApplicationConfiguration.Initialize();

#if !STORE   // в сборке для Магазина диагностика (в ней есть запись звука) не нужна
// ===== Диагностика: --diag ничего не меняет, только записывает отчёт о микрофонах и радио на рабочий стол =====
if (args.Contains("--diag"))
{
    var rep = new System.Text.StringBuilder();
    rep.AppendLine("One Click Switcher: отчёт диагностики");
    rep.AppendLine("Время: " + DateTime.Now);
    rep.AppendLine("Windows: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + " (" + Environment.OSVersion.Version + ")");
    rep.AppendLine("64-разрядная система: " + Environment.Is64BitOperatingSystem);
    rep.AppendLine();

    try
    {
        using var en = new MMDeviceEnumerator();

        string defConsole = "", defComm = "";
        try { defConsole = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; } catch { }
        try { defComm = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }

        var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All).ToList();
        rep.AppendLine("=== Микрофоны (устройства записи): всего " + all.Count + " ===");
        rep.AppendLine("Во время замера нужно говорить в микрофон или стучать по столу 4 секунды: так видно, слышит ли его Windows.");
        rep.AppendLine();

        // Просим говорить именно в момент замера: раньше замер шёл молча, и «тишина» в отчёте ничего не доказывала
        MessageBox.Show(
            "Сейчас программа 4 секунды будет слушать микрофон." + Environment.NewLine + Environment.NewLine +
            "Нажмите OK и сразу говорите в микрофон («раз, два, три...») или стучите по столу, пока не откроется отчёт.",
            "One Click Switcher: диагностика");

        // По-настоящему записываем звук с каждого активного микрофона, как это делает Telegram или Zoom,
        // и ищем самое громкое место записи. Если микрофон выключен по-настоящему, будет 0
        var recPeaks = new Dictionary<string, float>();
        var captures = new List<WasapiCapture>();
        foreach (var d in all)
        {
            if (d.State != DeviceState.Active) continue;
            try
            {
                var cap = new WasapiCapture(d);
                var fmt = cap.WaveFormat;
                string id = d.ID;
                recPeaks[id] = 0f;
                cap.DataAvailable += (s2, ev) =>
                {
                    float max = 0f;
                    if (fmt.BitsPerSample == 32)         // обычный формат Windows: дробные числа
                    {
                        for (int o = 0; o + 3 < ev.BytesRecorded; o += 4)
                            max = Math.Max(max, Math.Abs(BitConverter.ToSingle(ev.Buffer, o)));
                    }
                    else if (fmt.BitsPerSample == 16)    // целые числа
                    {
                        for (int o = 0; o + 1 < ev.BytesRecorded; o += 2)
                            max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(ev.Buffer, o)) / 32768f);
                    }
                    lock (recPeaks) { if (max > recPeaks[id]) recPeaks[id] = max; }
                };
                cap.StartRecording();
                captures.Add(cap);
            }
            catch { }
        }

        // Заодно смотрим «полоску громкости» Windows, 4 секунды (40 замеров по 0,1 с)
        var peaks = new Dictionary<string, float>();
        for (int i = 0; i < 40; i++)
        {
            foreach (var d in all)
            {
                if (d.State != DeviceState.Active) continue;
                try
                {
                    float p = d.AudioMeterInformation.MasterPeakValue;
                    if (!peaks.ContainsKey(d.ID) || p > peaks[d.ID]) peaks[d.ID] = p;
                }
                catch { }
            }
            Thread.Sleep(100);
        }
        foreach (var cap in captures)
        {
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }

        int n = 0;
        foreach (var d in all)
        {
            if (d.State == DeviceState.NotPresent) continue;   // давно отключённые устройства в отчёте не нужны
            n++;
            rep.AppendLine("Микрофон " + n);
            try { rep.AppendLine("  Название: " + d.FriendlyName); } catch { rep.AppendLine("  Название: не удалось получить"); }
            try { rep.AppendLine("  Устройство: " + d.DeviceFriendlyName); } catch { }
            rep.AppendLine("  Состояние: " + d.State);
            rep.AppendLine("  Главное (по умолчанию): " + (d.ID == defConsole ? "ДА" : "нет"));
            rep.AppendLine("  Для связи (по умолчанию): " + (d.ID == defComm ? "ДА" : "нет"));
            if (d.State == DeviceState.Active)
            {
                try { rep.AppendLine("  Заглушен (Mute): " + (d.AudioEndpointVolume.Mute ? "ДА" : "нет")); } catch (Exception ex) { rep.AppendLine("  Заглушен: ошибка " + ex.Message); }
                try { rep.AppendLine("  Громкость: " + Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100) + " %"); } catch { }
                if (recPeaks.ContainsKey(d.ID)) rep.AppendLine("  Записанный звук за 4 с, то есть то, что получают программы (0 тишина, 1 максимум): " + Math.Round(recPeaks[d.ID], 3));
                if (peaks.ContainsKey(d.ID)) rep.AppendLine("  Полоска громкости Windows за 4 с (0 тишина, 1 максимум): " + Math.Round(peaks[d.ID], 3));
            }
            rep.AppendLine("  Код: " + d.ID);
            rep.AppendLine();
        }
    }
    catch (Exception ex)
    {
        rep.AppendLine("Ошибка при чтении микрофонов: " + ex.Message);
        rep.AppendLine();
    }

    rep.AppendLine("=== Устройства вывода (динамики, наушники) ===");
    try
    {
        using var enOut = new MMDeviceEnumerator();
        string defOut = "";
        try { defOut = enOut.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        foreach (var dev in enOut.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            rep.AppendLine(dev.FriendlyName + (dev.ID == defOut ? "  [главное]" : ""));
            try
            {
                rep.AppendLine("  Заглушено: " + (dev.AudioEndpointVolume.Mute ? "ДА" : "нет") +
                    ", громкость " + Math.Round(dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100) + " %");
            }
            catch { }
        }
    }
    catch (Exception ex)
    {
        rep.AppendLine("Ошибка при чтении устройств вывода: " + ex.Message);
    }
    rep.AppendLine();

    rep.AppendLine("=== Радиомодули (Bluetooth, Wi-Fi) ===");
    try
    {
        var rs = Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult();
        if (rs.Count == 0) rep.AppendLine("Не найдено ни одного.");
        foreach (var r in rs) rep.AppendLine(r.Kind + ": " + r.Name + ", состояние " + r.State);
    }
    catch (Exception ex)
    {
        rep.AppendLine("Ошибка при чтении радиомодулей: " + ex.Message);
    }

    // --diag-name on / off добавляет к имени файла отчёта метку, чтобы два замера не перезаписали друг друга
    string diagSuffix = "";
    int dn = Array.IndexOf(args, "--diag-name");
    if (dn >= 0 && dn + 1 < args.Length) diagSuffix = "_" + new string(args[dn + 1].Where(char.IsLetterOrDigit).ToArray());
    var repPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OneClickSwitcher-diag" + diagSuffix + ".txt");
    File.WriteAllText(repPath, rep.ToString(), new System.Text.UTF8Encoding(true));
    try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + repPath + "\"") { UseShellExecute = true }); } catch { }
    return;
}
#endif

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
    ["mic"] = (Loc.T("title_mic"), () => Task.FromResult(ToggleMicrophones()), () => Task.FromResult(GetMicState())),
    ["sound"] = (Loc.T("title_sound"), () => Task.FromResult(ToggleSound()), () => Task.FromResult(GetSoundState())),
};

// ===== Окно «Статус»: --status показывает все функции с переключателями и кнопками действий =====
// Повторный запуск (ещё один клик по ярлыку) закрывает окно, а не открывает второе
SoundPlayer? statusClick = null;   // держим ссылку на звук щелчка, пока он играет
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

    // Состояния узнаём заранее, до создания окна: так проще и надёжнее
    var statusKeys = new[] { "bluetooth", "wifi", "mic", "sound" };
    var statusStates = statusKeys.Select(k => switches[k].GetState().GetAwaiter().GetResult()).ToArray();

    var statusForm = new StatusForm(switches, statusStates, PlayClick);

    // Фоновый «слушатель»: ждёт сигнала «закрой» от второй копии программы
    _ = statusForm.Handle;
    statusCloseEvent.Reset();   // клики, пришедшие, пока окно ещё готовилось, не считаем
    var statusWaiter = new Thread(() =>
    {
        while (true)
        {
            statusCloseEvent.WaitOne();
            statusForm.BeginInvoke(new Action(() =>
            {
                // Время двойного щелчка Windows игнорируем: так случайный двойной клик не закроет окно
                if ((DateTime.Now - statusStarted).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
                statusForm.Close();
            }));
        }
    })
    { IsBackground = true };
    statusWaiter.Start();

    Application.Run(statusForm);
    GC.KeepAlive(statusClick);
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
        Loc.T("usage_head") + Environment.NewLine +
        "OneClickSwitcher.exe --toggle bluetooth" + Environment.NewLine +
        "OneClickSwitcher.exe --action lock" + Environment.NewLine +
        "OneClickSwitcher.exe --status" + Environment.NewLine + Environment.NewLine +
        "OneClickSwitcher.exe --setup (" + Loc.T("usage_setup") + ")" + Environment.NewLine + Environment.NewLine +
        Loc.T("usage_switches") + " " + string.Join(", ", switches.Keys) + Environment.NewLine +
        Loc.T("usage_actions") + " lock, sleep, shutdown, restart" + Environment.NewLine + Environment.NewLine +
        Loc.T("usage_got") + " [" + string.Join(" | ", args) + "]",
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

// ===== Микрофоны: запоминаем громкость, чтобы вернуть её при включении =====
// Некоторые микрофоны (например, в недорогих веб-камерах) игнорируют заглушение Windows.
// Поэтому при выключении мы дополнительно ставим громкость 0, а при включении возвращаем прежнюю.
string MicVolumeFile() => Path.Combine(Path.GetDirectoryName(Shortcuts.IconsDir)!, "mic-volumes.txt");

Dictionary<string, float> LoadMicVolumes()
{
    var map = new Dictionary<string, float>();
    try
    {
        string file = MicVolumeFile();
        if (!File.Exists(file)) return map;
        foreach (var line in File.ReadAllLines(file))
        {
            int bar = line.LastIndexOf('|');
            if (bar <= 0) continue;
            if (float.TryParse(line.Substring(bar + 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                map[line.Substring(0, bar)] = v;
        }
    }
    catch { }
    return map;
}

void SaveMicVolumes(Dictionary<string, float> map)
{
    try
    {
        string file = MicVolumeFile();
        if (map.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, map.Select(p =>
            p.Key + "|" + p.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
    }
    catch { }
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

        var saved = LoadMicVolumes();
        int changed = 0;
        foreach (var m in mics)
        {
            try
            {
                var vol = m.AudioEndpointVolume;
                if (anyOn)
                {
                    // Выключаем: запоминаем громкость (если она не нулевая), глушим и ставим громкость 0
                    float current = vol.MasterVolumeLevelScalar;
                    if (current > 0.001f) saved[m.ID] = current;
                    vol.Mute = true;
                    try { vol.MasterVolumeLevelScalar = 0f; } catch { }
                }
                else
                {
                    // Включаем: сначала возвращаем громкость (только если она всё ещё 0), потом снимаем заглушение
                    if (saved.TryGetValue(m.ID, out float old))
                    {
                        try { if (vol.MasterVolumeLevelScalar < 0.001f) vol.MasterVolumeLevelScalar = old; } catch { }
                        saved.Remove(m.ID);
                    }
                    vol.Mute = false;
                }
                changed++;
            }
            catch { }
        }
        SaveMicVolumes(saved);
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

// ===== Щелчок для окна «Статус»: тот же звук, что у плашки, но без плашки =====
void PlayClick(int st)
{
    var smp = new List<short>();
    AddSilence(smp, 20);
    if (st == 1) AddClick(smp, 2600, 220, 0.45);
    else if (st == 0) AddClick(smp, 1500, 130, 0.45);
    else
    {
        AddClick(smp, 700, 90, 0.5);
        AddSilence(smp, 110);
        AddClick(smp, 700, 90, 0.5);
    }
    statusClick = new SoundPlayer(ToWav(smp));
    statusClick.Play();
}

// ===== Выполняем действие =====
int result = sw.Toggle().GetAwaiter().GetResult();

// ===== Выбираем надпись и звук (цвет плашки зависит от состояния: Theme.State) =====
string line;
var samples = new List<short>();
AddSilence(samples, 100);

if (result == 1)
{
    line = Loc.T("st_on");
    AddClick(samples, 2600, 220, 0.45);
}
else if (result == 0)
{
    line = Loc.T("st_off");
    AddClick(samples, 1500, 130, 0.45);
}
else
{
    line = result == -1 ? Loc.T("st_notfound") : Loc.T("st_failed");
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

// ----- Язык интерфейса -----
// Все надписи программы собраны в одну таблицу: ключ -> { русский, английский }.
// Язык берётся из Windows: русский, если система русская, иначе английский.
// Для проверки язык можно задать вручную: OneClickSwitcher.exe --status --lang en
// Чтобы добавить язык, нужна ещё одна колонка в таблице и ещё одно число в Detect()
static class Loc
{
    public static readonly int Lang = Detect();   // 0 = русский, 1 = английский

    static int Detect()
    {
        var a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, "--lang");
        if (i >= 0 && i + 1 < a.Length)
        {
            if (a[i + 1] == "ru") return 0;
            if (a[i + 1] == "en") return 1;
        }
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? 0 : 1;
    }

    // Текст по ключу на нужном языке; если ключа нет, возвращается сам ключ (так ошибку видно сразу)
    public static string T(string key) => Table.TryGetValue(key, out var v) ? v[Lang] : key;
    public static string T(string key, params object[] args) => string.Format(T(key), args);

    // Тексты на всех языках: нужно, чтобы найти ярлык, созданный при другом языке интерфейса
    public static IEnumerable<string> All(string key) => Table.TryGetValue(key, out var v) ? v : new[] { key };

    static readonly Dictionary<string, string[]> Table = new()
    {
        // Названия ярлыков и выключателей
        ["title_bluetooth"] = new[] { "Bluetooth", "Bluetooth" },
        ["title_wifi"] = new[] { "Wi-Fi", "Wi-Fi" },
        ["title_mic"] = new[] { "Микрофон", "Microphone" },
        ["title_sound"] = new[] { "Звук", "Sound" },
        ["title_lock"] = new[] { "Блокировка", "Lock" },
        ["title_sleep"] = new[] { "Сон", "Sleep" },
        ["title_status"] = new[] { "Статус", "Status" },
        ["title_shutdown"] = new[] { "Выключение", "Shut down" },
        ["title_restart"] = new[] { "Перезагрузка", "Restart" },

        // Справка при запуске без параметров
        ["usage_head"] = new[] { "Запускайте программу через ярлыки, например:", "Run the program through shortcuts, for example:" },
        ["usage_setup"] = new[] { "окно с созданием ярлыков", "shortcut creation window" },
        ["usage_switches"] = new[] { "Выключатели:", "Switches:" },
        ["usage_actions"] = new[] { "Действия:", "Actions:" },
        ["usage_got"] = new[] { "Получено:", "Received:" },

        // Состояние на плашке
        ["st_on"] = new[] { "Включён", "On" },
        ["st_off"] = new[] { "Выключен", "Off" },
        ["st_notfound"] = new[] { "Не найден", "Not found" },
        ["st_failed"] = new[] { "Не получилось", "Didn't work" },

        // Окно первого запуска
        ["fr_sub"] = new[] { "Один клик по ярлыку, и функция переключена.", "One click on a shortcut, and the function is switched." },
        ["fr_step1"] = new[] { "1. Какие ярлыки создать на рабочем столе", "1. Which shortcuts to create on the desktop" },
        ["fr_installed"] = new[] { "установлен", "installed" },
        ["fr_pinned"] = new[] { "на панели задач", "on the taskbar" },
        ["fr_hint"] = new[] { "«Статус» открывает окно со всеми функциями.", "“Status” opens a window with all the functions." },
        ["fr_step2"] = new[] { "2. Закрепите ярлыки на панели задач", "2. Pin the shortcuts to the taskbar" },
        ["fr_pin_intro"] = new[] { "Ярлыки появятся на рабочем столе. Закреплять их нужно оттуда:", "The shortcuts appear on the desktop. Pin them from there:" },
        ["fr_pin_steps"] = new[]
        {
            "1. Нажмите на ярлык правой кнопкой мыши." + Environment.NewLine +
            "2. Если в меню есть «Закрепить на панели задач», выберите его. Так бывает в Windows 10 и в части версий Windows 11." + Environment.NewLine +
            "3. Если такого пункта нет, выберите «Показать дополнительные параметры», а в открывшемся меню «Закрепить на панели задач».",

            "1. Right-click a shortcut." + Environment.NewLine +
            "2. If the menu has “Pin to taskbar”, choose it. This is how it works in Windows 10 and in some versions of Windows 11." + Environment.NewLine +
            "3. If there is no such item, choose “Show more options”, and in the menu that opens choose “Pin to taskbar”."
        },
        ["fr_note"] = new[]
        {
            "Закреплённые значки работают сами по себе, поэтому ярлыки с рабочего стола после этого можно удалить. " +
            "Саму программу, которую мы скопируем в постоянную папку, удалять и переносить нельзя: ярлыки ведут в неё.",

            "Pinned icons work on their own, so the desktop shortcuts can be deleted afterwards. " +
            "The program itself, which we copy to a permanent folder, must not be deleted or moved: the shortcuts point to it."
        },
        ["fr_copy_note"] = new[] { "Программа скопируется в вашу папку пользователя, ярлыки будут вести туда.", "The program will be copied to your user folder, and the shortcuts will point there." },
        ["fr_close"] = new[] { "Закрыть", "Close" },
        ["fr_create"] = new[] { "Создать ярлыки", "Create shortcuts" },
        ["fr_none"] = new[] { "Ничего не выбрано.", "Nothing is selected." },
        ["fr_copyfail"] = new[] { "Не удалось скопировать программу. Закройте окна One Click Switcher и повторите.", "Couldn't copy the program. Close the One Click Switcher windows and try again." },
        ["fr_createfail"] = new[] { "Не удалось создать ярлыки.", "Couldn't create the shortcuts." },
        ["fr_done"] = new[] { "Готово: ярлыки созданы", "Done: shortcuts created" },
        ["fr_done_count"] = new[] { "Создано ярлыков: {0}. Они лежат на рабочем столе.", "Shortcuts created: {0}. They are on the desktop." },
        ["fr_pin_next"] = new[] { "Осталось закрепить нужные ярлыки на панели задач. Как это сделать, написано ниже.", "All that is left is to pin the shortcuts you need to the taskbar. How to do it is described below." },
        ["fr_copied"] = new[] { "Программа скопирована в папку {0}. Файл, который вы запускали, теперь можно удалить.", "The program was copied to the folder {0}. The file you ran can now be deleted." },
        ["fr_inplace"] = new[] { "Программа уже работает из своей постоянной папки: {0}.", "The program is already running from its permanent folder: {0}." },
        ["fr_finished"] = new[] { "Установка закончена. Окно можно закрыть.", "Setup is finished. You can close this window." },
        ["fr_all"] = new[] { "Все ярлыки установлены.", "All shortcuts are installed." },

        // Окно «Статус»
        ["sp_none"] = new[] { "нет", "none" },
        ["sp_actions"] = new[] { "ДЕЙСТВИЯ", "ACTIONS" },
        ["sp_link"] = new[] { "Создать ярлыки…", "Create shortcuts…" },
        ["sp_notfound"] = new[] { "не найдено", "not found" },
        ["sp_failed"] = new[] { "не получилось", "didn't work" },
        ["act_lock"] = new[] { "Блокировка", "Lock" },
        ["act_sleep"] = new[] { "Сон", "Sleep" },
        ["act_shutdown"] = new[] { "Выключить", "Shut down" },
        ["act_restart"] = new[] { "Перезагрузка", "Restart" },

        // Отсчёт выключения и перезагрузки
        ["cd_shutdown"] = new[] { "Выключение", "Shut down" },
        ["cd_restart"] = new[] { "Перезагрузка", "Restart" },
        ["cd_shuttingdown"] = new[] { "Выключение...", "Shutting down..." },
        ["cd_restarting"] = new[] { "Перезагрузка...", "Restarting..." },
        ["cd_sub_shutdown"] = new[] { "Компьютер выключится через", "Shutting down in" },
        ["cd_sub_restart"] = new[] { "Компьютер перезагрузится через", "Restarting in" },
        ["cd_sec"] = new[] { "с", "s" },
        ["cd_test"] = new[] { "(тест)", "(test)" },
        ["cd_hint"] = new[] { "Отмена: кнопка, Esc или ещё один клик по иконке", "Cancel: button, Esc, or click the icon again" },
        ["cd_cancel"] = new[] { "Отмена", "Cancel" },
        ["cd_test_shutdown"] = new[] { "Тестовый режим: компьютер не выключится", "Test mode: the computer will not shut down" },
        ["cd_test_restart"] = new[] { "Тестовый режим: компьютер не перезагрузится", "Test mode: the computer will not restart" },
    };
}

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
    public static Color RowHover => Pick(0xeef1f6, 0x40434d);   // подсветка строки и кнопок под мышью
    public static Color TrackOn => Pick(0x0a5cb8, 0x7db8ff);    // переключатель: дорожка и ползунок
    public static Color TrackOff => Pick(0x8a91a0, 0x6b7080);
    public static Color KnobOn => Pick(0xffffff, 0x16181d);
    public static Color KnobOff => Pick(0xffffff, 0xd5d9e2);

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
        ("mic", Loc.T("title_mic"), "--toggle mic"),
        ("sound", Loc.T("title_sound"), "--toggle sound"),
        ("lock", Loc.T("title_lock"), "--action lock"),
        ("sleep", Loc.T("title_sleep"), "--action sleep"),
        ("status", Loc.T("title_status"), "--status"),
        ("shutdown", Loc.T("title_shutdown"), "--action shutdown"),
        ("restart", Loc.T("title_restart"), "--action restart"),
    };

    // Запущена ли программа как установленная из Магазина (пакет MSIX)
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);
    public static bool Packaged
    {
        get
        {
            int len = 0;
            return GetCurrentPackageFullName(ref len, null) != 15700;   // 15700 = «программа не из пакета»
        }
    }

    // «Псевдоним запуска»: Windows создаёт его при установке из Магазина, он всегда ведёт на актуальную версию
    public static string AliasPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "OneClickSwitcher.exe");

    // Значки лежат отдельно от программы, в папке пользователя.
    // Из Магазина: запись в обычную AppData Windows незаметно перенаправляет внутрь пакета, и ярлыки значков не увидят.
    // Поэтому берём настоящую папку данных пакета: она обычная, и оболочка Windows читает значки оттуда
    public static string IconsDir => Packaged
        ? Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "icons")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneClickSwitcher", "icons");

    // Постоянная папка программы: в профиле пользователя, права администратора не нужны.
    // Из Магазина программа лежит в защищённой папке Windows, копировать её не нужно
    public static string InstallDir => Packaged
        ? Path.GetDirectoryName(AliasPath)!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OneClickSwitcher");

    // Копирует программу в постоянную папку и возвращает путь к копии.
    // copied = false, если программа уже запущена оттуда (копировать нечего).
    // Если копия занята (например, открыт отсчёт выключения), бросает исключение
    public static string Install(out bool copied)
    {
        string exe = Environment.ProcessPath ?? "";
        string target = Path.Combine(InstallDir, "OneClickSwitcher.exe");
        copied = false;
        if (Packaged) return AliasPath;   // ярлыки ведут на псевдоним: Магазин сам обновляет программу под ним
        if (exe == "") throw new InvalidOperationException("Не удалось определить путь к программе");
        if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return exe;

        Directory.CreateDirectory(InstallDir);
        File.Copy(exe, target, true);
        copied = true;
        return target;
    }

    // Есть ли уже на рабочем столе ярлык этой функции
    public static bool Exists(string key)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var title in Loc.All("title_" + key))
            if (File.Exists(Path.Combine(desktop, title + ".lnk"))) return true;
        return false;
    }

    // Есть ли ярлык этой функции на рабочем столе или на панели задач
    public static bool ExistsAnywhere(string key)
    {
        if (Exists(key)) return true;
        string? args = All.Where(a => a.Key == key).Select(a => a.Args).FirstOrDefault();
        return args != null && PinnedArgs().Contains(args);
    }

    // Параметры запуска ярлыков, закреплённых на панели задач.
    // Windows хранит такие ярлыки в папке User Pinned\TaskBar; ярлык узнаём по параметрам, а не по названию
    static HashSet<string>? pinnedArgs;
    static HashSet<string> PinnedArgs()
    {
        if (pinnedArgs != null) return pinnedArgs;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null && Directory.Exists(dir))
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                foreach (var file in Directory.GetFiles(dir, "*.lnk"))
                {
                    try
                    {
                        string a = shell.CreateShortcut(file).Arguments;
                        if (!string.IsNullOrWhiteSpace(a)) set.Add(a.Trim());
                    }
                    catch { }
                }
            }
        }
        catch { }
        pinnedArgs = set;
        return set;
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
    public string Note = "";   // надпись справа, например «установлен»
    public bool Installed;     // ярлык уже на рабочем столе или на панели задач: строка неактивна

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
        if (Installed) return;   // установленный ярлык не переключается
        Checked = !Checked;
        Focus();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && !Installed)
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
            using var fill = new SolidBrush(Installed ? Theme.Note : Theme.Row);
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
        float noteW = Note == "" ? 0 : Ui.F(Installed ? (Note.Length > 11 ? 120 : 88) : 70);
        Ui.DrawText(g, Title, font, Installed ? Theme.Sub : Theme.Text,
            new RectangleF(tile.Right + Ui.F(12), 0, Width - tile.Right - Ui.F(16) - noteW, Height));
        if (Note != "")
        {
            using var noteFont = Ui.MakeFont("Segoe UI Semibold", 13);
            Ui.DrawText(g, Note, noteFont, Installed ? Theme.On : Theme.Sub,
                new RectangleF(Width - Ui.F(12) - noteW, 0, noteW, Height), StringAlignment.Far);
        }
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
        AddLabel(Loc.T("fr_sub"), 84, 54, 560, 22, 15, color: Theme.Sub);

        // Шаг 1: выбор ярлыков (после создания этот блок заменяется сообщением «Готово»)
        var step1 = new List<Control>();
        step1.Add(AddLabel(Loc.T("fr_step1"), 24, 92, 632, 26, 17, "Segoe UI Semibold"));
        var rows = new List<CheckRow>();
        string[] order = { "status", "bluetooth", "wifi", "mic", "sound", "lock", "sleep", "shutdown", "restart" };
        for (int i = 0; i < order.Length; i++)
        {
            var item = Shortcuts.All.First(a => a.Key == order[i]);
            var row = new CheckRow
            {
                Key = item.Key,
                Title = item.Title,
                // По умолчанию отмечен только «Статус»; остальное человек выбирает сам.
                // Ярлыки, которые уже лежат на столе или закреплены на панели задач, отмечены и недоступны
                Installed = Shortcuts.ExistsAnywhere(item.Key),
                Checked = Shortcuts.ExistsAnywhere(item.Key) || item.Key == "status",
                Note = Shortcuts.Exists(item.Key) ? Loc.T("fr_installed")
                     : Shortcuts.ExistsAnywhere(item.Key) ? Loc.T("fr_pinned") : "",
                Location = new Point(Ui.S(24 + (i % 2) * 320), Ui.S(126 + (i / 2) * 50)),
                Size = new Size(Ui.S(312), Ui.S(44))
            };
            if (row.Installed)
            {
                row.Cursor = Cursors.Default;
                row.TabStop = false;
            }
            rows.Add(row);
            step1.Add(row);
            Controls.Add(row);
        }
        step1.Add(AddLabel(Loc.T("fr_hint"),
            24, 376, 632, 22, 14, color: Theme.Sub));

        // Шаг 2: закрепление на панели задач
        var lower = new List<Control>();   // всё, что поднимется вверх после создания ярлыков
        var step2Title = AddLabel(Loc.T("fr_step2"), 24, 408, 632, 26, 17, "Segoe UI Semibold");
        lower.Add(step2Title);
        lower.Add(AddLabel(Loc.T("fr_pin_intro"), 24, 436, 632, 22, 15));
        lower.Add(AddLabel(
            Loc.T("fr_pin_steps"),
            24, 460, 632, 106, 15));

        // Заметка
        var note = AddLabel(
            Loc.T("fr_note"),
            24, 574, 632, 84, 15);
        note.BackColor = Theme.Note;
        note.Padding = new Padding(Ui.S(14), Ui.S(10), Ui.S(14), Ui.S(10));
        lower.Add(note);

        // Кнопки и результат
        var result = AddLabel(Loc.T("fr_copy_note"),
            24, 672, 320, 40, 13, color: Theme.Sub);
        result.TextAlign = ContentAlignment.MiddleLeft;
        var close = AddButton(Loc.T("fr_close"), 536, 120, false);
        var create = AddButton(Loc.T("fr_create"), 354, 170, true);
        lower.Add(result);
        lower.Add(close);
        lower.Add(create);
        close.Click += (s, e) => Close();
        create.Click += (s, e) =>
        {
            var chosen = rows.Where(r => r.Checked && !r.Installed).Select(r => r.Key).ToList();
            if (chosen.Count == 0)
            {
                result.ForeColor = Theme.Sub;
                result.Text = Loc.T("fr_none");
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
                result.Text = Loc.T("fr_copyfail");
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
                result.Text = Loc.T("fr_createfail");
                return;
            }

            // Готово: убираем выбор ярлыков и показываем крупное сообщение, что всё сделано
            Beeper.Play(0.35, (659.0, 100), (880.0, 180));
            foreach (var c in step1) c.Visible = false;
            create.Visible = false;

            Controls.Add(new GlyphBox
            {
                Key = "check",
                TileColor = Theme.OnBg,
                GlyphColor = Theme.On,
                Location = new Point(Ui.S(24), Ui.S(100)),
                Size = new Size(Ui.S(56), Ui.S(56))
            });
            AddLabel(Loc.T("fr_done"), 96, 98, 560, 34, 24, "Segoe UI Semibold");
            AddLabel(Loc.T("fr_done_count", n),
                96, 134, 560, 26, 16, color: Theme.Sub);
            AddLabel(Loc.T("fr_pin_next"),
                24, 170, 632, 48, 16, "Segoe UI Semibold");
            AddLabel(copied
                    ? Loc.T("fr_copied", Shortcuts.InstallDir)
                    : Loc.T("fr_inplace", Shortcuts.InstallDir),
                24, 222, 632, 64, 15, color: Theme.Sub);

            // Сообщение «окно можно закрыть» читается в самом конце, рядом с кнопкой «Закрыть»
            result.ForeColor = Theme.On;
            result.Text = Loc.T("fr_finished");
            result.Visible = true;

            // Остальное поднимаем вверх на место выбора ярлыков
            int delta = Ui.S(408 - 296);
            foreach (var c in lower) c.Top -= delta;
            close.Left = Ui.S(656) - close.Width;     // кнопка «Закрыть» становится главной и правее
            close.BackColor = Theme.Primary;
            close.ForeColor = Color.White;
            close.FlatAppearance.BorderColor = Theme.Primary;
            int newFull = Ui.S(736) - delta;
            AutoScrollMinSize = new Size(0, newFull);
            ClientSize = new Size(ClientSize.Width, Math.Min(newFull, ClientSize.Height));
        };
        // Все ярлыки уже на рабочем столе: кнопка создания не нужна
        if (rows.All(r => r.Installed))
        {
            create.Visible = false;
            result.ForeColor = Theme.On;
            result.Text = Loc.T("fr_all");
            close.Left = Ui.S(656) - close.Width;
            close.BackColor = Theme.Primary;
            close.ForeColor = Color.White;
            close.FlatAppearance.BorderColor = Theme.Primary;
        }
        AcceptButton = create.Visible ? create : close;
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

// Окно «Статус»: все функции с переключателями, кнопки действий и ссылка на создание ярлыков
class StatusForm : RoundForm
{
    readonly Dictionary<string, (string Name, Func<Task<int>> Toggle, Func<Task<int>> GetState)> sw;
    readonly Action<int> playClick;
    static readonly string[] FuncKeys = { "bluetooth", "wifi", "mic", "sound" };
    static readonly (string Key, string Title, string Args)[] Actions =
    {
        ("lock", Loc.T("act_lock"), "--action lock"),
        ("sleep", Loc.T("act_sleep"), "--action sleep"),
        ("shutdown", Loc.T("act_shutdown"), "--action shutdown"),
        ("restart", Loc.T("act_restart"), "--action restart"),
    };

    readonly int[] state;                       // 1 = включено, 0 = выключено, -1 = нет устройства
    readonly bool[] busy = new bool[4];         // функция сейчас переключается
    readonly string?[] rowMsg = new string?[4]; // короткое сообщение об ошибке в строке
    readonly DateTime[] rowMsgUntil = new DateTime[4];

    int hoverRow = -1, hoverAction = -1;
    bool hoverLink;
    bool closeAfter;                            // закрыть, как только закончится переключение
    bool refreshing;                            // идёт обновление состояний
    DateTime lastInside = DateTime.Now;         // когда мышь последний раз была над окном
    readonly System.Windows.Forms.Timer watch = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };   // как часто заново узнаём состояния

    public StatusForm(Dictionary<string, (string Name, Func<Task<int>> Toggle, Func<Task<int>> GetState)> switches,
        int[] states, Action<int> click)
    {
        sw = switches;
        state = states;
        playClick = click;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Card;
        Opacity = 0.97;
        // Ссылка «Создать ярлыки…» нужна, только пока какого-то ярлыка ещё нет (ни на столе, ни на панели задач)
        showLink = new[] { "status", "bluetooth", "wifi", "mic", "sound", "lock", "sleep", "shutdown", "restart" }
            .Any(k => !Shortcuts.ExistsAnywhere(k));
        Size = new Size(Ui.S(400), Ui.S(showLink ? 421 : 366));
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Ui.S(48));

        watch.Tick += (s, e) => Watch();
        watch.Start();

        // Состояния могут меняться без нашего участия (Bluetooth включился, и появился микрофон гарнитуры),
        // поэтому, пока окно открыто, регулярно читаем их заново
        poll.Tick += (s, e) => RefreshAll();
        poll.Start();

    }

    // ----- Положение элементов (в пикселях эскиза, масштаб добавляется сам) -----
    Rectangle RowRect(int i) => new Rectangle(Ui.S(8), Ui.S(8) + i * Ui.S(54), Ui.S(384), Ui.S(54));
    Rectangle SwitchRect(int i) => new Rectangle(Ui.S(330), RowRect(i).Y + Ui.S(13), Ui.S(48), Ui.S(28));
    Rectangle ActionRect(int i) => new Rectangle(Ui.S(16) + i * Ui.S(94), Ui.S(271), Ui.S(86), Ui.S(76));
    readonly bool showLink;
    Rectangle LinkRect => showLink ? new Rectangle(Ui.S(8), Ui.S(366), Ui.S(384), Ui.S(44)) : Rectangle.Empty;

    // ----- Рисование -----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Ui.DrawCardBorder(g, ClientSize);

        using var nameFont = Ui.MakeFont("Segoe UI", 20);
        using var chipFont = Ui.MakeFont("Segoe UI Semibold", 15);
        using var noFont = Ui.MakeFont("Segoe UI", 18, FontStyle.Bold);
        using var capFont = Ui.MakeFont("Segoe UI Semibold", 12);
        using var actFont = Ui.MakeFont("Segoe UI", 12);
        using var linkFont = Ui.MakeFont("Segoe UI Semibold", 17);

        for (int i = 0; i < 4; i++)
        {
            var r = RowRect(i);
            int st = state[i];

            if (i == hoverRow)
            {
                using var path = Shapes.Round(r, Ui.F(8));
                using var fill = new SolidBrush(Theme.RowHover);
                g.FillPath(fill, path);
            }

            // Значок функции в цветной плитке: серый и перечёркнутый, если выключено
            var (col, tint) = Theme.State(st);
            var tile = new RectangleF(r.X + Ui.S(14), r.Y + (r.Height - Ui.S(36)) / 2f, Ui.S(36), Ui.S(36));
            Ui.DrawTile(g, tile, Ui.F(9), tint, FuncKeys[i], col, st == 0, 1.9, 0.62f);
            float tx = tile.Right + Ui.S(12);
            Ui.DrawText(g, sw[FuncKeys[i]].Name, nameFont, Theme.Text,
                new RectangleF(tx, r.Y, Ui.S(236) - tx - Ui.S(6), r.Height));

            // Короткое сообщение об ошибке в строке
            if (rowMsg[i] != null && DateTime.Now < rowMsgUntil[i])
            {
                Ui.DrawText(g, rowMsg[i]!, chipFont, Theme.Bad,
                    new RectangleF(Ui.S(176), r.Y, Ui.S(138), r.Height), StringAlignment.Far);
            }

            // Справа переключатель или «нет», если устройства нет
            if (st == -1)
                Ui.DrawText(g, Loc.T("sp_none"), noFont, Theme.Bad, new RectangleF(Ui.S(300), r.Y, Ui.S(78), r.Height), StringAlignment.Far);
            else
                DrawSwitch(g, SwitchRect(i), st == 1, busy[i]);
        }

        // Разделитель и ряд действий
        using (var line = new Pen(Theme.Line))
        {
            g.DrawLine(line, Ui.S(16), Ui.S(232), Width - Ui.S(16), Ui.S(232));
            if (showLink) g.DrawLine(line, Ui.S(16), Ui.S(359), Width - Ui.S(16), Ui.S(359));
        }
        Ui.DrawText(g, Loc.T("sp_actions"), capFont, Theme.Sub, new RectangleF(Ui.S(16), Ui.S(242), Ui.S(200), Ui.S(22)));

        for (int i = 0; i < 4; i++)
        {
            var r = ActionRect(i);
            using (var path = Shapes.Round(r, Ui.F(10)))
            {
                using var fill = new SolidBrush(i == hoverAction ? Theme.RowHover : Theme.Btn);
                g.FillPath(fill, path);
                using var border = new Pen(Theme.BtnLine);
                g.DrawPath(border, path);
            }
            float gs = Ui.F(26);
            var box = new RectangleF(r.X + (r.Width - gs) / 2f, r.Y + Ui.S(14), gs, gs);
            Glyphs.Draw(g, Actions[i].Key, box, Theme.Text, 1.9, false);
            Ui.DrawText(g, Actions[i].Title, actFont, Theme.Text,
                new RectangleF(r.X, box.Bottom + Ui.S(4), r.Width, Ui.S(20)), StringAlignment.Center);
        }

        // Ссылка «Создать ярлыки…»
        if (hoverLink)
        {
            using var path = Shapes.Round(LinkRect, Ui.F(8));
            using var fill = new SolidBrush(Theme.RowHover);
            g.FillPath(fill, path);
        }
        if (showLink)
            Ui.DrawText(g, Loc.T("sp_link"), linkFont, Theme.On, new RectangleF(Ui.S(20), Ui.S(366), Ui.S(360), Ui.S(44)));
    }

    // Переключатель: дорожка и круглый ползунок. Пока функция переключается, рисуется бледнее
    static void DrawSwitch(Graphics g, Rectangle r, bool on, bool dim)
    {
        var track = on ? Theme.TrackOn : Theme.TrackOff;
        var knob = on ? Theme.KnobOn : Theme.KnobOff;
        if (dim)
        {
            track = Color.FromArgb(110, track);
            knob = Color.FromArgb(150, knob);
        }
        using (var path = Shapes.Round(r, r.Height / 2f))
        using (var fill = new SolidBrush(track))
            g.FillPath(fill, path);
        int d = r.Height - 2 * Ui.S(3);
        int kx = on ? r.Right - Ui.S(3) - d : r.X + Ui.S(3);
        using var kb = new SolidBrush(knob);
        g.FillEllipse(kb, kx, r.Y + Ui.S(3), d, d);
    }

    // ----- Мышь -----
    void UpdateHover(Point p)
    {
        int row = -1, act = -1;
        bool link = false;
        for (int i = 0; i < 4; i++)
            if (RowRect(i).Contains(p)) { row = i; break; }
        for (int i = 0; i < 4; i++)
            if (ActionRect(i).Contains(p)) act = i;
        if (LinkRect.Contains(p)) link = true;

        if (row != hoverRow || act != hoverAction || link != hoverLink)
        {
            hoverRow = row; hoverAction = act; hoverLink = link;
            Invalidate();
        }
        Cursor = row >= 0 || act >= 0 || link ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverRow = -1; hoverAction = -1; hoverLink = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var p = e.Location;

        for (int i = 0; i < 4; i++)
        {
            if (!RowRect(i).Contains(p)) continue;
            if (state[i] != -1 && !busy[i]) ToggleRow(i);
            else if (state[i] == -1)
            {
                Msg(i, Loc.T("sp_notfound"));   // устройства нет: проверяем ещё раз, вдруг уже появилось
                RefreshAll();
                Invalidate();
            }
            return;
        }
        for (int i = 0; i < 4; i++)
        {
            if (ActionRect(i).Contains(p)) { Launch(Actions[i].Args); return; }
        }
        if (LinkRect.Contains(p)) { Launch("--setup"); return; }

        Close();   // щелчок мимо строк и кнопок закрывает окно
    }

    // ----- Действия -----
    // Переключаем функцию на месте: окно остаётся, строка обновляется
    async void ToggleRow(int i)
    {
        busy[i] = true;
        Invalidate();
        int r;
        try { r = await sw[FuncKeys[i]].Toggle(); }
        catch { r = -2; }
        busy[i] = false;
        if (IsDisposed) return;

        if (r == 1 || r == 0) state[i] = r;
        else if (r == -1) state[i] = -1;
        else Msg(i, Loc.T("sp_failed"));
        playClick(r);
        Invalidate();
        if (closeAfter) { Close(); return; }
        RefreshAll();   // переключение одной функции могло изменить другую (например, Bluetooth и микрофон гарнитуры)
    }

    // Заново читаем состояния всех строк и перерисовываем, если что-то изменилось
    async void RefreshAll()
    {
        if (refreshing || busy.Any(b => b)) return;
        refreshing = true;
        try
        {
            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                int r;
                try { r = await sw[FuncKeys[i]].GetState(); }
                catch { r = -1; }
                if (IsDisposed) return;
                if (busy[i]) continue;
                if (r != state[i]) { state[i] = r; changed = true; }
            }
            if (changed) Invalidate();
        }
        finally { refreshing = false; }
    }

    void Msg(int i, string text)
    {
        rowMsg[i] = text;
        rowMsgUntil[i] = DateTime.Now.AddSeconds(2);
    }

    // Запускаем программу с другими параметрами (действие или окно создания ярлыков) и закрываем это окно
    void Launch(string args)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) Process.Start(new ProcessStartInfo(exe, args + " --lang " + (Loc.Lang == 0 ? "ru" : "en")) { UseShellExecute = false });
        }
        catch { }
        Close();
    }

    // Пока мышь над окном, оно остаётся. Ушла мышь: закрываем через 5 секунд
    void Watch()
    {
        if (busy.Any(b => b) || Bounds.Contains(Cursor.Position)) lastInside = DateTime.Now;
        else if ((DateTime.Now - lastInside).TotalMilliseconds > 5000)
        {
            Close();
            return;
        }

        bool changed = false;
        for (int i = 0; i < 4; i++)
            if (rowMsg[i] != null && DateTime.Now >= rowMsgUntil[i]) { rowMsg[i] = null; changed = true; }
        if (changed) Invalidate();
    }

    // ----- Окно: выход по Esc, без прерывания переключения -----
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        lastInside = DateTime.Now;
        Activate();

        // Пока окно открыто, слушаем нажатия мыши по всему экрану (см. OnMouseHook)
        try
        {
            mouseHookProc = OnMouseHook;
            mouseHook = SetWindowsHookEx(14, mouseHookProc, GetModuleHandle(null), 0);   // 14 = WH_MOUSE_LL
        }
        catch { mouseHook = IntPtr.Zero; }
    }

    // ----- Закрытие щелчком в любом другом месте экрана -----
    // Щелчок на панели задач намеренно пропускаем: там пользователь нажимает на сам ярлык,
    // и второй запуск программы закрывает окно сам. Если закрывать его здесь, окно закрылось бы
    // и тут же открылось снова.
    [StructLayout(LayoutKind.Sequential)]
    struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }
    delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    MouseHookProc? mouseHookProc;   // ссылка нужна, чтобы Windows не потеряла обработчик
    IntPtr mouseHook = IntPtr.Zero;

    IntPtr OnMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = (int)wParam;
            // 0x201, 0x204, 0x207: нажатие левой, правой, средней кнопки мыши
            if (code >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207) && IsHandleCreated)
            {
                var d = Marshal.PtrToStructure<MouseHookData>(lParam);
                var p = new Point(d.X, d.Y);
                bool outside = !Bounds.Contains(p);
                bool onTaskbar = !Screen.FromPoint(p).WorkingArea.Contains(p);
                if (outside && !onTaskbar) BeginInvoke(new Action(Close));
            }
        }
        catch { }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref m, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Если Bluetooth или Wi-Fi прямо сейчас переключается, ждём окончания, иначе переключение оборвётся
        if (busy.Any(b => b))
        {
            closeAfter = true;
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        watch.Stop();
        poll.Stop();
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        base.OnFormClosed(e);
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
        string verb = restart ? Loc.T("cd_restart") : Loc.T("cd_shutdown");
        string finalWord = restart ? "Restart" : "Shut down";
        string finalHint = restart ? Loc.T("cd_restarting") : Loc.T("cd_shuttingdown");
        string command = restart ? "/r /t 0" : "/s /t 0";
        string subText = restart ? Loc.T("cd_sub_restart") : Loc.T("cd_sub_shutdown");
        string sec = Loc.T("cd_sec");

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
            Text = test ? verb + " " + Loc.T("cd_test") : verb
        };
        var sub = new Label
        {
            Location = new Point(Ui.S(104), Ui.S(58)), Size = new Size(Ui.S(240), Ui.S(44)),
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Theme.Sub,
            Font = Ui.MakeFont("Segoe UI", 15),
            Text = subText + " 5\u00A0" + sec
        };
        var hint = new Label
        {
            Location = new Point(Ui.S(22), Ui.S(104)), Size = new Size(Ui.S(316), Ui.S(34)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Sub,
            Font = Ui.MakeFont("Segoe UI", 13),
            Text = Loc.T("cd_hint")
        };
        var btn = new Button
        {
            Location = new Point(Ui.S(22), Ui.S(146)), Size = new Size(Ui.S(316), Ui.S(44)),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = Theme.Btn,
            ForeColor = Theme.Text,
            Font = Ui.MakeFont("Segoe UI Semibold", 17),
            Text = Loc.T("cd_cancel")
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
                sub.Text = subText + " " + left + "\u00A0" + sec;
                Beeper.Play(0.35, (tones[5 - left], 150));   // нота для цифры 4, 3, 2, 1
            }
            else if (left == 0)
            {
                canCancel = false;
                Beeper.Play(0.4, (392.0, 450));   // низкая длинная нота: отмена уже невозможна
                big.Text = "0";
                title.Text = finalWord;
                sub.Text = test ? (restart ? Loc.T("cd_test_restart") : Loc.T("cd_test_shutdown")) : finalHint;
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
