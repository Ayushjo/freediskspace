#!/bin/bash
# Runs the opt-in live tests against this Mac: real diskutil and tmutil, a
# Time Machine snapshot created and deleted, the real mount table.
# Run them on their own, never as part of the full suite: creating a snapshot
# or running diskutil while the timing-sensitive staging tests run delays
# FSEvents and fails those (measured 2026-10-22: 6 failures mixed, 0 alone).
set -euo pipefail
cd "$(dirname "$0")/.."
DISKMAP_LIVE_TESTS=1 exec scripts/test.sh \
  --filter "LocalSnapshotsTests|SpaceAccountTests|TimeMachineSnapshotRemovalTests|ForeignMountsTests|SystemStorageCatalogTests|DeveloperStorageCatalogTests" "$@"
