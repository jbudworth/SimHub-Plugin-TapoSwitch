using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimHub.Plugin.TapoSwitch.Tapo
{
    /// <summary>Common surface both the legacy Kasa client and the KLAP/Tapo client implement.</summary>
    public interface ISmartSwitchClient : IDisposable
    {
        Task<bool> GetIsOnAsync(CancellationToken ct = default);
        Task SetOnAsync(bool on, CancellationToken ct = default);
        Task<bool> ToggleAsync(CancellationToken ct = default);
    }
}
