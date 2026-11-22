using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Capsul Box: таймер выключения ПК. Выключает сама Windows (shutdown /s /t), поэтому таймер сработает, даже если
/// агент закроют; отмена — shutdown /a. Работает только при включённом в настройках управлении питанием.
/// </summary>
public sealed class ShutdownTimer(ConfigStore config, ILogger<ShutdownTimer> log)
{
    private long _deadlineMs; // 0 — таймера нет

    public object State()
    {
        var d = Interlocked.Read(ref _deadlineMs);
        if (d != 0 && d < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) d = 0;
        return new { allowed = config.Get().Controls.AllowPower, deadline = d == 0 ? (long?)null : d };
    }

    /// <summary>minutes = 0 отменяет таймер. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SetAsync(int minutes, CancellationToken ct)
    {
        if (!config.Get().Controls.AllowPower) return "Разреши управление питанием: в настройках агента включи «Enable hold-to-confirm power controls»";
        if (minutes is < 0 or > 1440) return "От 1 минуты до 24 часов";
        if (!OperatingSystem.IsWindows()) return "Только Windows";
        await Run(["/a"], ct); // снять прежний таймер (если его нет — Windows просто ответит ошибкой)
        if (minutes == 0) { Interlocked.Exchange(ref _deadlineMs, 0); return null; }
        if (await Run(["/s", "/t", (minutes * 60).ToString()], ct) != 0) return "Windows не приняла таймер выключения";
        Interlocked.Exchange(ref _deadlineMs, DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds());
        return null;
    }

    private async Task<int> Run(string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug("shutdown: {Msg}", e.Message); return -1; }
    }
}
