# Gap Analysis: Codebase vs Spec

What's wrong, what's missing, what needs to change, and how it all connects.

---

## 1. CRITICAL: BuildScript() is Linux bash, not Windows PowerShell

**Where:** `JobOrchestrationService.cs:247-259`

The spec says every SSM command is a PowerShell script that creates a `test_[TIMESTAMP].bat` on the instance and executes it. The code currently generates a bash script with Linux paths:

```csharp
// CURRENT (wrong)
return $"""
        #!/bin/bash
        set -e
        {executablePath} "{inputPath}"
        aws s3 cp "{outputPath}" "{s3Dest}"
        """;
```

**Spec requires:**
```powershell
$ErrorActionPreference = 'Stop'
$jobBatDir = 'C:\processor\jobs'
$ts = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$batPath = Join-Path $jobBatDir "test_$ts.bat"

New-Item -ItemType Directory -Force -Path 'C:\data','C:\data\output',$jobBatDir | Out-Null
Copy-S3Object -BucketName '{source_bucket}' -Key '{s3_key}' -LocalFile 'C:\data\{filename}' -Region '{bucket_region}'

@"
@echo off
"C:\processor\process.exe" --file "{filename}"
"@ | Set-Content -Path $batPath -Encoding ASCII

cmd /c "`"$batPath`""
Write-S3Object -BucketName '{output_bucket}' -Key 'results/result_{filename}' -File 'C:\data\output\result_{filename}' -Region '{output_region}'
```

**What must change:**
- Rewrite `BuildScript()` to emit the PowerShell script above
- The method needs **more parameters** than it currently receives:
  - Source S3 bucket name (from the `S3ObjectItem.BucketName`)
  - Source S3 key (from the `S3ObjectItem.Key`)
  - Bucket region (from the S3 service's region cache)
  - Output bucket name (parsed from `OutputS3Prefix`)
  - Output region
  - Job bat directory path (new config value)
- The `executablePath` default must change from `/opt/processor/run.sh` to `C:\processor\process.exe`
- Input paths change from `/data/` to `C:\data\`
- Output paths change from `/data/output/` to `C:\data\output\`
- Uses `Copy-S3Object` / `Write-S3Object` (AWSPowerShell), NOT `aws s3 cp` (CLI may not be present)

**Connected to:**
- `ExecuteJobsAsync()` calls `BuildScript()` — its signature and the data it passes must expand
- `IJobOrchestrationService.ExecuteJobsAsync()` interface signature may need new params (bat dir, regions)
- `JobAssignmentViewModel.RunRequested` event passes `(assignments, exePath, outputPrefix)` — may need to pass bat dir too
- `MainViewModel` wires `RunRequested` to `JobExecution.StartExecution` — same expansion
- `JobExecutionViewModel.StartExecution()` — same expansion
- `IS3Service` — needs a method or property to look up a bucket's region (already has `KnownBucketRegions` and `GetBucketRegionAsync`)

---

## 2. CRITICAL: SSM document is Linux, not Windows

**Where:** `SsmService.cs:37`

```csharp
DocumentName = "AWS-RunShellScript"  // Linux
```

**Must be:**
```csharp
DocumentName = "AWS-RunPowerShellScript"  // Windows
```

**Connected to:** Nothing else needs to change for this line, but it's the single most important bug — commands will fail on Windows Server instances with `AWS-RunShellScript`.

---

## 3. CRITICAL: SSM timeout is hardcoded to 60s, ignores config

**Where:** `SsmService.cs:42`

```csharp
TimeoutSeconds = 60
```

**Config says:** `CommandTimeoutSeconds: 600` (10 minutes)

The timeout should come from `IConfiguration["Ssm:CommandTimeoutSeconds"]` injected into `SsmService`. Processing geospatial data can take minutes — 60 seconds will cause most real jobs to time out.

**What must change:**
- Inject `IConfiguration` into `SsmService` constructor
- Read `Ssm:CommandTimeoutSeconds` and use it in `SendCommandAsync`
- Or pass timeout as a parameter from the orchestration layer

---

## 4. CRITICAL: No S3 input download in the SSM script

**Where:** `JobOrchestrationService.BuildScript()`

The current script assumes input files are already on the instance at `/data/{filename}`. The spec says input files are **pulled from S3 per job** via `Copy-S3Object`. This is the entire point of the new architecture — data is not pre-loaded.

**What must change:**
- `BuildScript()` must emit a `Copy-S3Object` line before the bat execution
- This requires the source bucket name, source key, and bucket region — data that lives on `S3ObjectItem` but is not currently passed through the script builder

**Connected to:**
- `S3ObjectItem` model already has `BucketName` and `Key` — these get passed into `BuildScript()`
- `IS3Service.GetBucketRegionAsync()` or `KnownBucketRegions` can provide the region
- The orchestration service needs access to `IS3Service` (currently it only has `ISsmService` and `IEc2Service`)

---

## 5. Missing config values in appsettings.json

**Where:** `appsettings.json`

Current:
```json
{
  "Processing": {
    "ExecutablePath": "/opt/processor/run.sh",
    "OutputS3Prefix": "s3://output-bucket/results/"
  }
}
```

Spec requires:
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
  }
}
```

**Missing keys:**
- `Processing:JobBatDirectory` — where bats are created on the instance
- `Processing:JobBatNamePrefix` — `test_` prefix for bat filenames
- `Processing:PollIntervalSeconds` — currently hardcoded to 3000ms in `JobOrchestrationService`
- `Aws:ScanRegions` — multi-region scanning

**Wrong values:**
- `ExecutablePath` is `/opt/processor/run.sh` (Linux) — should be `C:\processor\process.exe`
- `OutputS3Prefix` is `s3://output-bucket/results/` — should be `s3://batchtest3-cbai/results/`

**Connected to:**
- `JobAssignmentViewModel` reads these at construction (line 18-19) — fallback defaults are also Linux paths
- `JobOrchestrationService.BuildScript()` uses `executablePath` and `outputS3Prefix`
- New config values (`JobBatDirectory`, `JobBatNamePrefix`) need to be threaded through to `BuildScript()`

---

## 6. SSM test command is Linux bash

**Where:** `Ec2ManagerViewModel.cs:189`

```csharp
const string testCommand = "echo \"test\" > /tmp/batch-processor-test-$(date +%s).txt && echo \"SUCCESS\"";
```

**Spec requires (Windows PowerShell):**
```powershell
'test' | Out-File C:\temp\batch-processor-test-$(Get-Date -Format 'yyyyMMdd_HHmmss').txt; Write-Output 'SUCCESS'
```

This test will fail on Windows Server instances because it's bash syntax sent via `AWS-RunShellScript` (which is also wrong per item #2).

**Connected to:** Also depends on fix #2 (document name change to `AWS-RunPowerShellScript`). Both must change together.

---

## 7. Per-instance send serialization is missing

**Where:** `JobOrchestrationService.ExecuteJobsAsync()` lines 122-143

The spec says: "Sends are serialized per InstanceId so two jobs on the same box do not race on shared local files during dispatch."

Current code fires ALL sends in parallel with `Task.WhenAll`:
```csharp
foreach (var assignment in assignments)
    foreach (var file in assignment.Files)
        sendTasks.Add(SendSingleCommandAsync(...));
await Task.WhenAll(sendTasks);
```

**What must change:**
- Group files by instance
- For each instance, send commands sequentially (one after the other)
- Across instances, send in parallel

Conceptual structure:
```csharp
var instanceTasks = assignments.Select(async assignment =>
{
    foreach (var file in assignment.Files)
        await SendSingleCommandAsync(...);  // sequential within instance
});
await Task.WhenAll(instanceTasks);  // parallel across instances
```

**Connected to:** The polling loop (`PollUntilAllCompleteAsync`) does not need to change — it already polls all active results regardless of send order.

---

## 8. Poll interval is hardcoded

**Where:** `JobOrchestrationService.cs:191`

```csharp
await Task.Delay(3000, ct);  // hardcoded 3 seconds
```

Spec says `PollIntervalSeconds` should come from config. Currently the orchestration service has no access to `IConfiguration`.

**What must change:**
- Inject `IConfiguration` into `JobOrchestrationService`
- Read `Processing:PollIntervalSeconds` (default 3)
- Use it in the polling delay

---

## 9. JobAssignmentViewModel has no JobBatDirectory field

**Where:** `JobAssignmentViewModel.cs`

The Tab 3 UI in the spec shows three configurable fields:
1. Processor path (`C:\processor\process.exe`)
2. Job Bat Dir (`C:\processor\jobs\`)
3. Output S3 Path

The viewmodel only has `ExecutablePath` and `OutputS3Prefix`. There's no `JobBatDirectory` property, no UI binding for it, and it's not passed through `RunRequested`.

**What must change:**
- Add `[ObservableProperty] private string _jobBatDirectory` to `JobAssignmentViewModel`
- Load default from `IConfiguration["Processing:JobBatDirectory"]`
- Add it to the `RunRequested` event signature: `Action<IList<JobAssignment>, string, string, string>`
- Pass it through `MainViewModel` → `JobExecutionViewModel.StartExecution()` → `ExecuteJobsAsync()`
- Pass it into `BuildScript()`

**Connected to (full chain):**
1. `JobAssignmentViewModel.RunRequested` event signature
2. `MainViewModel` lambda wiring (line 23-26)
3. `JobExecutionViewModel.StartExecution()` parameter list
4. `IJobOrchestrationService.ExecuteJobsAsync()` interface signature
5. `JobOrchestrationService.ExecuteJobsAsync()` implementation
6. `JobOrchestrationService.BuildScript()` parameter list
7. `JobAssignmentView.xaml` — needs a TextBox bound to `JobBatDirectory`

---

## 10. S3ObjectItem lacks BucketRegion for script generation

**Where:** `S3ObjectItem.cs`

`BuildScript()` needs the source bucket's region to emit `Copy-S3Object -Region '{bucket_region}'`. The `S3ObjectItem` has `BucketName` but no region.

**Options:**
- Add a `BucketRegion` property to `S3ObjectItem` (set when browsing S3)
- Or look it up at script-build time via `IS3Service.GetBucketRegionAsync()`

**Connected to:**
- `S3BrowserViewModel` — when creating `S3ObjectItem` entries, it could set the region from the cached bucket region
- `JobOrchestrationService` — would need `IS3Service` injected if doing runtime lookup
- The output bucket region also needs to be known — either parsed from config or looked up

---

## 11. RunRequested event and StartExecution don't switch to Tab 3

**Where:** `MainViewModel.cs:23-26`

```csharp
JobAssignment.RunRequested += (assignments, exePath, outputPrefix) =>
{
    _ = JobExecution.StartExecution(assignments, exePath, outputPrefix);
};
```

This starts execution but doesn't switch the tab to the execution view (Tab 3). The user has to manually click to see progress.

**What must change:**
```csharp
JobAssignment.RunRequested += (assignments, exePath, outputPrefix) =>
{
    SelectedTabIndex = 3;
    _ = JobExecution.StartExecution(assignments, exePath, outputPrefix);
};
```

---

## 12. Output bucket/prefix parsing

**Where:** `JobOrchestrationService.BuildScript()`

The `OutputS3Prefix` is a string like `s3://batchtest3-cbai/results/`. The `Write-S3Object` cmdlet needs separate `-BucketName` and `-Key` parameters, plus `-Region`.

Current code just appends to the prefix as a single `aws s3 cp` destination. The new PowerShell version needs the prefix parsed into bucket name and key prefix.

**What must change:**
- Parse `s3://bucket-name/key-prefix/` into bucket name and key prefix
- Determine the output bucket's region (config, lookup, or assume same region)
- Emit separate `-BucketName` and `-Key` parameters in the `Write-S3Object` call

---

## 13. JobAssignmentView.xaml needs bat dir config UI

**Where:** `JobAssignmentView.xaml`

The spec's Tab 3 mock shows three config fields at the top. The XAML needs a TextBox bound to the new `JobBatDirectory` property, matching the existing `ExecutablePath` and `OutputS3Prefix` fields.

---

## Summary: Change Dependency Graph

```
appsettings.json                    (add missing keys, fix values)
    |
    v
SsmService.cs                       (RunPowerShellScript, configurable timeout)
    |
    v
JobOrchestrationService.cs          (rewrite BuildScript to PowerShell+bat,
    |                                 add S3 download, serialize per instance,
    |                                 accept bat dir + regions, configurable poll)
    |
    v
IJobOrchestrationService.cs         (expand ExecuteJobsAsync signature)
    |
    v
JobExecutionViewModel.cs            (expand StartExecution params)
    |
    v
JobAssignmentViewModel.cs           (add JobBatDirectory prop, expand RunRequested)
    |
    v
MainViewModel.cs                    (pass bat dir through, switch to tab 3)
    |
    v
JobAssignmentView.xaml              (add bat dir TextBox)
    |
    v
Ec2ManagerViewModel.cs              (fix SSM test command to PowerShell)
    |
    v
S3ObjectItem.cs                     (optionally add BucketRegion)
```

### Priority Order

1. `SsmService.cs` — change document to `AWS-RunPowerShellScript` + configurable timeout (everything else depends on this)
2. `appsettings.json` — add missing keys, fix Linux paths to Windows paths
3. `JobOrchestrationService.BuildScript()` — rewrite to PowerShell with bat generation + S3 download
4. `JobOrchestrationService.ExecuteJobsAsync()` — add per-instance serialization
5. `IJobOrchestrationService` + `JobExecutionViewModel` + `JobAssignmentViewModel` + `MainViewModel` — thread `JobBatDirectory` through the chain
6. `Ec2ManagerViewModel` — fix SSM test command
7. `JobAssignmentView.xaml` — add bat dir config TextBox
8. `S3ObjectItem` / region lookup — ensure bucket region available at script-build time
