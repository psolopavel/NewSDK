using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CameraDownloader.Helpers;
using CameraDownloader.Models;
using NLog;

namespace CameraDownloader
{
    public class DownloadHandler
    {
        private readonly object _deviceListLocker = new object();
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private static void TryCreateFolder(string folderName)
        {
            if (!String.IsNullOrEmpty(folderName) && !Directory.Exists(folderName))
            {
                Directory.CreateDirectory(folderName);
            }
        }

        private void SetPath(string localRecordPath, string sdkLogPath)
        {
            try
            {
                TryCreateFolder(localRecordPath);
                TryCreateFolder(sdkLogPath);

                int bRet = NETDEVSDK.NETDEV_SetLogPath(sdkLogPath);
                if (NETDEVSDK.TRUE != bRet)
                {
                    _logger.Error($"Set log path to {sdkLogPath} fail: {NETDEVSDK.NETDEV_GetLastError()}");
                }
                else
                {
                    _logger.Debug($"Set log path to {sdkLogPath} succeeded");
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Create path fail");
            }
        }


        private void SetConnectTime(Int32 iWaitTime, Int32 iTryTime)
        {
            int bRet = NETDEVSDK.NETDEV_SetConnectTime(iWaitTime, iTryTime);
            if (NETDEVSDK.TRUE != bRet)
            {
                _logger.Error($"Set Connect Time fail {NETDEVSDK.NETDEV_GetLastError()}");
            }
        }


        private void SetTimeOut(Int32 iReceiveTimeOut, Int32 iFileTimeOut)
        {
            NETDEV_REV_TIMEOUT_S revTimeout = new NETDEV_REV_TIMEOUT_S();
            revTimeout.dwRevTimeOut = iReceiveTimeOut;
            revTimeout.dwFileReportTimeOut = iFileTimeOut;
            int iRet = NETDEVSDK.NETDEV_SetRevTimeOut(ref revTimeout);
            if (NETDEVSDK.TRUE != iRet)
            {
                _logger.Error($"Set Connect Time fail {NETDEVSDK.NETDEV_GetLastError()}");
            }
        }

        public DownloadHandler(DownloadParameters appParams)
        {
            SetPath(appParams.RecordingsFolder, appParams.SdkLogsFolder);

            int iRet = NETDEVSDK.NETDEV_Init();
            if (NETDEVSDK.TRUE != iRet)
            {
                _logger.Error($"Error initializing SDK {NETDEVSDK.NETDEV_GetLastError()}");
                return;
            }

            SetConnectTime(30, 3);

            SetTimeOut(10, 60);
        }

        public async Task InitDownloadService(DownloadParameters appParams)
        {
            if (String.IsNullOrEmpty(appParams.DataFolder))
            {
                _logger.Error("Root folder for data is empty");
                Environment.Exit(0);
                throw new Exception("Root folder for data is empty");
            }

            _logger.Debug("Loading cameras list");

            IntPtr cloudHandle = LoginCloudDevice(appParams.CloudServerUrl, appParams.CloudServerLogin, appParams.CloudServerPassword, appParams.CamerasList);
            if (cloudHandle == IntPtr.Zero)
            {
                return;
            }

            var cameras = LoadCameraList(cloudHandle, appParams);
            var options = new ParallelOptions { MaxDegreeOfParallelism = cameras.Count == 0 ? 10 : cameras.Count  };
            if (appParams.OperationMode == Mode.Download)
            {
                _logger.Debug("Starting download process");
                await Parallel.ForEachAsync(cameras, options, async (c, _) => { await c.DownloadCameraFiles(); });
            }
            if (appParams.OperationMode == Mode.Reboot)
            {
                _logger.Debug("Starting reboot process");
                await Parallel.ForEachAsync(cameras, options, async (c, _) => { await c.RebootCamera(); });
            }

            NETDEVSDK.NETDEV_Logout(cloudHandle);
            NETDEVSDK.NETDEV_Cleanup();
        }


        ////login cloud device
        public IntPtr LoginCloudDevice(string url, string userName, string password, string cameraId)
        {
            _logger.Debug($"Connecting to the cloud {url} user {userName} ");

            IntPtr lpCloudDevHandle = NETDEVSDK.NETDEV_LoginCloud(url, userName, password);
            if (lpCloudDevHandle == IntPtr.Zero)
            {
                _logger.Error($"Error connecting to cloud account: " + NETDEVSDK.NETDEV_GetLastError());
                return IntPtr.Zero;
            }

            _logger.Info($"Connected to the cloud {url} user {userName}");
            return lpCloudDevHandle;
        }

        public List<CameraDownloader> LoadCameraList(IntPtr cloudDevHandle, DownloadParameters appParams)
        {
            var result = new List<CameraDownloader>();
            IntPtr lpDevListHandle = NETDEVSDK.NETDEV_FindCloudDevListEx(cloudDevHandle);
            if (lpDevListHandle == IntPtr.Zero)
            {
                _logger.Error($"Error getting list of cameras in cloud acount : {NETDEVSDK.NETDEV_GetLastError()}");
                return result;
            }

            lock (_deviceListLocker)
            {
                int bRet = NETDEVSDK.TRUE;
                while (bRet == NETDEVSDK.TRUE)
                {
                    NETDEV_CLOUD_DEV_BASIC_INFO_S stCloudDevInfo = new NETDEV_CLOUD_DEV_BASIC_INFO_S();
                    bRet = NETDEVSDK.NETDEV_FindNextCloudDevInfoEx(lpDevListHandle, ref stCloudDevInfo);
                    if (NETDEVSDK.TRUE == bRet)
                    {
                        stCloudDevInfo.szDevNameString = NETDEVSDK.GetDefaultString(stCloudDevInfo.szDevName);
                        string searchName = stCloudDevInfo.szDevNameString.ToUpper();

                        if (appParams.CamerasIntervals.Count(t => t.CameraId.Equals(searchName)) == 1)
                        {
                            var cameraIntervals = appParams.CamerasIntervals.First(t => t.CameraId.Equals(searchName));
                            _logger.Debug($"Camera {stCloudDevInfo.szDevNameString} added to the list to process");

                            DeviceInfo deviceInfoTemp = new DeviceInfo
                            {
                                CloudDeviceHandle = cloudDevHandle,
                                CloudUrl = appParams.CloudServerUrl,
                                CloudUserName = appParams.CloudServerLogin,
                                CloudPassword = appParams.CloudServerPassword,
                                CloudDeviceSdkInfo = stCloudDevInfo,
                                CameraName = stCloudDevInfo.szDevNameString,
                                CameraIntervals = cameraIntervals.Intervals
                            };
                            deviceInfoTemp.IpAddress = deviceInfoTemp.CloudDeviceSdkInfo.szIPAddr;
                            var downloader = new CameraDownloader(deviceInfoTemp, appParams);
                            result.Add(downloader);
                        }
                    }
                }

                NETDEVSDK.NETDEV_FindCloseCloudDevListEx(lpDevListHandle);
            }
            _logger.Info($"Available cameras for processing {result.Count}");
            return result;
        }
    }
}
