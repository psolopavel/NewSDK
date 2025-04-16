using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CameraDownloader.Models;
using CameraDownloader.Helpers;

namespace CameraDownloader
{
    public class CameraDownloader
    {
        private static int SuccessfulCameraCount = 0;

        private static List<string> SuccessfulCameraList = new List<string>();
        private static readonly string FailedCamerasFile = "FailedCameras.txt";
        private static readonly object FileLock = new object();

        public DeviceInfo Camera { get; set; }
        public DownloadParameters Parameters { get; set; }
        public DeviceFilesInfo DeviceData { get; set; } = new DeviceFilesInfo();

        private readonly ILogger _logger;


        public CameraDownloader(DeviceInfo device, DownloadParameters appParams)
        {
            Camera = device;
            Parameters = appParams;
            _logger = LogManager.GetCurrentClassLogger().WithProperty("CameraId", Camera.CameraName);
        }

        private async Task<bool> GetChannelList()
        {
            bool result = false;
            int maxRetries = Parameters.MaxConnectRetries;
            int retryDelay = Parameters.InitialConnectTimeoutSec;
            TimeSpan maxConnectTime = TimeSpan.FromSeconds(Parameters.MaxConnectTimeSec);
            DateTime startTime = DateTime.Now;

            NETDEV_CLOUD_DEV_LOGIN_INFO_S pCloudInfo = new NETDEV_CLOUD_DEV_LOGIN_INFO_S()
            {
                dwLoginProto = (int)NETDEV_LOGIN_PROTO_E.NETDEV_LOGIN_PROTO_ONVIF,
                szDeviceName = Camera.CloudDeviceSdkInfo.szDevUserName,
                dwConnectMode = 0,
                dwT2UTimeout = 30,
                //szDevicePassword = "12345678-a"
            };

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (DateTime.Now - startTime > maxConnectTime)
                {
                    _logger.Error($"Exceeded maximum connection time of {maxConnectTime.TotalMinutes} minutes.");
                    break;
                }

                while (SuccessfulCameraCount >= Parameters.MaxCamersByOneTime)
                {
                    _logger.Error("Kamers limit was reched. 10 secconds delay was start");
                    await Task.Delay(10000);
                }

                _logger.Debug(
                    $"Attempt {attempt}: Trying to connect to the camera {Camera.CameraName} IP {Camera.CloudDeviceSdkInfo.szIPAddr}");

                Camera.DeviceHandle = NETDEVSDK.NETDEV_LoginCloudDevice_V30(Camera.CloudDeviceHandle, ref pCloudInfo);

                if (Camera.DeviceHandle == IntPtr.Zero)
                {
                    lock (FileLock)
                    {
                        SuccessfulCameraList.Remove(Camera.CameraName);
                        RemoveFromFailedList(Camera.CameraName);
                    }
                    _logger.Error(
                        $"Login {Camera.CameraName} at IP {Camera.CloudDeviceSdkInfo.szIPAddr} attempt {attempt} failed,the error is {NETDEVSDK.NETDEV_GetLastError()} ");
                }
                else
                {
                    _logger.Info(
                        $"Connected to camera {Camera.CameraName} at IP {Camera.CloudDeviceSdkInfo.szIPAddr} attempt {attempt} {NETDEVSDK.NETDEV_GetLastError()}");

                    //get the channel list
                    int pdwChlCount = NETDEVSDK.NETDEV_LEN_32;

                    int channelInfoSize = Marshal.SizeOf<NETDEV_VIDEO_CHL_DETAIL_INFO_S>();
                    IntPtr pstVideoChlList = Marshal.AllocHGlobal(NETDEVSDK.NETDEV_LEN_32 * channelInfoSize);
                    int iRet = NETDEVSDK.NETDEV_QueryVideoChlDetailList(Camera.DeviceHandle, ref pdwChlCount,
                        pstVideoChlList);
                    if (NETDEVSDK.TRUE == iRet)
                    {
                        Camera.ChannelsCount = pdwChlCount;
                        _logger.Debug(
                            $"For camera {Camera.CameraName} there are channels available : {Camera.ChannelsCount}");

                        for (int i = 0; i < pdwChlCount; i++)
                        {
                            IntPtr ptrTemp = IntPtr.Add(pstVideoChlList, channelInfoSize * i);
                            NETDEV_VIDEO_CHL_DETAIL_INFO_S channelItem =
                                Marshal.PtrToStructure<NETDEV_VIDEO_CHL_DETAIL_INFO_S>(ptrTemp);
                            _logger.Debug($"Channel {i + 1} status {channelItem.enStatus}");


                            if (channelItem.enStatus == 1) // Channel is online
                            {
                                ChannelInfo channelInfo = new ChannelInfo()
                                {
                                    m_devVideoChlInfo = channelItem
                                };
                                Camera.Channels.Add(channelInfo);
                            }
                        }

                        _logger.Info($"Camera {Camera.CameraName} is connected {SuccessfulCameraCount} of {Parameters.MaxCamersByOneTime}. Channels {Camera.Channels.Count} attempt {attempt}");
                        result = true;
                        break;
                    }
                    else
                    {
                        _logger.Error(
                            $"{Camera.CameraName} - unable to get list of channels - {NETDEVSDK.NETDEV_GetLastError()}");
                    }
                }
                if (attempt < maxRetries)
                {
                    _logger.Debug($"{Camera.CameraName} Retrying in {retryDelay} seconds...");
                    await Task.Delay(retryDelay * 1000);

                    if (attempt % 3 == 0) // Increment delay every 3rd attempt
                    {
                        retryDelay = retryDelay + Parameters.InitialConnectTimeoutSec;
                    }
                }
            }
            return result;
        }

        private static void AddToFailedList(string cameraName)
        {
            lock (FileLock)
            {
                if (!File.Exists(FailedCamerasFile))
                {
                    File.WriteAllText(FailedCamerasFile, cameraName + Environment.NewLine);
                }
                else
                {
                    var existingCameras = File.ReadAllLines(FailedCamerasFile)
                                            .Select(line => line.Trim()) // Удаляем пробелы и \r\n
                                            .ToList();

                    if (!existingCameras.Contains(cameraName))
                    {
                        File.AppendAllText(FailedCamerasFile, cameraName + Environment.NewLine);
                    }
                }
            }
        }

        private static void RemoveFromFailedList(string cameraName)
        {
            lock (FileLock)
            {
                if (!File.Exists(FailedCamerasFile)) return;

                var cameras = File.ReadAllLines(FailedCamerasFile)
                                .Select(line => line.Trim()) // Удаляем пробелы и \r\n
                                .ToList();

                if (cameras.Remove(cameraName))
                {
                    File.WriteAllLines(FailedCamerasFile, cameras);
                }
            }
        }
        public async Task<List<CameraFileInfo>> DownloadCameraFiles()
        {
            if (!await GetChannelList()) return null;

            lock (FileLock)
            {
                if (SuccessfulCameraCount >= Parameters.MaxCamersByOneTime)
                {
                    _logger.Warn($"Camera {Camera.CameraName} skipped: limit of {Parameters.MaxCamersByOneTime} active cameras reached.");
                    return null;
                }
                SuccessfulCameraCount++;
                SuccessfulCameraList.Add(Camera.CameraName);
                AddToFailedList(Camera.CameraName);
            }

            _logger.Debug($"Searching for files for download from camera {Camera.CameraName}");
            for (int channel = 0; channel < Camera.ChannelsCount; channel++)
            {
                _logger.Debug($"Processing channel {channel + 1} from {Camera.CameraName}");
                foreach (var cameraInterval in Camera.CameraIntervals)
                {
                    _logger.Debug($"Processing period {cameraInterval.StartTime} - {cameraInterval.EndTime}");
                    if (!SuccessfulCameraList.Contains(Camera.CameraName))
                    {
                        _logger.Warn($"Camera {Camera.CameraName} wasn't allowed for work");
                        break;
                    }
                    await DownloadFilesFromDevice(channel + 1,
                        new DateTimeOffset(cameraInterval.StartTime.ToUniversalTime()).ToUnixTimeSeconds(),
                        new DateTimeOffset(cameraInterval.EndTime.ToUniversalTime()).ToUnixTimeSeconds());
                }
            }
            _logger.Info($"Files from {Camera.CameraName} are processed");
            if (SuccessfulCameraList.Contains(Camera.CameraName))
            {
                SuccessfulCameraCount--;
            }
            NETDEVSDK.NETDEV_Logout(Camera.DeviceHandle);
            return new List<CameraFileInfo>();
        }

        public async Task<bool> RebootCamera()
        {
            if (!await GetChannelList())
                return false;
            _logger.Debug($"Rebooting camera {Camera.CameraName}");
            int iRet = NETDEVSDK.NETDEV_Reboot(Camera.DeviceHandle);
            if (NETDEVSDK.TRUE != iRet)
            {
                _logger.Error($"Rebooting camera {Camera.CameraName} failed - {NETDEVSDK.NETDEV_GetLastError()}");
                return false;
            }
            _logger.Info($"Camera {Camera.CameraName} is rebooted");
            return true;
        }

        private void SetFilePath(DeviceFileInfo deviceFile)
        {
            string targetFilePath = Path.Combine(Parameters.RecordingsFolder, Camera.CameraName);

            if (!Directory.Exists(targetFilePath))
            {
                Directory.CreateDirectory(targetFilePath);
            }

            deviceFile.TargetFilePath = targetFilePath;
            deviceFile.TargetFileName = $"{DateTimeOffset.FromUnixTimeSeconds(deviceFile.StartTime):yyyyMMdd}_{deviceFile.StartTime}_{deviceFile.EndTime}";
            deviceFile.TargetFileExtension = ".mp4";
            deviceFile.LengthInSeconds = deviceFile.EndTime - deviceFile.StartTime;
        }

        private bool FileAlreadyExists(DeviceFileInfo deviceFile)
        {
            bool result = false;

            string checkInProgressFileName = Path.Combine(deviceFile.TargetFilePath, deviceFile.TargetFileName);
            string checkCompletedFileName = Path.ChangeExtension(checkInProgressFileName, deviceFile.TargetFileExtension);

            if (File.Exists(checkInProgressFileName))
            {
                _logger.Info($"File {Camera.CameraName}{Path.DirectorySeparatorChar}{Path.GetFileName(checkInProgressFileName)} already exists - skipping");
                result = true;
            }

            if (File.Exists(checkCompletedFileName))
            {
                _logger.Info($"File {Camera.CameraName}{Path.DirectorySeparatorChar}{Path.GetFileName(checkCompletedFileName)} already exists - skipping");
                result = true;

            }
            return result;
        }

        private async Task DownloadFilesFromDevice(int channelId, long startTime, long endTime)
        {
            if (startTime >= endTime)
            {
                _logger.Error("Invalid time : start time greater then end time");
                return;
            }
            DeviceData.DeviceFiles.Clear();

            NETDEV_FILECOND_S stFileCond = new NETDEV_FILECOND_S();

            stFileCond.tBeginTime = startTime;
            stFileCond.tEndTime = endTime;
            stFileCond.dwFileType = (int)NETDEV_PLAN_STORE_TYPE_E.NETDEV_TYPE_STORE_TYPE_ALL;
            stFileCond.dwChannelID = channelId;

            DeviceData.DeviceHandle = Camera.DeviceHandle;

            IntPtr fileHandle = NETDEVSDK.NETDEV_FindFile(DeviceData.DeviceHandle, ref stFileCond);

            if (fileHandle == IntPtr.Zero)
            {
                if (SuccessfulCameraList.Contains(Camera.CameraName))
                {
                    SuccessfulCameraCount--;
                    SuccessfulCameraList.Remove(Camera.CameraName);
                    RemoveFromFailedList(Camera.CameraName);
                    _logger.Error($"Camera {Camera.CameraName} was removed from list. In present free is {Parameters.MaxCamersByOneTime - SuccessfulCameraCount} slots");
                }
                _logger.Error($"DownloadFilesFromDevice : NETDEV_FindFile {Camera.CameraName} camera {channelId} Error : {NETDEVSDK.NETDEV_GetLastError()}");
                return;
            }

            NETDEV_FINDDATA_S findData = new NETDEV_FINDDATA_S();
            while (NETDEVSDK.TRUE == NETDEVSDK.NETDEV_FindNextFile(fileHandle, ref findData))
            {
                var deviceFile = new DeviceFileInfo()
                {
                    DeviceFileName = findData.szFileName,
                    StartTime = findData.tBeginTime,
                    EndTime = findData.tEndTime
                };
                SetFilePath(deviceFile);
                DeviceData.DeviceFiles.Add(deviceFile);
            }

            _logger.Info($"In {Camera.CameraName} found files to download - {DeviceData.DeviceFiles.Count}");

            if (NETDEVSDK.FALSE == NETDEVSDK.NETDEV_FindClose(fileHandle))
            {
                if (SuccessfulCameraList.Contains(Camera.CameraName))
                {
                    SuccessfulCameraCount--;
                    SuccessfulCameraList.Remove(Camera.CameraName);
                    RemoveFromFailedList(Camera.CameraName);
                    _logger.Error($"Camera {Camera.CameraName} was removed from list. In present free is {Parameters.MaxCamersByOneTime} slots");
                }
                _logger.Error($"DownloadFilesFromDevice: NETDEV_FindClose {Camera.CameraName} channel {channelId} error : {NETDEVSDK.NETDEV_GetLastError()}");
                return;
            }

            if (DeviceData.DeviceFiles.Count == 0)
            {
                if (SuccessfulCameraList.Contains(Camera.CameraName))
                {
                    SuccessfulCameraCount--;
                    SuccessfulCameraList.Remove(Camera.CameraName);
                    RemoveFromFailedList(Camera.CameraName);
                    _logger.Error($"Camera {Camera.CameraName} was removed from list. In present free is {Parameters.MaxCamersByOneTime} slots");
                }
                _logger.Error($"No files found in camera {Camera.CameraName} for channel {channelId}");
                return;
            }

            foreach (var deviceFile in DeviceData.DeviceFiles)
            {
                if (FileAlreadyExists(deviceFile))
                {
                    continue;
                }
                bool result = await StartDownloadFile(deviceFile, channelId);
                if (!result)
                {
                    try
                    {
                        File.Delete(Path.Combine(deviceFile.TargetFilePath, deviceFile.TargetFileName));
                        _logger.Info($"File {Camera.CameraName}{Path.DirectorySeparatorChar}{deviceFile.TargetFileName} was deleted");
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, $"Error deleting {Camera.CameraName}{Path.DirectorySeparatorChar}{deviceFile.TargetFileName}");
                    }
                }
                if (IsGlobalTimeout())
                {
                    _logger.Info("Application going to break by global timeout");
                    break;
                }
            }
        }

        private bool IsGlobalTimeout()
        {
            return Parameters.MaxProcessTime < DateTime.Now;
        }

        private async Task<bool> StartDownloadFile(DeviceFileInfo deviceFile, int channelId)
        {
            NETDEV_PLAYBACKCOND_S stPlayBackInfo = new NETDEV_PLAYBACKCOND_S();

            stPlayBackInfo.tBeginTime = deviceFile.StartTime;
            stPlayBackInfo.tEndTime = deviceFile.EndTime;

            stPlayBackInfo.hPlayWnd = IntPtr.Zero;
            stPlayBackInfo.dwStreamMode = (int)NETDEV_STREAM_MODE_E.NETDEV_STREAM_MODE_ALL;
            stPlayBackInfo.dwStreamIndex = (int)NETDEV_LIVE_STREAM_INDEX_E.NETDEV_LIVE_STREAM_INDEX_MAIN;
            stPlayBackInfo.dwDownloadSpeed = (int)NETDEV_E_DOWNLOAD_SPEED_E.NETDEV_DOWNLOAD_SPEED_FORTY;
            stPlayBackInfo.dwTransType = 1;
            stPlayBackInfo.dwChannelID = channelId;

            IntPtr lpDevHandle = Camera.DeviceHandle;
            if (IntPtr.Zero == lpDevHandle)
            {
                _logger.Error("Download cancelled - invalid camera device handle");
                return false;
            }

            var localFilePathName = NETDEVSDK.GetByteArray(Path.Combine(deviceFile.TargetFilePath, deviceFile.TargetFileName), NETDEVSDK.NETDEV_LEN_260);

            IntPtr pHandle = NETDEVSDK.NETDEV_GetFileByTime(lpDevHandle, ref stPlayBackInfo, localFilePathName, (int)NETDEV_MEDIA_FILE_FORMAT_E.NETDEV_MEDIA_FILE_MP4);

            if (IntPtr.Zero == pHandle)
            {
                _logger.Error($"Error getting handle for download {Camera.CameraName} {deviceFile.TargetFileName} - error {NETDEVSDK.NETDEV_GetLastError()}");
                return false;
            }
            deviceFile.FileDownloadHandle = pHandle;
            _logger.Debug($"Starting download {deviceFile.TargetFileName} from {Camera.CameraName} {DateTimeOffset.FromUnixTimeSeconds(deviceFile.StartTime):yyyy-MM-dd hh:mm}");

            var result = await DownloadFile(deviceFile);


            return result;
        }

        private async Task<bool> DownloadFile(DeviceFileInfo deviceFile)
        {
            long startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long downloadTimeoutSec = Parameters.FileTimeout;
            int downloadPercent = 0;
            int playbackErrorCount = 0;
            DateTime noFileProgressStartTime = DateTime.Now;

            long noProgressDownloadedSeconds = 0;

            do
            {
                if (!deviceFile.IsDownloaded)
                {
                    long iPlayTime = 0;
                    int iRet = NETDEVSDK.NETDEV_PlayBackControl(deviceFile.FileDownloadHandle, (int)NETDEV_VOD_PLAY_CTRL_E.NETDEV_PLAY_CTRL_GETPLAYTIME, ref iPlayTime);
                    if (NETDEVSDK.TRUE == iRet)
                    {
                        playbackErrorCount = 0;
                        deviceFile.CurDownloadTime = iPlayTime;
                        deviceFile.DownloadedLengthInSeconds = deviceFile.LengthInSeconds - (deviceFile.EndTime - iPlayTime);

                        if (noProgressDownloadedSeconds < deviceFile.DownloadedLengthInSeconds)
                        {
                            noProgressDownloadedSeconds = deviceFile.DownloadedLengthInSeconds;
                            noFileProgressStartTime = DateTime.Now;
                        }

                        if (iPlayTime + 1 >= deviceFile.EndTime) // File downloaded
                        {
                            int stopResult = NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
                            if (stopResult == NETDEVSDK.TRUE)
                            {
                                deviceFile.IsDownloaded = true;
                                _logger.Info($"File {Camera.CameraName}-{deviceFile.TargetFileName} was downloaded in {deviceFile.DownloadSeconds} seconds");
                                return true;
                            }
                        }
                    }
                    else
                    {
                        playbackErrorCount++;
                        _ = NETDEVSDK.NETDEV_GetLastError();
                        if (playbackErrorCount > 5)
                        {
                            break;
                        }
                    }
                    if ((Parameters.FileTimeout < deviceFile.DownloadSeconds) || (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startTime > downloadTimeoutSec)) // TIMEOUT
                    {
                        NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
                        deviceFile.IsDownloaded = false;
                        _logger.Error($"File {Camera.CameraName}-{deviceFile.TargetFileName} wasn't downloaded in {deviceFile.DownloadSeconds} - timeout");
                        return false;
                    }

                    if (noFileProgressStartTime.AddSeconds(Parameters.NoProgressFileTimeout) < DateTime.Now) // TIMEOUT
                    {
                        NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
                        deviceFile.IsDownloaded = false;
                        _logger.Error($"File {Camera.CameraName} {deviceFile.TargetFileName} download stuck in {noFileProgressStartTime:HH:mm:ss} for {(DateTime.Now - noFileProgressStartTime).TotalSeconds} seconds - cancelling");
                        return false;
                    }

                    if (IsGlobalTimeout())
                    {
                        NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
                        deviceFile.IsDownloaded = false;
                        _logger.Error($"File {Camera.CameraName} {deviceFile.TargetFileName} cancelled due to global timeout");
                        return false;
                    }

                    deviceFile.DownloadSeconds++;
                    double newDownloadPercent = 100 * (double)deviceFile.DownloadedLengthInSeconds / deviceFile.LengthInSeconds;
                    if (newDownloadPercent > downloadPercent + 1)
                    {
                        downloadPercent = int.CreateTruncating(newDownloadPercent);
                        _logger.Debug($"{Camera.CameraName} {deviceFile.TargetFileName} - {downloadPercent:D3}%");
                    }
                }
                else
                {
                    NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
                    return true;
                }
                await Task.Delay(1000);
            } while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startTime <= downloadTimeoutSec);
            NETDEVSDK.NETDEV_StopGetFile(deviceFile.FileDownloadHandle);
            return false;
        }

    }
}