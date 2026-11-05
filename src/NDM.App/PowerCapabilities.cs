using System.Runtime.InteropServices;

namespace NDM.App;

/// <summary>1.11.0: what the machine can do, asked before the power dropdown is filled. Tests replace <see cref="PowerCapabilities.Current"/>; nothing here changes the power state.</summary>
public interface IPowerCapabilities { bool HibernateAvailable { get; } }

public static class PowerCapabilities
{
    public static IPowerCapabilities Current { get; set; } = new SystemPowerCapabilities();
}

public sealed class SystemPowerCapabilities : IPowerCapabilities
{
    [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, byte[] output, uint outputSize);
    private bool? hibernate;
    /// <summary>SYSTEM_POWER_CAPABILITIES: SystemS4 (byte 6) and HiberFilePresent (byte 8). Read only; any failure counts as not available.</summary>
    public bool HibernateAvailable => hibernate ??= Read();
    private static bool Read()
    {
        try
        {
            var buffer = new byte[96];
            return CallNtPowerInformation(4, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0 && buffer[6] != 0 && buffer[8] != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return false; }
    }
}
