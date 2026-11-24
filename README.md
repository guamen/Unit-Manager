# Unit Manager

Unit Manager is a Windows desktop application by Guaroa Software Corporation for finding files and folders, inspecting running processes, and diagnosing resource usage. It is built with C# and Windows Forms.

## Download for Windows

[Download Unit Manager for Windows x64 (ZIP)](https://raw.githubusercontent.com/guamen/Unit-Manager/master/Build/Unit-Manager-win-x64.zip).

Extract the ZIP and open `Unit Manager.exe` from the extracted folder. The prebuilt app requires the **.NET 10 Desktop Runtime (x64)**.

## Requirements

- Windows
- .NET 10 SDK to build from source
- .NET 10 Desktop Runtime to run the framework-dependent executable
- Administrator permissions for per-process disk and network measurements in PC Diagnostics

## Build and run

From the repository root in PowerShell, run:

```powershell
dotnet build FileViewer.sln -c Release
& ".\bin\Release\net10.0-windows\Unit Manager.exe"
```

To launch directly into **PC Diagnostics**, use:

```powershell
dotnet run --project FileViewer.csproj -- --diagnostic
```

## Features

- **File Search** finds files and folders on a selected drive. Filter by name or wildcard, extension, creation date, or file size. The junk-file filter identifies categories such as crash dumps, logs, and temporary files. Folder sizes include files the scan could access in nested folders. You can open a result's location or permanently delete it after confirming.
- **Task Manager** shows running processes and refreshes every three seconds by default. Inspect CPU and memory usage, threads, modules, network connections, and hosted services. You can also change process priority or CPU affinity, suspend or resume a process, end it, or create a memory dump.
- **PC Diagnostics** samples CPU, RAM, disk reads and writes, and incoming and outgoing traffic. Select a column header to sort, or filter the table to processes that host services. Each row reports usage for the entire process. Internet usage is estimated from traffic involving public IP addresses. The app may recommend lowering an application's CPU priority or requesting that it close. Applying either action requires confirmation.

Choose **Run as administrator** in **PC Diagnostics** for disk and network measurements. Unit Manager closes the previous instance after Windows starts the elevated one.
