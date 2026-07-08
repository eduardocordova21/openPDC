using GSF.PhasorProtocols;
using openPDC.Model;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace openPDC.Adapters.Services
{
    /// <summary>
    /// Provides device (PMU) persistence operations backing <see cref="DeviceController"/>,
    /// covering .PmuConnection-file based upserts and batch upserts.
    /// </summary>
    public interface IDeviceService
    {
        /// <summary>
        /// Persists the parent device (and, for concentrators, its child devices) along with their
        /// phasors and measurements, from a received configuration frame.
        /// </summary>
        /// <param name="settings">Connection settings parsed from the .PmuConnection file.</param>
        /// <param name="configFrame">Configuration frame received from the PMU.</param>
        /// <param name="validRequest">Validated device metadata for the request.</param>
        /// <param name="userName">Name of the user to record as the creator/updater of saved records.</param>
        /// <returns>The number of devices saved.</returns>
        Task<int> ProcessConfigurationFrameAsync(ConnectionSettings settings, IConfigurationFrame configFrame, DeviceMetadata validRequest, string userName);

        /// <summary>
        /// Inserts new device records or updates existing ones (matched by Acronym) for an entire
        /// batch, reporting the outcome of every device individually.
        /// </summary>
        /// <param name="devices">The devices to update or insert.</param>
        /// <param name="userName">Name of the user to record as the creator/updater of saved records.</param>
        /// <returns>The per-device results and a summary of the batch operation.</returns>
        UpsertDeviceResponse UpsertDevices(IReadOnlyList<Device> devices, string userName);

        /// <summary>
        /// Validates and parses a multipart/form-data request containing a .PmuConnection file and
        /// the metadata needed to create or update the associated device(s).
        /// </summary>
        /// <param name="request">The incoming HTTP request.</param>
        /// <returns>The parsed device metadata and file contents.</returns>
        Task<DeviceMetadata> ValidateRequestAsync(HttpRequestMessage request);
    }
}