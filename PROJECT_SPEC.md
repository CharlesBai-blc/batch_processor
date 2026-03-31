# S3 Batch Processor — Full Project Specification

## What This Application Is

A Windows desktop application that serves as a command center for dispatching geospatial batch processing jobs to AWS EC2 instances. The operator selects data files from S3, selects EC2 instances, assigns which files go to which instance, and triggers remote execution via AWS Systems Manager (SSM). The app never touches data directly — it orchestrates remote compute and uses S3 as the shared file system.

The application is NOT a web app. It is a native Windows GUI built in C# / WPF that runs on the operator's local machine and communicates with AWS services via SDK calls.

---

## Architecture

Three components:

1. **Operator (local machine):** Runs the WPF app. Talks to AWS APIs over HTTPS using IAM credentials. Acts as a remote control — sends commands, monitors status, displays results.

2. **Compute (EC2 instances):** Windows Server instances that do the actual work. Can be on-demand or spot. **Input data files are pulled from S3 per job.** The processor executable is also downloaded from S3 at the start of each job via a configurable **Deploy Source** (`s3://bucket/key`). For each set of files assigned to an instance, the SSM-delivered script **creates a new batch file** on the instance in a configured folder, named `test_[TIMESTAMP].bat`, containing the process commands for all assigned files.

3. **Storage (S3):** Holds input data files, output results, and the processor executable. Serves as the single source of truth for what input files exist and what results have been produced. Per-job batch files (`test_[TIMESTAMP].bat`) are generated on the instance at runtime and uploaded to the output S3 prefix after execution as an audit log.

---

## User Flow (End to End)

### Step 1: Connect
The operator launches the app. It reads AWS credentials from the local credential chain (~/.aws/credentials, environment variables, or SSO cache). The status bar confirms identity and region.

### Step 2: Browse S3
The operator navigates an S3 bucket browser. They drill into folders, filter by file type (.tif, .tiff, .las), and select the data files they want to process. Selected files are staged and then committed for processing.

### Step 3: Select EC2 Instances
The operator switches to the EC2 tab. Instances are discovered across all configured regions. The operator selects which instances to use for this processing run.

The operator can also **add planned spot instances** via the "Add Spot Instances" panel: select a region, pick a launch template, set a count, and click Add. These appear as planned placeholders in the Selected Instances pane — no AWS resources are created yet. Actual launching is deferred to the Run step.

### Step 4: Assign Files to Instances
The operator manually assigns selected S3 files to specific EC2 instances using the assignment UI. Files can be assigned individually via [+ Add Files] buttons on each instance card, or distributed automatically via round-robin. The UI shows a clear mapping: Instance A → [file1.tif, file2.tif], Instance B → [file3.las].

### Step 5: Execute
The operator clicks "Run Processing." The app validates assignments, then executes a pre-flight sequence:
1. **Launches any planned spot instances** via `ec2:RunInstances` with spot market options, using the configured launch template. Placeholder instances are replaced with real instance IDs.
2. Checks all target instances are running
3. Starts any stopped instances and waits for Running state + SSM agent registration
4. Sends **one SSM command per instance** containing all files assigned to that instance

Each SSM command is a PowerShell script that writes a timestamped batch file and runs it. The batch file itself contains **all operations** as an audit log:
- Creates working directories (Input Dir, output dir, job log dir)
- Downloads the processor executable from S3 via `aws s3 cp`
- Downloads all assigned input data files from their source S3 buckets via `aws s3 cp`
- Executes the binary per file with `FILE_START:`, `FILE_DONE:`, and `FILE_FAILED:` markers for per-file tracking
- Uploads each result file to S3 via `aws s3 cp`
- Uploads itself (the batch file) to the output S3 path as an audit log

The PowerShell wrapper is minimal — it only creates the job log directory, writes the bat to disk, and runs it via `cmd /c`.

### Step 6: Monitor
The app polls SSM command status every 3 seconds. Since each instance receives one command covering multiple files, the app parses stdout for `FILE_START:<name>`, `FILE_DONE:<name>`, and `FILE_FAILED:<name>` markers to track per-file progress within the batch. Status states: Pending, In Progress, Success, Failed, Timed Out.

### Step 7: Collect Output
The processing executable uploads its output directly to S3 as part of its execution. Results appear at the configured output prefix (e.g., `s3://batchtest3-cbai/results/`).

---

## S3 Layout

| Bucket | Purpose | Contents |
|---|---|---|
| `batchtest1-cbai` | Input data | `data/sample.txt`, `data/scan-001.tif`, `data/survey.las` |
| `batchtest2-cbai` | Input data | `data/sample2.txt`, `data/survey2.txt` |
| `batchtest3-cbai` | Deployment + Output | `deploy/process.bat` (processor executable, downloaded to instances via Deploy Source), `results/*.result` (job output) |

The processor executable is distributed via S3 and downloaded to each instance at the start of every job. Per-job batch files (`test_[TIMESTAMP].bat`) are generated on the instance at runtime and uploaded to the output S3 prefix after execution.

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
│   │  │ S3 Browser │  │EC2 Fleet  │  │ Job Orchestrator    │  │       │
│   │  │           │  │           │  │                     │  │       │
│   │  │ List      │  │ Discover  │  │ Assign files→EC2   │  │       │
│   │  │ Navigate  │  │ Start/Stop│  │ Launch spot (pre-  │  │       │
│   │  │ Select    │  │ Select    │  │   flight)          │  │       │
│   │  │ Preview   │  │ Add Spot  │  │ Dispatch SSM cmds  │  │       │
│   │  │           │  │           │  │ Poll & monitor     │  │       │
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
│   │ Input:     │    │ Windows Server  │    │ RunCommand API   │      │
│   │ .tif/.las  │    │ instances       │    │                  │      │
│   │            │    │                 │    │ Sends PowerShell │      │
│   │ Deploy:    │    │ Downloads       │    │ to EC2 instances │      │
│   │ process.*  │    │ processor+data  │    │                  │      │
│   │            │    │ from S3         │    │ Returns status   │      │
│   │ Output:    │    │                 │    │                  │      │
│   │ *.result   │    │ SSM Agent +     │    │                  │      │
│   │            │    │ AWSPowerShell   │    │                  │      │
│   └────────────┘    └─────────────────┘    └──────────────────┘      │
│                                                                      │
│   EC2 Instances have:                                                │
│   - Windows Server 2019/2022 AMI                                     │
│   - SSM Agent pre-installed (default on AWS Windows AMIs)            │
│   - AWS CLI pre-installed (default on AWS Windows AMIs)              │
│   - IAM Instance Profile with SSM + S3 permissions                   │
│   - Outbound internet or VPC endpoints for SSM + S3                  │
│   - No inbound ports required                                        │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

---

## EC2 Instance Requirements

### Base Image
- Windows Server AMI (2019 or 2022)
- SSM Agent pre-installed (default on AWS Windows AMIs)
- AWS CLI installed (required for `aws s3 cp` commands in the generated batch files)

### IAM Instance Profile
An IAM role attached to the instance with:
- `AmazonSSMManagedInstanceCore` managed policy (SSM Agent communication)
- `s3:GetObject` on input buckets (pull input data per job)
- `s3:PutObject` on output bucket (upload results)

### Network
- Outbound internet access OR VPC endpoints for SSM and S3 services
- No inbound ports required (SSM is outbound-initiated)

### On-Disk Layout

```
C:\processor\
    process.bat          <- downloaded from S3 DeploySource at start of each job
    jobs\                  <- configured job log directory
        test_20260329_143022_123.bat   <- one batch file per SSM command (per instance); name = test_[TIMESTAMP]

<InputDir>\              <- configurable via "Input Dir" field (default: C:\data)
    <input files>        <- pulled from S3 per job
    output\
        result_<filename>  <- written by processor, uploaded to S3 by the batch file
```

**Naming rule:** Each SSM command (one per instance) generates a single `test_[TIMESTAMP].bat` containing **all operations** — directory creation, S3 downloads, binary execution with tracking markers, result uploads, and self-upload as an audit log. The timestamp ensures uniqueness across successive runs.

**Input directory** is configurable via the "Input Dir" field in Tab 3 (default: `C:\data`). The batch file creates this directory if it doesn't exist.

### Instance Lifecycle

**On-Demand Instances:**
- Persist between jobs
- Can be stopped when idle to save cost (EBS volume retained)
- Local processor and accumulated `test_*.bat` files persist on disk across stop/start cycles unless you clean them up

**Spot Instances:**
- Configured in Tab 2 via the "Add Spot Instances" panel (region, launch template, count)
- Added as planned placeholders — no AWS resources created until Run
- Actually launched during pre-flight via `ec2:RunInstances` with spot market options
- Auto-terminated after job execution completes (success or failure)
- Treated as fully disposable — processor downloaded from Deploy Source, inputs from S3
- Can be reclaimed by AWS with 2 minutes notice

### Instance Startup Sequence
Regardless of on-demand or spot, every instance follows this sequence on boot:

1. SSM Agent starts and registers with Systems Manager
2. User Data script runs (recommended):
   - Creates `C:\processor\`, `C:\processor\jobs\`, and `C:\data\` directories
   - Ensures AWSPowerShell module is available (pre-installed on AWS Windows AMIs)
3. Instance appears in SSM Fleet Manager (ready for jobs)
4. Processor executable is downloaded from S3 DeploySource at the start of each job (not during boot)

Estimated time from boot to ready: 1–3 minutes.

---

## AWS Prerequisites

### Operator IAM Permissions

The IAM user running the WPF app needs:

| Permission | Used By |
|---|---|
| `sts:GetCallerIdentity` | App startup identity check |
| `s3:ListAllMyBuckets` | Tab 1 bucket listing |
| `s3:ListBucket` | Tab 1 file browsing |
| `s3:GetObject` | Tab 1 file preview |
| `s3:GetBucketLocation` | Tab 1 region detection |
| `ec2:DescribeInstances` | Tab 2 instance discovery (all regions) |
| `ec2:DescribeLaunchTemplates` | Tab 2 spot instance template listing |
| `ec2:RunInstances` | Pre-flight spot instance launching |
| `ec2:TerminateInstances` | Post-execution spot instance cleanup |
| `ec2:StartInstances` | Pre-flight auto-start |
| `ec2:StopInstances` | Tab 2 stop instances |
| `iam:PassRole` | Required to pass instance profile role when launching spot instances |
| `iam:CreateServiceLinkedRole` | One-time: create EC2 Spot service-linked role (only needed once per account) |
| `ssm:SendCommand` | Tab 3 job dispatch |
| `ssm:GetCommandInvocation` | Tab 3 status polling |
| `ssm:CancelCommand` | Tab 3 job cancellation |
| `ssm:DescribeInstanceInformation` | Pre-flight SSM agent check |

### AWS Credential Handling

Do NOT hardcode credentials anywhere. The app uses the default AWS credential resolution chain:
1. Environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`)
2. Shared credentials file (`~/.aws/credentials`)
3. AWS SSO cached credentials

---

## Tech Stack

| Component | Choice | NuGet Package | Rationale |
|-----------|--------|---------------|-----------|
| Runtime | .NET 8 (LTS) | — | Long-term support, current stable release |
| GUI Framework | WPF | — (built-in) | Best native Windows desktop framework, mature MVVM support |
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

## Job Dispatch Flow

### Pre-Flight
1. App validates all files are assigned and all instances have files
2. **If any planned spot instances exist:** App launches them via `ec2:RunInstances` with spot market options using the configured launch template. Placeholder instance objects are updated with real instance IDs and state. Instances are grouped by template+region for efficient batched API calls.
3. App checks target instances are running via `ec2:DescribeInstances`
4. If any instance is stopped, app starts it via `ec2:StartInstances` and polls until Running (5 min timeout, 5s interval)
5. App polls `ssm:DescribeInstanceInformation` until SSM agent is online on all instances (5 min timeout)
6. Pre-flight status is displayed in the execution panel with per-instance progress messages (panel stays visible on failure for error diagnosis)

### Job Execution
A job is a single SSM `SendCommand` call (using `AWS-RunPowerShellScript`) targeting one EC2 instance. **One command is sent per instance**, containing all files assigned to that instance. Commands to different instances are dispatched in parallel.

The command payload is a PowerShell script that writes a bat file containing all operations and runs it:

```powershell
# PowerShell wrapper — creates job log dir, writes bat, runs it
$ErrorActionPreference = 'Stop'
$jobBatDir = 'C:\processor\jobs'
New-Item -ItemType Directory -Force -Path $jobBatDir | Out-Null
$ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$batPath = Join-Path $jobBatDir "test_$ts.bat"

$batContent = @"
@echo off

REM === Directory setup ===
if not exist "<InputDir>" mkdir "<InputDir>"
if not exist "<InputDir>\output" mkdir "<InputDir>\output"
if not exist "C:\processor\jobs" mkdir "C:\processor\jobs"

REM === Download binary ===
aws s3 cp "s3://<deploy-bucket>/<deploy-key>" "<BinaryDir>\<BinaryName>" --region <deploy-region>

REM === Download input files ===
aws s3 cp "s3://<source-bucket>/data/<file1>" "<InputDir>\<file1>" --region <bucket-region>
aws s3 cp "s3://<source-bucket>/data/<file2>" "<InputDir>\<file2>" --region <bucket-region>

REM === Process files ===
REM Binary args are configurable via CommandArgs (default: --file "{filename}")
REM {filename} is replaced with the full input path: <InputDir>\<file-name>
echo FILE_START:<file1>
"<BinaryDir>\<BinaryName>" <resolved-command-args-for-file1>
if %ERRORLEVEL% EQU 0 (echo FILE_DONE:<file1>) else (echo FILE_FAILED:<file1>)
echo FILE_START:<file2>
"<BinaryDir>\<BinaryName>" <resolved-command-args-for-file2>
if %ERRORLEVEL% EQU 0 (echo FILE_DONE:<file2>) else (echo FILE_FAILED:<file2>)

REM === Upload results ===
REM Output S3 path can be overridden per-instance via the Output Path field on each instance card
aws s3 cp "<InputDir>\output\result_<file1>" "s3://<output-bucket>/results/result_<file1>" --region <output-region>
if %ERRORLEVEL% NEQ 0 echo UPLOAD_FAILED:<file1>
aws s3 cp "<InputDir>\output\result_<file2>" "s3://<output-bucket>/results/result_<file2>" --region <output-region>
if %ERRORLEVEL% NEQ 0 echo UPLOAD_FAILED:<file2>

REM === Upload this bat file as audit log ===
aws s3 cp "%~f0" "s3://<output-bucket>/results/test_%TIMESTAMP%.bat" --region <output-region>
if %ERRORLEVEL% NEQ 0 echo BAT_UPLOAD_FAILED
"@
$batContent = $batContent.Replace('%TIMESTAMP%', $ts)
Set-Content -Path $batPath -Value $batContent -Encoding ASCII

cmd /c "`"$batPath`""
```

All S3 operations use `aws s3 cp` (AWS CLI) inside the bat file. The bat is fully self-contained — it serves as a complete audit log of every operation performed during the job.

The binary:
1. Reads the input file from `<InputDir>\<filename>` (configurable, default `C:\data`)
2. Processes it
3. Writes result to `<InputDir>\output\result_<filename>`
4. Prints progress/status to stdout, errors to stderr

The binary does NOT handle S3 transfers. The bat file handles all downloads and uploads.

### Monitoring
- App polls `ssm:GetCommandInvocation` every 3 seconds (configurable via `PollIntervalSeconds`)
- SSM returns overall command status: `Pending`, `InProgress`, `Success`, `Failed`, `Cancelled`, `TimedOut`
- SSM returns stdout and stderr from the script
- App parses stdout for per-file tracking markers:
  - `FILE_START:<filename>` → file processing started
  - `FILE_DONE:<filename>` → file processing succeeded
  - `FILE_FAILED:<filename>` → file processing failed
- When the overall command completes, any files still marked Pending/InProgress inherit the command's final status
- App updates UI with status, duration, and output per file in a results DataGrid

### Cancellation
- App calls `ssm:CancelCommand` with the command ID
- SSM delivers a cancellation signal to the instance
- Currently running process may not stop immediately

### Failure Handling

| Failure | Detection | Recovery |
|---|---|---|
| S3 download fails | `aws s3 cp` fails, SSM reports `Failed` | Retry job |
| process.exe crashes | Non-zero exit code in SSM output | Inspect stderr, fix and retry |
| Instance terminated (spot reclaim) | `GetCommandInvocation` returns error or `Failed` | Retry on another instance |
| SSM Agent not responding | `SendCommand` fails or times out | Start instance, wait for SSM |
| Network issue | AWS API call throws exception | Retry with backoff |

---

## Binary Executable Contract

### Invocation
The binary is invoked once per file with configurable arguments. The **Command Args** field in Tab 3 controls the argument format, with `{filename}` replaced by the full input path (`<InputDir>\<file-name>`).

Default Command Args: `--file "{filename}"`

Example invocations:
```
# Default args with Input Dir = C:\data: --file "{filename}"
"C:\processor\process.bat" --file "C:\data\sample.txt"

# Custom args: --mode fast --input "{filename}" --threads 4
"C:\processor\myprocessor.exe" --mode fast --input "C:\data\sample.txt" --threads 4
```

The binary path is prepended automatically from Binary Dir + the selected binary file name.

### Expected Behavior
1. Read input from `<InputDir>\<filename>` (bat file downloads files here; Input Dir is configurable, default `C:\data`)
2. Process the file
3. Write result to `<InputDir>\output\result_<filename>` (bat file uploads from here)
4. Print progress and status to stdout
5. Print errors to stderr
6. Return exit code 0 on success, non-zero on failure

The binary does NOT handle S3 uploads or downloads. The bat file handles all S3 transfers.

### Deployment
- The binary is stored in S3 at a configurable **Deploy Source** path (e.g. `s3://batchtest3-cbai/deploy/process.bat`)
- At the start of each job, the bat file downloads the binary from Deploy Source to the local **Binary Directory** via `aws s3 cp`
- The operator can change the Deploy Source via the **Deploy Picker** in the Assignment tab (an S3 browser modal)
- Per-job batch files (`test_[TIMESTAMP].bat`) contain all operations and are uploaded to the output S3 path as an audit log
- To update the binary: upload a new version to the Deploy Source path in S3

---

## Design Decisions

### Why WPF (not WinUI 3, WinForms, MAUI)

- **WPF** has the most mature MVVM ecosystem, a working Visual Studio XAML designer, and 18+ years of community knowledge. For a desktop tool with moderate UI complexity (tree views, list views, data templates, card layouts), WPF is the fastest path to working software.
- **WinUI 3** is Microsoft's future direction but lacks a WYSIWYG designer and has a smaller ecosystem.
- **WinForms** lacks the data binding and templating needed for the card-based EC2 view and the assignment UI.
- **MAUI** targets cross-platform which we explicitly don't need, and adds complexity.

### Why MVVM with CommunityToolkit.Mvvm

The application has distinct functional panels (S3 browser, EC2 fleet, job orchestrator) that map naturally to separate ViewModels. MVVM allows:
- Each panel to be developed and tested independently
- Clean separation between AWS service logic and UI
- Easy unit testing of ViewModels without a running UI
- CommunityToolkit.Mvvm uses source generators so there's minimal boilerplate

### Why Interface-Based Services

All AWS interactions go through interfaces (`IS3Service`, `IEc2Service`, `ISsmService`). This allows:
- Mocking for unit tests (no real AWS calls in tests)
- Swapping implementations (e.g., a mock SSM service for offline development)
- Clean DI registration

### Why SSM RunCommand (not SSH, not direct API)

- **No key management**: SSM uses IAM auth, no SSH key pairs to distribute or manage
- **No open ports**: Instances don't need inbound ports open. SSM communicates via the SSM agent's outbound HTTPS connection
- **Built-in logging**: Command output is captured and retrievable via the API
- **AWS-native**: Same mechanism AWS uses internally for fleet management
- The operator's local machine never directly connects to the EC2 instance

### Why Everything Is Pulled From S3 At Runtime

Every job explicitly downloads both the processor executable and input files from S3 before processing. This means:
- No assumption of pre-loaded data or tools on instances
- Spot instances work identically to on-demand
- Any instance can process any file
- Processor updates are instant — just upload a new version to the Deploy Source path
- Scaling up means adding bare instances; no manual setup beyond base AMI

### Why Per-Instance Batched Commands With `test_[TIMESTAMP].bat`

- **One SSM command per instance:** Reduces API calls and simplifies orchestration. All files assigned to an instance run in a single PowerShell session.
- **Per-file tracking via markers:** `FILE_START:`, `FILE_DONE:`, `FILE_FAILED:` in stdout let the app track individual file progress within a batch.
- **Unique batch file per run:** The timestamped `test_[TIMESTAMP].bat` ensures successive runs never collide. The batch file serves as an audit log of exactly what commands ran.

---

## Application Layout

The app uses a primary window with a tab-based workflow. The user progresses through tabs left to right, though they can jump back at any time.

```
┌─────────────────────────────────────────────────────────────────────────┐
│  S3 Batch Processor                                        [—] [□] [×] │
├─────────────────────────────────────────────────────────────────────────┤
│  [1. Select Files]  [2. Select Instances]  [3. Assign & Run]           │
├═════════════════════════════════════════════════════════════════════════┤
│                                                                         │
│                    (Active tab content here)                            │
│                                                                         │
├─────────────────────────────────────────────────────────────────────────┤
│  ✓ Connected: arn:aws:iam::123456:user/operator  │  us-east-1         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Tab 1: Select Files (S3 Browser)
- Lists all accessible S3 buckets
- Browse files within buckets with folder navigation
- Filter by file type (.tif/.tiff/.las toggle)
- Preview file metadata (size, last modified)
- Stage and commit files for processing
- Detects bucket region via `GetBucketLocation`

### Tab 2: Select Instances (EC2 Fleet)
- Discovers instances across all configured regions via `DescribeInstances`
- **Collapsible region groups** — each region is an expandable section showing the instance count (e.g. `us-east-1 (3)`)
- **Search bar** — filter instances by name (case-insensitive substring match)
- Shows instance ID, state, type, region, name tag
- Start/stop instances
- Select instances for processing
- **Add Spot Instances** panel: select region, pick launch template, set count — adds planned placeholders to Selected Instances (no AWS resources created yet)
- Auto-refresh every 30 seconds (toggleable; preserves planned instances)

### Tab 3: Assign & Run (Split View)
Upper panel (JobAssignment):
- Configuration fields: Binary (with S3 browse picker), Binary Dir, Input Dir, Job Log Dir, Output S3 Path, Command Args
- Input Dir controls where input files are downloaded on the EC2 instance (default: `C:\data`); directory is created automatically
- Command Args supports `{filename}` placeholder, replaced with the full input path (`<InputDir>\<file-name>`) per invocation
- Per-instance Output Path override on each instance card (blank = use global Output S3 Path)
- Assign files to instances manually via [+ Add Files] buttons or auto-distribute (round-robin)
- Validation gate: all files assigned, all instances have files, all config fields filled
- [Run Processing] button triggers execution

Lower panel (JobExecution):
- Pre-flight log: instance startup progress, SSM agent registration
- Progress bar with summary: "X / Y completed (Z success, W failed)"
- Results DataGrid: per-file status, duration, output, errors
- [Cancel Jobs] button during execution

---

## Configuration

App settings in `appsettings.json`:

```json
{
  "Aws": {
    "DefaultRegion": "us-east-1",
    "ScanRegions": ["us-east-1", "us-east-2", "us-west-1", "us-west-2",
                     "eu-west-1", "eu-west-2", "eu-central-1",
                     "ap-southeast-1", "ap-southeast-2", "ap-northeast-1"]
  },
  "Processing": {
    "JobLogDirectory": "C:\\processor\\jobs",
    "JobBatNamePrefix": "test_",
    "ProcessorDirectory": "C:\\processor",
    "InputDirectory": "C:\\data",
    "DeploySource": "s3://batchtest3-cbai/deploy/process.bat",
    "OutputS3Prefix": "s3://batchtest3-cbai/results/",
    "CommandArgs": "--file \"{filename}\"",
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

| Key | Description |
|---|---|
| `Aws:DefaultRegion` | Initial AWS region on startup |
| `Aws:ScanRegions` | Regions to scan for EC2 instances |
| `Processing:JobLogDirectory` | Directory on EC2 where `test_[TIMESTAMP].bat` files are written |
| `Processing:JobBatNamePrefix` | Prefix for generated batch file names (default: `test_`) |
| `Processing:ProcessorDirectory` | Directory on EC2 where the binary is downloaded to |
| `Processing:InputDirectory` | Directory on EC2 where input files are downloaded (default: `C:\data`); created automatically if missing |
| `Processing:DeploySource` | S3 URI of the binary (e.g. `s3://bucket/deploy/process.bat`) |
| `Processing:OutputS3Prefix` | S3 path where result files are uploaded |
| `Processing:CommandArgs` | Arguments template for binary invocation; `{filename}` is replaced with the full input path (`<InputDir>\<file-name>`) |
| `Processing:PollIntervalSeconds` | SSM status polling interval in seconds |
| `Ssm:CommandTimeoutSeconds` | SSM command timeout |
| `Preview:MaxFileSizeBytes` | Max bytes to fetch for S3 file preview |

---

## Solution Structure

```
S3BatchProcessor/
├── S3BatchProcessor.sln
├── README.md
├── PROJECT_SPEC.md
├── BatchProcessingSpec.md
│
├── src/
│   └── S3BatchProcessor.App/
│       ├── S3BatchProcessor.App.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── appsettings.json
│       │
│       ├── Models/
│       │   ├── S3BucketItem.cs
│       │   ├── S3ObjectItem.cs
│       │   ├── S3ItemType.cs
│       │   ├── Ec2InstanceItem.cs
│       │   ├── Ec2InstanceState.cs
│       │   ├── LaunchTemplateItem.cs
│       │   ├── JobAssignment.cs
│       │   ├── JobStatus.cs
│       │   └── JobResult.cs
│       │
│       ├── Services/
│       │   ├── IAwsConnectionService.cs / AwsConnectionService.cs
│       │   ├── IS3Service.cs / S3Service.cs
│       │   ├── IEc2Service.cs / Ec2Service.cs
│       │   ├── ISsmService.cs / SsmService.cs
│       │   └── IJobOrchestrationService.cs / JobOrchestrationService.cs
│       │
│       ├── ViewModels/
│       │   ├── MainViewModel.cs
│       │   ├── StatusBarViewModel.cs
│       │   ├── S3BrowserViewModel.cs
│       │   ├── Ec2ManagerViewModel.cs
│       │   ├── JobAssignmentViewModel.cs
│       │   └── JobExecutionViewModel.cs
│       │
│       ├── Views/
│       │   ├── MainWindow.xaml / .cs
│       │   ├── S3BrowserView.xaml / .cs
│       │   ├── Ec2ManagerView.xaml / .cs
│       │   ├── JobAssignmentView.xaml / .cs
│       │   └── JobExecutionView.xaml / .cs
│       │
│       ├── Converters/
│       │   ├── FileSizeConverter.cs
│       │   ├── BoolToVisibilityConverter.cs
│       │   ├── InverseBoolConverter.cs
│       │   ├── InstanceStateToColorConverter.cs
│       │   ├── JobStatusToColorConverter.cs
│       │   └── MultiValueArrayConverter.cs
│       │
│       └── Resources/
│           └── Styles.xaml
│
└── tests/
    └── S3BatchProcessor.Tests/
```

---

## Milestone Plan

### Milestone 1: S3 Browser ✅

**Goal**: Prove AWS connectivity and S3 read access through a working file browser GUI.

**Scope**: Credential chain connection, region selector, bucket listing, folder navigation, file filtering, multi-file selection with staging/commit, text preview.

### Milestone 2: EC2 Fleet Viewer ✅

**Goal**: Prove EC2 API access and SSM remote command execution.

**Scope**: Discover instances across all regions, display as cards with state, select instances for processing, SSM connectivity test, auto-refresh.

### Milestone 3: Assignment + Execution ✅

**Goal**: Complete the core workflow — assign files to instances, execute, and collect results.

**Scope**: Manual file-to-instance assignment with [+ Add Files] buttons, auto-distribute, deploy picker for processor selection from S3, validation gate, pre-flight instance startup with SSM agent check, one SSM command per instance (PowerShell with per-file tracking markers), progress polling, cancel support.

### Milestone 4: Spot Instance Support ✅

**Goal**: Launch spot instances on-demand and integrate them into the processing workflow.

**Scope**: "Add Spot Instances" panel in Tab 2 with region/template/count selection, deferred launch during pre-flight, auto-termination after execution, IAM permission requirements (`iam:PassRole`, `iam:CreateServiceLinkedRole`, `ec2:RunInstances`, `ec2:TerminateInstances`).

---

## Cross-Cutting Concerns

### Error Handling Strategy

- All AWS SDK calls wrapped in try/catch for `AmazonServiceException`
- Errors displayed inline (status bar or panel-specific error banners), NOT modal dialogs
- Specific error handling for access denied, S3 errors, SSM errors, network errors, credential errors
- Retry logic: use SDK built-in retry (default 3 retries with exponential backoff)

### Async Pattern

- All service methods are async (`Task<T>` return types)
- ViewModels use `async void` only for command handlers (as required by ICommand pattern)
- Long-running operations show loading indicators
- `CancellationToken` passed through to all AWS calls where possible
- UI thread updates via `Dispatcher.Invoke` for callbacks from background operations

### Logging

Use `Microsoft.Extensions.Logging` with `ILogger<T>` injected into services and ViewModels:
- Log all AWS API calls at Debug level
- Log errors at Error level with exception details
- Log user actions (bucket selected, files selected, job dispatched) at Information level
- Output to: Debug console (development), file log (production)

---

## What This App Does NOT Do

- **No authentication UI** — credentials configured externally via AWS CLI
- **No TIFF/LAS rendering** — these are processed on EC2, not viewed locally
- **No EC2 provisioning beyond spot** — on-demand instances are pre-existing; the app can launch spot instances via launch templates but does not create AMIs, VPCs, or other infrastructure
- **No scheduling or cron** — the operator manually triggers each processing run
- **No multi-user or collaboration** — single operator desktop tool
- **No database** — all state is transient (in memory during the session)
- **No persistent job history** — once the app closes, job history is gone (could be added later)

---

## Future Considerations

- **Job bat cleanup:** Remove or rotate old `test_*.bat` files on instances to save disk
- **Spot retry on reclaim:** Detect mid-job spot termination and retry on another instance
- **Auto-scaling:** Automatically request spot instances when job queue grows
- **Progress streaming:** Use SSM output streaming instead of polling for real-time logs
- **Result aggregation:** Tab to browse and download results from the output bucket
- **Cost tracking:** Estimate and display per-job cost based on instance type, runtime, and data transfer
- **Persistent job history:** Save job results across sessions (currently in-memory only)
