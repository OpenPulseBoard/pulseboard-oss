# Runbook: TLS certificate rotation

**Scope:** Routine and emergency rotation of TLS material across the
PulseBoard deployment — public certs, internal mTLS certs, and the
private-PKI intermediate / root.

See  for the trust-zone topology
this runbook operates against.

---

## What rotates, how often, by what

| Material               | Lifetime | Rotated by                       | Action required |
|------------------------|----------|----------------------------------|-----------------|
| Public leaf certs      | 90 d     | cert-manager + Let's Encrypt     | None — automatic at T-30 d |
| Internal mTLS leaves   | 30 d     | cert-manager + internal CA       | None — automatic at T-7 d |
| Internal intermediate  | 18 mo    | This runbook (planned)           | See *Intermediate rotation* |
| Internal root CA       | 10 y     | This runbook (planned)           | See *Root rotation* |
| Cloud-provider CA pins | n/a      | Renovate PR against `ConfigMap`  | Review + merge |
| Cluster KEK (data)     | n/a      | `PULSE_MASTER_KEY` rewrap        | Out of scope — see Phase 6 #4 |

## Routine leaf rotation (automatic)

cert-manager handles this. The only operator obligation is to keep the
alerts green:

- `CertificateNotReady` for > 1 h → page on-call.
- `CertificateExpiringSoon` (T-14 d, T-3 d) → ticket.

Verify any time with:
```
kubectl get certificate -A
kubectl describe certificate <name> -n <ns>
```

## Intermediate rotation (every 18 months, planned)

The intermediate CA is the trust anchor every workload pins. Rotation
uses **dual-issuer overlap** so no pod ever sees an empty trust bundle.

1. Generate the new intermediate offline, signed by the existing root
   (HSM ceremony, 2-person rule).
2. Publish it as `ClusterIssuer/pulseboard-internal-ca-next` alongside
   the current one. Both issuers are valid; trust bundle now contains
   both.
3. Re-issue every workload leaf so it chains to the new intermediate:
   bump the `Certificate` resource's `issuerRef` to `-next`, let
   cert-manager roll the secret, restart pods via a rolling
   deployment.
4. Wait **at least one leaf lifetime (30 d)** so any cached chain in
   long-lived connections has cycled.
5. Remove the old intermediate from the trust bundle. The old issuer
   stays defined but is no longer referenced.
6. After **6 months** of no incidents, delete the old intermediate
   resource entirely.

## Root rotation (decennial or emergency)

Root rotation is **always treated as an incident-class change**, even
when planned. Same dual-trust overlap pattern, but the overlap window
is **12 months**, not 6, and the cutover is gated by a tabletop
exercise plus a staging dry-run.

1. HSM ceremony to generate the new root (2-person rule, recorded).
2. Cross-sign the existing intermediate with the new root. Distribute
   the new root to every trust bundle (workloads, ingress, customers
   with pinned chains — communicate ≥ 90 d in advance).
3. Issue a new intermediate from the new root; transition workloads
   per the *Intermediate rotation* procedure.
4. After the overlap window, remove the old root from trust bundles
   and archive the old root key offline.

## Emergency rotation (compromise suspected)

Triggered by any of:

- Confirmed leak of a private key file.
- HSM tamper alarm.
- Unexpected certificate issued by our CAs (CT log surprise).
- Compliance directive (e.g., a CA distrust event).

Sequence (target completion: **≤ 4 h**):

1. Declare incident; freeze cert-manager auto-renewal of the affected
   issuer to avoid issuing more material that chains to compromised
   keys (`kubectl patch clusterissuer ... --type=merge -p '{"spec":{"...":...}}'`).
2. Revoke the compromised cert(s) — Let's Encrypt revocation for
   public leaves, internal CRL update for internal leaves.
3. If a CA key is compromised (not just a leaf), execute the
   *Intermediate rotation* or *Root rotation* sequence on an
   **accelerated** timeline: the overlap window is shortened to the
   minimum needed for one rolling restart of every workload.
4. Rotate any secret material that may have transited a TLS session
   protected by the compromised key (API tokens, OIDC client
   secrets) per Phase 6 #3.
5. File CT log monitors for the old key's fingerprint.

## Verifying a rotation

Always confirm at least these after any rotation:

```
# Public chain visible from the internet
openssl s_client -connect api.pulseboard.app:443 -servername api.pulseboard.app </dev/null \
  | openssl x509 -noout -dates -issuer -subject

# Internal mTLS chain from inside the cluster
kubectl exec -n pulseboard deploy/edge -- \
  openssl s_client -connect postgres.pulseboard.svc:5432 -starttls postgres </dev/null \
  | openssl x509 -noout -dates -issuer

# cert-manager view
kubectl get certificates,certificaterequests -A
```

A successful rotation produces zero `Ready=False` certificates and
zero TLS handshake failures in the edge's `pulse_*` error counters
for one full day.
