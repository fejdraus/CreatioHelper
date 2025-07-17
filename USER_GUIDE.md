# CreatioHelper User Guide

## Overview

**CreatioHelper** is a cross-platform graphical interface built with Avalonia designed to simplify working with **Creatio WorkspaceConsole**. It automates routine operations related to package management, compilation, and server synchronization in Creatio environments.

This guide covers:

- Getting Started
- Application Interface Overview
- Using CreatioHelper
- Server Synchronization
- Tips & Best Practices
- Troubleshooting

---

## Getting Started

### Requirements

- **.NET 8 SDK** installed on your system.
- **Windows OS** for full IIS support; Linux is supported in folder mode.
- Administrator rights when managing IIS or remote servers.
- Access to Creatio installation (must be version 7.12.0.0 or newer).

### Running the Application

```bash
# Run from source
 dotnet run --project CreatioHelper
```

Alternatively, launch the compiled executable from the **publish** directory.

---

## Application Interface Overview

Upon launching CreatioHelper, you will see:

- **Site Selection:** Choose between IIS Mode (automatically detects Creatio sites on Windows IIS) or Folder Mode (manually select the root directory of your Creatio instance).
- **Packages Path:** Directory containing packages (.zip or folders) to install.
- **Packages to Delete Before/After:** Comma-separated package names to remove before or after installation.
- **Servers Sync Panel:** Manage additional servers for synchronization after updates.
- **Control Buttons:**

  - **Start:** Executes the full update or regeneration process.
  - **Stop:** Aborts ongoing operations.
  - **Refresh Status:** Checks the status of remote servers (IIS Site/Pool).
  - **Log Output:** Displays progress and errors.

---

## Using CreatioHelper

### Basic Operation

1. **Select Site or Folder:**

   - **IIS Mode:** Select from available IIS sites.
   - **Folder Mode:** Browse to the root folder of the Creatio installation.

2. **Specify Packages:**

   - **Packages Path:** Path to the new packages.
   - **Packages to Delete Before:** Remove old/unused packages prior to installation.
   - **Packages to Delete After:** Clean up specific packages after installation.

3. **Start Process:**

   - Press **Start** to initiate:

     1. IIS Site/Pool stop.
     2. Pre-deletion of packages.
     3. Installation of new packages.
     4. Regeneration of schema sources.
     5. Rebuild and compilation via WorkspaceConsole.
     6. Redis cache flush (if Redis is configured).
     7. IIS Site/Pool restart.

4. **Monitor Log:** Check the output log for progress and any errors.

### Schema Rebuild without Packages

If no packages or deletions are specified, the **Start** button triggers:

- Schema sources regeneration.
- Full workspace rebuild and compilation.

---

## Server Synchronization

### Adding Servers

1. Open the **Servers Sync** panel.
2. Add each server's:

   - **Name:** Hostname or IP.
   - **Network Path:** UNC path to the site's root.
   - **Site Name:** IIS site name.
   - **Pool Name:** IIS application pool name.

### Synchronization Process

When enabled, after main server update:

1. Stop IIS Sites and Pools on all added servers.
2. Copy updated configuration and binaries from the main server.
3. Restart Sites and Pools on each server.
4. Output log reflects success or issues per server.

---

## Tips & Best Practices

- Always **backup** Creatio databases and files before starting.
- Run as **Administrator** to ensure IIS and remote commands execute correctly.
- Verify **WinRM** is enabled for remote server commands.
- Prefer **Folder Mode** on Linux or when IIS is not available.
- Keep Redis available if used, as the tool clears its cache post-deployment.

---

## Troubleshooting

| Issue                           | Cause                         | Solution                     |
| ------------------------------- | ----------------------------- | ---------------------------- |
| _Creatio application not found_ | Incorrect folder or IIS site  | Verify selected path/site    |
| _Access Denied errors_          | Missing admin privileges      | Run as Administrator         |
| _Remote servers show Error_     | WinRM not enabled or firewall | Enable WinRM, check firewall |
| _Redis flush failed_            | Redis unavailable             | Ensure Redis is running      |

---

## Conclusion

CreatioHelper streamlines Creatio maintenance, especially for managing packages, rebuilding schemas, and synchronizing multiple servers. Its GUI simplifies tasks that would otherwise require multiple manual steps through WorkspaceConsole.

For advanced needs, combine CreatioHelper with **CreatioHelper.Agent** to remotely monitor and control Creatio servers via API.
