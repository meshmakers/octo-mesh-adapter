# Changelog

Notable changes of the OctoMesh mesh adapter. Newest first; entries name their work item.

## [Unreleased]

### Changed

- NuGet packages raised to the newest version within the .NET 10 line (AB#9597): MailKit and MimeKit 5.0.0,
  Microsoft.ML and Microsoft.ML.TimeSeries 6.0.0, AngleSharp 1.10.5, ModelContextProtocol.AspNetCore 2.4.0,
  SSH.NET 2026.0.1, System.Security.Cryptography.Pkcs 10.0.15, and the test tooling (xunit, Testcontainers 4.17.0,
  Microsoft.NET.Test.Sdk, coverlet, FluentAssertions, TimeProvider.Testing 10.11.1). The IMAP requeue tests now
  intercept `IMailFolder.MoveToAsync(IList<UniqueId>, IMoveRequest, ...)` because MailKit 5 made the single-id
  overload an extension method. IronOcr, PdfSharp and QuestPDF stay on their versions (see the PR for the reasons).

### Fixed

- `ApplyChanges@2`: concurrent writers across adapter replicas end in one stored write and a reported conflict
  (AB#9466). A MongoDB WriteConflict (112) is now recognised anywhere in the exception chain (the repository
  wraps it), the transaction is aborted and the write retried up to six times with a short jittered backoff
  (about 0.4 to 0.8 s in total). With `onDuplicateKey: Report` a conflict that outlasts the retries writes the
  flag `true` at `duplicateKeyTargetPath`; without Report it fails the pipeline. Before, the wrapped conflict
  failed the pipeline (a CK method answered `METHOD_HANDLER_FAILED` instead of its declared `CONCURRENT_CHANGE`),
  and exhausted retries ended as a silent success although nothing was written.

### Added

- `DetectStreamDataGaps@1` (AB#9588, PR #129): the missing time ranges `[start, end)` and missing slot counts
  of many series in one windowed stream-data archive over the half-open period `[from, to)`, without a row
  limit (internal batching, per-day COUNT on the storage path of AB#7360, paged row reads). The offset of
  `from`/`to` sets the day boundary; there is no time zone, day start or per-day option.
- `DetectStreamDataGaps@1` validity periods per series (AB#9589): `validityPeriods` (inline list of
  `{rtId, from, to}`) or `validityPeriodsPath` (JSONPath to such objects; the inline list wins). Each period is
  half-open `[from, to)`, `to` unset is an open end; a series may have several, overlapping or touching ones are
  merged. Only the slots inside `[from, to)` AND inside a series' periods are checked: ranges are cut exactly at
  the validity boundaries (so each range can be re-requested as one valid request, e.g. one EDA CR_REQ_PT), a gap
  across a hole between periods becomes two ranges, and the counts cover only those slots. A series without an
  entry is neither checked nor reported (one info line names how many); a path resolving to nothing warns naming
  the path and reports no series; an entry with `from >= to` fails naming the entry. Nothing outside every period
  is read: the per-day COUNT covers only the bins a batch's periods touch, and rows are read only inside the
  periods. Without validity periods the node reads and reports exactly as before.
- Sample method pipeline `docs/examples/slot-booking-method.yaml`: book a slot with a unique index and answer
  the declared method error `CONCURRENT_CHANGE` (AB#9466). Integration tests `SlotBookingConflictTests` run it
  against MongoDB (Testcontainers).

### Compatibility

- AB#9466 changes no System CK model. A unique index belongs in a CK type's first version: adding one to an
  existing type that holds data is a Major change of the CK model (AB#5347).
