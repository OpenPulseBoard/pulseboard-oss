# Security Policy

## Supported versions

PulseBoard is pre-alpha. There are no supported releases yet; security
fixes land on `main`.

## Reporting a vulnerability

**Please do not file public GitHub issues for security problems.**

Email `security@pulseboard.cloud` (replace with the real address once
the org is set up) with:

- A clear description of the issue and its impact.
- Steps to reproduce, ideally with a minimal proof of concept.
- Your assessment of severity and any suggested mitigation.
- Whether you intend to publish your findings, and on what timeline.

We will acknowledge receipt within **3 business days** and aim to provide
an initial assessment within **10 business days**. We coordinate
disclosure: please give us a reasonable window (default 90 days) to ship
a fix before going public.

## Scope

In scope:

- The PulseBoard edge service (this repo): ingest, query, alerting,
  notify, auth modules.
- Anything that could let one tenant read or write another tenant's data
  once multi-tenancy lands (Phase 1 in ).
- Credential / token handling, signed webhook receivers, audit log
  integrity.

Out of scope for now (no bounty, low priority):

- Findings in third-party dependencies that are already publicly known
  and tracked.
- Self-DoS by submitting astronomically large payloads to an unprotected
  demo instance you are running yourself.
- Anything that requires AGPL violations to exploit (e.g. running a
  hostile forked build against your own infrastructure).

## Safe harbor

We will not pursue legal action against good-faith researchers who follow
this policy. If you are uncertain whether a particular activity is in
scope, ask first.
