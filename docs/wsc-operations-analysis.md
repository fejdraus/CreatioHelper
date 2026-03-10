# WorkspaceConsole Operations Analysis (Decompiled)

Source: `/tmp/wsc-decompiled/`

## Architecture

- Entry point: `Program.cs` -> `WorkspaceConsoleApplication.Run(args)`
- Operations are dispatched via a large `switch` in `ExecuteOperation(WorkspaceConsoleOperation operation)` inside `WorkspaceConsoleApplication.cs`
- The `-operation` parameter maps to the `WorkspaceConsoleOperation` enum (83 values total)
- Each enum value calls a private method in `WorkspaceConsoleApplication`, which typically resolves an `IXxxOperation` from DI and calls `Execute()`

---

## Full Enum of Operations (`WorkspaceConsoleOperation.cs`)

```
LoadLicResponse, DisableTrackChanges, SaveRepositoryContent, SaveDBContent,
LoadSvnContent, SaveVersionSvnContent, LoadWorkspaceContent, RegenerateSchemaSources,
SaveEntitySchemaReferences, ClearAllWorkspaces, DeleteWorkspaces, DeleteUnusedWorkspaces,
SaveEntitySchemasStructureInDB, SaveSystemEntitySchemasStructureInDB,
InstallFromRepository, InstallBundlePackages, PrevalidateInstallFromRepository,
ConcatRepositories, ConcatSvnRepositories, ValidateContent, CreateCustomPackage,
CreateCurrentPackage, InitializeSysSchemaContent, InstallTestPackages,
InstallUnitTestPackages, InstallTestPackagesBySuffix,
ValidateResourcesByResourceManagers, ActualizeSchemaCaptions, ExecuteProcess,
ExecuteScript, UpdatePackages, ReloadSchemaLocalizableCaptions, BuildWorkspace,
RebuildWorkspace, ExtractMetadata, UpdateWorkspaceSolution, GenerateRequiredSourceCode,
InstallRequiredSqlScripts, InstallRequiredData, UpdateRequiredSchemasDBStructure,
CreateLocalizationSchemasInDBStructure, InstallPackagesFromWorkingCopy,
GeneratePackageResourceChecksums, CompressPackages, EncryptSecurityText,
RemoveConfigurationComments, CreateSystemDBStructure, UpdateSystemDBStructure,
InstallZipPackage, InstallApp, UninstallApp, ExportApp, CreateWorkspace,
InstallSystemPackage, InstallCoreSystemSettings, SetSysSetting, SaveLicenseRequest,
BuildConfiguration, LoadPackagesToFileSystem, LoadPackagesToDB, InstallPackages,
DownloadPackages, RegenerateAdditionalSchemaSources, InstallPackageData,
GenerateSysSchemaProperties, ActualizeSysAdminUnitInRole, ZipDirectory,
UnzipDirectory, ChangeFeatureState, ResetFeatureState, BackupConfiguration,
RestoreConfiguration, DeletePackages, UpdateConfiguration, SetPackageType,
Rebuild, Build, RebuildPackage, BuildPackage, UpdatePackageHierarchy,
SetFileContentStorageConnectionString
```

---

## Detailed Operation Analysis

### 1. DownloadPackages (`-operation DownloadPackages`)

**IMPORTANT ANSWER: This is NOT "загрузить пакеты из файловой системы" (upload from FS to DB). It is the OPPOSITE -- it DOWNLOADS packages FROM the DATABASE TO the file system.**

- **What it does**: Reads packages from `PackageDBStorage` (database, for the current workspace) and writes them to a file system directory using `PackageStorageComposer.Compose()` + `Save()`.
- **Key parameters**:
  - `destinationPath` -- the file system directory to write packages to
  - `excludedPackageName` -- comma-separated list of packages to exclude
- **Implementation flow**:
  1. Creates `PackageDBStorage(AppConnection, Workspace.Id)` -- this is the SOURCE (database)
  2. Loads packages from DB: `packageStorage.Load()`
  3. Optionally excludes packages by name
  4. Creates a file-system `PackageStorage` via `PackageStorageFactory.Create(destinationPath)` -- this is the TARGET
  5. Composes source into target: `PackageStorageComposer.Compose(dbStorage, fsStorage, loadBeforeCompose: false)`
  6. Saves: `fsStorage.Save(dbStorage.GetPackageContentProvider())`
- **In Russian terminology**: This is "выгрузить пакеты в файловую систему" (export/unload packages to FS), equivalent to the UI button "Выгрузить пакеты в файловую систему".

**The OPPOSITE operation -- "загрузить пакеты из ФС в БД" (upload from FS to DB) -- is `LoadPackagesToDB`:**
- Uses `PackageInstallUtilities.LoadPackagesToDB(null, packageInstallOptions)`
- Also optionally runs `InstallSystemSchemaDBStructure` first
- Key parameters: `webApplicationPath` (sets FileDesignMode root path), plus install options

**And `LoadPackagesToFileSystem`** is the reverse of `LoadPackagesToDB`:
- Uses `PackageInstallUtilities.LoadPackagesToFileSystem()`
- Loads packages from DB to the file system development directory

---

### 2. ExportApp (`-operation ExportApp`)

- **What it does**: Exports a Creatio application (app) to a file by its code.
- **Key parameters**:
  - `appCode` -- the application code identifier
  - `destinationPath` -- where to write the exported app file
- **Implementation**: Resolves `IAppExporter` and `IAppManager`, calls `_appManager.GetAppIdByCode(appCode)` to get the GUID, then `_appExporter.Export(appId, destinationPath, appCode)`.
- **Use case**: Export an installed application for transfer/backup.

---

### 3. InstallApp (`-operation InstallApp`)

- **What it does**: Installs a Creatio application from a package archive.
- **Key parameters**:
  - `sourcePath` -- path to the app archive file
  - `destinationPath` -- working directory for extraction
- **Implementation flow**:
  1. Creates `Temp` and `Unzip` subdirectories under `destinationPath`
  2. Extracts the archive: `_packageExtractor.Extract(sourcePath, unzipDir)`
  3. Checks if an app with the same code is already installed with errors; if so, uninstalls it first
  4. Installs: `_appInstaller.Install(unzipDir, tempDir, installOptions, appInfo)`
  5. App name/code are derived from the archive filename (without extension)
- **Important**: Sets `IsAppInstalling = true` on install options. Checks `ProcessPackageInstallBehavior` feature toggle.

---

### 4. InstallZipPackage (`-operation InstallZipPackage`)

- **What it does**: Installs packages from a ZIP archive (not a full app, just packages).
- **Key parameters**:
  - `sourcePath` -- path to the ZIP file
  - `destinationPath` -- working directory for installation
  - Various install options (installPackageData, installPackageSqlScript, etc.)
- **Implementation**: Delegates to `IPackageZipInstaller.Install(sourcePath, destinationPath, installOptions)`. Checks `ProcessPackageInstallBehavior` feature toggle.

---

### 5. InstallPackageData (`-operation InstallPackageData`)

- **What it does**: Installs specific package data (seed/reference data) for named packages.
- **Key parameters**:
  - `packageName` -- the package name (can be empty to install from all packages)
  - `packageData` -- array of data items in format `PackageName.DataName` (dot-separated); if no dot, uses `packageName` as the package
- **Implementation flow**:
  1. Parses `packageData` strings into `PackageDataDescription` objects (PackageName + DataName)
  2. Creates and loads a `PackageStorage` from the factory
  3. Finds matching `PackageSchemaDataDescriptor` entries in the storage
  4. Calls `_packageInstallUtilities.InstallData(dataDescriptors, contentProvider, installOptions)` with `BackupDataChanges = false`
- **Use case**: Selectively install/reinstall data for specific package schemas without doing a full package install.

---

### 6. BackupConfiguration (`-operation BackupConfiguration`)

- **What it does**: Creates a backup of the configuration (packages).
- **Key parameters**:
  - `sourcePath` -- source packages directory
  - `destinationPath` -- temp files directory
  - `backupPath` -- where to store the backup (defaults to a system config path if empty)
- **Implementation**: Simply delegates to `IPackageBackupManager.CreateBackup(sourcePath, tempPath, backupPath)`. Has an overload that also accepts `packagesToDelete`.

---

### 7. RestoreConfiguration (`-operation RestoreConfiguration`)

- **What it does**: Restores configuration from a previously created backup.
- **Key parameters**:
  - `backupPath` -- path to the backup directory (defaults to system config path if empty)
  - `installPackageData` -- boolean, whether to install package data during restore
  - `ignoreSqlScriptBackwardCompatibilityCheck` -- boolean
- **Implementation**:
  1. Validates the backup directory exists and contains files
  2. Builds `PackageInstallOptions` from the parameters
  3. If `FeatureUseAppEventsEntityChangeTracking` is enabled, sets `RestoreData = true`
  4. Calls `_packageBackupManager.RestoreFromBackup(backupPath, installOptions)`

---

### 8. CreateLicenseRequest / SaveLicenseRequest (`-operation SaveLicenseRequest`)

- **Enum value**: `SaveLicenseRequest` (the operation name used on CLI)
- **What it does**: Generates a `.tlr` license request file.
- **Key parameters**:
  - `destinationPath` -- directory to save the file
  - `customerId` -- the customer ID string
  - `version` -- Creatio version
  - `fileName` -- optional, defaults to `LicenseRequest.tlr`
- **Implementation**: Creates a `FileStream` and calls `LicManager.CreateLicenseRequest(stream, "", customerId, version, dbType)`. The `dbType` is included only if `FeatureAddPlatformInfoToLicenseRequest` is enabled.

---

## Key Related Operations (Not in Original List but Important)

| Enum Value | What It Does |
|---|---|
| `LoadPackagesToDB` | **"Загрузить пакеты из ФС в БД"** -- uploads packages from file system to database. Uses `PackageInstallUtilities.LoadPackagesToDB()` |
| `LoadPackagesToFileSystem` | Loads packages from DB to FS development directory. Uses `PackageInstallUtilities.LoadPackagesToFileSystem()` |
| `BuildConfiguration` | Compiles the configuration |
| `InstallPackages` | Installs unzipped packages (sets `InstallUnzippedPackages = true`) |
| `DeletePackages` | Deletes packages by name |
| `ChangeFeatureState` | Toggles feature flags |
| `SetSysSetting` | Sets a system setting value |
| `BuildWorkspace` / `RebuildWorkspace` | Compiles workspace |

---

## Summary Table

| CLI Operation | Class | Direction / Purpose |
|---|---|---|
| `DownloadPackages` | `DownloadPackagesOperation` | DB -> File System (export/unload) |
| `LoadPackagesToDB` | (inline, uses `PackageInstallUtilities`) | File System -> DB (import/upload) |
| `LoadPackagesToFileSystem` | (inline, uses `PackageInstallUtilities`) | DB -> FS dev directory |
| `ExportApp` | `ExportAppOperation` | Export app by code to file |
| `InstallApp` | `InstallAppOperation` | Install app from archive |
| `InstallZipPackage` | `InstallZipPackageOperation` | Install packages from ZIP |
| `InstallPackageData` | `InstallPackageDataOperation` | Install specific data items |
| `BackupConfiguration` | `BackupConfigurationOperation` | Create config backup |
| `RestoreConfiguration` | `RestoreConfigurationOperation` | Restore config from backup |
| `SaveLicenseRequest` | `CreateLicenseRequestOperation` | Generate .tlr license request |
