# S3 Batch Processing Job Dispatch System

A Windows desktop application (WPF, .NET 8, C#) that serves as a command center for dispatching geospatial batch processing jobs to AWS EC2 instances. The app orchestrates remote compute via AWS Systems Manager (SSM) and uses S3 as the shared file system.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later)
- Windows 10/11 (WPF requires Windows)
- AWS credentials configured (`~/.aws/credentials` or environment variables)

## Clone

```bash
git clone https://github.com/CharlesBai-blc/batch_processor.git
cd batch_processor
```

## Restore Dependencies

```bash
dotnet restore
```

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run --project src/S3BatchProcessor.App
```

## Run Tests

```bash
dotnet test
```

## Publish (Standalone Exe)

**Self-contained single-file exe** — bundles the .NET runtime so the target machine doesn't need .NET installed:

```bash
dotnet publish src/S3BatchProcessor.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

The output is a single `S3BatchProcessor.App.exe` in the `./publish/` folder. Copy it to any Windows x64 machine and run — no .NET installation required.

**Framework-dependent exe** — smaller output, but requires .NET 8 runtime on the target machine:

```bash
dotnet publish src/S3BatchProcessor.App -c Release -r win-x64 -o ./publish
```

## Project Structure

```
S3BatchProcessor.sln
src/
  S3BatchProcessor.App/
    Models/          Data models (S3 objects, EC2 instances, job assignments)
    ViewModels/      MVVM view models (S3 browser, EC2 fleet, job assignment/execution)
    Views/           WPF XAML views
    Services/        AWS service wrappers (S3, EC2, SSM, job orchestration)
    Converters/      XAML value converters
    appsettings.json Default configuration
tests/
  S3BatchProcessor.Tests/
```

## Configuration

Edit `src/S3BatchProcessor.App/appsettings.json` to set defaults for AWS regions, deploy source, output path, command args, and timeouts. All settings can be overridden at runtime in the UI.

## Documentation

- [User Guide](USER_GUIDE.md) — step-by-step usage with a full workflow example
- [Project Spec](PROJECT_SPEC.md) — milestones and implementation details
- [Batch Processing Spec](BatchProcessingSpec.md) — architecture, SSM script format, and IAM requirements
