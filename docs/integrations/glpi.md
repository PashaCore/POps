# GLPI export (design note)

**Status: design, not implemented.** Nothing on this page exists in POps today. It describes how POps could send
its inventory and helpdesk tickets to [GLPI](https://glpi-project.org/), the open-source IT asset management and
service desk many schools and public institutions already run. Where options are listed, the choice has not been
made. Comments are welcome in an issue or a discussion.

## Why

Schools that use GLPI keep their asset register, loans, contracts and service desk there. POps knows the current
state of each Windows PC (hardware, installed software, who opened which ticket from the tray) because its agent
reports it. Today an administrator who wants that data in GLPI has to export a CSV from **Raporlar** and import it
by hand, or install a second inventory agent on every PC. An export from the POps server to GLPI's REST API would
keep GLPI current without a second agent.

## Scope

In scope:

- **Inventory:** devices, their hardware and installed software, POps → GLPI.
- **Helpdesk tickets:** tickets opened from the tray or the panel, with their public replies, POps → GLPI.

Not in scope for a first version:

- Importing anything from GLPI into POps (devices, users, software catalogue).
- Licences, Windows Update state, tasks, deployments, DNS policy events, quarantine and remote-control sessions
  (see [What stays in POps](#what-stays-in-pops)).
- Changing PCs from GLPI. POps stays the only path to act on a managed PC.

## Sync direction

| Data | Direction | Owner of the truth |
| --- | --- | --- |
| Device identity and hardware | POps → GLPI | POps (what the agent reports) |
| Installed software | POps → GLPI | POps |
| Fields POps does not know (inventory number, location details, contract, user in charge, comments) | none | GLPI |
| Tickets: creation, subject, description, device link | POps → GLPI | POps |
| Tickets: status and replies after export | **Option A:** none (POps keeps its own copy). **Option B:** GLPI → POps for status and public follow-ups, so the tray shows replies written in GLPI. | A: each system for its own copy. B: GLPI. |

A first version would implement inventory and Option A for tickets. Option B needs polling GLPI or a webhook from
GLPI and a decision on who answers students: it is listed under [Open questions](#open-questions).

## Transport

GLPI offers two ways in. Both are candidates; the choice depends on testing against the GLPI versions schools run.

1. **REST API** (`apirest.php`). Item by item: find or create the `Computer`, then its components, software
   versions and the link table entries. POps decides matching and updates itself. This is the only way to create
   tickets and follow-ups.
2. **Native inventory** (GLPI 10 and later accept inventory documents over HTTP, in GLPI's JSON
   [inventory format](https://github.com/glpi-project/inventory_format)). POps would build one JSON document per
   device, and GLPI's own import and dictionary rules handle matching and deduplication, as they do for the GLPI
   agent. Authentication and rate limits of that endpoint must be checked for the targeted versions.

Working assumption: native inventory for devices and software if it fits, REST API for tickets. GLPI 11 adds a new
high-level API; whether the legacy REST API stays available in the versions schools use must be checked before
implementation.

## Authentication

- A dedicated GLPI user for POps with a profile that allows only what the export needs: create and update
  computers, their components and software links, create tickets and follow-ups, read the location tree. No
  administrator rights.
- An **API client** in GLPI (Setup → General → API) with its **App-Token**, restricted to the POps server's address
  where GLPI allows it.
- The user's **API token** (user token), not a password.
- A session per sync run: `GET apirest.php/initSession` with the headers `App-Token: <app token>` and
  `Authorization: user_token <user token>`; later calls send `Session-Token` and `App-Token`; `killSession` at the
  end. The session token is kept in memory only.
- `GLPI_URL`, `GLPI_APP_TOKEN` and `GLPI_USER_TOKEN` live in the backend `.env`, like the SMTP credentials. They
  are never stored in the database, never sent to the panel and never written to a log.
- HTTPS only, with certificate checks. GLPI usually runs on the school network, so its address will often be
  private: like webhooks, the export would refuse private addresses unless an explicit setting allows them, and
  would pin the checked address and not follow redirects ([`docs/security.md`](../security.md#notifications)).

## Mapping

Field names on the POps side are database columns ([`docs/database.md`](../database.md)). GLPI item types and
fields must be confirmed against the targeted GLPI version.

### Device → `Computer`

| POps | Source | GLPI | Notes |
| --- | --- | --- | --- |
| Hardware ID (`HW-…`) | `clients.pc_name` | stored in a POps link table, not in GLPI | The stable key on the POps side; see [Matching](#matching-and-conflicts). |
| Host name | `clients.hostname` | `Computer.name` | |
| BIOS serial number | `clients.dna_bios` | `Computer.serial` | Used for matching. |
| SMBIOS UUID | `clients.dna_uuid` | `Computer.uuid` | Used for matching. |
| Lab | `clients.lab_name` | `Location` | A configurable mapping from lab to an existing GLPI location; unmapped labs are not created automatically. |
| Operating system | `hw_inventory.os_version` | `Item_OperatingSystem` (`OperatingSystem`, `OperatingSystemVersion`) | Free text in POps; needs parsing. |
| Processor | `hw_inventory.cpu` | `Item_DeviceProcessor` / `DeviceProcessor` | |
| Memory | `hw_inventory.ram` | `Item_DeviceMemory` / `DeviceMemory` | Text in POps; converted to MB. |
| Graphics card | `hw_inventory.gpu` | `Item_DeviceGraphicCard` / `DeviceGraphicCard` | |
| Motherboard | `hw_inventory.motherboard` | `Item_DeviceMotherboard` / `DeviceMotherboard` | |
| Disks | `hw_inventory.disk_info` | `Item_DeviceHardDrive` / `DeviceHardDrive` | Format of `disk_info` to be checked. |
| IP and MAC address | `hw_inventory.ip_address`, `hw_inventory.mac_address` | `NetworkPort` with its name and IP address | |
| Last contact | `clients.last_seen` | not exported | Changes every few seconds; GLPI is not a live view. |
| Signed-in user | `clients.logged_user` | **not exported** | Personal data; see [Privacy](#privacy). |
| Display name, agent version, capabilities, quarantine state | `clients`, `agent_versions` | not exported | POps operating state. |

### Installed software

| POps | Source | GLPI |
| --- | --- | --- |
| Program name | `device_software.name` | `Software.name` |
| Publisher | `device_software.publisher` | `Software.manufacturers_id` (`Manufacturer`) |
| Version | `device_software.version` | `SoftwareVersion.name` |
| Installed on | `device_software.pc_name` | `Item_SoftwareVersion` (computer ↔ version) |
| Install date | `device_software.install_date` | `Item_SoftwareVersion.date_install` |

A program removed from the PC removes only the `Item_SoftwareVersion` link that POps created. POps never deletes
`Software` or `SoftwareVersion` items: other computers or licences in GLPI may use them.

### Helpdesk ticket → `Ticket`

| POps | Source | GLPI | Notes |
| --- | --- | --- | --- |
| Subject | `tickets.subject` | `Ticket.name` | |
| Description | `tickets.body` | `Ticket.content` | Prefixed with "Opened in POps from the tray / the panel". |
| Category | `tickets.category` (`donanim`, `yazilim`, `ag`, `yazici`, `hesap`, `diger`) | `Ticket.itilcategories_id` | Configurable mapping to existing GLPI categories. |
| Priority | `tickets.priority` (`low`, `normal`, `high`) | `Ticket.priority` | Proposed: low → 2, normal → 3, high → 4. |
| Status | `tickets.status` (`open`, `in_progress`, `waiting`, `resolved`, `closed`) | `Ticket.status` | Proposed: new, processing (assigned), pending, solved, closed. Numeric values to be confirmed. |
| Opened at | `tickets.created_at` | `Ticket.date` | |
| Device | `tickets.pc_name` | `Item_Ticket` (link to the exported `Computer`) | Only when the device has been exported. |
| Reporter | `tickets.reporter` | **not exported by default** | The Windows user name of a student. Optional, off by default. |
| Public replies | `ticket_messages` with `internal = false` | `ITILFollowup` (`is_private = 0`) | |
| Internal notes | `ticket_messages` with `internal = true` | not exported by default | Optional, as private follow-ups. |

## Matching and conflicts

- POps keeps a link table (`pc_name` ↔ GLPI `Computer` id, last export time, last error). Once linked, a device is
  always updated through its link, never searched again.
- An unlinked device is matched by BIOS serial number, then SMBIOS UUID. Exactly one match: link it. No match:
  create the computer. More than one match: **do not guess**; list the device on **Sistem** for an administrator to
  link or skip.
- POps writes only the fields it owns (the tables above). It never empties or overwrites other fields, never
  deletes a `Computer`, and never changes the GLPI location of a computer whose lab has no mapping.
- A device deleted in POps is left in GLPI as it is; the link is removed. Retiring hardware is a GLPI decision.
- A computer deleted or moved to the trash in GLPI is not recreated automatically; the link is marked broken and
  shown on **Sistem**.
- Tickets are exported once and then belong to GLPI (Option A). With Option B, GLPI wins for status, and replies are
  appended in time order on both sides.

## Running the export

- Off by default. A superadmin turns it on; the change is written to the audit log.
- Runs on a schedule (for example nightly) and on demand. Only changed devices and new or changed tickets are sent,
  in small batches with a time limit per request.
- Optional, time-limited and never fatal, like every outbound call of the server: a GLPI that is down or slow does
  not affect agents or the panel. The last run, its counts and its errors are shown on **Sistem**; repeated failures
  create a notification.
- Could become a module (`glpi`) in the existing module system, with an organisation-wide switch.

## Privacy

Sending data to GLPI is a new transfer of personal data and must be named in the school's KVKK notice
([`kvkk-aydinlatma.md`](../kvkk-aydinlatma.md)). The export follows data minimisation:

- no screen images, no foreground program, no signed-in user name, no event log content;
- the reporter of a ticket and internal notes only if the school turns them on;
- nothing leaves the school network unless GLPI itself is outside it.

## What stays in POps

Tasks and their output, deployments and packages, remote-control sessions and previews, quarantine and bypass codes,
the hash-chained audit log, capabilities and modules, DNS policy events, Windows Update details, licences (GLPI has
its own licence management; two places for the same seat count would drift), device secrets and enrollment tokens,
notifications.

## Testing

- Unit tests for the mapping functions (text parsing of memory, disks and operating system).
- An integration test against a GLPI container in CI, if a pinned image of a supported version is practical;
  otherwise recorded API responses.
- A dry-run mode that shows what would be created or changed without writing to GLPI.

## Open questions

1. Native inventory or REST API for devices (see [Transport](#transport)), and which GLPI versions to support.
2. Option A or B for ticket status and replies.
3. Whether to export the ticket reporter at all, and how to map it to a GLPI requester.
4. Whether exported computers should carry a POps link (for example in the comment field) for administrators.
5. How to handle labs that map to no GLPI location.
