# Release checklist

WinDictate Restricted Pilot {{VERSION}}, commit `{{COMMIT}}`, built {{BUILD_DATE}}.
{{DIRTY_WARNING}}
The build brief's definition of done, item by item, with where the evidence is. Evidence types:

- **Build**: enforced while this package was built. The release tool stops on failure, so a package that exists has passed it.
- **Windows run**: checked by the official build on a fresh Windows virtual machine after packaging (`.github/workflows/pilot-release.yml`); see that run's summary and `CI-VERIFICATION.md` artefact.
- **UAT**: to be checked by the pilot testers on a real Windows 11 laptop (`03-Documentation/UAT-TEST-PLAN.md`).

| # | Requirement | Evidence | Type |
|---|---|---|---|
| 1 | Fixed version and source commit recorded | {{VERSION}}, `{{COMMIT}}`: `02-Source/SOURCE_COMMIT.txt`, `05-Release/RELEASE-MANIFEST.json`, About window | Build |
| 2 | External AI cleanup absent | Code removed; guard tests `NoNetworkCode`, `NoRemoteCleanupEndpointOrKeys` | Build |
| 3 | No endpoint or API-key configuration | No such settings; old ones dropped on save (`SettingsFromAnotherBuild_DropCleanupAndHistory_WhenSaved`); Settings window test checks no such control exists | Build, Windows run |
| 4 | No first-run model download | No download code (`NoModelDownload`); compiled program has no network references (`TheProgram_ReferencesNoNetworkingCode`) | Build, Windows run |
| 5 | One fixed model supplied and hashed | `01-Runtime/win-x64/models/{{MODEL_FILE}}`, SHA-256 `{{MODEL_SHA256}}`, checked by the release tool and at every start; `04-Compliance/MODEL-SOURCE-LICENCE-AND-HASH.md` | Build |
| 6 | Transcript history disabled and unavailable | No feature or setting (`NoTranscriptHistory`); `NothingDictated_IsWrittenToDisk_NorToTheLog` | Build; UAT L-1 to L-3 |
| 7 | Audio memory-only | No audio file code (`NoAudioFiles`, `FilesAreWrittenOnlyForSettingsAndTheLog`); buffers overwritten (`Audio_IsOverwritten_…`) | Build; UAT L-3 |
| 8 | Clipboard behaviour documented and tested | `03-Documentation/DATA-FLOW-AND-PRIVACY.md`; `TextInserterTests`, clipboard controller tests; Windows tests for real clipboard, privacy markers, picture preserved | Build, Windows run; UAT C-1 to C-6 |
| 9 | Focus-change safety passes | Controller tests; Windows test `ADictationStartedInOneWindow_IsNotInsertedIntoAnother` | Build, Windows run; UAT F-1 to F-3 |
| 10 | Recording indicator and cancel behaviour pass | Controller and indicator tests; Windows indicator tests and pictures | Build, Windows run; UAT I-1 to I-4, H-2 |
| 11 | No automatic startup by default | No start-up code (`NoStartWithWindowsEntry`); install script adds none; install test checks the Run key stays empty | Build, Windows run; UAT INS-2 |
| 12 | Logs contain no dictated text | `NothingDictated_IsWrittenToDisk_NorToTheLog`; logging rules in DATA-FLOW-AND-PRIVACY.md | Build; UAT L-1 |
| 13 | Clean-machine offline test passes | Fresh Windows virtual machine: the packaged engine and model transcribe synthesised speech; the packaged program starts, verifies and loads the model, idles and exits with zero connection attempts recorded | Windows run; UAT T-4, N-1 |
| 14 | Uninstall and reinstall pass | Install, exit, uninstall, check nothing remains, reinstall, uninstall | Windows run; UAT X-1 to X-3 |
| 15 | Runtime package is self-contained | Self-contained .NET {{RUNTIME_VERSION}} publish; runs on the test machine without any .NET installed by the package (only the documented Visual C++ prerequisite) | Build, Windows run |
| 16 | Exact source snapshot included separately | `02-Source/{{SOURCE_ZIP}}` (`git archive` of the commit) | Build |
| 17 | SBOM complete | `04-Compliance/SBOM.cdx.json` and `SBOM.csv`; every shipped file assigned to a component or the build fails | Build |
| 18 | Licences and notices complete | `04-Compliance/LICENCES/`, `THIRD-PARTY-NOTICES.txt` (also shipped beside the program) | Build |
| 19 | Model source, licence and hash documented | `04-Compliance/MODEL-SOURCE-LICENCE-AND-HASH.md`, `OPERATIONAL-AI-FACTSHEET.md` | Build |
| 20 | All required documents present | `START-HERE.md`, `03-Documentation/` (10 documents), `04-Compliance/`, `05-Release/` | Build |
| 21 | SHA-256 file complete | `05-Release/SHA256SUMS.txt` covers every file; verified after writing | Build |
| 22 | No confidential, internal or personal workplace information | `05-Release/SANITISATION-REPORT.md` (automated scan; private terms can be supplied at build time); manual review below | Build; manual |
| 23 | Final ZIP frozen and separately hashed | `{{ZIP_NAME}}` and `{{ZIP_NAME}}.sha256`; the tool never overwrites an existing package | Build |

## Manual review before submission

| Check | Done by | Date |
|---|---|---|
| Read `SANITISATION-REPORT.md`; no unexpected findings | | |
| Opened About on the test laptop: version, commit and model hash match this checklist | | |
| UAT test plan completed and signed off | | |
| Package scanned with the organisation's endpoint scanner | | |
| Master copy of the ZIP and `.sha256` retained unchanged | | |
| Submitted only through the permitted channel | | |
