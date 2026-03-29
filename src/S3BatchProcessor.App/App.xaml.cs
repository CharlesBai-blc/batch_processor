using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using S3BatchProcessor.App.Services;
using S3BatchProcessor.App.ViewModels;
using S3BatchProcessor.App.Views;

namespace S3BatchProcessor.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration);
        _serviceProvider = services.BuildServiceProvider();

        var mainVm = _serviceProvider.GetRequiredService<MainViewModel>();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = mainVm;
        mainWindow.Show();

        await mainVm.StatusBar.InitializeAsync();
        await mainVm.S3Browser.LoadBucketsCommand.ExecuteAsync(null);
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<IAwsConnectionService, AwsConnectionService>();
        services.AddSingleton<IS3Service, S3Service>();
        services.AddSingleton<IEc2Service, Ec2Service>();
        services.AddSingleton<ISsmService, SsmService>();
        services.AddSingleton<IJobOrchestrationService, JobOrchestrationService>();

        services.AddTransient<StatusBarViewModel>();
        services.AddTransient<S3BrowserViewModel>();
        services.AddTransient<Ec2ManagerViewModel>();
        services.AddTransient<JobAssignmentViewModel>();
        services.AddTransient<JobExecutionViewModel>();
        services.AddTransient<MainViewModel>();

        services.AddTransient<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
