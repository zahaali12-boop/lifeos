using Quicker.Inventory.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Inventory.Application;

/// <summary>Lots and serials for other modules (<see cref="ITrackingDirectory"/>): the lot and serial services behind one contract.</summary>
public sealed class TrackingDirectory(LotService lots, SerialService serials) : ITrackingDirectory
{
    public Task<LotInfo?> FindLotAsync(Guid lotId, CancellationToken cancellationToken = default) => lots.GetAsync(lotId, cancellationToken);

    public Task<SerialInfo?> FindSerialAsync(Guid itemId, string serialNumber, CancellationToken cancellationToken = default) => serials.FindAsync(itemId, serialNumber, cancellationToken);

    public Task<Result<SerialInfo>> SetSerialStatusAsync(Guid serialId, string status, string? note, CancellationToken cancellationToken = default) =>
        serials.SetStatusAsync(serialId, new SerialStatusRequest(status, note), cancellationToken);
}
