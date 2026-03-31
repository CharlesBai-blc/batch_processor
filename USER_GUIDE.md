# S3 Batch Processor — User Guide

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [AWS Infrastructure Setup](#aws-infrastructure-setup)
4. [App Configuration](#app-configuration)
5. [Using the App](#using-the-app)
6. [Troubleshooting](#troubleshooting)

---

## Overview

The S3 Batch Processor is a Windows desktop application that dispatches geospatial batch processing jobs to AWS EC2 instances. It never touches data directly — it orchestrates remote compute via AWS Systems Manager (SSM) and uses S3 as the shared file system.

The workflow:
1. **Browse S3** — select input files from your S3 buckets
2. **Select EC2 instances** — choose on-demand instances and/or plan spot instances
3. **Assign & Run** — distribute files across instances, configure the binary and arguments, and execute

Each job downloads the binary from S3, runs it against assigned input files, and uploads results back to S3. A timestamped batch file (`test_*.bat`) is generated per instance per job and uploaded as an audit log.

---

## Prerequisites

### Local Machine

- **Windows 10/11** with .NET 8 runtime
- **AWS CLI credentials** configured — the app reads credentials from the standard AWS credential chain:
  - Environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`)
  - Shared credentials file (`~/.aws/credentials`)
  - AWS CLI configured via `aws configure`
- **Outbound HTTPS** access to AWS APIs (S3, EC2, SSM, STS, IAM)

### AWS Account

- An IAM user (or role) with the permissions listed in [Operator IAM Permissions](#operator-iam-permissions)
- One or more S3 buckets containing input data
- An S3 bucket (or prefix) for the binary executable and job output
- EC2 instances that meet the [instance requirements](#ec2-instance-requirements), or launch templates for spot instances

---

## AWS Infrastructure Setup

### S3 Buckets

You need at least two logical areas in S3:

| Purpose | Example | Contents |
|---|---|---|
| **Input data** | `s3://mybucket/data/` | Files to be processed (`.tif`, `.las`, `.txt`, etc.) |
| **Deploy + Output** | `s3://mybucket-deploy/deploy/process.bat` | Your binary executable |
| **Output** | `s3://mybucket-deploy/results/` | Job results and audit batch files |

Input data can span multiple buckets. The deploy source and output prefix can be in the same bucket or different ones.

### EC2 Instance Requirements

#### Base Image
- **Windows Server AMI** (2019 or 2022)
- SSM Agent pre-installed (default on AWS Windows AMIs)
- AWS CLI installed (required for `aws s3 cp` commands in the generated batch files)

#### IAM Instance Profile

Create an IAM role (e.g., `BatchProcessorEC2Role`) and attach it as an instance profile. The role needs:

| Permission | Resource | Purpose |
|---|---|---|
| `AmazonSSMManagedInstanceCore` | (managed policy) | SSM Agent communication |
| `s3:GetObject` | Input buckets + deploy bucket | Pull binary and input files |
| `s3:PutObject` | Output bucket/prefix | Upload results and batch audit files |

Example inline policy for S3:
```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Effect": "Allow",
            "Action": "s3:GetObject",
            "Resource": [
                "arn:aws:s3:::mybucket-input/*",
                "arn:aws:s3:::mybucket-deploy/deploy/*"
            ]
        },
        {
            "Effect": "Allow",
            "Action": "s3:PutObject",
            "Resource": "arn:aws:s3:::mybucket-deploy/results/*"
        }
    ]
}
```

#### Network
- Outbound internet access **OR** VPC endpoints for SSM and S3
- No inbound ports required (SSM is outbound-initiated)

#### On-Disk Layout

The binary expects this directory structure on each EC2 instance:

```
C:\processor\
    process.bat          <- binary downloaded from S3 at the start of each job
    jobs\                <- batch audit files written here
        test_*.bat       <- one per SSM command per instance; contains ALL operations as audit log

<InputDir>\              <- configurable via "Input Dir" field (default: C:\data)
    <input files>        <- pulled from S3 per job
    output\
        result_<filename> <- written by processor, uploaded to S3 by the batch file
```

These directories are created automatically by the batch file at job start. If you want them pre-created, add a User Data script to your instance/launch template:

```powershell
<powershell>
New-Item -ItemType Directory -Force -Path 'C:\processor','C:\processor\jobs','C:\data','C:\data\output'
</powershell>
```

### Launch Templates (for Spot Instances)

If you plan to use spot instances, create an EC2 Launch Template in each region you'll use:

1. Go to **EC2 > Launch Templates > Create launch template**
2. Configure:
   - **AMI**: Windows Server 2019 or 2022
   - **Instance type**: Your preferred type (e.g., `m5.large`, `c5.xlarge`)
   - **IAM instance profile**: The `BatchProcessorEC2Role` created above
   - **Security group**: Allow outbound HTTPS (or use VPC endpoints)
   - **User data** (optional but recommended):
     ```powershell
     <powershell>
     New-Item -ItemType Directory -Force -Path 'C:\processor','C:\processor\jobs','C:\data','C:\data\output'
     </powershell>
     ```
3. Give it a descriptive name (e.g., `BatchProcessor-Spot-m5large`)

The app will list your launch templates when you select a region in the spot instance panel.

### Operator IAM Permissions

The IAM user running the WPF app needs these permissions:

| Permission | Purpose |
|---|---|
| `sts:GetCallerIdentity` | App startup identity check (status bar) |
| `s3:ListAllMyBuckets` | Tab 1: list buckets |
| `s3:ListBucket` | Tab 1: browse files in buckets |
| `s3:GetObject` | Tab 1: preview text files |
| `s3:GetBucketLocation` | Tab 1: detect bucket region |
| `ec2:DescribeInstances` | Tab 2: discover instances |
| `ec2:DescribeLaunchTemplates` | Tab 2: list templates for spot instances |
| `ec2:RunInstances` | Pre-flight: launch spot instances |
| `ec2:TerminateInstances` | Post-execution: terminate spot instances |
| `ec2:StartInstances` | Pre-flight: auto-start stopped instances |
| `ec2:StopInstances` | Tab 2: stop instances |
| `iam:PassRole` | Required to pass instance profile role when launching spot instances |
| `iam:CreateServiceLinkedRole` | One-time: create EC2 Spot service-linked role |
| `ssm:SendCommand` | Tab 3: dispatch jobs |
| `ssm:GetCommandInvocation` | Tab 3: poll job status |
| `ssm:CancelCommand` | Tab 3: cancel running jobs |
| `ssm:DescribeInstanceInformation` | Pre-flight: check SSM agent is online |

#### Setting Up the IAM User

1. Go to **IAM > Users > Create user** (or use an existing one)
2. Attach the following managed policies:
   - `AmazonS3ReadOnlyAccess` (covers ListBuckets, ListBucket, GetObject, GetBucketLocation)
   - `AmazonSSMFullAccess` (covers SendCommand, GetCommandInvocation, CancelCommand, DescribeInstanceInformation)
3. Create an inline policy for EC2 and IAM:

```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Sid": "EC2Management",
            "Effect": "Allow",
            "Action": [
                "ec2:DescribeInstances",
                "ec2:DescribeLaunchTemplates",
                "ec2:RunInstances",
                "ec2:TerminateInstances",
                "ec2:StartInstances",
                "ec2:StopInstances"
            ],
            "Resource": "*"
        },
        {
            "Sid": "PassRoleForSpot",
            "Effect": "Allow",
            "Action": "iam:PassRole",
            "Resource": "arn:aws:iam::<ACCOUNT_ID>:role/BatchProcessorEC2Role"
        },
        {
            "Sid": "SpotServiceLinkedRole",
            "Effect": "Allow",
            "Action": "iam:CreateServiceLinkedRole",
            "Resource": "*",
            "Condition": {
                "StringEquals": {
                    "iam:AWSServiceName": "spot.amazonaws.com"
                }
            }
        },
        {
            "Sid": "StsIdentity",
            "Effect": "Allow",
            "Action": "sts:GetCallerIdentity",
            "Resource": "*"
        }
    ]
}
```

Replace `<ACCOUNT_ID>` with your 12-digit AWS account ID, and `BatchProcessorEC2Role` with the name of your EC2 instance profile role.

4. Generate access keys and configure them locally:
```
aws configure
```

---

## App Configuration

Configuration lives in `appsettings.json` alongside the application executable. All values have reasonable defaults.

```json
{
  "Aws": {
    "DefaultRegion": "us-east-1",
    "ScanRegions": [
      "us-east-1", "us-east-2", "us-west-1", "us-west-2",
      "eu-west-1", "eu-west-2", "eu-central-1",
      "ap-southeast-1", "ap-southeast-2", "ap-northeast-1"
    ]
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

### Configuration Reference

#### `Aws` Section

| Key | Default | Description |
|---|---|---|
| `DefaultRegion` | `us-east-1` | Default AWS region for API calls |
| `ScanRegions` | 10 regions (see above) | Regions to scan for EC2 instances in Tab 2. Add or remove regions depending on where you run instances. More regions = slower scan. |

#### `Processing` Section

| Key | Default | Description |
|---|---|---|
| `ProcessorDirectory` | `C:\processor` | Path on EC2 instances where the binary is downloaded to. The deploy source file is saved as `<ProcessorDirectory>\<filename>`. |
| `InputDirectory` | `C:\data` | Path on EC2 instances where input files are downloaded. Created automatically if missing. Output goes to `<InputDirectory>\output\`. |
| `DeploySource` | `s3://batchtest3-cbai/deploy/process.bat` | S3 URI of the binary. Can also be changed at runtime via the S3 browse picker in Tab 3. |
| `OutputS3Prefix` | `s3://batchtest3-cbai/results/` | S3 URI path where results and audit batch files are uploaded. Must end with `/`. |
| `CommandArgs` | `--file "{filename}"` | Arguments passed to the binary for each file. `{filename}` is replaced with the full input path (e.g. `C:\data\sample.txt`). |
| `JobLogDirectory` | `C:\processor\jobs` | Path on EC2 instances where per-job batch files (`test_*.bat`) are written. |
| `JobBatNamePrefix` | `test_` | Prefix for generated batch file names. Full name: `<prefix><timestamp>.bat`. |
| `PollIntervalSeconds` | `3` | How often (in seconds) the app polls SSM for job status updates during execution. Lower = more responsive but more API calls. |

#### `Ssm` Section

| Key | Default | Description |
|---|---|---|
| `CommandTimeoutSeconds` | `600` | SSM command timeout (10 minutes). If a command hasn't completed within this time, SSM marks it as TimedOut. Increase for very large files. |

#### `Preview` Section

| Key | Default | Description |
|---|---|---|
| `MaxFileSizeBytes` | `1048576` | Maximum file size (1 MB) for text preview in the S3 browser. Files larger than this won't show a preview. |

### Changing Configuration at Runtime

Some configuration values can be overridden in the app's UI (Tab 3) without editing `appsettings.json`:

- **Binary**: Click "Browse S3..." to pick a different binary executable
- **Binary Dir**: Edit the text field directly
- **Input Dir**: Edit the text field directly (where input files download on EC2)
- **Job Log Dir**: Edit the text field directly
- **Output S3 Path**: Edit the text field directly
- **Command Args**: Edit the text field directly (use `{filename}` as placeholder)

Each instance card also has an **Output Path** field that overrides the global Output S3 Path for that specific instance. Leave it blank to use the global value.

These runtime changes are not persisted — they reset to `appsettings.json` values on restart.

---

## Using the App

### Tab 1: S3 Browser — Select Input Files

This tab lets you browse your S3 buckets and select files for processing.

**Layout**: Three columns — bucket list (left), file browser (center), staging/selection pane (right).

#### Step-by-step:

1. **Buckets load automatically** on startup, grouped by region. Click a bucket to browse its contents.

2. **Navigate folders** by double-clicking. Use the breadcrumb bar or "Back" button to go up. The filter bar shows item counts and has a **"Filter TIFF/LAS only"** checkbox for geospatial workflows.

3. **Stage files** by checking the checkbox next to each file you want to process. Staged files appear in the "Staged" section on the right. You can stage files from multiple buckets — the bucket name is tracked per file.

4. **Commit staged files** by clicking **"Select for processing"**. This moves them to the "Selected for Processing" section. Only committed files carry forward to Tab 3.

5. Click **"Continue →"** to proceed to Tab 2 (or switch tabs manually).

**Tips:**
- Click a text file to preview its contents (up to 1 MB)
- Use the header checkbox to check/uncheck all visible files at once
- You can stage files from different buckets across different regions

### Tab 2: EC2 Fleet — Select Instances

This tab discovers and displays your EC2 instances. You select which instances will participate in the job.

**Layout**: Instance cards (left), selected instances pane (right).

#### Using On-Demand Instances:

1. **Instances load automatically** on startup, scanned across all configured regions. Instances are grouped by region in **collapsible sections** — click a region header to expand/collapse it. Each section shows the instance count (e.g. `us-east-1 (3)`).

2. **Search** — type in the search bar at the top to filter instances by name. The filter is case-insensitive and matches any part of the instance name. Clear the search to show all instances again.

3. **Click "Select"** on an instance card to add it to the Selected Instances pane (right side). Click the **X** next to a selected instance to remove it.

4. **Start/stop** instances via right-click or the buttons on each card. The app can auto-start stopped instances during pre-flight (Tab 3), so you don't have to start them manually.

5. **Auto-refresh** is enabled by default (every 30 seconds). Toggle it off with the checkbox if needed.

#### Using Spot Instances:

Spot instances are cheaper but can be reclaimed by AWS. They are planned in Tab 2 but only launched when you click "Run Processing" in Tab 3.

1. **Expand** the "Add Spot Instances" panel at the bottom of the left column.

2. **Select a region** from the dropdown. The app loads available launch templates for that region.

3. **Select a launch template** from the dropdown. This determines the AMI, instance type, and IAM profile.

4. **Set the count** (how many spot instances to request).

5. **Click "Add Spot Instances"**. Placeholder entries appear in the Selected Instances pane marked as "Pending". **No AWS resources are created yet** — these are just plans.

6. You can add spot instances from multiple regions with different templates.

7. Click **"Continue →"** to proceed to Tab 3.

### Tab 3: Assign & Run — Configure, Assign Files, Execute

This tab is split into two areas: **Job Assignment** (upper) and **Job Execution** (lower, appears during/after a run).

#### Configuration (top bar):

| Field | Description | How to set |
|---|---|---|
| **Binary** | S3 URI of the binary executable | Click "Browse S3..." to navigate and select. Pre-filled from `appsettings.json`. |
| **Binary Dir** | Directory on EC2 where binary is saved | Default: `C:\processor`. Rarely needs changing. |
| **Input Dir** | Directory on EC2 where input files are downloaded | Default: `C:\data`. Created automatically if it doesn't exist. |
| **Job Log Dir** | Directory on EC2 for batch audit files | Default: `C:\processor\jobs`. |
| **Output S3 Path** | S3 path for result uploads | Default from config. Format: `s3://bucket/prefix/`. Must end with `/`. |
| **Command Args** | Arguments passed to the binary | Default: `--file "{filename}"`. The `{filename}` placeholder is replaced with the full input path (e.g. `C:\data\sample.txt`). |

Each instance card also has an **Output Path** field. Fill it in to override the global Output S3 Path for that instance only (e.g., to send different instances' results to different S3 locations). Leave blank to use the global value.

#### Assigning Files to Instances:

Your selected files appear in the **"Unassigned Files"** pool (left). Your selected instances appear as cards (right), each with an **"+ Add Files"** button.

**Manual assignment:**
1. Select one or more files in the unassigned pool (Ctrl+click or Shift+click for multi-select)
2. Click **"+ Add Files"** on the target instance card
3. Files move from the pool to that instance's card

**Auto-distribute:**
- Click **"Auto-Distribute"** to round-robin all unassigned files across instances evenly

**Remove files:**
- Click the **X** next to a file chip on an instance card to move it back to unassigned

#### Validation:

Before running, the app checks:
- All files are assigned (none left in the unassigned pool)
- All instances have at least one file
- Binary deploy source is set
- Input directory is set
- Output S3 path is set
- Command args is set

If validation fails, an orange banner appears listing what needs to be fixed.

#### Running:

1. Click **"Run Processing ▶"**

2. **Pre-flight** begins (yellow panel):
   - **Planned spot instances** are launched via `ec2:RunInstances` with spot market options
   - **Stopped instances** are started automatically
   - Polls until all instances reach **Running** state (5 min timeout)
   - Polls until **SSM agent** is registered on all instances (5 min timeout)

3. **Execution** begins:
   - One SSM command per instance, dispatched in parallel
   - Each command writes a self-contained bat file (with all operations: downloads, execution, uploads) and runs it
   - Progress bar shows: "X / Y completed (Z success, W failed)"
   - Results DataGrid shows per-file status, duration, output, and errors

4. **After completion**:
   - Results are displayed in the DataGrid
   - Spot instances (if any) are **automatically terminated**
   - On-demand instances remain running (stop manually from Tab 2 if desired)

#### Cancellation:

- Click the **Cancel** button during execution
- All in-flight SSM commands are cancelled
- All pending/in-progress files are marked as Failed with "Cancelled by user"

### Binary Executable Contract

Your binary (`.bat`, `.exe`, or any executable) is invoked once per file with the arguments you specify in the **Command Args** field. The `{filename}` placeholder is replaced with the full input path (e.g. `C:\data\sample.txt`).

**Default invocation (Command Args = `--file "{filename}"`, Input Dir = `C:\data`):**
```
C:\processor\process.bat --file "C:\data\sample.txt"
```

**Custom example (Command Args = `--mode fast --input "{filename}" --threads 4`):**
```
C:\processor\myprocessor.exe --mode fast --input "C:\data\sample.txt" --threads 4
```

The binary path is always prepended automatically from the Binary + Binary Dir fields. You only control the arguments via Command Args.

**Expected behavior:**
1. Read input from `<InputDir>\<filename>` (the bat file downloads files here; Input Dir is configurable, default `C:\data`)
2. Process the file
3. Write result to `<InputDir>\output\result_<filename>` (the bat file uploads from here)
4. Print progress/status to stdout
5. Print errors to stderr
6. Return exit code 0 on success, non-zero on failure

The binary does NOT handle S3 transfers. The batch file handles all downloads and uploads using `aws s3 cp`.

---

## Example Workflow

This walks through a complete job from start to finish. The scenario: you have 4 data files across 2 S3 buckets and want to process them on 1 on-demand instance and 1 spot instance.

**Setup assumed:**
- Binary `process.bat` uploaded to `s3://batchtest3-cbai/deploy/process.bat`
- Input files in `s3://batchtest1-cbai/data/` (3 files) and `s3://batchtest2-cbai/data/` (1 file)
- One on-demand EC2 instance (`i-0abc123`) already exists in `us-east-2`
- A launch template `BatchProcessor-Spot-m5large` exists in `us-east-2`
- `appsettings.json` has default config with `CommandArgs` set to `--file "{filename}"`

### 1. Select Files (Tab 1)

1. App launches, buckets load in the left sidebar grouped by region
2. Click `batchtest1-cbai` → navigate into `data/` folder
3. Check the boxes next to `scan-001.tif`, `scan-002.tif`, `survey.las`
4. Click **"Select for processing"** — 3 files move to the "Selected for Processing" pane
5. Click `batchtest2-cbai` → navigate into `data/`
6. Check `sample2.txt`, click **"Select for processing"**
7. "Selected for Processing" now shows 4 files across 2 buckets
8. Click **"Continue →"**

### 2. Select Instances (Tab 2)

1. Instances load — you see `i-0abc123` (S3BatchProc-Worker-1) in `us-east-2`, state: Stopped
2. Click **"Select"** on its card — it appears in Selected Instances (right pane)
3. Expand **"Add Spot Instances"** at the bottom
4. Select region: `us-east-2`
5. Select launch template: `BatchProcessor-Spot-m5large`
6. Count: `1`
7. Click **"Add Spot Instances"** — a placeholder `Spot-Planned-BatchProcessor-Spot-m5large-1` (Pending) appears in Selected Instances
8. Selected Instances now shows 2 entries (1 on-demand + 1 planned spot)
9. Click **"Continue →"**

### 3. Assign & Run (Tab 3)

1. Top bar shows: Binary: `s3://batchtest3-cbai/deploy/process.bat`, Binary Dir: `C:\processor`, Input Dir: `C:\data`, Command Args: `--file "{filename}"`, Output S3 Path: `s3://batchtest3-cbai/results/`
2. Left side: 4 unassigned files. Right side: 2 instance cards
3. Click **"Auto-Distribute"** — files are round-robined:
   - `S3BatchProc-Worker-1`: `scan-001.tif`, `survey.las`
   - `Spot-Planned-...`: `scan-002.tif`, `sample2.txt`
4. Bottom bar shows: Unassigned: 0 | Total: 4 files
5. Click **"Run Processing ▶"**

### 4. Pre-flight

The yellow pre-flight panel appears:
```
Launching 1 spot instance(s) in us-east-2...
Launched: i-0def456
Starting S3BatchProc-Worker-1 (i-0abc123)...
Waiting for 2 instance(s) to reach Running state... (0s)
  ✓ S3BatchProc-Spot-20260330-143022 is now Running.
  ✓ S3BatchProc-Worker-1 is now Running.
All instances are running. Waiting for SSM agent registration...
  ✓ S3BatchProc-Worker-1 SSM agent is online.
  ✓ S3BatchProc-Spot-20260330-143022 SSM agent is online.
All instances ready for commands.
```

### 5. Execution

Phase changes to "Executing Commands". For each instance, the SSM script writes and runs a bat file. The bat file `test_20260330_143045_789.bat` contains **all operations**:
   ```bat
   @echo off

   REM === Directory setup ===
   if not exist "C:\data" mkdir "C:\data"
   if not exist "C:\data\output" mkdir "C:\data\output"
   if not exist "C:\processor\jobs" mkdir "C:\processor\jobs"

   REM === Download binary ===
   aws s3 cp "s3://batchtest3-cbai/deploy/process.bat" "C:\processor\process.bat" --region us-east-2

   REM === Download input files ===
   aws s3 cp "s3://batchtest1-cbai/data/scan-001.tif" "C:\data\scan-001.tif" --region us-east-2
   aws s3 cp "s3://batchtest1-cbai/data/survey.las" "C:\data\survey.las" --region us-east-2

   REM === Process files ===
   echo FILE_START:scan-001.tif
   "C:\processor\process.bat" --file "C:\data\scan-001.tif"
   if %ERRORLEVEL% EQU 0 (echo FILE_DONE:scan-001.tif) else (echo FILE_FAILED:scan-001.tif)
   echo FILE_START:survey.las
   "C:\processor\process.bat" --file "C:\data\survey.las"
   if %ERRORLEVEL% EQU 0 (echo FILE_DONE:survey.las) else (echo FILE_FAILED:survey.las)

   REM === Upload results ===
   aws s3 cp "C:\data\output\result_scan-001.tif" "s3://batchtest3-cbai/results/result_scan-001.tif" --region us-east-2
   aws s3 cp "C:\data\output\result_survey.las" "s3://batchtest3-cbai/results/result_survey.las" --region us-east-2

   REM === Upload this bat file as audit log ===
   aws s3 cp "%~f0" "s3://batchtest3-cbai/results/test_20260330_143045_789.bat" --region us-east-2
   ```

The progress bar updates as files complete: `2 / 4 completed (2 success, 0 failed)` → `4 / 4 completed (4 success, 0 failed)`

### 6. Completion

- Phase shows "Complete"
- Results DataGrid shows all 4 files with Status: Success
- The spot instance (`i-0def456`) is **automatically terminated**
- The on-demand instance (`i-0abc123`) remains running — stop it manually from Tab 2 if desired
- Results are now in S3 at `s3://batchtest3-cbai/results/`:
  - `result_scan-001.tif`, `result_scan-002.tif`, `result_survey.las`, `result_sample2.txt`
  - `test_20260330_143045_789.bat`, `test_20260330_143045_801.bat` (audit logs)

---

## Troubleshooting

### Pre-flight Failures

**"Failed to launch spot instances"**
- Check that your IAM user has `ec2:RunInstances`, `iam:PassRole`, and `iam:CreateServiceLinkedRole`
- For `iam:PassRole`: the resource must match your EC2 instance profile role ARN
- For `iam:CreateServiceLinkedRole`: only needed once per AWS account for EC2 Spot. After the first successful launch, this permission is no longer required.

**"Timed out waiting for instances to start"**
- Instance may be stuck in a transitional state. Check the EC2 console.
- For spot instances: your requested instance type may not have capacity in that region/AZ. Try a different type or region.

**"SSM agent did not come online"**
- Instance is running but SSM Agent hasn't registered yet. This can take 1–3 minutes after boot.
- Check that the instance has the `AmazonSSMManagedInstanceCore` IAM policy attached.
- Check that the instance has outbound internet access (or VPC endpoints for SSM).
- Verify the AMI has SSM Agent pre-installed (all standard AWS Windows AMIs do).

### Execution Failures

**"BAT_UPLOAD_FAILED"**
- The batch audit file couldn't upload to S3. Check the EC2 instance profile has `s3:PutObject` on the output bucket.

**"UPLOAD_FAILED:\<filename\>"**
- A specific result file couldn't upload. The processor may not have written `C:\data\output\result_<filename>`, or S3 permissions are missing.

**"FILE_FAILED:\<filename\>"**
- The processor returned a non-zero exit code for that file. Check the error output column in the results DataGrid.

### Common Issues

**No buckets appear in Tab 1**
- Check AWS credentials are configured (`aws configure` or environment variables)
- Check the IAM user has `s3:ListAllMyBuckets`

**No instances appear in Tab 2**
- Check `ScanRegions` in `appsettings.json` includes the regions where your instances exist
- Check the IAM user has `ec2:DescribeInstances`
- Terminated instances are filtered out

**No launch templates in the spot panel**
- Select a region first
- Check you have launch templates created in that region
- Check the IAM user has `ec2:DescribeLaunchTemplates`

**Files don't show in Tab 1 after selecting a bucket**
- Check the IAM user has `s3:ListBucket` on that bucket
- If using the TIFF/LAS filter, try unchecking it — your files may have different extensions

**"Access Denied" during execution**
- This is an EC2 instance profile issue, not the operator IAM user. Check the instance's IAM role has `s3:GetObject` on input buckets and `s3:PutObject` on the output bucket.
