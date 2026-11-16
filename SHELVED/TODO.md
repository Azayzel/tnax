# TNAX Deferred Work

[Plan](../PLAN.md) | [Decisions](../DECISIONS.md)

Parked ideas, not MVP commitments. Active work and release blockers belong in the plan. An unchecked item is not a promise to build it.

## After Reliable Backup

- [ ] **S01 - Remote gallery and thumbnails.** Revisit after verified backups; assess bandwidth and F4-210 load first.
- [ ] **S02 - Desktop folder monitoring and launch at login.** Revisit after manual Windows imports and durable retries work.
- [ ] **S03 - Discovery and Wake-on-LAN.** Revisit after explicit endpoints are dependable; discovery must not become a connection requirement.
- [ ] **S04 - Android and macOS clients.** Revisit after the Windows/iPhone family pilot.
- [ ] **S05 - Shared albums and guest links.** Revisit only with a separate permissions and exposure review.
- [ ] **S06 - Cross-device deduplication.** Revisit when measured storage duplication justifies it; same-device retry safety is already MVP scope.
- [ ] **S07 - Full Photos library reconstruction.** Albums, favorites, and edit history need a separate fidelity contract; original media and Live Photo pairing are MVP scope.
- [ ] **S08 - Client-side encryption.** Revisit with a key-recovery plan and an explicit tradeoff against NAS-side Photos indexing.
- [ ] **S09 - Off-site second copy.** Revisit after local recovery is proven; choose retention, cost, and encryption deliberately.
- [ ] **S10 - Large-library throughput.** Revisit the prototype's 2 GiB staging and 24-asset scan limits after interruption and recovery tests pass.
- [ ] **S11 - QR/deep-link pairing.** Replace manual setup-file selection after native pairing import and trust lifecycle tests pass on both platforms.

## Not Planned

- Two-way deletion mirroring or automatic "free up phone space."
- Direct Internet exposure of NAS services or a TLS-validation bypass.
- A custom replacement for TOS administration, RAID management, or TerraSync.
- AI indexing, facial recognition, or an app-hosting backend unrelated to dependable backup.

## Promotion Rule

Promote one item into [PLAN.md](../PLAN.md) with a concrete need, bounded scope, and an exit test. Record architecture changes in [DECISIONS.md](../DECISIONS.md); mark the original item as promoted with its phase reference. Do not mix parked features into an active milestone silently.
