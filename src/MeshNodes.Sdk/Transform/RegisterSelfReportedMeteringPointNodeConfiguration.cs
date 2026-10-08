using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// RegisterSelfReportedMeteringPoint node configuration (AB#5636, requirement MTR-01). Registers a
/// metering point whose values the participant reports itself: customer, operating facility,
/// metering point with <c>MeteringDataSource = SelfReported</c>, raw measurement anchor and
/// participation period, idempotently and in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// The request at <see cref="RequestPath"/> carries <c>meteringPointNumber</c> (33 characters,
/// starting with <c>AT</c> and with <see cref="ReservedNumberPrefix"/>), <c>direction</c>
/// (<c>consumption</c> or <c>production</c>), optional <c>displayName</c>,
/// <c>participationFactor</c> (1 to 100, default 100), <c>participationFrom</c> (a date in
/// <see cref="TimeZone"/>, default today), <c>loadProfile</c> and <c>annualConsumptionKwh</c>
/// (consumption only), <c>productionType</c> and <c>capacityKwp</c> (production only) and
/// <c>customer</c> (<c>name</c>, <c>legalEntityType</c>). Everything is validated before anything is
/// written; a validation failure is a result with <c>status: error</c> and a <c>code</c>
/// (<c>VALIDATION</c>, <c>NUMBER_RESERVED</c>, <c>DIRECTION_CONFLICT</c>, <c>NOT_SELF_REPORTED</c>),
/// never an exception.
/// </para>
/// <para>
/// A second request for the same number and direction returns <c>created: false</c> with the same
/// runtime ids and updates the name, the partition factor and the sizes that the request names; the
/// participation start is never changed. The result written to <see cref="TargetPath"/> is
/// <c>status</c>, <c>code</c> (on error), <c>message</c>, <c>created</c> and <c>meteringPoint</c>
/// (<c>rtId</c>, <c>meteringPointNumber</c>, <c>direction</c>, <c>anchors</c> with
/// <c>obisCode</c> and <c>rtId</c>, <c>participation</c> with <c>from</c> and <c>factor</c>).
/// </para>
/// <para>
/// The customer number is not written by the node: the customer is inserted on its own, so the
/// engine assigns <c>CustomerNumber</c> from the tenant's auto-increment counter, as the app's create
/// flow does.
/// </para>
/// </remarks>
[NodeName("RegisterSelfReportedMeteringPoint", 1)]
public record RegisterSelfReportedMeteringPointNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    // ------------------------------------------------------------------ Paths

    /// <summary>JSONPath to the request object. Default <c>$.body</c>.</summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string RequestPath { get; init; } = "$.body";

    /// <summary>JSONPath where the result object is written.</summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string TargetPath { get; init; }

    // ------------------------------------------------------------------ Rules

    /// <summary>
    /// Prefix every self-reported metering point number must carry; any other number is rejected with
    /// <c>NUMBER_RESERVED</c>, so the simulation and the EDA number ranges stay untouched. Default
    /// <c>AT009998</c>.
    /// </summary>
    [PropertyGroup("Rules", 0)]
    public string ReservedNumberPrefix { get; init; } = "AT009998";

    /// <summary>Time zone of <c>participationFrom</c> and of its default "today". Default <c>Europe/Vienna</c>.</summary>
    [PropertyGroup("Rules", 1)]
    public string TimeZone { get; init; } = "Europe/Vienna";

    /// <summary>Partition factor of a new metering point when the request names none. Default 100.</summary>
    [PropertyGroup("Rules", 2)]
    public int DefaultParticipationFactor { get; init; } = 100;

    /// <summary>End of the participation period created for a new metering point (UTC). Default 2099-12-31T23:59:59Z.</summary>
    [PropertyGroup("Rules", 3)]
    public DateTime ParticipationTo { get; init; } = new(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    // ------------------------------------------------------------------ Schema

    /// <summary>CkTypeId of the consumer metering points. Default <c>EnergyCommunity/Consumer</c>.</summary>
    [PropertyGroup("Schema", 0, "ckTypeSelector")]
    public string ConsumerCkTypeId { get; init; } = "EnergyCommunity/Consumer";

    /// <summary>CkTypeId of the producer metering points. Default <c>EnergyCommunity/Producer</c>.</summary>
    [PropertyGroup("Schema", 1, "ckTypeSelector")]
    public string ProducerCkTypeId { get; init; } = "EnergyCommunity/Producer";

    /// <summary>CkTypeId of the customers. Default <c>EnergyCommunity/Customer</c>.</summary>
    [PropertyGroup("Schema", 2, "ckTypeSelector")]
    public string CustomerCkTypeId { get; init; } = "EnergyCommunity/Customer";

    /// <summary>CkTypeId of the operating facilities. Default <c>Basic.Energy/OperatingFacility</c>.</summary>
    [PropertyGroup("Schema", 3, "ckTypeSelector")]
    public string FacilityCkTypeId { get; init; } = "Basic.Energy/OperatingFacility";

    /// <summary>CkTypeId of the measurement anchors. Default <c>Basic.Energy/EnergyMeasurement</c>.</summary>
    [PropertyGroup("Schema", 4, "ckTypeSelector")]
    public string EnergyMeasurementCkTypeId { get; init; } = "Basic.Energy/EnergyMeasurement";

    /// <summary>CkTypeId of the participation periods. Default <c>EnergyCommunity/ParticipationPeriod</c>.</summary>
    [PropertyGroup("Schema", 5, "ckTypeSelector")]
    public string ParticipationPeriodCkTypeId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>Association role customer to facility. Default <c>EnergyCommunity/AssociatedFacilities</c>.</summary>
    [PropertyGroup("Schema", 6)]
    public string CustomerFacilityAssociationRoleId { get; init; } = "EnergyCommunity/AssociatedFacilities";

    /// <summary>
    /// Association role metering point to facility and anchor to metering point. Default
    /// <c>System/ParentChild</c>.
    /// </summary>
    [PropertyGroup("Schema", 7)]
    public string ParentAssociationRoleId { get; init; } = "System/ParentChild";

    /// <summary>Association role participation period to metering point. Default <c>EnergyCommunity/ParticipationPeriod</c>.</summary>
    [PropertyGroup("Schema", 8)]
    public string ParticipationPeriodAssociationRoleId { get; init; } = "EnergyCommunity/ParticipationPeriod";

    /// <summary>Record attribute on the participation period with <c>From</c> and <c>To</c>. Default <c>TimeRange</c>.</summary>
    [PropertyGroup("Schema", 9)]
    public string ParticipationTimeRangeAttribute { get; init; } = "TimeRange";

    /// <summary>String attribute with the metering point number. Default <c>MeteringPointNumber</c>.</summary>
    [PropertyGroup("Schema", 10)]
    public string MeteringPointNumberAttribute { get; init; } = "MeteringPointNumber";

    /// <summary>Integer attribute with the partition factor in percent. Default <c>PartitionFactor</c>.</summary>
    [PropertyGroup("Schema", 11)]
    public string PartitionFactorAttribute { get; init; } = "PartitionFactor";

    /// <summary>Enum attribute naming where the values come from. Default <c>MeteringDataSource</c>.</summary>
    [PropertyGroup("Schema", 12)]
    public string DataSourceAttribute { get; init; } = "MeteringDataSource";

    /// <summary>Data source assumed when the attribute is missing. Default 0 (Eda).</summary>
    [PropertyGroup("Schema", 13)]
    public int DefaultDataSource { get; init; } = 0;

    /// <summary>Data source key written for and required of a self-reported metering point. Default 2.</summary>
    [PropertyGroup("Schema", 14)]
    public int SelfReportedDataSource { get; init; } = 2;

    /// <summary>String attribute on the measurement anchor carrying its OBIS code. Default <c>ObisCode</c>.</summary>
    [PropertyGroup("Schema", 15)]
    public string ObisCodeAttribute { get; init; } = "ObisCode";

    /// <summary>Raw register anchored for a consumer. Default <c>1-1:1.9.0 G.01</c>.</summary>
    [PropertyGroup("Schema", 16)]
    public string ConsumerInputObis { get; init; } = "1-1:1.9.0 G.01";

    /// <summary>Raw register anchored for a producer. Default <c>1-1:2.9.0 G.01</c>.</summary>
    [PropertyGroup("Schema", 17)]
    public string ProducerInputObis { get; init; } = "1-1:2.9.0 G.01";

    // ------------------------------------------------------------------ Customer

    /// <summary>
    /// Well-known name of the default customer used when the request names none; one per tenant,
    /// found again by this name. Default <c>self-reported-default-customer</c>.
    /// </summary>
    [PropertyGroup("Customer", 0)]
    public string DefaultCustomerWellKnownName { get; init; } = "self-reported-default-customer";

    /// <summary>Company name of the default customer. Default <c>Self-reported metering points</c>.</summary>
    [PropertyGroup("Customer", 1)]
    public string DefaultCustomerName { get; init; } = "Self-reported metering points";

    /// <summary>
    /// Prefix of the well-known name of a named customer; the rest is derived from the name, so the
    /// same name always finds the same customer. Default <c>self-reported-customer-</c>.
    /// </summary>
    [PropertyGroup("Customer", 2)]
    public string CustomerWellKnownNamePrefix { get; init; } = "self-reported-customer-";

    /// <summary>
    /// Legal entity type of a customer whose request names none (NaturalPerson, LegalPerson, Company,
    /// LocalAuthority). Default <c>Company</c>.
    /// </summary>
    [PropertyGroup("Customer", 3)]
    public string DefaultLegalEntityType { get; init; } = "Company";

    /// <summary>BillingCycle key of a new customer. Default 1 (Quarterly, the CK default).</summary>
    [PropertyGroup("Customer", 4)]
    public int CustomerBillingCycle { get; init; } = 1;

    /// <summary>TaxProcedureCreditNote key of a new customer. Default 2 (NoTaxProcedure).</summary>
    [PropertyGroup("Customer", 5)]
    public int CustomerTaxProcedureCreditNote { get; init; } = 2;

    // ------------------------------------------------------------------ Facility

    /// <summary>FacilityType key of a new facility. Default 0 (Unknown).</summary>
    [PropertyGroup("Facility", 0)]
    public int FacilityType { get; init; } = 0;

    /// <summary>Street of the mandatory facility address (the request carries none). Default <c>-</c>.</summary>
    [PropertyGroup("Facility", 1)]
    public string FacilityStreet { get; init; } = "-";

    /// <summary>Zip code of the mandatory facility address. Default 0.</summary>
    [PropertyGroup("Facility", 2)]
    public int FacilityZipcode { get; init; } = 0;

    /// <summary>City of the mandatory facility address. Default <c>-</c>.</summary>
    [PropertyGroup("Facility", 3)]
    public string FacilityCityTown { get; init; } = "-";

    /// <summary>National code of the mandatory facility address. Default <c>AT</c>.</summary>
    [PropertyGroup("Facility", 4)]
    public string FacilityNationalCode { get; init; } = "AT";
}
