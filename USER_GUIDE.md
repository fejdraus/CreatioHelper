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

CreatioHelper supports two sync modes selectable in Settings:

- **File Copy (SFTP)** — incremental rsync-style sync over SSH. Works from any OS to Linux/macOS targets.
- **Syncthing** — external Syncthing-based sync via REST API.

### Adding Servers (File Copy / SFTP mode)

1. Open the **Servers Sync** panel.
2. Click **Add Server** and fill in:

   - **Name** *(required)*: display label for this server.
   - **Site Name / Pool Name**: IIS site and pool (Windows targets only).
   - **Remote site path** *(required)*: absolute path on the target server (e.g. `/var/www/creatio`).
   - **Service name**: systemd/launchctl service to stop/start during sync (leave empty to skip).
   - **SSH host** *(required)*: IP address or hostname of the target server.
   - **Port**: SSH port (default `22`).
   - **SSH username** *(required)*: login on the target server.
   - **SSH auth** *(one required)*: either a password **or** a path to a private key file.
   - **Folders to sync**: relative paths from site root (e.g. `Terrasoft.Configuration`). Leave empty to sync the entire site directory.
   - **Exclude patterns**: comma-separated names or glob patterns to skip (e.g. `logs,*.log,App_Data`). Name-only patterns match at any depth; path patterns containing `/` match relative to the site root. Applied to both files and directories.

### Synchronization Process (SFTP)

After the main deployment step:

1. Stop the remote service via SSH (`sudo systemctl stop <service>`), if configured.
2. Copy only **changed** files via SFTP (compared by size and modification time, 2-second tolerance).
3. Start the remote service via SSH.
4. Output log shows each copied file and the total count per server.

**Resume and retry:** If the SSH connection drops mid-transfer, the sync automatically reconnects and resumes each file from the last transferred byte. Up to 10 reconnect attempts are made with increasing delays between them (3 s, 6 s, … capped at 30 s).

---

## Tips & Best Practices

- Always **backup** Creatio databases and files before starting.
- Run as **Administrator** on Windows to ensure IIS commands execute correctly.
- For SFTP sync, ensure the target server has an SSH daemon running and the specified user has write access to the site directory.
- Prefer **Folder Mode** on Linux or when IIS is not available.
- Keep Redis available if used, as the tool clears its cache post-deployment.

---

## Troubleshooting

| Issue                           | Cause                         | Solution                     |
| ------------------------------- | ----------------------------- | ---------------------------- |
| _Creatio application not found_ | Incorrect folder or IIS site  | Verify selected path/site    |
| _Access Denied errors_          | Missing admin privileges      | Run as Administrator         |
| _Remote servers show Error_     | SSH unreachable or wrong credentials | Check host/port/user/password, ensure sshd is running |
| _Redis flush failed_            | Redis unavailable             | Ensure Redis is running      |

---

## Conclusion

CreatioHelper streamlines Creatio maintenance, especially for managing packages, rebuilding schemas, and synchronizing multiple servers. Its GUI simplifies tasks that would otherwise require multiple manual steps through WorkspaceConsole.

For advanced needs, combine CreatioHelper with **CreatioHelper.Agent** to remotely monitor and control Creatio servers via API.
