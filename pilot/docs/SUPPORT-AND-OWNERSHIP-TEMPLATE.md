# Support and ownership (template)

WinDictate Restricted Pilot {{VERSION}}. To be completed by the organisation running the pilot. Placeholders are in square brackets; nothing here is pre-filled with any organisation's details.

## Ownership

| Role | Name / team | Contact route | Notes |
|---|---|---|---|
| Business owner (decides scope and continuation) | [ ] | [ ] | |
| Pilot lead (day-to-day, collects feedback) | [ ] | [ ] | |
| Technical owner (packaging, deployment, removal) | [ ] | [ ] | |
| Security / risk reviewer | [ ] | [ ] | |
| Data protection reviewer | [ ] | [ ] | |
| Component update owner (watches .NET, speech engine and model updates; requests rebuilds) | [ ] | [ ] | Recorded as "TO BE ASSIGNED" in the SBOM |
| Software maintainer (source changes and rebuilds) | WinDictate maintainer | Via the source repository's issue tracker, or as agreed | Open-source project, MIT licence |

## Pilot scope

| Item | Decision |
|---|---|
| Pilot users (number, role) | [ ] |
| Devices (model, Windows build) | [ ] |
| Start and end dates | [ ] |
| Permitted uses | Post-event dictation and drafting (see README-PILOT.md); [ ] |
| Applications in scope | [ ] |
| Start automatically with Windows? | No (default) / Yes, via deployment package: [ ] |
| Success criteria | [ ] |

## Support model

| Situation | First step | Escalation |
|---|---|---|
| WinDictate does not start (message about processor, Visual C++ runtime or speech model) | Check the prerequisite named in the message; reinstall from the package | Technical owner |
| Dictation does not insert, or inserts in the wrong place | Collect About > Copy details, the time of the event and `windictate.log` (contains no dictated text) | Pilot lead → maintainer |
| Recognition quality | Collect examples with **invented** content only | Pilot lead |
| Suspected data handling issue | Stop use (tray > Exit); do not send dictated content anywhere | Security / data protection reviewer |
| Removal | `Uninstall-WinDictate.ps1` (see UNINSTALL-INSTRUCTIONS.md) | Technical owner |

What to collect for any support case: About > **Copy details** (version, build, commit, model check, Windows version), the approximate time, the application being dictated into, and `%LOCALAPPDATA%\WinDictate\windictate.log`. Never collect or forward audio or dictated text from real work.

## Change and release control

- The package is frozen: any change (code, dependencies, configuration, model) produces a new version number, new hashes and a new SBOM.
- Rebuild triggers: a .NET security update (monthly), a speech engine or model update, a defect fix, or a scope change.
- Each new package goes through the same review, sanitisation and UAT before replacing the previous one.

## Exit plan

At the end of the pilot, run the uninstall script on every device (UNINSTALL-INSTRUCTIONS.md). WinDictate keeps no dictated content, so there is no data to retrieve or destroy beyond settings and logs.
