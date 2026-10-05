# GLPI export

POps can send its inventory and helpdesk tickets to [GLPI](https://glpi-project.org/), the open-source IT asset
management and service desk many schools and public institutions already run. The first version is implemented
(`Backend/pops/glpi.py`, **Sistem** → **Entegrasyonlar**); [What is implemented](#what-is-implemented) lists it and
the choices it made. The rest of the page is the design it follows; where options are listed and the first version
did not need a choice, the choice is still open. Comments are welcome in an issue or a discussion.

## What is implemented

- **Transport:** GLPI's REST API (`apirest.php`) for everything: `initSession` (headers `App-Token` and
  `Authorization: user_token …`), then `Session-Token` on every call, `changeActiveEntities` when an entity is set,
  and `killSession` at the end of each run. Tested against a fake GLPI that records the calls
  (`Backend/tests/test_glpi.py`); testing against a real GLPI 10 is still to do.
- **Settings** are made by a superadmin on **Sistem** → **Entegrasyonlar** and stored in `global_settings` (`glpi_*`):
  on/off, the GLPI address, the app token and the user token (both **encrypted** with the same key as the 2FA
  secrets, never returned by the API or written to a log or the audit log), the entity, the interval (once a day,
  every 12 or 6 hours, or manual only), what to send (computers, installed software, tickets, the reporter's name),
  the date from which tickets are sent, and the lab → GLPI location mapping. This replaces the `.env` variables of
  the design below: a school can set it up from the panel, and the tokens still never reach the browser. When the
  address changes, the saved tokens must be entered again, so they are never sent to another host by themselves;
  **Bağlantıyı sına** with another address uses only the tokens typed in the form.
- **Off by default.** While it is off nothing is sent, not even by **Şimdi eşitle**. Turning it on, changing a
  setting (the names of the changed settings, not their values) and starting a sync by hand are audit-logged.
- **Address guard:** like the notification webhook, the GLPI host is resolved and every address must be public,
  unless `GLPI_ALLOW_PRIVATE=1` is set in `.env` (the usual case: GLPI on the school network). The connection is
  pinned to the checked address and redirects are not followed. HTTPS with certificate checks is required;
  `GLPI_CA_FILE` adds the school's own certificate authority. Plain `http://` is accepted only when
  `GLPI_ALLOW_PRIVATE=1` and every address of the host is private.
- **Matching** as designed below, with the link table `glpi_links` (migration `0029`): a linked device is updated
  through its link; an unlinked one is matched by BIOS serial number, then SMBIOS UUID (placeholder serials such as
  "To be filled by O.E.M." are not used); a computer already linked to another POps device is not linked twice; one
  match links it, none creates it, several are listed on **Sistem** and nothing is guessed. Searches are anchored
  (`^…$`) and use GLPI's stored form of `&`, `<` and `>`; responses are read unsanitized
  (`X-GLPI-Sanitized-Content: false`). Computers in GLPI's trash are not matched. A computer that GLPI no longer has
  (an update fails and reading it answers 404) is marked broken and not created again; an update GLPI refuses for
  another reason (rights, validation) is an item error and is retried. **Bağlantıyı unut** clears a link so the next
  run looks the device up again. A device deleted in POps loses its link and stays in GLPI.
- **Computers:** `name` (host name), `serial`, `uuid` and, for a lab that has a mapping, `locations_id`; the entity on
  creation. Fields without a value are not sent, so they do not empty GLPI's value. Only changed devices are sent
  (a hash of the sent fields is kept).
- **Installed software:** `Software` (found by name, created with its `Manufacturer` if missing), `SoftwareVersion`
  (found under the software, created if missing) and `Item_SoftwareVersion` with `date_install`, created in batches
  of 50. A version already attached to the computer (by the GLPI agent or by hand) is not added again and does not
  become POps's. A partly accepted batch (`207 ERROR_GLPI_PARTIAL_ADD`) records the links GLPI created. A program
  removed from the PC removes only the link POps created (a link already gone from GLPI is simply forgotten); POps
  never deletes a `Software` or `SoftwareVersion`. A device's list is sent again only when it changed or a previous
  run did not finish it.
- **Tickets:** option A. Tickets opened from the configured date (by default the day ticket export was turned on)
  are sent once as `Ticket` (subject, text with "POps'ta tepsiden / panelden açıldı", priority low/normal/high →
  2/3/4, status open/in_progress/waiting/resolved/closed → 1/2/4/5/6, date) and linked to the device's computer
  with `Item_Ticket`. Public replies, also those written later in POps, are added once each as `ITILFollowup`
  (`is_private = 0`); internal notes are not sent. A reply GLPI refuses (for example on a ticket closed there) is
  not retried. The reporter's name is written into the ticket text only when
  that option is on; it is not mapped to a GLPI requester.
- **Runs:** in the background, from the scheduler when the interval is due, or with **Şimdi eşitle**; one run at a
  time (database advisory lock), each request limited to 15 seconds and a run to 10 minutes (what is left goes in
  the next run). A `POST` is never retried after a dropped connection, so a request GLPI did process cannot create a
  copy; the run stops and the next one continues from the link table. A run still going when the server stops is
  cancelled and continues next time. A run that cannot connect stops with a clear message ("GLPI kullanıcı jetonunu kabul etmedi …");
  an error on one item is counted and the run goes on. The last run, its counts and errors, the linked counts and
  the unmatched devices are shown on **Sistem**; after three failed runs in a row a notification is raised
  (`glpi_failed`).
- **Not in the first version:** hardware components (processor, memory, disks, graphics card, motherboard), the
  operating system and network ports; ticket categories; option B; a dry run. GLPI's native inventory format has
  not been tried.

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
- The design placed `GLPI_URL`, `GLPI_APP_TOKEN` and `GLPI_USER_TOKEN` in the backend `.env`. The implementation
  keeps them in the database instead, with the tokens encrypted, so they can be set from the panel; they are still
  never sent to the panel and never written to a log (see [What is implemented](#what-is-implemented)).
- HTTPS only, with certificate checks. GLPI usually runs on the school network, so its address will often be
  private: like webhooks, the export would refuse private addresses unless an explicit setting allows them, and
  would pin the checked address and not follow redirects ([`docs/security.md`](../security.md#notifications)).

## Mapping

Field names on the POps side are database columns ([`docs/database.md`](../database.md)). GLPI item types and
fields must be confirmed against the targeted GLPI version.

### Device → `Computer`

| POps | Source | GLPI | Notes |
| --- | --- | --- | --- |
| Hardware ID (`HW-…`) | `clients.pc_name` | stored in the POps link table `glpi_links`, not in GLPI | The stable key on the POps side; see [Matching](#matching-and-conflicts). No GLPI field (such as the inventory number or the comment) is used for it: those belong to GLPI. |
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

1. Native inventory or REST API for devices (see [Transport](#transport)), and which GLPI versions to support. The
   first version uses the REST API; it has to be tried against the GLPI 10 and 11 versions schools run.
2. Option A or B for ticket status and replies. The first version is option A.
3. How to map the ticket reporter to a GLPI requester. The first version only writes the name into the text, and
   only when the school turns it on.
4. Whether exported computers should carry a POps link (for example in the comment field) for administrators. The
   first version writes nothing into GLPI's own fields.
5. How to handle labs that map to no GLPI location. The first version leaves their location alone.
