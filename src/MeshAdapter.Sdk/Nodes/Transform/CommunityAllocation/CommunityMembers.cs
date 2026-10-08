using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

/// <summary>Whether a community member consumes or produces.</summary>
internal enum CommunityMemberKind
{
    Consumer,
    Producer
}

/// <summary>
/// One metering point of the community as the allocation sees it: kind, partition factor, data
/// source, participation periods and the raw input anchor. Shared by <c>AllocateCommunityEnergy@1</c>,
/// <c>CommunityEnergyBalance@1</c> and <c>PrepareMeterReadings@1</c>, so all three answer
/// "who participates in this slot" with the same rule.
/// </summary>
internal sealed record CommunityMember(
    string RtId,
    CommunityMemberKind Kind,
    string? MeteringPointNumber,
    int PartitionFactor,
    int DataSource,
    List<(DateTime? From, DateTime? To)> Periods)
{
    /// <summary>The raw input anchor (consumption resp. generation register), if one exists.</summary>
    public string? InputAnchorRtId { get; set; }

    /// <summary>True when one participation period covers the whole slot.</summary>
    public bool Participates(AllocationSlot slot)
        => Periods.Any(p => (p.From is null || p.From.Value <= slot.From)
                            && (p.To is null || slot.To <= p.To.Value));
}

/// <summary>A raw slot value read from the archive.</summary>
internal readonly record struct CommunityRawValue(decimal? Value, string Quality);

/// <summary>
/// The runtime-model coordinates of the community (types, roles, attribute names, raw OBIS codes).
/// Every community node carries the same properties with the same defaults; this record is their
/// common, node-independent form.
/// </summary>
internal sealed record CommunityModelSchema
{
    public required string ConsumerCkTypeId { get; init; }
    public required string ProducerCkTypeId { get; init; }
    public required string MeteringPointCkTypeId { get; init; }
    public required string EnergyMeasurementCkTypeId { get; init; }
    public required string ParentAssociationRoleId { get; init; }
    public required string ParticipationPeriodCkTypeId { get; init; }
    public required string ParticipationPeriodAssociationRoleId { get; init; }
    public required string ParticipationTimeRangeAttribute { get; init; }
    public required string PartitionFactorAttribute { get; init; }
    public required int DefaultPartitionFactor { get; init; }
    public required string MeteringPointNumberAttribute { get; init; }
    public required string DataSourceAttribute { get; init; }
    public required int DefaultDataSource { get; init; }
    public required string ObisCodeAttribute { get; init; }
    public required string ConsumerInputObis { get; init; }
    public required string ProducerInputObis { get; init; }

    public static CommunityModelSchema From(AllocateCommunityEnergyNodeConfiguration c) => new()
    {
        ConsumerCkTypeId = c.ConsumerCkTypeId,
        ProducerCkTypeId = c.ProducerCkTypeId,
        MeteringPointCkTypeId = c.MeteringPointCkTypeId,
        EnergyMeasurementCkTypeId = c.EnergyMeasurementCkTypeId,
        ParentAssociationRoleId = c.ParentAssociationRoleId,
        ParticipationPeriodCkTypeId = c.ParticipationPeriodCkTypeId,
        ParticipationPeriodAssociationRoleId = c.ParticipationPeriodAssociationRoleId,
        ParticipationTimeRangeAttribute = c.ParticipationTimeRangeAttribute,
        PartitionFactorAttribute = c.PartitionFactorAttribute,
        DefaultPartitionFactor = c.DefaultPartitionFactor,
        MeteringPointNumberAttribute = c.MeteringPointNumberAttribute,
        DataSourceAttribute = c.DataSourceAttribute,
        DefaultDataSource = c.DefaultDataSource,
        ObisCodeAttribute = c.ObisCodeAttribute,
        ConsumerInputObis = c.ConsumerInputObis,
        ProducerInputObis = c.ProducerInputObis
    };

    public static CommunityModelSchema From(CommunityEnergyBalanceNodeConfiguration c) => new()
    {
        ConsumerCkTypeId = c.ConsumerCkTypeId,
        ProducerCkTypeId = c.ProducerCkTypeId,
        MeteringPointCkTypeId = c.MeteringPointCkTypeId,
        EnergyMeasurementCkTypeId = c.EnergyMeasurementCkTypeId,
        ParentAssociationRoleId = c.ParentAssociationRoleId,
        ParticipationPeriodCkTypeId = c.ParticipationPeriodCkTypeId,
        ParticipationPeriodAssociationRoleId = c.ParticipationPeriodAssociationRoleId,
        ParticipationTimeRangeAttribute = c.ParticipationTimeRangeAttribute,
        PartitionFactorAttribute = c.PartitionFactorAttribute,
        DefaultPartitionFactor = c.DefaultPartitionFactor,
        MeteringPointNumberAttribute = c.MeteringPointNumberAttribute,
        DataSourceAttribute = c.DataSourceAttribute,
        DefaultDataSource = c.DefaultDataSource,
        ObisCodeAttribute = c.ObisCodeAttribute,
        ConsumerInputObis = c.ConsumerInputObis,
        ProducerInputObis = c.ProducerInputObis
    };

    public static CommunityModelSchema From(PrepareMeterReadingsNodeConfiguration c) => new()
    {
        ConsumerCkTypeId = c.ConsumerCkTypeId,
        ProducerCkTypeId = c.ProducerCkTypeId,
        MeteringPointCkTypeId = c.MeteringPointCkTypeId,
        EnergyMeasurementCkTypeId = c.EnergyMeasurementCkTypeId,
        ParentAssociationRoleId = c.ParentAssociationRoleId,
        ParticipationPeriodCkTypeId = c.ParticipationPeriodCkTypeId,
        ParticipationPeriodAssociationRoleId = c.ParticipationPeriodAssociationRoleId,
        ParticipationTimeRangeAttribute = c.ParticipationTimeRangeAttribute,
        PartitionFactorAttribute = c.PartitionFactorAttribute,
        DefaultPartitionFactor = c.DefaultPartitionFactor,
        MeteringPointNumberAttribute = c.MeteringPointNumberAttribute,
        DataSourceAttribute = c.DataSourceAttribute,
        DefaultDataSource = c.DefaultDataSource,
        ObisCodeAttribute = c.ObisCodeAttribute,
        ConsumerInputObis = c.ConsumerInputObis,
        ProducerInputObis = c.ProducerInputObis
    };
}

/// <summary>
/// Loads the community members (consumers and producers with partition factor, data source,
/// participation periods and raw input anchor) inside a session the calling node opened. Extracted
/// from <c>AllocateCommunityEnergy@1</c> unchanged, so every node that reuses it sees exactly the
/// members the allocation sees.
/// </summary>
internal static class CommunityMemberLoader
{
    /// <param name="repository">The tenant repository.</param>
    /// <param name="session">An open session with a started transaction; the caller commits it.</param>
    /// <param name="s">The model coordinates.</param>
    /// <param name="nodeContext">Receives the warnings.</param>
    /// <param name="nodeName">Node name prefixed to every warning.</param>
    /// <param name="loadAnchors">False skips the input anchors (InputAnchorRtId stays null).</param>
    /// <param name="meteringPointNumbers">
    /// Optional filter: only metering points with one of these numbers are loaded (participation
    /// periods are still read as a whole and joined).
    /// </param>
    public static async Task<List<CommunityMember>> LoadAsync(
        ITenantRepository repository,
        IOctoSession session,
        CommunityModelSchema s,
        INodeContext nodeContext,
        string nodeName,
        bool loadAnchors = true,
        IReadOnlyCollection<string>? meteringPointNumbers = null)
    {
        var consumerType = new RtCkId<CkTypeId>(s.ConsumerCkTypeId);
        var producerType = new RtCkId<CkTypeId>(s.ProducerCkTypeId);
        var meteringPointType = new RtCkId<CkTypeId>(s.MeteringPointCkTypeId);
        var periodType = new RtCkId<CkTypeId>(s.ParticipationPeriodCkTypeId);
        var periodRole = new RtCkId<CkAssociationRoleId>(s.ParticipationPeriodAssociationRoleId);
        var emType = new RtCkId<CkTypeId>(s.EnergyMeasurementCkTypeId);
        var parentRole = new RtCkId<CkAssociationRoleId>(s.ParentAssociationRoleId);

        RtEntityQueryOptions PointOptions()
            => meteringPointNumbers is null
                ? RtEntityQueryOptions.Create()
                : RtEntityQueryOptions.Create().FieldIn(s.MeteringPointNumberAttribute, meteringPointNumbers.ToList());

        var consumers = (await repository.GetRtEntitiesByTypeAsync(
            session, consumerType, PointOptions(), 0, int.MaxValue)).Items.ToList();
        var producers = (await repository.GetRtEntitiesByTypeAsync(
            session, producerType, PointOptions(), 0, int.MaxValue)).Items.ToList();
        var periods = (await repository.GetRtEntitiesByTypeAsync(
            session, periodType, RtEntityQueryOptions.Create(), 0, int.MaxValue)).Items.ToList();

        IMultipleOriginResultSet<RtEntity>? periodTargets = null;
        if (periods.Count > 0)
        {
            periodTargets = await repository.GetRtAssociationTargetsAsync(
                session, periods.Select(p => p.RtId).ToArray(), periodType, periodRole, meteringPointType,
                GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
        }

        var anchors = new List<RtEntity>();
        IMultipleOriginResultSet<RtEntity>? anchorTargets = null;
        if (loadAnchors)
        {
            var inputObis = new List<string> { s.ConsumerInputObis, s.ProducerInputObis };
            anchors = (await repository.GetRtEntitiesByTypeAsync(
                    session, emType, RtEntityQueryOptions.Create().FieldIn(s.ObisCodeAttribute, inputObis),
                    0, int.MaxValue)).Items
                .Where(a => inputObis.Contains(a.GetAttributeStringValueOrDefault(s.ObisCodeAttribute) ?? string.Empty,
                    StringComparer.Ordinal))
                .ToList();

            if (anchors.Count > 0)
            {
                // Outbound from each anchor via the parent role to its metering point; same 8-arg call
                // shape as SimulateEnergyMeasurements@1 and GetAssociationTargets@1.
                anchorTargets = await repository.GetRtAssociationTargetsAsync(
                    session, anchors.Select(a => a.RtId).ToArray(), emType, parentRole, meteringPointType,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
            }
        }

        // ---- participation periods by metering point
        var periodById = periods.ToDictionary(p => p.RtId.ToString(), StringComparer.Ordinal);
        var periodsByPoint = new Dictionary<string, List<(DateTime?, DateTime?)>>(StringComparer.Ordinal);
        var periodsWithoutRange = 0;
        foreach (var (origin, targets) in Pairs(periodTargets))
        {
            if (!periodById.TryGetValue(origin, out var period))
            {
                continue;
            }

            if (period.GetAttributeValueOrDefault(s.ParticipationTimeRangeAttribute) is not RtRecord range)
            {
                periodsWithoutRange++;
                continue;
            }

            var from = CommunityValues.AsUtc(range.GetAttributeValueOrDefault("From"));
            var to = CommunityValues.AsUtc(range.GetAttributeValueOrDefault("To"));
            foreach (var target in targets)
            {
                var key = target.RtId.ToString();
                if (!periodsByPoint.TryGetValue(key, out var list))
                {
                    periodsByPoint[key] = list = [];
                }

                list.Add((from, to));
            }
        }

        if (periodsWithoutRange > 0)
        {
            nodeContext.Warning(
                $"{nodeName}: {periodsWithoutRange} participation period(s) without '{s.ParticipationTimeRangeAttribute}' ignored.");
        }

        // ---- members
        var members = new Dictionary<string, CommunityMember>(StringComparer.Ordinal);
        var clampedFactors = 0;
        foreach (var (entities, kind) in new[]
                 {
                     (consumers, CommunityMemberKind.Consumer), (producers, CommunityMemberKind.Producer)
                 })
        {
            foreach (var e in entities)
            {
                var rtId = e.RtId.ToString();
                if (members.ContainsKey(rtId))
                {
                    continue;
                }

                var pf = CommunityValues.AsInt(e.GetAttributeValueOrDefault(s.PartitionFactorAttribute))
                         ?? s.DefaultPartitionFactor;
                if (pf is < 0 or > 100)
                {
                    clampedFactors++;
                    pf = Math.Clamp(pf, 0, 100);
                }

                var dataSource = CommunityValues.AsDataSource(e.GetAttributeValueOrDefault(s.DataSourceAttribute))
                                 ?? s.DefaultDataSource;
                members[rtId] = new CommunityMember(rtId, kind,
                    e.GetAttributeStringValueOrDefault(s.MeteringPointNumberAttribute),
                    pf, dataSource,
                    periodsByPoint.TryGetValue(rtId, out var list) ? list : []);
            }
        }

        if (clampedFactors > 0)
        {
            nodeContext.Warning($"{nodeName}: {clampedFactors} partition factor(s) outside 0..100 were clamped.");
        }

        // ---- input anchors by metering point and OBIS code
        var anchorById = anchors.ToDictionary(a => a.RtId.ToString(), StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var (origin, targets) in Pairs(anchorTargets).OrderBy(p => p.Origin, StringComparer.Ordinal))
        {
            if (!anchorById.TryGetValue(origin, out var anchor))
            {
                continue;
            }

            var obis = anchor.GetAttributeStringValueOrDefault(s.ObisCodeAttribute);
            foreach (var target in targets)
            {
                if (!members.TryGetValue(target.RtId.ToString(), out var member))
                {
                    continue;
                }

                var expected = member.Kind == CommunityMemberKind.Consumer ? s.ConsumerInputObis : s.ProducerInputObis;
                if (!string.Equals(obis, expected, StringComparison.Ordinal))
                {
                    continue;
                }

                // Origins are visited in rtId order, so the first anchor wins deterministically.
                if (member.InputAnchorRtId is null)
                {
                    member.InputAnchorRtId = origin;
                }
                else
                {
                    duplicates++;
                }
            }
        }

        if (duplicates > 0)
        {
            nodeContext.Warning(
                $"{nodeName}: {duplicates} additional input anchor(s) for the same metering point and register ignored.");
        }

        return members.Values.ToList();
    }

    private static IEnumerable<(string Origin, List<RtEntity> Targets)> Pairs(IMultipleOriginResultSet<RtEntity>? set)
    {
        if (set is null)
        {
            yield break;
        }

        foreach (var kvp in set)
        {
            yield return (kvp.Key.RtId.ToString(), kvp.Value.Items.ToList());
        }
    }
}

/// <summary>
/// The per-slot step shared by the final allocation and the provisional balance: builds the
/// participating members of one slot with their raw values and hands them to the allocator. Keeping
/// this one method is what makes a balance slot with complete inputs equal to the final run.
/// </summary>
internal static class CommunitySlotAllocation
{
    /// <summary>
    /// Allocates one slot over <paramref name="ordered"/> (callers pass them in rtId ordinal order).
    /// Returns null when nobody participates in the slot.
    /// </summary>
    public static SlotAllocation? Allocate(
        CommunityAllocator allocator,
        AllocationSlot slot,
        IReadOnlyList<CommunityMember> ordered,
        IReadOnlyDictionary<(string, DateTime), CommunityRawValue> values,
        string missingQuality,
        out List<AllocationMember> consumers,
        out List<AllocationMember> producers)
    {
        consumers = [];
        producers = [];
        foreach (var m in ordered)
        {
            if (!m.Participates(slot))
            {
                continue;
            }

            CommunityRawValue raw = default;
            var found = m.InputAnchorRtId is not null && values.TryGetValue((m.InputAnchorRtId, slot.From), out raw);
            var member = new AllocationMember(m.RtId, m.PartitionFactor, found ? raw.Value : null,
                found ? raw.Quality : missingQuality);
            (m.Kind == CommunityMemberKind.Consumer ? consumers : producers).Add(member);
        }

        if (consumers.Count == 0 && producers.Count == 0)
        {
            return null;
        }

        return allocator.Allocate(consumers, producers);
    }
}
