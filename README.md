# iLovePDF Tools

Windows right-click utility powered by the iLoveAPI REST API. After installing, right-click a file,
choose **Show more options**, then:

| File type | Menu entries |
|---|---|
| `.pdf` | iLovePDF: Compress PDF, iLovePDF: Merge PDFs |
| `.doc` `.docx` `.ppt` `.pptx` | iLovePDF: Convert to PDF |
| `.jpg` `.jpeg` | iLovePDF: Convert to PDF (one PDF per image), iLovePDF: Combine into one PDF |

No window opens during an operation: progress and the result appear as a Windows notification
(click it to reveal the result, or the log on errors). Results are saved next to the original
(`name_compressed.pdf`, `name_merged.pdf`, `name.pdf`) and never overwrite an existing file.
A GUI (Start menu > "iLovePDF Tools Settings") exists only for the API key, options and the activity log.

PDF to Word is **not** offered: iLoveAPI has no such tool.

## Build (on Windows)
1. Install .NET 8 SDK and Inno Setup 6.
2. `powershell -ExecutionPolicy Bypass -File .\build.ps1`
3. Installer: `artifacts\iLovePDFTools-Setup-1.0.0.exe`

## Architecture
- `PdfTools.Core` - API client (auth/start/upload/process/download/delete), job logic, DPAPI key store, JSON-lines log. Unit-tested with a fake API.
- `PdfTools.Runner` (`PdfToolsRunner.exe`) - launched by the context menu. Collects a multi-selection (Explorer starts one process per file) via mutex + named pipe, runs the job, shows notifications.
- `PdfTools.Settings` (`PdfToolsSettings.exe`) - WPF window: key, compression level, region, log viewer.
- `installer/PdfTools.iss` - per-user Inno Setup wizard (no admin rights), key page with iLoveAPI signup link and live key check, registry entries under HKCU.

## Notes and limits
- Auth uses only the project **public** key via `/auth`; the secret key is never needed or stored. The key is DPAPI-encrypted per Windows user in `%AppData%\PdfTools`.
- Windows hides classic context-menu entries when more than 15 files are selected.
- File size/count limits are enforced by iLoveAPI; the free plan has a monthly file quota.
- Unsigned installer: SmartScreen shows "unknown publisher" (click More info > Run anyway).
- Silent install skips the key page; add the key afterwards in Settings.
- Logs: `%AppData%\PdfTools\logs` (action, file names, result, time; no paths or contents), kept 30 days by default.
