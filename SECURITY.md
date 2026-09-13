# Security policy

## Scope

GearHub is a local Windows desktop app: it reads device data over HID, Bluetooth and XInput,
and stores settings and history in `%APPDATA%\GearHub`. There is no server component,
no telemetry and no network communication.

## Reporting a vulnerability

Please report security issues privately instead of opening a public issue:

1. On GitHub: **Security** tab → **Report a vulnerability** (private disclosure).
2. If that is not possible, open a minimal public issue asking for a private contact
   channel — without any details of the vulnerability.

Please include:

- the GearHub version (installer or the built exe),
- your Windows version,
- what happens and how to reproduce it,
- the potential impact — what an attacker could achieve.

## What to expect

This is a hobby project maintained in free time, so responses are best effort — usually
within a few days. Confirmed issues are fixed in a release as soon as possible, and the
fix is mentioned in the release notes.
