# Changelog

Notable changes of the OctoMesh mesh adapter. Newest first; entries name their work item.

## [Unreleased]

### Fixed

- `ApplyChanges@2`: concurrent writers across adapter replicas end in one stored write and a reported conflict
  (AB#9466). A MongoDB WriteConflict (112) is now recognised anywhere in the exception chain (the repository
  wraps it), the transaction is aborted and the write retried up to six times with a short jittered backoff
  (about 0.4 to 0.8 s in total). With `onDuplicateKey: Report` a conflict that outlasts the retries writes the
  flag `true` at `duplicateKeyTargetPath`; without Report it fails the pipeline. Before, the wrapped conflict
  failed the pipeline (a CK method answered `METHOD_HANDLER_FAILED` instead of its declared `CONCURRENT_CHANGE`),
  and exhausted retries ended as a silent success although nothing was written.

### Added

- Sample method pipeline `docs/examples/slot-booking-method.yaml`: book a slot with a unique index and answer
  the declared method error `CONCURRENT_CHANGE` (AB#9466). Integration tests `SlotBookingConflictTests` run it
  against MongoDB (Testcontainers).

### Compatibility

- AB#9466 changes no System CK model. A unique index belongs in a CK type's first version: adding one to an
  existing type that holds data is a Major change of the CK model (AB#5347).
