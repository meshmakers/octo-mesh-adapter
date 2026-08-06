using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Unit tests for the camt.053 parser core. The synthetic fixtures use fake IBANs/values only.
/// The real Klaus corpus is verified by <see cref="ParseCamt053_RealCorpus_MatchesHandCounts"/>,
/// which is gated on the CAMT_CORPUS_DIR env var so no real financial data enters version control.
/// </summary>
public class ImportFromCamt053NodeTests
{
    // Raiffeisen / STUZZA-APC dialect: non-ISO namespace, has NtryRef, structured RmtInf, has AcctSvcrRef.
    private const string RaiffeisenXml = """
        <Document xmlns="ISO:camt.053.001.02:APC:STUZZA:payments:003">
          <BkToCstmrStmt>
            <Stmt>
              <LglSeqNb>202600007</LglSeqNb>
              <Acct><Id><IBAN>AT111111111111111111</IBAN></Id><Ccy>EUR</Ccy></Acct>
              <Ntry>
                <NtryRef>NR-1</NtryRef>
                <Amt Ccy="EUR">40</Amt>
                <CdtDbtInd>DBIT</CdtDbtInd>
                <Sts>BOOK</Sts>
                <BookgDt><Dt>2026-07-06</Dt></BookgDt>
                <ValDt><Dt>2026-07-06</Dt></ValDt>
                <AcctSvcrRef>ASR-123</AcctSvcrRef>
                <BkTxCd><Domn><Cd>PMNT</Cd><Fmly><Cd>ICDT</Cd><SubFmlyCd>ESCT</SubFmlyCd></Fmly></Domn><Prtry><Cd>116</Cd><Issr>APC</Issr></Prtry></BkTxCd>
                <NtryDtls><TxDtls>
                  <Refs><TxId>TX-9</TxId></Refs>
                  <RltdPties><Cdtr><Nm>Brauerei X</Nm></Cdtr><CdtrAcct><Id><IBAN>AT222222222222222222</IBAN></Id></CdtrAcct></RltdPties>
                  <RltdAgts><CdtrAgt><FinInstnId><BIC>RVSAAT2S016</BIC></FinInstnId></CdtrAgt></RltdAgts>
                  <RmtInf><Strd><CdtrRefInf><Ref>RE-2026-320</Ref></CdtrRefInf></Strd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    // Hypo / ISO dialect: standard namespace, no AcctSvcrRef (NOTPROVIDED), 5-decimal amount, unstructured RmtInf.
    private const string HypoXml = """
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt>
            <Stmt>
              <LglSeqNb>202600004</LglSeqNb>
              <Acct><Id><IBAN>AT333333333333333333</IBAN></Id><Ccy>EUR</Ccy></Acct>
              <Ntry>
                <Amt Ccy="EUR">550.00000</Amt>
                <CdtDbtInd>CRDT</CdtDbtInd>
                <Sts>BOOK</Sts>
                <BookgDt><Dt>2026-04-15</Dt></BookgDt>
                <ValDt><Dt>2026-04-15</Dt></ValDt>
                <AcctSvcrRef>NOTPROVIDED</AcctSvcrRef>
                <BkTxCd><Domn><Cd>PMNT</Cd><Fmly><Cd>RCDT</Cd><SubFmlyCd>ESCT</SubFmlyCd></Fmly></Domn></BkTxCd>
                <NtryDtls><TxDtls>
                  <Refs><EndToEndId>E2E-7</EndToEndId><TxId>TXH-1</TxId></Refs>
                  <RltdPties><Dbtr><Nm>Gast Meier</Nm></Dbtr><DbtrAcct><Id><IBAN>NL78ABNA1234567890</IBAN></Id></DbtrAcct></RltdPties>
                  <RmtInf><Ustrd>Zimmer 12</Ustrd><Ustrd>Juli</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void ParseCamt053_Raiffeisen_DebitEntryMappedCorrectly()
    {
        var entries = ImportFromCamt053Node.ParseCamt053(RaiffeisenXml);

        var e = Assert.Single(entries);
        Assert.Equal("AT111111111111111111|ASR-123", e["transactionId"]);
        Assert.Equal(-40.0, (double)e["amount"]!); // DBIT => negative
        Assert.Equal("EUR", e["currency"]);
        Assert.Equal(1, e["direction"]); // Debit
        Assert.Equal("2026-07-06T00:00:00Z", e["bookingDate"]);
        Assert.Equal("Brauerei X", e["counterpartName"]);
        Assert.Equal("AT222222222222222222", e["counterpartIban"]);
        Assert.Equal("RVSAAT2S016", e["counterpartBic"]);
        Assert.Equal("RE-2026-320", e["purpose"]); // structured RmtInf
        Assert.Equal("NR-1", e["paymentReference"]); // NtryRef preferred
        Assert.Equal("ESCT", e["bankTransactionCode"]);
    }

    [Fact]
    public void ParseCamt053_Hypo_CreditEntryFallbackKeyAndFiveDecimals()
    {
        var entries = ImportFromCamt053Node.ParseCamt053(HypoXml);

        var e = Assert.Single(entries);
        // No AcctSvcrRef (NOTPROVIDED) => fallback IBAN|LglSeqNb|position
        Assert.Equal("AT333333333333333333|202600004|1", e["transactionId"]);
        Assert.Equal(550.0, (double)e["amount"]!); // CRDT => positive, 5 decimals parsed
        Assert.Equal(0, e["direction"]); // Credit
        Assert.Equal("Gast Meier", e["counterpartName"]); // debtor for credits
        Assert.Equal("NL78ABNA1234567890", e["counterpartIban"]);
        Assert.Equal("Zimmer 12 Juli", e["purpose"]); // unstructured concat
        Assert.Equal("E2E-7", e["endToEndReference"]);
        Assert.Equal("TXH-1", e["paymentReference"]); // no NtryRef => TxId
        Assert.Equal("ESCT", e["bankTransactionCode"]);
    }

    [Fact]
    public void ParseCamt053_NamespaceAgnostic_BothDialectsYieldEntries()
    {
        Assert.NotEmpty(ImportFromCamt053Node.ParseCamt053(RaiffeisenXml));
        Assert.NotEmpty(ImportFromCamt053Node.ParseCamt053(HypoXml));
    }

    [Fact]
    public void ParseCamt053_EmptyStatement_ReturnsNoEntries()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt><Stmt><LglSeqNb>1</LglSeqNb>
                <Acct><Id><IBAN>AT999999999999999999</IBAN></Id><Ccy>EUR</Ccy></Acct>
              </Stmt></BkToCstmrStmt>
            </Document>
            """;
        Assert.Empty(ImportFromCamt053Node.ParseCamt053(xml));
    }

    /// <summary>
    /// Verifies the parser against Klaus's frozen corpus using the hand-counted totals from the
    /// Begleitunterlage. Gated on CAMT_CORPUS_DIR (the local Austausch_Meshmakers_Bankabgleich path);
    /// skipped in CI where the confidential corpus is absent.
    /// </summary>
    [Fact]
    public void ParseCamt053_RealCorpus_MatchesHandCounts()
    {
        var corpus = Environment.GetEnvironmentVariable("CAMT_CORPUS_DIR");
        if (string.IsNullOrWhiteSpace(corpus) || !Directory.Exists(corpus))
        {
            return; // no confidential corpus available — skip
        }

        var muster = Path.Combine(corpus, "02_Muster_Analyse");

        // (firm folder, expected total booking count from the Begleitunterlage)
        var expected = new (string Firm, int Total)[]
        {
            ("GastroAcker", 850),
            ("Tecob", 303),
            ("BierOK", 40),
            ("PureEscape", 78)
        };

        foreach (var (firm, total) in expected)
        {
            var dir = Path.Combine(muster, firm);
            var count = Directory.GetFiles(dir, "*.xml")
                .Sum(f => ImportFromCamt053Node.ParseCamt053(File.ReadAllText(f)).Count);
            Assert.Equal(total, count);
        }

        // --- GastroAcker deep checks against the Begleitunterlage hand-counts ---
        var gastro = Directory.GetFiles(Path.Combine(muster, "GastroAcker"), "*.xml")
            .SelectMany(f => ImportFromCamt053Node.ParseCamt053(File.ReadAllText(f)))
            .ToList();

        // §5: 763/850 have a real AcctSvcrRef (composite key = IBAN|ref, one pipe);
        // 87/850 fall back to IBAN|LglSeqNb|position (two pipes).
        var ids = gastro.Select(e => (string)e["transactionId"]!).ToList();
        Assert.Equal(87, ids.Count(id => id.Count(c => c == '|') == 2));
        Assert.Equal(763, ids.Count(id => id.Count(c => c == '|') == 1));

        // The dedup key MUST be collision-free across the whole account (UniqueNotDeleted index).
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // §8: the July-2026 productive month (statement 202600007) has 104 bookings, 60 debit / 44 credit.
        var julyFile = Directory.GetFiles(Path.Combine(muster, "GastroAcker"), "*202600007*.xml").Single();
        var july = ImportFromCamt053Node.ParseCamt053(File.ReadAllText(julyFile));
        Assert.Equal(104, july.Count);
        Assert.Equal(60, july.Count(e => (int)e["direction"]! == 1)); // Debit
        Assert.Equal(44, july.Count(e => (int)e["direction"]! == 0)); // Credit
    }
}
