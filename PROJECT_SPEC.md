# S3 Batch Processor — Full Project Specification

## What This Application Is

A Windows desktop executable that acts as a local command center for dispatching geospatial batch processing jobs. The user selects data files from S3, selects EC2 instances, assigns which files go to which instance, and triggers remote execution. For each data file, the SSM script **generates a unique batch file** (`test_[TIMESTAMP].bat`) on the instance that contains the process command for that specific job. Results are collected back to S3.

The application is NOT a web app. It is a native Windows GUI built in C# / WPF that runs on the operator's local machine and communicates with AWS services via SDK calls.

---

## User Flow (End to End)

### Step 1: Connect
The operator launches the app. It reads AWS credentials from the local credential chain (~/.aws/credentials, environment variables, or SSO cache). The status bar confirms identity and region.

### Step 2: Browse S3
The operator navigates an S3 bucket browser. They drill into folders, filter by file type (.tif, .tiff, .las), and multi-select the data files they want to process.

### Step 3: View EC2 Instances
The operator switches to (or sees alongside) an EC2 panel. On-demand instances are displayed as cards showing instance ID, type, state, availability zone, and tags. The operator can start/stop instances from here.

### Step 4: Assign Files to Instances
The operator assigns selected S3 files to specific EC2 instances. This is manual — the user decides which files go where. Input data files are **pulled from S3 per job** (not assumed pre-loaded). The UI shows a clear mapping: Instance A → [file1.tif, file2.tif], Instance B → [file3.las].

### Step 5: Execute
The operator confirms the assignment and hits "Run." For each data file, the SSM script:
1. Downloads the input file from S3 to the instance (`C:\data\`)
2. **Creates a unique batch file** `test_[TIMESTAMP].bat` in a configured jobs directory (`C:\processor\jobs\`) containing the process command for that specific file
3. Executes the generated `.bat` file
4. Uploads the result back to S3

No shared `.bat` file is pulled from S3 — each job gets its own locally-generated launcher script.

### Step 6: Monitor
The app polls SSM command status and displays progress per instance and per file. Status states: Pending, In Progress, Success, Failed, Timed Out.

### Step 7: Collect Output
On successful completion, the processing executable's output (which lands on the EC2 instance's local disk at `C:\data\output\`) is pushed back to a designated S3 output path. This upload is part of the SSM command script (using `Write-S3Object`).

---

## High-Level Infrastructure

```
┌──────────────────────────────────────────────────────────────────────┐
│                        Operator's Local Machine                      │
│                                                                      │
│   ┌──────────────────────────────────────────────────────────┐       │
│   │            S3 Batch Processor (WPF Desktop App)          │       │
│   │                                                          │       │
│   │  ┌───────────┐  ┌───────────┐  ┌─────────────────────┐  │       │
│   │  │ S3 Browser │  │EC2 Manager│  │ Job Orchestrator    │  │       │
│   │  │           │  │           │  │                     │  │       │
│   │  │ List      │  │ List      │  │ Assign files→EC2   │  │       │
│   │  │ Navigate  │  │ Start/Stop│  │ Dispatch SSM cmds  │  │       │
│   │  │ Select    │  │ Show cards│  │ Poll status        │  │       │
│   │  │ Preview   │  │ Health    │  │ Collect output     │  │       │
│   │  └─────┬─────┘  └─────┬─────┘  └──────────┬──────────┘  │       │
│   │        │              │                    │             │       │
│   └────────┼──────────────┼────────────────────┼─────────────┘       │
│            │              │                    │                      │
└────────────┼──────────────┼────────────────────┼─────────────────────┘
             │              │                    │
      AWS SDK calls   AWS SDK calls       AWS SDK calls
             │              │                    │
             ▼              ▼                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│                              AWS Cloud                                │
│                                                                      │
│   ┌────────────┐    ┌─────────────────┐    ┌──────────────────┐      │
│   │    S3      │    │     EC2         │    │      SSM         │      │
│   │            │    │                 │    │                  │      │
│   │ Input:     │    │ On-demand inst. │    │ RunCommand API   │      │
│   │ .tif/.las  │    │ w/ process.exe  │    │                  │      │
│   │            │    │ pre-installed   │    │ Sends PS scripts │      │
│   │ Output:    │    │                 │    │ to EC2 instances │      │
│   │ processed  │    │ Per-job bats    │    │                  │      │
│   │ results    │    │ generated local │    │ Returns status   │      │
│   └────────────┘    └─────────────────┘    └──────────────────┘      │
│                                                                      │
│   EC2 Instances have:                                                │
│   - Windows Server (2019/2022) with SSM Agent running                │
│   - IAM role with ssm:*, s3:GetObject, s3:PutObject                 │
│   - process.exe pre-installed at C:\processor\                       │
│   - C:\processor\jobs\ dir for per-job test_[TIMESTAMP].bat files    │
│   - Input data pulled from S3 per job (NOT pre-loaded)               │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

### AWS Prerequisites (Outside This App)

These must be set up before the app is useful. The app does NOT provision these — it assumes they exist:

1. **S3 Buckets** — Input bucket with .tif/.tiff/.las files. Output bucket (or prefix) for results.
2. **EC2 On-Demand Instances** — Windows Server (2019/2022), pre-configured with:
   - `process.exe` installed at `C:\processor\process.exe` (via AMI, User Data, or manual install)
   - `C:\processor\jobs\` directory for per-job `test_[TIMESTAMP].bat` files
   - SSM Agent installed and running (default on AWS Windows AMIs)
   - AWSPowerShell module available (default on AWS Windows AMIs)
   - IAM Instance Profile with policies for `ssm:*`, `s3:GetObject`, `s3:PutObject`
   - Input data files are **pulled from S3 per job** — NOT assumed pre-loaded on the instance
3. **Operator IAM User/Role** — The local AWS credentials need permissions for:
   - `s3:ListBucket`, `s3:ListAllMyBuckets`, `s3:GetObject`
   - `ec2:DescribeInstances`, `ec2:StartInstances`, `ec2:StopInstances`
   - `ssm:SendCommand`, `ssm:GetCommandInvocation`, `ssm:ListCommandInvocations`
   - `sts:GetCallerIdentity` (for connection verification)

---

## Tech Stack

| Component | Choice | NuGet Package | Rationale |
|-----------|--------|---------------|-----------|
| Runtime | .NET 8 (LTS) | — | Long-term support, current stable release |
| GUI Framework | WPF | — (built-in) | Best native Windows desktop framework, mature MVVM support, Visual Studio designer |
| MVVM Toolkit | CommunityToolkit.Mvvm | `CommunityToolkit.Mvvm` | Microsoft-maintained, source generators for zero-boilerplate MVVM |
| AWS S3 | AWS SDK v4 | `AWSSDK.S3` | S3 bucket listing, object browsing, file download/upload |
| AWS EC2 | AWS SDK v4 | `AWSSDK.EC2` | Instance listing, start/stop, describe |
| AWS SSM | AWS SDK v4 | `AWSSDK.SimpleSystemsManagement` | RunCommand dispatch, status polling |
| AWS STS | AWS SDK v4 | `AWSSDK.SecurityToken` | Credential verification (GetCallerIdentity) |
| DI Container | Microsoft.Extensions.DependencyInjection | `Microsoft.Extensions.DependencyInjection` | Standard .NET DI |
| Logging | Microsoft.Extensions.Logging | `Microsoft.Extensions.Logging` | Standard .NET logging abstractions |

### Build & Publish

```bash
# Development
dotnet build
dotnet run --project src/S3BatchProcessor.App

# Release (single-file Windows executable)
dotnet publish src/S3BatchProcessor.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

---

## Design Decisions

### Why WPF (not WinUI 3, WinForms, MAUI)

- **WPF** has the most mature MVVM ecosystem, a working Visual Studio XAML designer, and 18+ years of community knowledge. For a desktop tool with moderate UI complexity (tree views, list views, data templates, card layouts), WPF is the fastest path to working software.
- **WinUI 3** is Microsoft's future direction but lacks a WYSIWYG designer and has a smaller ecosystem. Would be the choice if we needed Fluent Design polish, but for an internal operator tool, function > aesthetics.
- **WinForms** lacks the data binding and templating needed for the card-based EC2 view and the assignment UI.
- **MAUI** targets cross-platform which we explicitly don't need, and adds complexity.

### Why MVVM with CommunityToolkit.Mvvm

The application has distinct functional panels (S3 browser, EC2 manager, job orchestrator) that map naturally to separate ViewModels. MVVM allows:
- Each panel to be developed and tested independently
- Clean separation between AWS service logic and UI
- Easy unit testing of ViewModels without a running UI
- CommunityToolkit.Mvvm uses source generators so there's minimal boilerplate: `[ObservableProperty]` and `[RelayCommand]` attributes handle property change notification and command binding

### Why Interface-Based Services

All AWS interactions go through interfaces (`IS3Service`, `IEc2Service`, `ISsmService`). This allows:
- Mocking for unit tests (no real AWS calls in tests)
- Swapping implementations (e.g., a mock S3 service that reads from local filesystem for offline development)
- Clean DI registration

### Why SSM RunCommand (not SSH, not direct API)

- **No key management**: SSM uses IAM auth, no SSH key pairs to distribute or manage
- **No open ports**: Instances don't need port 22 open. SSM communicates via the SSM agent's outbound HTTPS connection
- **Built-in logging**: Command output is captured and retrievable via the API
- **AWS-native**: Same mechanism AWS uses internally for fleet management
- The operator's local machine never directly connects to the EC2 instance. All communication is mediated through the SSM API.

### Per-Job Batch File Generation (Not Shared S3 Pull)

This is a key architectural decision. For each data file, the SSM script **creates a unique batch file** (`test_[TIMESTAMP].bat`) on the instance containing the process command for that specific job. No shared `.bat` or runner script is pulled from S3 per job. The `process.exe` binary is pre-installed on the instance (via AMI/User Data/manual), and each per-job `.bat` simply invokes it with the right arguments.

Input data files are **pulled from S3 per job** — they are not assumed to be pre-loaded on the instance.

### Output Collection Strategy

After the processing executable completes, its output is on the EC2 instance's local disk at `C:\data\output\`. The SSM command includes a follow-up step using `Write-S3Object` (AWSPowerShell) to upload the result back to the designated S3 output prefix. The SSM command sent to each instance is a PowerShell script:
```powershell
# Conceptual flow for the SSM command payload (one per data file)
$ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$batPath = "C:\processor\jobs\test_$ts.bat"

Copy-S3Object -BucketName '<source-bucket>' -Key 'data/<filename>' -LocalFile 'C:\data\<filename>'

@"
@echo off
"C:\processor\process.exe" --file "<filename>"
"@ | Set-Content -Path $batPath -Encoding ASCII

cmd /c "`"$batPath`""

Write-S3Object -BucketName '<output-bucket>' -Key 'results/result_<filename>' -File 'C:\data\output\result_<filename>'
```

---

## Solution Structure

```
S3BatchProcessor/
├── S3BatchProcessor.sln
├── README.md
├── MILESTONE1_SPEC.md              # You're reading it (or rather, the full spec)
│
├── src/
│   └── S3BatchProcessor.App/
│       ├── S3BatchProcessor.App.csproj
│       │
│       ├── App.xaml                 # Application entry, resource dictionaries
│       ├── App.xaml.cs              # DI container setup, startup logic
│       │
│       ├── Models/
│       │   ├── S3BucketItem.cs      # Bucket: Name, CreationDate
│       │   ├── S3ObjectItem.cs      # S3 object: Key, Name, Size, LastModified, IsFolder, ItemType
│       │   ├── S3ItemType.cs        # Enum: Bucket, Folder, TiffFile, LasFile, TextFile, OtherFile
│       │   ├── Ec2InstanceItem.cs   # Instance: InstanceId, Type, State, AZ, Tags, PublicIp, LaunchTime
│       │   ├── Ec2InstanceState.cs  # Enum: Pending, Running, Stopping, Stopped, Terminated
│       │   ├── JobAssignment.cs     # Maps: Instance → List<S3ObjectItem>
│       │   ├── JobStatus.cs         # Enum: NotStarted, Pending, InProgress, Success, Failed, TimedOut
│       │   └── JobResult.cs         # Per-file result: File, Instance, Status, StartTime, EndTime, Output
│       │
│       ├── Services/
│       │   ├── IAwsConnectionService.cs    # Credential verification, region management
│       │   ├── AwsConnectionService.cs
│       │   ├── IS3Service.cs               # ListBuckets, ListObjects, GetObjectContent, PutObject
│       │   ├── S3Service.cs
│       │   ├── IEc2Service.cs              # DescribeInstances, StartInstance, StopInstance
│       │   ├── Ec2Service.cs
│       │   ├── ISsmService.cs              # SendCommand, GetCommandStatus, CancelCommand
│       │   ├── SsmService.cs
│       │   ├── IJobOrchestrationService.cs # Coordinates assignment → execution → output collection
│       │   └── JobOrchestrationService.cs
│       │
│       ├── ViewModels/
│       │   ├── MainViewModel.cs            # Root VM: holds child VMs, manages navigation between panels
│       │   ├── StatusBarViewModel.cs        # Connection status, identity, region
│       │   ├── S3BrowserViewModel.cs        # Bucket listing, object browsing, selection, filtering, preview
│       │   ├── Ec2ManagerViewModel.cs       # Instance listing, start/stop, health display
│       │   ├── JobAssignmentViewModel.cs    # File→Instance assignment UI logic
│       │   └── JobExecutionViewModel.cs     # Execution dispatch, progress polling, result display
│       │
│       ├── Views/
│       │   ├── MainWindow.xaml              # Root window: tab/panel layout, status bar
│       │   ├── MainWindow.xaml.cs
│       │   ├── S3BrowserView.xaml           # S3 browser (UserControl)
│       │   ├── S3BrowserView.xaml.cs
│       │   ├── Ec2ManagerView.xaml          # EC2 instance cards (UserControl)
│       │   ├── Ec2ManagerView.xaml.cs
│       │   ├── JobAssignmentView.xaml       # Assignment panel (UserControl)
│       │   ├── JobAssignmentView.xaml.cs
│       │   ├── JobExecutionView.xaml        # Execution monitor (UserControl)
│       │   └── JobExecutionView.xaml.cs
│       │
│       ├── Converters/
│       │   ├── FileSizeConverter.cs          # Bytes → "1.2 GB"
│       │   ├── BoolToVisibilityConverter.cs
│       │   ├── InstanceStateToColorConverter.cs  # Running=Green, Stopped=Gray, etc.
│       │   └── JobStatusToColorConverter.cs
│       │
│       └── Resources/
│           └── Styles.xaml                   # Shared styles, data templates, color palette
│
└── tests/
    └── S3BatchProcessor.Tests/
        ├── S3BatchProcessor.Tests.csproj
        ├── Services/
        │   ├── S3ServiceTests.cs
        │   ├── Ec2ServiceTests.cs
        │   └── SsmServiceTests.cs
        └── ViewModels/
            ├── S3BrowserViewModelTests.cs
            └── Ec2ManagerViewModelTests.cs
```

---

## Application Layout

The app uses a primary window with a tab-based workflow. The user progresses through tabs left to right, though they can jump back at any time.

```
┌─────────────────────────────────────────────────────────────────────────┐
│  S3 Batch Processor                                        [—] [□] [×] │
├─────────────────────────────────────────────────────────────────────────┤
│  [1. Select Files]  [2. Select Instances]  [3. Assign & Run]           │
│  Region: [us-east-1 ▼]                                     [Refresh]  │
├═════════════════════════════════════════════════════════════════════════┤
│                                                                         │
│                    (Active tab content here)                            │
│                                                                         │
├─────────────────────────────────────────────────────────────────────────┤
│  ✓ Connected: arn:aws:iam::123456:user/operator  │  us-east-1         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Tab 1: Select Files (S3 Browser)

```
┌──────────┬──────────────────────────────────────┬───────────────────┐
│          │  my-bucket / data / 2024 /            │                   │
│ Buckets  │  ◄ Back                               │  Preview          │
│          ├──────────────────────────────────────┤                   │
│ ┌──────┐ │  ☐ [Filter TIFF/LAS only]            │  sample.txt       │
│ │bucket│ │  Showing 42 of 156 files              │  Size: 2.4 KB     │
│ │bucket│ │                                      │                   │
│ │*activ│ │  📁 subfolder-a/                     │  ┌───────────────┐│
│ │bucket│ │  📁 subfolder-b/                     │  │ Text content  ││
│ │bucket│ │  📄 scan-001.tif      45.2 MB        │  │ displayed     ││
│ │      │ │  📄 scan-002.tif      38.1 MB        │  │ here...       ││
│ │      │ │  📄 survey.las        120.3 MB       │  │               ││
│ │      │ │  📄 sample.txt        2.4 KB         │  └───────────────┘│
│ └──────┘ │                                      │                   │
│          │  3 files selected (203.6 MB)          │                   │
│          │                [Continue → ]          │                   │
└──────────┴──────────────────────────────────────┴───────────────────┘
```

### Tab 2: Select Instances (EC2 Manager)

```
┌─────────────────────────────────────────────────────────────────────┐
│  On-Demand Instances                                    [Refresh]  │
│                                                                     │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐    │
│  │ i-0a1b2c3d4e    │  │ i-1f2e3d4c5b    │  │ i-9z8y7x6w5v    │    │
│  │ c5.2xlarge      │  │ c5.4xlarge      │  │ m5.xlarge       │    │
│  │ ● Running       │  │ ○ Stopped       │  │ ● Running       │    │
│  │ us-east-1a      │  │ us-east-1b      │  │ us-east-1a      │    │
│  │ Tag: worker-1   │  │ Tag: worker-2   │  │ Tag: worker-3   │    │
│  │                 │  │                 │  │                 │    │
│  │ [Stop] [Select] │  │ [Start][Select] │  │ [Stop] [Select] │    │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘    │
│                                                                     │
│  2 instances selected                        [Continue → ]         │
└─────────────────────────────────────────────────────────────────────┘
```

### Tab 3: Assign & Run

```
┌─────────────────────────────────────────────────────────────────────┐
│  Processor: C:\processor\process.exe                  [Configure]  │
│  Job Bat Dir: C:\processor\jobs\                      [Configure]  │
│  Output S3 Path: s3://batchtest3-cbai/results/        [Configure]  │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │  i-0a1b2c3d4e (c5.2xlarge, Running)                        │   │
│  │  ┌──────────────────────────────────────────────────────┐   │   │
│  │  │  📄 scan-001.tif   📄 scan-002.tif   [+ Add Files]  │   │   │
│  │  └──────────────────────────────────────────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │  i-9z8y7x6w5v (m5.xlarge, Running)                         │   │
│  │  ┌──────────────────────────────────────────────────────┐   │   │
│  │  │  📄 survey.las                        [+ Add Files]  │   │   │
│  │  └──────────────────────────────────────────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  Unassigned files: (none)                                          │
│                                                                     │
│                              [▶ Run Processing]                     │
│                                                                     │
│  ── Progress ──────────────────────────────────────────────────    │
│  i-0a1b2c3d4e │ scan-001.tif │ ████████░░ 80% │ In Progress       │
│  i-0a1b2c3d4e │ scan-002.tif │ ░░░░░░░░░░  0% │ Pending           │
│  i-9z8y7x6w5v │ survey.las   │ ██████████ Done │ ✓ Success         │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Milestone Plan

### Milestone 1: S3 Browser (Current)

**Goal**: Prove AWS connectivity and S3 read access through a working file browser GUI.

**Scope**:
- AWS credential chain connection + status display
- Region selector
- Bucket listing in left panel
- Object browsing with folder navigation (breadcrumb, back button)
- File type filtering (.tif/.tiff/.las toggle)
- Multi-file selection with count/size display
- Text file preview pane (proof of concept for S3 GetObject)

**Key implementation details**:
- `S3Service` uses `ListBucketsAsync`, `ListObjectsV2Async` (with Delimiter="/"), `GetObjectAsync`
- `S3BrowserViewModel` tracks `CurrentBucket`, `CurrentPrefix`, `NavigationStack`, `Items` (ObservableCollection), `SelectedItems`
- Use `CollectionViewSource` for filtering
- Breadcrumb built from splitting `CurrentPrefix` on "/"
- Text preview: stream `GetObjectResponse.ResponseStream`, read as UTF-8, cap at 1 MB
- Cancel in-flight preview downloads via `CancellationTokenSource` when selection changes

**Acceptance criteria**:
1. App launches and shows connection status
2. Buckets appear in sidebar
3. Clicking bucket loads its contents
4. Double-click folder navigates in, breadcrumb updates, back button works
5. Filter toggle hides/shows non-target files
6. Multi-select with Ctrl/Shift click, count and size shown
7. Selecting a .txt file displays its contents in preview pane
8. Region change refreshes bucket list
9. Missing credentials show a clear error (no crash)

### Milestone 2: EC2 Instance Viewer + Control

**Goal**: Prove EC2 API access and SSM remote command execution.

**Scope**:
- List on-demand EC2 instances (filtered by tag, e.g., `BatchProcessor=true`)
- Display as cards: instance ID, type, state, AZ, name tag, launch time
- Start and stop instances from the app
- Color-coded state indicators (green=running, gray=stopped, yellow=pending)
- SSM connectivity test: send a command to create a test file on an instance, verify success
- Auto-refresh instance state on a polling interval

**Key implementation details**:
- `Ec2Service` uses `DescribeInstancesAsync` with tag filters, `StartInstancesAsync`, `StopInstancesAsync`
- `SsmService` uses `SendCommandAsync` with `AWS-RunPowerShellScript` document, `GetCommandInvocationAsync` for status
- `Ec2ManagerViewModel` holds `ObservableCollection<Ec2InstanceItem>`, polling timer for state refresh
- Instance cards use `ItemsControl` with a `WrapPanel` ItemsPanelTemplate and a `DataTemplate` for the card layout
- SSM test command: `'test' | Out-File C:\temp\batch-processor-test-$(Get-Date -Format 'yyyyMMdd_HHmmss').txt; Write-Output 'SUCCESS'`

**Acceptance criteria**:
1. On-demand instances appear as cards
2. Start/stop buttons work and state updates
3. SSM test command executes and returns success
4. Instance cards visually reflect current state

### Milestone 3: Assignment + Execution

**Goal**: Complete the core workflow — assign files to instances, execute, and collect results.

**Scope**:
- Tab 3 UI: selected instances as containers, drag or button-assign files into them
- Unassigned file tracker
- Configurable: processor path (`C:\processor\process.exe`), job bat directory (`C:\processor\jobs\`), output S3 path
- "Run" dispatches SSM commands — one per data file, serialized per instance, parallel across instances
- SSM command per data file: generate a `test_[TIMESTAMP].bat`, execute it, then `Write-S3Object` output back to S3
- Progress polling: per-file, per-instance status
- Result display: success/failure per file with timestamps
- Cancel running jobs

**Key implementation details**:
- `JobAssignment` model maps `Ec2InstanceItem` → `List<S3ObjectItem>`
- `JobOrchestrationService` coordinates:
  1. Build SSM command payload per data file (PowerShell script that creates a `test_[TIMESTAMP].bat` and runs it)
  2. Send commands — serialized per instance, parallel across instances — via `Task.WhenAll`
  3. Poll `GetCommandInvocation` on a timer
  4. Update `JobResult` models as statuses come back
- SSM command template (configured, not hardcoded):
  ```powershell
  $ErrorActionPreference = 'Stop'
  $jobBatDir = 'C:\processor\jobs'
  $ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
  $batPath = Join-Path $jobBatDir "test_$ts.bat"

  New-Item -ItemType Directory -Force -Path 'C:\data','C:\data\output',$jobBatDir | Out-Null

  Copy-S3Object -BucketName '{source_bucket}' -Key 'data/{filename}' -LocalFile 'C:\data\{filename}' -Region '{bucket_region}'

  @"
  @echo off
  "C:\processor\process.exe" --file "{filename}"
  "@ | Set-Content -Path $batPath -Encoding ASCII

  cmd /c "`"$batPath`""

  Write-S3Object -BucketName '{output_bucket}' -Key 'results/result_{filename}' -File 'C:\data\output\result_{filename}' -Region '{output_region}'
  ```
- **Hard rule:** No `Copy-S3Object` of a shared `process.bat` / job runner from S3 per job. One `test_[TIMESTAMP].bat` per data file, written locally on the instance, then executed.
- `JobExecutionViewModel` manages active jobs, polls status, updates progress bars
- Cancellation: `SsmService.CancelCommandAsync` sends `CancelCommand` API call

**Acceptance criteria**:
1. Files can be assigned to instances
2. Unassigned files are clearly shown
3. "Run" dispatches SSM commands to all instances
4. Progress updates in real time
5. Success/failure status per file per instance
6. Output files appear in S3 output path
7. Jobs can be cancelled mid-execution

### Milestone 4 (Future): Spot Instance Support

**Not in current scope.** Notes for future reference:
- Spot instances are ephemeral, fully disposable
- Same per-job `test_[TIMESTAMP].bat` generation model applies — no difference from on-demand in dispatch flow
- Input data pulled from S3 per job (same as on-demand)
- `process.exe` delivered via AMI or User Data (recreated on each spot launch)
- Spot instance request/management via EC2 launch templates
- App must handle mid-job termination (spot reclaim with 2 min notice): detect failure, retry on another instance
- Spot pricing display in the instance cards

---

## Cross-Cutting Concerns

### AWS Credential Handling

Do NOT hardcode credentials anywhere. The app uses the default AWS credential resolution chain:
1. Environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`)
2. Shared credentials file (`~/.aws/credentials`)
3. AWS SSO cached credentials
4. EC2 instance profile (not relevant for local desktop use)

Construct AWS clients with no explicit credentials:
```csharp
var s3Client = new AmazonS3Client(RegionEndpoint.USEast1);
var ec2Client = new AmazonEC2Client(RegionEndpoint.USEast1);
var ssmClient = new AmazonSimpleSystemsManagementClient(RegionEndpoint.USEast1);
```

`AwsConnectionService` handles:
- Verifying credentials on startup via `STS.GetCallerIdentity`
- Managing the active region (user-selectable)
- Recreating clients when region changes
- Exposing connection state for the status bar

### Error Handling Strategy

- All AWS SDK calls wrapped in try/catch for `AmazonServiceException`
- Errors displayed inline (status bar or panel-specific error banners), NOT modal dialogs
- Specific error handling:
  - `AccessDeniedException` → "Insufficient permissions for [operation]"
  - `AmazonS3Exception` → S3-specific errors (NoSuchBucket, NoSuchKey)
  - `InvalidInstanceId` → "Instance not found or SSM agent not running"
  - Network errors → "Cannot reach AWS. Check internet connection."
  - Credential errors → "AWS credentials not found or expired. Run 'aws configure' to set up credentials."
- Retry logic: use SDK built-in retry (default 3 retries with exponential backoff)
- Timeout: SSM command default timeout configurable, default 600 seconds

### Async Pattern

- All service methods are async (`Task<T>` return types)
- ViewModels use `async void` only for command handlers (as required by ICommand pattern)
- Long-running operations show loading indicators
- `CancellationToken` passed through to all AWS calls where possible
- UI thread updates handled by WPF's binding system (ObservableCollection and INotifyPropertyChanged dispatch to UI thread automatically when using CommunityToolkit.Mvvm)

### Configuration

The app needs a small set of configurable values. For the PoC, these can be constants or simple settings in `appsettings.json`:
- Default AWS region
- EC2 instance tag filter key/value (for filtering which instances to show)
- Processing executable path on EC2 (`C:\processor\process.exe`)
- Job bat directory on EC2 (`C:\processor\jobs\`)
- Job bat name prefix (`test_`)
- S3 output bucket/prefix
- SSM command timeout (seconds)
- SSM poll interval (seconds)
- Text preview max size (bytes)

Store in `appsettings.json` loaded via `Microsoft.Extensions.Configuration`:
```json
{
  "Aws": {
    "DefaultRegion": "us-east-1",
    "ScanRegions": ["us-east-1", "us-east-2", "us-west-1", "us-west-2"]
  },
  "Ec2": {
    "FilterTagKey": "BatchProcessor",
    "FilterTagValue": "true"
  },
  "Processing": {
    "JobBatDirectory": "C:\\processor\\jobs",
    "JobBatNamePrefix": "test_",
    "ExecutablePath": "C:\\processor\\process.exe",
    "OutputS3Prefix": "s3://batchtest3-cbai/results/",
    "PollIntervalSeconds": 3
  },
  "Ssm": {
    "CommandTimeoutSeconds": 600
  },
  "Preview": {
    "MaxFileSizeBytes": 1048576
  }
}
```

There is **no** `DeploySource` / deploy key for a shared job `.bat` — that workflow is retired in favor of local `test_[TIMESTAMP].bat` generation.

### Logging

Use `Microsoft.Extensions.Logging` with `ILogger<T>` injected into services and ViewModels:
- Log all AWS API calls at Debug level
- Log errors at Error level with exception details
- Log user actions (bucket selected, files selected, job dispatched) at Information level
- Output to: Debug console (development), file log (production)
- This is useful for troubleshooting AWS permission issues and SSM command failures

---

## What This App Does NOT Do

- **No authentication UI** — credentials configured externally via AWS CLI
- **No file upload to S3 from the desktop app** — input files are already in S3; result uploads happen on the EC2 instance via the SSM script
- **No TIFF/LAS rendering** — these are processed on EC2, not viewed locally
- **No EC2 provisioning** — instances are pre-existing, the app just discovers and controls them
- **No scheduling or cron** — the operator manually triggers each processing run
- **No multi-user or collaboration** — single operator desktop tool
- **No database** — all state is transient (in memory during the session), jobs are fire-and-monitor
- **No persistent job history** — once the app closes, job history is gone (could be added later)
