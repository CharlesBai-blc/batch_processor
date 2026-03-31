# Spot Instance Support

## What It Does

The app can launch EC2 Spot Instances on demand from the EC2 tab, use them for job processing exactly like on-demand instances, and automatically terminate them when the job finishes. This gives you access to cheaper compute without any manual AWS Console work — launch, process, clean up, all from the app.

## Why It Works

The app already downloads everything from S3 at the start of each job (processor executable via Deploy Source, input data files from source buckets). Instances are stateless — there's nothing pre-installed beyond the base Windows AMI. This means a freshly launched spot instance is just as capable as an on-demand instance that's been running for weeks. The existing pre-flight sequence (`EnsureInstancesRunningAsync`) already handles waiting for instances to reach Running state and for SSM Agent to come online, so newly launched spot instances integrate seamlessly.

## How to Use It

### 1. Set Up a Launch Template (one-time, in AWS Console)

Before using spot instances, create an EC2 Launch Template in the AWS Console that defines:

- **AMI:** Windows Server 2019 or 2022 (with SSM Agent pre-installed)
- **Instance type:** e.g. `m5.xlarge`, `c5.2xlarge`
- **IAM Instance Profile:** Role with `AmazonSSMManagedInstanceCore` + S3 read/write permissions
- **Security group:** Outbound internet access (for SSM + S3 communication)
- **User data (recommended):**
  ```powershell
  <powershell>
  New-Item -ItemType Directory -Force -Path 'C:\processor\jobs','C:\data','C:\data\output'
  </powershell>
  ```

The launch template captures all the configuration needed to launch a working instance. You create it once and reuse it from the app.

### 2. Launch Spot Instances from the App

1. Open the **EC2 tab** (Tab 2: Select Instances)
2. Expand the **"Launch Spot Instances"** panel at the bottom of the instance list
3. Select a **Region** from the dropdown — this triggers loading available launch templates
4. Select a **Launch Template** from the dropdown
5. Set the **Instance Count** (default: 1)
6. Click **"Launch Spot Instances"**

The app calls `ec2:RunInstances` with `MarketType=Spot` and your selected launch template. Status messages appear below the button showing progress and the IDs of launched instances.

After a short delay, the instance list refreshes and your new spot instances appear with an orange **SPOT** badge.

### 3. Use Them Like Any Other Instance

Select the spot instances, assign files, run your job. The pre-flight sequence will wait for them to reach Running state and for SSM Agent to register — typically 1-3 minutes from launch.

### 4. Auto-Termination After Job Completion

When job execution completes (success, failure, or cancellation), the app automatically terminates any spot instances that were launched during this session. You don't need to do anything — cleanup is automatic.

Specifically:
- The app tracks which instance IDs it launched via spot requests (stored in a session-scoped set)
- When `JobExecutionViewModel.ExecutionCompleted` fires, `MainViewModel` filters the job assignments for spot-launched instances and calls `Ec2Manager.TerminateSpotLaunchedInstancesAsync`
- Termination is grouped by region for efficiency
- The instance list refreshes after termination

## What the SPOT Badge Means

Any instance running on the spot market (regardless of who launched it) shows an orange **SPOT** badge on its card in the instance list. This is determined by the EC2 API's `InstanceLifecycle` field.

The badge tells you two things:
- This instance is running at spot pricing (cheaper, but can be reclaimed by AWS)
- If it was launched by this app session, it will be auto-terminated after job completion

## Instance Tagging

Spot instances launched by the app are tagged with:

| Tag | Value | Purpose |
|---|---|---|
| `Name` | `S3BatchProc-Spot-YYYYMMDD-HHmmss` | Identifies the instance in the AWS Console and in the app's instance list |
| `SpotLaunchedBy` | `S3BatchProcessor` | Marks the instance as app-launched (for identification in the AWS Console) |

## IAM Permissions Required

The operator's IAM user needs these additional permissions (beyond the existing ones):

| Permission | Used By |
|---|---|
| `ec2:DescribeLaunchTemplates` | Loading available launch templates for the dropdown |
| `ec2:RunInstances` | Launching spot instances |
| `ec2:TerminateInstances` | Auto-terminating spot instances after job completion |
| `ec2:CreateTags` | Tagging launched instances (used implicitly by `RunInstances` with `TagSpecifications`) |

## Edge Cases

### No Launch Templates in Region
If the selected region has no launch templates, the status text shows "No launch templates found in this region." Select a different region or create a template in the AWS Console.

### Launch Failure
If `RunInstances` fails (e.g. no spot capacity, invalid template, insufficient permissions), the error message is displayed in the status text below the launch button. No instances are created and nothing needs cleanup.

### Spot Interruption During a Job
If AWS reclaims a spot instance mid-job, the SSM command will fail. The app's polling will detect this:
- `GetCommandInvocation` will return an error or `Failed` status
- Affected files will be marked as Failed in the results grid
- The instance will no longer appear in subsequent refreshes (terminated by AWS)

The app does **not** automatically retry failed files on another instance. The operator can re-assign and re-run manually.

### App Closed Before Job Completes
If the operator closes the app while spot instances are still running, auto-termination will not happen (it's session-scoped). The instances will continue running until:
- AWS reclaims them (spot interruption)
- The operator manually terminates them from the AWS Console
- Another mechanism (e.g. a CloudWatch alarm or instance max-runtime tag) shuts them down

For safety, consider setting a `max-runtime` tag or CloudWatch alarm on your launch template to auto-terminate instances that run longer than expected.

### Multiple Job Runs in One Session
The spot-launched instance tracking is cumulative within a session. If you launch spot instances, run a job, then launch more and run another job, auto-termination after the second job will terminate all spot instances launched during the entire session (that are still running).

## Configuration

No `appsettings.json` changes are needed. The launch template is selected at runtime through the UI. The region dropdown is populated from the existing `Aws:ScanRegions` configuration.

## Architecture

```
Operator clicks "Launch Spot Instances"
    |
    v
Ec2ManagerViewModel.LaunchSpotInstancesCommand
    |
    v
Ec2Service.LaunchSpotInstancesAsync
    |-- ec2:RunInstances (MarketType=Spot, LaunchTemplate reference, tags)
    |
    v
Instance IDs stored in _spotLaunchedInstanceIds (session-scoped HashSet)
    |
    v
Instance list refreshes, SPOT badges appear
    |
    v
[Normal job flow: assign files, run, pre-flight, execute, poll]
    |
    v
JobExecutionViewModel.ExecutionCompleted fires
    |
    v
MainViewModel filters assignments for spot-launched instances
    |
    v
Ec2ManagerViewModel.TerminateSpotLaunchedInstancesAsync
    |
    v
Ec2Service.TerminateInstancesAsync (grouped by region)
    |
    v
Instance list refreshes, terminated instances disappear
```
