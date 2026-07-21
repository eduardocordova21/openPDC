using GSF.Data;
using GSF.Data.Model;
using GSF.Diagnostics;
using openPDC.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace openPDC.Adapters.Services
{
    /// <summary>
    /// Data-driven counterpart of the .PmuConnection-file flow: creates devices, phasors and all
    /// derived measurements from data supplied directly as JSON (no PMU connection), replicating
    /// what the openPDC Input Device Wizard produces. Kept in a separate partial file so the
    /// upstream-shaped <see cref="ProcessConfigurationFrameAsync"/> flow stays untouched and merges
    /// with GPA remain clean.
    /// </summary>
    public partial class DeviceService
    {
        #region [ Methods ]

        /// <inheritdoc/>
        public UpsertDeviceResponse UpsertDevicesFromData(IReadOnlyList<DeviceImportRequest> devices, string userName)
        {
            Log.Publish(MessageLevel.Info, nameof(UpsertDevicesFromData), $"Importing {devices.Count} device item(s) from data");

            using AdoDataConnection context = DataContext;

            TableOperations<Device> deviceTable = new(context);
            TableOperations<Phasor> phasorTable = new(context);
            TableOperations<Measurement> measurementTable = new(context);

            SignalTypeCache signalTypes = LoadSignalTypes(context);

            var response = new UpsertDeviceResponse
            {
                NumberOfReceivedRecords = 0,
                NumberOfRecordsProcessedWithSuccess = 0,
                NumberOfRecordsWithFail = 0,
                Details = []
            };

            foreach (var item in devices)
            {
                response.NumberOfReceivedRecords++;

                try
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Acronym))
                        throw new InvalidOperationException("Device Acronym is required.");

                    bool hasChildren = item.Children != null && item.Children.Count > 0;

                    if (item.IsConcentrator && !hasChildren)
                        throw new InvalidOperationException("A concentrator must include at least one child device.");

                    if (!item.IsConcentrator && hasChildren)
                        throw new InvalidOperationException("A standalone PMU must not include child devices; set IsConcentrator = true.");

                    if (item.IsConcentrator)
                    {
                        // Upsert the parent concentrator; the wizard does not create phasors or
                        // measurements for the concentrator itself, only for its child PMUs.
                        var parentDevice = BuildDevice(item.Acronym, item.Name, item.AccessID, item.FramesPerSecond,
                            item.CompanyID, item.ProtocolID, item.HistorianID, item.VendorDeviceID, item.InterconnectionID,
                            isConcentrator: true, parentID: null, connectionString: item.ConnectionString);

                        bool parentExists = deviceTable.QueryRecordWhere("Acronym = {0}", item.Acronym) != null;
                        int parentDeviceID = UpsertDeviceRecord(parentDevice, userName);

                        response.NumberOfRecordsProcessedWithSuccess++;
                        AddDetail(response, item.Acronym, parentExists ? UpsertDeviceStatus.Updated : UpsertDeviceStatus.Included,
                            $"Concentrator saved with {item.Children.Count} child device(s).");

                        foreach (var child in item.Children)
                        {
                            response.NumberOfReceivedRecords++;

                            try
                            {
                                if (string.IsNullOrWhiteSpace(child.Acronym))
                                    throw new InvalidOperationException("Child device Acronym is required.");

                                // Children inherit the concentrator's foreign keys.
                                var childDevice = BuildDevice(child.Acronym, child.Name, child.AccessID, child.FramesPerSecond,
                                    item.CompanyID, item.ProtocolID, item.HistorianID, item.VendorDeviceID, item.InterconnectionID,
                                    isConcentrator: false, parentID: parentDeviceID, connectionString: string.Empty);

                                bool childExists = deviceTable.QueryRecordWhere("Acronym = {0}", child.Acronym) != null;
                                int childDeviceID = UpsertDeviceRecord(childDevice, userName);

                                SavePhasorsFromItems(child.Phasors, childDeviceID, phasorTable, userName);
                                int count = SaveMeasurementsFromItems(child.Acronym, child.Name, childDeviceID, item.HistorianID,
                                    child.Phasors, child.AnalogLabels, child.DigitalLabels, signalTypes, measurementTable, context, userName);

                                response.NumberOfRecordsProcessedWithSuccess++;
                                AddDetail(response, child.Acronym, childExists ? UpsertDeviceStatus.Updated : UpsertDeviceStatus.Included,
                                    $"Child device saved with {count} measurement(s).");
                            }
                            catch (Exception childEx)
                            {
                                response.NumberOfRecordsWithFail++;
                                AddDetail(response, child.Acronym, UpsertDeviceStatus.Failed, $"Failed to import child device, exception: {childEx.Message}");
                                Log.Publish(MessageLevel.Error, nameof(UpsertDevicesFromData),
                                    $"Failed to import child device '{child.Acronym}'", exception: childEx);
                            }
                        }
                    }
                    else
                    {
                        var device = BuildDevice(item.Acronym, item.Name, item.AccessID, item.FramesPerSecond,
                            item.CompanyID, item.ProtocolID, item.HistorianID, item.VendorDeviceID, item.InterconnectionID,
                            isConcentrator: false, parentID: null, connectionString: item.ConnectionString);

                        bool deviceExists = deviceTable.QueryRecordWhere("Acronym = {0}", item.Acronym) != null;
                        int deviceID = UpsertDeviceRecord(device, userName);

                        SavePhasorsFromItems(item.Phasors, deviceID, phasorTable, userName);
                        int count = SaveMeasurementsFromItems(item.Acronym, item.Name, deviceID, item.HistorianID,
                            item.Phasors, item.AnalogLabels, item.DigitalLabels, signalTypes, measurementTable, context, userName);

                        response.NumberOfRecordsProcessedWithSuccess++;
                        AddDetail(response, item.Acronym, deviceExists ? UpsertDeviceStatus.Updated : UpsertDeviceStatus.Included,
                            $"Device saved with {count} measurement(s).");
                    }
                }
                catch (Exception ex)
                {
                    response.NumberOfRecordsWithFail++;
                    AddDetail(response, item?.Acronym, UpsertDeviceStatus.Failed, $"Failed to import device, exception: {ex.Message}");
                    Log.Publish(MessageLevel.Error, nameof(UpsertDevicesFromData),
                        $"Failed to import device '{item?.Acronym}'", exception: ex);
                }
            }

            return response;
        }

        /// <summary>
        /// Builds a <see cref="Device"/> record from import data, using the same defaults as the
        /// .PmuConnection-file flow (see BuildParentDevice / ProcessAndSaveChildDevice).
        /// </summary>
        private static Device BuildDevice(string acronym, string name, int accessID, int? framesPerSecond,
            int? companyID, int? protocolID, int? historianID, int? vendorDeviceID, int? interconnectionID,
            bool isConcentrator, int? parentID, string connectionString)
        {
            return new Device
            {
                Acronym = acronym,
                Name = string.IsNullOrWhiteSpace(name) ? acronym : name,
                IsConcentrator = isConcentrator,
                ProtocolID = protocolID,
                CompanyID = companyID,
                HistorianID = historianID,
                VendorDeviceID = vendorDeviceID,
                InterconnectionID = interconnectionID,
                ParentID = parentID,
                AccessID = accessID,
                FramesPerSecond = framesPerSecond > 0 ? framesPerSecond : 30,
                ConnectionString = connectionString ?? string.Empty,
                Enabled = true,
                AllowUseOfCachedConfiguration = true,
                AutoStartDataParsingSequence = true,
                ConnectOnDemand = parentID == null,
                DataLossInterval = 5.0,
                AllowedParsingExceptions = 10,
                ParsingExceptionWindow = 5.0,
                DelayedConnectionInterval = 5.0,
                MeasurementReportingInterval = 100000
            };
        }

        /// <summary>
        /// Inserts or updates phasors from import data, matched by (DeviceID, SourceIndex). Skips
        /// phasors with an empty or "unused" label, mirroring the wizard. Persists Phase and BaseKV.
        /// </summary>
        private static void SavePhasorsFromItems(List<PhasorImportItem> phasors, int deviceID, TableOperations<Phasor> phasorTable, string userName)
        {
            if (phasors == null)
                return;

            int sourceIndex = 1;

            foreach (var item in phasors)
            {
                string label = item?.Label?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(label) || label.Equals("unused", StringComparison.OrdinalIgnoreCase))
                {
                    sourceIndex++;
                    continue;
                }

                int targetIndex = item.SourceIndex ?? sourceIndex;

                var existingPhasor = phasorTable.QueryRecordWhere(
                    "DeviceID = {0} AND SourceIndex = {1}", deviceID, targetIndex);

                var phasor = new Phasor
                {
                    DeviceID = deviceID,
                    Label = label,
                    Type = string.Equals(item.Type, "I", StringComparison.OrdinalIgnoreCase) ? "I" : "V",
                    Phase = string.IsNullOrEmpty(item.Phase) ? "+" : item.Phase,
                    BaseKV = item.BaseKV,
                    SourceIndex = targetIndex
                };

                var nowTime = DateTime.Now;
                var now = new DateTime(nowTime.Year, nowTime.Month, nowTime.Day, nowTime.Hour, nowTime.Minute, nowTime.Second, nowTime.Millisecond, DateTimeKind.Local);

                phasor.CreatedBy = userName;
                phasor.UpdatedBy = userName;
                phasor.CreatedOn = now;
                phasor.UpdatedOn = now;

                if (existingPhasor == null)
                    phasorTable.AddNewRecord(phasor);
                else
                    phasorTable.UpdateRecord(phasor, new RecordRestriction(
                        "DeviceID = {0} AND SourceIndex = {1}", deviceID, targetIndex));

                sourceIndex++;
            }
        }

        /// <summary>
        /// Creates or updates all measurements derived from a device's phasors, analog and digital
        /// labels: PMU-level signals (FREQ/DFDT/FLAG), phasor magnitude/angle pairs, analogs and
        /// digitals. PointTags are generated by the canonical
        /// <see cref="global::PhasorProtocolAdapters.CommonPhasorServices.CreatePointTag"/>; the
        /// SignalReference is {acronym}-{suffix}{index}. Returns the number of measurements upserted.
        /// </summary>
        private static int SaveMeasurementsFromItems(string deviceAcronym, string deviceName, int deviceID, int? historianID,
            List<PhasorImportItem> phasors, List<string> analogLabels, List<string> digitalLabels,
            SignalTypeCache signalTypes, TableOperations<Measurement> measurementTable, AdoDataConnection context, string userName)
        {
            string name = string.IsNullOrWhiteSpace(deviceName) ? deviceAcronym : deviceName;

            // Resolve company/vendor acronyms (needed by CreatePointTag) from the device view, the
            // same way the .PmuConnection flow does after the device upsert.
            TableOperations<DeviceDetail> deviceDetailTable = new(context);
            var deviceDetail = deviceDetailTable.QueryRecordWhere("Acronym = {0}", deviceAcronym);
            string companyAcronym = deviceDetail?.CompanyAcronym ?? string.Empty;
            string vendorAcronym = deviceDetail?.VendorAcronym;

            var nowTime = DateTime.Now;
            var now = new DateTime(nowTime.Year, nowTime.Month, nowTime.Day, nowTime.Hour, nowTime.Minute, nowTime.Second, nowTime.Millisecond, DateTimeKind.Local);

            int count = 0;

            // PMU-level signals: frequency, dF/dt, status flags. SignalReference {acronym}-{suffix}.
            foreach (string suffix in new[] { "FQ", "DF", "SF" })
            {
                if (!signalTypes.PmuTypes.TryGetValue(suffix, out var pmuType))
                    continue;

                CommonController.UpsertMeasurement(measurementTable, new Measurement
                {
                    DeviceID = deviceID,
                    HistorianID = historianID,
                    PointTag = CreateTag(companyAcronym, deviceAcronym, vendorAcronym, pmuType.Acronym, null, -1, DefaultPhase, 0),
                    SignalTypeID = pmuType.ID,
                    SignalReference = $"{deviceAcronym}-{suffix}",
                    Description = $"{name} {CommonController.PmuSignalDescription(suffix)}",
                    Internal = true,
                    Enabled = true,
                    Adder = 0.0d,
                    Multiplier = 1.0d,
                    CreatedBy = userName,
                    UpdatedBy = userName,
                    CreatedOn = now,
                    UpdatedOn = now
                });
                count++;
            }

            // Phasor measurements: magnitude (PM) and angle (PA) for each defined phasor.
            if (phasors != null)
            {
                int phasorIndex = 1;

                foreach (var phasorItem in phasors)
                {
                    string label = phasorItem?.Label?.Trim() ?? string.Empty;

                    if (string.IsNullOrEmpty(label) || label.Equals("unused", StringComparison.OrdinalIgnoreCase))
                    {
                        phasorIndex++;
                        continue;
                    }

                    bool isVoltage = !string.Equals(phasorItem.Type, "I", StringComparison.OrdinalIgnoreCase);
                    var phasorTypes = isVoltage ? signalTypes.VoltageTypes : signalTypes.CurrentTypes;
                    char phaseChar = string.IsNullOrEmpty(phasorItem.Phase) ? '+' : phasorItem.Phase[0];

                    foreach (string sfx in new[] { "PM", "PA" })
                    {
                        if (!phasorTypes.TryGetValue(sfx, out var phasorType))
                            continue;

                        string measurementLabel = sfx == "PM"
                            ? (isVoltage ? "Voltage Magnitude" : "Current Magnitude")
                            : (isVoltage ? "Voltage Angle" : "Current Angle");

                        CommonController.UpsertMeasurement(measurementTable, new Measurement
                        {
                            DeviceID = deviceID,
                            HistorianID = historianID,
                            PointTag = CreateTag(companyAcronym, deviceAcronym, vendorAcronym, phasorType.Acronym, label, phasorIndex, phaseChar, phasorItem.BaseKV),
                            SignalTypeID = phasorType.ID,
                            PhasorSourceIndex = phasorIndex,
                            SignalReference = $"{deviceAcronym}-{sfx}{phasorIndex}",
                            Description = $"{name} {label} {measurementLabel}",
                            Internal = true,
                            Enabled = true,
                            Adder = 0.0d,
                            Multiplier = 1.0d,
                            CreatedBy = userName,
                            UpdatedBy = userName,
                            CreatedOn = now,
                            UpdatedOn = now
                        });
                        count++;
                    }

                    phasorIndex++;
                }
            }

            // Analog values. SignalReference {acronym}-AV{index}.
            if (signalTypes.AnalogID.HasValue && analogLabels != null)
            {
                int analogIndex = 1;

                foreach (string analogLabel in analogLabels)
                {
                    string label = analogLabel?.Trim() ?? string.Empty;

                    CommonController.UpsertMeasurement(measurementTable, new Measurement
                    {
                        DeviceID = deviceID,
                        HistorianID = historianID,
                        PointTag = CreateTag(companyAcronym, deviceAcronym, vendorAcronym, signalTypes.AnalogAcronym, label, analogIndex, DefaultPhase, 0),
                        SignalTypeID = signalTypes.AnalogID.Value,
                        SignalReference = $"{deviceAcronym}-AV{analogIndex}",
                        Description = $"{name} Analog Value {analogIndex}",
                        Internal = true,
                        Enabled = true,
                        Adder = 0.0d,
                        Multiplier = 1.0d,
                        CreatedBy = userName,
                        UpdatedBy = userName,
                        CreatedOn = now,
                        UpdatedOn = now
                    });
                    count++;
                    analogIndex++;
                }
            }

            // Digital values. SignalReference {acronym}-DV{index}.
            if (signalTypes.DigitalID.HasValue && digitalLabels != null)
            {
                int digitalIndex = 1;

                foreach (string digitalLabel in digitalLabels)
                {
                    string label = digitalLabel?.Trim() ?? string.Empty;

                    CommonController.UpsertMeasurement(measurementTable, new Measurement
                    {
                        DeviceID = deviceID,
                        HistorianID = historianID,
                        PointTag = CreateTag(companyAcronym, deviceAcronym, vendorAcronym, signalTypes.DigitalAcronym, label, digitalIndex, DefaultPhase, 0),
                        SignalTypeID = signalTypes.DigitalID.Value,
                        SignalReference = $"{deviceAcronym}-DV{digitalIndex}",
                        Description = $"{name} Digital Value {digitalIndex}",
                        Internal = true,
                        Enabled = true,
                        Adder = 0.0d,
                        Multiplier = 1.0d,
                        CreatedBy = userName,
                        UpdatedBy = userName,
                        CreatedOn = now,
                        UpdatedOn = now
                    });
                    count++;
                    digitalIndex++;
                }
            }

            Log.Publish(MessageLevel.Info, nameof(SaveMeasurementsFromItems),
                $"Upserted {count} measurement(s) for device '{deviceAcronym}'");

            return count;
        }

        /// <summary>
        /// Generates a point tag using the system's configured point tag name expression via the
        /// canonical CommonPhasorServices.CreatePointTag. Falls back to a deterministic tag if that
        /// call fails, so a naming-expression problem never blocks measurement creation.
        /// </summary>
        private static string CreateTag(string companyAcronym, string deviceAcronym, string vendorAcronym,
            string signalTypeAcronym, string label, int signalIndex, char phase, int baseKV)
        {
            try
            {
                return global::PhasorProtocolAdapters.CommonPhasorServices.CreatePointTag(
                    companyAcronym, deviceAcronym, vendorAcronym, signalTypeAcronym, label, signalIndex, phase, baseKV);
            }
            catch (Exception ex)
            {
                Log.Publish(MessageLevel.Warning, nameof(CreateTag),
                    $"CreatePointTag failed for device '{deviceAcronym}' signal '{signalTypeAcronym}'; using fallback tag.", exception: ex);

                string prefix = string.IsNullOrEmpty(companyAcronym) ? deviceAcronym : $"{companyAcronym}_{deviceAcronym}";
                string indexPart = signalIndex > 0 ? signalIndex.ToString() : string.Empty;
                return $"{prefix}:{signalTypeAcronym}{indexPart}";
            }
        }

        /// <summary>
        /// Loads the SignalType records needed for measurement creation, once per batch.
        /// </summary>
        private static SignalTypeCache LoadSignalTypes(AdoDataConnection context)
        {
            TableOperations<GSF.TimeSeries.Model.SignalType> sigTypeTable = new(context);

            var cache = new SignalTypeCache();

            foreach (string suffix in new[] { "FQ", "DF", "SF" })
            {
                var st = sigTypeTable.QueryRecordWhere("Suffix = {0} AND Source = 'PMU'", suffix);
                if (st?.ID > 0)
                    cache.PmuTypes[suffix] = (st.ID, st.Acronym ?? suffix);
            }

            foreach (string acronym in new[] { "VPHM", "VPHA" })
            {
                var st = sigTypeTable.QueryRecordWhere("Acronym = {0}", acronym);
                if (st?.ID > 0)
                    cache.VoltageTypes[st.Suffix] = (st.ID, st.Acronym);
            }

            foreach (string acronym in new[] { "IPHM", "IPHA" })
            {
                var st = sigTypeTable.QueryRecordWhere("Acronym = {0}", acronym);
                if (st?.ID > 0)
                    cache.CurrentTypes[st.Suffix] = (st.ID, st.Acronym);
            }

            var alog = sigTypeTable.QueryRecordWhere("Acronym = {0}", "ALOG");
            if (alog?.ID > 0)
            {
                cache.AnalogID = alog.ID;
                cache.AnalogAcronym = alog.Acronym ?? "ALOG";
            }

            var digi = sigTypeTable.QueryRecordWhere("Acronym = {0}", "DIGI");
            if (digi?.ID > 0)
            {
                cache.DigitalID = digi.ID;
                cache.DigitalAcronym = digi.Acronym ?? "DIGI";
            }

            return cache;
        }

        /// <summary>
        /// Appends a per-device result to the response.
        /// </summary>
        private static void AddDetail(UpsertDeviceResponse response, string acronym, UpsertDeviceStatus status, string message = null)
        {
            response.Details.Add(new UpsertDeviceResponseDetail
            {
                DeviceAcronym = acronym,
                Status = status.ToString(),
                Message = message
            });
        }

        #endregion [ Methods ]

        #region [ Nested Types ]

        /// <summary>
        /// Signal types pre-loaded once per batch for measurement creation. Each phasor entry is
        /// keyed by its Suffix ("PM" magnitude / "PA" angle) and carries the type ID and acronym.
        /// </summary>
        private sealed class SignalTypeCache
        {
            public Dictionary<string, (int ID, string Acronym)> PmuTypes { get; } = new();
            public Dictionary<string, (int ID, string Acronym)> VoltageTypes { get; } = new();
            public Dictionary<string, (int ID, string Acronym)> CurrentTypes { get; } = new();
            public int? AnalogID { get; set; }
            public string AnalogAcronym { get; set; } = "ALOG";
            public int? DigitalID { get; set; }
            public string DigitalAcronym { get; set; } = "DIGI";
        }

        #endregion [ Nested Types ]

        #region [ Static ]

        /// <summary>Phase argument used for non-phasor signals (no meaningful phase).</summary>
        private const char DefaultPhase = ' ';

        #endregion [ Static ]
    }
}
