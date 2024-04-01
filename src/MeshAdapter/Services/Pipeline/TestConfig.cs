using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshNodes.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

internal static class TestConfig
{
     public static PipelineConfigurationRoot Test1 => new()
        {
            Transformations = new List<NodeConfiguration>
            {
                // Retrieve from distributed event hub message
                new RetrieveFromMessageNodeConfiguration
                {
                    Description = "Retrieve from distributed event hub message"
                },
                // Only sample (useless)
                new GetRtEntitiesByIdNodeConfiguration
                {
                    Description = "Retrieve RtEntity if exists",
                    CkTypeId = "IndustryEnergy/EnergyMeter",
                    TargetPropertyName = "EnergyMeterResult",
                    RtIds = new List<OctoObjectId>
                    {
                        new("65dc6d24cc529cdc46c84fcc")
                    }
                },
                // Add to mongo db rt model
                new CreateUpdateInfoNodeConfiguration
                {
                    Description = "update",
                    CkTypeId = "IndustryEnergy/EnergyMeter",
                    RtId = new OctoObjectId("65dc6d24cc529cdc46c84fcc"),
                    TargetPropertyName = "_UpdateItems",
                    AttributeUpdates = new List<AttributeUpdateConfiguration>
                    {
                        new() {
                            AttributeName = "Voltage",
                            AttributeValueType = AttributeValueTypesDto.Double,
                            ValuePath = "$.Sinus5"
                        }
                    }
                },
                new ApplyChangesNodeConfiguration
                {
                    TargetPropertyName = "_UpdateItems"
                },
                // Add to time series
                new EnrichWithMongoDataConfiguration
                {
                    Path = "$._UpdateItems",
                    Description = "update",
                    CkTypeId = "IndustryEnergy/EnergyMeter",
                    RtId = new OctoObjectId("65dc6d24cc529cdc46c84fcc"),
                    TargetPropertyName = "_UpdateItems",
                    AttributeUpdates = new List<AttributeUpdateConfiguration>
                    {
                        new() {
                            AttributeName = "Power",
                            AttributeValueType = AttributeValueTypesDto.Double,
                            ValuePath = "$.Power"
                        },
                        new()
                        {
                            AttributeName = "Ampere",
                            AttributeValueType = AttributeValueTypesDto.Double,
                            ValuePath = "$.Ampere"
                        }
                    }
                }
            }
        };
}