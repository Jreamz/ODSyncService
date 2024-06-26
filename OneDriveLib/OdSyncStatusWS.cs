﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.ServiceModel;
using System.Text;
using Native;
using System.DirectoryServices.AccountManagement;
//using FileSyncLibrary;

namespace OdSyncService
{
    // NOTE: You can use the "Rename" command on the "Refactor" menu to change the class name "OdSyncStatusWS" in both code and config file together.
    public class OdSyncStatusWS : IOdSyncStatusWS
    {
        private static string userSID = null;

        internal static bool OnDemandOnly { get; set; } = false;

        private static string UserSID
        {
            get
            {
                if (userSID == null)
                {
                    var userIdentity = WindowsIdentity.GetCurrent();
                    userSID = userIdentity.User.ToString();
                }
                return userSID;
            }
        }

        public ServiceStatus GetStatus(string Path)
        {

            if (!OnDemandOnly)
            {
                if (Native.API.IsTrue<IIconError>(Path))
                    return ServiceStatus.Error;
                if (Native.API.IsTrue<IIconUpToDate>(Path))
                    return ServiceStatus.UpToDate;
                if (Native.API.IsTrue<IIconReadOnly>(Path))
                    return ServiceStatus.ReadOnly;
                if (Native.API.IsTrue<IIconShared>(Path))
                    return ServiceStatus.Shared;
                if (Native.API.IsTrue<IIconSharedSync>(Path))
                    return ServiceStatus.SharedSync;
                if (Native.API.IsTrue<IIconSync>(Path))
                    return ServiceStatus.Syncing;
                if (Native.API.IsTrue<IIconGrooveUpToDate>(Path))
                    return ServiceStatus.UpToDate;
                if (Native.API.IsTrue<IIconGrooveSync>(Path))
                    return ServiceStatus.Syncing;
                if (Native.API.IsTrue<IIconGrooveError>(Path))
                    return ServiceStatus.Error;
            }

            return ServiceStatus.OnDemandOrUnknown;
        }


        public IEnumerable<StatusDetail> GetStatusInternal()
        {
            //const string hklm = "HKEY_LOCAL_MACHINE";
            const string subkeyString = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"; // SkyDrive\UserSyncRoots\";

            using (var key = Registry.LocalMachine.OpenSubKey(subkeyString))
            {
                if (key == null)
                {
                    yield return new StatusDetail() { Status = ServiceStatus.OnDemandOrUnknown };
                }
                else
                {
                    if (key.SubKeyCount == 0)
                    {
                        yield return new StatusDetail() { Status = ServiceStatus.OnDemandOrUnknown, ServiceType="OneDrive" };
                    }
                    foreach (var subkey in key.GetSubKeyNames())
                    {
                        var displayKey = key.OpenSubKey(subkey);
                        var displayName = displayKey?.GetValue("DisplayNameResource") as string;
                        using (var userKey = key.OpenSubKey(String.Format("{0}{1}", subkey, @"\UserSyncRoots")))
                        {
                            if (userKey != null && userKey.Name.Contains(UserSID))
                            {
                                
                                
                                foreach (var valueName in userKey.GetValueNames())
                                {
                                    var detail = new StatusDetail();
                                    try
                                    {
                                        var id = new SecurityIdentifier(valueName);
                                        string userName = id.Translate(typeof(NTAccount)).Value;
                                        detail.UserName = userName;
                                        detail.UserSID = valueName;
                                        detail.DisplayName = displayName;
                                        detail.SyncRootId = subkey;
                                        
                                        string[] parts = userKey.Name.Split('!');

                                        if (parts.Length > 1)
                                        {
                                            detail.ServiceType = parts[Math.Min(2, parts.Length - 1)].Split('|')[0];
                                        } else
                                        {
                                            detail.ServiceType = "INVALID";
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        detail.UserName = String.Format("{0}: {1}", ex.GetType().ToString(),
                                            ex.Message);
                                        OneDriveLib.WriteLog.WriteErrorEvent("OneDrive " + detail.UserName);
                                    }
                                    detail.LocalPath = userKey.GetValue(valueName) as string;
                                    detail.StatusString = GetStatus(detail.LocalPath).ToString();
                                    yield return detail;
                                }
                            }
                        }
                    }
                }
            }



        }

        public IEnumerable<StatusDetail> GetStatusInternalGroove()
        {
            //const string hklm = "HKEY_LOCAL_MACHINE";
            const string subkeyString = @"Software\Microsoft\Office"; // SkyDrive\UserSyncRoots\";

            using (var key = Registry.CurrentUser.OpenSubKey(subkeyString))
            {
                if (key == null)
                {
                    yield return new StatusDetail() { Status = ServiceStatus.OnDemandOrUnknown, ServiceType="Groove" };
                }
                else
                {
                    if (key.SubKeyCount == 0)
                    {
                        yield return new StatusDetail() { Status = ServiceStatus.OnDemandOrUnknown };
                    }
                    foreach (var subkey in key.GetSubKeyNames())
                    {
                        using (var userKey = key.OpenSubKey(String.Format("{0}{1}", subkey, @"\Common\Internet")))
                        {
                            if (userKey != null && userKey.GetValue("LocalSyncClientDiskLocation") as String[] != null)
                            {
                                string[] folders = userKey.GetValue("LocalSyncClientDiskLocation") as String[];
                                foreach (var folder in folders)
                                {
                                    var detail = new StatusDetail();
                                    try
                                    {

                                        detail.UserName = WindowsIdentity.GetCurrent().Name;
                                        detail.UserSID = UserPrincipal.Current.Sid.ToString();


                                        string[] parts = subkey.Split('!');

                                        detail.ServiceType = String.Format("Groove{0}", parts[parts.Length - 1]);

                                    }
                                    catch (Exception ex)
                                    {
                                        detail.UserName = String.Format("Groove - {0}: {1}", ex.GetType().ToString(),
                                            ex.Message);
                                        OneDriveLib.WriteLog.WriteErrorEvent(detail.UserName);
                                    }
                                    detail.LocalPath = folder;
                                    detail.StatusString = GetStatus(detail.LocalPath).ToString();
                                    yield return detail;
                                }
                            }
                        }

                    }
                }
            }



        }
        public StatusDetailCollection GetStatus()
        {

            OneDriveLib.WriteLog.WriteToFile = true;
            OneDriveLib.WriteLog.WriteInformationEvent(String.Format("Is Interactive: {0}, Is UAC Enabled: {1}, Is Elevated: {2}", Environment.UserInteractive, OneDriveLib.UacHelper.IsUacEnabled,
                OneDriveLib.UacHelper.IsProcessElevated));

            StatusDetailCollection statuses = new StatusDetailCollection();

            foreach (var status in GetStatusInternal())
            {
                OneDriveState state = new OneDriveState();
                var hr = API.GetStateBySyncRootId(status.SyncRootId, out state);
                if(hr == 0)
                {
                    status.QuotaUsedBytes = state.UsedQuota;
                    status.QuotaTotalBytes = state.TotalQuota;
                    status.NewApiStatus = state.CurrentState;
                    status.StatusString = state.CurrentState == 0 ? "Synced" : state.Label;
                    status.QuotaLabel = state.QuotaLabel;
                    status.QuotaColor = new QuotaColor(state.IconColorA, state.IconColorR, state.IconColorG, state.IconColorB);
                    status.IconPath = state.IconUri;
                    status.IsNewApi = true;
                    statuses.Add(status);
                }
                if (hr != 0 && status.Status != ServiceStatus.OnDemandOrUnknown)
                {
                    if (status.Status == ServiceStatus.Error)
                    {
                        status.StatusString = API.GetStatusByDisplayName(status.DisplayName);
                    }
                    statuses.Add(status);
                }
            }
            foreach (var status in GetStatusInternalGroove())
            {
                if (status.Status != ServiceStatus.OnDemandOrUnknown)
                {
                    if (status.Status == ServiceStatus.Error)
                    {
                        status.StatusString = API.GetStatusByDisplayName(status.DisplayName);
                    }
                    statuses.Add(status);
                }
            }
            return statuses;
        }



    }
}
