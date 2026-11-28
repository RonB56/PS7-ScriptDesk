# External-process filesystem paths

`PS7ScriptDesk.Application.Utilities.ExternalProcessPathContract` owns the Windows package identity lookup and the physical data and temp roots for application-owned files handed to external processes. An unpackaged process retains its ordinary LocalApplicationData and temporary directory. A packaged process uses `LocalAppData/Packages/<dynamic family>/LocalCache/Local` and its `Temp` child. Keep the existing directory suffixes within these roots.

Review every new process launch, working directory, generated script, JSON/manifest, log, archive, environment path, pipe/DAP/LSP path, and Explorer request for an application-owned filesystem boundary. Use the physical contract when another process must see the file. Internal-only storage may retain its logical view. User-owned documents, selected export destinations, installed executables and modules remain at their real paths. Do not change global environment variables or MSIX virtualization.

Unpackaged tests establish ordinary development behavior. Synthetic packaged identity fixtures establish path construction only. Only owner-run clean installed MSIX testing establishes packaged startup and automatic first-launch metadata generation. Include both startup and metadata in each production release check; a warm cache is insufficient.
