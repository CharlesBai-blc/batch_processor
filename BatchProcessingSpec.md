# Batch Processing Job Dispatch System — Specification

## Overview

A Windows desktop application (WPF, .NET 8, C#) that serves as a command center for dispatching geospatial batch processing jobs to AWS EC2 instances. The app never touches data directly — it orchestrates remote compute via AWS Systems Manager (SSM) and uses S3 as the shared file system.

## Architecture

Three components:

1. **Operator (local machine):** Runs the WPF app. Talks to AWS APIs over HTTPS using IAM credentials. Acts as a remote control — sends commands, monitors status, displays results.

2. **Compute (EC2 instances):** Windows Server instances that do the actual work. **Input data files are pulled from S3 per job.** The **job runner is not downloaded from S3:** for each data file, the SSM script **creates** a batch file on disk in a configured folder, named **`test_[TIMESTAMP].bat`**, containing the process command for that job only. The processor binary is expected on the instance (AMI, User Data, or manual install).

3. **Storage (S3):** Holds input data files and output results. **Per-job `.bat` files are not stored in or pulled from S3** — they exist only on the instance for the duration of the workflow (and may be retained on disk until cleaned up).

## S3 Layout

| Bucket | Purpose | Contents |
|---|---|---|
| `batchtest1-cbai` | Input data | `data/sample.txt`, `data/scan-001.tif`, `data/survey.las` |
| `batchtest2-cbai` | Input data | `data/sample2.txt`, `data/survey2.txt` |
| `batchtest3-cbai` | Output (typical) | `results/*.result` (job output). Processor and per-job `test_[TIMESTAMP].bat` are **not** sourced from S3 for each run. |

## EC2 Instance Requirements

### Base Image
- Windows Server AMI (2019 or 2022)
- SSM Agent pre-installed (default on AWS Windows AMIs)
- AWS CLI pre-installed (default on AWS Windows AMIs)

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
    process.exe          <- on instance (AMI / User Data / manual)
    jobs\                  <- configured directory for per-job bats
        test_<TIMESTAMP>.bat   <- one per data file; TIMESTAMP unique per job

C:\data\
    <input files>        <- pulled from S3 per job
    output\
        *.result         <- written by processor, uploaded to S3
```

## Instance Lifecycle

### On-Demand Instances
- Launched manually or from the app
- Persist between jobs
- Can be stopped when idle to save cost (EBS volume retained)
- Processor and accumulated `test_*.bat` files may persist across stop/start unless cleaned up

### Spot Instances
- Requested by the app via launch template
- Can be reclaimed by AWS with 2 minutes notice
- Treated as fully disposable — **inputs/outputs** via S3; processor and per-job bats recreated from AMI/User Data as needed
- App must handle mid-job termination (detect failure, retry on another instance)

### Instance Startup Sequence
Regardless of on-demand or spot, every instance follows this sequence on boot:

1. SSM Agent starts and registers with Systems Manager
2. User Data script runs (recommended):
   - Creates `C:\processor\`, `C:\processor\jobs\` (or configured job bat directory), and `C:\data\`
   - Places `process.exe` (or equivalent) under `C:\processor\` without using per-job S3 download of a runner `.bat`
3. Instance appears in SSM Fleet Manager (ready for jobs)

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
| `ec2:StartInstances` | Pre-flight auto-start |
| `ec2:RunInstances` | Spot/on-demand instance launch (future) |
| `ssm:SendCommand` | Tab 3 job dispatch |
| `ssm:GetCommandInvocation` | Tab 3 status polling |
| `ssm:CancelCommand` | Tab 3 job cancellation |

## Job Dispatch Flow

### Pre-Flight
1. App verifies AWS credentials via `sts:GetCallerIdentity`
2. App checks target instance is running via `ec2:DescribeInstances`
3. If instance is stopped, app starts it via `ec2:StartInstances` and waits for SSM registration

### Job Execution
A job is a single SSM `SendCommand` call targeting one EC2 instance for one input file. **Sends are serialized per `InstanceId`** so two jobs on the same box do not race on shared local files during dispatch.

The command payload is a PowerShell script. **Conceptual flow:**

```powershell
$ErrorActionPreference = 'Stop'
$jobBatDir = 'C:\processor\jobs'
$ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$batPath = Join-Path $jobBatDir "test_$ts.bat"

New-Item -ItemType Directory -Force -Path 'C:\data','C:\data\output',$jobBatDir | Out-Null

Copy-S3Object -BucketName '<source-bucket>' -Key 'data/<filename>' -LocalFile 'C:\data\<filename>' -Region '<bucket-region>'

# Per-job bat — holds the run command for this data file only (not from S3)
@"
@echo off
"C:\processor\process.exe" --file "<filename>"
"@ | Set-Content -Path $batPath -Encoding ASCII

cmd /c "`"$batPath`""

Write-S3Object -BucketName 'batchtest3-cbai' -Key 'results/result_<filename>' -File 'C:\data\output\result_<filename>' -Region '<output-bucket-region>'
```

**Hard rules:** No `Copy-S3Object` of a shared `process.bat` / job runner from S3 per job. One **`test_[TIMESTAMP].bat`** per data file, written locally, then executed.

Note: Uses `Copy-S3Object` / `Write-S3Object` from the AWSPowerShell module (pre-installed on AWS Windows AMIs) instead of the AWS CLI, which may not be present.

The process.exe binary:
1. Reads the input file from `C:\data\<filename>`
2. Processes it (real logic TBD, stub simulates with a delay)
3. Writes result to `C:\data\output\result_<filename>`
4. Prints progress and status to stdout, errors to stderr
5. Exits with code 0 on success

The SSM script handles all S3 transfers (download input, upload result). The processor is a pure file-in/file-out tool with no AWS dependencies.

### Monitoring
- App polls `ssm:GetCommandInvocation` every 3–5 seconds
- SSM returns status: `InProgress`, `Success`, `Failed`, `Cancelled`, `TimedOut`
- SSM returns stdout and stderr from the script (includes process.exe console output)
- App updates UI with status, logs, and progress

### Cancellation
- App calls `ssm:CancelCommand` with the command ID
- SSM delivers a cancellation signal to the instance
- Currently running process may not stop immediately (SSM cancels the script, not the child process)

### Failure Handling
| Failure | Detection | Recovery |
|---|---|---|
| S3 download fails | Copy-S3Object throws, SSM reports `Failed` | Retry job |
| process.exe crashes | Non-zero exit code in SSM output | Inspect stderr, fix and retry |
| Instance terminated (spot reclaim) | `GetCommandInvocation` returns error or `Failed` | Retry on another instance |
| SSM Agent not responding | `SendCommand` fails or times out | Start instance, wait for SSM registration |
| Network issue | AWS API call throws exception | Retry with backoff |

## Processing Executable Contract

The process.exe binary must conform to this interface:

### Arguments
```
process.exe --file <filename>
```

| Argument | Description |
|---|---|
| `--file` | Name of the input file (not a full path). Located at `C:\data\<filename>` |

### Expected Behavior
1. Read input from `C:\data\<filename>`
2. Process the file
3. Write result to `C:\data\output\result_<filename>`
4. Print progress and status to stdout
5. Print errors to stderr

The processor does NOT handle S3 uploads. The SSM script handles all S3 transfers.

### Exit Codes
| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Invalid arguments |
| 2 | Input file not found |
| 3 | Processing failed |

### Deployment
- **Processor:** Lives on the instance at a known path (e.g. `C:\processor\process.exe`). Delivered via AMI, User Data, or ops — **not** as a per-job S3 pull of a shared runner `.bat`.
- **Per-job bats:** `test_[TIMESTAMP].bat` files are **generated on the instance** by each SSM script; they are **not** pulled from S3.

### Optional bootstrap
One-time S3 copy of `process.exe` in User Data is allowed as infrastructure; it does **not** replace the per-job `test_[TIMESTAMP].bat` model.

## WPF App Tabs

### Tab 1: S3 Browser
- Lists all accessible S3 buckets
- Browse files within buckets
- Preview file metadata (size, last modified, storage class)
- Detects bucket region via `GetBucketLocation`

### Tab 2: EC2 Fleet
- Discovers instances across configured regions via `DescribeInstances`
- Shows instance ID, state, type, region, SSM status
- Start/stop instances
- Shows IAM role attachment status
- Future: launch new on-demand or spot instances

### Tab 3: Job Dispatch
- Configure job bat directory and prefix (`test_[TIMESTAMP].bat`), processor path, output prefix
- Select input file(s) from Tab 1
- Select target instance(s) from Tab 2
- Dispatch jobs (one SSM command per file; sends serialized per instance)
- Live status updates via SSM polling
- View stdout/stderr logs
- Cancel running jobs
- Job history with results

## Configuration

App settings align with `PROJECT_SPEC.md` / `appsettings.json` in the repo. **Target shape** (names may match implementation):

```json
{
  "Aws": {
    "DefaultRegion": "us-east-1",
    "ScanRegions": ["us-east-1", "us-east-2", "us-west-1", "us-west-2"]
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
  }
}
```

There is **no** `DeploySource` / deploy key for a shared job `.bat` — that workflow is retired in favor of local `test_[TIMESTAMP].bat` generation.

## Future Considerations

- **Cleanup:** Periodically remove old `test_*.bat` files from the job directory on instances
- **Batch dispatch:** Send multiple files to multiple instances in parallel (with per-instance send serialization)
- **Auto-scaling:** Automatically request spot instances when job queue grows
- **Progress streaming:** Use SSM output streaming instead of polling for real-time logs
- **Version pinning:** Pin processor via AMI or install path; not via per-job S3 runner `.bat`
- **Result aggregation:** Tab to browse and download results from the output bucket
- **Cost tracking:** Estimate and display per-job cost based on instance type, runtime, and data transfer
