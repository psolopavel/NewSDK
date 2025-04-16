using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;
using NLog;
using NLog.Extensions.Logging;
using CameraDownloader.Models;

namespace CameraDownloader
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Build a config object, using env vars and JSON providers.
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("CameraDownloader.Settings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            LogManager.Setup().LoadConfigurationFromSection(config);
            var logger = LogManager.GetCurrentClassLogger();
            logger.Debug("Parsing configuration settings");
            DownloaderSettings settings = config.GetRequiredSection("Settings").Get<DownloaderSettings>();
            logger.Info($"Started with {args.Length} parameters : {string.Join(" ", args)}");
            var appParams = new DownloadParameters();
            if (appParams.ParseParameters(args, settings))
            {
                logger.Debug($"Url {appParams.CloudServerUrl} cameras {appParams.CamerasList} start date {appParams.StartDownloadTime}");
                try
                {
                    var demo = new DownloadHandler(appParams);
                    await demo.InitDownloadService(appParams);
                }
                catch (Exception e)
                {
                    logger.Error(e);
                }
            }
        }
    }
}
