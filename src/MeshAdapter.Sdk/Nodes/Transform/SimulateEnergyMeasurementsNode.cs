using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.SimulationNodes.Generators;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(SimulateEnergyMeasurementsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SimulateEnergyMeasurementsNode(NodeDelegate next) : IPipelineNode
{
    public Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SimulateEnergyMeasurementsNodeConfiguration>();

        if (c.NumDays <= 0)
        {
            throw new InvalidOperationException(
                $"SimulateEnergyMeasurements: NumDays must be > 0 (got {c.NumDays}).");
        }

        if (c.MeteringPoints.Count == 0)
        {
            throw new InvalidOperationException(
                "SimulateEnergyMeasurements: MeteringPoints list is empty.");
        }

        var emCkType = new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId);
        var timeRangeRecordId = new RtCkId<CkRecordId>(c.TimeRangeCkRecordId);
        var amountRecordId = new RtCkId<CkRecordId>(c.AmountCkRecordId);
        var roleId = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);
        var startUtc = DateTime.SpecifyKind(c.StartDate, DateTimeKind.Utc);
        var slotDuration = TimeSpan.FromMinutes(15);
        const int slotsPerDay = 96;

        var entities = new List<EntityUpdateInfo<RtEntity>>();
        var associations = new List<AssociationUpdateInfo>();

        foreach (var mp in c.MeteringPoints)
        {
            if (mp.ObisCodes.Count == 0)
            {
                nodeContext.Warning(
                    $"SimulateEnergyMeasurements: MeteringPoint '{mp.MeteringPointRtId}' has no ObisCodes — skipped.");
                continue;
            }

            var mpRtId = new OctoObjectId(mp.MeteringPointRtId);
            var mpCkType = new RtCkId<CkTypeId>(mp.MeteringPointCkTypeId);
            var profileParts = mp.ProfileKind.Split(':', 2);
            var profileFamily = profileParts[0];
            var profileSubKey = profileParts.Length > 1 ? profileParts[1] : string.Empty;

            for (var day = 0; day < c.NumDays; day++)
            {
                var dayStart = startUtc.AddDays(day);
                var dayOfYear = dayStart.DayOfYear;

                for (var slot = 0; slot < slotsPerDay; slot++)
                {
                    var from = dayStart.AddTicks(slotDuration.Ticks * slot);
                    var to = from.Add(slotDuration);

                    double amount = profileFamily switch
                    {
                        "Load" => EnergyProfiles.LoadProfileSlot(profileSubKey, mp.ProfileParameter, slot),
                        "PV" => EnergyProfiles.PvProfileSlot(mp.ProfileParameter, dayOfYear, slot),
                        _ => throw new InvalidOperationException(
                            $"SimulateEnergyMeasurements: unsupported ProfileKind '{mp.ProfileKind}' on MeteringPoint '{mp.MeteringPointRtId}'.")
                    };

                    foreach (var obisCode in mp.ObisCodes)
                    {
                        var emRtId = OctoObjectId.GenerateNewId();
                        var wkn = $"EM-{mp.MeteringPointRtId}-{obisCode}";

                        var timeRange = new RtRecord { CkRecordId = timeRangeRecordId };
                        timeRange.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
                        timeRange.SetAttributeValue("To", AttributeValueTypesDto.DateTime, to);

                        // Basic/Amount is a record carrying { Value: Double, Unit: Enum<UnitOfMeasure> }.
                        // KWh = key 1 in the BDEW load- and PV-profile output.
                        var amountRecord = new RtRecord { CkRecordId = amountRecordId };
                        amountRecord.SetAttributeValue("Value", AttributeValueTypesDto.Double, amount);
                        amountRecord.SetAttributeValue("Unit", AttributeValueTypesDto.Enum, c.AmountUnit);

                        var entity = new RtEntity
                        {
                            CkTypeId = emCkType,
                            RtId = emRtId,
                            RtWellKnownName = wkn,
                            RtChangedDateTime = DateTime.UtcNow
                        };
                        entity.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, timeRange);
                        entity.SetAttributeValue("Amount", AttributeValueTypesDto.Record, amountRecord);
                        entity.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, obisCode);
                        entity.SetAttributeValue("DataQuality", AttributeValueTypesDto.Enum, c.DataQuality);

                        entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(entity));

                        associations.Add(AssociationUpdateInfo.CreateInsert(
                            origin: new RtEntityId(emCkType, emRtId),
                            target: new RtEntityId(mpCkType, mpRtId),
                            roleId: roleId));
                    }
                }
            }
        }

        nodeContext.Debug(
            $"SimulateEnergyMeasurements: produced {entities.Count} entity candidates and {associations.Count} association candidates " +
            $"across {c.NumDays} day(s) and {c.MeteringPoints.Count} MeteringPoint(s).");

        dataContext.Set(c.EntityUpdatesOutputPath, entities, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        dataContext.Set(c.AssociationUpdatesOutputPath, associations, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        return next(dataContext, nodeContext);
    }
}
