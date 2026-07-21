// ReSharper disable CheckNamespace
#pragma warning disable 1591

using System.Collections.Generic;
using System.ComponentModel;

namespace openPDC.Model
{
    /// <summary>
    /// Data-driven representation of a device (PMU) to import, mirroring what the openPDC Input
    /// Device Wizard produces from a .PmuConnection file, but supplied directly as JSON instead of
    /// read from a live PMU connection. A single request item is either a standalone PMU (with its
    /// own <see cref="Phasors"/>) or a concentrator (<see cref="IsConcentrator"/> = true) whose child
    /// PMUs are supplied in <see cref="Children"/>.
    /// </summary>
    public class DeviceImportRequest
    {
        /// <summary>Unique device acronym; used as the upsert key.</summary>
        public string Acronym { get; set; }

        /// <summary>Human-readable device name; defaults to <see cref="Acronym"/> when empty.</summary>
        public string Name { get; set; }

        /// <summary>Device access ID (ID code).</summary>
        public int AccessID { get; set; }

        /// <summary>Frames per second; defaults to 30 when not provided.</summary>
        public int? FramesPerSecond { get; set; }

        /// <summary>Company foreign key (already resolved to its numeric ID).</summary>
        public int? CompanyID { get; set; }

        /// <summary>Protocol foreign key (already resolved to its numeric ID).</summary>
        public int? ProtocolID { get; set; }

        /// <summary>Historian foreign key (already resolved to its numeric ID).</summary>
        public int? HistorianID { get; set; }

        /// <summary>Vendor device foreign key (already resolved to its numeric ID).</summary>
        public int? VendorDeviceID { get; set; }

        /// <summary>Interconnection foreign key (already resolved to its numeric ID).</summary>
        public int? InterconnectionID { get; set; }

        /// <summary>Connection string for the device (optional).</summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// When true, this item is a concentrator: no phasors/measurements are created for it and its
        /// child PMUs must be supplied in <see cref="Children"/>.
        /// </summary>
        public bool IsConcentrator { get; set; }

        /// <summary>Phasor definitions for a standalone PMU. Ignored when <see cref="IsConcentrator"/> is true.</summary>
        public List<PhasorImportItem> Phasors { get; set; }

        /// <summary>Analog measurement labels; one measurement is created per entry.</summary>
        public List<string> AnalogLabels { get; set; }

        /// <summary>Digital measurement labels; one measurement is created per entry (label may be empty).</summary>
        public List<string> DigitalLabels { get; set; }

        /// <summary>Child PMUs when this item is a concentrator. They inherit this item's foreign keys.</summary>
        public List<DeviceImportChild> Children { get; set; }
    }

    /// <summary>
    /// A child PMU of a concentrator. Carries its own identification, phasors and measurement labels,
    /// but inherits the parent's foreign keys (Company/Protocol/Historian/VendorDevice/Interconnection).
    /// </summary>
    public class DeviceImportChild
    {
        /// <summary>Unique device acronym; used as the upsert key.</summary>
        public string Acronym { get; set; }

        /// <summary>Human-readable device name; defaults to <see cref="Acronym"/> when empty.</summary>
        public string Name { get; set; }

        /// <summary>Device access ID (ID code).</summary>
        public int AccessID { get; set; }

        /// <summary>Frames per second; defaults to 30 when not provided.</summary>
        public int? FramesPerSecond { get; set; }

        /// <summary>Phasor definitions for this child PMU.</summary>
        public List<PhasorImportItem> Phasors { get; set; }

        /// <summary>Analog measurement labels; one measurement is created per entry.</summary>
        public List<string> AnalogLabels { get; set; }

        /// <summary>Digital measurement labels; one measurement is created per entry (label may be empty).</summary>
        public List<string> DigitalLabels { get; set; }
    }

    /// <summary>
    /// A single phasor definition to import. Phasors with an empty or "unused" label are skipped,
    /// matching the Input Device Wizard behavior.
    /// </summary>
    public class PhasorImportItem
    {
        /// <summary>Phasor label.</summary>
        public string Label { get; set; }

        /// <summary>Phasor type: "V" (voltage) or "I" (current). Defaults to voltage.</summary>
        [DefaultValue("V")]
        public string Type { get; set; }

        /// <summary>Phasor phase: "+", "-", "0", "A", ... Defaults to "+".</summary>
        [DefaultValue("+")]
        public string Phase { get; set; }

        /// <summary>Nominal line kV associated with the phasor; used by the point tag generator.</summary>
        [DefaultValue(0)]
        public int BaseKV { get; set; }

        /// <summary>Optional 1-based source index; defaults to the item's position in the list.</summary>
        public int? SourceIndex { get; set; }
    }
}
