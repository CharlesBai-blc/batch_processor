# Batch Processing Job Dispatch System — Specification

## Overview

A Windows desktop application (WPF, .NET 8, C#) that serves as a command center for dispatching geospatial batch processing jobs to AWS EC2 instances. The app never touches data directly — it orchestrates remote compute via AWS Systems Manager (SSM) and uses S3 as the shared file system.

## Architecture

Three components:

1. **Operator (local machine):** Runs the WPF app. Talks to AWS APIs over HTTPS using IAM credentials. Acts as a remote control — sends commands, monitors status, displays results.

2. **Compute (EC2 instances):** Windows Server instances that do the actual work. Input data files and the processor executable are pulled from S3 at the start of each job. For each set of files assigned to an instance, the SSM-delivered script creates a batch file on disk named `test_[TIMESTAMP].bat`, containing the process commands for all assigned files with per-file tracking markers.

3. **Storage (S3):** Holds input data files, the processor executable (Deploy Source), and output results. Per-job batch files (`test_[TIMESTAMP].bat`) are generated on the instance at runtime and uploaded to the output S3 prefix after execution as an audit log.

## S3 Layout

| Bucket | Purpose | Contents |
|---|---|---|
| `batchtest1-cbai` | Input data | `data/sample.txt`, `data/scan-001.tif`, `data/survey.las` |
| `batchtest2-cbai` | Input data | `data/sample2.txt`, `data/survey2.txt` |
| `batchtest3-cbai` | Deployment + Output | `deploy/process.bat` (processor executable), `results/*.result` (job output) |

## EC2 Instance Requirements

### Base Image
- Windows Server AMI (2019 or 2022)
- SSM Agent pre-installed (default on AWS Windows AMIs)
- AWS CLI installed (required for `aws s3 cp` commands in the generated batch files)

### IAM Instance Profile
An IAM role attached to the instance with:
- `AmazonSSMManagedInstanceCore` managed policy (SSM Agent communication)
- `s3:GetObject` on input buckets and deploy bucket (pull processor + input data per job)
- `s3:PutObject` on output bucket (upload results)

### Network
- Outbound internet access OR VPC endpoints for SSM and S3 services
- No inbound ports required (SSM is outbound-initiated)

### On-Disk Layout

```
C:\processor\
    process.bat          <- downloaded from S3 DeploySource at start of each job
    jobs\                  <- configured job log directory
        test_<TIMESTAMP>.bat   <- one per SSM command (per instance); contains ALL operations as audit log

<InputDir>\              <- configurable via "Input Dir" field (default: C:\data); created automatically
    <input files>        <- pulled from S3 per job
    output\
        result_<filename>  <- written by processor, uploaded to S3 by the batch file
```

## Instance Lifecycle

### On-Demand Instances
- Persist between jobs
- Can be stopped when idle to save cost (EBS volume retained)
- Accumulated `test_*.bat` files may persist across stop/start unless cleaned up

### Spot Instances
- Configured in Tab 2 via "Add Spot Instances" panel (region, launch template, count)
- Added as planned placeholders — no AWS resources created until the user clicks Run
- Launched during pre-flight via `ec2:RunInstances` with spot market options
- Auto-terminated after job execution completes (success or failure)
- Treated as fully disposable — processor downloaded from Deploy Source, inputs from S3
- Can be reclaimed by AWS with 2 minutes notice

### Instance Startup Sequence
1. SSM Agent starts and registers with Systems Manager
2. User Data script runs (recommended):
   - Creates `C:\processor\`, `C:\processor\jobs\`, and `C:\data\` directories
   - Ensures AWS CLI is available
3. Instance appears in SSM Fleet Manager (ready for jobs)
4. Processor executable is downloaded from S3 DeploySource at the start of each job

Estimated time from boot to ready: 1–3 minutes.

## Operator IAM Permissions

The IAM user running the WPF app needs:

| Permission | Used By |
|---|---|
| `sts:GetCallerIdentity` | App startup identity check |
| `s3:ListAllMyBuckets` | Tab 1 bucket listing |
| `s3:ListBucket` | Tab 1 file browsing |
| `s3:GetObject` | Tab 1 file preview |
| `s3:GetBucketLocation` | Tab 1 region detection |
| `ec2:DescribeInstances` | Tab 2 instance discovery |
| `ec2:DescribeLaunchTemplates` | Tab 2 spot instance template listing |
| `ec2:RunInstances` | Pre-flight spot instance launching |
| `ec2:TerminateInstances` | Post-execution spot instance cleanup |
| `ec2:StartInstances` | Pre-flight auto-start |
| `ec2:StopInstances` | Tab 2 stop instances |
| `iam:PassRole` | Required to pass instance profile role when launching spot instances |
| `iam:CreateServiceLinkedRole` | One-time: create EC2 Spot service-linked role |
| `ssm:SendCommand` | Tab 3 job dispatch |
| `ssm:GetCommandInvocation` | Tab 3 status polling |
| `ssm:CancelCommand` | Tab 3 job cancellation |
| `ssm:DescribeInstanceInformation` | Pre-flight SSM agent check |

## Job Dispatch Flow

### Pre-Flight
1. App validates all files are assigned and all instances have files
2. **If any planned spot instances exist:** App launches them via `ec2:RunInstances` with spot market options. Placeholder instances are updated with real IDs.
3. App checks target instances are running via `ec2:DescribeInstances`
4. If any instance is stopped, app starts it via `ec2:StartInstances` and polls until Running (5 min timeout)
5. App polls `ssm:DescribeInstanceInformation` until SSM agent is online on all instances (5 min timeout)

### Job Execution
One SSM `SendCommand` call per instance, containing all files assigned to that instance. Commands to different instances are dispatched in parallel.

The command payload is a PowerShell script that writes a self-contained bat file and runs it. The bat file contains **all operations** as an audit log:

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
REM {filename} is replaced with the full input path: <InputDir>\<file-name>
echo FILE_START:<file1>
"<BinaryDir>\<BinaryName>" <resolved-args-for-file1>
if %ERRORLEVEL% EQU 0 (echo FILE_DONE:<file1>) else (echo FILE_FAILED:<file1>)
echo FILE_START:<file2>
"<BinaryDir>\<BinaryName>" <resolved-args-for-file2>
if %ERRORLEVEL% EQU 0 (echo FILE_DONE:<file2>) else (echo FILE_FAILED:<file2>)

REM === Upload results ===
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

All S3 operations use `aws s3 cp` (AWS CLI) inside the bat file. The bat is self-contained — every operation is visible in the audit log.

### Monitoring
- App polls `ssm:GetCommandInvocation` every 3 seconds (configurable)
- SSM returns overall status: `Pending`, `InProgress`, `Success`, `Failed`, `Cancelled`, `TimedOut`
- App parses stdout for per-file markers: `FILE_START:`, `FILE_DONE:`, `FILE_FAILED:`
- When overall command completes, files still Pending/InProgress inherit the command's final status
- UI shows per-file status, duration, output, and errors in a results DataGrid

### Cancellation
- App calls `ssm:CancelCommand` with the command ID
- SSM delivers a cancellation signal to the instance
- All in-flight results marked as Failed with "Cancelled by user" output

### Failure Handling
| Failure | Detection | Recovery |
|---|---|---|
| S3 download fails | `aws s3 cp` fails, SSM reports `Failed` | Retry job |
| Processor crashes | Non-zero exit code, `FILE_FAILED:` marker | Inspect stderr, fix and retry |
| Instance terminated (spot reclaim) | `GetCommandInvocation` returns error or `Failed` | Retry on another instance |
| SSM Agent not responding | `SendCommand` fails or times out | Start instance, wait for SSM |
| Network issue | AWS API call throws exception | Retry with backoff |

## Binary Executable Contract

### Invocation
The binary is invoked once per file. Arguments are configurable via the **Command Args** field in Tab 3. The `{filename}` placeholder is replaced with the full input path (`<InputDir>\<file-name>`).

Default Command Args: `--file "{filename}"`

```
# Default (Input Dir = C:\data): "C:\processor\process.bat" --file "C:\data\sample.txt"
# Custom:  "C:\processor\myprocessor.exe" --mode fast --input "C:\data\sample.txt" --threads 4
```

### Expected Behavior
1. Read input from `<InputDir>\<filename>` (bat file downloads files here; Input Dir is configurable, default `C:\data`)
2. Process the file
3. Write result to `<InputDir>\output\result_<filename>` (bat file uploads from here)
4. Print progress and status to stdout
5. Print errors to stderr
6. Return exit code 0 on success, non-zero on failure

The binary does NOT handle S3 transfers. The bat file handles all downloads and uploads.

### Deployment
- Binary stored in S3 at a configurable Deploy Source path (e.g. `s3://batchtest3-cbai/deploy/process.bat`)
- Downloaded to each instance at the start of every job via `aws s3 cp` inside the bat file
- Operator can change Deploy Source via the S3 browse picker in the Assignment tab
- Per-job batch files (`test_[TIMESTAMP].bat`) contain all operations and are uploaded to the output S3 path as an audit log

## WPF App Tabs

### Tab 1: S3 Browser
- Lists all accessible S3 buckets (grouped by region)
- Browse files within buckets with folder navigation and breadcrumbs
- Filter by file type (.tif/.tiff/.las toggle)
- Preview text file content (up to 1 MB)
- Two-stage selection: stage files (checkbox), then commit for processing
- Detects bucket region via `GetBucketLocation`

### Tab 2: EC2 Fleet
- Discovers instances across all configured regions via `DescribeInstances`
- **Collapsible region groups** — each region is an expandable section showing instance count (e.g. `us-east-1 (3)`)
- **Search bar** — filter instances by name (case-insensitive substring match)
- Shows instance ID, name tag, state (color-coded), type, region, AZ, public IP, launch time
- Start/stop instances
- Select instances for processing
- **Add Spot Instances** panel: select region, pick launch template, set count — adds planned placeholders to Selected Instances (no AWS resources created yet; launched during pre-flight)
- Auto-refresh every 30 seconds (toggleable; preserves planned instances)

### Tab 3: Assign & Run (Split View)
Upper panel (JobAssignment):
- Configuration: Binary (with S3 browse picker), Binary Dir, Input Dir, Job Log Dir, Output S3 Path, Command Args
- Input Dir controls where input files are downloaded on EC2 (default: `C:\data`); created automatically if missing
- Command Args uses `{filename}` placeholder, replaced with the full input path (`<InputDir>\<file-name>`) per invocation
- Per-instance Output Path override on each instance card (blank = use global)
- Assign files to instances via [+ Add Files] buttons or auto-distribute (round-robin)
- Validation: all files assigned, all instances have files, all config fields filled

Lower panel (JobExecution):
- Pre-flight log with per-instance startup and SSM agent status
- Progress bar: "X / Y completed (Z success, W failed)"
- Results DataGrid: per-file status, duration, output, errors
- Cancel running jobs

## Configuration

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

## Future Considerations

- **Job bat cleanup:** Remove or rotate old `test_*.bat` files on instances to save disk
- **Spot retry on reclaim:** Detect mid-job spot termination and retry on another instance
- **Auto-scaling:** Automatically request spot instances when job queue grows
- **Progress streaming:** Use SSM output streaming instead of polling for real-time logs
- **Result aggregation:** Tab to browse and download results from the output bucket
- **Cost tracking:** Estimate and display per-job cost based on instance type, runtime, and data transfer
- **Persistent job history:** Save job results across sessions (currently in-memory only)
