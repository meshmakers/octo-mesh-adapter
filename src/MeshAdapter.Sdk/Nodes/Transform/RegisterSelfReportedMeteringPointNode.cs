using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Registers a self-reported metering point (AB#5636, MTR-01): customer, operating facility, metering
/// point with <c>MeteringDataSource = SelfReported</c>, raw measurement anchor and participation
/// period, the same entity chain the demo generator builds. Idempotent by metering point number and
/// direction; the request is validated completely before anything is written, and all writes happen
/// in one transaction, so a rejected or failed request never leaves half a chain behind.
/// </summary>
/// <remarks>
/// Writes go straight through the tenant repository in the node's session rather than as update
/// infos for <c>ApplyChanges@2</c>: the conflict rules (direction, data source) need the lookup and
/// the write in one transaction, and the customer has to be inserted on its own so the engine's
/// auto-increment assigns its <c>CustomerNumber</c> (the engine's auto-increment step only runs for a
/// batch in which every entity type carries an auto-increment attribute). The request is never
/// logged.
/// </remarks>
[NodeConfiguration(typeof(RegisterSelfReportedMeteringPointNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class RegisterSelfReportedMeteringPointNode(
    NodeDelegate next,
    IMeshEtlContext etlContext) : IPipelineNode
{
    private const string NodeName = "RegisterSelfReportedMeteringPoint";

    internal const string Validation = "VALIDATION";
    internal const string NumberReserved = "NUMBER_RESERVED";
    internal const string DirectionConflict = "DIRECTION_CONFLICT";
    internal const string NotSelfReported = "NOT_SELF_REPORTED";
    internal const string CityNotFound = "CITY_NOT_FOUND";

    // CK attribute names of the entity chain (EnergyCommunity 4.9, Basic.Energy, Basic); they are the
    // model, not configuration, and are the ones create-demo-community.yaml writes.
    private const string NameAttribute = "Name";
    private const string StateAttribute = "State";
    private const string CarrierTypeAttribute = "CarrierType";
    private const string EnergyConsumptionAttribute = "EnergyConsumption";
    private const string LoadProfileAttribute = "LoadProfile";
    private const string ProductionTypeAttribute = "ProductionType";
    private const string CapacityAttribute = "EnergyProductionCapacity";
    private const int StateActive = 1;
    private const int CarrierElectricity = 1;

    private static readonly Regex NumberPattern = new("^AT[0-9A-Z]{31}$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, int> LoadProfiles =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["H0"] = 0, ["G0"] = 1, ["L0"] = 2, ["HeatPump"] = 3, ["Ev"] = 4
        };

    private static readonly IReadOnlyDictionary<string, int> ProductionTypes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unknown"] = 0, ["Solar"] = 1, ["HEP"] = 2, ["Wind"] = 3, ["Other"] = 4, ["Biomass"] = 5, ["CHP"] = 6
        };

    private static readonly IReadOnlyDictionary<string, int> LegalEntityTypes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["NaturalPerson"] = 0, ["LegalPerson"] = 1, ["Company"] = 2, ["LocalAuthority"] = 3
        };

    /// <summary>Clock for the default participation start ("today"); replaced by tests.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    internal sealed record Request(
        string Number,
        CommunityMemberKind Kind,
        string Direction,
        string? DisplayName,
        int? Factor,
        DateOnly? ParticipationFrom,
        int? LoadProfile,
        double? AnnualConsumptionKWh,
        int? ProductionType,
        double? CapacityKWp,
        string? CustomerName,
        int? CustomerLegalEntityType,
        int? Zipcode);

    /// <summary>The city a facility is parented to and whose zip code and name its address carries.</summary>
    private sealed record City(OctoObjectId RtId, int Zipcode, string? Name);

    private sealed class Rejection(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<RegisterSelfReportedMeteringPointNodeConfiguration>();
        var timeZone = CommunityValues.ResolveTimeZone(c.TimeZone, nodeContext, NodeName);

        // ---------------------------------------------------------------- validate (no I/O)
        Request request;
        try
        {
            request = Validate(dataContext.Get<JsonNode>(c.RequestPath), c);
        }
        catch (Rejection rejection)
        {
            await WriteAsync(dataContext, nodeContext, c, Error(rejection));
            return;
        }

        // ---------------------------------------------------------------- lookup + write, one transaction
        // AB#5028 / AB#5127 — scoped by default (config-selected identity; the pipelines set
        // ServiceAccount): reads and writes the tenant's own customers, facilities, metering points,
        // anchors and participation periods, so the writes are stamped and subject to data permissions
        // like every other business-data node. A narrower identity that cannot see an existing metering
        // point lets the unique number index refuse the duplicate rather than writing one.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        JsonObject result;
        try
        {
            result = await RegisterAsync(session, c, request, timeZone, nodeContext);
            await session.CommitTransactionAsync();
        }
        catch (Rejection rejection)
        {
            await session.AbortTransactionAsync();
            await WriteAsync(dataContext, nodeContext, c, Error(rejection));
            return;
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        await WriteAsync(dataContext, nodeContext, c, result);
    }

    // ==================================================================== registration

    private async Task<JsonObject> RegisterAsync(IOctoSession session,
        RegisterSelfReportedMeteringPointNodeConfiguration c, Request r, TimeZoneInfo timeZone,
        INodeContext nodeContext)
    {
        var repository = etlContext.TenantRepository;
        var ownType = new RtCkId<CkTypeId>(r.Kind == CommunityMemberKind.Consumer ? c.ConsumerCkTypeId : c.ProducerCkTypeId);
        var otherType = new RtCkId<CkTypeId>(r.Kind == CommunityMemberKind.Consumer ? c.ProducerCkTypeId : c.ConsumerCkTypeId);
        var byNumber = RtEntityQueryOptions.Create().FieldIn(c.MeteringPointNumberAttribute, new List<string> { r.Number });

        var other = (await repository.GetRtEntitiesByTypeAsync(session, otherType, byNumber, 0, int.MaxValue)).Items
            .FirstOrDefault(e => e.GetAttributeStringValueOrDefault(c.MeteringPointNumberAttribute) == r.Number);
        if (other is not null)
        {
            throw new Rejection(DirectionConflict,
                $"Metering point {r.Number} is already registered with the other direction.");
        }

        var existing = (await repository.GetRtEntitiesByTypeAsync(session, ownType, byNumber, 0, int.MaxValue)).Items
            .Where(e => e.GetAttributeStringValueOrDefault(c.MeteringPointNumberAttribute) == r.Number)
            .OrderBy(e => e.RtId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

        if (existing is not null)
        {
            var dataSource = CommunityValues.AsDataSource(existing.GetAttributeValueOrDefault(c.DataSourceAttribute))
                             ?? c.DefaultDataSource;
            if (dataSource != c.SelfReportedDataSource)
            {
                throw new Rejection(NotSelfReported,
                    $"Metering point {r.Number} belongs to a simulated or EDA metering point.");
            }

            return await UpdateAsync(session, c, r, ownType, existing, timeZone, nodeContext);
        }

        return await CreateAsync(session, c, r, ownType, timeZone, nodeContext);
    }

    private async Task<JsonObject> CreateAsync(IOctoSession session,
        RegisterSelfReportedMeteringPointNodeConfiguration c, Request r, RtCkId<CkTypeId> mpType,
        TimeZoneInfo timeZone, INodeContext nodeContext)
    {
        var repository = etlContext.TenantRepository;
        var customerType = new RtCkId<CkTypeId>(c.CustomerCkTypeId);
        var facilityType = new RtCkId<CkTypeId>(c.FacilityCkTypeId);
        var parentRole = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);

        // ---- city of the facility (AB#6014): resolved before the first write, so CITY_NOT_FOUND
        // aborts a transaction that has not written anything yet.
        var city = await ResolveCityAsync(session, c, r, nodeContext);

        // ---- customer: reuse by its natural key, otherwise insert it on its own (auto-increment)
        var customerWkn = r.CustomerName is null
            ? c.DefaultCustomerWellKnownName
            : c.CustomerWellKnownNamePrefix + CustomerKey(r.CustomerName);
        var customer = (await repository.GetRtEntitiesByTypeAsync(session, customerType,
                RtEntityQueryOptions.Create().FieldIn(nameof(RtEntity.RtWellKnownName), new List<string> { customerWkn }),
                0, int.MaxValue)).Items
            .Where(e => e.RtWellKnownName == customerWkn)
            .OrderBy(e => e.RtId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        var customerId = customer?.RtId;
        if (customerId is null)
        {
            var legalEntity = r.CustomerLegalEntityType ?? LegalEntityTypes[c.DefaultLegalEntityType];
            var newCustomer = NewEntity(customerType, customerWkn);
            newCustomer.SetAttributeValue("Contact", AttributeValueTypesDto.Record,
                Contact(legalEntity, r.CustomerName ?? c.DefaultCustomerName));
            newCustomer.SetAttributeValue("BillingCycle", AttributeValueTypesDto.Enum, c.CustomerBillingCycle);
            newCustomer.SetAttributeValue(StateAttribute, AttributeValueTypesDto.Enum, StateActive);
            newCustomer.SetAttributeValue("TaxProcedureCreditNote", AttributeValueTypesDto.Enum,
                c.CustomerTaxProcedureCreditNote);
            await ApplyAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(newCustomer)], [], "the customer");
            customerId = newCustomer.RtId;
        }

        // ---- facility, metering point, raw anchor, participation period
        var name = r.DisplayName ?? r.Number;
        var from = ParticipationStartUtc(r, timeZone);
        var factor = r.Factor ?? c.DefaultParticipationFactor;

        var facility = NewEntity(facilityType, null);
        facility.SetAttributeValue(NameAttribute, AttributeValueTypesDto.String, name);
        facility.SetAttributeValue(StateAttribute, AttributeValueTypesDto.Enum, StateActive);
        facility.SetAttributeValue("FacilityType", AttributeValueTypesDto.Enum, c.FacilityType);
        facility.SetAttributeValue("Address", AttributeValueTypesDto.Record, Address(c, null, city));

        var mp = NewEntity(mpType, null);
        mp.SetAttributeValue(NameAttribute, AttributeValueTypesDto.String, name);
        mp.SetAttributeValue(c.MeteringPointNumberAttribute, AttributeValueTypesDto.String, r.Number);
        mp.SetAttributeValue(StateAttribute, AttributeValueTypesDto.Enum, StateActive);
        mp.SetAttributeValue(CarrierTypeAttribute, AttributeValueTypesDto.Enum, CarrierElectricity);
        mp.SetAttributeValue(c.PartitionFactorAttribute, AttributeValueTypesDto.Int, factor);
        mp.SetAttributeValue(c.DataSourceAttribute, AttributeValueTypesDto.Enum, c.SelfReportedDataSource);
        ApplySizes(mp, r);
        if (r.Kind == CommunityMemberKind.Producer && r.ProductionType is null)
        {
            // ProductionType is mandatory on Basic.Energy/Producer.
            mp.SetAttributeValue(ProductionTypeAttribute, AttributeValueTypesDto.Enum, ProductionTypes["Unknown"]);
        }

        var obis = RawObis(c, r.Kind);
        var anchor = NewAnchor(c, mp.RtId, obis, from);
        var period = NewPeriod(c, mp.RtId, from);

        var mpId = new RtEntityId(mpType, mp.RtId);
        var anchorId = new RtEntityId(new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId), anchor.RtId);
        var periodId = new RtEntityId(new RtCkId<CkTypeId>(c.ParticipationPeriodCkTypeId), period.RtId);
        var facilityId = new RtEntityId(facilityType, facility.RtId);
        var cityId = new RtEntityId(new RtCkId<CkTypeId>(c.CityCkTypeId), city.RtId);
        await ApplyAsync(session,
            [
                EntityUpdateInfo<RtEntity>.CreateInsert(facility),
                EntityUpdateInfo<RtEntity>.CreateInsert(mp),
                EntityUpdateInfo<RtEntity>.CreateInsert(anchor),
                EntityUpdateInfo<RtEntity>.CreateInsert(period)
            ],
            [
                AssociationUpdateInfo.CreateInsert(new RtEntityId(customerType, customerId.Value), facilityId,
                    new RtCkId<CkAssociationRoleId>(c.CustomerFacilityAssociationRoleId)),
                AssociationUpdateInfo.CreateInsert(mpId, facilityId, parentRole),
                AssociationUpdateInfo.CreateInsert(facilityId, cityId, parentRole),
                AssociationUpdateInfo.CreateInsert(anchorId, mpId, parentRole),
                AssociationUpdateInfo.CreateInsert(periodId, mpId,
                    new RtCkId<CkAssociationRoleId>(c.ParticipationPeriodAssociationRoleId))
            ],
            "the metering point chain");

        nodeContext.Debug($"{NodeName}: created metering point {mp.RtId} with anchor and participation period.");
        return Ok(true, "Metering point registered.", r, mp.RtId.ToString(),
            [(obis, anchor.RtId.ToString())], LocalDate(from, timeZone), factor);
    }

    private async Task<JsonObject> UpdateAsync(IOctoSession session,
        RegisterSelfReportedMeteringPointNodeConfiguration c, Request r, RtCkId<CkTypeId> mpType, RtEntity existing,
        TimeZoneInfo timeZone, INodeContext nodeContext)
    {
        var repository = etlContext.TenantRepository;
        var emType = new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId);
        var periodType = new RtCkId<CkTypeId>(c.ParticipationPeriodCkTypeId);
        var parentRole = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);
        var periodRole = new RtCkId<CkAssociationRoleId>(c.ParticipationPeriodAssociationRoleId);
        var mpId = new RtEntityId(mpType, existing.RtId);
        var obis = RawObis(c, r.Kind);

        var anchors = Targets(await repository.GetRtAssociationTargetsAsync(session, [existing.RtId], mpType,
                parentRole, emType, GraphDirections.Inbound, null, RtEntityQueryOptions.Create()))
            .Where(a => a.GetAttributeStringValueOrDefault(c.ObisCodeAttribute) == obis)
            .OrderBy(a => a.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
        var periods = Targets(await repository.GetRtAssociationTargetsAsync(session, [existing.RtId], mpType,
                periodRole, periodType, GraphDirections.Inbound, null, RtEntityQueryOptions.Create()))
            .Select(p => (p.GetAttributeValueOrDefault(c.ParticipationTimeRangeAttribute) as RtRecord)
                ?.GetAttributeValueOrDefault("From"))
            .Select(CommunityValues.AsUtc)
            .OfType<DateTime>()
            .OrderBy(d => d)
            .ToList();

        var entities = new List<IEntityUpdateInfo<RtEntity>>();
        var associations = new List<AssociationUpdateInfo>();

        // ---- name, factor, sizes: only what the request names; the participation start stays.
        var update = new RtEntity(mpType, existing.RtId);
        if (r.DisplayName is not null)
        {
            update.SetAttributeValue(NameAttribute, AttributeValueTypesDto.String, r.DisplayName);
        }

        if (r.Factor is not null)
        {
            update.SetAttributeValue(c.PartitionFactorAttribute, AttributeValueTypesDto.Int, r.Factor.Value);
        }

        ApplySizes(update, r);
        if (update.Attributes.Count > 0)
        {
            entities.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(mpId, update));
        }

        // ---- repair a missing raw anchor or participation period (never a second one)
        DateTime start;
        if (periods.Count > 0)
        {
            start = periods[0];
        }
        else
        {
            start = ParticipationStartUtc(r, timeZone);
            var period = NewPeriod(c, existing.RtId, start);
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(period));
            associations.Add(AssociationUpdateInfo.CreateInsert(new RtEntityId(periodType, period.RtId), mpId, periodRole));
        }

        string anchorRtId;
        if (anchors.Count > 0)
        {
            anchorRtId = anchors[0].RtId.ToString();
        }
        else
        {
            var anchor = NewAnchor(c, existing.RtId, obis, start);
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(anchor));
            associations.Add(AssociationUpdateInfo.CreateInsert(new RtEntityId(emType, anchor.RtId), mpId, parentRole));
            anchorRtId = anchor.RtId.ToString();
        }

        await RepairCityParentAsync(session, c, r, mpType, existing, entities, associations, nodeContext);

        if (entities.Count > 0 || associations.Count > 0)
        {
            await ApplyAsync(session, entities, associations, "the metering point update");
        }

        var factor = r.Factor
                     ?? CommunityValues.AsInt(existing.GetAttributeValueOrDefault(c.PartitionFactorAttribute))
                     ?? c.DefaultParticipationFactor;
        nodeContext.Debug($"{NodeName}: metering point {existing.RtId} already registered; updated.");
        return Ok(false, "Metering point already registered; updated.", r, existing.RtId.ToString(),
            [(obis, anchorRtId)], LocalDate(start, timeZone), factor);
    }

    // ==================================================================== city parent (AB#6014)

    /// <summary>
    /// The city of the facility: the one whose zip code equals the request's <c>zipcode</c>, else the
    /// configured <c>DefaultZipcode</c>. Several cities share a zip code in the Locations.Austria
    /// directory (and a tenant may hold a second tree); the lowest runtime id wins, so the choice is
    /// the same on every call. No match is <c>CITY_NOT_FOUND</c>: an explicit zip code is never
    /// silently replaced by the default, and a missing default city means the directory is missing.
    /// </summary>
    private async Task<City> ResolveCityAsync(IOctoSession session,
        RegisterSelfReportedMeteringPointNodeConfiguration c, Request r, INodeContext nodeContext)
    {
        var zipcode = r.Zipcode ?? c.DefaultZipcode;
        if (zipcode <= 0)
        {
            throw new PipelineNodeExecutionException(
                $"{NodeName}: DefaultZipcode '{c.DefaultZipcode}' must be a positive postal code, e.g. 5020.");
        }

        var matches = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session,
                new RtCkId<CkTypeId>(c.CityCkTypeId),
                RtEntityQueryOptions.Create().FieldEquals(c.CityZipcodeAttribute, zipcode), 0, int.MaxValue)).Items
            .Where(e => CommunityValues.AsInt(e.GetAttributeValueOrDefault(c.CityZipcodeAttribute)) == zipcode)
            .OrderBy(e => e.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
        {
            throw r.Zipcode is not null
                ? new Rejection(CityNotFound, $"No city with zip code {zipcode} is known in this tenant.")
                : new Rejection(CityNotFound,
                    $"No city with the default zip code {zipcode} is known in this tenant, so the operating facility "
                    + "cannot get its city. The tenant needs the Locations.Austria city directory; or pass the "
                    + "zipcode of a city it has.");
        }

        if (matches.Count > 1)
        {
            nodeContext.Debug($"{NodeName}: {matches.Count} cities share zip code {zipcode}; linked the one with the lowest rtId.");
        }

        var city = matches[0];
        return new City(city.RtId, zipcode, city.GetAttributeStringValueOrDefault(c.CityNameAttribute));
    }

    /// <summary>
    /// Re-registration of a metering point whose facility has no parent (registered before the city
    /// rule): links the city and fills the address placeholders. A facility with any parent is left
    /// alone, so an operator's choice of city is never overwritten.
    /// </summary>
    private async Task RepairCityParentAsync(IOctoSession session,
        RegisterSelfReportedMeteringPointNodeConfiguration c, Request r, RtCkId<CkTypeId> mpType, RtEntity existing,
        List<IEntityUpdateInfo<RtEntity>> entities, List<AssociationUpdateInfo> associations, INodeContext nodeContext)
    {
        var repository = etlContext.TenantRepository;
        var facilityType = new RtCkId<CkTypeId>(c.FacilityCkTypeId);
        var parentRole = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);

        var facility = Targets(await repository.GetRtAssociationTargetsAsync(session, [existing.RtId], mpType,
                parentRole, facilityType, GraphDirections.Outbound, null, RtEntityQueryOptions.Create()))
            .OrderBy(f => f.RtId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (facility is null)
        {
            nodeContext.Debug($"{NodeName}: metering point {existing.RtId} has no operating facility; no city to repair.");
            return;
        }

        var parents = Targets(await repository.GetRtAssociationTargetsAsync(session, [facility.RtId], facilityType,
            parentRole, new RtCkId<CkTypeId>(c.FacilityParentCkTypeId), GraphDirections.Outbound, null,
            RtEntityQueryOptions.Create()));
        if (parents.Any())
        {
            return;
        }

        var city = await ResolveCityAsync(session, c, r, nodeContext);
        var facilityId = new RtEntityId(facilityType, facility.RtId);
        associations.Add(AssociationUpdateInfo.CreateInsert(facilityId,
            new RtEntityId(new RtCkId<CkTypeId>(c.CityCkTypeId), city.RtId), parentRole));

        var update = new RtEntity(facilityType, facility.RtId);
        update.SetAttributeValue("Address", AttributeValueTypesDto.Record,
            Address(c, facility.GetAttributeValueOrDefault("Address") as RtRecord, city));
        entities.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(facilityId, update));
        nodeContext.Debug($"{NodeName}: facility {facility.RtId} had no parent; linked city {city.RtId}.");
    }

    private async Task ApplyAsync(IOctoSession session, List<IEntityUpdateInfo<RtEntity>> entities,
        List<AssociationUpdateInfo> associations, string what)
    {
        var operationResult = new OperationResult();
        await etlContext.TenantRepository.ApplyChangesAsync(session, entities, associations, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                $"{NodeName}: writing {what} failed ({operationResult.GetMessages()}).");
        }
    }

    // ==================================================================== entity builders

    private static RtEntity NewEntity(RtCkId<CkTypeId> type, string? wellKnownName)
    {
        var entity = new RtEntity(type, OctoObjectId.GenerateNewId());
        if (wellKnownName is not null)
        {
            entity.RtWellKnownName = wellKnownName;
        }

        return entity;
    }

    private static RtEntity NewAnchor(RegisterSelfReportedMeteringPointNodeConfiguration c, OctoObjectId mpRtId,
        string obis, DateTime from)
    {
        // Same well-known name rule as SaveTimeRangeSeriesInArchive@1 ({MeteringPointRtId}_{MeterCode}),
        // so the first reported value lands on this anchor instead of creating a second one.
        var anchor = NewEntity(new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId), $"{mpRtId}_{obis}");
        anchor.SetAttributeValue(c.ObisCodeAttribute, AttributeValueTypesDto.String, obis);
        anchor.SetAttributeValue("DataQuality", AttributeValueTypesDto.Enum, 1);
        var amount = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/Amount") };
        amount.SetAttributeValue("Value", AttributeValueTypesDto.Double, 0.0);
        amount.SetAttributeValue("Unit", AttributeValueTypesDto.Enum, 1);
        anchor.SetAttributeValue("Amount", AttributeValueTypesDto.Record, amount);
        anchor.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, TimeRange(from, from.AddMinutes(15)));
        return anchor;
    }

    private static RtEntity NewPeriod(RegisterSelfReportedMeteringPointNodeConfiguration c, OctoObjectId mpRtId,
        DateTime from)
    {
        var period = NewEntity(new RtCkId<CkTypeId>(c.ParticipationPeriodCkTypeId), $"{mpRtId}_participation");
        var to = DateTime.SpecifyKind(c.ParticipationTo, DateTimeKind.Utc);
        period.SetAttributeValue(c.ParticipationTimeRangeAttribute, AttributeValueTypesDto.Record, TimeRange(from, to));
        return period;
    }

    private static RtRecord TimeRange(DateTime from, DateTime to)
    {
        var range = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/TimeRange") };
        range.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
        range.SetAttributeValue("To", AttributeValueTypesDto.DateTime, to);
        return range;
    }

    private static RtRecord Contact(int legalEntityType, string name)
    {
        var contact = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/Contact") };
        contact.SetAttributeValue("LegalEntityType", AttributeValueTypesDto.Enum, legalEntityType);
        contact.SetAttributeValue(legalEntityType == LegalEntityTypes["NaturalPerson"] ? "LastName" : "CompanyName",
            AttributeValueTypesDto.String, name);
        return contact;
    }

    /// <summary>
    /// The facility address: zip code and city name from the linked city; street and national code
    /// kept from an existing address, else the configured placeholders (the request carries none).
    /// </summary>
    private static RtRecord Address(RegisterSelfReportedMeteringPointNodeConfiguration c, RtRecord? existing,
        City city)
    {
        var address = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/Address") };
        address.SetAttributeValue("Street", AttributeValueTypesDto.String,
            KeptString(existing, "Street") ?? c.FacilityStreet);
        address.SetAttributeValue("Zipcode", AttributeValueTypesDto.Int, city.Zipcode);
        address.SetAttributeValue("CityTown", AttributeValueTypesDto.String,
            city.Name is { Length: > 0 } name ? name : KeptString(existing, "CityTown") ?? "-");
        address.SetAttributeValue("NationalCode", AttributeValueTypesDto.String,
            KeptString(existing, "NationalCode") ?? c.FacilityNationalCode);
        return address;
    }

    private static string? KeptString(RtRecord? record, string attribute)
        => record?.GetAttributeValueOrDefault(attribute) is string { Length: > 0 } s && s != "-" ? s : null;

    private static void ApplySizes(RtEntity entity, Request r)
    {
        if (r.LoadProfile is { } profile)
        {
            entity.SetAttributeValue(LoadProfileAttribute, AttributeValueTypesDto.Enum, profile);
        }

        if (r.AnnualConsumptionKWh is { } consumption)
        {
            entity.SetAttributeValue(EnergyConsumptionAttribute, AttributeValueTypesDto.Double, consumption);
        }

        if (r.ProductionType is { } productionType)
        {
            entity.SetAttributeValue(ProductionTypeAttribute, AttributeValueTypesDto.Enum, productionType);
        }

        if (r.CapacityKWp is { } capacity)
        {
            entity.SetAttributeValue(CapacityAttribute, AttributeValueTypesDto.Double, capacity);
        }
    }

    private static IEnumerable<RtEntity> Targets(IMultipleOriginResultSet<RtEntity>? set)
        => set is null ? [] : set.SelectMany(kvp => kvp.Value.Items);

    private static string RawObis(RegisterSelfReportedMeteringPointNodeConfiguration c, CommunityMemberKind kind)
        => kind == CommunityMemberKind.Consumer ? c.ConsumerInputObis : c.ProducerInputObis;

    /// <summary>Local midnight of the participation start (default: today in the time zone), in UTC.</summary>
    private DateTime ParticipationStartUtc(Request r, TimeZoneInfo timeZone)
    {
        var day = r.ParticipationFrom
                  ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Clock.GetUtcNow().UtcDateTime, timeZone));
        var planner = new AllocationDayPlanner(timeZone, 0, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        return planner.BuildDay(day).StartUtc;
    }

    private static string LocalDate(DateTime utc, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Natural key of a named customer: a readable slug plus a short hash of the normalised name, so
    /// the same name always finds the same customer and two names that slug alike stay apart.
    /// </summary>
    internal static string CustomerKey(string name)
    {
        var normalised = name.Trim().ToLowerInvariant();
        var slug = Regex.Replace(normalised, "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..8];
        return slug.Length == 0 ? hash : $"{slug}-{hash}";
    }

    // ==================================================================== validation

    internal static Request Validate(JsonNode? node, RegisterSelfReportedMeteringPointNodeConfiguration c)
    {
        if (node is not JsonObject body)
        {
            throw new Rejection(Validation, "The request must be a JSON object.");
        }

        var number = OptionalString(body, "meteringPointNumber")
                     ?? throw new Rejection(Validation, "meteringPointNumber is required.");
        if (!NumberPattern.IsMatch(number))
        {
            throw new Rejection(Validation,
                "meteringPointNumber must have 33 characters, start with AT and contain only digits and capital letters.");
        }

        if (!number.StartsWith(c.ReservedNumberPrefix, StringComparison.Ordinal))
        {
            throw new Rejection(NumberReserved,
                $"Only metering point numbers starting with {c.ReservedNumberPrefix} can be registered.");
        }

        var direction = OptionalString(body, "direction")?.ToLowerInvariant();
        CommunityMemberKind kind = direction switch
        {
            "consumption" => CommunityMemberKind.Consumer,
            "production" => CommunityMemberKind.Producer,
            _ => throw new Rejection(Validation, "direction must be 'consumption' or 'production'.")
        };

        var displayName = OptionalString(body, "displayName");
        if (displayName is { Length: > 200 })
        {
            throw new Rejection(Validation, "displayName must not be longer than 200 characters.");
        }

        int? factor = null;
        if (Present(body, "participationFactor"))
        {
            factor = ReadNumber(body, "participationFactor") is { } f && f % 1 == 0 && f is >= 1 and <= 100
                ? (int)f
                : throw new Rejection(Validation, "participationFactor must be a whole number from 1 to 100.");
        }

        DateOnly? from = null;
        if (Present(body, "participationFrom"))
        {
            from = OptionalString(body, "participationFrom") is { } s
                   && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                       out var day)
                ? day
                : throw new Rejection(Validation, "participationFrom must be a date in the form yyyy-MM-dd.");
        }

        var loadProfile = ReadEnum(body, "loadProfile", LoadProfiles, kind, CommunityMemberKind.Consumer);
        var consumption = NonNegative(body, "annualConsumptionKwh", kind, CommunityMemberKind.Consumer);
        var productionType = ReadEnum(body, "productionType", ProductionTypes, kind, CommunityMemberKind.Producer);
        var capacity = NonNegative(body, "capacityKwp", kind, CommunityMemberKind.Producer);

        string? customerName = null;
        int? legalEntity = null;
        if (Present(body, "customer"))
        {
            if (body["customer"] is not JsonObject customer)
            {
                throw new Rejection(Validation, "customer must be an object with name and legalEntityType.");
            }

            customerName = OptionalString(customer, "name")
                           ?? throw new Rejection(Validation, "customer.name is required when customer is given.");
            if (customerName.Length > 200)
            {
                throw new Rejection(Validation, "customer.name must not be longer than 200 characters.");
            }

            if (Present(customer, "legalEntityType"))
            {
                legalEntity = OptionalString(customer, "legalEntityType") is { } t
                              && LegalEntityTypes.TryGetValue(t, out var key)
                    ? key
                    : throw new Rejection(Validation,
                        $"customer.legalEntityType must be one of {string.Join(", ", LegalEntityTypes.Keys)}.");
            }
        }

        int? zipcode = null;
        if (Present(body, "zipcode"))
        {
            zipcode = body["zipcode"] is JsonValue z ? ParseZipcode(z) : null;
            if (zipcode is null)
            {
                throw new Rejection(Validation, "zipcode must be a postal code: a whole number from 1 to 99999 or a string of digits.");
            }
        }

        if (!LegalEntityTypes.ContainsKey(c.DefaultLegalEntityType))
        {
            throw new PipelineNodeExecutionException(
                $"{NodeName}: DefaultLegalEntityType '{c.DefaultLegalEntityType}' is not a legal entity type.");
        }

        return new Request(number, kind, direction!, displayName, factor, from, loadProfile, consumption,
            productionType, capacity, customerName, legalEntity, zipcode);
    }

    private static int? ParseZipcode(JsonValue value)
    {
        int parsed;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                if (CommunityValues.ParseJsonNumber(value) is not { } d || d % 1 != 0 || d is < 1 or > 99999)
                {
                    return null;
                }

                return (int)d;
            case JsonValueKind.String:
                var s = value.GetValue<string>().Trim();
                return s.Length is > 0 and <= 5 && s.All(char.IsAsciiDigit)
                       && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    private static bool Present(JsonObject body, string name)
        => body.TryGetPropertyValue(name, out var value) && value is not null;

    private static string? OptionalString(JsonObject body, string name)
    {
        if (!Present(body, name))
        {
            return null;
        }

        return body[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                                         && v.GetValue<string>().Trim() is { Length: > 0 } s
            ? s
            : throw new Rejection(Validation, $"{name} must be a non-empty string.");
    }

    private static decimal? ReadNumber(JsonObject body, string name)
        => body[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? CommunityValues.ParseJsonNumber(v)
            : null;

    private static int? ReadEnum(JsonObject body, string name, IReadOnlyDictionary<string, int> values,
        CommunityMemberKind kind, CommunityMemberKind validFor)
    {
        if (!Present(body, name))
        {
            return null;
        }

        if (kind != validFor)
        {
            throw new Rejection(Validation, $"{name} is only allowed for direction {Direction(validFor)}.");
        }

        return OptionalString(body, name) is { } s && values.TryGetValue(s, out var key)
            ? key
            : throw new Rejection(Validation, $"{name} must be one of {string.Join(", ", values.Keys)}.");
    }

    private static double? NonNegative(JsonObject body, string name, CommunityMemberKind kind,
        CommunityMemberKind validFor)
    {
        if (!Present(body, name))
        {
            return null;
        }

        if (kind != validFor)
        {
            throw new Rejection(Validation, $"{name} is only allowed for direction {Direction(validFor)}.");
        }

        return ReadNumber(body, name) is { } d && d >= 0
            ? (double)d
            : throw new Rejection(Validation, $"{name} must be a number not below 0.");
    }

    private static string Direction(CommunityMemberKind kind)
        => kind == CommunityMemberKind.Consumer ? "consumption" : "production";

    // ==================================================================== result

    private static JsonObject Ok(bool created, string message, Request r, string rtId,
        IEnumerable<(string Obis, string RtId)> anchors, string from, int factor)
    {
        var anchorArray = new JsonArray();
        foreach (var (obis, anchorRtId) in anchors)
        {
            anchorArray.Add(new JsonObject { ["obisCode"] = obis, ["rtId"] = anchorRtId });
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["message"] = message,
            ["created"] = created,
            ["meteringPoint"] = new JsonObject
            {
                ["rtId"] = rtId,
                ["meteringPointNumber"] = r.Number,
                ["direction"] = r.Direction,
                ["anchors"] = anchorArray,
                ["participation"] = new JsonObject { ["from"] = from, ["factor"] = factor }
            }
        };
    }

    private static JsonObject Error(Rejection rejection) => new()
    {
        ["status"] = "error",
        ["code"] = rejection.Code,
        ["message"] = rejection.Message,
        ["created"] = false
    };

    private async Task WriteAsync(IDataContext dataContext, INodeContext nodeContext,
        RegisterSelfReportedMeteringPointNodeConfiguration c, JsonObject result)
    {
        dataContext.Set<JsonNode>(c.TargetPath, result, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);
        await next(dataContext, nodeContext);
    }
}
