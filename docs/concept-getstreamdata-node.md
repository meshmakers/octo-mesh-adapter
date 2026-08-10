# Concept: `GetStreamData@1` / `AggregateStreamData@1` — Extract-Nodes für Stream Data Archives

> Status: **AB#4726 (§5) und AB#4728 (§6) implementiert. §7 offen.**
> Azure DevOps: **AB#4722** (Issue) mit den Tasks **AB#4726** (Basisfunktionalität, erledigt),
> **AB#4728** (Gap-Detection, erledigt) und **AB#4752** (Spalten-Aggregation, §7).
> **AB#4727 (Downsampling) ist bewusst nicht Teil dieses Features** — siehe §2.
> Epic: AB#3364 „Stream Data v2: deep system integration on an archive-based foundation".
>
> **Ablage der geteilten Helfer:** abweichend vom ursprünglichen Entwurf liegen sie flach in
> `src/MeshAdapter.Sdk/Nodes/` statt in einem Unterordner `Nodes/StreamData/` — konsistent mit den
> vorhandenen geteilten Helfern (`FieldFilterExtensions.cs`, `SortOrderExtensions.cs`, `Query.cs`)
> und ohne Namenskollision zwischen einem Namespace `…Nodes.StreamData` und dem importierten
> `Meshmakers.Octo.Runtime.Contracts.StreamData`. §6 und §7 folgen derselben Ablage.

Dieses Dokument beschreibt den Entwurf so, dass die drei Tasks **getrennt voneinander** umgesetzt
werden können. §5–§7 sind je ein Task; §4 beschreibt die gemeinsamen Grundlagen.

**Ergebnis sind zwei Nodes:**

| Node | Zweck |
|---|---|
| `GetStreamData@1` | Rohdatenzeilen aus einem Archiv lesen (§5), optional mit Lückenreport (§6) |
| `AggregateStreamData@1` | Kennzahlen über einen Zeitraum bilden — Summe, Min, Max, Mittelwert, Anzahl (§7) |

---

## 1. Ausgangslage und Ziel

`GetQueryById@1` führt heute nur **persistierte** Query-Entitäten aus (`RtSimpleSdQuery`,
`RtAggregationSdQuery`, `RtGroupingAggregationSdQuery`, `RtDownsamplingSdQuery`). Wer ad-hoc aus
einem Archiv lesen will, muss also erst eine Query-Entität anlegen.

Die beiden neuen Nodes sind das **konfigurationsgetriebene Gegenstück**: Archiv, Spalten, Zeitraum
und Filter stehen direkt am Node. Anforderung aus AB#4722:

> It should be possible to query data from a stream data archive with the possibility to filter for
> wellKnownName. It should also be possible to filter for time range (from, to, limit) and define a
> sorting.

Der Fokus liegt auf **exakten Daten**: die Nodes liefern die tatsächlich gespeicherten Werte, nicht
eine verdichtete Näherung. Dazu passend zwei Ergänzungen:

* **Lückenerkennung** für **TimeRange-Archive**: feststellen, ob der abgefragte Zeitraum lückenlos
  abgedeckt ist (z. B. ob bei 15-Minuten-Fenstern wirklich jedes Intervall geliefert wurde), und die
  Lücken zurückgeben.
* **Spalten-Aggregation**: einen einzelnen Kennwert über den gesamten Zeitraum bilden. Leitbeispiel:
  aus dem Archiv `energy-measurements` die **Summe der verbrauchten Energie** eines Monats sowie die
  **maximale Datenqualität** in diesem Zeitraum ermitteln — auf Wunsch nur dann, wenn der Zeitraum
  lückenlos ist.

---

## 2. Getroffene Entscheidungen

| Frage | Entscheidung |
|---|---|
| Lesen vs. Aggregieren | **Zwei Nodes.** `GetStreamData@1` liefert Zeilen, `AggregateStreamData@1` Kennzahlen. Begründung in §2.1. |
| Lückenanalyse in `GetStreamData@1` | Als **Zusatzausgabe**: Daten → `targetPath`, Lücken → `gapsTargetPath` (Muster wie `GetFileSystemContent@1` mit `fileNameTargetPath`). Intern zwei getrennte Queries. |
| Lückenanalyse in `AggregateStreamData@1` | Als **Guard**: Bool `requireGapFree`. `false` (Default) aggregiert immer; `true` bricht mit einer Pipeline-Exception ab, wenn im Zeitraum Lücken sind. Überlappungen lassen den Guard **nicht** fehlschlagen — siehe §2.3. |
| `wellKnownName` | **Nur Zeilenfilter** auf `rtWellKnownName`. Das Archiv wird ausschließlich per `archiveRtId` adressiert — konsistent zu `GetQueryById@1` und `SaveStreamDataInArchive@1`. |
| Gap-Algorithmus | **Coverage/Union** — alle `[window_start, window_end)` im Zeitraum vereinigen und von `[from, to)` abziehen; der Rest sind die Lücken. Braucht kein `Period`, erkennt auch Teilabdeckung und variable Fensterlängen. |
| Gap-Scope | **Pro Quell-Entität** (Gruppierung nach `rtId`). Ein fehlendes Viertelstundenintervall bei einem Zähler darf nicht dadurch verdeckt werden, dass ein anderer Zähler geliefert hat. |

### 2.1 Warum zwei Nodes

* **Die Property-Semantik kollidiert.** `Columns` ist beim Lesen eine Liste von Attributpfaden, beim
  Aggregieren eine Liste aus Pfad **und** Funktion. `SortOrders`, `Skip`/`Take` und `Limit` sind bei
  einer Aggregation bedeutungslos und blieben als toter Ballast in der Config stehen, den die
  Studio-UI trotzdem rendert.
* **Die Ergebnisform ist eine andere:** eine Kennzahlenzeile (bzw. eine je Gruppe) statt n Rohdatenzeilen.
* **Kein Modus-Enum.** Ein Node mit drei sich ausschließenden Betriebsarten wäre schwer zu
  dokumentieren und zu validieren.
* **Die Lückenlogik wird trotzdem nicht dupliziert.** `StreamDataGapAnalyzer` (§6.4) ist per Design
  eine reine, DB-freie Funktion in `Nodes/`; beide Nodes rufen sie auf. Geteilt werden
  außerdem `StreamDataNodeHelpers` (§5.2) und die Archiv-/Filter-Auflösung aus §4. Dupliziert werden
  nur einige Config-Deklarationen.

Preis dieser Aufteilung: Wer Rohdaten **und** Monatssumme braucht, konfiguriert Archiv, Filter und
Zeitraum zweimal.

### 2.2 Was die Schreibseite bereits garantiert — und was nicht

Geprüft, weil es bestimmt, wogegen der Aggregations-Guard überhaupt schützen muss.

**Exakte Duplikate sind ausgeschlossen.** Die Tabelle hat den Primärschlüssel
`(window_start, window_end, rtid, cktypeid)`, und `BuildTimeRangeInsertSql`
(`Runtime.Engine.CrateDb/Client/CrateDatabaseClient.cs:272`) schreibt
`ON CONFLICT (...) DO UPDATE`: eine Re-Lieferung desselben Fensters für dieselbe Entität wird zum
Upsert (und setzt `was_updated = TRUE`), niemals zu einer zweiten Zeile. Eine Summe kann durch
identische Fenster also nicht doppelt zählen.

**Partielle Überlappungen sind erlaubt — bewusst.**
`octo-construction-kit-engine/docs/concept-time-range-archives.md:47-50` listet das unter Non-Goals:

> „Overlap detection. Storing both `[13:00, 14:00)` and `[13:00, 13:30)` for the same entity is
> allowed — they are independent rows under the `(start, end, rtid, ckTypeId)` natural key. The
> query layer is responsible for picking a consistent slicing if the consumer needs one."

Ebenso Zeile 259: „different windows (even overlapping) coexist as independent rows." Auch
Fenster-Alignment wird ausdrücklich nicht erzwungen („the EDA market has irregular slots").

Validiert wird beim Schreiben nur `To > From` je Zeile
(`CrateDbStreamDataRepository.cs:396-404`); `SaveTimeRangeStreamDataInArchiveNode` prüft
zusätzlich nur, dass alle Quell-`rtId`s als Entitäten existieren (Orphan-Guard). Keine der beiden
Ebenen betrachtet das Verhältnis der Fenster zueinander.

### 2.3 Konsequenz für `requireGapFree`

Überlappende Fenster sind laut Storage-Konzept **gültige Daten**, nicht ein Defekt. Sie zum harten
Fehler zu machen würde legitime Modelle blockieren — und der Name „gap free" verspricht das auch
nicht. Deshalb:

* `requireGapFree: true` schlägt **nur bei Lücken** fehl.
* Überlappungen werden trotzdem erkannt — der Merge-Schritt des Coverage-Algorithmus stolpert
  ohnehin über sie — und als `hasOverlaps` je Serie gemeldet plus einmal als **Warnung** geloggt.
* Wer Überlappungen als Abbruchgrund behandeln will, prüft `hasOverlaps` aus dem Lückenreport von
  `GetStreamData@1` per `If@1`. Damit bleibt die Entscheidung bei der Pipeline, ohne dass eine
  weitere Config-Property nötig wird.

Relevant bleibt der Hinweis trotzdem: eine `SUM` über sich überlappende Fenster zählt den
Überlappungsbereich mehrfach. Das gehört in die Node-Doku (§7.5).

### 2.4 Abgrenzung Aggregation ↔ Downsampling

Leicht zu verwechseln, deshalb explizit:

| | Aggregation (§7, dieses Feature) | Downsampling (nicht enthalten) |
|---|---|---|
| Ergebnis | **ein** Wert je Kennzahl über den ganzen Zeitraum (bzw. je `groupBy`-Gruppe) | eine **Zeitreihe** aus n Bins |
| Engine-API | `ExecuteAggregationQueryAsync` / `ExecuteGroupedAggregationQueryAsync` | `ExecuteDownsamplingQueryAsync` |
| Typischer Zweck | Monatsverbrauch, Maximalwert, Zählerstände | Chart, Trendkurve |

### Nicht-Ziele

* **Kein Downsampling (AB#4727 gestrichen).** In diesem Feature geht es um exakte Daten und um die
  Lückenerkennung — eine Verdichtung in Bins verwischt genau die Information, die hier gebraucht
  wird. Downsampling-Queries sind über `GetQueryById@1` mit einer persistierten
  `RtDownsamplingSdQuery` vollständig abgedeckt (inkl. resolution-aware Rollup-Auswahl,
  AB#4195/AB#4233/AB#4290/AB#4725); das genügt.
* **Keine resolution-aware Rollup-Auswahl.** Beide Nodes lesen **exakt das konfigurierte Archiv** —
  vorhersagbar und ohne Exaktheits-Guards. Wer Routing will, nimmt `GetQueryById@1`.
* Keine Änderungen an Engine, CrateDB-Provider, GraphQL oder Studio. Die Nodes erreichen die
  Refinery Studio automatisch über `NodeSchemaRegistry` → `NodeDescriptorDto` → Adapter-Hub.
* Kein neuer CK-Model-Typ, keine Migration — neue Nodes sind rein additiv.

---

## 3. Technische Befunde, die den Entwurf bestimmen

Beide sind am Code verifiziert.

### (a) `ExecuteDownsamplingQueryAsync` ist für Gap-Detection unbrauchbar

Naheliegend wäre, Lücken über eine Downsampling-Query mit `COUNT` je Bin zu finden — leere Bins
wären die Lücken. **Das funktioniert nicht.**

`CrateDbStreamDataRepository.ResolveEffectiveDownsamplingLimitAsync`
(`octo-construction-kit-engine-mongodb/src/Runtime.Engine.CrateDb/CrateDbStreamDataRepository.cs:846`)
klemmt die angeforderte Bucket-Anzahl auf die Anzahl **distinkter Quell-Bins im Zeitraum** herunter:

```csharp
var distinctBins = Convert.ToInt32(countObj);
if (distinctBins > 0 && distinctBins < requestedLimit)
{
    return distinctBins;   // <- Clamp
}
```

Fehlen 20 von 96 Viertelstunden, werden aus 96 angeforderten Buckets 76 — das Raster wird
stillschweigend breiter und genau die Lücken verschwinden. Der Clamp ist für Charts richtig
(AB#4246), für Lückenerkennung fatal.

**Konsequenz:** Gap-Detection scannt über `ExecuteQueryAsync` die tatsächlichen Fenster (§6).

### (b) Welche Spalten wirklich in `StreamDataRow.Values` landen

`ExecuteQueryAsync` projiziert bei windowed Archiven über `IncludeDefaultVariables`
(`Runtime.Engine.CrateDb/QueryBuilder/CrateQueryBuilder.cs:185`) stets:

```
"window_end" AS "timestamp", "window_start", "window_end", "was_updated",
"rtid", "cktypeid", "rtwellknownname", "rtcreationdatetime", "rtchangeddatetime"
```

In `StreamDataRow.Values` landet aber nur, was in `options.Columns` **explizit angefordert** wurde
(`MapToStreamDataRow`, `CrateDbStreamDataRepository.cs:2646`). Für den Gap-Scan muss
`"window_start"` also explizit in `WithColumns(...)` stehen; `window_end` kommt gratis über
`StreamDataRow.Timestamp`.

Aggregatwerte werden anders verschlüsselt: `{physicalColumn}_{funcToken}` (z. B. `energy_sum`) mit
SQL-Alias-Fallback `{Func}_{physicalColumn}` (`Sum_energy`) — siehe
`GetQueryByIdNode.ResolveStreamAggregationValue` (`:1188`).

---

## 4. Gemeinsame Grundlagen (gelten für beide Nodes)

### Node-Skelett

| Node | Config-Record | Implementierung |
|---|---|---|
| `GetStreamData@1` | `src/MeshNodes.Sdk/Extract/GetStreamDataNodeConfiguration.cs` | `src/MeshAdapter.Sdk/Nodes/Extract/GetStreamDataNode.cs` |
| `AggregateStreamData@1` | `src/MeshNodes.Sdk/Extract/AggregateStreamDataNodeConfiguration.cs` | `src/MeshAdapter.Sdk/Nodes/Extract/AggregateStreamDataNode.cs` |

Namespaces: Config in `Meshmakers.Octo.MeshAdapter.Nodes.Extract`, Implementierung in
`Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract`. Der **Namespace** erzeugt die UI-Kategorie
„Extract" (`NodeSchemaRegistry.DeriveCategory`) — es gibt keine explizite Kategoriedeklaration.

Config: `[NodeName("<Name>", 1)]`, Basis `TargetPathNodeConfiguration`.
Node: `[NodeConfiguration(typeof(<Name>NodeConfiguration))]`, Primary Constructor mit
`NodeDelegate next` als **erstem** Parameter, Rest aus DI.

```csharp
public class GetStreamDataNode(NodeDelegate next, IMeshEtlContext context, ISystemContext systemContext)
    : IPipelineNode
```

### Registrierung

Je Node:

* `.RegisterNode<...>()` in
  `src/MeshAdapter.Sdk/Configuration/DependencyInjection/ServiceCollectionExtensions.cs` — **zwingend**
  (registriert implizit auch die Config).
* `RegisterNodeConfiguration<...>()` im Extract-Block von
  `src/MeshNodes.Sdk/Configuration/DataPipelineBuilderExtensions.cs` — Konvention, hält die Liste
  vollständig.

### Repository- und Archiv-Auflösung

Muster aus `GetQueryByIdNode.ResolveStreamDataContextAsync` (`GetQueryByIdNode.cs:675`):

```csharp
var tenantContext = await systemContext.FindTenantContextAsync(context.TenantId);
var repository = tenantContext.GetStreamDataRepository()
                 ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext, tenantId);
var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
               ?? throw MeshAdapterPipelineExecutionException.ArchiveNotFound(nodeContext, archiveRtId);
```

Der `ArchiveSnapshot`
(`octo-construction-kit-engine/src/Runtime.Contracts/StreamData/ArchiveSnapshot.cs`) liefert alles
Nötige in einem Aufruf:

| Property | Bedeutung für die Nodes |
|---|---|
| `TargetCkTypeId` | Pflichtfeld jeder `StreamDataQueryOptions*`-Instanz |
| `Status` | nur `Activated` erlaubt Lesen (die Engine wirft sonst `ArchiveNotActivatedException`) |
| `UsesWindowedStorage` | Zeitachse: `timestamp` (raw) vs. `window_start`/`window_end` (TimeRange/Rollup) |
| `Period` | die native Fensterlänge, z. B. `PT15M` — **advisory**, wird beim Insert nicht erzwungen |
| `Columns` | projizierbare Spalten inkl. Computed-Column-Zustand |

> **Hinweis zu `Period`:** Bei `TimeRangeArchive` ist `Period` laut CK-Modell ausdrücklich nur
> beschreibend („the engine does not enforce that incoming `[from, to)` windows match the declared
> period"), und für variable Perioden darf es `null` sein. Genau deshalb ist der Coverage-Algorithmus
> (§6) nicht auf `Period` angewiesen.

### Filter (identisch in beiden Nodes)

**`wellKnownName`** → `FieldFilter("rtWellKnownName", In, values)`; bei genau einem Wert `Equals`.
`rtWellKnownName` ist auf jeder Archivtabelle eine Standardspalte und im `StreamDataFieldResolver`
case-insensitiv registriert — es ist damit ein ganz normaler Feldfilter, keine Sonderbehandlung im
Storage nötig.

**Generische `FieldFilters`** über den `scratch`-Trick aus
`GetQueryByIdNode.BuildStreamDataFieldFilters` (`:916`): `FieldFilterExtensions.GetFieldFilter`
gegen ein Wegwerf-`RtEntityQueryOptions` laufen lassen und die entstandenen `FieldFilter` übernehmen —
so wird die `ComparisonValuePath`-Logik (inkl. Wildcard-Expansion) nicht dupliziert.

**Entitäts-Scoping** über `WithRtIds(...)`; die Engine setzt das als `In`-Filter auf `rtid` um und es
wirkt damit auf allen Query-Arten. `RtIds` wird als `ICollection<string>` konfiguriert, nicht als
`ICollection<OctoObjectId>`: die Ids kommen typischerweise als Strings aus den Pipeline-Daten, und so
ist die generierte Schema-Form garantiert ein `array of string`. Der Node parst sie und wirft bei
einem ungültigen Wert.

### Spaltennamen für Sortierung und Filter (identisch in beiden Nodes)

**Nachgezogen nach einem Feldfehler** — beim ersten Test kam `sortOrders: [{attributeName: WindowStart}]`
unsortiert zurück. Ursache: Ein Node gibt Ergebnis-Header wie `Timestamp`, `WindowStart`, `WindowEnd`
und `WellKnownName` aus, der `StreamDataFieldResolver` kennt aber nur die physischen Namen
(`window_start`). Sein Lookup ist case-insensitiv, aber **nicht** trennzeichen-insensitiv — `WindowStart`
trifft nicht.

Verschärfend: Die Storage-Schicht **verwirft einen nicht auflösbaren Namen kommentarlos** —
`AddSortOrders` und `BuildFieldFilterDtos` überspringen ihn beide mit `continue`. Eine vertippte
Sortierung liefert damit still Storage-Reihenfolge, ein vertippter Filter still **zu viele** Zeilen.
Der Filterfall ist der gefährlichere: das Ergebnis wird breiter statt schmaler, ohne jeden Hinweis.

Beide Nodes übersetzen deshalb vor dem Aufruf (`StreamDataNodeHelpers.ResolveQueryableColumn`) und
lehnen ab, was sie nicht zuordnen können:

| Eingabe | Raw-Archiv | Windowed-Archiv |
|---|---|---|
| `Timestamp` | `timestamp` | `window_end` (dort gibt es keine `timestamp`-Spalte) |
| `WindowStart` / `WindowEnd` | **Fehler** — kein Fenster vorhanden | `window_start` / `window_end` |
| `WellKnownName` | `rtWellKnownName` | `rtWellKnownName` |
| Standardspalte der Storage-Form | unverändert | unverändert |
| Spalte des Archivs (Path bzw. Name) | unverändert | unverändert |
| alles andere | **Fehler**, mit Liste der gültigen Namen | dito |

Die Engine-seitige Ursache ist als eigener Bug erfasst (**AB#4765**) — jeder andere Konsument der
Stream-Data-Query-API läuft weiterhin hinein. **§7 muss dieselbe Übersetzung anwenden**, für
`FieldFilters` und für `groupBy`.

### UTC-Regel (AB#4734)

Ein `DateTime` mit `Kind == Unspecified` — also `"2026-07-01T00:00:00"` aus Pipeline-JSON — wird
**als UTC** gelesen, nicht als Lokalzeit des Adapter-Hosts. Gilt für Literale und für aus JSONPath
gelesene Werte gleichermaßen.

### Fehlerbehandlung und Logging

Keine rohen Exceptions. Statische Factories in
`src/MeshAdapter.Sdk/MeshAdapterPipelineExecutionException.cs`, Meldung stets mit
`[{nodeContext.NodePath}]`-Präfix. Storage-Aufrufe wie in `GetQueryByIdNode.ExecuteAsync` (`:537`)
kapseln, damit keine CrateDB-Exception aus dem Node austritt.

Hausregel (durchgängig in `GetQueryByIdNode` sichtbar): **warnen und degradieren** bei
informativen/behebbaren Zuständen (ein `*Path`, der ins Leere zeigt); **werfen** bei
Fehlkonfigurationen, die still falsche Daten erzeugen würden (ein vorhandener, aber nicht
datumsartiger Pfadwert; ein invertierter Zeitraum). Geloggt wird über `INodeContext`
(`Debug`/`Info`/`Warning`/`Error`), nicht über `ILogger`.

### Versionierung

Alle Tasks bleiben bei `@1`. Optionale Config-Properties hinzuzufügen ist additiv und kein
Breaking Change — bestehende Pipelines sind nicht betroffen. Eine `@2` wäre nur nötig, wenn sich die
Semantik bestehender Properties änderte.

### Doku-Pflicht

Laut Präambel von `CLAUDE.md` aktualisiert **jeder Task** `CLAUDE.md` §Extract Nodes **und**
`docs/developer-guide.md` für seinen eigenen Umfang. XML-`<summary>`-Kommentare auf Config-Klasse und
jeder Property sind Pflicht — über `NodeSchemaRegistry.InjectXmlDescriptions` sind sie die einzige
Doku-Oberfläche im Pipeline-Editor.

---

## 5. Task AB#4726 — `GetStreamData@1`, Basisfunktionalität

Eigenständig lieferbar; ergibt einen vollständig nutzbaren Node. §6 und §7 bauen darauf auf.

### 5.1 Konfiguration (Anteil dieses Tasks)

```csharp
[NodeName("GetStreamData", 1)]
public record GetStreamDataNodeConfiguration : TargetPathNodeConfiguration
{
    [PropertyGroup("Archive", 0)] public required OctoObjectId ArchiveRtId { get; init; }

    // Leer ⇒ alle ingested Archivspalten + WellKnownName (§5.4)
    [PropertyGroup("Query", 0)] public ICollection<string>? Columns { get; init; }
    [PropertyGroup("Query", 1)] public ICollection<string>? WellKnownNames { get; init; }
    [PropertyGroup("Query", 2, "jsonpath")] public string? WellKnownNamesPath { get; init; }
    [PropertyGroup("Query", 3)] public ICollection<string>? RtIds { get; init; }
    [PropertyGroup("Query", 4, "jsonpath")] public string? RtIdsPath { get; init; }
    [PropertyGroup("Query", 5)] public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
    [PropertyGroup("Query", 6)] public ICollection<SortOrderDto>? SortOrders { get; set; }
    [PropertyGroup("Query", 7)] public int? Skip { get; init; }
    [PropertyGroup("Query", 8)] public int? Take { get; init; }

    // Literal schlägt Path — exakt die GetQueryById@1-Semantik
    [PropertyGroup("TimeRange", 0)] public DateTime? From { get; init; }
    [PropertyGroup("TimeRange", 1, "jsonpath")] public string? FromPath { get; init; }
    [PropertyGroup("TimeRange", 2)] public DateTime? To { get; init; }
    [PropertyGroup("TimeRange", 3, "jsonpath")] public string? ToPath { get; init; }
    [PropertyGroup("TimeRange", 4)] public int? Limit { get; init; }
    [PropertyGroup("TimeRange", 5, "jsonpath")] public string? LimitPath { get; init; }
}
```

Wiederverwendete Typen:
`FieldFilterWithPathDto` und `SortOrderDto` aus `src/MeshNodes.Sdk/PipelineDataTransferObjects/`.

`ArchiveRtId` ist ein nicht-nullbarer Struct und wird von
`NodeSchemaRegistry.AddRequiredForNonNullableValueTypes` automatisch als `required` ins Schema
geschrieben.

### 5.2 Helfer-Refactoring (Teil dieses Tasks, kein Copy-Paste)

`GetQueryByIdNode` enthält bereits exakt die benötigte Logik. Sie wandert nach
`src/MeshAdapter.Sdk/Nodes/StreamDataNodeHelpers.cs` (`internal static`);
`GetQueryByIdNode` ruft danach die Helfer auf. Das Verhalten bleibt identisch und ist durch die
bestehenden ~2 300 Zeilen `GetQueryByIdNodeTests.cs` abgesichert.

| Methode | heute in `GetQueryByIdNode.cs` | gebraucht von |
|---|---|---|
| `ToUtc` / `ToUtcOrNull` | 648 / 665 | §5, §7 |
| `ResolveDateTimeFromPath` | 585 | §5, §7 |
| `ResolveIntFromPath` | 613 | §5 |
| `ResolveStreamColumnValue` (Punkte entfernt, kleingeschrieben) | 980 | §5, §6 |
| `ResolveStreamAggregationValue` (`{col}_{token}` + `{Func}_{col}`-Fallback) | 1188 | §7 |
| `MapStreamAggregation` (`AggregationTypesDto` → `AggregationFunction` + Key-Token) | — | §7 |

Die letzten beiden Zeilen werden erst von §7 benötigt. Sie lassen sich entweder gleich mitziehen
(empfohlen — ein Refactoring statt zwei) oder mit §7 nachziehen.

### 5.3 Ablauf

1. Repository + `ArchiveSnapshot` auflösen (§4).
2. Overrides auflösen: `From`/`To`/`Limit` als Literal, sonst über `FromPath`/`ToPath`/`LimitPath`
   aus dem Datenkontext; alles nach UTC normalisiert.
3. Filter bauen (§4).
4. Query ausführen:
   ```csharp
   var options = StreamDataQueryOptions.Create()
       .WithCkTypeId(snapshot.TargetCkTypeId)
       .WithColumns(columns)
       .WithRtIds(rtIds)
       .WithTimeRange(from, to)      // beide Grenzen unabhängig optional (AB#4617)
       .WithLimit(limit)
       .WithSortOrders(sortOrders)
       .WithFieldFilters(filters)
       .WithPagination(skip, take);  // Offset / PageSize; der Row-Cap ist Limit

   var result = await streamDataRepo.ExecuteQueryAsync(archiveRtId, options);
   ```
5. `QueryResult` bauen (§5.4) und schreiben:
   `dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode)`.
6. `await next(dataContext, nodeContext);` — immer als letztes.

### 5.4 Ergebnisform

`QueryResult` aus `src/MeshAdapter.Sdk/Nodes/Query.cs` — damit lässt sich
`QueryResultToMarkdownTable@1` direkt anschließen. Spaltenaufbau analog
`BuildSimpleStreamDataQueryResult` (`GetQueryByIdNode.cs:941`): führende `Timestamp`-Spalte, dann die
projizierten Spalten. Bei windowed Archiven zusätzlich `WindowStart`/`WindowEnd`.

Werte werden über `ResolveStreamColumnValue` gelesen: `StreamDataRow.Values` ist mit dem
**physischen CrateDB-Spaltennamen** verschlüsselt — Attributpfad ohne Punkte und kleingeschrieben
(`Amount.Value` → `amountvalue`).

**Leeres `Columns` liest das ganze Archiv** (nachgezogen nach dem ersten Praxiseinsatz): alle
*ingested* Spalten aus `snapshot.Columns`, davor eine `WellKnownName`-Spalte direkt aus
`StreamDataRow.RtWellKnownName`. Ohne das liefert die Minimalkonfiguration — nur ein `archiveRtId` —
lediglich die Zeitachse, was als Stolperfalle gemeldet wurde. Ist `Columns` gesetzt, gilt die Liste
unverändert; `rtWellKnownName` lässt sich dort wie jede andere Spalte anfordern.

**Computed Columns bleiben aus dem Automatik-Satz heraus.** Sie haben einen leeren `Path`, werden
über ihren `Name` adressiert (`StreamDataFieldResolver.CreateForArchive` warnt ausdrücklich vor
`snapshot.Columns.Select(c => c.Path)`), und nach einer Formeländerung liegt die physische Spalte
unter `{base}__v{N}` — `ComputedColumnNaming` ist im CrateDB-Provider `internal`. Damit findet die
Wertauflösung des Nodes sie auch bei expliziter Angabe nicht; **derselbe latente Fehler steckt in
`GetQueryById@1`** und ist ein eigenes Work Item wert.

### 5.5 Neue Exception-Factories

| Factory | Wofür |
|---|---|
| `ArchiveNotFound` | `ArchiveRtId` zeigt auf kein (oder ein soft-gelöschtes) Archiv |
| `StreamDataArchiveQueryFailed` | Storage-Fehler; die bestehende `StreamDataQueryFailed` verlangt eine Query-Entität und passt hier nicht |
| `StreamDataTimeRangeInvalid` | `From >= To` |
| `StreamDataLimitInvalid` | `Limit <= 0` — die Storage-Schicht lehnt das ohnehin ab, aber ohne die Property zu nennen |
| `UnknownStreamDataColumn` | Sortier-/Filterspalte weder Ergebnis-Header noch Archivspalte (siehe §4) |
| `InvalidRtId` | Ein konfigurierter oder aus den Pipeline-Daten gelesener Wert ist keine gültige Runtime-Id |

`StreamDataNotEnabled` besteht bereits und wird wiederverwendet.

### 5.6 Tests

**Unit** — `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/GetStreamDataNodeTests.cs`, Basis
`Helpers/NodeTestBase.cs`. Fake-Set analog `GetQueryByIdNodeTests.cs`:

```csharp
A.CallTo(() => _systemContext.FindTenantContextAsync(TestTenantId)).Returns(Task.FromResult(_tenantContext));
A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);
A.CallTo(() => _tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
```

Abgedeckt: Options-Aufbau; `wellKnownNames` → `rtWellKnownName`-Filter (Einzelwert `Equals`,
Mehrfachwert `In`); Sortierung; `skip`/`take`; Literal-vor-Path-Präzedenz bei `From`/`To`/`Limit`;
UTC-Normalisierung von `Kind=Unspecified`; Stream Data nicht aktiviert → Exception; Archiv nicht
gefunden → Exception; `next` wird aufgerufen.

**Integration** —
`tests/MeshAdapter.Sdk.IntegrationTests/Nodes/Extract/GetStreamDataNodeIntegrationTests.cs` gegen
das bestehende `RtRawArchive` der `StreamDataFixture`. Harness wie
`GetQueryByIdNodeStreamDataIntegrationTests.cs:408` — `QueryResult` vom gefakten
`IDataContext.Set` abgreifen. `[Trait("Category","Integration")]` + `[Collection("Sequential")]`.

---

## 6. Task AB#4728 — Gap-Detection

Additiv auf §5. Liefert außerdem den `StreamDataGapAnalyzer`, den §7 für `requireGapFree` nutzt.

### 6.1 Zusätzliche Konfiguration an `GetStreamData@1`

```csharp
[PropertyGroup("Gaps", 0, "jsonpath")] public string? GapsTargetPath { get; init; }
[PropertyGroup("Gaps", 1)] public TimeSpan? ExpectedInterval { get; init; }
[PropertyGroup("Gaps", 2)] public bool GapsOnly { get; init; }
[PropertyGroup("Gaps", 3)] public int? MaxGapScanRows { get; init; }
```

### 6.2 Einbettung in den bestehenden Node

Nachgetragen nach der Umsetzung von §5 — der Node ist seither gewachsen, und der Gap-Zweig muss sich
an vier Stellen einfügen statt danebenzustehen:

* **`RtIds` und Feldfilter einmal auflösen.** `ResolveRtIds` und `BuildFieldFilters` werten JSONPath
  aus und warnen bei einem Pfad, der ins Leere zeigt. Ein zweiter Aufruf für den Gap-Scan würde
  dieselbe Warnung ein zweites Mal loggen und die Pfade erneut auswerten. Beide Werte werden vor den
  Queries **einmal** ermittelt und an beide weitergereicht.
* **Bei `GapsOnly` nichts für die Datenabfrage vorbereiten.** `ResolveColumns`, `BuildProjectedColumns`
  und vor allem `BuildSortOrders` entfallen — letzteres wirft bei einer unbekannten Sortierspalte
  (§4), und dieser Fehler wäre für eine Abfrage, die gar nicht ausgeführt wird, nur verwirrend.
* **`WellKnownName` nicht projizieren.** Er steht wie `Timestamp` direkt auf `StreamDataRow`; der
  Gap-Scan fordert nur `window_start` an.
* **Zeitbereichs-Validierung erweitern.** Der Node erlaubt offene Grenzen; sobald die Lückenanalyse
  aktiv ist, sind `From` und `To` Pflicht (§6.7) — geprüft wird das zusätzlich zur bestehenden
  `From >= To`-Prüfung.

### 6.3 Eigene Abfrage

Die Lückenanalyse braucht eine **eigene** Query — `Limit`/`Skip`/`Take` der Datenabfrage würden das
Ergebnis verfälschen:

```csharp
var gapOptions = StreamDataQueryOptions.Create()
    .WithCkTypeId(snapshot.TargetCkTypeId)
    .WithColumns([WindowStartColumn])   // "window_start" — sonst fehlt es in row.Values (§3b)
    .WithRtIds(rtIds)
    .WithTimeRange(from, to)            // beide Grenzen Pflicht
    .WithFieldFilters(filters)          // dieselben wellKnownName-/Feldfilter wie die Datenabfrage
    .WithLimit(maxGapScanRows + 1);     // Speicherbremse
```

`window_end` kommt über `StreamDataRow.Timestamp` (windowed Archive aliasen `window_end AS timestamp`).
Sortiert wird in-memory — es wird ohnehin alles materialisiert.

**Speicherbremse.** `MaxGapScanRows`, Default 200 000. Kommen `max + 1` Zeilen zurück, bricht der
Node mit klarer Meldung ab („Zeitraum oder Entitätsmenge einschränken"), statt still ein falsches
Ergebnis zu liefern. Zur Einordnung: ein Jahr 15-Minuten-Werte = 35 040 Zeilen pro Entität.

### 6.4 Algorithmus

`src/MeshAdapter.Sdk/Nodes/StreamDataGapAnalyzer.cs` — eine reine, DB-freie Funktion und
damit der wertvollste Testpunkt des ganzen Features. Wird von `GetStreamData@1` (Report) und
`AggregateStreamData@1` (Guard) genutzt.

```
je rtId-Gruppe:
  windows = Zeilen → (Start = values["window_start"], End = row.Timestamp), beide non-null
  jedes Fenster auf [from, to) clampen, nach Start sortieren
  überlappende/angrenzende Fenster verschmelzen → coveredRanges
     dabei mitzählen, ob echte Überlappungen aufgetreten sind → hasOverlaps
  cursor = from
  für jeden range in coveredRanges:
      wenn range.Start > cursor  →  Lücke (cursor, range.Start)
      cursor = max(cursor, range.End)
  wenn cursor < to  →  Lücke (cursor, to)
```

Beispiel: `from = 12:00`, `to = 13:00`, Period 15 min, vorhanden `[12:00,12:15)`, `[12:15,12:30)`,
`[12:45,13:00)` → eine Lücke `[12:30, 12:45)` mit `missingIntervals: 1`.

**`hasOverlaps` fällt beim Mergen gratis an** und wird deshalb mitgemeldet: eine `SUM` über sich
überlappende Fenster zählt den Überlappungsbereich mehrfach. Überlappungen sind laut
Storage-Konzept aber gültige Daten (§2.2) und lassen `requireGapFree` **nicht** fehlschlagen —
sie werden gemeldet und gewarnt, die Entscheidung trifft die Pipeline (§2.3).

**Intervall-Auflösung** für die Zählwerte: `ExpectedInterval` → `snapshot.Period` → keines. Ohne
Intervall werden die Lücken trotzdem als Zeitbereiche gemeldet, die `*Intervals`-Felder bleiben
`null` und es wird einmal gewarnt. `missingIntervals` je Lücke = `ceil(duration / interval)`;
`duration` ist immer exakt.

### 6.5 Bekannte Grenze — bewusst und zu dokumentieren

Ein Coverage-Scan sieht nur Entitäten mit **mindestens einer Zeile** im Fenster. Eine Entität, die im
gesamten Zeitraum gar nichts geliefert hat, taucht nicht auf.

Abmilderung: Sind `RtIds` bzw. `WellKnownNames` konfiguriert, ist die Sollmenge bekannt — für jede
Entität ohne Zeilen wird eine Serie mit einer einzigen Lücke über `[from, to)` und
`isComplete: false` erzeugt. Ohne diese Angabe wird die Einschränkung im Node-Doc-Kommentar und im
Developer-Guide festgehalten und einmal als Info geloggt.

### 6.6 Ergebnisform

`src/MeshAdapter.Sdk/Nodes/StreamDataGapReport.cs` — interne Records; `OctoObjectId` mit
`[JsonConverter(typeof(OctoObjectIdConverter))]` wie `QueryResultRow` in `Nodes/Query.cs`.

```yaml
from: 2026-07-01T00:00:00Z
to:   2026-07-02T00:00:00Z
interval: PT15M
seriesCount: 2
seriesWithGapsCount: 1
isComplete: false
series:
  - rtId: 68a1...
    wellKnownName: METER-4711
    expectedIntervals: 96
    presentIntervals: 94
    missingIntervals: 2
    hasOverlaps: false
    isComplete: false
    gaps:
      - from: 2026-07-01T12:30:00Z
        to:   2026-07-01T13:00:00Z
        duration: PT30M
        durationSeconds: 1800
        missingIntervals: 2
  - rtId: 68a2...
    wellKnownName: METER-4712
    expectedIntervals: 96
    presentIntervals: 96
    missingIntervals: 0
    hasOverlaps: false
    isComplete: true
    gaps: []
```

Zeitspannen werden als ISO-8601-String (`XmlConvert.ToString(TimeSpan)`) **und** als
`*Seconds`-`double` ausgegeben — der String ist lesbar, die Zahl in nachgelagerten Nodes rechenbar.

### 6.7 Validierung

`GapsTargetPath` gesetzt ⇒ `From` und `To` Pflicht, und das Archiv muss `UsesWindowedStorage` sein
(TimeRange oder Rollup; Raw-Archive haben keine Fenster). `GapsOnly` ohne `GapsTargetPath` ⇒ Fehler.

Neue Factories: `GapDetectionRequiresWindowedArchive`, `GapDetectionTimeRangeRequired`,
`GapScanRowLimitExceeded`.

### 6.8 Tests

**`tests/MeshAdapter.Sdk.Tests/Nodes/StreamDataGapAnalyzerTests.cs`** — komplett ohne DB:
lückenlos; Lücke in der Mitte / am Anfang / am Ende; mehrere Lücken; überlappende Fenster
(→ `hasOverlaps`); angrenzende Fenster ohne Lücke; Fenster ragen über `[from, to)` hinaus
(Clamping); variable Fensterlängen ohne `Period`; kein Intervall bekannt → `*Intervals` null; leere
Serie; mehrere `rtId`s unabhängig voneinander; Entität aus `RtIds` ohne jede Zeile →
Vollausfall-Serie.

**Node-Tests:** `gapsTargetPath` auf einem Raw-Archiv → Exception; `gapsTargetPath` löst eine
**zweite** Query aus; `GapsOnly` überspringt die Datenabfrage.

**Fixture-Erweiterung (Aufwand nicht übersehen).**
`tests/MeshAdapter.Sdk.IntegrationTests/Fixtures/StreamDataFixture.cs` stellt heute nur ein
`RtRawArchive` mit 5 Punkten im 15-Minuten-Abstand bereit. Für die Gap-Tests wird zusätzlich ein
**`RtTimeRangeArchive`** mit `Period = PT15M` gebraucht, befüllt über
`IStreamDataRepository.InsertTimeRangeAsync` für **zwei** Quell-Entitäten und mit absichtlich
ausgelassenen Fenstern: eine Lücke in der Mitte, eine am Rand, eine Entität lückenlos. Diese Fixture
wird von §7 mitgenutzt.

---

## 7. Neuer Task — `AggregateStreamData@1`, Spalten-Aggregation

> Work Item in Azure DevOps noch anzulegen, als Kind von AB#4722.

Hängt von §5 ab (Helfer, Archiv-/Filter-Auflösung). `requireGapFree` hängt zusätzlich von §6 ab
(`StreamDataGapAnalyzer`) — ohne §6 lässt sich der Node ohne diese eine Property ausliefern.

### 7.1 Anwendungsbeispiel

Aus dem Archiv `energy-measurements` die verbrauchte Energie eines Monats je Zähler aufsummieren und
die maximale Datenqualität im selben Zeitraum ausgeben — nur wenn der Monat lückenlos vorliegt:

```yaml
- type: AggregateStreamData@1
  archiveRtId: <energy-measurements archiveRtId>
  aggregations:
    - attributePath: Energy
      function: SUM
    - attributePath: DataQuality
      function: MAXIMUM
  groupBy: [ rtId ]
  from: 2026-07-01T00:00:00
  to:   2026-08-01T00:00:00
  requireGapFree: true
  expectedInterval: PT15M
  targetPath: $.monthly
```

### 7.2 Konfiguration

```csharp
[NodeName("AggregateStreamData", 1)]
public record AggregateStreamDataNodeConfiguration : TargetPathNodeConfiguration
{
    [PropertyGroup("Archive", 0)] public required OctoObjectId ArchiveRtId { get; init; }

    [PropertyGroup("Aggregation", 0)] public required ICollection<AggregationColumnDto> Aggregations { get; init; }
    [PropertyGroup("Aggregation", 1)] public ICollection<string>? GroupBy { get; init; }

    [PropertyGroup("Query", 0)] public ICollection<string>? WellKnownNames { get; init; }
    [PropertyGroup("Query", 1, "jsonpath")] public string? WellKnownNamesPath { get; init; }
    [PropertyGroup("Query", 2)] public ICollection<string>? RtIds { get; init; }
    [PropertyGroup("Query", 3, "jsonpath")] public string? RtIdsPath { get; init; }
    [PropertyGroup("Query", 4)] public ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    [PropertyGroup("TimeRange", 0)] public DateTime? From { get; init; }
    [PropertyGroup("TimeRange", 1, "jsonpath")] public string? FromPath { get; init; }
    [PropertyGroup("TimeRange", 2)] public DateTime? To { get; init; }
    [PropertyGroup("TimeRange", 3, "jsonpath")] public string? ToPath { get; init; }

    [PropertyGroup("Gaps", 0)] public bool RequireGapFree { get; init; }
    [PropertyGroup("Gaps", 1)] public TimeSpan? ExpectedInterval { get; init; }
    [PropertyGroup("Gaps", 2)] public int? MaxGapScanRows { get; init; }
}
```

Bewusst **nicht** enthalten: `Columns`, `SortOrders`, `Skip`, `Take`, `Limit` — bei einer Aggregation
ohne Bedeutung (siehe §2.1).

**Spaltennamen übersetzen (§4).** `FieldFilters`, `GroupBy` und die `attributePath`-Werte in
`Aggregations` laufen durch `StreamDataNodeHelpers.ResolveQueryableColumn`. Ohne das verwirft die
Storage-Schicht einen nicht auflösbaren Namen stillschweigend — bei einem Filter heißt das ein zu
breites Ergebnis, bei einer Aggregation also eine **zu hohe Summe**, ohne jeden Hinweis. Genau der
Fehler, der bei `GetStreamData@1` im Feld auftrat.

**Neuer DTO** `src/MeshNodes.Sdk/PipelineDataTransferObjects/AggregationColumnDto.cs`:

```csharp
public record AggregationColumnDto
{
    /// <summary>Attributpfad, der aggregiert wird (z. B. "Energy", "Amount.Value").</summary>
    public required string AttributePath { get; set; }

    /// <summary>Aggregationsfunktion.</summary>
    public required AggregationTypesDto Function { get; set; }

    /// <summary>Zustandsliteral für StateDuration; sonst ignoriert (AB#4336).</summary>
    public string? ComparisonValue { get; set; }
}
```

Eigener DTO statt direkter Nutzung von `AggregationColumn`: Config-Typen leben in `MeshNodes.Sdk`
(schmale Abhängigkeiten, JSON-Schema-freundlich), `AggregationColumn` dagegen in
`Runtime.Contracts`. `AggregationTypesDto` (`octo-sdk/src/Communication.Contracts/DataTransferObjects/`)
wird bereits von `GetQueryByIdNodeConfiguration` genutzt.

Unterstützte Funktionen (aus `AggregationFunction`): `Count`, `Minimum`, `Maximum`, `Average`, `Sum`,
`TimeWeightedAverage`, `StateDuration`. `AggregationTypesDto.None` wird abgelehnt.

### 7.3 Ablauf

1. Repository + `ArchiveSnapshot` auflösen (§4).
2. `From`/`To` auflösen und nach UTC normalisieren.
3. Filter bauen (§4).
4. **Wenn `RequireGapFree`:** Gap-Scan nach §6.3/§6.4 **vor** der Aggregation. Ist eine Serie
   unvollständig → Pipeline-Exception, die die betroffenen Serien mit `wellKnownName`, fehlenden
   Intervallen und dem ersten Lückenbereich benennt. Keine teilweisen Ergebnisse: entweder alle
   Serien sind lückenlos oder der Node bricht ab.
   Erkannte Überlappungen brechen **nicht** ab (§2.3), werden aber mit Serienangabe als Warnung
   geloggt.
5. Aggregation ausführen:
   ```csharp
   var columns = c.Aggregations
       .Select(a => new AggregationColumn(a.AttributePath,
                                          MapStreamAggregation(a.Function).Function,
                                          a.ComparisonValue))
       .ToList();

   StreamDataQueryResult result;
   if (c.GroupBy is { Count: > 0 })
   {
       var options = StreamDataGroupedAggregationQueryOptions.Create()
           .WithCkTypeId(snapshot.TargetCkTypeId)
           .WithGroupByColumns(c.GroupBy.ToList())
           .WithAggregationColumns(columns)
           .WithRtIds(rtIds)
           .WithTimeRange(from, to)
           .WithFieldFilters(filters);
       result = await streamDataRepo.ExecuteGroupedAggregationQueryAsync(archiveRtId, options);
   }
   else
   {
       var options = StreamDataAggregationQueryOptions.Create()
           .WithCkTypeId(snapshot.TargetCkTypeId)
           .WithAggregationColumns(columns)
           .WithRtIds(rtIds)
           .WithTimeRange(from, to)
           .WithFieldFilters(filters);
       result = await streamDataRepo.ExecuteAggregationQueryAsync(archiveRtId, options);
   }
   ```
6. `QueryResult` bauen (§7.4) und nach `TargetPath` schreiben.
7. `await next(dataContext, nodeContext);`

Rollup-Archive funktionieren ohne Zusatzaufwand: `ExecuteAggregationQueryAsync` hat einen
chain-aware Pfad (`ResolveRollupChainAggregationAsync`), der die Aggregate aus den
Rollup-Spalten zusammensetzt.

### 7.4 Ergebnisform

`QueryResult`, mit Parität zu den bestehenden Buildern in `GetQueryByIdNode`
(`BuildAggregationStreamDataQueryResult` `:992`, `BuildGroupedAggregationStreamDataQueryResult` `:1015`):

* ohne `groupBy`: eine Spalte je Aggregation (Header = Attributpfad), **eine** Zeile, `RtId` null
* mit `groupBy`: zuerst die Group-By-Spalten, dann die Aggregationsspalten; eine Zeile je Gruppe

Werte werden über `ResolveStreamAggregationValue` gelesen — Aggregatschlüssel sind
`{physicalColumn}_{funcToken}` mit SQL-Alias-Fallback `{Func}_{physicalColumn}` (§3b).

Wird derselbe Attributpfad mehrfach mit verschiedenen Funktionen aggregiert (`MIN` + `MAX` auf
`Energy`), sind die Ergebnisschlüssel durch das Funktionssuffix eindeutig — die Spaltenheader im
`QueryResult` wären es dagegen nicht. Header deshalb als `{AttributePath} ({Function})` ausgeben,
sobald ein Pfad mehrfach vorkommt.

### 7.5 Fachliche Fallstricke (in die Node-Doku übernehmen)

* **`Sum` ist nur bei disjunkten Fenstern korrekt.** Identische Fenster kann es nicht geben — der
  Primärschlüssel upserted sie (§2.2). *Überlappende* Fenster sind dagegen erlaubt und werden im
  Überlappungsbereich doppelt gezählt. `requireGapFree` fängt das bewusst nicht ab (§2.3); wer
  darauf reagieren muss, wertet `hasOverlaps` aus dem Lückenreport aus.
* **`Average` ist arithmetisch, nicht zeitgewichtet.** Bei gleich langen Fenstern (der 15-Minuten-Fall)
  identisch; bei variablen Fensterlängen ist `TimeWeightedAverage` das richtige Mittel.
* **`TimeWeightedAverage` und `StateDuration` auf Raw-Archiven brauchen `From` und `To`** — die Engine
  routet dort auf `ExecuteRawTimeWeightedAggregationAsync` (LOCF). Am Node validieren, statt die
  Storage-Exception durchzureichen.

### 7.6 Validierung

`Aggregations` nicht leer; keine Funktion `None`; `StateDuration` verlangt `ComparisonValue`;
`RequireGapFree` ⇒ `From` und `To` Pflicht und Archiv muss `UsesWindowedStorage` sein;
`TimeWeightedAverage`/`StateDuration` auf Raw-Archiv ⇒ `From` und `To` Pflicht.

Neue Factories: `AggregationColumnsMissing`, `UnsupportedAggregationFunction`,
`StateDurationComparisonValueMissing`, `AggregationGapGuardFailed` (nennt die betroffenen Serien).
`GapDetectionRequiresWindowedArchive` und `GapDetectionTimeRangeRequired` aus §6 werden mitgenutzt.

### 7.7 Tests

**Unit** — `tests/MeshAdapter.Sdk.Tests/Nodes/Extract/AggregateStreamDataNodeTests.cs`:
Options-Aufbau mit und ohne `groupBy` (richtige Repository-Methode!); Mapping
`AggregationTypesDto` → `AggregationFunction`; `ComparisonValue` wird durchgereicht;
Ergebnis-Mapping inkl. mehrfach aggregiertem Pfad; `requireGapFree: false` ignoriert Lücken;
`requireGapFree: true` bei Lücken → Exception mit Serienangabe; `requireGapFree: true` bei
Überlappung (ohne Lücke) → **kein** Abbruch, aber Warnung; `requireGapFree: true` auf Raw-Archiv →
Exception; Validierungsfehler (`Aggregations` leer, `None`, `StateDuration` ohne
`ComparisonValue`); `next` wird aufgerufen.

**Integration** —
`tests/MeshAdapter.Sdk.IntegrationTests/Nodes/Extract/AggregateStreamDataNodeIntegrationTests.cs`
gegen das TimeRange-Archiv aus §6.8: Summe über den lückenlosen Zeitraum entspricht der
Handsumme der eingefügten Werte; `MAXIMUM` liefert den erwarteten Höchstwert; `groupBy: [rtId]`
liefert eine Zeile je Zähler; `requireGapFree: true` schlägt für die Serie mit den künstlichen
Lücken fehl und geht für die lückenlose Serie durch.

---

## 8. Verifikation

```bash
cd octo-mesh-adapter

dotnet build -c Debug                              # warnings-as-errors
dotnet test --filter "Category!=Integration"       # inkl. GetQueryById-Suite (sichert das §5.2-Refactoring)
dotnet test --filter "Category=Integration"        # MongoDB- und CrateDB-Testcontainer
grep -cE "GetStreamData|AggregateStreamData" src/MeshAdapter/bin/Debug/net10.0/pipeline-schema.json
```

Ende-zu-Ende gegen einen Tenant mit aktiviertem Stream Data und einem aktivierten TimeRange-Archiv:

```yaml
triggers:
  - type: FromHttpRequest@1
    path: /streamdata-test
    method: POST
transformations:
  # Rohdaten + Lückenreport
  - type: GetStreamData@1
    archiveRtId: <archiveRtId>
    columns: [ Energy, DataQuality ]
    wellKnownNames: [ METER-4711 ]
    from: 2026-07-01T00:00:00
    to:   2026-08-01T00:00:00
    sortOrders:
      - attributeName: timestamp
        sortOrder: Ascending
    targetPath:     $.rows          # §5
    gapsTargetPath: $.gaps          # §6
    expectedInterval: PT15M         # §6

  # Monatssumme je Zähler, nur wenn lückenlos
  - type: AggregateStreamData@1     # §7
    archiveRtId: <archiveRtId>
    aggregations:
      - attributePath: Energy
        function: SUM
      - attributePath: DataQuality
        function: MAXIMUM
    groupBy: [ rtId ]
    from: 2026-07-01T00:00:00
    to:   2026-08-01T00:00:00
    requireGapFree: true
    expectedInterval: PT15M
    targetPath: $.monthly

  - type: SetPipelineExecutionResult@1
    path: $
```

Erwartung: `$.rows` enthält das `QueryResult` mit `Timestamp`-Spalte und den Werten, `$.gaps` die
Serien-Auswertung mit exakt den Lücken, die im Archiv fehlen, und `$.monthly` je Zähler eine Zeile
mit Energiesumme und maximaler Datenqualität.

---

## 9. Dateien je Task

| Task | Neu | Geändert |
|---|---|---|
| **AB#4726**<br>Basis | `MeshNodes.Sdk/Extract/GetStreamDataNodeConfiguration.cs`<br>`MeshAdapter.Sdk/Nodes/Extract/GetStreamDataNode.cs`<br>`MeshAdapter.Sdk/Nodes/StreamDataNodeHelpers.cs`<br>`tests/MeshAdapter.Sdk.Tests/Nodes/Extract/GetStreamDataNodeTests.cs`<br>`tests/MeshAdapter.Sdk.IntegrationTests/Nodes/Extract/GetStreamDataNodeIntegrationTests.cs` | `MeshAdapter.Sdk/Nodes/Extract/GetQueryByIdNode.cs` (Helfer)<br>`ServiceCollectionExtensions.cs`<br>`DataPipelineBuilderExtensions.cs`<br>`MeshAdapterPipelineExecutionException.cs`<br>`CLAUDE.md`, `docs/developer-guide.md` |
| **AB#4728**<br>Gaps | `MeshAdapter.Sdk/Nodes/StreamDataGapAnalyzer.cs`<br>`MeshAdapter.Sdk/Nodes/StreamDataGapReport.cs`<br>`tests/MeshAdapter.Sdk.Tests/Nodes/StreamDataGapAnalyzerTests.cs` | `GetStreamDataNodeConfiguration.cs` + `GetStreamDataNode.cs` (Gap-Zweig)<br>`MeshAdapterPipelineExecutionException.cs`<br>`tests/MeshAdapter.Sdk.IntegrationTests/Fixtures/StreamDataFixture.cs` (TimeRange-Archiv)<br>Tests<br>`CLAUDE.md`, `docs/developer-guide.md` |
| **neu**<br>Aggregation | `MeshNodes.Sdk/Extract/AggregateStreamDataNodeConfiguration.cs`<br>`MeshNodes.Sdk/PipelineDataTransferObjects/AggregationColumnDto.cs`<br>`MeshAdapter.Sdk/Nodes/Extract/AggregateStreamDataNode.cs`<br>`tests/MeshAdapter.Sdk.Tests/Nodes/Extract/AggregateStreamDataNodeTests.cs`<br>`tests/MeshAdapter.Sdk.IntegrationTests/Nodes/Extract/AggregateStreamDataNodeIntegrationTests.cs` | `ServiceCollectionExtensions.cs`<br>`DataPipelineBuilderExtensions.cs`<br>`MeshAdapterPipelineExecutionException.cs`<br>`StreamDataNodeHelpers.cs` (Aggregations-Helfer, falls nicht in §5 mitgezogen)<br>`CLAUDE.md`, `docs/developer-guide.md` |

**Abhängigkeiten:** §6 und §7 setzen §5 voraus. `requireGapFree` (§7) setzt zusätzlich den
`StreamDataGapAnalyzer` aus §6 voraus — ohne §6 ist §7 bis auf diese eine Property lieferbar.

---

## 10. Referenzen

* `src/MeshAdapter.Sdk/Nodes/Extract/GetQueryByIdNode.cs` — nächstliegende Vorlage (Stream-Data-Hälfte);
  deckt außerdem Downsampling ab, das hier bewusst nicht nachgebaut wird (§2)
* `src/MeshAdapter.Sdk/Nodes/Load/SaveTimeRangeStreamDataInArchive.cs` — Schreib-Gegenstück
* `octo-construction-kit-engine/src/Runtime.Contracts/StreamData/` — `IStreamDataRepository`,
  `ArchiveSnapshot`, `StreamDataQueryOptions`, `StreamDataAggregationQueryOptions`,
  `StreamDataGroupedAggregationQueryOptions`, `StreamDataRow`
* `octo-construction-kit-engine/src/Runtime.Contracts/Repositories/Query/AggregationColumn.cs`,
  `AggregationFunction.cs`
* `octo-construction-kit-engine/docs/concept-time-range-archives.md` — TimeRange-Archive im Detail
* `octo-construction-kit-engine-mongodb/docs/streamdata-archive-concept.md` — Archiv-Grunddesign
* `docs/developer-guide.md` §Extract Nodes, `docs/test-concept.md`, `docs/integration-test-concept.md`
