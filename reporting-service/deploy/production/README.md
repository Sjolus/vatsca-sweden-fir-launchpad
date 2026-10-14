# Launchpad reporting hosting

This overlay runs only in `launchpad-reporting`. It creates a dedicated Longhorn
storage class, two fixed-size volumes and namespace resource ceilings. It does
not change shared ingress, storage classes or other applications.

Use the image published by the Reporting service workflow and override the
Kustomize image with its verified digest. The placeholder tag deliberately cannot
start a production container. Keep the ArgoCD Application pinned to the reviewed
Git revision and image digest; deployment is deliberate, not an image-tag poll.

The data filesystem is 2 GiB. The separate backup filesystem is 3 GiB, with two
replicas of each on Longhorn. Automatic platform backup jobs are excluded; the
daily SQLite job retains two snapshots for at most two days. The independent
hourly check expires old snapshots and fails on missing/stale backups. Both jobs
must be monitored. These backups share the cluster's failure domain and are not
off-site disaster recovery. Do not add unconstrained NAS copies or snapshots.

After provisioning, set `spec.snapshotMaxCount: 2` on each of the two associated
Longhorn Volume resources. Longhorn 1.11 does not apply this field from a storage
class parameter. Verify those limits and the disabled default recurring-job group
on the actual volumes before enabling intake; never patch unrelated volumes.

The application runs as UID/GID 10001 with a read-only container filesystem and
no service-account token. It has no configured notification service or outbound
work. The NetworkPolicy is a desired boundary; enforcement depends on the cluster
CNI. A Flannel-only installation does not enforce it. Existing kubelet log rotation
is 10 MiB per file with five files per container; application logs exclude request
content and client addresses.

The HTTPS router suppresses access logs, tracing and metrics for allowed and
unknown paths. Health, readiness and metrics stay off the public route. The shared
HTTP redirect and certificate store remain unchanged. The bootstrap starts with
the sibling `enrollment` overlay, which excludes public intake. After storage,
backup, routing and enrollment checks, change the ArgoCD source path to
`reporting-service/deploy/production` and sync the reviewed revision. Production
opens only the four exact intake paths. Return to the enrollment overlay before
an offline restore or other maintenance that requires intake to remain closed.

No trusted proxy ranges are assumed: forwarded addresses are ignored and the
service's conservative source limit is shared by proxy traffic. Add a narrowly
verified proxy range only when its forwarding behavior and isolation are proven.

Owner enrollment is performed in the operator's own interactive terminal:

```sh
kubectl -n launchpad-reporting exec -it deployment/launchpad-reporting -- \
  node scripts/enroll-owner.mjs --database /data/reporting.sqlite
```

Paste the short-lived code directly into the HTTPS backoffice and create the
passkey in the browser. Do not paste the code into a chat, URL, deployment log or
commit. Ordinary deployment must never run enrollment or replace credentials.
See the service README for offline restore and deletion-reapplication requirements.
