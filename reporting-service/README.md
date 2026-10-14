# Launchpad reporting service

This service accepts the structured reports sent by Sweden FIR Launchpad and provides a private maintainer backoffice. It runs as one Node.js 24 process with a dedicated SQLite database. It does not control desktop applications or create GitHub issues.

## Running and testing

Node.js 24.13 or later within the 24.x line is required. Dependencies are locked in `package-lock.json`; SQLite is provided by Node.

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm test
npm start
```

Set `PRIVACY_OPERATOR` and `PRIVACY_CONTACT` before starting the service. The normal entry point refuses to start without them. Tests use temporary databases and synthetic records; they do not contact the production endpoint, enroll a real passkey or send notifications.

The Dockerfile accepts a `NODE_IMAGE` build argument. Resolve the Node 24 base image to an approved digest for production builds and deploy the resulting application image by digest. The image runs as UID/GID 10001 and needs write access only to its dedicated data mount. Package scripts do not run during dependency installation.

| Setting | Purpose |
| --- | --- |
| `PUBLIC_ORIGIN` | Exact HTTPS origin, default `https://launchpad.space.sjolus.org`. Also fixes the passkey relying party. No trailing slash or alternate origins. |
| `PORT` | HTTP listener, default 3000. TLS terminates at the ingress. |
| `DATA_PATH` | Database path, default `./data/reporting.sqlite`; the container uses `/data/reporting.sqlite`. |
| `PRIVACY_OPERATOR`, `PRIVACY_CONTACT` | Public operator name and contact instructions displayed by `/privacy`. Neither is a secret. |
| `TRUSTED_PROXY_CIDRS` | Optional comma-separated final-proxy addresses/ranges. Forwarding headers are ignored by default. |
| `NTFY_ENDPOINT`, `NTFY_TOKEN` | Optional private HTTPS topic and server-side bearer token. Omit both to disable notifications. Store the token in a deployment Secret. |
| `LIMIT_*` | Lower a limit from `src/config.mjs`, using uppercase snake case, for example `LIMIT_MAX_EVENTS=50000`. Invalid values stop startup. Free-space reserve can be increased. Raw event retention cannot be lowered below eight days because event IDs must survive the seven-day retry horizon. |

## Intake and private access

Public intake has four JSON POST endpoints:

| Path | Accepted data | Acknowledgement |
| --- | --- | --- |
| `/api/v1/usage/batches` | Up to 25 typed usage events / 64 KiB | `202` with accepted and duplicate counts |
| `/api/v1/inventory/batches` | Separately consented snapshots of the fixed software/plugin catalogue / 64 KiB | `202` with accepted and duplicate counts |
| `/api/v1/errors/batches` | Up to 25 typed error events / 64 KiB | `202` with accepted and duplicate counts |
| `/api/v1/reports` | One structured manual report / 16 KiB, optionally 20 typed context events | `201` with a stable `LP-…` reference |

`src/schema.mjs` defines the versioned field and enum allowlists. Unknown fields, arbitrary messages, stack traces, paths, credentials, contact fields, attachments and compressed request bodies are rejected. UUIDs identify events, sessions and a local installation; they are not authentication. Versions accept bounded numeric public formats, including four-part EuroScope versions and known numeric prerelease suffixes.

An acknowledgement follows the committed SQLite transaction. Exact retries do not consume quota again; changed content under the same ID returns `409`. Entire batches are validated and admitted together. Quota or rate limits return `429` with `Retry-After`; unavailable storage returns `503`. A failed submission is not acknowledged as stored. Request bytes, concurrency and source-state memory are bounded before database admission.

The public pages are `/privacy` and `/backoffice/`; local static assets are explicitly allowed. `/auth/*` handles passkey ceremonies. Every `/api/admin/*` read requires an authenticated session, and changes require the exact origin and JSON content type. There is no public report or support-ID lookup. Secure, HttpOnly, SameSite cookies have a 15-minute idle limit and one-hour absolute limit. Support deletion additionally requires a sign-in within the preceding five minutes and an exact confirmation.

The backoffice opens on usage: active and returning installations, Launchpad sessions and versions, explicit application launches, feature use, GNG package sources, setup routes and display preferences. Inventory uses its own sample, including installations that permit inventory without general usage. Latest snapshots show known application/plugin versions and distinguish missing metadata from unavailable observations. Reports, grouped errors, support lookup and service health remain separate views. The report inbox initially includes all retained dates; inactive unresolved reports are marked for review after twelve months.

Statistics describe received, unverified submissions from consenting installations, not the number of controllers or time spent controlling. A support ID is not a person. Version distributions use each installation's latest observation; daily distinct counts overlap. Comparisons are available only where the retained history covers the requested windows. The 30-day raw retention cannot establish a previous-30-day comparison. Long-term identifier-free aggregates count events, not unique installations. Missing completion remains unknown, and error-only reports never become the numerator of a usage completion rate.

Desktop usage, inventory and error permissions are independent. Inventory requests carry only fixed component IDs, status, source and validated public versions. They share the bounded automatic storage and usage admission budget but are excluded from usage metrics and manual-report context. Turning inventory off discards queued snapshots and events carrying application versions, preserving immutable retry payloads for the remaining events. New events omit application versions; Launchpad's own version remains basic context. Public intake cannot prove that a submitted consent or observation is genuine.

## Storage and retention

The default automatic budget is 50,000 events / 50 MiB accepted payload per UTC day, with 600 events per support ID/day. Retained automatic data stops at 100,000 records or 512 MiB of payload. Usage admission stops at 80% of the shared automatic allowance, leaving the remainder for errors. Manual reports have separate limits of 100/day, 10 per support ID/day and 1,000 retained reports / 16 MiB. Source, minute, concurrent-request and total incoming-byte limits apply independently.

Quota counters and ID deduplication are durable and updated in the same transaction as records. New UUIDs cannot create unlimited auxiliary rows: daily counters, minute counters, authentication state and audit records each have a hard cap. Failure of expiry does not bypass the admission limits. Unresolved reports are never discarded to make room.

Raw events expire after 30 days. Closed reports expire 90 days after closing; reopening cancels that expiry. One-minute housekeeping performs bounded work and reports its latest success/failure and expired-event backlog. Authentication challenges, sessions, enrollment invitations and old audit records are pruned separately. Deletion removes the selected support ID's raw records and linked audit targets; it cannot undo counts already stripped of identifiers.

Daily historical aggregates contain enum dimensions, bounded Launchpad version cohorts and counts, without support/session/operation IDs or exact event times. They expire after 365 days and are limited to 20,000 groups / 8 MiB of dimension data. At most 64 distinct release cohorts per application are retained; further releases join `other`. At capacity, the historical group is omitted while the raw accepted event remains available. The backoffice exposes a cumulative omitted-count indicator, independent of its selected filters. Neither aggregate cardinality nor user-supplied versions become Prometheus labels.

SQLite uses `DELETE` journals, `synchronous=FULL`, in-memory temporary tables and a 768 MiB database page ceiling. A 128 MiB filesystem free-space reserve stops admission. These are application safeguards, **not a filesystem quota**. The database, indexes and a possible rollback journal must fit an independently enforced data volume; allow at least 2 GiB for the default ceiling, including housekeeping/recovery headroom. Deletion makes pages reusable and does not promise that the database file shrinks.

## Deployment

The **Reporting service** workflow tests and builds pull requests, and publishes `ghcr.io/sjolus/launchpad-reporting:git-<full commit SHA>` after successful `main` checks. To publish an approved branch commit, run the workflow on that branch with **publish** enabled. The Node 24 base is resolved once to an official registry digest and reused for publication; existing commit tags are never overwritten by this workflow. The job summary gives the application image digest for deployment. Images target Linux x64; publication does not deploy the service. After the first publication, the package owner must set its GHCR visibility to **Public** for anonymous pulls.

`deploy/example.yaml` is a single-replica `Recreate` deployment with a read-only root filesystem, unprivileged user, resource limits and a network policy. It references an existing data claim instead of creating a PVC whose requested size might not be enforced. Replace its deliberately unresolved image/claim/namespace/TLS values in the hosting repository. Do not scale this service horizontally: in-memory request limits and notification cooldowns assume one process even though SQLite admission transactions are serialized.

Before public intake is enabled, the operator must establish the actual filesystem quota, backup quota and retention, resource/log ceilings, valid HTTPS routing, owner enrollment and published operator/contact details. Test these against synthetic data on isolated staging storage. Source manifests do not establish production readiness, and application readiness does not verify the backup service or disk allocation.

The example uses a Traefik IngressRoute with explicit public paths and route-level access-log/tracing suppression. `/metrics`, `/healthz` and `/readyz` are deliberately absent from the public route. Restrict them to probes and the monitoring network. Check support for the configured CRD fields on the installed ingress version; see the [Traefik route reference](https://doc.traefik.io/traefik/reference/routing-configuration/kubernetes/crd/http/ingressroute/).

Audit the entire connection path, including rejected/unmatched routes, proxy defaults, collectors and crash logging. Do not persist raw source addresses, identifying headers, query strings or request bodies. The app has no request-body/access logger; this cannot enforce an upstream provider's logs. Set small, rotated container-log limits at the runtime, such as two 10 MiB files per container; `ephemeral-storage` is not a replacement for runtime log rotation. Keep trace/debug request capture disabled. The source-rate limiter retains only short-lived keyed hashes in bounded process memory. Only trust a final proxy whose forwarding behavior and source ranges have been verified.

The example network policy denies outbound connections. Optional ntfy notifications require a separately reviewed rule for DNS and the private notification endpoint. Notifications contain fixed messages and a backoffice link, never support IDs or report details. They have one request in flight, a five-second timeout and a thirty-minute per-reason cooldown. Error-volume alerts are counts, not a failure-rate estimate. Service metrics use fixed stream/rejection labels and physical-store health; counters reset when the process restarts.

## Owner enrollment and recovery

After the initialized database is available, run the operator-only command from a trusted interactive terminal:

```sh
node scripts/enroll-owner.mjs --database /data/reporting.sqlite
```

It displays a one-time code valid for ten minutes. Open the HTTPS backoffice and complete enrollment yourself. Do not put that code in a URL, CI output, chat transcript or deployment log. The command refuses to print a code to noninteractive stdout. For an operator-controlled secret pipe, `--code-from-stdin` accepts a securely generated 32-byte base64url code without printing it.

There is one owner credential. Lost-passkey recovery uses the same command with `--replace-credential`, explicitly revoking the old credential, challenges and sessions. Ordinary startup and deployment never enroll or replace the owner. There is no public reset route or desktop-held admin secret.

## Backups and restore

`scripts/backup.mjs` creates a consistent SQLite snapshot rather than copying an open database file. It fixes a read snapshot for the duration, so a large backup may briefly delay intake writes. The helper validates the size and free-space reserve before copying, uses one exclusive backup lock, verifies the result, then promotes it. An interrupted or failed new copy leaves the previous verified snapshots within their age limit.

```sh
node scripts/backup.mjs --database /data/reporting.sqlite --directory /backup/launchpad
```

Use a dedicated, independently quota-enforced backup mount, private to the operator. Defaults retain at most **two completed snapshots plus one temporary copy** and remove snapshots older than two days whenever the helper runs. Reserve at least 3 GiB for the default 768 MiB per-file ceiling and free-space headroom. Schedule it daily using the hosting platform, with failure/staleness alerts and independent backup expiry so a failed schedule cannot silently extend retention. Snapshots, replicas and external backup services need their own matching count/age/byte policies. A stale lock or unexpected file makes the helper stop for inspection; it does not automatically remove arbitrary files or accumulate retry copies. Health intentionally reports backup verification as an operator responsibility.

Run the independent expiry and freshness check hourly on that same backup mount, offset from the daily snapshot job:

```sh
node scripts/check-backups.mjs --directory /backup/launchpad
```

It removes only recognized regular snapshots older than 48 hours and exits unsuccessfully if no snapshot remains or the newest is older than 30 hours. It shares the backup lock and preserves existing locks, pending copies, links and unexpected files for inspection. Inventory is bounded to three completed files (including a possible interrupted rotation), each at most 768 MiB. Logs contain counts, sizes, timestamps and status only. Monitor failed or missed hourly Jobs separately; this helper cannot expire files while its own schedule is stopped.

A restore stays offline until expiry and deletion requests have been reapplied. Maintain the reviewed support-ID deletion record separately from these rotating database snapshots, with operator-only access and bounded retention covering the backup window. If that record is unavailable, do not reopen an older database containing potentially deleted records.

Copy the selected snapshot to an isolated, quota-enforced staging location named `restore-staging.sqlite`. Provide an explicitly reviewed JSON array of support UUIDs deleted since the snapshot; an empty array means the operator has verified there were none. The deletion file is limited to 512 KiB / 10,000 IDs.

```sh
node scripts/prepare-restore.mjs --offline-staging-copy --database /restore/restore-staging.sqlite --verified-deletions /restore/deleted-support-ids.json
```

The helper checks integrity, deletes those records, reapplies expiry and clears all old credentials, sessions, challenges, enrollment invitations and audit records. Explicit owner enrollment is required again. Because the snapshot cannot know the quota consumed after it was taken, fresh intake remains blocked for that UTC day and resumes the next day. Keep public ingress closed while promoting the prepared database, enrolling the owner and checking health. The helper never replaces the live database or reopens ingress. Restore and deletion-record handling need an operator-tested deployment procedure; neither a successful snapshot nor the unit tests prove the hosting platform can recover.
