using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.SimulationNodes.Generators;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Backfills historical archive datapoints for the <c>EnergyMeasurement</c> entities that already
/// exist in the rt-model. Does not create entities and does not emit associations.
/// </summary>
[NodeConfiguration(typeof(SimulateEnergyMeasurementsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SimulateEnergyMeasurementsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private const int SlotsPerDay = 96;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SimulateEnergyMeasurementsNodeConfiguration>();

        if (c.NumDays <= 0)
        {
            throw new InvalidOperationException(
                $"SimulateEnergyMeasurements: NumDays must be > 0 (got {c.NumDays}).");
        }

        var emCkType = new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId);
        var producerCkType = new RtCkId<CkTypeId>(c.ProducerCkTypeId);
        var timeRangeRecordId = new RtCkId<CkRecordId>(c.TimeRangeCkRecordId);
        var amountRecordId = new RtCkId<CkRecordId>(c.AmountCkRecordId);
        var roleId = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);
        var startUtc = DateTime.SpecifyKind(c.StartDate, DateTimeKind.Utc);
        var slotDuration = TimeSpan.FromMinutes(15);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // 1. Load ALL existing EnergyMeasurement entities of the configured type.
        var emResult = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, emCkType, RtEntityQueryOptions.Create(), 0, int.MaxValue);
        var existingEms = emResult.Items.ToList();

        if (existingEms.Count == 0)
        {
            await session.CommitTransactionAsync();
            nodeContext.Warning(
                $"SimulateEnergyMeasurements: no existing EnergyMeasurement entities of type '{c.EnergyMeasurementCkTypeId}' found — nothing to backfill.");
            dataContext.Set(c.EntityUpdatesOutputPath, new List<EntityUpdateInfo<RtEntity>>(),
                DocumentModes.Extend, ValueKinds.Simple, TargetValueWriteModes.Overwrite);
            await next(dataContext, nodeContext);
            return;
        }

        // 2. Resolve each EM's parent MeteringPoint type via the ParentChild association role.
        //    Outbound from the EM origin returns the parent MeteringPoint as the association target.
        var originRtIds = existingEms.Select(e => e.RtId).ToArray();
        // Type-agnostic target query: navigate outbound from each EM origin via the ParentChild
        // role to its parent MeteringPoint, whatever its concrete type.
        // Same overload + direction shape as GetAssociationTargetsNode: multi-origin, target type
        // left unconstrained (null) so any parent MeteringPoint type is returned as the target.
        var targetCkTypeId = (RtCkId<CkTypeId>)null!;
        var parentResult = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
            session, originRtIds, emCkType, roleId, targetCkTypeId, GraphDirections.Outbound, null,
            RtEntityQueryOptions.Create(), null, null);

        await session.CommitTransactionAsync();

        // Map EM rtId → parent MeteringPoint CkTypeId (first parent wins).
        var parentTypeByEmRtId = new Dictionary<OctoObjectId, RtCkId<CkTypeId>>();
        foreach (var kvp in parentResult)
        {
            var parent = kvp.Value.Items.FirstOrDefault();
            if (parent?.CkTypeId != null)
            {
                parentTypeByEmRtId[kvp.Key.RtId] = parent.CkTypeId;
            }
        }

        // 3.-4. Generate datapoints for each existing EM.
        var datapoints = new List<EntityUpdateInfo<RtEntity>>();
        var producerProfileFullName = producerCkType.FullName;
        var emsWithoutParent = 0;

        foreach (var em in existingEms)
        {
            var obisCode = em.GetAttributeStringValueOrDefault("ObisCode", string.Empty);

            var profileIsPv = parentTypeByEmRtId.TryGetValue(em.RtId, out var parentType)
                              && string.Equals(parentType.FullName, producerProfileFullName,
                                  StringComparison.Ordinal);

            if (!parentTypeByEmRtId.ContainsKey(em.RtId))
            {
                emsWithoutParent++;
            }

            // Deterministic per-EM magnitude variation (±~20%) derived from a stable hash of the
            // EM rtId so each channel's curve differs while staying reproducible.
            var magnitude = profileIsPv ? c.ProducerPeakKWp : c.ConsumerDailyKWh;
            magnitude *= MagnitudeFactor(em.RtId);

            for (var day = 0; day < c.NumDays; day++)
            {
                var dayStart = startUtc.AddDays(day);
                var dayOfYear = dayStart.DayOfYear;

                for (var slot = 0; slot < SlotsPerDay; slot++)
                {
                    var from = dayStart.AddTicks(slotDuration.Ticks * slot);
                    var to = from.Add(slotDuration);

                    var amount = profileIsPv
                        ? EnergyProfiles.PvProfileSlot(magnitude, dayOfYear, slot)
                        : EnergyProfiles.LoadProfileSlot(c.LoadProfileSubKey, magnitude, slot);

                    var timeRange = new RtRecord { CkRecordId = timeRangeRecordId };
                    timeRange.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
                    timeRange.SetAttributeValue("To", AttributeValueTypesDto.DateTime, to);

                    // Basic/Amount is a record carrying { Value: Double, Unit: Enum<UnitOfMeasure> }.
                    var amountRecord = new RtRecord { CkRecordId = amountRecordId };
                    amountRecord.SetAttributeValue("Value", AttributeValueTypesDto.Double, amount);
                    amountRecord.SetAttributeValue("Unit", AttributeValueTypesDto.Enum, c.AmountUnit);

                    var entity = new RtEntity
                    {
                        CkTypeId = emCkType,
                        RtId = em.RtId,
                        RtWellKnownName = em.RtWellKnownName,
                        RtChangedDateTime = DateTime.UtcNow
                    };
                    entity.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, timeRange);
                    entity.SetAttributeValue("Amount", AttributeValueTypesDto.Record, amountRecord);
                    entity.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, obisCode);
                    entity.SetAttributeValue("DataQuality", AttributeValueTypesDto.Enum, c.DataQuality);

                    datapoints.Add(EntityUpdateInfo<RtEntity>.CreateInsert(entity));
                }
            }
        }

        if (emsWithoutParent > 0)
        {
            nodeContext.Warning(
                $"SimulateEnergyMeasurements: {emsWithoutParent} EnergyMeasurement(s) had no parent MeteringPoint via role '{c.ParentAssociationRoleId}' — defaulted to the load profile.");
        }

        nodeContext.Debug(
            $"SimulateEnergyMeasurements: produced {datapoints.Count} archive datapoint(s) " +
            $"for {existingEms.Count} existing EnergyMeasurement(s) across {c.NumDays} day(s).");

        // 5. Output ONLY the datapoints. No entity creation, no association updates.
        dataContext.Set(c.EntityUpdatesOutputPath, datapoints, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Stable, deterministic per-EM magnitude factor in roughly [0.8, 1.2] derived from a hash of
    /// the EM rtId, so different channels produce different curves while staying reproducible.
    /// </summary>
    private static double MagnitudeFactor(OctoObjectId rtId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rtId.ToString()));
        var fraction = (bytes[0] | (bytes[1] << 8)) / (double)ushort.MaxValue; // [0, 1]
        return 0.8 + (0.4 * fraction);
    }
}
