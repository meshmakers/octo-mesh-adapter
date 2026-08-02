using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class BuildSepaCreditTransferNodeTests : NodeTestBase
{
    private static readonly XNamespace Ns = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    private static JsonObject Item(string name, string iban, string? bic, string amount,
        string? remittance = null, string? creditorReference = null, string? purposeCode = null,
        string? endToEndId = null, string? instructionId = null)
    {
        var o = new JsonObject
        {
            ["recipientName"] = name,
            ["recipientIban"] = iban,
            ["amount"] = amount,
        };
        if (bic != null) o["recipientBic"] = bic;
        if (remittance != null) o["remittance"] = remittance;
        if (creditorReference != null) o["creditorReference"] = creditorReference;
        if (purposeCode != null) o["purposeCode"] = purposeCode;
        if (endToEndId != null) o["endToEndId"] = endToEndId;
        if (instructionId != null) o["instructionId"] = instructionId;
        return o;
    }

    private static BuildSepaCreditTransferNodeConfiguration BaseConfig() => new()
    {
        Path = "$.payments",
        TargetPath = "$.sepaXml",
        ContentLengthTargetPath = "$.sepaLen",
        TransactionCountTargetPath = "$.sepaCount",
        ControlSumTargetPath = "$.sepaCtrl",
        DebtorName = "meshmakers GmbH",
        DebtorIban = "AT702040400043129071",
        DebtorBic = "SBGSAT2SXXX",
        DebtorCountry = "AT",
        DebtorAddressLines = "Firmianstrasse 31a; 5020 Salzburg",
        MessageId = "MM-TEST-0001",
        PaymentInformationId = "MM-TEST-PMT-0001",
        RequestedExecutionDate = "2026-08-04",
        CreationDateTime = "2026-08-02T10:15:00",
        InitiatingPartyOrgId = "AT702040400043129071",
    };

    private static string CapturedString(IDataContext dataContext, string targetPath) =>
        (string)Fake.GetCalls(dataContext)
            .First(c => c.Method.Name == "Set" && (string?)c.Arguments[0] == targetPath)
            .Arguments[1]!;

    private static string DecodeXml(IDataContext dataContext, string targetPath) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(CapturedString(dataContext, targetPath)));

    /// <summary>Validates an instance document against the official PSA Austrian XSD.</summary>
    private static IReadOnlyList<string> ValidateAgainstAustrianXsd(string xml)
    {
        var xsdPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Sepa",
            "ISO.pain.001.001.03.austrian.004.Korrigendum.xsd");
        Assert.True(File.Exists(xsdPath), $"XSD not found at {xsdPath}");

        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        using (var xsdReader = XmlReader.Create(xsdPath))
        {
            schemas.Add(Ns.NamespaceName, xsdReader);
        }

        schemas.Compile();

        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity} L{e.Exception?.LineNumber}: {e.Message}");
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read())
        {
        }

        return errors;
    }

    [Fact]
    public async Task ProcessObjectAsync_BuildsValidBatch_FromOpenInvoices()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>
        {
            Item("A1 Telekom Austria AG", "AT611904300234573201", "BKAUATWWXXX", "149.90",
                remittance: "Rechnung 2026-0812 Kundennr 4455667"),
            Item("Hetzner Online GmbH", "DE89370400440532013000", "COBADEFFXXX", "87.50",
                creditorReference: "RF18000000000539007547034", endToEndId: "R0099887"),
        });

        var node = new BuildSepaCreditTransferNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);

        var xml = DecodeXml(dataContext, config.TargetPath);
        var doc = XDocument.Parse(xml);

        // header sums
        var grpHdr = doc.Descendants(Ns + "GrpHdr").Single();
        Assert.Equal("2", grpHdr.Element(Ns + "NbOfTxs")!.Value);
        Assert.Equal("237.40", grpHdr.Element(Ns + "CtrlSum")!.Value);
        Assert.Equal(2, doc.Descendants(Ns + "CdtTrfTxInf").Count());

        // structured RF reference carries the mandatory SCOR type element (Austrian rule)
        var strd = doc.Descendants(Ns + "Strd").Single();
        Assert.Equal("SCOR", strd.Descendants(Ns + "Cd").Single().Value);
        Assert.Equal("RF18000000000539007547034", strd.Descendants(Ns + "Ref").Single().Value);

        // side outputs
        Assert.Equal("237.40", CapturedString(dataContext, "$.sepaCtrl"));

        // schema-valid against the official PSA Austrian XSD
        var errors = ValidateAgainstAustrianXsd(xml);
        Assert.True(errors.Count == 0, "XSD errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task ProcessObjectAsync_ReproducesBmdReferenceBatch_AndIsXsdValid()
    {
        var config = BaseConfig();
        config.MessageId = "06240920KBMDLOHN249A39B20ABB4C2BA96";
        config.PaymentInformationId = "06240920KBMDLOHN";
        config.RequestedExecutionDate = "2026-06-24";
        config.CreationDateTime = "2026-06-24T09:20:27";

        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>
        {
            Item("Finanzamt Österreich", "AT950100000005554915", "BUNDATWWXXX", "10105.15",
                remittance: "2606+686540L+295977DB+27998DZ", purposeCode: "TAXS",
                instructionId: "2406261", endToEndId: "913108486"),
            Item("Österreichische Gesundheitskasse", "AT063500000000058016", "RVSAAT2SXXX", "30612.31",
                remittance: "110001173089 SV 6/2026", instructionId: "2406262"),
            Item("Magistrat Salzburg", "AT832040400000010009", "SBGSAT2S", "2399.81",
                creditorReference: "440392710666", instructionId: "2406263", endToEndId: "440392710666"),
        });

        var node = new BuildSepaCreditTransferNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var xml = DecodeXml(dataContext, config.TargetPath);
        var doc = XDocument.Parse(xml);
        Assert.Equal("3", doc.Descendants(Ns + "GrpHdr").Single().Element(Ns + "NbOfTxs")!.Value);
        Assert.Equal("43117.27", doc.Descendants(Ns + "GrpHdr").Single().Element(Ns + "CtrlSum")!.Value);

        var errors = ValidateAgainstAustrianXsd(xml);
        Assert.True(errors.Count == 0, "XSD errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task ProcessObjectAsync_RejectsInvalidRows()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>
        {
            // bad IBAN, amount <= 0, both remittance AND structured reference
            Item("Bad Payee", "DE00WRONG", "COBADEFFXXX", "-5.00",
                remittance: "x", creditorReference: "RF1"),
        });

        var node = new BuildSepaCreditTransferNode(next);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_RejectsRemittanceOver140Chars()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>
        {
            Item("Vendor", "AT611904300234573201", "BKAUATWWXXX", "10.00",
                remittance: new string('x', 141)),
        });

        var node = new BuildSepaCreditTransferNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyInput_Throws()
    {
        var config = BaseConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>());

        var node = new BuildSepaCreditTransferNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingDebtor_Throws()
    {
        var config = BaseConfig();
        config.DebtorIban = null;
        config.DebtorIbanPath = null;
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<JsonNode>("$.payments")).Returns(new List<JsonNode?>
        {
            Item("Vendor", "AT611904300234573201", "BKAUATWWXXX", "10.00", remittance: "x"),
        });

        var node = new BuildSepaCreditTransferNode(next);
        await Assert.ThrowsAnyAsync<Exception>(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}
